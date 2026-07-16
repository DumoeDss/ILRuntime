# neo-recluster-9 -- Wave-2 child ship-log

## Outcome: FRESH grounding delivered; NO engine fix shipped (all 9 are distinct deep singletons)

This child FRESH-ran the full Neo smoke (no filter), confirmed the count at **9 failed**
(`Ran 948 tests, 9 failded, 20 ignored, 7 todos`), re-clustered the CURRENT 9, and
DEEP-diagnosed the most tractable candidates to byte/IL-level roots. The 9 are a STRICT
SUBSET of ground-10's 9 (UnitTest_TestInline01 stays fixed via recluster-10).

**All 9 surviving are DISTINCT DEEP singletons** -- none yielded a contained, verifiable
fix in this budget. Shipping an unverifiable "looks fixed" change would violate the
mandate. The wave has exhausted the mechanical quick-win surface (neo-remaining-34-batch
lesson reaffirmed): further gains need per-root deep children.

## Deliverables (this child, NOT committed -- LEAD commits)
- `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-09.md` -- the FRESH 9 re-cluster
  + per-test pinned/presumed root table + fix-scope verdicts + recommended next-child
  priority. This is the load-bearing artifact for the next wave.

## No engine/test change this child
- No `ILRuntime/` source touched. No `TestCases/` added. NeoStep 414/0 (unchanged;
  nothing changed). Full smoke 9 (unchanged). Legacy-neutral by construction (no change).

## The pinned roots (summary; full detail in fullsmoke-ground-09.md)
- **StructTest6** -- byref out-STRUCT write-back: the byref `(-1, primOffset)` carries
  ONLY the prim offset; CopyNeoCallThisBack objIdx==-1 CopyBlock's PrimitiveSize bytes,
  so a pure-ref struct's (0 prim + N refs) fields are never written back. The Constrained
  arm (Neo.cs:7410-7424) ALREADY recovers the ref base via a localInfos scan -- the SAME
  recovery is needed in the byref write-back. LARGE (representation change or
  localInfos-threaded write-back).
- **StructTest12** -- `new T(){i=10}` (generic struct T): Roslyn lowers to
  `Activator.CreateInstance<T>()` and ILRuntime resolves T to **ILTypeInstance** for the
  Activator call (while `constrained T` in the SAME method resolves T to MyStruct2 --
  inconsistent). The mStack index (1) lands in the struct local; get_i reads it as field
  0 -> "1". DEEP (generic-param resolution + struct representation). SECOND latent gap:
  constrained-callvirt direct-call path has NO slot-0 write-back for a mutating IL-struct
  method.
- **ReflectionTest14** -- INVESTIGATED + DISPROVED as a reflection bug this child.
  Diagnostics (GetFields_11_Neo stub, ldlen arm, IsAssignableFrom stub) PROVED:
  GetFields returns a valid 2-element FieldInfo[]; the FIRST foreach iteration runs
  (`tags_F` prints); the NRE is on the SECOND iteration's ldlen because the GetFields
  dest register (r6, frame offset 20) is CLOBBERED 5->0 mid-loop-body by one of the
  body's calls. A separate IsAssign diagnostic showed its return also does not reach
  its dest cleanly. So ReflectionTest14 is a **register-transition / frame-slot
  clobbering bug in a foreach body with mixed calls** (same defect class as
  UnitTest_TestStackRegisterTransition3 / RegisterVMTest04), NOT a reflection gap.
  All 3 diagnostics reverted; engine clean; NeoStep 414/0.
- **ReflectionTest25** -- attribute construction: `[TestCLRAttribute2(name,desc,params
  string[])]` -> InitializeCustomAttribute/CreateInstance mishandles the params-array
  ctor arg -> Parameters null/wrong. DEEP (Cecil CustomAttribute -> CLR instance bridge).
- **RegisterVMTest04** -- stfld.ref reads garbage index 0x10000013 in a >3-arg virtual-
  IL-call through a generic instance (default `action=null` param slot). DEEP.
- **UnitTest_TestStackRegisterTransition3** -- struct-by-value (16 prim + 2 refs) to an
  IL callee corrupted; `[NoJIT]`. DEEP (struct marshalling / register transition).
- **UnitTest_10051** -- `.x.RawValue` constrained-callvirt property read on a nested
  struct field. DEEP (F-10 handoff noted residual).
- **UnitTest_StaticTest05** -- `ref <IL-static Vector3 field>` write-back lost (IL-static
  `ldsflda` deferred sibling of the CLR-static ldsflda gap). DEEP.
- **MyTest.Test** -- boxed-CLR-struct enumerator `this` mis-marshalled across loop
  iterations (InvalidCast String->IEnumerator). DEEP (Step-19).

## Recommended next-child priority
1. Register-transition / frame-clobbering defect class (HIGHEST COVERAGE, likely shared
   root): ReflectionTest14 (disproven as reflection -- it is a foreach-body frame-slot
   clobber), UnitTest_TestStackRegisterTransition3, possibly RegisterVMTest04. All show a
   live register's frame slot clobbered at a call boundary in a loop/mixed-call sequence.
   ReflectionTest14's pinned signal: GetFields dest r6 (offset 20) goes 5->0 between
   iterations -- cleanest lead into the allocator bug.
2. StructTest6 + UnitTest_StaticTest05 (neo-byref-struct-writeback) -- the Constrained
   arm's localInfos recovery is the template; high coverage.
3. StructTest12 (neo-generic-newobj-activator) -- generic-param resolution for `new T()`.
4. UnitTest_10051, ReflectionTest25, MyTest.Test -- each its own dedicated deep child.

## Verify (truth = full-smoke number)
- FULL SMOKE: **9 -> 9** (no fix shipped; the 9 reported honestly as distinct deep
  singletons). FRESH no-filter run: `Ran 948 tests, 9 failded`.
- NeoStep **414/0** (no regression; no engine change).
- Legacy-neutral (no engine change).
