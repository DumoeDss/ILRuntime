# Planning Context — neo-f13-nested-run-executeneo (TRUE COMPLETION)

> SEED. F-13: nesting `appdomain.Invoke` (a 2nd `Run`/`ExecuteNeo`) INSIDE an
> in-flight `ExecuteNeo` corrupts the outer method's instruction pointer (`ip`
> runs off the end of its body -> garbage opcode). Root cause NOT yet isolated
> (suspect a process-static shared across the per-interpreter frames). Isolate +
> fix. Scope-AWARE: this is a deep engine re-entrancy issue; isolate the root
> cause first, then decide ship-vs-sequence.

## What this change is

Surfaced by `neo-f4-parametrized-run-entry` (the single-level host re-entry works;
the NESTED case -- an IL method that itself calls `appdomain.Invoke` mid-ExecuteNeo
-- corrupts the outer frame's `ip`). The implementer flagged: "suspect a process-
static shared across the per-interpreter frames -- `fixed` pin + GC-triggering
inner JIT, or a shared `Stack`/`ValueTypePointer`." The F-4 #3 IL-side probe hit
this (returned -97, swallowed); the gates moved host-side to avoid it.

## The dump-gate (binding -- ISOLATE the root cause first)

On HEAD `7a0f4cbd`:
1. Reproduce: an IL method that, mid-ExecuteNeo, calls a CLR method that re-enters
   via `appdomain.Invoke` (a 2nd Run/ExecuteNeo on a FRESH pooled interpreter).
   After the inner returns, the OUTER ExecuteNeo's `ip` is wrong (runs past its
   body). Cite where `ip` is held (a `byte*` local in ExecuteNeo? a ref?).
2. The suspect: is `ip` (or the frame pointer) a process-static / shared across
   interpreters? Or does the inner `appdomain.Invoke` (which `RequestILIntepreter`s
   a fresh interpreter) somehow disturb the outer's pinned frame (a `fixed` pin
   invalidated by a GC the inner JIT triggers)? Cite the `fixed`/pin sites in
   ExecuteNeo + the frame allocation.
3. **Scope-aware:** is the fix SMALL (a missing `fixed` re-pin after the inner
   call returns; or `ip` should be a local-by-value not a stale ref; or the
   interpreter-pool isolation is incomplete) or LARGE (a fundamental re-entrancy
   redesign)? Isolate the root cause; ship if tractable; sequence if it's a deep
   redesign.

## Authoritative prior context

1. `openspec/changes/archive/2026-07-08-neo-f4-parametrized-run-entry/{design.md, ship-log.md}`
   -- F-13 surfaced here (the nested-re-entrancy blocker for the IL-side F-4 #3
   probe; the gates moved host-side to avoid it). The implementer's root-cause
   suspect is recorded.
2. `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- ExecuteNeo's
  frame + `ip` handling (the `byte* frameBase` / `OpCodeR* ip` / the `fixed` pins)
  + the Call arm (where a CLR method re-entering via appdomain.Invoke would nest).
3. `ILRuntime/Runtime/Intepreter/ILIntepreter.cs` -- the interpreter pool
  (`RequestILIntepreter`/`FreeILIntepreter`) + `appdomain.Invoke` -> `Run`.
4. `.trae/documents/neo-deferred-items.md` -- F-13 / NEO-NESTED-RUN-EXECUTENEO row.

## Scope (TRUE COMPLETION -- isolate first)

The success criterion: an IL method that nests `appdomain.Invoke` mid-ExecuteNeo
returns correctly (the outer `ip` survives the inner re-entry). Construct an
adversarial probe (the F-4 #3 IL-side shape: a method that catches an exception
then invokes an instance method via appdomain.Invoke) -> the correct result, not
the -97 corruption. If the root cause is a missing pin/isolation (SMALL), fix it;
if it's a fundamental re-entrancy redesign (LARGE), isolate + document + sequence
honestly (TRUE-COMPLETION: the follow-on is driven next, not parked).

## Build + test (CRITICAL -- always `-f net8.0`)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep14   # add the nested-re-entry probe
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep     # 226/0/0 regression
```
ALWAYS `-f net8.0`. A test taking >10s = the ip-corruption loop -- kill + investigate (that IS the bug).

## Spec authoring traps
- `specs/neo-dispatch/spec.md` (or neo-optimizer) delta PURE ASCII; SHALL-first. Legacy is the REFERENCE. Neo-only `#if ENABLE_NEO_MODE`. Legacy-neutral.

## Deliverables
`proposal.md`, `design.md` (the ISOLATED root cause + the fix design OR the
honest sequence decision, file:line-cited), `specs/<cap>/spec.md` (delta),
`tasks.md`.
