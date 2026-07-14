# Tasks: neo-delegate-dispatch-arg-marshal (Wave-2 D1)

- [x] 1. Build CLI (Debug_Neo --no-incremental, UseSharedCompilation=false) + TestCases (Debug). (0 errors)
- [x] 2. Baseline full Neo smoke: 935 ran / 31 failed. Confirm DelegateExtTest01/02 + DelegateTest01 fail with identical NIE ("Owner type: System.Int32"). Confirm the 3 are in the 31.
- [x] 3. RE-AUDIT the delegate path with diagnostics in BOTH NeoRunDelegateTargetOnThis (gated target.IsExtend) and NeoInvokeSub (gated method.IsExtend). RESULT: only NeoInvokeSub fires for DelegateExtTest01 (real CLR-delegate adapter -> autogen Invoke -> NeoInvoke -> NeoInvokeSub). DISPROVES the batch-child "NeoRunDelegateTargetOnThis headShift + D2" diagnosis.
- [x] 4. Pin root: NeoInvokeSub has no IsExtend branch -- bound instance not written as param0; args[0] (=int a) lands in the extension-this slot. Compare Legacy ILInvokeSub (IsExtend && instance!=null -> push instance, paramCnt--).
- [x] 5. Implement fix in NeoInvokeSub (DelegateAdapter.cs, Neo-gated): extendBound = !hasThis && IsExtend && instance!=null; write instance to paramInfos[0], argIdx=1; argCount = extendBound ? paramCnt-1 : paramCnt; align byref-scratch index brIdx = i + (extendBound?1:0), slotIdx=argIdx. Non-extend byte-identical.
- [x] 6. Remove all diagnostics; confirm source clean.
- [x] 7. NeoStep broad smoke: 401/0 (no regression).
- [x] 8. FULL SMOKE delta: 31 -> 28 (935 ran; 28 strict subset of 31; the 3 D1 tests flipped; 0 regressions). EXIT=127 known graceful crash.
- [x] 9. Stash-toggle: stash DelegateAdapter.cs -> DelegateExtTest01 FAILS (NIE) -> pop -> PASSES.
- [x] 10. Legacy-neutral: plain Debug build = 0 errors.
