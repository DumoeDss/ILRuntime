# Ship Log -- neo-recluster-28 (Wave-2 child of neo-overhaul)

Branch: `features/object-model-overhaul`. Worker: PLANNER+IMPLEMENTER
(background). 2026-07-15. NOT committed (LEAD commits).

## Mandate
Re-cluster the CURRENT full Neo smoke failures (was reported as 28), find +
fix the LARGEST tractable sub-cluster, verify by a fresh full smoke (count
must drop). Truth = the full-smoke number.

## Phase 1 -- FRESH grounding (the current 28)
Fresh full smoke (Debug_Neo, no filter, same TestCases.dll + HotfixAOT.patch):
`Ran 935 tests, 28 failed, 20 ignored, 7 todos` (exit 127 = known graceful
Dict-NRE crash; summary emitted). Legacy (plain Debug) build clean.

The 28 are predominantly DEEP singletons / distinct roots. Cluster table
written to `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-28.md`.
Largest by COUNT = the throw@6988/7070 grab-bag (11 tests), but each is a
DISTINCT test-internal assertion (NOT one fix). Largest SHARED-ROOT clusters
are 2-test pairs (Test01.Generics/G generics2 stfld.ref; byref out-STRUCT;
hotfix field-index). The single MOST TRACTABLE item was Cluster F: an
explicitly-tagged deferred NIE.

## Phase 2 -- fix shipped (Cluster F: ldsflda CLR static struct field)
`ExpTest_10.UnitTest_Struct2` hit the tagged NIE "Neo Ldsflda: CLR static
field address deferred (follow-up)" @ `ILIntepreter.Neo.cs:5428`.
`TestStruct.instance.value = 222; += 111; WriteLine(instance.value)` on a
CLR-static struct field (`static TestStruct instance`).

### The fix (runtime-only, Neo-gated, ~80 lines, single file
`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`)
A self-describing descriptor `NeoClrStaticFieldAddr { CLRType ClrType; int
FieldHash; }` pushed onto mStack by the Ldsflda CLR-static branch (replaces
the NIE); emit byref `(mStackIdx, 0)`. The consumers recognize
`mStack[objIdx] is NeoClrStaticFieldAddr` BEFORE their existing branches:
- raw Stfld VT-owner byref arm -> read boxed struct via
  `ClrType.GetFieldValue(FieldHash, null)`, `f.SetValue(boxed, value)`,
  write back via `ClrType.SetStaticFieldValue(FieldHash, boxed)`.
- raw Ldfld VT-owner IsValueType arm -> read boxed struct, `f.GetValue(boxed)`.
- nested ldflda (0x10 marker) arm -> when the containing origin is a
  NeoClrStaticFieldAddr, produce a `NeoNestedFieldAddr` with `IsClrStatic`
  (reads the boxed struct from the static) so the ldind READ path works.

NO JIT marker -- runtime content-detection on a NEW mStack type (collision-
free, mirrors `NeoNestedFieldAddr`). The descriptor is reclaimed with the
frame (no process-static leak).

### CRITICAL Legacy-parity gotcha (load-bearing; nearly shipped the wrong thing)
The nested-`+=` on a CLR static struct field (`ldflda <inner>; ldind; add;
stind`) MUST NOT persist under Neo. Legacy does NOT persist it: verified by
running `TestValueTypeBinding.Test01` (`[ILRuntimeTest(IsToDo = true)]`,
`TestVector3.One.X += vec.X`) under Legacy -- `One.X` stays 1, so the test's
`if(One.X==1) throw` fires (IsToDo -> counted as todo). Persisting it under
Neo (.NET-correct via `SetStaticFieldValue`) mutates the SHARED static
`TestVector3.One`, which breaks `UnitTest_10047` (`arr2[0] += TestVector3.One`
expects One.X=1). So `WriteNeoNestedInnerField`'s `IsClrStatic` branch is an
intentional NO-OP write-back (the stind mutates only the descriptor's local
boxed copy, discarded with the frame). The raw Stfld/Ldfld CLR-static arms
(the `= v` / read forms) DO persist (Legacy parity: Struct2
`instance.value=222` persists; the nested `+=111` does not -> Struct2 prints
222 for `instance`, matching Legacy's 222). Struct2 has NO assertion, so it
passes either way; mirroring Legacy keeps the test suite's shared-static
isolation intact.

## Verify (truth = full-smoke number)
- FULL SMOKE: **28 -> 27** (`Ran 935 tests, 27 failed, 20 ignored, 7 todos`).
  The 27 are a STRICT SUBSET of the ground 28 (diff = only UnitTest_Struct2
  removed). Confirmed via sorted diff of the failure `Test name:` lists.
- UnitTest_Struct2 PASSES; prints 222/222/222 (matches Legacy's instance.value
  semantics). The `CLR static field address deferred` NIE count = 0.
- NeoStep smoke: **401/0** (no regression; the additive guards + the new
  descriptor type do not affect any existing path -- verified by the
  child-19/24/26/27/29/F-10/nested-ldflda array/struct probes all green).
- Legacy-neutral: `dotnet build ILRuntimeTestCLI -c Debug` (plain,
  ENABLE_NEO_MODE off) = 0 errors. All changes are in `ILIntepreter.Neo.cs`
  (file-gated `#if ENABLE_NEO_MODE`).
- Stash-toggle (sanity): baseline (change stashed) TestValueTypeBinding run =
  `2 failed, 1 todo` (10047 passes, Test01 todo); with the change (first cut,
  persistent) = `3 failed` (10047 broke); with the Legacy-mirror no-op write-
  back = `2 failed, 1 todo` (matches baseline exactly). Airtight.

## Files (NOT committed, LEAD commits)
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`:
  - new `sealed class NeoClrStaticFieldAddr { CLRType; int FieldHash; }`.
  - new fields on `NeoNestedFieldAddr`: `IsClrStatic`, `ClrStaticType`,
    `ClrStaticFieldHash`.
  - `Ldsflda` CLR-static branch: descriptor push (replaces the deferred NIE).
  - raw `Stfld` VT-owner byref arm: leading `is NeoClrStaticFieldAddr` guard.
  - raw `Ldfld` VT-owner IsValueType arm: leading `is NeoClrStaticFieldAddr`
    guard.
  - nested ldflda (0x10 marker) arm: `is NeoClrStaticFieldAddr` branch ->
    NeoNestedFieldAddr(IsClrStatic).
  - `WriteNeoNestedInnerField`: `IsClrStatic` branch = NO-OP write-back
    (Legacy mirror).
- `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-28.md` (fresh cluster
  table + post-fix status).
- `rasen/changes/neo-recluster-28/ship-log.md` (this file).

## Remaining (for future children -- see fullsmoke-ground-28.md)
27 failures remain, all DEEP. Recommended next-batch priority:
1. Test01.UnitTest_Generics + Generics2 (x2, stfld.ref NRE @ Neo.cs:5007 on a
   self-referential generic singleton -- highest shared-root coverage).
2. Hotfix Neo-bridge field-index (x2, PushToStack/AssignFromStack via Legacy
   Execute path).
3. byref out-STRUCT (x2, distinct from recluster-38's ref-field fix).
4. ReflectionTest14 (ldlen-on-null; GetFields under Neo returns null).
5. typeof(<genericparam>) TypeForCLR mis-resolution (TestGenericMethod2).
6. The throw@6988/7070 grab-bag (11 distinct value-corruption roots; each
   needs its own JIT-dump triage child).
