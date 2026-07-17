# Design: neo-raw-ldfld-stfld-cluster-e

## Root cause (confirmed by post-specialization JIT dumps + stash-toggle)
The typed `Ldfld_*` / `Stfld_*` heap arms (`ILIntepreter.Neo.cs:4516-4840`)
call `GetNeoILInstance(mStack, ownerSlot)` which assumes the owner slot holds
a heap `ILTypeInstance` (an mStack index). For an IN-FRAME IL value-type owner
(the struct's flat managed bytes live in the frame), the typed opcode MUST be
the `_Inline` variant (which treats the owner offset as a direct frame byte
offset). The `_Inline` rewrite is the job of
`TypeSpecializeNeoOpcodes.TryRewriteFieldAccessForInline`
(JITCompiler.cs:1601-1651), and it fires ONLY when
`registerTypes[ownerReg]` is the struct's `ILType` (`IsValueType && !IsEnum`).

Two producers of an in-frame IL-VT value have NO seeding case, so the owner
register stays unseeded, the `_Inline` rewrite does not fire, the plain heap
arm runs, `GetNeoILInstance` reads the struct's first field-bytes as an mStack
index, and throws NRE (or NIE for a CLR boxed object):

1. `Ldarga` / `Ldarga_S` -- address of a struct PARAMETER. `Ldloca` already
   propagates the source local's VT type (JITCompiler.cs:1297-1304); `Ldarga`
   does not. Symptom: `void F(Struc a){ a.a = 3; }` -> `ldarga.s r1,r0; ldc;
   stfld.i4 r1,...` stays plain (Final-Results dump confirmed for
   `ValueTest(TestStruc a)` and `tttt(Vector3 a)`).
2. `Ldfld_Value` -- a whole-IL-VT field LOAD. Its dest register holds an
   in-frame copy of the field for BOTH a heap and an in-frame owner (the
   runtime arm copies the field's primitive+ref region into the dest either
   way), but the dest is never seeded. Symptom: `a.C.x` where `a.C` is a
   Vector3 -> `ldfld.value r6,r0,...; ldfld.r4 r6,r6,...` stays plain.

This is the SAME defect class as child-16/21/23 (an unseeded in-frame-VT /
reference producer that defeats the registerTypes-driven typed-opcode
specialization). The fix follows the established pattern: add a seeding case
for each missing producer.

## Fix (Neo-gated, additive, single file JITCompiler.cs, +35 lines)
1. A `case OpCodeREnum.Ldarga: case OpCodeREnum.Ldarga_S:` block in the
   `TypeSpecializeNeoOpcodes` switch, byte-identical to the existing `Ldloca`
   case: when `registerTypes[Register2]` (the source param) is an IL value
   type, seed `registerTypes[Register1]` (the byref dest) with it. A primitive
   / ref param is not an ILType -> no seed -> no change (a `ref int` byref
   stays untyped, correct).
2. Inside the existing `Stfld_Value`/`Ldfld_Value` in-frame-owner
   discriminator block, when `op.Code == Ldfld_Value`, resolve the field's
   ILType from `Operand4` (`appdomain.GetType(op.Operand4)`; `Operand4` =
   field-type hash, stamped at body emission JITCompiler.cs:3238) and seed
   `registerTypes[op.Register1]` (the dest) when it is an IL value type.

Both seedings ONLY affect `TryRewriteFieldAccessForInline` (which acts solely
on `Ldfld_*`/`Stfld_*` consumers of that register). A byref / VT value passed
to a call, stored, or branched on does not consult this path, so the seedings
are benign for all non-field-access consumers.

## Soundness notes
- `registerTypes` is a single forward pass with no phi-merge (recurring
  gotcha). The seeded register is the straight-line top-of-stack produced
  immediately before the field-access consumer (same block), so reliable for
  these shapes. Full NeoStep smoke (398/0) is the safety net.
- `appdomain.GetType(hash)` may in principle collide, but it is the SAME
  resolution the runtime uses (`AppDomain.GetType(ip->Operand4)` at Neo.cs
  :4579), and the seed only chooses inline-vs-heap opcode selection (a
  mis-seed that flipped a genuine heap owner to inline would surface
  immediately as a test failure; none observed).

## Out of scope (reported, NOT fixed -- different roots)
- Delegate group (DelegateExtTest01/02, DelegateTest01): NIE "Owner type:
  System.Int32". The owner of `this.Value` (`ldfld.i4` on `this` in
  `DelegateExtObj.AddValue`, a CLASS) is a boxed Int32 -- a delegate
  Target/argument-marshalling bug (Step 19 delegate scope), NOT a
  field-access-encoding bug. The heap `ldfld.i4` arm is correct for a class
  instance; the delegate handed it the int arg as `this`.
- Test01.UnitTest_Generics/Generics2: `stfld.ref` NRE on a heap
  `SingletonTest` instance reached via `ldsfld` of an IL-static singleton --
  the ldsfld/box/ceq.ref singleton-init path materializes a bad owner (static
  field / box on a reference type), separate root.
- Test05.TestStructDictionary: `ldfld.i4` NRE on an owner that is a
  `callvirt.clr` return (List.get_Item) -- a CLR-call IL-instance-return
  materialization gap, separate root.
- RegisterVMTest04: `stfld.ref` ArgOOB (`List.get_Item`) on a generic-type
  owner (`ILScrollRect2<>`), separate root.

## Verify (all recorded)
- Stash-toggle (name-filter `ExpTest_10.UnitTest_100`, 12 tests): HEAD (fix
  stashed) = 3 failed (NRE); fix = 0 failed.
- Full smoke: 51 -> 48 (exactly the 3 targets dropped; ZERO new failures --
  set-diff confirmed).
- NeoStep smoke: 398/0 (no Neo regression).
- Legacy-neutral: plain Debug + useRegister=true + NeoStep = 398 ran / 18
  failed (matches documented Legacy NeoStep baseline; change is 100%
  `#if ENABLE_NEO_MODE`).
