# Planning Context — neo-f4-parametrized-run-entry (F-4 #3 + F-12 TRUE COMPLETION)

> SEED for the planner. TRUE-COMPLETION: `ILIntepreter.Run(method, instance, p)`
> marshals `instance` + `p` into the Neo frame + handles reference returns. This
> closes F-4 path #3 (reading IL instance METHODS off a caught exception via
> `appdomain.Invoke`) AND F-12 (Run ref-return).

## What this change is

The public `ILIntepreter.Run(ILMethod method, object instance, object[] p)`
re-entry path (`ILIntepreter.cs:87-137`) under `ENABLE_NEO_MODE` is a Step-6
PARAMETERLESS-ONLY shim: it marshals NEITHER `instance` NOR `p` into the Neo
frame (handles only no-arg static methods). So:
- **F-4 path #3:** `appdomain.Invoke(instanceMethod, caughtException)` -> the IL
  instance-method override has no `this` -> NRE. (Reading an IL METHOD off a
  caught exception is the last blocked reflection path; F-4 paths #1/#2/#4
  shipped.)
- **F-12:** `NeoBoxReturnValue` handles primitive returns only; a reference-type
  return reads raw bytes.

## The prior finding (do NOT re-derive)

`neo-async-movenext-fix` routed AROUND `Run` (via `DriveMoveNextCore` + a fresh
interpreter calling `ExecuteNeo` directly). So `Run` itself is STILL the wide-open
parametrized gap. The parametrized-Run ABI extension is the fix:
1. Marshal `object[] p` into the callee param region via `WriteNeoCallSlot` per
   parameter (the Step-13b/17 param-marshal machinery).
2. Push `instance` as the slot-0 `this` (the callee frame's slot 0).
3. Extend `NeoBoxReturnValue` to the reference-return shape (box the Neo
   reference into the return slot).

## Authoritative prior context

1. `openspec/changes/archive/2026-07-08-neo-f4-reflection-on-neo/{design.md §8, ship-log.md}`
   -- F-4 path #3 deferral + the parametrized-Run spec (this is the follow-on).
2. `openspec/changes/archive/2026-07-08-neo-async-movenext-fix/` -- the
   DriveMoveNextCore pattern (Run routed around it); the `NeoBoxReturnValue`
   site (F-12).
3. `ILRuntime/Runtime/Intepreter/ILIntepreter.cs:87-137` (the Run entry shim) +
  the Neo frame-setup pattern (the S1 `NeoStep25LoadExec` / the Step-19
   `NeoInvokeSub` CLR->IL callback, which DOES marshal CLR args into a Neo frame
   -- the pattern to mirror).
4. `.trae/documents/neo-deferred-items.md` -- F-4 §3 (path #3) + F-12 /
  NEO-RUN-REF-RETURN rows.

## Dump-gate (binding)

On HEAD `cb07444d`:
1. Confirm `Run`'s Neo arm (`ILIntepreter.cs:94-137`) ignores `instance` + `p`
   (cite the lines). How does the Step-19 `DelegateAdapter.NeoInvokeSub` (CLR->IL
   callback) marshal CLR args into a Neo frame today? It's the pattern to mirror
   for `Run`'s `p`.
2. Is the `instance` (slot-0 `this`) the only missing piece for instance-method
   re-entry, or is there a deeper frame-setup gap?
3. `NeoBoxReturnValue`: confirm it handles only primitives (F-12); what's the
   reference-return fix (box the Neo reference)?
4. **Scope-aware:** is the parametrized-Run extension SMALL (mirror NeoInvokeSub's
   arg-marshal + push instance + extend NeoBoxReturnValue) or LARGE (deeper
   frame/ABI work)? Ship if tractable; sequence the remainder.

## Scope (TRUE COMPLETION)

Ship the parametrized-Run extension so `appdomain.Invoke(instanceMethod,
caughtException)` works (the F-4 #3 success criterion: read an IL method off a
caught exception). Construct an adversarial probe: a caught IL exception whose
instance METHOD is invoked via `appdomain.Invoke` -> the correct result (the
override runs with the right `this`), not NRE. Plus a ref-return probe (F-12).

## Build + test (CRITICAL -- always `-f net8.0`)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep14   # the exception step (add the method-read probe)
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep     # 221/0/0 regression
```
ALWAYS `-f net8.0`. `Debug_Neo` prints huge JIT output -- normal. A test taking
>10s usually = an infinite loop -- kill + investigate.

## Spec authoring traps
- `specs/neo-exceptions/spec.md` (or neo-dispatch) delta PURE ASCII; SHALL-first. Legacy is the REFERENCE. Neo-only `#if ENABLE_NEO_MODE`. The Run entry is SHARED -- the parametrized extension MUST be Neo-gated (the Legacy Run arm already marshals p+instance; the Neo arm is the gap). Legacy-neutral.

## Deliverables
`proposal.md`, `design.md` (the dump-gate verdict + the parametrized-Run ABI design +
whether it closes F-4 #3 AND F-12 in one), `specs/<cap>/spec.md` (delta), `tasks.md`.
