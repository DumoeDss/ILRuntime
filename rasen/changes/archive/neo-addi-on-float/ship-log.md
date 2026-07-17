# Ship Log — neo-addi-on-float (child 16 of neo-overhaul)

Date: 2026-07-12
Branch: features/object-model-overhaul
Capability: neo-optimizer (ADDED requirement)
Tier: A (autonomous, --no-gate)

## Root cause

`float += const` (and `-=`, `*=`, `/=`, `%=`) computed garbage on the Neo register
VM whenever the float/double/long operand was loaded through an indirect load
(`ldind.r4`, reached via `ldloca; ldflda; ldind.r4` on a CLR-struct field) or an
element load (`ldelem.r4`). Trace:

1. Roslyn lowers `a.X += 100` (a.X float field of CLR struct TestVector3) to
   `ldloca; ldflda; ldind.r4; ldc.r4 100; add; stind.r4` (the int literal 100 is
   constant-folded through the implicit int->float conv at C# compile time).
2. The front-half optimizer pass `EliminateConstantLoad` (ELDC, shared with Legacy
   and CORRECT -- Legacy depends on it) folds `add(floatReg, ldc.r4 100)` into the
   integer immediate form `addi r, r, 0x42C80000` (the IEEE bits of 100.0f) via
   `GetIntemediateValueOpcode(Add)->Addi` + `ReplaceRegisterWithConstant` writing
   `op.OperandFloat`. This step is type-agnostic.
3. The back-half `TypeSpecializeNeoOpcodes` (Neo-only) is supposed to rewrite
   `Addi -> Addi_R4` via `GetTypedImmediateBinaryOpcode(Addi,
   InferPrimTag(registerTypes[op.Register2]))` (JITCompiler.cs:1017). For this to
   fire, `registerTypes[op.Register2]` (the register holding a.X) MUST be
   FloatType.
4. THE GAP: `registerTypes` was seeded Float/Double/Long ONLY for `Ldc_*`
   (:865-872) and `Ldfld_*` (:1066-1073). The `Ldind_R4` that loaded a.X was NEVER
   seeded -> `registerTypes[a.X's reg]` stayed the default I4 -> `InferPrimTag`
   returned I4 -> `GetTypedImmediateBinaryOpcode` no-oped -> the plain integer
   `Addi` survived to runtime.
5. Neo's frame is UNTYPED (no per-slot ObjectType tag), so Neo's plain `Addi`
   runtime arm (ILIntepreter.Neo.cs:2468) is hardcoded integer (`*(int*)dst =
   *(int*)src + ip->Operand`) -> the float bits were integer-added -> garbage.
   Legacy was never affected: its `Addi` arm re-dispatches on
   `reg1->ObjectType` (StackObject tag) and reads `OperandFloat` for a Float slot.

## The fix (Neo-only, ~10-15 lines, no new opcode, no runtime/fold change)

Added `Ldind_*`/`Ldelem_*` seeding cases to the `TypeSpecializeNeoOpcodes`
`switch (op.Code)` in `JITCompiler.cs`, immediately after the existing
`Ldfld_R4`/`Ldfld_R8` block (after :1074):

- `Ldind_R4`/`Ldelem_R4` -> `appdomain.FloatType`
- `Ldind_R8`/`Ldelem_R8` -> `appdomain.DoubleType`
- `Ldind_I8`/`Ldelem_I8` -> `appdomain.LongType`
- `Ldind_I4`/`Ldelem_I4` -> `appdomain.IntType` (symmetry; matches the prior I4
  default fallback, so today it is a no-op)

These mirror the exact `SetRegisterType(registerTypes, op.Register1,
appdomain.XxxType)` form used by the neighboring `Ldc_*`/`Ldfld_*` cases. With the
seed in place, the typed-immediate specialization (`Addi->Addi_R4`, etc.) and the
typed-binary specialization (`Add->Add_R4`, etc.) fire correctly for an operand
loaded via a byref/CLR-struct-field indirection or an array element, and the
runtime executes the proper `*_R4`/`*_R8`/`*_I8` arm reading
`OperandFloat`/`OperandDouble`/`OperandLong`.

Neo-gated: `TypeSpecializeNeoOpcodes` is `#if ENABLE_NEO_MODE`, so Legacy
`ExecuteR` is byte-identical by construction.

## Scope (free coverage)

Because the fix seeds the OPERAND TYPE (not per-opcode), every typed-immediate and
typed-binary specialization that reads `registerTypes[Register2]` benefits:
`addi`/`subi`/`muli`/`divi`/`remi` (immediate) AND plain
`add`/`sub`/`mul`/`div`/`rem` (register-register). TC2 covers subi, TC3 covers
muli+addi; divi/remi share the identical mechanism. Plain reg-reg float Add/Sub/Mul
on the same operand path is also fixed (Neo's plain Add/Sub/Mul arms :1882-1890 are
similarly hardcoded integer).

## Probe + stash-toggle evidence (FAULT-on-HEAD -> PASS-after)

3 probes in `TestCases/NeoStepAddiOnFloatTest.cs`, each using `TestVector3.One`
((1,1,1)) and asserting via the HOST helper `TestCLRBinding.SumTestVector3Fields`
= `(int)(a.X+a.Y+a.Z+a.X+a.Y+a.Z)` (CLR-side float arithmetic, sidestepping the
separate out-of-scope `conv.i4`-float-bit-reinterpret bug). A wrong value trips a
deliberate `1/0` (DivideByZero).

- TC1 `a.X += 100` -> a=(101,1,1) -> Sum==206 (addi).
- TC2 `a.X -= 100` -> a=(-99,1,1) -> Sum==-194 (subi).
- TC3 `a.X *= 2; a.X += 1` -> a=(3,1,1) -> Sum==10 (muli + addi). NOTE: the
  design's single-expression `a.X = a.X*2 + 1` lowers the READ through a raw
  `ldfld` of the CLR-struct field (child-4 escaping shape -- a DIFFERENT,
  out-of-scope producer whose registerTypes seeding is NOT covered here), so it
  would not exercise this change. Reformulated as two compound assignments, each of
  which lowers via `ldloca;ldflda;ldind.r4` (the in-scope producer, identical to
  TC1/TC2). Same expected Sum==10.

Stash-toggle (stash ONLY JITCompiler.cs, keep probes, rebuild CLI Debug_Neo):
- HEAD (fix stashed): "Ran 3 tests, 3 failded" -- 3x DivideByZeroException. HEAD
  JIT for TC1: `5:addi r7,r7,1120403456` (0x42C80000 = bits of 100.0f) ->
  `a=(-1.47e-37,1,1)`.
- Fix applied: "Ran 3 tests, 0 failded". JIT for TC1: `5:addi.r4`; TC3:
  `5:muli.r4` + `10:addi.r4` (the typed *_R4 arms reading OperandFloat).

## Smoke results

- NeoStep smoke (Debug_Neo + useRegister=true, NeoStep filter): **346/0** (343
  baseline + 3 new probes), EXIT 0. ZERO regressions -- the type-specializer
  change did not perturb any existing test (float tests, child-11/12/13/14/15
  canaries all green).
- Legacy-neutral (plain Debug + useRegister=true, NeoStep filter): **346 ran /
  17 failded** == the documented 17-failure baseline. None of the 3 AddiOnFloat
  probes are in the failure set (all pass under Legacy -- Legacy's Addi
  re-dispatches on ObjectType).

## Files changed

- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- +30 lines (the
  `Ldind_*`/`Ldelem_*` seeding cases inside `TypeSpecializeNeoOpcodes`, Neo-only).
  No change to `Optimizer.ELDC.cs`, `Optimizer.Utils.cs`, the Neo runtime arms, or
  the object model.
- `TestCases/NeoStepAddiOnFloatTest.cs` -- new (3 probes).

## Commit

Deferred to rasen-ship (implementer mandate: do NOT commit). Source fix + probes
are in the working tree and verified. Commit trailer:
`Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`. Push needs
`git config lfs.useslockfiles false`.

## Surfaced follow-up (out of scope)

A raw `Ldfld`/`Stfld` of a CLR-struct field (the child-4 escaping shape, e.g.
`a.X = a.X*2 + 1` where Roslyn lowers the simple-assignment read via `ldfld` not
`ldind`) does NOT seed `registerTypes` either, so the same class of float-arithmetic
corruption exists on that producer path. It is a DIFFERENT site (raw Ldfld, not
Ldind/Ldelem) and seeding it is out of scope for this change (the spec pins
Ldind_*/Ldelem_* only). Candidate sibling child if it surfaces in the full smoke.
