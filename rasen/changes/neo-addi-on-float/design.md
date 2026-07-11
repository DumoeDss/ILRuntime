## Context

`float += const` is broken on the Neo register VM. Trace, from the reproducer
`var a = TestVector3.One; a.X += 100;` (`a.X` is a `float` field of the CLR
struct `TestVector3`, reached via `ldloca a; ldflda X; ldind.r4`):

1. Roslyn emits `... ldind.r4; ldc.r4 100; add; stind.r4` (the literal `100`
   is constant-folded through the implicit `int->float` conv at C# compile
   time, so it arrives as `ldc.r4`, not `ldc.i4; conv.r4`).
2. The **front-half** optimizer pass `EliminateConstantLoad` (ELDC,
   `Optimizer.ELDC.cs`, invoked at `JITCompiler.cs:517`) folds the binary
   `add(floatReg, ldc.r4 100)` into the immediate form: it calls
   `GetIntemediateValueOpcode(Add) -> Addi` (plain INTEGER, `Optimizer.Utils.cs:158`)
   and `ReplaceRegisterWithConstant`, which writes `op.OperandFloat =
   constant.OperandFloat` (= `100.0f`, bit-pattern `0x42C80000`) into the
   `Addi`'s immediate field (`Optimizer.Utils.cs:144-146`). Result so far:
   `addi r,r,0x42C80000` -- an integer `addi` whose immediate holds the IEEE
   bits of `100.0f`. This step is **type-agnostic and shared with Legacy**.
3. The **back-half** `TypeSpecializeNeoOpcodes` (`JITCompiler.cs:736`, Neo-only)
   is supposed to specialize `Addi -> Addi_R4` via
   `GetTypedImmediateBinaryOpcode(Addi, InferPrimTag(registerTypes[op.Register2]))`
   (`JITCompiler.cs:1017`). For this to fire, `registerTypes[op.Register2]`
   (the register holding `a.X`) MUST be `FloatType`.
4. **The gap:** `registerTypes` is seeded Float/Double/Long ONLY for `Ldc_*`
   (`JITCompiler.cs:865-872`) and `Ldfld_*` (`:1066-1073`). The `Ldind_R4`
   that loaded `a.X` is **not** in the seeding switch (it appears only at JIT
   emission, `:2911`). So `registerTypes[a.X's reg]` stays the default `I4`,
   `InferPrimTag` returns `I4`, `GetTypedImmediateBinaryOpcode` is a no-op, and
   the plain integer `Addi` survives to runtime.
5. **Why it corrupts on Neo but not Legacy:** Neo's frame is UNTYPED (no
   per-slot `ObjectType` tag), so Neo's `Addi` runtime arm is hardcoded
   integer (`ILIntepreter.Neo.cs:2468-2470`:
   `*(int*)dst = *(int*)src + ip->Operand`). Legacy's `Addi` runtime arm
   (`ILIntepreter.Register.cs:377-402`) re-dispatches on `reg1->ObjectType`
   and, for a `Float` slot, reads `ip->OperandFloat` -- so Legacy was always
   correct despite the identical fold. (Neo's plain `Add`/`Sub`/`Mul` arms,
   `Neo.cs:1882-1890`, are likewise hardcoded integer, so the same seeding gap
   would corrupt a pure register-register float `Add` on this path too.)

Confirmed pre-existing: child-15's reviewer stashed both source files at HEAD
and the HEAD JIT emits the identical `addi r,r,0x42C80000`.

## Goals / Non-Goals

**Goals:**
- Make Neo compute correct results for `float`/`double`/`long` operands loaded
  via `Ldind_*` (byref / CLR-struct field) or `Ldelem_*` (array element) and
  then combined with a constant (`+=`, `-=`, `*=`, `/=`, `%=`) or with another
  register.
- Root-cause, minimal, Neo-only, Legacy-neutral fix.
- A NeoStep probe that FAULTs on HEAD (proving the defect) and passes after.

**Non-Goals:**
- The separate `conv.i4`-on-float bit-reinterpret bug (child-15 note). Probes
  assert via CLR-side float arithmetic to sidestep it; it stays out of scope.
- Reworking the `registerTypes` dataflow into a phi-aware analysis (child-11
  noted it is a single linear pass with no phi-merge). The straight-line
  load-then-arithmetic shape is same-block and reliable; broader dataflow
  hardening is a different scope.
- Seeding every conceivable float producer (e.g. `Call` returning float). Seed
  the load producers the reproducer and sibling patterns reach; add more only
  if a probe demands it.

## Decisions

### D1: Fix at the seeding site (TypeSpecialize), NOT at the constant-fold site

**Decision:** add `Ldind_*` / `Ldelem_*` cases to the
`TypeSpecializeNeoOpcodes` seeding switch so `registerTypes[op.Register1]`
carries `FloatType`/`DoubleType`/`LongType`/`IntType`. The existing
immediate (`Addi -> *_R4/R8/I8`) and binary (`Add -> *_R4/R8/I8`)
specialization then fire unchanged.

**Rationale:**
- It is the root cause. The fold in step 2 is correct as-is (and Legacy depends
  on it). The defect is that Neo cannot recover the operand type at specialize
  time because the load producer never seeded it.
- It fixes the whole class for free: `addi`/`subi`/`muli`/`divi`/`remi`
  (immediate) AND plain `add`/`sub`/`mul`/`div`/`rem` (register-register) on
  this path -- they all key on `registerTypes[Register2]`.
- It is Neo-only (`TypeSpecializeNeoOpcodes` is `#if ENABLE_NEO_MODE`) and
  touches no shared code, so Legacy is byte-identical by construction.

**Alternatives considered and rejected:**
- **(a) Type-aware constant-fold** (make ELDC emit `Addi_R4` directly when the
  constant is `Ldc_R4`). Rejected: the typed immediate opcodes (`Addi_R4` etc.)
  are NOT in the shared front-half utility switches -- e.g.
  `GetOpcodeSourceRegister` (`Optimizer.Utils.cs:681-700`) lists only the plain
  immediate variants and throws `NotImplementedException` on anything else, and
  ELDC itself calls it on each consumer (`Optimizer.ELDC.cs:58`); a prior
  fold's `Addi_R4` would crash a later ELDC iteration. Making it Neo-gated
  would still require extending every shared utility that enumerates immediate
  opcodes, and Legacy's runtime has no `Addi_R4` arm, so it is a larger, more
  invasive surface for no gain over the seeding fix. It would also only fix the
  immediate form, not plain `Add`.
- **(b) Skip the fold for float constants + broaden the plain `Add` case to
  also consult `registerTypes[Register3]`.** Rejected: still depends on
  `registerTypes` (the `Ldc_R4` operand seeds its own dest Float, which helps
  only when that operand sits in the probed register position), leaves the
  constant load un-folded (a pessimization), and broadening the binary case
  touches a hot specialization path. D1 achieves the same correctness with a
  smaller blast radius.

### D2: Which producers to seed

Seed `Ldind_R4 -> FloatType`, `Ldind_R8 -> DoubleType`, `Ldind_I8 -> LongType`,
`Ldind_I4 -> IntType`, and the matching `Ldelem_R4`/`Ldelem_R8`/`Ldelem_I8`/
`Ldelem_I4`. Rationale:
- `R4`/`R8`/`I8` are the types whose default-`I4` fallback would be WRONG
  (float/double bit-patterns integer-added; long truncated to 32 bits). These
  are the load-bearing additions.
- `I4` is seeded for symmetry/future-proofing (the default fallback already
  yields `I4`, so it is a no-op today, but explicit seeding avoids surprises if
  the default ever changes).
- The signed/unsigned smaller widths (`Ldind_I1/U1/I2/U2/U4`, etc.) widen to
  `int` in CIL semantics and their consumers already operate on `int`; they are
  not on the float-correctness critical path, so they are left to a follow-up
  unless a probe fails. (Seeding them is harmless but out of scope for this
  fix.)

Placement: immediately after the existing `Ldfld_R4`/`Ldfld_R8` block
(`JITCompiler.cs:1069-1074`), within the same `switch (op.Code)` in
`TypeSpecializeNeoOpcodes`.

### D3: Probe design

Three probes in a new `TestCases/NeoStepAddiOnFloatTest.cs`, all using
`TestVector3` (CLR struct, float X/Y/Z; `TestVector3.One == (1,1,1)`) so `a.X`
is loaded by `ldflda; ldind.r4` -- the unseeded-`Ldind_R4` path. Each modifies
only `X` and asserts through the host helper
`TestCLRBinding.SumTestVector3Fields(a, a)` = `(int)(a.X+a.Y+a.Z+a.X+a.Y+a.Z)`
computed in CLR, so the float arithmetic + the int conversion happen on the
host (sidestepping the `addi` bug under test AND the out-of-scope
`conv.i4`-float-bit-reinterpret bug). A wrong result trips a deliberate `1/0`
(DivideByZero) -- the child-1/child-2 FAULT-to-fail discipline.

- TC1 `a.X += 100` -> a=(101,1,1) -> sum = (101+1+1)*2 = 206. (addi)
- TC2 `a.X -= 100` -> a=(-99,1,1) -> sum = (-99+1+1)*2 = -194. (subi)
- TC3 `a.X = a.X*2 + 1` -> a=(3,1,1) -> sum = (3+1+1)*2 = 10. (muli + addi)

On HEAD each computes garbage (integer op on float bits) -> sum != expected ->
DivideByZero. After D1, the `*_R4` arms compute the right float -> pass.

### D4: Scope automatically covers subi/muli/divi/remi and plain Add/Sub/Mul

Because D1 seeds the operand type (not per-opcode), every typed-immediate and
typed-binary specialization that reads `registerTypes[Register2]` benefits.
The crux's "check subi/muli" is satisfied without per-opcode code: TC2 covers
subi, TC3 covers muli+addi. `divi`/`remi` share the identical mechanism (same
`GetTypedImmediateBinaryOpcode` switch, `JITCompiler.cs:1842-1887`); a probe is
optional (division/modulo by a float constant is rare) but the fix covers them.

## Risks / Trade-offs

- **[registerTypes is a single-pass dataflow, no phi-merge]** (child-11) -> the
  seed is reliable only for the straight-line same-block `load; arith` shape
  (the `+=`/`-=`/`*=` patterns and `a = b.X + c.X`). Register reuse across
  blocks could still miss a type. Mitigation: this is the SAME reliability
  envelope the existing `Ldc_*`/`Ldfld_*` seeds already rely on for floats
  (and F-8 double-combine was fixed within it); the NeoStep smoke + the 3
  dedicated probes are the regression net. No pessimization, no broadening of
  the dataflow.
- **[Seeding a primitive type could perturb other type-spec decisions]** ->
  verified safe: the other consumers of `registerTypes` key on
  `IsNeoReferenceSlot` (Brtrue_Ref, Ceq_Ref, Beq_Ref, the `Move` is-ref flag)
  or `IsValueType` (field-access-inline); a primitive float/double/long is
  neither a reference slot nor a value type, so all those decisions are
  unchanged.
- **[conv.i4-on-float bit-reinterpret]** remains (separate bug) -> probes
  assert via CLR-side arithmetic so they do not depend on Neo `conv.i4` being
  correct; documented as out of scope.

## Migration Plan

None. The change is additive seeding under `ENABLE_NEO_MODE`. Rollback = revert
the one `JITCompiler.cs` hunk (removes the new `Ldind_*`/`Ldelem_*` cases);
probes fail-open (they only assert). No persisted artifact, no API, no schema.

## Open Questions

- Does a **float method return** (`Call`/`Callvirt` returning `float`) used in
  `+= const` also need seeding? The `Call` type-spec case
  (`JITCompiler.cs:1187+`) clears stale VT types but does not seed the return's
  primitive type. It is rarer than the load path (a returned float is usually
  stored to a local first, where it would need its own seeding). Defer unless a
  probe or the full smoke surfaces it; the fix site is identical (add a
  `Call`/`Callvirt` seeding branch keyed on the resolved `ReturnType`).
