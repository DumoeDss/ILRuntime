## ADDED Requirements

### Requirement: ExecuteNeo implements conv.r.un (Conv_R_Un)

Under `ENABLE_NEO_MODE`, the `conv.r.un` CIL opcode (convert an unsigned integer
to `F`, i.e. `OpCodeREnum.Conv_R_Un`) SHALL be served by a `case OpCodeREnum.Conv_R_Un:`
arm in `ExecuteNeo` (`ILIntepreter.Neo.cs`), mirroring the Legacy arm at
`ILIntepreter.Register.cs:1258-1294`.

The opcode is JIT-lowered identically to `Conv_R4`/`Conv_R8`: the source-type tag
is stamped in `ip->Operand2` (`JITCompiler.cs:975`), the destination register
width is `DoubleType` (`GetConvResultType`, `JITCompiler.cs:1466` -> 8 bytes), and
`LowerNeoOffsets` remaps its `DstOffset`/`SrcOffset` (`Optimizer.Neo.cs:589`). The
arm SHALL read the source value as UNSIGNED (via the existing `ReadConvU4`/
`ReadConvU8` helpers for the `I4`/`U4`/`I8`/`U8` tags, or a direct
`*(float*)`/`*(double*)` read for `R4`/`R8`), widen it to `double`, and write it
to `*(double*)(frameBase + ip->DstOffset)`. Integers SHALL be interpreted
unsigned (e.g. a source `0xFFFFFFFF` as `uint` yields `4294967295.0`, not `-1.0`).

This arm SHALL NOT alter Legacy (`ExecuteR` handles `conv.r.un` already). It is
Neo-gated (`#if ENABLE_NEO_MODE`).

#### Scenario: conv.r.un of a uint widens to a positive double under Neo
- **WHEN** an IL method under `ENABLE_NEO_MODE` executes the C# lowering for
  `double d = (double)(uint)0xFFFFFFFF;` (which emits `conv.r.un` then `conv.r8`)
  and reads `d` back
- **THEN** `d == 4294967295.0` (the unsigned interpretation), proving the Neo
  `Conv_R_Un` arm ran and interpreted the source unsigned, rather than throwing
  `NotImplementedException` ("Neo: opcode ... not yet implemented") or producing
  `-1.0` (the signed misinterpretation).

#### Scenario: conv.r.un is Legacy-neutral
- **WHEN** the same `(double)(uint)` conversion runs under the Legacy register VM
  (`useRegister=true`, plain `Debug`)
- **THEN** it behaves as before (Legacy's own `Conv_R_Un` arm serves it); the
  Neo-gated arm does not alter Legacy dispatch.

### Requirement: ExecuteNeo implements Switch (jump table)

Under `ENABLE_NEO_MODE`, the CIL `switch` opcode (`OpCodeREnum.Switch`) SHALL be
served by a `case OpCodeREnum.Switch:` arm in `ExecuteNeo`, mirroring the Legacy
arm at `ILIntepreter.Register.cs:2754-2764`.

The arm SHALL read the integer index value, look up the jump table
`method.JumpTablesRegister[ip->Operand]` (already populated by `PrepareJumpTable`,
`JITCompiler.cs:2174/2252`; targets remapped by `LowerNeoOffsets`,
`Optimizer.Neo.cs:1728`), and when `0 <= index < table.Length` SHALL set
`ip = ptr + table[index]` and continue; otherwise it SHALL fall through (no jump),
exactly as Legacy does.

This arm SHALL NOT alter Legacy. It is Neo-gated (`#if ENABLE_NEO_MODE`).

#### Scenario: a multi-case switch selects the matching case under Neo
- **WHEN** an IL method under `ENABLE_NEO_MODE` executes a C# `switch (int)` over
  at least three cases (e.g. `switch(x){ case 0: return 10; case 1: return 20;
  case 2: return 30; default: return -1; }`) with `x == 1` and reads the result
- **THEN** the result is `20` (the matching case), proving the Neo `Switch` arm
  read the index and jumped via the jump table, rather than throwing
  `NotImplementedException` ("Neo: opcode ... not yet implemented").

#### Scenario: an out-of-range switch index falls through to the default under Neo
- **WHEN** the same switch is executed with an index greater than the highest case
  label (e.g. `x == 99`)
- **THEN** the arm falls through (no jump-table jump) and the default branch runs
  (result `-1`), matching Legacy semantics (out-of-range = no jump).

#### Scenario: Switch is Legacy-neutral
- **WHEN** the same switch runs under the Legacy register VM
- **THEN** it behaves as before; the Neo-gated arm does not alter Legacy dispatch.
