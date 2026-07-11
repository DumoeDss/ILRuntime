# Proposal - neo-step23-24-roundtrip-regression

> RED-baseline regression GATE for the ILRuntime Neo overhaul. Investigate +
> fix two REAL, non-flaky self-check regressions that the lead-3 handoff over-
> claimed as 15/15 and 5/5. Fix before any new feature work. Branch:
> `features/object-model-overhaul`. Reproduced on HEAD `311a6915`, fresh rebuild.

## What broke (the RED baseline, reproduced on HEAD)

1. **NeoStep23Roundtrip: 13/15** (was claimed 15/15). Two cells throw
   `InvalidCastException: Unable to cast object of type
   'ILRuntime.CLR.TypeSystem.ILGenericParameterType' to type
   'ILRuntime.CLR.TypeSystem.CLRType'`:
   - `GenericProbe` (the generic-constrained-on-VT method cell in the
     methodNames loop) -- a generic method whose return type is the generic
     parameter `T`.
   - `FullModel` (the whole-pipeline Write/Read cell) -- it calls
     `BuildMethodDef` for every method including `GenericProbe`.
2. **NeoStep24CliRoundtrip: 4/5** (was claimed 5/5). The `Cell1 driver` cell
   reports `1 skip(s): TestCases.NeoStep24CliProbe..cctor()
   (IndexOutOfRangeException)` -- the Neo compiler driver force-compiles the
   empty static `.cctor` and it throws `IndexOutOfRangeException` in the
   optimizer lowering pass, so the driver records a skip and `IsComplete`
   becomes false.

Both are stable, non-flaky, reproduce on a clean rebuild of HEAD, and were
over-claimed by the prior handoff. They are the gate.

## Root cause (established by dump-gate, NOT guess -- see design.md)

### Failure #1 (InvalidCastException) -- introduced by the Step 25 S3-2 commit
`f32f816e` ("Neo S3-2+S3-3 TRUE-COMPLETION"). That commit added the
`ReturnTypeRefIdx` line to `NeoAssemblyWriter.BuildMethodDef`:

```csharp
rec.ReturnTypeRefIdx = retType != null
    ? b.IndexTypeRef(retType is ILType irt ? irt.TypeReference
        : module.ImportReference(((CLRType)retType).TypeForCLR), retType)
    : -1;
```

The false arm assumes every non-`ILType` return is a `CLRType`. But an OPEN
generic method's return (`GenericProbe<T>` returns `T`) is an
`ILGenericParameterType` -- a third `IType` that is NEITHER `ILType` NOR
`CLRType`, but which DOES carry a Cecil `TypeReference`. The cast
`(CLRType)retType` throws. The template cell for the SAME method passes
because it uses the separate `BuildTemplate`/`ForceBuildTemplate` path (which
does not touch the return type this way), which is why only the method +
full-model cells failed. `CompileFresh` itself succeeds (proven by dump).

### Failure #2 (IndexOutOfRangeException) -- a latent JIT/optimizer bug
exposed when the Step 25 S3-4 commit `bc9f1020` made the CLI driver
(`NeoCompiler.CompileCore`) force-compile the `.cctor` body (it appends
`GetStaticConstroctor()` to the method list). The empty `.cctor` is just a
`ret`; for a void method the JIT `Code.Ret` case leaves `Register1` at its
default `0` (it only sets `Register1 = --baseRegIdx` when `hasReturn`), so a
phantom register 0 with no underlying slot reaches the optimizer. The empty
`.cctor` has `LocalInfos.Length == 0` (no params, no locals, no stack temps),
and `LowerNeoOffsets`'s `case Ret: if (op.Register1 >= 0) LowerR1(...)` fires
(`0 >= 0`), then `LowerR1` does `localInfos[0].Offset` -> OOB.

Dump proof (the binding evidence): `code=Ret r1=0 localInfos.Length=0`, stack
`Optimizer.LowerR1 -> LowerNeoOffsets -> RunNeoBackHalf -> Compile ->
ILMethod.InitCodeBody -> BodyRegister`.

A follow-on effect: once #2 is fixed the `.cctor` is EMITTED, which exposed a
STALE test-replication loop in `NeoStep24CliRoundtripCheck` (Cell3/Cell5) that
did not include the `.cctor` the driver now compiles -- a test bug, also
fixed here.

## Scope

Neo-only (`#if ENABLE_NEO_MODE`), Legacy-neutral (all three touched files are
Neo-gated; plain `Debug` compiles them out). Minimal: 41 insertions / 2
deletions across 3 files. No new features, no engine redesign.

## Out of scope

- The JIT `Code.Ret` root cause (leaving `Register1 = 0` for void methods
  instead of `-1`) is left as-is; the optimizer-level defensive fix is
  sufficient and avoids touching the shared JIT. Noted as a forward-looking
  observation only.
- Any change to the `.cctor` seeding / functional deserialize->ExecuteNeo
  (those are Step-25 matters).
