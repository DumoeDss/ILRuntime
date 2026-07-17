# Tasks: neo-initobj-ref-byref

## 1. RE-AUDIT (empirical, DONE)
- [x] Build CLI (Debug_Neo --no-incremental) + TestCases (Debug), 0 errors.
- [x] Confirm full-smoke baseline = 21 failed; `UnitTest_NestedGenericRefOut`
      in the failed set (throws `System.Exception` at `if (loc != null) throw`).
- [x] Confirm `UnitTest_NestedGenericRefOut` PASSES under Legacy (not in any
      Legacy failure set; the bug is Neo-only).
- [x] Pin the CIL predecessor of the failing initobj via a temporary
      `case Code.Initobj` diagnostic: the standalone
      `UnitTest_NestedGenericRefOutSub2(string& result)` body has
      `initobj System.String` (TIsValue=False) with `prev = Ldarg_0`.
- [x] Pin the regression-risk shape: `ActivatorCreateInstanceWithArgsTest`'s
      `EqualityComparer<string>.Default.Equals(value, default)` initobj has
      `prev = Ldloca_S` (the folded `default` temp) -- INLINED into the caller
      (inside `inlinestart`/`inlineend`), so it bypasses `Translate`'s
      `case Code.Initobj` entirely.
- [x] Confirm the inliner (`Optimizer.InlineMethod`) copies `OpCodeR` by value
      + register remap only (no Operand4 scrub) -> a marker stamped at
      `case Code.Initobj` on the standalone helper propagates to the inlined
      copy that actually executes.

## 2. IMPLEMENT (DONE, Neo-gated -> Legacy-neutral)
- [x] `JITCompiler.cs`: add const `NeoInitobjByRefOperandMarker = 0x1` (disjoint
      Initobj Operand4 namespace; documented collision-free vs Operand3=RefOffset).
- [x] `JITCompiler.cs` `case Code.Initobj`: under `#if ENABLE_NEO_MODE`, resolve
      T; if `!IsValueType && ins.Previous is ldarg` -> stamp the marker.
- [x] `ILIntepreter.Neo.cs`: add `NeoWriteNullThroughByref(appdomain, frameBase,
      mStack, byrefSlotOffset)` helper (decode `(objIdx,off)`, write null at the
      target; mirrors `Stind_Ref` with `vIdx = -1`; bounds-checked heap arms).
- [x] `ILIntepreter.Neo.cs`: the three Initobj reference-type arms (IL ref,
      unknown CLR, CLR ref) each gate the direct-write behind the marker and call
      the helper when set.

## 3. VERIFY (DONE, truth = full-smoke count)
- [x] Name-filter `UnitTest_NestedGenericRefOut`: PASS (0 failed).
- [x] Stash-toggle (engine files only): stashed -> 1 failed (throws Exception);
      restored -> 0 failed. Proves the fix is load-bearing.
- [x] Regression-risk name-filters: `ActivatorCreateInstanceWithArgsTest` PASS;
      `InheritanceTest20` PASS (marker correctly excludes the ldloca-temp shape).
- [x] NeoStep smoke: 401 ran / 0 failed (no regression).
- [x] FULL SMOKE: 21 -> 20 (delta -1; the canary flips; the 20 survivors are a
      strict subset of the baseline 21 -- no new failures).
- [x] Legacy-neutral: plain `Debug` CLI build = 0 errors (change is entirely
      `#if ENABLE_NEO_MODE`-gated; the runtime helper is in file-gated
      `ILIntepreter.Neo.cs`; the bare public const is harmless).
