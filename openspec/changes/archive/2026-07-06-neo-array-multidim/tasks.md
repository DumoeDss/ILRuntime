## 1. Confirm the working multi-dim paths (regression guards)

- [x] 1.1 Confirm `NeoStep16_MultiDimRank2Probe` (minimal `int[,]` ctor + Set
  + Get) PASSES on HEAD (dump-confirmed by the planner; the autogen `Ctor_0_Neo`
  + `Set_0_Neo` redirects + reflection `Get`).
- [x] 1.2 Confirm `NeoStep16_MultiDimRank2MultiCell` (6 distinct cells)
  PASSES on HEAD -- rules out a coincidental default-0 / stale-value pass.
- [x] 1.3 Confirm `NeoStep16_MultiDimRank3Probe` (`int[,,]`) PASSES on HEAD.
- [x] 1.4 Confirm `NeoStep16_MultiDimRank2Metadata` (`Rank`/`Length`/
  `GetLength`) PASSES on HEAD.
- [x] 1.5 Confirm `NeoStep16_MultiDimRank2Long` (`long[,]`, NO binder, full
  reflection ctor + Set + Get) PASSES on HEAD -- proves the reflection
  fallback works for primitive elements.

## 2. Gap 1 -- Neo reference-element Get/Set value correctness (D1, dump-pin)

- [x] 1.6 Re-confirm the 3 FAIL-on-HEAD probes still map to Gap 1/2/3 (String =
  value correctness / assertion; Null = ArgOutOfRange from mStack[-1]; OutOfRange
  = TargetInvocationException wrapped). [apply baseline 2026-07-06]
- [x] 2.1 Dump-pin the bug: OQ1 RESOLVED -- the bug is on the GET-side
  reference-return write-back (`InvokeNeoClrMethod`), NOT the Set side. Set
  inputs are correct (`Set params=[0,0,alpha]`); `def.Invoke` returns the right
  value; but a NULL return was encoded as `targetRetRefBase` (a valid index)
  instead of the null sentinel (-1). The index-based `cgt.un` null-test then
  read the valid index as "not null" -> false-positive `!= null`.
- [x] 2.2 Applied the Neo-overload correction in `InvokeNeoClrMethod`
  (`ILIntepreter.Neo.cs` reference-return branch): if `res == null`, write `-1`
  to retDstPtr (mirrors `Ldnull` + the `idx < 0 ? null` convention); else
  unchanged. Legacy is byte-unchanged (Legacy passes the probe).
- [x] 2.3 `NeoStep16_MultiDimRank2String` FAIL-on-HEAD -> PASS-after-fix
  (confirmed 1/1 pass with the fix; the null `a[0,1]` cell no longer fires the
  `!= null` false positive).

## 3. Gap 2 -- Neo null-array `this` guard (D2)

- [x] 3.1 Read the Neo `HasThis` branch (`CLRMethod.cs:365-405`) and the
  Legacy null-`this` guard (`CLRMethod.cs:707-713` -- `if (instance == null)
  throw new NullReferenceException();`).
- [x] 3.2 In the Neo overload, read `thisIdx`; if `thisIdx < 0` (the Neo
  null-ref sentinel), set `instance = null` (do NOT index `mStack[-1]`).
  Applied `instance = thisIdx < 0 ? null : mStack[thisIdx]` in the reference-
  type `this` sub-branch; the existing ctor/method null-instance guards
  (`if (instance == null) throw new NullReferenceException()`) then fire.
- [x] 3.3 `NeoStep16_MultiDimRank2Null` FAIL-on-HEAD (Neo) -> PASS-after-fix
  (confirmed in the 8-probe smoke: now 8 ran / 1 failed = only OutOfRange
  remains). Legacy unchanged (Legacy-neutral gate is task 6.3, after Gap 3).

## 4. Gap 3 -- Unwrap TargetInvocationException (D3, shared, Legacy-neutral)

- [x] 4.1 In `CLRMethod.Invoke(byte*, AutoList, bool)` (Neo overload), wrapped
  ALL 3 reflection Invoke sites (not only the 2 named): non-newobj ctor
  `cDef.Invoke(instance, param)` (CLRMethod.cs:555), newobj ctor
  `cDef.Invoke(param)` (:570), method `def.Invoke(instance, param)` (:583) in
  `try/catch (TargetInvocationException tie) {
  ExceptionDispatchInfo.Capture(tie.InnerException).Throw(); throw; }`.
- [x] 4.2 In `CLRMethod.Invoke(ILIntepreter, StackObject*, AutoList, bool)`
  (Legacy overload), wrapped all 3 sites identically (CLRMethod.cs:698, :711,
  :738). Added `using System.Runtime.ExceptionServices;` (line 10).
- [x] 4.3 `NeoStep16_MultiDimRank2OutOfRange` FAIL-on-HEAD (BOTH engines) ->
  PASS-after-fix. Stash-toggle proof (LOAD-BEARING): HEAD Legacy 11 fail incl
  OutOfRange (TIE); fixed 10 fail, OutOfRange PASSES on BOTH Neo (198/198) and
  Legacy (8/8 multi-dim; 723/10 full with OutOfRange now passing).
- [x] 4.4 Legacy-neutral gate: stash-toggle of both source files. HEAD full
  Legacy baseline = 723 ran / 11 fail; with fix = 723 ran / 10 fail. The 10
  remaining failures are IDENTICAL pre-existing Neo-under-Legacy failures
  (NeoOptHardening K1, NeoStep13/14/15/16 Neo-specific, NeoNaNR8,
  StelemI_NIntArray) -- NONE mention TargetInvocationException, none changed.
  (OQ2 RESOLVED: grep TestCases for `TargetInvocationException` = 0 matches ->
  no Legacy test catches TIE directly -> no regression possible.)

## 5. Probe finalization + adversarial coverage

- [x] 5.1 Keeper set finalized: all 8 `NeoStep16_MultiDim*` probes kept (5
  regression guards: Probe/MultiCell/Rank3/Metadata/Long; 3 load-bearing:
  String/Null/OutOfRange). Shared `NeoStep16_MultiDim` prefix catches all in
  the smoke filter. (Probe is NOT subsumed by MultiCell -- it isolates the
  minimal ctor+Set+Get round-trip; kept for diagnostic granularity.)
- [x] 5.2 Adversarial FAIL-on-HEAD -> PASS-after confirmed for each gap:
  String (Gap1, after null-encoding fix), Null (Gap2, after null-this guard),
  OutOfRange (Gap3, after TIE unwrap). Regression guards PASS-throughout
  (primitive + metadata untouched).
- [x] 5.3 IL value-type-element `[,]` probe (`MyStruct[,]`) NOT added --
  documented as a Non-Goal (design Non-Goals: the reflection `Get` returns a
  boxed struct; the existing `retType.IsValueType` return branch
  (ILIntepreter.Neo.cs:686-702) handles a boxed CLR struct via
  WriteNeoValueType, so a pure-primitive struct element likely WORKS, but the
  VT-with-ref-field + IL-element-type copy semantics are a deeper edge deferred
  to a neo-byref / VT-completion follow-up).

## 6. Build, smoke, Legacy-neutral

- [x] 6.1 Built CLI `Debug_Neo` (0 errors) + TestCases plain `Debug` (0
  errors). Build-cache verified by the stash-toggle itself (HEAD = 11 Legacy
  fail, fix = 10 -- behavior changed, so the ILRuntime DLL rebuilt with the
  Gap 3 unwrap; no stale-binary ambiguity).
- [x] 6.2 Full `NeoStep` smoke (`Debug_Neo`, `-f net8.0`, filter `NeoStep`) =
  **198/198 pass, 0 failed** (190 baseline + 8 multi-dim probes).
- [x] 6.3 Legacy-neutral: plain `Debug` + `useRegister=true`; multi-dim filter
  = 8/8 pass (incl OutOfRange, proving Gap 3 Legacy-neutral); full baseline =
  723/10 (down from HEAD 723/11; no new failures).

## 7. Docs + spec archive

- [x] 7.1 Appended durable apply findings to
  `openspec/changes/neo-array-multidim/planning-context.md` (OQ1 root cause +
  resolution; OQ2 resolution; exact lines changed; new edges discovered; smoke
  evidence) under `## Findings -- neo-array-multidim (apply, 2026-07-06)`.
- [x] 7.2 Updated `.trae/documents/neo-deferred-items.md`: marked the D-ARR row
  RESOLVED (rank-1 + rank-2+); updated the D-ARR detail block + the
  opportunistic list (multi-dim removed) to reflect `neo-array-multidim`
  delivery (Gap 1/2/3 closed; IL VT-element `[,]` + multi-dim Address stay
  deferred as Non-Goals).
- [ ] 7.3 (Ship/archive step, post-review) merge the `neo-arrays` spec delta
  into `openspec/specs/neo-arrays/spec.md` (lift the multi-dim NON-GOAL; add
  the new Requirements) and move the change to `openspec/changes/archive/`.
