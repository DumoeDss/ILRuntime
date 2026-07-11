## Why

Step 12 made in-frame value types first-class flat bytes in the Neo frame and
added inline field access, but whole value-type copy / assignment
(`Vector3 a = b;`, `struct { string s; }` assignment) is still broken: the
existing `Move` opcode's `ExecuteNeo` arm can only copy at most ONE reference
slot (`ip->Operand == 1` branch copies a single ref), so any value type with
more than one reference field (or any copy path that needs the dest's full ref
run) silently corrupts or truncates references. Step 12b closes this by
introducing a dedicated `Move_Vt` opcode that copies the full primitive byte
range AND every reference slot of the destination value type, selected by a new
JIT `LowerMove` pass that runs after BCP/FCP copy-elision and is given the
destination slot's value type (primitiveSize + refCount) at compile time.

## What Changes

- Add `Move_Vt` to `OpCodeREnum`. Its `ExecuteNeo` arm performs
  `Unsafe.CopyBlock` of the destination's full primitive byte range, then a
  per-slot loop copying each of the destination's reference-field mStack slots
  (handling the null/-1 convention independently on both src and dst).
- Add a JIT `LowerMove` pass (inside `TypeSpecializeNeoOpcodes`,
  JITCompiler.cs) that scans surviving `Move` instructions and rewrites to
  `Move_Vt` when the destination register is an in-frame IL value type with
  refCount > 0. Pure-primitive VT copies keep the existing `Move` (its byte
  CopyBlock is already correct when refCount == 0).
- Encode `primitiveSize` and `refCount` into `Move_Vt`'s stable, non-aliased
  Operand fields (`Operand2`, `Operand3`) plus the dest's `RefOffset` (via the
  existing `LowerNeoOffsets` Move-lowering stamping, extended for `Move_Vt`),
  so the opcode is self-contained at runtime.
- Add `TestCases/NeoStep12bTest.cs` covering: pure-primitive VT copy,
  VT-with-one-ref-field copy, VT-with-multiple-ref-fields copy (the current
  breakage), nested-VT copy, and copy-aliasing independence (mutating src after
  copy does not change dst).

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `neo-value-types`: Add value-type copy / assignment semantics (whole-struct
  copy with independent reference-slot copy, no aliasing) via the `Move_Vt`
  opcode and the JIT `LowerMove` pass.

## Impact

- `ILRuntime/Runtime/Intepreter/RegisterVM/OpCodeREnum.cs` — add `Move_Vt`
  (placed after the existing `Move`).
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` — new `LowerMove`
  logic inside `TypeSpecializeNeoOpcodes` (runs on register-index form, where
  dest register index + `registerTypes` are valid). `GetOpcodeDestRegister` /
  `GetOpcodeSourceRegister` must report `Move_Vt`'s registers so BCP/FCP/inline
  keep treating it like `Move` (registered once; BCP/FCP run BEFORE the
  rewrite, so they only ever see `Move`).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` — extend the
  `LowerNeoOffsets` `Move` case (lines ~153-177) to also lower `Move_Vt`:
  resolve src/dst byte offsets via `LowerR1R2`, stamp `Operand2` = primitiveSize,
  `Operand3` = dst RefOffset, and resolve the src ref base. The
  `addrAlias` pre-scan is irrelevant here (Moves carry real registers, not
  addresses).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` — add the
  `Move_Vt` `ExecuteNeo` arm near the existing `Move` arm (lines ~440-458).
- `TestCases/NeoStep12bTest.cs` — new, ASCII, following the NeoStep12 test
  convention (DivideByZero assertions, not `throw`).
- No Legacy path touched; all new runtime code is `#if ENABLE_NEO_MODE`.
- Regression surface: whole-VT copy is pervasive (every `Vector3 a = b;`, every
  struct assignment, every struct-return-temp). A buggy `Move_Vt` can silently
  corrupt any NeoStep test that assigns a struct. The full NeoStep smoke is the
  mandatory gate.
