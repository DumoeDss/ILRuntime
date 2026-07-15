# Full Neo Smoke Grounding -- 2026-07-15 FRESH re-cluster (21 failing)

## STATUS: re-cluster only; NO fix shipped this child (see "Why no fix" below)
The fresh re-cluster + per-test pinning IS complete. The tractable candidate
(initobj-on-a-reference-type-through-a-byref) was JIT-dump-pinned and a fix
ATTEMPTED but ABORTED: there is NO reliable runtime OR alias-based discriminator
between the two operand shapes (a genuine byref vs an alias-folded reference
temp); a runtime-only fix either regresses `ActivatorCreateInstanceWithArgsTest`
+ `InheritanceTest20` (when it over-derefs) or fails to fix the target
(`UnitTest_NestedGenericRefOut`) when conservative. The correct fix is a JIT-
level producer-chain byref resolution (scan back through `move`s to the ldloca
producer; if its source is a declared reference local, resolve DstOffset to that
local's offset so the existing direct-write works). That is a larger/riskier
change than this session verified cleanly. **Full smoke stays at 21** (codebase
reverted to clean HEAD; NeoStep 401/0; Legacy-neutral by construction).

## Fresh ground (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch)
`Ran 935 tests, 21 failded, 20 ignored, 7 todos` (exit 0; the prior-known graceful
Dict-NRE crash did not surface; summary emitted). NeoStep **401/0** (baseline).
Legacy (plain Debug) build VERIFIED clean earlier this session (0 errors). The 21
are the CURRENT frontier after 42 wave-2 children drove 189 -> 21.

## The CURRENT 21, clustered by PINNED root (fresh run)

Each row = exception type + the throwing JIT opcode + the Neo.cs frame + the
test-internal throwing site. "grab-bag @7098" = the test's OWN assertion fired
(reaches the CIL Throw handler @ Neo.cs:7098); each is a DISTINCT value-
corruption root, NOT a single fix.

### Cluster A -- throw@7098 grab-bag (test's own assertion), 12 tests, DISTINCT roots
- DelegateTest42 (DelegateTest.cs:662) -- delegate assertion (delegate.Target /
  dispatch wrong for an IL-instance-method delegate). Step-19.
- ExpTest_20.UnitTest_TestInline01 (LightTester2.cs:143) -- reference-arg
  aliasing: `Sub(object o){o=null;}` nullifies the caller's local (by-ref instead
  of by-value copy on a plain Call). [PINNED; same class as child-13 newobj arg
  alias, but on a plain Call -- the next-most-tractable pinned root]
- ExpTest_20.UnitTest_TestFCP (LightTester2.cs:158) -- TestVector3NoBinding ctor
  (reflection fallback, NO redirect) yields (1,0,0) instead of (1,1,0): the 2nd
  ctor arg (y) is lost. Deep (`!isNewObj` ctor path).
- ExpTest_20.UnitTest_TestStackRegisterTransition3 (LightTester2.cs:339) --
  register-transition value corruption (needs JIT dump).
- ReflectionTest25 (ReflectionTest.cs:680) -- CLR attribute reflection
  (GetCustomAttribute wrong/null).
- RefOutTest.UnitTest_NestedGenericRefOut (RefOutTest.cs:436) -- **initobj-byref
  gap (PINNED, see "Why no fix" below).**
- StaticTest.UnitTest_StaticTest05 (StaticTest.cs:106) -- `ref Vector3
  staticField` write-back lost. MULTI-BUG: the ldsflda ReferenceOffset-vs-
  PrimitiveOffset offset for an IL-struct static field is ONE bug (ldsflda uses
  ReferenceOffset; stobj writes flat bytes to Primitives[off] which needs
  PrimitiveOffset), but fixing that ALONE does NOT clear the test -- a second
  bug (read-back / static-instance persistence) remains.
- StructTests.StructTest6 (Structs.cs:262) -- `Dictionary.TryGetValue(strId,
  out StructTest cube)`: the out STRUCT (IL struct with ref fields) is NOT
  written back (cube.type stays "123"). Distinct from recluster-38's byref
  REF-field fix (CLR-binding out-STRUCT write-back).
- StructTests.StructTest12 (Structs.cs:396) -- generic struct constrained to
  ITestStruct: `new T(){i=10}` -> `ins.i` == 0 (constrained-callvirt property
  set on a generic struct param).
- Test05.TestForEach (Test05.cs:275) -- `[ILRuntimeTest(ExpectException=
  typeof(NotSupportedException))]`; ParseOne throws NSE("error") which the
  framework SHOULD honor as Pass (PASSES under Legacy, confirmed), but is
  reported Failed under Neo. ExpectException / exception-propagation-through-
  foreach-finally mismatch. GetNeoException + ILRuntimeException wrapping look
  correct in isolation -- root is murky (likely the foreach-finally re-throw
  path double-wraps or the framework's type check sees a wrapper).
- TestValueTypeBinding.UnitTest_10046 (TestValueTypeBinding.cs:470) --
  TestVector3 via delegate: `a = a + One2; a.X != 2` (delegate VT-arg marshal
  OR struct-newobj retDst gap).
- TestValueTypeBinding.UnitTest_10051 (TestValueTypeBinding.cs:619) --
  Fixed64Vector2 `.x.RawValue` constrained-callvirt property read on a nested
  struct field (F-10 handoff noted this; distinct from the `+=` ldflda path,
  which child neo-nested-ldflda-byref fixed).

### Cluster B -- autogen Neo binding stub / upstream arg marshalling, 5 tests
All route `InvokeNeoClrMethod -> autogen *_Neo stub` (or reflection fallback). The
exception comes from UPSTREAM arg marshalling feeding a bad index / a mis-typed
`this`. Distinct per test.

- GenericMethodTest11 -- `constrained.T` -> autogen `CompareTo_0_Neo` casts
  ILTypeInstance->IComparable<int> (constrained dispatch on an IL type
  implementing a CLR interface; the int field value is marshalled as `this`
  instead of the boxed int). Neo.cs:7480.
- ReflectionTest10 -- autogen `Invoke_1_Neo` -> ILRuntimeMethodInfo.Invoke ->
  CheckCLRTypes -> framework Enum.ToObject(ILRuntimeType) "Type must be a type
  provided by the runtime". Neo.cs:4212. (Same ILRuntimeType-virtual-missing
  class as child-18, but Enum.ToObject is a static framework method, not a
  virtual -- candidate fix = an Enum.ToObject Neo redirect OR an
  ILRuntimeMethodInfo.Invoke unwrap; needs a Legacy-pass comparison to pin.)
- StructTests.StructTest11 -- autogen `Add_0_Neo` (List<Anim>.Add) ->
  List.get_Item OOB (upstream item/this marshalling feeds a bad mStack index;
  Anim is a struct with a ref field). Neo.cs:4212.
- MyTest.Test -- autogen `get_Current_0_Neo` casts String->IEnumerator
  (enumerator `this` mis-marshalled; a String ends up in the `this` slot).
  Neo.cs:4267.
- Test05.TestGenericMethod2 -- autogen `ChangeType_1_Neo` ->
  Convert.DefaultToType "Invalid cast String->Int32". PINNED root =
  typeof(<generic param>) under Neo returns an ILRuntimeType whose
  ILType.TypeForCLR mis-resolves BOTH A=int and B=double to int. Neo.cs:3677.

### Cluster C -- raw ldfld.i4 / stfld.ref owner NRE/OOB (collection/generic), 2 tests
- Test05.TestStructDictionary -- ldfld.i4 NRE @ Neo.cs:4800 on `TestStruct::id`
  from a Dictionary enumerator element / `List<TestStruct>[i]` (the owner
  register holds flat bytes or a mis-resolved index; Ldfld_I4 calls
  GetNeoILInstance on it).
- RegisterVMTest04 -- stfld.ref IndexOOB @ Neo.cs:5123 on
  `ILScrollRect2<T>::viewRectEvent` (generic IL type, Action ref field).

### Cluster D -- ldlen on null reflection array, 1 test
- ReflectionTest14 -- ldlen NRE @ Neo.cs:6027. The locals show `FieldInfo[] v5 =
  null` BUT `field` non-null (ambiguous). The ldlen is likely INSIDE the
  framework `typeof(ICollection).IsAssignableFrom(property.PropertyType)` call
  operating on a null internal reflection array, NOT a clean
  ILRuntimeType.GetFields-returns-null fix. Needs a deeper reflection
  investigation.

### Cluster E -- List.get_Item IndexOOB (ret/ret-area), 1 test
- DelegateTest19 -- List.get_Item OOB @ Neo.cs:4313 (ret-vt-with-ref-fields
  branch, TestCLREnum return; returnRefCount/retRefBase mis-computed for a CLR
  enum return).

## Why no fix shipped (the initobj-byref investigation, durable)
- **The gap is REAL and JIT-dump-pinned.** `result = default(T)` on a generic T
  that is a reference type (UnitTest_NestedGenericRefOut: `ref string loc; ...
  result = default`) lowers to CIL `initobj T` on a BYREF. The optimizer's addr-
  alias folding (Optimizer.Neo.cs:52-53 / 1506-1508) EXCLUDES declared reference
  locals (`localIsRef`) from folding ("a reference local produces a genuine
  pointer"). So for a reference target the initobj's `DstOffset` points at the
  BYREF VALUE slot (the 8-byte (objIdx,off) pair), NOT the target. The initobj
  reference-type arm wrote `-1` into that byref temp -- the assignment was
  silently lost (loc stayed "fff").
- **Two operand shapes are involved and they are NOT runtime-distinguishable.**
  (1) A genuine byref to a DECLARED ref local (NestedGenericRefOut: byref
  `(-1, locOffset)` in the temp slot, e.g. `(-1, 4)` at DstOff=20). (2) An
  alias-FOLDED reference TEMP (Activator: `EqualityComparer<T>.Default.Equals(
  value, default)` -- the `default` temp is a stack temp, localIsRef=FALSE, so
  it IS folded; DstOffset resolves to the temp itself, and a null temp reads
  `(-1, 0)` -- INDISTINGUISHABLE at runtime from a frame-native byref). A
  runtime deref that fixes (1) corrupts (2) (regresses
  ActivatorCreateInstanceWithArgsTest + InheritanceTest20 -> net WORSE); a
  conservative direct-write that is safe for (2) does not fix (1).
- **A JIT marker was attempted but the discriminator was unreliable.**
  `ResolveLiveAlias(r1).Reg == r1` failed (register reuse leaves STALE entries
  in the STATIC addrAlias that ResolveLiveAlias falls back to). The per-
  instruction `liveAliasMap.ContainsKey(r1)` discriminator ALSO failed (for the
  Activator ref-temp case the liveAliasMap entry is absent at the initobj -- the
  ldloca's source register is reused/mis-marked). Confirmed empirically: both
  cases end up with the marker set (Op4=1) at runtime.
- **A JIT-only forward-walk map (refLocalByrefSource) was ALSO attempted and
  failed.** A separate map (like liveAliasMap but including LocalIsReference
  sources), maintained for ldloca/ldarga of declared ref locals + Move
  propagation, consulted by the Initobj case to re-resolve DstOffset to the
  reference local's offset. FAILED because `localIsRef[loc's register]` is
  FALSE: a process-static diagnostic (`Optimizer.NeoInitobjOptDbg`) showed, for
  the NestedGenericRefOut initobj, `initobj R20 mapKeys=2 srcIsRef=n/a
  prevIsRef(r1=4)=False`. The initobj operand is register 20 (a stack temp);
  ResolveLiveAlias(20) -> register 4 (stale reuse alias); and
  `localIsRef[4] == false` -- so the ldloca-of-a-ref-local guard never fired and
  the map was never populated for `loc`. TWO compounding unreliabilities: (a)
  the static addrAlias resolves the initobj operand to a STALE register (4) via
  register reuse, not the real source; (b) even register 4 is not marked in
  localIsRef. The reference local `loc` is NOT reliably identifiable from the
  initobj site through the existing alias/localIsRef machinery.
- **THE CORRECT FIX (durable, for a future child): JIT-time producer-chain
  byref resolution.** In the optimizer's Initobj case, scan back through `move`s
  to the ldloca/ldflda producer; if the producer's source is a declared reference
  local (localIsRef true) / a ref param / a ref field, resolve DstOffset to the
  target's frame byte offset so the EXISTING direct-write `*(int*)(frameBase +
  DstOffset) = -1` lands on the target. This is a JIT-ONLY change (no runtime
  marker, no runtime deref) and matches how the in-frame-VT initobj path already
  resolves. The scan-back-through-moves is the non-trivial part (the byref can
  flow through inlined param copies); it must also handle ldflda-of-ref-field and
  ref-param (ldarga) producers. Alternatively, make the addr-alias folding track
  reference locals for the initobj consumer specifically (risky -- changes alias
  semantics for the heap path; the escape-reconciliation pass would need to
  cover it).
- **Stash-toggle evidence (before abort):** the un-gated runtime deref fixed
  NestedGenericRefOut (isolation 1/0) but took the full smoke 21 -> 22 (regressed
  ActivatorCreateInstanceWithArgsTest + InheritanceTest20, both confirmed pass-
  on-HEAD / fail-with-fix in isolation). Net negative. Reverted.

## Recommended next-batch priority (by coverage / confidence)
1. **initobj-byref via JIT producer-chain resolution** (UnitTest_NestedGenericRefOut)
   -- the root is PINNED and the fix shape is known (JIT-only DstOffset
   resolution); the work is the move-scan + ldflda/ref-param producer coverage.
2. Cluster B typeof(generic-param) root (TestGenericMethod2) -- likely broader.
3. UnitTest_TestInline01 (reference-arg aliasing on a plain Call) -- PINNED,
   mirrors child-13's newobj fix on the plain-Call form.
4. Cluster B delegate/constrained dispatch (GenericMethodTest11, MyTest.Test) --
   Step-19 / constrained-callvirt on IL types.
5. StaticTest05 (IL-struct static ldsflda+stobj write-back) -- MULTI-BUG (the
   offset is one; a read-back/persistence bug remains).
6. Cluster C (TestStructDictionary + RegisterVMTest04) -- collection/generic
   struct field access; 2 distinct roots.
7. Cluster A StructTest6 (CLR-binding out-STRUCT write-back) -- distinct from
   recluster-38's ref-field fix.
8. The Cluster A grab-bag -- each needs its own JIT-dump triage child.
