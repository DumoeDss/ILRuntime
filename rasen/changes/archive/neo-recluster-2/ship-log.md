# Ship Log -- neo-recluster-2

## What shipped
Fixed MyTest.Test (boxed-CLR-struct enumerator InvalidCastException). Two coupled
sub-bugs, both Neo-gated -> Legacy-neutral:

1. `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- added
   `case OpCodeREnum.Box:` to the TypeSpecializeNeoOpcodes seeding switch,
   seeding `registerTypes[op.Register1] = appdomain.ObjectType`. Box was the
   one remaining reference producer that the seeding pass did not classify;
   without it, every `move` of a boxed value was a non-reference Move (the Move
   specialization keys the reference flag solely on registerTypes[src]),
   copying only the prim index and leaving the dest pointing at the box
   register's own ref slot. A reused temp then overwrote that slot and the dest
   dangled (a String ended up in the `e` local -> get_Current's cast threw).

2. `ILRuntimeTestBase/AutoGenerate/System_Collections_Generic_IEnumerator_1_
   KeyValuePair_2_I_t1.cs` -- get_Current_0_Neo replaced its literal
   `// TODO: CLR value type return in reflection fallback: Step 13` with the
   child-28 `WriteNeoValueType` write for the KeyValuePair<int,int> return
   (was discarding the result -> GetEnumeratorTest2's "0  0").

## Verification (truth = full-smoke number)
- Name-filter MyTest.Test: 1/0 PASS (exit 0; no String cast; GetEnumeratorTest2
  now prints "1  1").
- NeoStep smoke: **417/0** (no regression).
- FULL SMOKE: **2 -> 1** (`Ran 951 tests, 1 failded`; StructTest12 the sole
  survivor; strict subset of the entry 2).
- Legacy-neutral: plain Debug build **0 errors** (TypeSpecializeNeoOpcodes is
  the Neo-only pass; the stub fix is inside `#if ENABLE_NEO_MODE`).

## Residual (honestly reported)
- MyTest.Test's MAIN loop prints "0  0" under Neo (Legacy "1  1"); GetEnumeratorTest
  and GetEnumeratorTest2 are correct. The test has no value assertion -> it passes.
  A deeper box-identity sub-issue in the GetEnumerator-inlined loop context (not the
  crash root). Candidate follow-up child.
- StructTest12 REMAINS (Activator.CreateInstance<T> generic-param mis-resolution +
  heap-not-struct return; two coupled bugs; deepest singleton).

## Not committed (LEAD commits)
Files: JITCompiler.cs, the autogen binding stub, design.md, tasks.md, this ship-log,
rasen/changes/neo-overhaul/handoff/fullsmoke-ground-02.md.
