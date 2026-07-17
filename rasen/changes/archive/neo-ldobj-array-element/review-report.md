# Review Report: neo-ldobj-array-element (child 26 of neo-overhaul)

Reviewer: independent (REVIEWER role, not the implementer).
Branch: `features/object-model-overhaul`. Mode: Neo (`ENABLE_NEO_MODE`).
Date: 2026-07-13.

## Scope reviewed (uncommitted working tree)

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (+38): Stobj (WRITE-back) Array
  branch at `:5681-5695`; Ldobj (READ) Array branch at `:5794-5816`.
- `ILRuntimeTestBase/TestFramework/TestVector3.cs` (+8): `NeoArrElemIntProbe.One` + int `operator +`
  at `:413-420`.
- `TestCases/NeoStepLdobjArrayElementTest.cs` (new): TC1 (`arr[0] += One` -> 302), TC2 (two indices
  -> 307), host read-back via `NeoArrElemFieldSum`.

## Verification performed (all by this reviewer, re-run live)

1. Builds clean: `ILRuntimeTestCLI` `-c Debug_Neo --no-incremental` -> 0 errors; `TestCases -c
   Debug` -> 0 errors. (Build-server killed + `UseSharedCompilation=false` per child-25 gotcha.)
2. NeoStep smoke (with fix): **373 ran / 0 failed** (371 baseline + TC1 + TC2). No regressions.
3. Stash-toggle (stashed `ILIntepreter.Neo.cs` ONLY, kept probe + int operator+): the 2 probes ->
   **2 ran / 2 FAILED** with the exact HEAD NIE
   `Step 17/13b: field/element access on a CLR object via the IL-instance path is deferred ...
   Owner type: ILRuntimeTest.TestFramework.NeoArrElemIntProbe[]` at `ILIntepreter.Neo.cs:5781`
   (the `GetNeoILInstance` fallback in the Ldobj arm). `git stash pop` restored the +38-line fix.
   Airtight FAIL->PASS.
4. Code-level reads confirmed every structural claim (branch order, decode offsets, helper
   signatures, ldelema encode, NeoIsClrObject exclusion). Details below.

## Findings by severity

### Blocker
(none)

### Major
(none)

### Minor

**M1. TC2's combined-sum assertion is invariant to element targeting -- it does NOT genuinely
prove element-index decode.** [This-PR, probe strength]

TC2 asserts `NeoArrElemFieldSum(arr,0) + NeoArrElemFieldSum(arr,1) == 307`. The `+= One` operation
adds (1,1) to the targeted element, i.e. +2 to that element's field-sum. With `arr[0]=(100,200)`
(sum 300) and `arr[1]=(1,2)` (sum 3), two `+=` operations add +2 each = +4 to the *combined* sum
regardless of which element each one targets:

- Correct decode: `arr[0]->(101,201)` (302), `arr[1]->(2,3)` (5); combined = 307.
- Hypothetical silent "always-element-0" decode bug: both `+=` hit `arr[0]` ->
  `arr[0]=(102,202)` (304), `arr[1]` unchanged (3); combined = 304 + 3 = **307**.

Both yield 307, so TC2 cannot distinguish a correct element-index decode from a silent
always-element-0 mis-target. TC2's *stated* purpose ("a wrong element-index decode yields a
different combined sum") is therefore not achieved.

Mitigations that keep this at Minor (not higher):
- The actual code is verified CORRECT by direct reading: `off = *(int*)(frameBase + ip->SrcOffset +
  4)` (Ldobj `:5752`) / `+ ip->DstOffset + 4` (Stobj `:5634`) reads the byref's SECOND int, which
  `Ldelema` encodes as `elementIdx` (`:5913`). The realistic decode bugs (slot swap, wrong `+0` vs
  `+4`) crash or NIE, which the stash-toggle and the green run rule out.
- TC1 fully proves the ldobj-read + stobj-write round-trip at `elementIdx=0` (host read-back of a
  mutated element).
- TC2 still has *some* marginal value: it exercises a second `ldelema` producing `elementIdx=1`
  and runs ldobj/stobj over it, so a crash/NIE on index 1 IS caught.

Recommend (route to implementer, non-blocking): if element-index-decode coverage is the goal,
assert a PER-ELEMENT sum that distinguishes correct from always-0 -- e.g. add
`NeoArrElemFieldSum(arr, 1) == 5` (an always-0 decode leaves `arr[1]` at sum 3, failing). The
constants 302 and 307 are themselves arithmetically reachable from the inputs (child-24 lesson
applied): 101+201=302; 302+(2+3)=307.

### Trivial

**T1. Dead defensive `InitBlock`-on-null arm in Ldobj (`:5814-5815`).** [This-PR, intentional]
For a value-type array, `Array.GetValue(i)` returns a *non-null* boxed VT (a boxed zero VT is
still non-null), so the `else Unsafe.InitBlock(...)` zero-fill is unreachable on the VT path.
Harmless and consistent with the identical pattern in the F-10 sub-case (`:5830-5831`); explicitly
acknowledged in `design.md`. No action needed.

**T2. Local-name style drift in the Stobj Array branch.** [This-PR, cosmetic]
The Stobj Array branch uses prefixed locals `stobjSrcCur`/`stobjBoxed` (`:5692-5693`) while the
sibling `NeoIsClrObject` arm just above uses unprefixed `srcCur`/`boxed` (`:5677-5678`). There is
no scope collision (separate `else if` blocks); the prefixing is cosmetic/for grep-ability. No
action needed.

## Dimension-by-dimension verdict

**1 & 5. The runtime-detection-vs-marker verdict (the crux) -- SOUND.** Verified:
- (a) The `objIdx == -1` (frame-native) branch is the FIRST branch in BOTH arms -- Stobj `:5639`,
  Ldobj `:5757` -- dispatched BEFORE the `NeoIsClrObject` check and the new `Array` check. So when
  the `Array` check runs, `objIdx >= 0` is guaranteed and `mStack[objIdx]` is the byref's referent.
- (b) There is NO ldobj/stobj shape where the operand is NOT a byref. CIL `ldobj`/`stobj` (ECMA-335
  III.4.26 / III.4.29) copy a value type to/from an ADDRESS; the operand is inherently a managed
  pointer, never a direct value. This is the key semantic difference from `ldfld` (child-24), which
  IS polymorphic over value-vs-address and whose owner register could therefore hold flat bytes
  whose first int was a struct field value -- the ambiguity that forced child-24's JIT marker.
- (c) `NeoIsClrObject` (`:6527-6535`) returns `!(o is ILTypeInstance) && !(o is Array)` -- i.e. it
  explicitly returns FALSE for an Array, so an Array owner correctly falls through to the new
  branch; a CLR-object owner is dispatched earlier; an IL-instance owner is neither Array nor a CLR
  object and falls to `GetNeoILInstance`. No collision window exists. `Ldelema` itself rejects a
  non-value-type element with a tagged NIE (`:5909-5911`), so a reference-type-element array byref
  is unreachable at ldobj/stobj. The child-24 marker (`NeoRawLdfldArrayElementByRefMarker`,
  confirmed present in `JITCompiler.cs` + `ILIntepreter.Neo.cs` + the child-24 test) is genuinely
  unneeded here. This is NOT a latent silent-corruption risk.

**2. Both-branches correctness -- CORRECT.**
- Ldobj (READ): decodes the byref from `ip->SrcOffset` (matches `objIdx = *(int*)(frameBase +
  ip->SrcOffset + 0)` at `:5751`); `off` (second int, `:5752`) is the element index per the
  `Ldelema` encode (`:5913`). `Array.GetValue(off)` boxes the VT element; `WriteNeoValueType` (sig
  `(object, byte*, int)` at `:258`) flattens it into `frameBase + ip->DstOffset`. Box/unbox is the
  standard reflection round-trip.
- Stobj (WRITE): decodes the byref from `ip->DstOffset` (the WRITE target; matches `objIdx =
  *(int*)(frameBase + ip->DstOffset + 0)` at `:5633`); `off` (`:5634`) is the element index.
  `ReadNeoValueType(t.TypeForCLR, frameBase, ref stobjSrcCur, primSize)` (sig `(Type, byte*, ref
  int, int)` at `:242`) boxes the src flat bytes at `ip->SrcOffset`; `Array.SetValue(boxed, off)`
  stores them. This is the precise mirror of Ldobj. Decode offsets are correct in each arm
  (SrcOffset for Ldobj, DstOffset for Stobj).
- Both `ReadNeoValueType`/`WriteNeoValueType` use per-type cached reader/writer tables
  (`s_neoVtReaders`/`s_neoVtWriters`, `:249`/`:263`) built by `CreateNeoVtReader`/`Writer` -- pure
  flat-byte `Unsafe.ReadUnaligned`/`WriteUnaligned`, so a custom blittable CLR struct like
  `NeoArrElemIntProbe { int A; int B; }` is handled generically (not a hardcoded-primitive set).

**3. No-overfire / branch placement -- CORRECT.** The branches sit between `NeoIsClrObject` and
`GetNeoILInstance` in both arms. `mStack[objIdx] is Array` is true ONLY for an array referent; a
CLR-object-field byref is dispatched by `NeoIsClrObject` first; an IL-instance-field byref is not
an Array and falls to `GetNeoILInstance`; a frame-native byref is dispatched by the `objIdx == -1`
branch. The Array branch is reached ONLY for a `Ldelema`-produced byref to a value-type array
element. No misfire vector.

**4. Regression surface -- CLEAN.** Full NeoStep smoke is 373/0. The sibling families cited
(child-19 Stfld-array, child-24 Ldfld-array, child-25 byref-marshal, child-15 ldind/stind,
NeoStep12/13/13b/17 VT-copy) are all within the 373 and stay green. The broad ldobj/stobj VT-copy
surface (frame-native, CLR-object-field, IL-instance, F-10) is unchanged -- the new branch is a
pure `else if` insertion that only triggers for an Array owner, which previously NIE'd.

**6. Probe strength + read-back -- ADEQUATE (with M1 caveat).** The host read-back
(`NeoArrElemFieldSum(arr, i)` at `TestClass3.cs:198-201`) takes the ARRAY + index, runs natively on
the host (CLR method), and reads `arr[i].A + arr[i].B` straight from the array's backing storage.
It does NOT depend on the broken `ldelem.any` path. Because `stobj` mutates the shared managed
array via `Array.SetValue`, the host is a genuine independent witness to the ldobj-read +
stobj-write round-trip. `BuildNeoArrElemProbeArray` (`TestClass3.cs:207-214`) seeds the array
host-side (no IL constructor/Stfld). 302 (101+201) and 307 (302+5) are arithmetically reachable.
The int probe (`NeoArrElemIntProbe`) is a valid proof of the ldobj/stobj arms: the CIL lowering
`ldelema; ldobj; op_Addition; stobj` is type-agnostic at the IL level, so the int struct exercises
the same arms as a float struct would; the int sidesteps the pre-existing float
constructor / float VT-return bug (out of scope) cleanly. See M1 for the one probe-strength gap.

**7. UnitTest_10047 progression -- HONEST.** On HEAD the `ldobj` NIE fires; with the fix the NIE is
gone and the test reaches its own value assertion `Math.Abs(arr2[0].X - 1244) > 0.001f`. The
residual non-pass is the pre-existing float `op_Addition`-return / float-ctor bug (UnitTest_10047
uses TestVector3 = float fields). The int probe -- which uses the SAME ldobj/stobj arms and the
same `+=` lowering but with int arithmetic -- PASSES, proving the ldobj/stobj fix is correct and
the residual is genuinely the unrelated float bug. Progression is attributable to THIS fix.

## PRE-EXISTING items noted (out of scope, not introduced here)

- The float `op_Addition`-return / `new TestVector3(float,float,float)` constructor bug that blocks
  a TestVector3 (float-field) `+=` probe from PASSING end-to-end. Surfaced and correctly scoped out
  by the implementer as a candidate sibling child (same defect class as the child-16/21
  float-producer family, in the ctor-arg / VT-return path).
- `ldelem.any` / `stelem.any` on a CLR-struct array (plain `a = arr[i]` / `arr[i] = v`) returns
  garbage -- a separate broken path, NOT ldobj/stobj.

## Verdict

**APPROVE-WITH-FINDINGS.** The fix is correct, minimal, Neo-gated, and fully verified by build +
373/0 smoke + an airtight stash-toggle (2/2 NIE -> 0/2). The crux -- runtime detection without a
JIT marker -- is SOUND (ldobj/stobj operand is always a byref; the frame-native case is dispatched
first; no flat-bytes ambiguity, unlike child-24's raw-Ldfld). The single Minor (M1: TC2's combined
sum is invariant to element targeting) is a probe-strength note, not a code defect -- the
element-index decode is independently verified correct by code reading. Route M1 to the implementer
as optional hardening (assert a per-element sum); do not block ship.
