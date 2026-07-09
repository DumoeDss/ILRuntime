# Ship Log - neo-step23-24-roundtrip-regression

> RED-baseline regression GATE. Shipped 2026-07-09. Branch
> `features/object-model-overhaul`. HEAD `311a6915`. Two REAL, non-flaky
> self-check regressions that the lead-3 handoff over-claimed as 15/15 and
> 5/5. Fixed via dump-gate, verified by stash-toggle. Neo-only, Legacy-neutral.

## Root cause (per failure, established by dump BEFORE the fix)

### Failure #1 -- NeoStep23Roundtrip 13/15 (InvalidCastException)
- **Cell**: `GenericProbe` (method cell) + `FullModel` (whole-pipeline cell).
- **Symptom**: `InvalidCastException: ILGenericParameterType -> CLRType`.
- **Dump**: a `Console.Error` loop-diag printed
  `GenericProbe retType=ILRuntime.CLR.TypeSystem.ILGenericParameterType`,
  then `[CompileFresh OK] GenericProbe` (so JIT compile is fine), then the
  `BuildMethodDef` call threw -- pinning the throw to the `(CLRType)retType`
  cast at `NeoAssemblyWriter.cs:759`.
- **Cause**: the `ReturnTypeRefIdx` line (added by Step 25 S3-2, commit
  `f32f816e`) cast `((CLRType)retType)` in its false arm. An OPEN generic
  method's return is an `ILGenericParameterType` (a third IType, not ILType,
  not CLRType). The cast threw. The template cell for the SAME method passed
  (separate `BuildTemplate` path) -- which is exactly why only the method +
  full-model cells failed.

### Failure #2 -- NeoStep24CliRoundtrip 4/5 (IndexOutOfRangeException)
- **Cell**: `Cell1 driver` (`1 skip(s): .cctor() (IndexOutOfRangeException)`).
- **Dump**: a temp diag in `NeoCompiler.CompileCore`'s skip-catch dumped the
  full stack:
  ```
  System.IndexOutOfRangeException
    at Optimizer.LowerR1(OpCodeR&, StackSlotInfo[]) at Optimizer.Neo.cs:1456
    at Optimizer.LowerNeoOffsets(CompiledFrame&, AppDomain) at :856
    at JITCompiler.RunNeoBackHalf(...) at JITCompiler.cs:678
    at JITCompiler.Compile(...) at :627
    at ILMethod.InitCodeBody(Boolean) at ILMethod.cs:840
    at ILMethod.BodyRegister at :479
    at NeoCompiler.CompileCore(...) at NeoCompiler.cs:342
  ```
  A `LowerR1` OOB-diag then confirmed the exact state: `code=Ret r1=0
  localInfos.Length=0`.
- **Cause**: a latent JIT/optimizer bug exposed when Step 25 S3-4 (commit
  `bc9f1020`) made `NeoCompiler.CompileCore` force-compile the `.cctor` body.
  The empty `.cctor` is just a `ret`. For a void method the JIT `Code.Ret`
  case leaves `Register1` at its default `0` (it only sets it when
  `hasReturn`), so a phantom register-0 (no underlying slot) reaches the
  optimizer. The `.cctor` has `LocalInfos.Length == 0` (no params/locals/
  temps); `case Ret: if (op.Register1 >= 0) LowerR1(...)` fires (`0 >= 0`)
  and `localInfos[0]` OOBs.

### Follow-on (test-only, exposed by Fix #2)
Once Fix #2 lets the `.cctor` compile, the driver EMITS it (7 methods), but
the Cell3/Cell5 replication loop in `NeoStep24CliRoundtripCheck` did not
include the `.cctor` (stale w.r.t. the S3-4 driver change) -> `Cell3:
MethodDefs 7 vs ngen 6` + `Cell5: body mismatch for NestedMethod` (positional
shift). Fixed by mirroring the driver (append `GetStaticConstroctor`).

## The fix (3 files, 41 insertions / 2 deletions, all `#if ENABLE_NEO_MODE`)

1. **`ILRuntime/Runtime/NeoAOT/NeoAssemblyWriter.cs`** (Fix #1): new
   `ResolveReturnTypeCecilRef(IType, ModuleDefinition)` helper handling
   `ILType` / `ILGenericParameterType` / `CLRType` explicitly; called from
   `BuildMethodDef`'s `ReturnTypeRefIdx` line. Eliminates the
   `(CLRType)retType` cast.
2. **`ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs`** (Fix #2):
   `LowerR1` now defensively bounds-checks `localInfos`
   (`(r1 >= 0 && r1 < localInfos.Length) ? localInfos[r1].Offset : 0`),
   mirroring the existing defensive pattern. Safe because `ExecuteNeo`'s `Ret`
   reads `DstOffset` only when there IS a return value.
3. **`ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep24CliRoundtripCheck.cs`**
   (Fix #3, test-only): the Cell3/Cell5 replication loop now appends
   `GetStaticConstroctor() as ILMethod`, mirroring the driver.

## Non-changes (deliberate)
- The shared-JIT `Code.Ret` root cause (leaving `Register1 = 0` for void
  methods) is NOT touched (it is in `JITCompiler.cs`, shared Legacy+Neo). The
  Neo-only optimizer fix is sufficient and keeps the change Legacy-neutral.
- `LowerR1R2` / `LowerR1R2R3` / the inline `localInfos[op.Register1]` sites
  at Box/Unbox/Ldstr/Ldftn are NOT broadened (those carry real local-slot
  registers; only void-`Ret` is the phantom case). Minimal scope.

## Verification evidence

### Build (dev subset only -- the sln cannot build whole)
```
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo   -> 0 errors
dotnet build TestCases/TestCases.csproj -c Debug                      -> 0 errors
```

### The two regression gates (after fix)
```
NeoStep23Roundtrip -> NeoStep23 V1 roundtrip: 15/15 cells passed, 0 failed.
NeoStep24CliRoundtrip -> NeoStep24 V1 CLI roundtrip: 5/5 cells passed, 0 failed.
```

### Regression smoke (no regression)
```
NeoStep (Debug_Neo, useRegister=true) -> Ran 238 tests, 0 failded, 0 ignored, 0 todos
```
(238/0/0 -- the documented baseline; unchanged.)

### STASH-TOGGLE PROOF (the binding evidence)
Stashed the 3 fix files (`git stash push -- <the 3 files>`) -> re-ran the two
gates on the un-fixed tree:

| Gate | Fix STASHED (RED) | Fix POPPED (GREEN) |
|---|---|---|
| NeoStep23Roundtrip | 13/15 (2 fail: GenericProbe + FullModel, InvalidCast) | 15/15 |
| NeoStep24CliRoundtrip | 4/5 (1 fail: Cell1 driver .cctor IndexOutOfRange) | 5/5 |

The RED counts reproduce the ORIGINAL pre-fix failures exactly; the GREEN
counts are the fixed state. This proves the 3-file diff is what flips the
gates (not an environment/build artifact).

### Legacy-neutrality
All 3 touched files are `#if ENABLE_NEO_MODE` (Optimizer.Neo.cs +
NeoAssemblyWriter.cs) or `#if ENABLE_NEO_MODE && DEBUG`
(NeoStep24CliRoundtripCheck.cs). A plain `Debug` (Legacy) build compiles them
out entirely, so Legacy is structurally unaffected. (The Legacy 518/519
regression reference was not re-run -- not required, the change cannot reach
Legacy.)

## Delivered scope / deferred
- **Delivered**: the two regression gates green (15/15 + 5/5); NeoStep 238/0/0
  preserved; Neo-only + Legacy-neutral.
- **Deferred (forward-looking observations, NOT regressions)**:
  - A future tightening of the shared JIT `Code.Ret` case to set
    `Register1 = -1` for void methods (would make the phantom-register state
    impossible at the source) -- not required for this gate; the optimizer
    defensive fix is sufficient.
  - Formal spec'ing of the `.cctor`-in-MethodDefTable + return-type-token
    resolution contract in `openspec/specs/neo-optimizer/spec.md` -- left for
    a future spec-bearing change (this regression fix carries no delta).

## Review verdict
Self-reviewed by the investigator+fixer (single-agent gate; author == verifier
is acceptable for a RED-regression GATE per the LEAD's flat-hierarchy
contract). Dump-gate satisfied for BOTH failures (root cause established by
stack-trace + state dump BEFORE the fix); stash-toggle satisfied (RED->GREEN
flips on the 3-file diff). Minimal, Neo-gated, Legacy-neutral.
