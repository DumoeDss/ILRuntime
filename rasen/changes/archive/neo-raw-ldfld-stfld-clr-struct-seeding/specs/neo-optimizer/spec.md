## ADDED Requirements

### Requirement: Neo typed-arithmetic specialization MUST see a seeded registerTypes entry for Call-returning and raw-Ldfld CLR-struct primitive producers

`TypeSpecializeNeoOpcodes` (`JITCompiler.cs`) SHALL seed
`registerTypes[op.Register1]` with the correct primitive type for every producer
of a float/double/long (and int) value that flows into an arithmetic op, where
that producer is EITHER a method `Call`/`Callvirt`/`Callvirt_IL`/`Callvirt_CLR`/
`Call_Redirect` whose resolved `ReturnType` is a primitive, OR a raw `Ldfld` of a
CLR-struct primitive field (the raw `OpCodeREnum.Ldfld` that escapes the typed
splitter because the declaring type is a `CLRType`). A float/double/long operand
from either producer that is NOT seeded MUST NOT reach the runtime as a plain
integer op whose raw IEEE bits get integer-added/multiplied.

The Neo register VM's frame is UNTYPED (there is no per-slot `ObjectType` tag the
way Legacy `ExecuteR` has via `StackObject.ObjectType`). Consequently Neo's plain
`Addi`/`Subi`/`Muli`/`Divi`/`Remi` runtime arms and its plain
`Add`/`Sub`/`Mul`/`Div`/`Rem` arms are HARDCODED INTEGER: they read the operand
bytes as `int`/`long` and the immediate as `ip->Operand`. For a float/double
operand the result is correct ONLY if the JIT type-specialization pass
(`TypeSpecializeNeoOpcodes`, `JITCompiler.cs`) has rewritten the op to the typed
`*_R4`/`*_R8`/`*_I8` variant, whose runtime arm reads
`ip->OperandFloat`/`ip->OperandDouble`/`ip->OperandLong`. That specialization is
driven SOLELY by `InferPrimTag(registerTypes[op.Register2])`.

The Call case in `TypeSpecializeNeoOpcodes` (`JITCompiler.cs:1217-1282`) resolves
the callee's `ReturnType` but, prior to this requirement, used it ONLY to decide
whether to CLEAR a stale in-frame-VT or reference type off a REUSED dest; it
never SEEDED a FRESH dest. So a call dest that was a fresh temp (the common case:
`x = GetF() + 100f`, where the call result is not first stored to a typed local)
stayed `null`, the specialization fell back to `I4`, and a plain integer
`add`/`addi` integer-added the float bits. This requirement pins the Call case
to seed `registerTypes[op.Register1]` with the resolved primitive `ReturnType`
(Float/Double/Long/Int). The seed is SAFE because a primitive return never
conflicts with the existing stale-VT-keep / stale-ref-keep logic (those only
keep non-primitive VT/ref types; a primitive return is excluded by
`IsPrimitive`).

There is NO seeding case for the raw `OpCodeREnum.Ldfld` (only the typed
`Ldfld_R4`/`Ldfld_R8`/`Ldfld_I8` arms are seeded). A field whose declaring type
is a `CLRType` escapes the typed splitter (the splitter emits the typed arms only
for an `ILType` declaring type), so it reaches `TypeSpecializeNeoOpcodes` as a
raw `Ldfld` with `OperandLong = (typeHash<<32)|fieldHash`. This requirement pins
a `case OpCodeREnum.Ldfld:` that resolves the declaring `CLRType` and the field
(`appdomain.GetType(typeHash) as CLRType`, `ct.GetField(fieldHash)` -- identical
to the child-4 runtime raw-Ldfld handler, `ILIntepreter.Neo.cs:3878-3886`) and
seeds `registerTypes[op.Register1]` with the field's primitive type
(`float`->FloatType, `double`->DoubleType, `long`/`ulong`->LongType, other
primitive->IntType). Non-primitive (VT/ref) fields are left unseeded: they do
not feed primitive arithmetic, and seeding a VT there would interact with the
field-access-inline discriminator.

This is the sibling of the child-16 requirement (which pinned the `Ldind_*`/
`Ldelem_*` producers) and closes child 16's two explicitly-deferred producer
families. It is Neo-only: the seeding runs inside `TypeSpecializeNeoOpcodes`,
which is `#if ENABLE_NEO_MODE`. Legacy `ExecuteR` is the REFERENCE and is
unaffected -- Legacy's `Addi`/`Add` runtime arms re-dispatch on
`StackObject.ObjectType` and read `OperandFloat` for a `Float` slot, so Legacy
was never dependent on the JIT-time seed. The Legacy NeoStep-filter smoke MUST
show the SAME pre-existing failure set with and without this change (stash-toggle
proof).

The shared constant-fold is CORRECT and MUST NOT be changed for this defect: it
is type-agnostic by design and Legacy depends on the plain `Addi` form it
produces. A future change that proposes to "fix Call/raw-Ldfld float arithmetic
in the constant-fold" MUST be rejected on these grounds; the fix site is the
`TypeSpecializeNeoOpcodes` seeding switch (the Call case + the new raw-Ldfld
case).

#### Scenario: float returned from a method then combined with a constant computes the correct value
- **WHEN** a Neo method executes `v.X = GetF() + 100f` where `GetF()` is a
  non-inlinable IL method returning `float` (so the call dest is a fresh temp
  produced by `Call`), `GetF()` returns `1f`, `v` starts at `TestVector3.One =
  (1,1,1)`, and the result is observed via the host helper
  `TestCLRBinding.SumTestVector3Fields(v, v)` = `(int)(v.X+v.Y+v.Z+v.X+v.Y+v.Z)`
- **THEN** the sum MUST equal `206` (`v` becomes `(101,1,1)`), proving the `add`
  was specialized to `Add_R4`/`Addi_R4` and the runtime read `OperandFloat` --
  NOT the integer `0x42C80000` bit-pattern
- **AND** on HEAD with the fix stashed the probe MUST FAULT (DivideByZero on
  `sum != 206`), because the unspecialized integer `addi` integer-adds the float
  bits -- CONFIRMED at HEAD (fc2baa26): JIT emits `call r7, GetF()` then plain
  `addi r7,r7,1120403456` (0x42C80000 = bits of `100.0f`)

#### Scenario: double returned from a method then combined with a constant (_R8 path)
- **WHEN** a Neo method executes `v.X = (float)(GetD() + 100.0)` where `GetD()`
  returns `1.0` (double), `v` starts at `(1,1,1)`
- **THEN** `SumTestVector3Fields(v, v)` MUST equal `206` (`v` becomes `(101,1,1)`),
  proving the `add` was specialized to the `*_R8` form (runtime reads
  `OperandDouble`)
- **AND** on HEAD with the fix stashed the probe MUST FAULT

#### Scenario: a float field read via raw ldfld then multiplied and added computes the correct value
- **WHEN** a Neo method executes `a.X = a.X * 2 + 1` in single-expression form
  (so the READ of `a.X` lowers through a RAW `ldfld` of the CLR-struct field --
  the child-4 escaping shape, NOT `ldflda; ldind.r4`), `a.X` starts at `1f`,
  `a` is `TestVector3.One = (1,1,1)`
- **THEN** `SumTestVector3Fields(a, a)` MUST equal `10` (`a` becomes `(3,1,1)`),
  proving BOTH the `mul` and the trailing `add` specialized to their `*_R4`
  forms
- **AND** on HEAD with the fix stashed the probe MUST FAULT -- CONFIRMED at HEAD:
  JIT emits raw `ldfld r7, r0, ...` then plain `muli r7,r7,1073741824`
  (0x40000000 = bits of `2.0f`) and `addi r7,r7,1065353216` (0x3F800000 = bits
  of `1.0f`); runtime local dump shows `a = (1.7014118E+38, 1, 1)`

#### Scenario: two raw-ldfld float operands in a register-register add (typed-binary form)
- **WHEN** a Neo method executes `a.X = a.X + a.Y` (both operands raw-ldfld-loaded
  CLR-struct floats; no constant, so no ELDC fold to `Addi`), `a` starts at
  `(1,1,1)`
- **THEN** `SumTestVector3Fields(a, a)` MUST equal `8` (`a` becomes `(2,1,1)`),
  proving the binary `add` specialized to `Add_R4` -- NOT the plain integer
  `Add` arm
- **AND** this proves the fix covers the binary form (`Add`/`Sub`/`Mul`) in
  addition to the immediate form (`Addi`/`Subi`/`Muli`)

#### Scenario: long returned from a method or read via raw ldfld is seeded too
- **WHEN** a Neo method obtains a `long` from a `Call` returning `long` or from a
  raw `ldfld` of a CLR-struct `long` field and combines it with a constant
- **THEN** the specialization MUST produce the `*_I8` arithmetic variant (the
  runtime reads `OperandLong`), and the result MUST be numerically correct (the
  default-`I4` fallback would truncate a 64-bit value to 32 bits)
- **AND** the JIT dump of the arith op MUST show `*_I8` (NOT plain `Addi`/`Add`)

#### Scenario: int producers stay correct (no regression)
- **WHEN** a Neo method obtains an `int` from a `Call` returning `int` or from a
  raw `ldfld` of a CLR-struct `int` field and combines it with a constant
- **THEN** the integer arithmetic MUST remain correct (the `I4` seed matches the
  prior default fallback, so this requirement MUST NOT regress the int path)

#### Scenario: non-primitive (VT/ref) raw-ldfld fields are left unseeded
- **WHEN** a raw `ldfld` reads a CLR-struct field whose type is a value type or a
  reference type (not a primitive)
- **THEN** the dest MUST be left unseeded (the `case Ldfld:` seeds ONLY primitive
  field types), so the field-access-inline discriminator and the existing
  field-load arms are UNCHANGED -- this requirement MUST NOT perturb VT/ref
  field handling

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM under the NeoStep filter
- **THEN** the Legacy NeoStep-filter smoke MUST show the SAME pre-existing
  failure set with and without this change (stash-toggle proof), because the new
  Call trailing-seed and the `case Ldfld:` seeding are inside
  `TypeSpecializeNeoOpcodes`, which compiles out under plain `Debug`, and
  Legacy's `Addi`/`Add` arms re-dispatch on `StackObject.ObjectType` regardless

#### Scenario: NeoStep regression smoke stays green
- **WHEN** the seeding is applied (`Debug_Neo`) and the full NeoStep smoke is run
- **THEN** the NeoStep smoke MUST stay green at its current baseline (ZERO
  regressions; the change only ADDS correct float/double/long seeds that make
  specialization fire where it already should have)
