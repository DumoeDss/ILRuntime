# Ship Log -- neo-recluster-13 (wave-2 child of neo-overhaul)

## Mandate
FRESH-ground the CURRENT full Neo smoke, re-cluster the 13, batch-fix the most
tractable singletons. Success = full-smoke count dropping (13 -> lower).

## What ran (all FRESH, this child)
- Build: CLI Debug_Neo --no-incremental -p:UseSharedCompilation=false (0 errors);
  TestCases Debug (0 errors); plain Debug CLI (0 errors, Legacy-neutral).
- FRESH full smoke (no filter): `Ran 938 tests, 13 failded` (exit 0, no crash).
- The 13 are a STRICT SUBSET of ground-15's 15 (StructTest11 + TestStructDictionary
  fixed by neo-il-struct-box-call-boundary). Full per-test classification +
  pinned roots at `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-13.md`.

## Result: 13 -> 13 (no count reduction this child; honest)
The 13 are ALL deep singletons (the shallow surface is exhausted -- 49 prior
wave-2 children drove 189 -> 13). This child DEEP-diagnosed the #4 priority
(UnitTest_TestFCP) to its precise runtime location and shipped ONE real
correctness fix that does not reduce the 13 (none of the 13 exercise the fixed
path), plus pinned the residual for the next child.

## Shipped (NOT committed -- LEAD commits)
### 1. Correctness fix: CLR-struct newobj via reflection writes flat bytes
File: `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`,
`InvokeNeoClrMethod` ~line 1344 (the `isNewobj` branch).
- BEFORE: a CLR value-type newobj routed through the reflection fallback (no
  redirect -- e.g. a struct with no ValueTypeBinder ctor) ALWAYS stored the
  result as a REFERENCE (boxed-struct index written to the dest), even for a
  value type whose dest register holds FLAT bytes -> the caller read the index
  reinterpreted as the struct's first fields (zeros/garbage).
- AFTER: discriminate on `DeclearingType.TypeForCLR.IsValueType && !IsPrimitive
  && !IsEnum`; for a struct, write the flat managed bytes via WriteNeoValueType
  (mirrors the struct-return store + the autogen Ctor_Neo !isNewObj write-back);
  reference-type newobj keeps the index store. Neo-gated (file-gated) ->
  Legacy-neutral by construction.
- This is the reflection-fallback analog of `neo-clr-struct-newobj-retdest-null`
  (which fixed the autogen-stub Call_Redirect path).
- Proven by TC7 (`return new TestVector3NoBinding(1f,2f,3f)`): HEAD -> caller
  got (0,0,0); after fix -> (1,2,3). Stash-toggle-style airtight.

### 2. Permanent probes: TestCases/NeoStepRecluster13Probe.cs (TC1-TC7)
Isolation probes that PROVED (by exhaustion) where the UnitTest_TestFCP root is
NOT, plus TC7 the load-bearing regression probe for the fix:
- TC1/TC2: struct ctor into a local (literal args) WORKS.
- TC3: Convert.ToInt64 3x WORKS (not a stale stub).
- TC4: divi.r4 chain 3x WORKS (divi.r4 itself fine; empty JIT-dump operands are
  a printer gap).
- TC5/TC6: the full ToColor body in-place WORKS.
- TC7: as-value struct newobj return -- the fix's regression probe.
TC8 removed (the unfixed Move/ret gap, documented in fullsmoke-ground-13.md).

## PINNED root (UnitTest_TestFCP, the #4 priority) -- for the next child
UnitTest_TestFCP: ToColor("#FF00FF00") returns (1,0,0) instead of (1,1,0).
Runtime instrumentation PROVED the reflection struct-ctor path is fully correct:
1. ctor receives a=1,b=1,c=0 (divi.r4 args ARE correct at the call boundary);
2. targetBase[0..12] = (1,1,0) post-Invoke (box-mutation + WriteNeoValueType OK);
3. CopyNeoCallThisBack copies 12B (1,1,0) to the caller offset 56 (r12) -- size
   + offset CORRECT;
4. BUT the caller's `color` = (1,0,0) -- the y/z loss is AFTER step 3, in
   ToColor's `46:move r13, r12; 53:ret r13` sequence.
ROOT: ToColor's lowering ends with a PLAIN `OpCodeREnum.Move` (not `Move_Vt`)
before `ret r13`. The Neo `Move` arm copies `ip->Operand2` bytes (Neo.cs:2086);
for a 12-byte struct return the JIT must stamp Operand2=12 (or emit Move_Vt),
but in ToColor's register-allocation context it copies only the leading 4 bytes
(x=1), leaving r13.y/r13.z stale (0,0) -> ret returns (1,0,0). TC7 (newobj
return) works because InvokeNeoClrMethod writes the dest directly (no Move);
TC8/ToColor fail because they route through `move r;ret r`. Same "Neo untyped
frame needs JIT-time struct-size facts" theme (child-11/16/21/24/29 +
il-struct-box curVtTypes tracker). Candidate child: `neo-vt-return-move-size`
(JIT Move emission -- stamp Operand2 = VT primitive size for a Move whose
src/dst register is a value type, or rewrite to Move_Vt). Could also affect
StructTest6 / StructTest12 (re-audit the vt-move path).

## Verify
- FULL SMOKE: **13 -> 13** (no count reduction; the same 13, no new failures;
  944 ran = 938 + 6 new NeoStep probes). The fix flipped none of the 13 (none
  use a CLR-struct reflection newobj; UnitTest_TestFCP uses call.ctor-into-local
  and hits the SEPARATE Move/ret gap).
- NeoStep: **410/0** (404 baseline + 6 new probes; no regression).
- Legacy-neutral: plain Debug CLI build 0 errors (fix is `#if ENABLE_NEO_MODE`
  file-gated; probe uses host CLR types, runs under both).

## Remaining (13 deep singletons, honestly reported)
Ordered by this child's coverage + confidence + pinning (full detail in
fullsmoke-ground-13.md):
1. UnitTest_TestFCP -- PINNED (JIT Move size for vt return).
2. RegisterVMTest04 -- >3-arg virtual-IL-call param-map (generic instance).
3. MyTest.Test -- boxed-CLR-struct enumerator dispatch (Step-19).
4. StructTest6 -- byref out-STRUCT write-back.
5. UnitTest_TestInline01 -- inlined-call reference-arg aliasing.
6. ReflectionTest14 -- null internal reflection array.
7. StaticTest05 / StructTest12 -- distinct deep roots.
8. Cluster A grab-bag: UnitTest_TestStackRegisterTransition3 / ReflectionTest25
   / UnitTest_10046 / UnitTest_10051 / DelegateTest42. NOTE: DelegateTest42 +
   UnitTest_10046 + UnitTest_10051 + MyTest.Test all touch Step-19
   delegate/enumerator dispatch (4 tests -- worth a re-audit for a shared root).
