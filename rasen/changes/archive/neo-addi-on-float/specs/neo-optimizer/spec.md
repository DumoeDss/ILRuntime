## ADDED Requirements

### Requirement: Neo typed-arithmetic specialization MUST see a seeded registerTypes entry for indirect-load and element-load float/double/long producers

`TypeSpecializeNeoOpcodes` (`JITCompiler.cs`) SHALL seed `registerTypes[op.Register1]`
with the correct primitive type for every `Ldind_*` and `Ldelem_*` producer of a
float/double/long (and int) value, so that the typed-immediate specialization
(`Addi/Subi/Muli/Divi/Remi -> *_R4/*_R8/*_I8`) and the typed-binary specialization
(`Add/Sub/Mul/Div/Rem -> *_R4/*_R8/*_I8`) fire for an operand loaded via a
byref/CLR-struct-field indirection or an array element. A float/double/long operand
that is NOT seeded MUST NOT reach the runtime as a plain integer immediate whose raw
IEEE bits get integer-added.

The Neo register VM's frame is UNTYPED (there is no per-slot `ObjectType` tag
the way Legacy `ExecuteR` has via `StackObject.ObjectType`). Consequently
Neo's plain `Addi`/`Subi`/`Muli`/`Divi`/`Remi` runtime arms
(`ILIntepreter.Neo.cs:2468-2575`) and its plain `Add`/`Sub`/`Mul`/`Div`/`Rem`
arms (`ILIntepreter.Neo.cs:1882-1920`) are HARDCODED INTEGER: they read the
operand bytes as `int`/`long` and the immediate as `ip->Operand`. For a
float/double operand the result is correct ONLY if the JIT type-specialization
pass (`TypeSpecializeNeoOpcodes`, `JITCompiler.cs`) has rewritten the op to the
typed `*_R4`/`*_R8`/`*_I8` variant, whose runtime arm reads
`ip->OperandFloat`/`ip->OperandDouble`/`ip->OperandLong`. That specialization
(immediate form via `GetTypedImmediateBinaryOpcode` at `JITCompiler.cs:1842`,
binary form via `GetTypedBinaryOpcode` at `JITCompiler.cs:1685`) is driven
SOLELY by `InferPrimTag(registerTypes[op.Register2])`.

Therefore `TypeSpecializeNeoOpcodes` MUST seed `registerTypes[op.Register1]`
with the correct primitive type for every producer of a float/double/long
value that can flow into an arithmetic op. This requirement pins the producers
that were MISSING from the seeding switch at the time this requirement was
added: the indirect loads `Ldind_R4`/`Ldind_R8`/`Ldind_I8` (and `Ldind_I4`)
and the element loads `Ldelem_R4`/`Ldelem_R8`/`Ldelem_I8` (and `Ldelem_I4`).
Previously only `Ldc_*` (`JITCompiler.cs:865-872`) and `Ldfld_*` (`:1066-1073`)
seeded these types, so a float/double/long loaded via a byref/CLR-struct-field
indirection (`ldflda; ldind.r4`) or an array element (`ldelem.r4`) was
mis-typed as the default `I4`, the typed specialization silently no-oped, and a
plain integer immediate whose raw IEEE bits got integer-added reached the
runtime (e.g. `a.X += 100` lowered to `addi r,r,0x42C80000`, the bits of
`100.0f`, yielding garbage).

This requirement is Neo-only: the seeding runs inside
`TypeSpecializeNeoOpcodes`, which is `#if ENABLE_NEO_MODE`. Legacy `ExecuteR`
is the REFERENCE and is unaffected -- Legacy's `Addi` runtime arm
(`ILIntepreter.Register.cs:377-402`) re-dispatches on `reg1->ObjectType` and
reads `OperandFloat` for a `Float` slot, so Legacy was never dependent on the
JIT-time seed. The Legacy NeoStep-filter smoke MUST show the SAME pre-existing
failure set with and without this change (stash-toggle proof).

The shared constant-fold (`Optimizer.ELDC.EliminateConstantLoad` +
`Optimizer.Utils.GetIntemediateValueOpcode`/`ReplaceRegisterWithConstant`) is
CORRECT and MUST NOT be changed for this defect: it is type-agnostic by design
and Legacy depends on the plain `Addi` form it produces. A future change that
proposes to "fix addi-on-float in the constant-fold" MUST be rejected on these
grounds; the fix site is the `TypeSpecializeNeoOpcodes` seeding switch.

#### Scenario: float field-by-indirection plus-equals a constant computes the correct value
- **WHEN** a Neo method executes `a.X += 100` where `a.X` is a `float` field of
  a CLR value type (`TestVector3`) reached via `ldflda; ldind.r4` (so the
  operand is produced by `Ldind_R4`, not `Ldfld_R4`), `a.X` starts at `1f`, and
  the result is observed via the host helper
  `TestCLRBinding.SumTestVector3Fields(a, a)` = `(int)(a.X+a.Y+a.Z+a.X+a.Y+a.Z)`
- **THEN** the sum MUST equal `206` (`a` becomes `(101,1,1)`), proving the
  `add` was specialized to `Add_R4`/`Addi_R4` and the runtime read
  `OperandFloat` -- NOT the integer `0x42C80000` bit-pattern
- **AND** on HEAD with the fix stashed the probe MUST FAULT (DivideByZero on
  `sum != 206`), because the unspecialized integer `Addi` integer-adds the
  float bits

#### Scenario: float field-by-indirection minus-equals a constant (subi path)
- **WHEN** a Neo method executes `a.X -= 100` under the same `Ldind_R4` shape
  (`a.X` starts at `1f`)
- **THEN** `SumTestVector3Fields(a, a)` MUST equal `-194` (`a` becomes
  `(-99,1,1)`), proving the `sub` was specialized to the `*_R4` form
- **AND** on HEAD with the fix stashed the probe MUST FAULT

#### Scenario: compound float mul-then-add (muli + addi paths)
- **WHEN** a Neo method executes `a.X = a.X * 2 + 1` under the same `Ldind_R4`
  shape (`a.X` starts at `1f`)
- **THEN** `SumTestVector3Fields(a, a)` MUST equal `10` (`a` becomes `(3,1,1)`),
  proving BOTH the `mul` and the trailing `add` specialized to their `*_R4`
  forms
- **AND** on HEAD with the fix stashed the probe MUST FAULT

#### Scenario: double and long indirect/element producers are seeded too
- **WHEN** a Neo method loads a `double` via `Ldind_R8`/`Ldelem_R8` or a
  `long` via `Ldind_I8`/`Ldelem_I8` and combines it with a constant
  (`+=`, `-=`, etc.)
- **THEN** the specialization MUST produce the `*_R8` / `*_I8` arithmetic
  variant (the runtime reads `OperandDouble` / `OperandLong`), and the result
  MUST be numerically correct
- **AND** the JIT dump of the arith op MUST show `*_R8` / `*_I8` (NOT plain
  `Addi`/`Add`) and `registerTypes[]` for the operand register MUST show
  `DoubleType` / `LongType` (NOT the default `IntType`)

#### Scenario: int indirect/element producers stay correct (no regression)
- **WHEN** a Neo method loads an `int` via `Ldind_I4`/`Ldelem_I4` and combines
  it with a constant
- **THEN** the integer arithmetic MUST remain correct (the `I4` seed matches
  the prior default fallback, so this requirement MUST NOT regress the int path)

#### Scenario: typed-immediate AND typed-binary forms both covered
- **WHEN** a Neo method combines two `Ldind_R4` results in a register-register
  `add` (no constant, so no ELDC fold to `Addi`) -- e.g. `float r = a.X + a.Y`
- **THEN** the `add` MUST specialize to `Add_R4` (both operand registers are
  seeded `FloatType` by their `Ldind_R4` producers) and compute the correct
  float result -- NOT stay as the plain integer `Add` arm
- **AND** this proves the fix covers the binary form (`Add`/`Sub`/`Mul`) in
  addition to the immediate form (`Addi`/`Subi`/`Muli`)

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM under the NeoStep filter
- **THEN** the Legacy NeoStep-filter smoke MUST show the SAME pre-existing
  failure set with and without this change (stash-toggle proof), because the
  new `Ldind_*`/`Ldelem_*` seeding cases are inside `TypeSpecializeNeoOpcodes`,
  which compiles out under plain `Debug`, and Legacy's `Addi` arm re-dispatches
  on `StackObject.ObjectType` regardless

#### Scenario: NeoStep regression smoke stays green
- **WHEN** the seeding is applied (`Debug_Neo`) and the full NeoStep smoke is
  run
- **THEN** the NeoStep smoke MUST stay green at its current baseline (ZERO
  regressions; the change only ADDS correct float/double/long seeds that make
  specialization fire where it already should have)
