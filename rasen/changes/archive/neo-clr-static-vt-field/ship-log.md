# Ship Log — neo-clr-static-vt-field

**Status:** IMPLEMENTED + VERIFIED by the implementer (autonomous LEAD run, Tier A).
**Commit / push / archive:** DEFERRED to the shipper/LEAD (implementer constraint: "Do NOT commit").

## What shipped

Relaxed the child-3 `NeoClrVtStaticFieldIsUnsafe` guard so a blittable CLR
value-type STATIC field whose type has a registered `ValueTypeBinder` (the
`TestVector3.One` shape) reads/writes through the existing flat-byte box-roundtrip
instead of NIE-ing.

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`:
  `NeoClrVtStaticFieldIsUnsafe(Type ft, int slotSize, bool hasBinder)` ->
  `(Type ft, int slotSize)`. DELETED the `if (hasBinder) return true;` clause
  (and its rationale comment). KEPT byte-for-byte the two sound rejections:
  (a) `NeoClrStructHasRefFields(ft)` (recursive; ref-field CLR structs stay
  refused), (b) the slot-overflow clause `GetNeoValueTypeManagedSize(ft) >
  slotSize` (the REAL AccessViolation guard). Rewrote the helper's header
  comment: a registered binder is IRRELEVANT to the flat-byte path
  (`ReadNeoValueType`/`WriteNeoValueType` are pure
  `Unsafe.ReadUnaligned`/`WriteUnaligned`; there is no Neo `byte*` binder API --
  the binder only exposes Legacy `StackObject*` marshalling). Dropped the
  `hasBinder` parameter + the two `AppDomain.ValueTypeBinders.ContainsKey(ft)`
  computations at the Stsfld/Ldsfld CLR-static call sites. Net runtime diff ~5
  lines (1 clause + 1 comment block removed, 2 call-site lines trimmed, comment
  rewritten). The flat-byte box-roundtrip BEHIND the guard is unchanged (already
  correct).
- `ILRuntimeTestBase/TestFramework/TestClass3.cs`: added
  `public static TestVector3 NeoClrVtStaticProbe;` (writable, default-init, does
  NOT stomp `TestVector3.One`) + `TestCLRBinding.HostReadNeoClrVtStaticProbe()`
  returning `(int)(X+Y+Z)` (plain host CLR read isolating a broken Stsfld WRITE
  from a broken Ldsfld READ -- the child-3 TC1 pattern).
- `TestCases/NeoStepClrVtStaticFieldTest.cs` (new): TC1
  (`NeoStepClrVtStatic_TC1_LdsfldBinderVtRead` -- ldsfld TestVector3.One read,
  assert sum==6) + TC2 (`NeoStepClrVtStatic_TC2_StsfldLdsfldBinderVtRoundTrip` --
  stsfld write + host read + ldsfld read-back, assert h==3 && s==6). Both
  FAULT-to-fail (1/0 on wrong value); names embed "NeoStep".

## Root cause (the over-cautious guard, DISPROVEN)

The child-3 worker observed a binder-struct AccessViolation and (over-
conservatively) refused ALL binder structs at the Stsfld/Ldsfld CLR-static VT
branch. That rejection is unsound: `ReadNeoValueType`/`WriteNeoValueType` are
PURE FLAT-BYTE copies (`Unsafe.ReadUnaligned<T>`/`WriteUnaligned<T>` via cached
DynamicMethod delegates, sized by `GetNeoValueTypeManagedSize`=`Unsafe.SizeOf<T>`)
and do NOT consult the `ValueTypeBinder` -- there is NO Neo `byte*` binder API
(the binder only exposes Legacy `StackObject*` + `IList<object>` marshalling). So
a blittable binder struct (TestVector3 = 3 floats = 12 bytes) has the same
flat-byte representation with or without a binder. The child-3 "VT-binder AV" was
the SLOT-OVERFLOW case (a struct that did not fit the dest eval temp), which the
guard's THIRD clause already catches INDEPENDENTLY -- the binder clause was a
false correlation, not a sound check. Evidence the flat-byte path already works
for binder structs: TestVector3 is marshaled through it for by-value params/
returns in the live NeoStep smoke (`SumTestVector3Fields`), and child-4's raw
Ldfld/Stfld CLR-VT-OWNER handlers use the identical box-roundtrip with NO binder
guard and are green.

## Stash-toggle evidence (FAULT-on-HEAD -> PASS-after)

1. With fix: TC1+TC2 by filter => **Ran 2, 0 failed**.
2. `git stash push -- ILIntepreter.Neo.cs` (revert ONLY the guard fix; keep
   probes/infra), rebuild CLI. TC1+TC2 by filter => **Ran 2, 2 failed**, BOTH with
   the tagged NIE: `"Neo Ldsfld: CLR static value-type field One of type
   ILRuntimeTest.TestFramework.TestVector3 not supported under Neo (Step-13b
   ref-field/binder gap or slot-size overflow)"`.
3. `git stash pop` (restored clean; guard back to the 2-clause form), rebuild.
   TC1+TC2 by filter => **Ran 2, 0 failed**.

## Deltas

- NeoStep smoke: **324/0 -> 326/0** (+ TC1 + TC2; zero regressions across
  NeoStep12/13/13b/17/clr-static/raw-stfld-ldfld/misc-opcodes).
- Full (un-filtered) Neo smoke, `grep "CLR static value-type field One"`
  (distinct throws, excl. rethrows): **11 -> 1**. The remaining 1 is
  `ArrayTest.ArrayTest05` (`ldsfld TestVector3.One` into an undersized
  `stelem.any` temp `r4`) -- it NIEs via the KEPT slot-overflow clause (the real
  AV guard), NOT a binder NIE. This is the desired "slot-overflow stays guarded"
  outcome: the planner's 11->0 assumed every dest is sized to the struct; the
  array-element-store temp is not, so it is correctly refused with a clean NIE
  (preventing an OOB write / AccessViolation) rather than crashing.
- Net FATAL crashes in the full smoke: **1 before, 1 after** (the full smoke
  always crashes once pre-completion -- pre-existing; the crash POINT shifted to a
  newly-reachable method, but the count is unchanged). NOT more than baseline.

## SURFACED FOLLOW-UP (out of scope; for the LEAD/portfolio)

Now that `ldsfld TestVector3.One` succeeds, a full-smoke method that does
`ldsfld TestVector3.One` + `ldloca` + `ldflda` + `ldind.r4`/`stind.r4` (byref
field-mutate on a CLR struct local sourced from the static) reaches an
`AccessViolationException` in the `ldloca`/`ldflda`/`ldind`/`stind` path in
`ExecuteNeo` (around `.tmp-clrvt-full-after.log:126842`). This is a PRE-EXISTING
LATENT bug in a DIFFERENT opcode site (byref field access on a CLR struct local),
unmasked because ldsfld no longer NIEs short-circuit -- NOT a flaw in this guard
relaxation (the NeoStep smoke is fully green; the ldsfld/Stsfld paths are
correct). Candidate follow-up: the byref-CLR-struct-field
`ldflda`+`ldind`/`stind` path under Neo.

## F1-overlap verdict: KEEP `neo-clr-vt-refcount-stobjldobj` SEPARATE

The `Stobj`/`Ldobj` arms compute `refCount = ilType != null ? ilType.TotalReferenceCount : 0`
(ILIntepreter.Neo.cs:5311 Stobj / :5414 Ldobj); for a CLR struct `ilType` is null
so `refCount=0` and the ref-region copy loop is silently skipped -> a ref-field
CLR struct copied via stobj/ldobj has untracked GC refs (latent missed-GC-root).
This is the SAME mechanism class (flat-byte path cannot track GC refs for a
ref-field CLR struct) but a DIFFERENT opcode site (`Stobj`/`Ldobj` vs
`Stsfld`/`Ldsfld`), and NOT low-risk to fold in (needs binder-aware ref-region
marshalling or a new guard; touches the Step-17 `Move_Vt`/stobj-refloop surface ->
regression risk). Kept as a separate follow-up child.

## Legacy-neutral

`ILIntepreter.Neo.cs` is wholly `#if ENABLE_NEO_MODE`-gated; the change compiles
out under plain `Debug`. The infra additions (`TestClass3.NeoClrVtStaticProbe` +
`HostReadNeoClrVtStaticProbe`) are plain C#, semantically inert for Legacy.
Spot check: plain `Debug` + `useRegister=true` + `NeoStep` filter =>
**326 ran / 17 failed** (the 17 is the pre-existing Legacy failure set --
NeoStep13/14 etc.; TC1/TC2 are NOT among them; both pass under Legacy). Matches
the child-6 320/17 baseline + child-7 + these 2 probes.

## Files changed

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (guard + 2 call sites)
- `ILRuntimeTestBase/TestFramework/TestClass3.cs` (infra)
- `TestCases/NeoStepClrVtStaticFieldTest.cs` (new probes)
