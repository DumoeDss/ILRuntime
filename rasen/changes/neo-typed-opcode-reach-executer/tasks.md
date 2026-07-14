# Tasks -- neo-typed-opcode-reach-executer

## 1. RE-AUDIT (verify at 69)
- [x] Build CLI (Debug_Neo --no-incremental) + TestCases (Debug). 0 errors.
- [x] Baseline full Neo smoke = **69 failed**; 6 typed-opcode cluster
      (InheritanceTest07/16, JsonTest1, ReflectionTest09/11/23).
- [x] Pin the route: temp `StackTrace` dump at `Register.cs:5325` -> BOTH routes
      converge on `InvocationContext.Invoke` (InvocationContext.cs:458 ->
      ExecuteR). Reflection = ILRuntimePropertyInfo.GetValue; cross-binding =
      CrossBindingFunctionInfo.Invoke.
- [x] Neo-specific confirmed (Legacy passes via ExecuteR on Legacy bodies).

## 2. Implement (Neo-gated hybrid, Legacy-neutral)
- [x] `InvocationContext.Invoke`: `#if ENABLE_NEO_MODE` branch -> `CanInvokeNeo()`
      ? `InvokeNeo()` : fall through to Legacy `ExecuteR`/`Execute`.
- [x] `CanInvokeNeo`: reject ctors (`method.IsConstructor`), non-register bodies,
      and any arg slot that is `StackObjectReference` (byref) or
      `ValueTypeObjectReference` (binder VT). Admit only the simple shape.
- [x] `InvokeNeo`: read args from the LAST `paramCnt` slots before `esp`; re-enter
      via `intp.Run` (proven CLR->IL Neo re-entry); `PushObject` result at `ebp`,
      set `esp` so typed readers (which deref `esp`) work.
- [x] Removed the temp diagnostic from `Register.cs` (restored to pristine).

## 3. Verify (truth = full-smoke count)
- [x] Cluster name-filter: JsonTest1, ReflectionTest09/11/23 PASS after fix.
- [x] v1 (full Run port) REGRESSED 8 Hotfix tests (69 -> 73) -> replaced by hybrid.
- [x] FULL SMOKE delta: **69 -> 65**, "Not supported opcode" cluster = 0.
- [x] ZERO regressions (failure-set diff baseline vs post: empty pass->fail set).
- [x] NeoStep smoke **394/0** (no regression).
- [x] Stash-toggle: stash InvocationContext.cs -> ReflectionTest09 FAILS
      (`Not supported opcode Ldfld_U4`); pop -> PASS.
- [x] Legacy-neutral: 100% `#if ENABLE_NEO_MODE`; Legacy arms byte-identical.

## 4. Deferred (follow-up children, documented in design.md)
- [ ] neo-invocationctx-byref (InheritanceTest07 / VMethod3 ref int).
- [ ] neo-invocationctx-ctor (Hotfix HotfixClass___Extra..ctor path).
- [ ] neo-invocationctx-valuetype (binder VT args).
- [ ] InheritanceTest16 garbage float (separate Neo Muli_R4 producer bug).
