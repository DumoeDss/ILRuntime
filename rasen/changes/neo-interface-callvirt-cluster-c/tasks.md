# tasks -- neo-interface-callvirt-cluster-c

- [x] Phase 1 RE-AUDIT: build CLI Debug_Neo --no-incremental + TestCases Debug.
- [x] Confirm full smoke = 42 failed (932 ran). Extract the 4 C-cluster tests
      (Callvirt_Interface @ Neo.cs 1446/1453): InheritanceTest_Interface,
      InheritanceTest_Interface2, Test05.TestGenericStruct, TestAs.TestAs03.
- [x] Confirm all 4 PASS under Legacy (plain Debug + useRegister=true).
- [x] Diagnose: 3 DISTINCT root causes (interface-type ref-equality is FINE):
      RC1 structs skip VTable build; RC2 explicit-impl dotted name unmatched;
      RC3 CLR-adaptor this rejected (JIT elides `as IFace` isinst for a CLR-base
      IL type retrieved via a CLR collection).
- [x] Fix A (ILType.cs): BuildNeoVTable runs EnsureNeoInterfaceImplementorSlots
      for value types too; FindNeoImplementingMethod gets an explicit-impl
      dotted-name fallback. -> 3 tests fixed (42 -> 39).
- [x] Fix B (ILIntepreter.Neo.cs): ResolveNeoCallvirtInterfaceTarget unwraps a
      CrossBindingAdaptorType this to its ILInstance and remaps the call this for
      an ILMethod target. -> TestAs03 fixed (39 -> 38).
- [x] Full smoke: 42 -> 38 (all 4 flipped, ZERO new failures).
- [x] Stash-toggle: 4/4 FAIL on HEAD (stashed) -> pop -> 4/4 PASS.
- [x] NeoStep 398/0 (no regression).
- [x] Legacy-neutral: 398/18 (documented baseline) under plain Debug; all 4
      C-cluster tests PASS under Legacy.
