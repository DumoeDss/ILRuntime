# Tasks -- neo-recluster-2

- [x] FRESH grounding: build CLI (Debug_Neo --no-incremental) + TestCases (Debug).
- [x] Run full smoke (no filter): `Ran 951 tests, 2 failded` (StructTest12 + MyTest.Test).
- [x] Confirm BOTH PASS on Legacy (plain Debug+useRegister=true): exit 0 each.
- [x] Pin MyTest.Test root: JIT dump + code-dive -> (A) Box unseeded reference
      producer (TypeSpecializeNeoOpcodes has no `case Box`) -> Move specialization
      keys `Operand = IsNeoReferenceSlot(srcType)?1:0` -> non-ref Move copies only
      prim index -> dest dangles at box register's ref slot -> ldstr-reuse -> String
      cast. (B) get_Current_0_Neo TODO discards KVP value-type return -> "0  0".
- [x] Pin StructTest12 root: Activator.CreateInstance<T> resolves T to ILTypeInstance
      (not MyStruct2); redirect returns heap ILTypeInstance not a struct. Two coupled
      bugs; deepest singleton -- REPORTED, not fixed.
- [x] Fix (1): JITCompiler.cs TypeSpecializeNeoOpcodes +`case OpCodeREnum.Box:` seed
      registerTypes[dest]=ObjectType.
- [x] Fix (2): get_Current_0_Neo TODO -> WriteNeoValueType(KVP<int,int>) (child-28
      pattern).
- [x] Rebuild CLI (kill build-server + UseSharedCompilation=false; ILRuntimeTestBase
      touched -> CLI rebuild required).
- [x] Name-filter verify: MyTest.Test 1/0 PASS (exit 0); GetEnumeratorTest2 "1  1".
- [x] NeoStep smoke: 417/0 (no regression).
- [x] FULL SMOKE: 2 -> 1 (`Ran 951 tests, 1 failded`; StructTest12 sole survivor).
- [x] Legacy-neutral: plain Debug build 0 errors.
- [x] Artifacts: design.md, tasks.md, ship-log.md, fullsmoke-ground-02.md.
