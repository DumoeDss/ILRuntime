# Planning Context — neo-f4-reflection-on-neo (F-4 TRUE COMPLETION)

> SEED for the planner. TRUE-COMPLETION: reading IL-declared fields/methods off
> a Neo `ILTypeInstance` (e.g. a caught IL exception) works via the standard
> reflection / cross-binding bridges. Scope-AWARE: dump-gate each of the 4 broken
> paths; ship the tractable ones, sequence the rest.

## What this change is

F-4 / NEO-IL-EX-FIELDACCESS: once an IL exception can be THROWN + CAUGHT
(`neo-il-exception-throw` shipped that), reading IL-declared fields/methods off
the CAUGHT object via the standard bridges is broken on Neo. Four distinct
broken read paths (each a separate Neo mechanism), documented in
`.trae/documents/neo-deferred-items.md` F-4 §3 + surfaced by
`neo-il-exception-throw`:

1. **`((CrossBindingAdaptorType)e).ILInstance` bridge** -- `callvirt` on a CLR
   interface (`CrossBindingAdaptorType::get_ILInstance`) against the `Adapter`
   receiver -> ExecuteNeo throws `InvalidCastException` ("Object does not match
   target type").
2. **`e.GetType()`** -- NIE (`callvirt.clr` on `Object.GetType`).
3. **`appdomain.Invoke(instanceMethod, e)`** -- NRE: the public `Run`/`Invoke`
   re-entry path ignores the `instance` argument under `ENABLE_NEO_MODE` (the
   Step-6 entry shim handles only no-arg static methods). (Related to F-12 -- the
   parametrized-Run prerequisite.)
4. **`ILTypeInstance.this[index]` indexer** -- returns `null` under
   `ENABLE_NEO_MODE` (Legacy-only `StackObject[] fields`; Neo uses
   `byte[] Primitives + AutoList ManagedObjects`).

## Authoritative prior context

1. `.trae/documents/neo-deferred-items.md` -- F-4 §3 (the 4 broken paths, the
   workaround `e is MyEx` / isinst, the suspect sites).
2. `openspec/changes/archive/2026-07-08-neo-async-movenext-fix/` -- F-12 (Run
   ref-return / parametrized-Run) is RELATED to path #3; the async fix may have
   advanced the Run-entry machinery.
3. `openspec/changes/neo-completion-portfolio/handoff/lead-2.md` -- portfolio context.
4. The code: `ILTypeInstance.cs` (the Neo indexer + accessors -- `#if
   !ENABLE_NEO_MODE` at `:27,86,94,379,...`) + `ILIntepreter.cs:87-120` (the Run
   entry shim) + the callvirt-on-CLR-interface dispatch in ExecuteNeo + the
   `appdomain.Invoke` instance re-entry.

## Scope-AWARE dump-gate (binding -- the dump decides which paths ship vs sequence)

For EACH of the 4 paths, probe on HEAD `b0041e74`:
- Is it a SMALL focused fix (ship) or a LARGE machinery change (sequence)?
- Cite file:line.

Path #4 (the `ILTypeInstance` Neo indexer) is likely the most tractable (a
defined accessor gap -- add the Neo indexer reading `byte[] Primitives + AutoList`).
Path #3 (`appdomain.Invoke` instance re-entry) overlaps F-12 (the parametrized-Run
prerequisite the async fix may have advanced) -- check if the async work unblocked
it. Paths #1 (callvirt-on-CLR-interface) + #2 (callvirt.clr GetType) are Neo
dispatch gaps.

**Ship the tractable paths; sequence the large ones into follow-on children (TRUE-
COMPLETION: each follow-on MUST be driven next, not parked).** A green smoke does
NOT prove the gate -- construct an adversarial probe per shipped path (a caught IL
exception whose field/method is read via the bridge -> the correct value, not
null/NRE/InvalidCastException).

## Build + test (CRITICAL -- always `-f net8.0`)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep14   # the exception step (has the ILEx probes)
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep     # 219/0/0 regression
```
ALWAYS `-f net8.0`. `Debug_Neo` prints huge JIT output -- normal.

## Spec authoring traps
- `specs/neo-exceptions/spec.md` (or neo-optimizer) delta PURE ASCII; SHALL-first bodies. Capability: pick the best-fit (the reflection-read-off-exception is `neo-exceptions`; the ILTypeInstance indexer is the object model).
- Legacy is the REFERENCE. Neo-only `#if ENABLE_NEO_MODE`. Legacy-neutral.

## Deliverables
`proposal.md`, `design.md` (the per-path dump-gate verdict: ship vs sequence, each
file:line-cited + the adversarial probe per shipped path), `specs/<cap>/spec.md`
(delta), `tasks.md`.
