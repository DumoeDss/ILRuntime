# Tasks - implement-neo-step15 (isinst / castclass under Neo)

All runtime code goes behind `#if ENABLE_NEO_MODE`. Do NOT modify Legacy
(`ExecuteR`). Build: CLI `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` (0 errors); TestCases `dotnet build TestCases/TestCases.csproj -c Debug` (NEVER Debug_Neo). Regression gate: full NeoStep smoke stays green (was 58/58; Step 15 cases only add).

## 1. Offset-lowering (Optimizer.Neo.cs)

- [x] 1.1 In `LowerNeoOffsets`, add `case OpCodeREnum.Isinst:` and `case OpCodeREnum.Castclass:` to the existing `Box`/`Unbox`/`Unbox_Any` case (around line 430). Stamp `DstOffset`/`SrcOffset` from `localInfos[r1].Offset`/`localInfos[r2].Offset` and `Operand3`/`Operand4` from the r1/r2 `RefOffset` (same block shape as Box). R1==R2 so the result is in-place.

## 2. ExecuteNeo arms (ILIntepreter.Neo.cs)

- [x] 2.1 Add a `case OpCodeREnum.Isinst:` arm near the `Box`/`Unbox` cluster: resolve `type = AppDomain.GetType(ip->Operand)` (throw `NullReferenceException` if null, matching Legacy); read `srcIdx = *(int*)(frameBase + ip->SrcOffset)`, `obj = srcIdx >= 0 ? mStack[srcIdx] : null`; if `obj` is `ILTypeInstance` use `CanAssignTo(type)`, else `type.TypeForCLR.IsAssignableFrom(obj.GetType())`; keep the original reference on success else `null`; write result via `dstIdx = frameRefBase + ip->Operand3; mStack[dstIdx] = result; *(int*)(frameBase + ip->DstOffset) = result != null ? dstIdx : -1;`. Never throw on mismatch.
- [x] 2.2 Add a `case OpCodeREnum.Castclass:` arm with the same dispatch, but a failed check throws `System.InvalidCastException` (`"Cannot Cast {0} to {1}"` with source/target full names) and a `null` source passes through as `null`.

## 3. Tests (TestCases/NeoStep15Test.cs, ASCII)

- [x] 3.1 Add `NeoStep15_TC1_IsTrueOnDerived` - `Base` local holding `Derived`, `local is Derived` -> true.
- [x] 3.2 Add `NeoStep15_TC2_IsFalseOnUnrelated` - `obj is Unrelated` -> false (null result).
- [x] 3.3 Add `NeoStep15_TC3_AsInterface` - `obj as IFoo` non-null when implemented, null when not (two cases or one with both paths).
- [x] 3.4 Add `NeoStep15_TC4_BoxedValueTypeIs` - box a value type, `boxed is V` -> true; `boxed is Unrelated` -> false.
- [x] 3.5 Add `NeoStep15_TC5_CastclassSuccess` - cast `Base`->`Derived`, read a `Derived` field -> expected value.
- [x] 3.6 Add `NeoStep15_TC6_CastclassFailureCaught` - `(Unrelated)obj` inside try/catch asserting the catch fires (so the InvalidCastException path is exercised green).
- [x] 3.7 Add `NeoStep15_TC7_IsInCatchBody` - throw via `1/0`, `catch (DivideByZeroException e) { if (e is DivideByZeroException) return 7; }` (exercises isinst inside a catch body -- the indirect Step 14 benefit).

## 4. Build + regression

- [x] 4.1 `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` -> 0 errors.
- [x] 4.2 `dotnet build TestCases/TestCases.csproj -c Debug` -> produces TestCases.dll.
- [x] 4.3 Run full NeoStep smoke: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` -> all green (prior 58 + new Step 15 cases, no regressions). Investigate any case taking >10s (interpreter loop).
- [x] 4.4 Spot-check that NO existing NeoStep case regressed (compare count to the pre-Step-15 baseline).
