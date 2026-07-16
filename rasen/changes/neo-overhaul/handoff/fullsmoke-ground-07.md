# Full Neo Smoke Grounding -- 2026-07-16 FRESH re-cluster (7 failing -> FIXED to 6)

## STATUS: FRESH grounding (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch).
Full smoke was **7 failed** on entry (`Ran 948 tests, 7 failded, 20 ignored, 7
todos`). This child FRESH-ran the smoke, re-clustered the CURRENT 7, deep-diagnosed
the most tractable singleton (UnitTest_StaticTest05), shipped a 3-part Neo-gated fix,
and RE-RAN the full smoke: **7 -> 6** (UnitTest_StaticTest05 now PASSES). NeoStep
**414/0** (no regression). Legacy-neutral (all changes under `#if ENABLE_NEO_MODE`).

Build this child: CLI `Debug_Neo --no-incremental -p:UseSharedCompilation=false`
(0 errors), TestCases `Debug -p:UseSharedCompilation=false` (0 errors).

## Fresh ground (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch)
- PRE-fix:  `Ran 948 tests, 7 failded, 20 ignored, 7 todos`.
- POST-fix: `Ran 948 tests, 6 failded, 20 ignored, 7 todos`.

## The CURRENT 7 (pre-fix), clustered by defect class + per-test pinned root

### Cluster A -- byref/ref struct write-back to a struct location (2 tests)
- **StructTests.StructTest6 (Structs.cs:262)** -- `Dictionary.TryGetValue(strId,
  out cube)`; cube = StructTest {object objAsset; string type} = 0 prim + 2 refs.
  Dump: `cube.type = "123"` (expected "111"; the out param was NOT written back).
  TryGetValue is the reflection fallback. The out param's dest is sized as a single
  ILTypeInstance reference; the reflection write-back stores the result ILTypeInstance's
  mStack index at the dest prim slot, but cube's 2 fields live in the REF region which
  is never touched. DEEP (the byref encoding must carry the struct's ref base so the
  reflection write-back propagates the ref slots; ground-09 Cluster A unchanged).
- **StaticTest.UnitTest_StaticTest05 (StaticTest.cs:106)** -- `ref testVal2`
  (testVal2 = static IL-struct Vector3, pure-primitive 3-field struct) to
  UnitTest_StaticTest05Sub which does `i = Vector3.Zero`. Dump: `v0=True`
  (testVal2.x != 0; write-back lost). **FIXED THIS CHILD** (see Fix below).

### Cluster B -- constrained-callvirt on a struct / generic-struct (2 tests)
- **StructTests.StructTest12 (Structs.cs:396)** -- `T ins = new T() { i = 10 }`
  where T : struct, ITestStruct (T = MyStruct2). Dump: `ins.i = 1` (expected 10).
  ROOT PINNED via JIT dump: Roslyn lowers `new T()` (generic struct T) to
  `call Activator.CreateInstance<T>()`, and ILRuntime resolves the generic param T
  to **ILTypeInstance** for the Activator call's type arg (JIT dump:
  `call.redirect r3, System.Activator::ILTypeInstance CreateInstance[ILTypeInstance]()`)
  while `constrained T` in the SAME method correctly resolves T to MyStruct2 --
  inconsistent generic-param resolution. So the Activator result is a bare
  ILTypeInstance whose mStack index (1) is stored in the struct local `ins`; the
  later get_i reads `ins`'s first 4 bytes = the mStack index -> "1". DEEP
  (generic-param resolution for Activator: `AppDomain.GetMethod` line 2236 marks
  `ContainsGenericParameter` -> invalidToken; the fallback resolves T to
  ILTypeInstance instead of the enclosing method's T=MyStruct2).
- **TestValueTypeBinding.UnitTest_10051 (TestValueTypeBinding.cs:619)** --
  `list[0].V2.x.RawValue != 999`. V2 is a property get returning Fixed64Vector2
  (struct) by value; `.x` reads a Fixed64 struct field; `.RawValue` is a property
  read on Fixed64. A constrained-callvirt property read on a nested struct field.
  DEEP.

### Cluster C -- call marshalling through a generic instance / register transition (2 tests)
- **RegisterVMTest.RegisterVMTest04 (RegisterVMTest.cs:107)** --
  `ArgumentOutOfRangeException @ List.get_Item` <- ExecuteNeo stfld.ref (Neo.cs:5253).
  Dump: `stfld.ref r0, r12, 0x00000000, 268435971(0,0)` -- stfld.ref reads a ref
  index of 268435971 (0x10000013, garbage). `viewRectEvent = action` inside
  `ILScrollRect2<T>.SetViewRect` (override in a GENERIC INSTANCE
  ILScrollRect2<ScrollItem2>, `[ILRuntimeJIT(NoJIT)]`, called virtually with >3 args
  + default `action=null`). The `action` param's frame slot holds garbage instead of
  null. DEEP (>3-arg virtual-IL-call param map / calling convention through a generic
  instance; default-value param slot not zeroed).
- **ExpTest_20.UnitTest_TestStackRegisterTransition3 (LightTester2.cs:356)** --
  `System.Exception` (test assertion). Dump: `v0=True`. TransitionTest struct
  {int A; string B; float C; TransitionTestSub D} (16 prim + 2 refs) passed BY VALUE
  to TransitionTest2.Test(TransitionTest arg); the assertion `arg.A != 1` fires --
  arg.A arrived corrupted. `[ILRuntimeJIT(NoJIT)]`. DEEP (struct-by-value param
  marshalling to an IL callee; register-transition / Move_Vt flat-copy of a mixed-
  field struct).

### Cluster D -- boxed-CLR-struct enumerator interface dispatch (1 test)
- **MyTest.Test (Test01.cs:618)** -- `InvalidCastException: String -> IEnumerator<
  KVP<int,int>>` at autogen `get_Current_0_Neo:48`. A Dictionary enumerator `this`
  is mis-marshalled (a String ends up in the `this` slot on a later loop iteration).
  Step-19 / boxed-CLR-struct enumerator interface dispatch (this-register aliasing
  across loop iterations). DEEP (Step-19 delegate/enumerator family).

## Batch + pinned root FIXED this child: UnitTest_StaticTest05

### Pinned root (3 interacting defects, all in the `ldsflda <IL-static struct
field>; ldfld <sub-field>` / `ref <IL-static struct field>` interaction)
The test does `testVal2 = Vector3.One; UnitTest_StaticTest05Sub(ref testVal2);
if (testVal2.x != 0) throw;` where the callee sets `i = Vector3.Zero`. Three
interacting Neo gaps prevented both the write-back AND the read-back:

1. **WRITE: ldsflda used ReferenceOffset for an IL-struct static field.** The
   Ldsfelda IL-static arm (`ILIntepreter.Neo.cs:5635`) discriminator was
   `ldaFt.IsPrimitive ? PrimitiveOffset : ReferenceOffset`. An IL value-type
   (struct) static field is stored INLINE in Primitives (the Ldsfeld IL-struct arm
   at `:5704-5706` CopyBlock-reads `Primitives[PrimitiveOffset]`), so the byref MUST
   carry PrimitiveOffset. The old code gave ReferenceOffset (=1 for testVal2) ->
   stobj wrote Zero to `Primitives[1]` instead of `Primitives[4]` (the real
   PrimitiveOffset). Diag-confirmed: `stobj off=1` (ReferenceOffset), but
   testVal2's bytes live at PrimitiveOffset.
2. **READ: the JIT mis-folded `ldsflda <IL-static struct>; ldfld <field>` to
   `ldfld.r4.inline` (a frame-relative read).** The inline rewrite
   (`JITCompiler.cs` `TryRewriteFieldAccessForInline`, keys on
   `registerTypes[owner] is ILType && IsValueType`) fired because the owner register
   was stale-seeded Vector3 by a PRIOR `initobj r1, Vector3` and Ldsfelda NEVER
   cleared `registerTypes[dest]`. So the byref (objIdx, off) was mis-treated as an
   in-frame struct -> `ldfld.r4.inline` read `frameBase + SrcOffset + Operand2` =
   the byref's objIdx bytes reinterpreted as a float = garbage.
3. **READ: even non-inline `Ldfld_R4` ignored the byref's base offset.** With the
   fold prevented, `Ldfld_R4` read `Primitives[Operand2]` (=0 for field x) instead
   of `Primitives[primOff + Operand2]`.

### Fix (3 parts, all Neo-gated, Legacy-neutral by construction)
1. `ILIntepreter.Neo.cs:5635` (Ldsfelda IL-static arm) -- discriminator now
   `ldaFt.IsPrimitive || (ldaFt is ILType && ldaFt.IsValueType) ? PrimitiveOffset
   : ReferenceOffset`. Mirrors the Ldsfeld IL-struct arm. (CLR-struct static fields
   keep ReferenceOffset + the F-10 flag, unchanged.)
2. `JITCompiler.cs:1087` (TypeSpecializeNeoOpcodes switch) -- added
   `case OpCodeREnum.Ldsflda:` that clears `registerTypes[op.Register1] = null`. A
   Ldsfelda dest is ALWAYS a byref (pointer into a static field), never an in-frame
   VT, so no specialization (inline fold / Brtrue_Ref / Ceq_Ref / typed arith)
   should fire on it. Safe: clearing only prevents the broken inline fold for
   ldsfelda byrefs (which never worked -- a byref read as frame bytes is always
   garbage -> no passing test relied on it).
3. `ILIntepreter.Neo.cs:7819` (new helper `NeoLdfldStaticByrefOff`) + the 10 scalar
   Ldfld arms (`:4913-4952`, I1/U1/I2/U2/I4/U4/I8/U8/R4/R8) now read
   `ins.Primitives[NeoLdfldStaticByrefOff(...)]`. The helper adds the byref's
   static-field base offset when the owner `is ILTypeStaticInstance` (the reliable
   discriminator -- a normal heap Ldfld owner is a plain ILTypeInstance; the static
   byref shape is reachable ONLY via ldsfelda, which materializes the
   StaticInstance). For a non-static owner it returns Operand2 unchanged (no
   regression to the heap path).

### Verify (truth = full-smoke number)
- **FULL SMOKE: 7 -> 6** (UnitTest_StaticTest05 PASSES; the other 6 unchanged, no
  NEW failures introduced -- FRESH no-filter re-run confirmed
  `Ran 948 tests, 6 failded`).
- **NeoStep 414/0** (no regression; the engine change is Neo-gated and the
  TypeSpecialize/ldsflda/typed-Ldfld paths are exercised by the NeoStep suite).
- **Legacy-neutral:** all 3 changes are inside `#if ENABLE_NEO_MODE`
  (ExecuteNeo is Neo-only; TypeSpecializeNeoOpcodes is gated at
  `JITCompiler.cs:961`, the Ldsfelda case at `:1087` is inside it). Legacy compiles
  none of it.

## Remaining 6 (reported honestly -- each a distinct deep singleton)
- **StructTest6** -- byref-struct REF-REGION write-back (the byref must carry the
  struct's ref base so the reflection out-param write-back propagates ref slots).
  Sibling of the FIXED UnitTest_StaticTest05 but for a 0-prim+2-ref struct (the
  ref-region half, which the pure-primitive Vector3 case did not exercise).
- **StructTest12** -- JIT generic-param resolution for `Activator.CreateInstance<T>`
  in a generic method (T resolves to ILTypeInstance instead of the enclosing T).
- **UnitTest_10051** -- constrained-callvirt property read on a nested struct field.
- **RegisterVMTest04** -- >3-arg virtual-IL-call param map through a generic
  instance (default-value `action=null` slot holds garbage).
- **UnitTest_TestStackRegisterTransition3** -- struct-by-value param marshalling to
  an IL callee (mixed-field struct corrupted at the call boundary).
- **MyTest.Test** -- boxed-CLR-struct enumerator interface dispatch (Step-19,
  this-register aliasing across loop iterations).

## Lesson
The "byref must carry the struct ref base" verdict (ground-09 Cluster A) was
DISPROVEN for the PURE-PRIMITIVE struct case (UnitTest_StaticTest05 / Vector3): the
write-back + read-back are purely primitive bytes, and the 3 gaps above (ldsfelda
offset, bad inline fold, ldfld base offset) were each a contained, verifiable fix.
The ref-region half (StructTest6) remains genuinely deep. Re-audit of a "DEEP"
verdict by struct shape (pure-primitive vs ref-bearing) can surface a tractable
subset -- the 12-for-12 lesson refined, not broken (the fix is real and verified).

## Files this child (NOT committed, LEAD commits)
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`
  (ldsflda discriminator `:5635`; 10 scalar Ldfld arms `:4913-4952`; new helper
  `NeoLdfldStaticByrefOff` `:7819`).
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`
  (TypeSpecialize `case Ldsfelda:` `:1087`).
- `rasen/changes/neo-recluster-7/` (proposal/design/tasks).
- `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-07.md` (THIS file).
