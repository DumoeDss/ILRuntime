## Why

On the Neo register VM, `float += const` (and `-=`, `*=`) computes garbage
whenever the float operand is loaded through an indirect or element load. The
canonical reproducer surfaced by child 15 is `a.X += 100` where `a.X` is a
`float` field of a CLR struct (`TestVector3`) reached via `ldflda; ldind.r4`:
Roslyn lowers it to `ldind.r4; ldc.r4 100; add; stind.r4`, the optimizer's
constant-fold turns `add(floatReg, ldc.r4 100)` into an integer immediate
`addi r,r,0x42C80000` (the IEEE bit-pattern of `100.0f`), and Neo's `addi`
runtime arm is hardcoded integer (its frame is untyped), so the float bits are
integer-added. This is a CORRECTNESS defect, pre-existing and confirmed at HEAD
(child-15 reviewer stashed both source files at HEAD; HEAD JIT emits the
identical `addi r,r,0x42C80000`). It must be fixed because every Neo
`float`/`double` field or element updated by a constant is silently wrong.

## What Changes

- Seed the Neo type-specialization dataflow (`registerTypes[]`) for the
  indirect- and element-load producers that currently leave a float/double/long
  operand untyped: `Ldind_R4`/`Ldind_R8`/`Ldind_I8` (and `Ldind_I4` for
  completeness) and `Ldelem_R4`/`Ldelem_R8`/`Ldelem_I8` (and `Ldelem_I4`),
  inside `TypeSpecializeNeoOpcodes` (`JITCompiler.cs`). Today only `Ldc_*` and
  `Ldfld_*` seed these types, so a float loaded any other way is mis-typed as
  the default `I4`.
- As a direct consequence, the typed-immediate specialization
  (`Addi -> Addi_R4`, `Subi -> Subi_R4`, `Muli -> Muli_R4`, `Divi`/`Remi`
  likewise) and the typed binary specialization (`Add -> Add_R4`, `Sub -> Sub_R4`,
  ...) fire correctly for these operands, so the runtime executes the proper
  `*_R4`/`*_R8` arm reading `OperandFloat`/`OperandDouble` instead of the
  integer arm reading `Operand`. No new opcode, no runtime change, no optimizer
  fold change.
- Add `NeoStep` regression probes (`float += const`, `float -= const`, a
  compound `x = x*2 + 1`) that FAULT on HEAD (DivideByZero on the wrong value)
  and pass after the fix. The probes assert through the existing host helper
  `TestCLRBinding.SumTestVector3Fields` (CLR-side float arithmetic) to
  sidestep the separate, out-of-scope `conv.i4`-float-bit-reinterpret bug.
- Entirely Neo-gated (`TypeSpecializeNeoOpcodes` is `#if ENABLE_NEO_MODE`),
  so Legacy `ExecuteR` is byte-identical.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-optimizer`: Add a requirement that the Neo typed-arithmetic
  specialization (immediate and binary) MUST see a correctly-typed
  `registerTypes` entry for indirect-load (`Ldind_*`) and element-load
  (`Ldelem_*`) producers of float/double/long (and int) values, so a
  `float`/`double`/`long` operand loaded through a byref/CLR-struct-field/array
  element is specialized to the `*_R4`/`*_R8`/`*_I8` arithmetic variant and is
  NOT left as a plain integer immediate whose raw bits get integer-added. This
  is the root-cause fix; it also covers plain (non-immediate) `Add`/`Sub`/`Mul`
  on the same operand path.

## Impact

- **Code**: `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` --
  `TypeSpecializeNeoOpcodes` switch: add `Ldind_*`/`Ldelem_*` cases seeding
  `registerTypes[op.Register1]` with `FloatType`/`DoubleType`/`LongType`/
  `IntType` (Neo-only, ~10-15 lines). No change to `Optimizer.ELDC.cs`,
  `Optimizer.Utils.cs`, the runtime `ExecuteNeo` arms, or the object model.
- **Tests**: new `TestCases/NeoStepAddiOnFloatTest.cs` (3 probes).
- **Legacy**: neutral by construction (all changes under `ENABLE_NEO_MODE`).
  Legacy's `Addi` runtime arm already re-dispatches on the per-slot
  `StackObject.ObjectType` tag, so Legacy was never affected.
- **Regression surface**: low. Seeding a float/double/long type for these
  producers has no effect on the other type-spec decisions (branch-ref,
  Ceq_Ref, field-access-inline, Move), which all key on `IsNeoReferenceSlot` /
  `IsValueType` -- a primitive float/double/long is neither.
