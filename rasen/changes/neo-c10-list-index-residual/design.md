# neo-c10-list-index-residual -- design

Wave-2 C10 residual child of `neo-overhaul`. Two tests were targeted:
`StructTests.StructTest8` and `StructTests.StructTest11`. Re-audit (REAL run,
JIT-dump-confirmed) found they are TWO DISTINCT bugs, not one "autogen List
binding" root as the C11 report guessed.

## Bug 1 (StructTest8) -- FIXED + VERIFIED

### Reproducer
`TestCases/Structs.cs:283`:
```csharp
Vector3 vec = new Vector3(1,1,1);          // TestCases.Vector3, an IL struct
Vector3 vec2 = vec.TestReturnThis(2);       // IL method returns `this` by value
Console.WriteLine($"vec2.x ={vec2.x}");     // <-- throws (line 288)
if (vec2.x != 3) throw new Exception();
```
Throws `System.ArgumentOutOfRangeException: Index was out of range ... (Parameter
'index')`, stack `List`1.get_Item -> ExecuteNeo Ldfld_R4 arm (ILIntepreter.Neo.cs
:4526) -> `GetNeoILInstance`. (The `List.get_Item` frame is the `AutoList` (= a
`List<object>`) backing `mStack` being indexed OOB.)

### Root cause (JIT-dump-confirmed)
`vec2` is an IL-struct LOCAL assigned from a method return-by-value. The Neo JIT
INLINES `TestReturnThis`; the inlined body produces the result via an
`initobj r5, Vector3` / `ldobj r5, r3` temp, then `move r3, r5; move r1, r3` into
the `vec2` local (r1). The field read `vec2.x` is `ldfld.r4 r4, r1`.

The inline rewrite (`TryRewriteFieldAccessForInline`, JITCompiler.cs:1566) rewrites
`Ldfld_R4` -> `Ldfld_R4_Inline` ONLY when `registerTypes[ownerReg]` is an in-frame
IL value type. Inside the inlined method body the field accesses DID inline
(`ldfld.r4.inline`/`stfld.r4.inline`) because the owner was seeded by `ldloca`. But
at the main-body `ldfld vec2.x`, `registerTypes[r1]` was `null`:

- `BuildInitialRegisterTypes` seeds `vec2` (r1) = Vector3 (correctly).
- BUT the chain `initobj r5`/`ldobj r5` -> `move r3, r5` -> `move r1, r3` propagates
  `registerTypes` via the `Move` case (JITCompiler.cs:970), which does
  `SetRegisterType(registerTypes, dest, srcType)`. Since neither `Initobj` nor
  `Ldobj` had a seeding case in `TypeSpecializeNeoOpcodes`, the temp `r5` stayed
  untyped (null); `Move` then propagated null into r3 and into r1, CLOBBERING r1's
  declared Vector3 type.
- So `ldfld.r4 r4, r1` was NOT rewritten to inline. The runtime `Ldfld_R4` arm
  (`ILIntepreter.Neo.cs:4525`) does `GetNeoILInstance(mStack, *(int*)(frameBase +
  ip->SrcOffset))` -- it treats the owner slot as a heap mStack index. For the
  flat-bytes struct, that slot holds the first field's bits (the float `3.0f` =
  `0x40400000` = 1078263808), a huge OOB index into `mStack` (the `AutoList`) ->
  `List.get_Item` OOB.

### Fix (JIT time, Neo-only, additive)
Add a producer-seeding case for `Initobj`/`Ldobj` in `TypeSpecializeNeoOpcodes`
(`JITCompiler.cs`, after the `Ldc_R8` case). Both opcodes carry the type-token hash
in `op.Operand` (stamped at JIT Translate: `Initobj` JITCompiler.cs:2917,
`Ldobj` :3180). Seed `registerTypes[op.Register1] = appdomain.GetType(op.Operand)`
when non-null. This is the SAME producer-seeding pattern as child-16 (`Ldind_*`/
`Ldelem_*`) / child-21 (`Call` primitive return / raw `Ldfld`) / child-23
(`Ldfld_Ref`): every primitive/VT producer must seed its dest so downstream typed
specialization fires. A non-ILType result (CLR struct / primitive) does not trigger
the inline rewrite, so the seed is harmless.

After the fix, the chain seeds `r5 = Vector3`, propagates through the moves, and
`ldfld vec2.x` becomes `ldfld.r4.inline` (verified in the JIT dump) -- the inline
arm reads the field from the flat bytes directly. StructTest8 PASSes.

Files: `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` (one additive
case-block, ~20 lines, inside `#if ENABLE_NEO_MODE`). No runtime change, no
object-model change, no binding change.

## Bug 2 (StructTest11) -- NOT FIXED (separate, deeper)

A DIFFERENT bug: an IL-struct call arg (`new Anim(...)`, flat bytes under the Neo
VT-THIS-ADDR newobj) passed to a CLR `List<ILTypeInstance>.Add(ILTypeInstance)`
reference param is NOT boxed at the call boundary -> `ReadNeoReference` misreads the
struct's float bits as an mStack index -> OOB.

The first fix attempt was UNSOUND and was reverted. The root cause, the box
mechanism to reuse, the eliminated hypotheses, and the recommended fix path are all
documented in `handoff/worker-1.md`. The key blocker: the per-register
`registerTypes` array is a single LINEAR pass with NO phi-merge at loop back-edges,
so a reused eval-stack register's FINAL type is not its type at a specific call site
-- a `frame.NeoRegisterTypes`-based detection in `LowerNeoOffsets` cannot work. The
detection MUST run inside `TypeSpecializeNeoOpcodes` (where the type IS correct at
each instruction) and be correlated to the per-call `NeoCallParamMap` via an
encounter-order queue or a stamp on the call op.

## Verify (truth = full-smoke count, REAL run)
- Name-filter: StructTest8 PASS after the fix (FAIL on the 70-baseline); StructTest11
  still FAILS (Bug 2, unfixed).
- **FULL SMOKE delta: 70 -> 69** (StructTest8 flipped green; the one expected flip).
  Two independent full Neo smokes (both Bug 1 only) produce the IDENTICAL 69-failure
  set -- no regressions, no flakes.
- NeoStep **394/0** (no regression; matches the documented ~394/0 baseline).
- Legacy-neutral: StructTest8 PASSES under plain `Debug` + `useRegister=true`
  (Legacy). Structural too: the edit is inside `#if ENABLE_NEO_MODE`.

## Out of scope
- Bug 2 (StructTest11) -- see `handoff/worker-1.md`. Likely siblings in the same
  `Parameter 'index'` OOB class (re-audit after a Bug 2 fix): CLRBindingTest08,
  DelegateTest19, RegisterVMTest04, HotfixBasicTestCases.Test04.
