# Tasks: neo-enum-cluster-residual

## Phase 1 -- RE-AUDIT (done)
- [x] Build CLI (Debug_Neo) + TestCases (Debug); build-server shutdown +
      UseSharedCompilation=false.
- [x] Name-filter `EnumTest` under Neo: 25 ran, 6 failed. Recorded exact messages.
- [x] Confirm all 6 PASS on Legacy (plain Debug + useRegister=true): 25 ran / 0
      failed -> Neo-specific.
- [x] Note Test15 (C4) + Test21 (C12) already green -> not re-fixed.

## Phase 2 -- implement + verify (done)
- [x] Root B: add `#if ENABLE_NEO_MODE` Equals + GetHashCode overrides on
      `ILEnumTypeInstance` (ILTypeInstance.cs). value-compare `byte[] fields`.
- [x] Root C: constrained-IL-VT box path branches `if (ilBoxType.IsEnum)` ->
      `new ILEnumTypeInstance` + copy underlying bytes (ILIntepreter.Neo.cs).
- [x] Root A: hand-port HasFlag_2_Neo + CompareTo_4_Neo via the public surface
      (ILTypeInstance.Type.IsEnum + Primitives); add NeoEnumRawLong helper
      (System_Enum_Binding.cs).
- [x] Build clean (0 errors).

## Verify (done -- truth = full-smoke number)
- [x] Name-filter EnumTest under Neo (post-fix): 25 ran / 0 failed.
- [x] Stash-toggle: revert the 3 engine files -> EnumTest filter = 6 failed (the
      exact same 6); restore -> 0 failed. Fixes are load-bearing.
- [x] NeoStep smoke: 388 ran / 0 failed (no regression).
- [x] FULL SMOKE: **101 -> 95** (-6; exactly the 6 EnumTest tests flipping; 0
      EnumTest failures remain). 922 ran / 95 failed / 20 ignored / 7 todos.
- [x] Legacy-neutral: all changes `#if ENABLE_NEO_MODE`-gated (or file-gated);
      Legacy EnumTest 25/0; Legacy `#else` binding branches untouched.
