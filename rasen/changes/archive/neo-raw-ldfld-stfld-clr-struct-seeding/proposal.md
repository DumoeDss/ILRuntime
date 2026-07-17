## Why

On the Neo register VM, a `float`/`double`/`long` value produced by EITHER a
method `Call` (whose return type is a primitive float/double/long) OR a raw
`Ldfld` of a CLR-struct primitive field, and then used in `add`/`addi`/`sub`/
`mul`/..., is computed as GARBAGE: the typed `*_R4`/`*_R8`/`*_I8` arithmetic
specialization silently no-ops and a plain INTEGER op runs on the raw IEEE
bits. This is the same root-cause class as the shipped sibling child 16
(`neo-addi-on-float`, commit `43a74a85`), which seeded the `Ldind_*`/`Ldelem_*`
producers. Child 16 left TWO producer families un-seeded and explicitly deferred
them -- they are the scope of this change:

1. **Call returning a primitive float/double/long.** `TypeSpecializeNeoOpcodes`
   (`JITCompiler.cs`) has a `Call` case that only CLEARS a stale in-frame-VT or
   reference type off a REUSED dest register; it NEVER seeds a FRESH dest with
   the call's resolved `ReturnType`. So when the call dest is a fresh temp (the
   common case: `x = GetF() + 100f`), `registerTypes[dest]` stays `null`, the
   `Add`/`Addi` specialization keyed on `registerTypes[Register2]` falls back to
   `I4`, and a plain integer `add`/`addi` integer-adds the float bits. Child 16's
   own design.md "Open Questions" flagged this exact case and deferred it
   ("Defer unless a probe or the full smoke surfaces it"). It surfaces.

2. **Raw `Ldfld` of a CLR-struct primitive field.** A field whose DECLARING type
   is a `CLRType` escapes the Neo typed-splitter (child 4), so it reaches
   `TypeSpecializeNeoOpcodes` as a raw `OpCodeREnum.Ldfld` (not `Ldfld_R4`).
   There is NO seeding case for raw `Ldfld` (only the typed `Ldfld_R4`/`R8`/`I8`
   arms are seeded). Child 16's TC3 note explicitly called this single-expression
   producer shape (`a.X = a.X * 2 + 1` lowers the READ through raw `ldfld`) out
   of scope. It surfaces.

This is a CORRECTNESS defect, pre-existing and confirmed at HEAD (fc2baa26) with
both runtime FAULT evidence and JIT-dump evidence (see Re-Audit below). It must
be fixed because every Neo expression that does arithmetic on a float/double/long
RETURNED from a method or READ from a CLR-struct field via raw `ldfld` is silently
wrong.

### Re-Audit verdict (5-for-5 / 12-for-12 discipline: CONFIRM, do not trust framing)

Both shapes REPRODUCE on HEAD with concrete evidence. A temporary probe
(`TestCases/NeoStepFloatSeedingProbe.cs`, 4 probes) was built and run under Neo
(`Debug_Neo`). All 4 FAULT with `DivideByZeroException` (the `1/0` guard firing
on a wrong value). The NeoStep smoke stays at its 354/0 baseline + the 4 faulting
probes = "Ran 358 tests, 4 failded" -- ZERO regressions, the 4 failures are
exactly the new probes.

JIT-dump evidence (the load-bearing proof -- the producer's dest is mis-typed,
so specialization no-ops):

- **Shape 1 (Call ret float)** -- `NeoStepFloatSeeding_TC1_CallRetFloatAddConst`
  Final Results:
  ```
  3:call r7, TestCases.NeoStepFloatSeedingProbe.NeoStepGetOneF()
  4:addi r7,r7,1120403456
  5:stfld r6, r7, 0x...
  ```
  `call r7, GetOneF()` leaves `r7` UNSEEDED; `addi r7,r7,1120403456` is a PLAIN
  integer `addi` (1120403456 = 0x42C80000 = IEEE bits of `100.0f`), NOT
  `addi.r4`. The integer add corrupts the float -> garbage -> fault.

- **Shape 2 (raw Ldfld CLR-struct)** --
  `NeoStepFloatSeeding_TC3_RawLdfldClrStructMulAdd` Final Results:
  ```
  3:ldfld r7, r0, 0x2000001A59D8BA74
  4:muli r7,r7,1073741824
  5:addi r7,r7,1065353216
  6:stfld r6, r7, 0x...
  ```
  `ldfld r7, r0, ...` is a RAW `ldfld` (unsplit -- the declaring type is the
  CLRType `TestVector3`); `r7` is UNSEEDED; `muli r7,r7,1073741824`
  (0x40000000 = bits of `2.0f`) and `addi r7,r7,1065353216`
  (0x3F800000 = bits of `1.0f`) are PLAIN integer ops. TC4's runtime local dump
  is especially vivid: `TestVector3 a = (1.7014118E+38, 1, 1)` -- `a.X + a.Y`
  integer-added `0x3F800000 + 0x3F800000 = 0x7F000000 = 1.7e38` instead of
  `1 + 1 = 2`.

Both shapes are DISPROVEN-framing: the bug is real, the producer dest is
unseeded, and the specialization no-ops exactly as framed.

## What Changes

- **Shape 1 fix:** in `TypeSpecializeNeoOpcodes` (`JITCompiler.cs`), the
  `Call`/`Callvirt`/`Callvirt_IL`/`Callvirt_CLR`/`Call_Redirect` case, seed
  `registerTypes[op.Register1]` with the call's resolved primitive `ReturnType`
  (Float/Double/Long/Int) when the return is a primitive. The case already
  resolves the return type (for the stale-clearing logic); this adds the missing
  seed for a fresh/cleared dest. Safe: a primitive return never conflicts with
  the existing stale-VT-keep / stale-ref-keep logic (those only keep non-primitive
  VT/ref types).
- **Shape 2 fix:** in `TypeSpecializeNeoOpcodes`, add a `case OpCodeREnum.Ldfld:`
  that resolves the declaring CLRType + field from the raw encoding
  (`OperandLong = (typeHash<<32)|fieldHash`, identical to child-4's runtime
  handler) and seeds `registerTypes[op.Register1]` with the field's primitive
  type (float->FloatType, double->DoubleType, long->LongType, other primitive
  ->IntType). Non-primitive (VT/ref) fields are left unseeded (out of scope; they
  do not feed primitive arithmetic).
- As a direct consequence, the typed-immediate specialization
  (`Addi -> Addi_R4`, `Subi -> Subi_R4`, `Muli -> Muli_R4`, `Divi`/`Remi`
  likewise) and the typed binary specialization (`Add -> Add_R4`, `Sub -> Sub_R4`,
  ...) fire correctly for these operands. No new opcode, no runtime change, no
  optimizer fold change, no object-model change.
- `Stfld` is OUT OF SCOPE: raw `Stfld` consumes a value (`Register2`); it does
  not produce a value that feeds arithmetic, so it needs no dest seeding for this
  defect class. (The framing mentioned Stfld as the sibling escaping shape; the
  corruption path is the LOAD -- `Ldfld` -- not the store.)
- Add `NeoStep` regression probes (Call ret float/double + raw-Ldfld mul/add +
  raw-Ldfld reg-reg add) that FAULT on HEAD and pass after the fix. Probes assert
  through the existing host helper `TestCLRBinding.SumTestVector3Fields` (CLR-side
  float arithmetic) to sidestep the separate, out-of-scope `conv.i4`-float-bit-
  reinterpret bug.
- Entirely Neo-gated (`TypeSpecializeNeoOpcodes` is `#if ENABLE_NEO_MODE`), so
  Legacy `ExecuteR` is byte-identical.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-optimizer`: Add a requirement that the Neo typed-arithmetic specialization
  (immediate and binary) MUST see a correctly-typed `registerTypes` entry for
  Call-returning primitive-float/double/long producers AND raw-`Ldfld` CLR-struct
  primitive-field producers, so a `float`/`double`/`long` operand obtained from a
  method return or a raw CLR-struct field load is specialized to the
  `*_R4`/`*_R8`/`*_I8` arithmetic variant and is NOT left as a plain integer op
  whose raw bits get integer-added. This is the root-cause fix; it is the sibling
  of the child-16 Ldind/Ldelem seeding requirement and closes child 16's two
  explicitly-deferred producer families.

## Impact

- **Code**: `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` --
  `TypeSpecializeNeoOpcodes`: (a) extend the `Call` case to seed the primitive
  return type (~8-12 lines); (b) add a `case OpCodeREnum.Ldfld:` seeding branch
  that resolves the CLR field and seeds the primitive type (~15-20 lines). All
  Neo-only. No change to `Optimizer.ELDC.cs`, `Optimizer.Utils.cs`, the runtime
  `ExecuteNeo` arms, or the object model.
- **Tests**: the temporary re-audit probe `TestCases/NeoStepFloatSeedingProbe.cs`
  is promoted to the permanent regression suite (4 probes).
- **Legacy**: neutral by construction (all changes under `ENABLE_NEO_MODE`).
  Legacy's `Addi`/`Add` runtime arms re-dispatch on the per-slot
  `StackObject.ObjectType` tag, so Legacy was never affected.
- **Regression surface**: low. Seeding a primitive float/double/long/int type for
  these producers has no effect on the other type-spec decisions (branch-ref,
  Ceq_Ref, field-access-inline, Move), which all key on `IsNeoReferenceSlot` /
  `IsValueType` -- a primitive is neither. The raw-`Ldfld` field resolution
  reuses the proven child-4 hash-lookup path.
