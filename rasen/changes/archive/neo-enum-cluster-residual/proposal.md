# Proposal: neo-enum-cluster-residual (Wave-2 child C3)

## Context
Wave-2 cluster C3 of `neo-overhaul`. The 2026-07-13 full-smoke grounding (189
failures) listed 8 EnumTest failures under the "Enum cast/Equals/GetType" cluster.
Wave-2 children C4 (GetType VTable fallback, commit 0414c296) and C12 (runtime-Type
bridge / Enum.ToObject, commit 85f39fbc) already fixed two of them (Test15, Test21).

## Re-audit (against the CURRENT 101-baseline, not the 189-grounding)
Name-filtered `EnumTest` run under Neo (`Debug_Neo` + useRegister=true): 25 ran,
**6 failed**. The two C4/C12 fixes flipped Test15 + Test21 green. The EXACT residual
worklist + current messages:

1. `EnumTest.Test11` -- `Different string value: Enum4 vs. TestCases.EnumTest/TestEnum`
   (`enumValue.ToString()` returns the TYPE full name, not the value name; the
   string-interpolation form ` $"{x}" ` already worked).
2. `EnumTest.Test20` -- `Unable to cast ILEnumTypeInstance to System.Enum` at
   `System_Enum_Binding.HasFlag_2_Neo:144` (flag.HasFlag).
3. `EnumTest.Test22` -- `Unable to cast ILTypeInstance to System.Enum` at
   `System_Enum_Binding.CompareTo_4_Neo:220` (enum.CompareTo).
4. `EnumTest.Test30` -- bare `Exception`: boxed-enum `_testValue.Equals(Feature3)`
   returns false (TestClass2.Test2).
5. `EnumTest.Test32` -- same as Test30 (via TestClass1.Test2).
6. `EnumTest.Test33` -- bare `Exception`: `object.Equals(_testValue, Feature3)`
   returns false (TestClass2.Test3).

All 6 PASS on Legacy (plain Debug + useRegister=true, 25 ran / 0 failed) ->
Neo-specific.

## Root causes (3 distinct, pinned by Neo-vs-Legacy + code evidence)
- **Root B (Test30/32/33, 3 tests -- largest):** `ILTypeInstance.Equals(object)` has
  an `ILEnumTypeInstance` value-equality branch, but it is `#if !ENABLE_NEO_MODE`'d
  out -> under Neo it falls to `base.Equals` (REFERENCE equality). Two separately-
  boxed enum values are never reference-equal -> false. (The Object.Equals redirect
  `Equals_3_Neo` reads the receiver via `ReadNeoReference` -- no projection for an
  enum -- and calls `instance.Equals(obj)`, dispatching to this override.)
- **Root A (Test20, Test22):** the autogen Neo binding stubs `HasFlag_2_Neo` /
  `CompareTo_4_Neo` cast the boxed IL enum directly to `System.Enum`
  (`(System.Enum)ReadNeoReference`) -- but a boxed IL enum is an `ILEnumTypeInstance`
  / `ILTypeInstance`, NOT a real `System.Enum` -> `InvalidCastException`.
- **Root C (Test11; also a precondition for Test22's receiver):** a
  `constrained.callvirt` on an IL enum to an inherited Object method (ToString /
  Equals / GetHashCode) boxed the enum via `ilBoxType.Instantiate(false)` -- a PLAIN
  `ILTypeInstance` whose `ToString` returns the type's full name. It must box to an
  `ILEnumTypeInstance` (whose ToString returns the value name), mirroring the Box
  opcode arm.

## What ships (Neo-gated -> Legacy-neutral by construction)
- Root B: add `#if ENABLE_NEO_MODE` `Equals` + `GetHashCode` overrides on
  `ILEnumTypeInstance` (value-compare the `byte[] fields`).
- Root A: hand-port `HasFlag_2_Neo` + `CompareTo_4_Neo` to detect an IL enum
  (`ILTypeInstance` with `Type.IsEnum`) and compute the result directly on the
  underlying value bits (no real `System.Enum` exists for an IL enum).
- Root C: in the constrained-IL-VT box path, branch `if (ilBoxType.IsEnum)` ->
  `new ILEnumTypeInstance` + copy the underlying bytes (mirror the Box arm).

## Success criterion
Full Neo smoke count drops (101 -> lower), verified by re-running the full smoke
(the 6 residual EnumTest tests flipping green).
