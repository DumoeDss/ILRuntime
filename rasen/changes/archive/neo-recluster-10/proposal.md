# neo-recluster-10 -- Wave-2 child of neo-overhaul

## Why
The full Neo smoke was stuck at 10 failures. Re-cluster the CURRENT 10 from a
FRESH no-filter run, then batch-fix the most tractable singleton so the full-smoke
count drops (10 -> lower). No "looks fixed" -- truth is the full-smoke number.

## Grounding (FRESH, this child)
Pre-fix full smoke: `Ran 944 tests, 10 failded, 20 ignored, 7 todos` (exit 127,
known graceful Dict-NRE crash). NeoStep 404/0. The 10 are a strict subset of
ground-13's 13. Cluster table + per-test traces pinned in
`rasen/changes/neo-overhaul/handoff/fullsmoke-ground-10.md`.

## The flip shipped (UnitTest_TestInline01)
The recluster-34 handoff framed this as "by-ref aliasing on a plain Call."
DISPROVEN by isolation probes (NeoStepRecluster10Probe TC1/TC3/TC4): `new object()`
itself returned null -- the call was irrelevant. Root: `AppDomain.
IsInvalidMethodReference` (AppDomain.cs:2156) caches null for `System.Object..ctor()`,
and the Neo Newobj arm silently skipped (`ip++; continue;`) when targetMethod==null,
leaving the dest unwritten. Legacy handles this explicitly (Register.cs:3529-3536
"Means new object();" -> `new object()`).

3-part fix (all Neo-gated -> Legacy-neutral):
1. ILIntepreter.Neo.cs Newobj arm: null-method -> construct `new object()` + write
   to dest (reference-type newobj result).
2. Optimizer.Neo.cs LowerNeoOffsets Newobj case: stamp dest byte/ref offsets for
   the null-method case (was skipped -> DstOffset stayed a register INDEX ->
   clobbered an adjacent register; unmasked by fix #1 as UnitTest_1013).
3. JITCompiler.cs initobj marker: extend `NeoInitobjByRefOperandMarker` to a
   `Code.Ldflda` predecessor (Roslyn lowers `refField = null/default(T)` to
   `ldflda; initobj T`; unmasked by fix #1 as UnitTest_1013's SetNull).

Fix #1 is load-bearing; #2 and #3 are the siblings #1 unmasked (C7 pattern). All
three are required to flip UnitTest_TestInline01 WITHOUT regressing UnitTest_1013.

## Verify (truth = full-smoke number)
- FULL SMOKE: **10 -> 9** (`Ran 948 tests, 9 failded`; the 9 survivors are a
  strict subset of the pre-fix 10; UnitTest_TestInline01 flipped; UnitTest_1013
  passes both pre- and post-fix; no new regressions).
- NeoStep **414/0** (no regression; +4 = this child's probes).
- Legacy-neutral: plain Debug build 0 errors; all changes `#if ENABLE_NEO_MODE`-
  gated. Legacy NeoStep 414 ran / 19 failed (pre-existing Legacy-specific set;
  Neo-gated code does not compile into Legacy). All 4 probes PASS under Legacy.

## Scope / non-goals
The remaining 9 are DEEP singletons (RegisterVMTest04, MyTest.Test, StructTest6,
UnitTest_10051, UnitTest_TestStackRegisterTransition3, ReflectionTest25,
ReflectionTest14, StructTest12, UnitTest_StaticTest05) -- each needs its own JIT-
dump triage child. Reported honestly in the grounding doc; not addressed here.
