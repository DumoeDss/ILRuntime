# Tasks -- neo-f4-surfaced-gaps

> All tasks DONE. Both gaps fixed, both probes FAIL-on-HEAD -> PASS-after, full
> NeoStep regression green, Legacy-neutral.

- [x] 1. (propose) Recover planner's transcript; extract the two fix sites +
      the empirical Gap B re-characterization (plain ctor works on HEAD; real
      bug is the derived IL type's flat instance missing inherited fields).
- [x] 2. (propose) Write proposal.md / design.md (per-gap dump-gate verdicts,
      file:line-cited) / specs deltas (neo-exceptions ADDED, neo-newobj
      MODIFIED; PURE ASCII SHALL-first) / tasks.md.
- [x] 3. (Gap A fix) `ILIntepreter.ReadNeoReference`
      (`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:123`):
      `return idx >= 0 ? mStack[idx] : null;` (null-sentinel guard, Neo-only).
- [x] 4. (Gap B fix) `ILType.InitializeFields`
      (`ILRuntime/CLR/TypeSystem/ILType.cs`, `#if ENABLE_NEO_MODE` arm): seed
      `primitiveOffset` / `referenceOffset` from the IL base type's flat
      `TotalPrimitiveSize` / `TotalReferenceCount` so the derived type's flat
      instance covers inherited fields (Neo-only).
- [x] 5. (probes) `TestCases/NeoStep14Test.cs`: `NeoStep14_ILEx_GapA_TypeOpEqualityNull`
      (HEAD `Return:-96` -> after `Return:9`) and
      `NeoStep14_ILEx_GapB_NewobjStringArg` (HEAD throws -> after `Return:9`;
      exercises both plain `new MyEx("ctor-msg")` and `new DerivedEx("derived-msg") : base(msg)`).
- [x] 6. (verify FAIL-on-HEAD) Stash-toggle the two ILRuntime fixes, rebuild,
      re-run both probes: Gap A `Return:-96`, Gap B `1 tests failed`. Both
      load-bearing confirmed.
- [x] 7. (regression) `NeoStep14`: 23/0/0 (incl. the 2 new probes).
      `NeoStep` full smoke: 226/0/0 (was 224; +2 probes).
- [x] 8. (Legacy-neutral) `dotnet build ILRuntime/ILRuntime.csproj -c Debug` ->
      0 errors (both fixes are Neo-only).
- [x] 9. (deferred-items) Mark the F-4 row's "2 NEW gaps sequenced
      (op_Equality null-operand + newobj string-arg)" RESOLVED in
      `.trae/documents/neo-deferred-items.md`, citing this change and the Gap B
      re-characterization (so a future worker does not re-chase the stale
      "plain ctor stores this" hypothesis).
