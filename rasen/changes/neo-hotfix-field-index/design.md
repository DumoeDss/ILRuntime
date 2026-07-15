# Design -- neo-hotfix-field-index (wave-2 cluster H)

## 1. The question (from the LEAD)
Cluster H = HotfixBasicTestCases.Test04/Test05, two tests sharing ONE mechanism
(Neo-eval-stack bridge field-index). They were the 2 "DEEP (residual)" left by the
prior child `neo-hotfix-patched-il-execute` (which implemented the
PushToStack/AssignFromStack Neo bridge and fixed the OTHER 4 cluster-H tests). The
mandate: verify at 23 fails, re-audit whether the residuals are still the same DEEP
roots or have progressed to a tractable point, fix the tractable, report the deep.

## 2. RE-AUDIT (verified at 23, fresh run on HEAD a280ab7c)
- Build CLI Debug_Neo (0 errors, 12.67s real) + TestCases Debug (0 errors). HEAD clean.
- Name-filter on HEAD:
  - Test04 -> `TypeLoadException: Neo AssignFromStack: field index 1 out of range for
    HotfixAOT.HotfixClass___Extra (TotalFieldCount=1) and no CLR base adaptor`
    @ ILTypeInstance.cs:1177, via ILIntepreter.cs:2558 (eval-stack Stsfld ILType branch).
  - Test05 -> `TypeLoadException: Neo PushToStack: field index 0 out of range for
    ILRuntime.Hybrid.<PrivateImplementationDetails>_vFek... (TotalFieldCount=0) and no
    CLR base adaptor` @ ILTypeInstance.cs:918, via ILIntepreter.cs:2637 (eval-stack
    Ldtoken field case 0).
  - Same roots as the ground-24 -- NO progression, but ALSO NOT a changed root.
- Legacy (plain Debug) "HotfixBasicTestCases.Test0" -> 9 ran / 0 failed (Test04+05 PASS).

## 3. ROOT CAUSE (one shared mechanism, BOTH tests)
The Neo PushToStack/AssignFromStack bridge (in the `#else`/ENABLE_NEO_MODE block of
ILTypeInstance.cs) used INSTANCE-field accessors UNCONDITIONALLY:
  `if (fieldIdx < type.TotalFieldCount && ...)` (instance count)
  `off = type.GetFieldOffset(fieldIdx)`           (instance offset)
  `ft  = type.GetField(fieldIdx, out _)`          (instance type)

But the eval-stack interpreter's ILType branches for Stsfld/Ldsfld/Ldtoken
(ILIntepreter.cs:2558/2589/2637) call `t.StaticInstance.PushToStack/AssignFromStack
(fieldIdx)`. There `this` is an ILTypeStaticInstance and `fieldIdx` is a STATIC-field
index (resolved by HybridPatch GetFieldIndex -> ILType.GetField(name) ->
staticFieldMapping[name] = the 0-based static index, IDENTICAL to the Legacy arm).

So the bridge bounds-checked a static index against the INSTANCE count and resolved a
static index through the INSTANCE offset/type accessors:
- Test04: `___Extra` has TotalFieldCount=1 (only the instance field IntFieldAdded) but 2
  static fields (FloatFieldAdded, BoolFieldAdded). stsfld FloatFieldAdded -> fieldIdx=1
  (a valid static index) fails `1 < TotalFieldCount(1)`.
- Test05: `<PrivateImplementationDetails>` is a `.size N` blob struct with
  TotalFieldCount=0. ldtoken blob-field -> fieldIdx=0 fails `0 < 0`. PLUS the blob's
  declared type computes TPS=0/TRC=0 -> the Neo static instance's ManagedObjects is null
  -> the byte[] blob was never materialised (the child-6 finding, resurfacing on the
  eval-stack Ldtoken path that child-6 did NOT touch).

Legacy passes because its ILTypeStaticInstance ctor sizes `fields = new
StackObject[StaticFieldTypes.Length]` and PushToStack/AssignFromStack bounds-check
`fields.Length` (the static count) and index `managedObjs[fieldIdx]` directly -- so a
static fieldIdx is always in range and the blob byte[] is stored (the Legacy InitialValue
replay loop stores it at managedObjs[idx]).

## 4. THE FIX (Neo-gated, Legacy-neutral, ~44 added / 7 removed, ONE file)
Branch the two bridge methods on `bool isStatic = this is ILTypeStaticInstance;`:
- `fieldCount = isStatic ? type.StaticFieldTypes.Length : type.TotalFieldCount;`
- `off = isStatic ? type.GetStaticFieldOffset(fieldIdx) : type.GetFieldOffset(fieldIdx);`
- `ft  = isStatic ? type.StaticFieldTypes[fieldIdx] : type.GetField(fieldIdx, out _);`

This mirrors the PROVEN ExecuteNeo ldtoken field arm (ILIntepreter.Neo.cs:1970-1973:
`sIdx=(int)OperandLong; off=ilt.GetStaticFieldOffset(sIdx); ft=ilt.StaticFieldTypes[sIdx]`)
-- the fieldIdx encoding is byte-identical (HybridPatch Ldtoken case 0 stamps the same
`((typeHash<<32)|fieldIndex)` token the Neo JIT consumes).

The `is ILTypeStaticInstance` discriminator is universally correct (NOT just for these 2
call sites): StaticInstance is ONLY ever reached by the eval-stack Stsfld/Ldsfld/Ldtoken
arms (which always carry a static-field token); a regular ILTypeInstance is ONLY reached
by Ldfld/Stfld (instance-field token). ILEnumTypeInstance is neither -> correctly takes
the instance branch.

### 4.1 The blob (Test05) -- child-6 lineage
For a static field whose Cecil FieldDefinition has a non-empty InitialValue (the
PrivateImplementationDetails blob), surface the byte[] directly from Cecil
(`type.StaticFieldDefinitions[fieldIdx].InitialValue`) BEFORE the category chain, so the
downstream RuntimeHelpers.InitializeArray redirect (the child-6 Neo redirect) can bulk-
copy it. This mirrors both Legacy's static-instance InitialValue replay and the ExecuteNeo
ldtoken arm's initBlob first-check. Static-only (instance fields never carry a Cecil
InitialValue blob on this eval-stack path). The blob path is read-only (ldtoken) so
AssignFromStack needs NO InitialValue handling on the write side.

The 3 static accessor arrays (StaticFieldTypes / StaticFieldDefinitions /
staticFieldOffsets via GetStaticFieldOffset) are all populated in lockstep by the SAME
`idxStatic` counter in ILType.InitializeFields (ILType.cs:2972/2985 + the post-loop
offset pass), so they are guaranteed parallel -- safe to index by the same fieldIdx.

## 5. VERIFY (truth = full-smoke number)
- Name-filter, stash-toggle (git stash ILTypeInstance.cs ONLY -> rebuild -> run ->
  pop -> rebuild):
  - Test04 + Test05: 1 failed each on HEAD (the TypeLoadException above) -> 0 failed
    with the fix -> stash -> 1 failed each -> pop -> 0 failed. Airtight.
- FULL Neo smoke: **23 -> 21** (`Ran 935 tests, 21 failded, 20 ignored, 7 todos`; exit
  127 = known graceful Dict-NRE crash, summary emitted). The 21 are a STRICT SUBSET of
  the 23: every surviving test is in the ground-24 list; the only 2 removed are exactly
  HotfixBasicTestCases.Test04 + Test05. ZERO new failures.
- NeoStep smoke: **401/0** (no regression; the bridge is reached ONLY by the patched-IL
  eval-stack path -- normal Neo methods run via ExecuteNeo which never calls it).
- Legacy-neutral: plain Debug build 0 errors (the change is entirely inside the
  `#else`/ENABLE_NEO_MODE block); Legacy Hotfix 9/0 (Test04/05 still PASS -- byte-
  identical Legacy behavior).

## 6. DURABLE findings
1. **The "DEEP (residual)" verdict from neo-hotfix-patched-il-execute was DISPROVEN
   (12-for-12 lesson holds).** Both Test04 and Test05 are the SAME shared mechanism --
   the Neo bridge used instance accessors for a static fieldIdx -- NOT two distinct deep
   roots. A 2-line-per-method discriminator (`is ILTypeStaticInstance`) + the child-6
   blob first-check clears both. The prior child's framing ("eval-stack Stsfld/Ldsfeld
   ILType branch doesn't consult the SetStaticFieldCallback") was a mis-diagnosis: the
   callback / 0x10000000 bit is IRRELEVANT -- the added static fields resolve to a PLAIN
   static-field index on the `___Extra` ILType, and the bridge just needed to use the
   static accessor space. Re-audit vindicated.
2. **Any future "ILTypeInstance.PushToStack/AssignFromStack field index N out of range"
   on the Neo-eval-stack bridge: check `is ILTypeStaticInstance` FIRST.** Instance vs
   static fieldIdx live in DIFFERENT index spaces; the bridge must dispatch on the
   storage type. The static accessor triplet (StaticFieldTypes / GetStaticFieldOffset /
   StaticFieldDefinitions) is the correct parallel-arrays source (all keyed by the same
   idxStatic).
3. **The `.size N` blob TPS=0/TRC=0 gotcha (child-6) recurs on EVERY code path that
   reads a PrivateImplementationDetails static field**, not just ExecuteNeo ldtoken. The
   fix is uniform: source the byte[] from Cecil FieldDefinition.InitialValue. A future
   path touching blob statics should mirror the initBlob first-check.
4. **The blob fix here is the eval-stack counterpart of child-6's ExecuteNeo ldtoken
   fix.** Together they close the array-initializer intrinsic (`new T[]{many}`) for BOTH
   interpreter paths under Neo (ExecuteNeo + the patched-IL eval-stack Execute).
5. **SCOPE is intentionally narrow:** the bridge still NIEs on an IL-VALUE-TYPE field
   (instance OR static) reconstruction -- NOT reached by any current failing test, and
   correctly deferred (it would need the full box/reconstruct dance). CopyToRegister /
   AssignFromStack(all) / CopyValueTypeToStack / InitializeField remain empty no-ops
   under Neo (not reached by the patched-IL shapes).
