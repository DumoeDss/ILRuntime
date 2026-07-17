# Tasks -- neo-activator-createinstance-neo-redirect

## Phase 1 -- artifacts (propose) [DONE]
- [x] `proposal.md` -- what + why (Activator.CreateInstance on IL types; Neo
      falls through to broken autogen stub; mirror child-6).
- [x] `design.md` -- the 3 Neo redirects + `WriteNeoObjectResult` helper +
      registration + precedence argument.
- [x] `specs/neo-dispatch/spec.md` -- ADDED requirement (Scenario/SHALL).
- [x] `tasks.md` -- this checklist.

## Phase 2 -- implement + verify (apply)

### Implement [DONE]
- [x] Add `WriteNeoObjectResult` helper to `CLRRedirections.cs` under
      `#if ENABLE_NEO_MODE` (next to `WriteNeoDelegateResult`): null-aware ref
      writer (`null -> *(int*)retDst = -1`).
- [x] Add `CreateInstanceNeo` (generic) -- read `method.GenericArguments[0]`;
      IL -> `Instantiate()`, CLR -> `CreateDefaultInstance()`; write result.
- [x] Add `CreateInstance2Neo` (Type) -- `ReadNeoReference` the Type param;
      ILRuntimeType -> `Instantiate()`, else host `Activator.CreateInstance(t)`;
      null -> sentinel.
- [x] Add `CreateInstance3Neo` (Type, object[]) -- `ReadNeoReference` Type +
      object[]; null-check each element (`ArgumentNullException`); ILRuntimeType
      -> `Instantiate(object[])`, else host `Activator.CreateInstance(t, t2)`;
      write result.
- [x] Register all three on `RedirectMapNeo` in the `AppDomain` ctor (folded
      into the existing Activator loop with `#if ENABLE_NEO_MODE` sibling
      calls, mirroring the `Delegate.Combine` block).
- [x] Add `TestCases/NeoStepActivatorCreateInstanceTest.cs` probe: generic +
      Type + (Type, object[]) overloads on an IL type; assert default values +
      ctor-arg round-trip; FAULTS (MissingMethodException) on HEAD.

### Verify (run + record) [DONE]
- [x] `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental` (0 errors).
- [x] `dotnet build TestCases/TestCases.csproj -c Debug` (0 errors).
- [x] NeoStep smoke green: **361/0** (358 baseline + 3 probes), no regressions.
- [x] Stash-toggle FAIL->PASS: stash `CLRRedirections.cs` + `AppDomain.cs`
      (keep probe) -> rebuild -> 3 probes FAULT with the EXACT triage error
      `MissingMethodException: No parameterless constructor defined for type
      'ILRuntime.Runtime.Intepreter.ILTypeInstance'`; `git stash pop` ->
      rebuild -> 361/0 PASS. Airtight.
- [x] The 2 triage-flagged Activator tests under Neo (name filter):
      `ActivatorCreateInstanceWithArgsTestSimple` PASSES.
      `ActivatorCreateInstanceWithArgsTest` FAILS -- but NOT on Activator: the
      `Activator.CreateInstance` calls now all succeed (the MissingMethodException
      is gone; the instance is created with correct field values). The remaining
      failure is inside `ToString()` at `ILValue == null` (a `ceq` on a reference
      operand) -> `Neo callvirt this is null`. PROVEN SEPARATE: a diagnostic probe
      with PLAIN `new DiagData()` (NOT Activator) + the same `field == null`
      ternary fails with the IDENTICAL error. This is the
      `neo-ceq-null-sentinel` gap (separately PROPOSED child: a reference stored
      as a valid mStack index pointing to null compares index-vs-(-1) under ceq
      -> `ref == null` reads FALSE -> `ref.ToString()` on null). Out of scope for
      this child; the Activator redirect itself is complete and correct.
- [x] Legacy-neutral: plain `Debug` + `useRegister=true` + NeoStep = 361 ran /
      18 failed; all 18 are pre-existing Neo-specific tests (none are the
      Activator probes -- the 3 probes PASS under Legacy). The change is 100%
      `#if ENABLE_NEO_MODE` -> Legacy byte-identical to HEAD.
