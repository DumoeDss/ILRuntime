# Tasks - neo-f7b-sib-direct-call (F-7B-SIB triage, REACHED)

> Planner triage. The reproduction is DONE (three probes added to
> `TestCases/NeoStep19Test.cs`, all FAIL on HEAD `243c8a73`; the inliner defeat
> is CONFIRMED via the JIT dump). The apply stage implements the fix (D1 JIT
> flagging + D2 runtime rebase/promotion). Scope is MEDIUM (broader than the
> SMALL F-7B-style promotion the planning context assumed -- see design.md).
> Build/test commands ALWAYS use `-f net8.0`.

## 1. Reproduction (DONE by planner -- verified FAIL-on-HEAD)

- [x] 1.1 `NeoStep19_SIB_DirectCall_TryCatch` + `AppendBangBig(ref string s)`
      added (defeats the inliner via `hasExceptionHandler`; caller asserts
      `r == 4 && s == "abc!"`).
- [x] 1.2 `NeoStep19_SIB_DirectCall_BigBody` + `AppendBangLong(ref string s)`
      added (defeats the inliner via instruction count > 20; caller asserts
      `r == 4 && s == "abc!"`).
- [x] 1.3 `NeoStep19_SIB_DirectCall_PrimitiveRef` + `BumpIntBig(ref int x)`
      added (the scope-discriminating primitive probe; defeats via
      `hasExceptionHandler`; caller asserts `v == 15`).
- [x] 1.4 Reproduced 3/3 FAIL on HEAD `243c8a73`:
      - TryCatch: `s == "abc"`, `r == -1` (catch swallowed an inner throw).
      - BigBody: `Neo callvirt this is null` at `s.Length` inside the callee.
      - PrimitiveRef: `v == 5` (unchanged; should be `15`).
- [x] 1.5 Confirmed the inliner is DEFEATED for BOTH reference probes: the JIT
      dump shows a REAL `call r1, r6, AppendBangBig|AppendBangLong` in the
      caller body (NOT inlined) + a separate callee body.
- [x] 1.6 Diagnostic dump added to the `Call` case, run, captured the map shape
      (`entry[0] size=8 src=24 dst=0 plain [objIdx=-1 off=0]`), then REVERTED.
      Source `ILIntepreter.Neo.cs` verified CLEAN (`git diff --stat` empty).
- [x] 1.7 Root cause isolated: IL-callee byref params are UN-FLAGGED
      (`Optimizer.Neo.cs:1275` `clrParams == null` for IL -> `dstIsByRefParam`
      never set), copied VERBATIM (caller-relative offset), and dereffed against
      the CALLEE frame. See design.md Facts 1-3.
- [x] 1.8 **Apply-stage gate 0 (REQUIRED before any source edit):** rebuilt
      TestCases + Debug_Neo CLI on the apply HEAD `243c8a73`; ran the three
      probes; confirmed they STILL fail with the SAME signatures:
      TryCatch -> DivideByZeroException (the `s != "abc!"` assertion fires),
      BigBody -> `NullReferenceException: Neo callvirt this is null` (dangling
      index), PrimitiveRef -> DivideByZeroException (the `v != 15` assertion).
      Reproduction re-verified on the apply HEAD before any source edit.

## 2. D1 -- flag IL-callee direct-Call byref params (JIT) -- PIVOTED to runtime-only (D2)

> **APPLY-STAGE DEVIATION (documented, honors the design's GOALS + the delegate-
> path mechanism over its D1 letter).** D1 as written (flag the IL-callee byref
> param in `Optimizer.Neo.cs` so `CopyNeoCallArguments` DEREFs and
> `CopyNeoCallThisBack` writes back) is the **CLR-callee model**, which does NOT
> fit an IL callee. The IL callee CONSUMES a byref -- its frame layout
> (`AllocateLocalStackSpaces` / `AllocateSlotForType`, `JITCompiler.cs:1883-
> 1892`) sizes a byref param as an 8-byte Ref Slot and its `stind_*`/`ldind_*`
> DEREF that byref at runtime. Flagging `dstIsByRefParam` would make
> `CopyNeoCallArguments` (`ILIntepreter.Neo.cs:340-348`) DEREF the byref and
> copy flat referent bytes into the 8-byte slot -- CORRUPTING the byref the
> callee needs. The AUTHORITATIVE mechanism to port -- the delegate path
> (`NeoRunDelegateTargetOnThis`, which is GREEN for F-7/F-7B) -- does NOT flag
> the byref in the map at all; it copies the byref VERBATIM (the param is
> `plain` because the IL callee is unflagged) and fixes it up at RUNTIME
> (re-base offset for primitive/value, promote to caller-owned slot for
> reference). So D1 is REPLACED by a runtime-only helper (D2 below). This
> ELIMINATES the HIGH-risk JIT gate entirely: NO JIT change -> NO possible
> inlined-Step-17 regression by construction. The structural proof: an inlined
> direct target is folded by the JIT inliner (`JITCompiler.cs:2236-2274`,
> `Optimizer.InlineMethod`) and NEVER emits a `Call` opcode, so it never
> reaches the `Call` case where the runtime fixup runs.

- [N/A] 2.1 ~~Flag IL-callee byref in `Optimizer.Neo.cs`.~~ REJECTED (would make
        CopyNeoCallArguments deref the byref the IL callee needs verbatim). See
        the deviation note above.
- [N/A] 2.2 ~~Element-typed dest-slot sizing for IL byref.~~ REJECTED (the IL
        callee frame ALREADY sizes a byref param as an 8-byte Ref Slot at
        `JITCompiler.cs:1889`; resizing to element-typed would corrupt the
        callee's byref read). Unchanged.
- [x] 2.3 **gate (HIGH-risk regression):** no JIT change shipped, so the
        inlined Step-17 byref cases are byte-identical BY CONSTRUCTION. Verified
        empirically: `NeoStep17` smoke = 47/47 green; full `NeoStep` smoke =
        238/238 green. The HIGH-risk gate is satisfied trivially (the risk was
        scoped to a JIT change that was not made).

## 3. D2 -- pre-call rebase (primitive) + promotion (reference) in the Call case (DONE, runtime-only)

- [x] 3.1 Added `NeoPreCallByrefFixup` helper (after `InvokeNeoCallTarget`,
        `ILIntepreter.Neo.cs` ~`:642`), a direct-`Call` specialization of
        `NeoRunDelegateTargetOnThis`'s byref loop (`:711-807`). It reads the IL
        callee's params from `target.Parameters` + `target.CompiledFrame.ParamInfos`
        (same sources the delegate path uses), iterates the byref params, and
        returns the pre-call bookkeeping via out params. Wired in the `Call`
        case between `CopyNeoCallArguments` and `InvokeNeoCallTarget` (gated on
        `targetMethod is ILMethod`).
- [x] 3.2 PRIMITIVE/value-byref: re-base the offset in `targetBase` by
        `frameDist = targetBase - frameBase` (the F-7 relativization). The
        callee's `objIdx == -1` deref resolves to the caller cell; the mutation
        lands in-frame as flat bytes and survives the callee Ret pop. NO
        `CopyNeoCallThisBack` write-back needed for this channel (the rebase
        makes the callee write directly to the caller cell -- same as the
        delegate path). Undo after the run restores the caller-relative offset.
- [x] 3.3 REFERENCE-byref: PROMOTE into a caller-owned mStack slot (reserved via
        `mStack.Add(null)` BEFORE `InvokeNeoCallTarget` -> the callee's
        `frameRefBase` sits above it), rewrite the byref to `(callerSlot,
        JITCompiler.NeoF10ByrefOffsetFlag)`, and after the run stamp the caller
        cell to `callerSlot`. Reuses the EXISTING `Stind_Ref`/`Ldind_Ref`
        `NeoF10ByrefOffsetFlag` sub-arms -- NO new dispatch arms. The post-run
        stamp + targetBase restore are inline in the `Call` case.
- [x] 3.4 A direct `Call` has a single invocation (no multicast chain); the
        caller-owned slot is a helper-local (not cross-invocation shared). The
        rebase/rewrite undo runs once after the call (idempotent; harmless).

## 4. Probes (the three triage probes become regression probes -- ALL PASS)

- [x] 4.1 `NeoStep19_SIB_DirectCall_TryCatch` FAIL-on-HEAD -> PASS (reference
        byref via exception-handler defeat; caller now observes `s == "abc!"`).
- [x] 4.2 `NeoStep19_SIB_DirectCall_BigBody` FAIL-on-HEAD -> PASS (reference
        byref via instruction-count defeat; caller now observes `s == "abc!"`).
- [x] 4.3 `NeoStep19_SIB_DirectCall_PrimitiveRef` FAIL-on-HEAD -> PASS
        (primitive byref -- the scope-discriminating case; caller observes
        `v == 15`).
- [ ] 4.4 (Optional adversarial) an IL callee with a byref param AND a non-byref
        param. NOT added -- the helper iterates the WHOLE param list and only
        touches `IsByRef` params (a non-byref param is skipped), so a mixed
        signature is correct by construction. Left as a sequencing note (the
        `NeoStep19` 19/19 + full `NeoStep` 238/238 smoke cover the regression).

## 5. Regression (MUST stay green -- ALL GREEN)

- [x] 5.1 The inlined Step-17 byref cases (`NeoStep17_Increment` / `_AddInto` /
        `_Produce` / `_PassForward` / ...) stay byte-identical green. No JIT
        change shipped (D1 pivoted to runtime-only), so the inlined codegen is
        untouched BY CONSTRUCTION. `NeoStep17` smoke = 47/47 green.
- [x] 5.2 The F-7 / F-7B delegate-byref cases (`NeoStep19_ByRef_Int` / `_Out` /
        `_Multicast` / `_String` / `_StringWriteBack` /
        `_StringMulticastWriteBack`) stay green (the delegate path
        `NeoRunDelegateTargetOnThis` is UNCHANGED). `NeoStep19` smoke = 19/19
        green (16 delegate cases + 3 new SIB probes).
- [x] 5.3 Full `NeoStep` smoke: green = 238/238, 0 failed (the F-7B 235 baseline
        + the 3 SIB probes). No regression.
- [x] 5.4 Legacy-neutral: the `ILRuntime` library builds 0 errors in plain
        `Debug` (Legacy). All edits are Neo-only (the `NeoPreCallByrefFixup`
        helper + the `Call`-case wiring are both inside `ILIntepreter.Neo.cs`,
        which is entirely `#if ENABLE_NEO_MODE`; the `JITCompiler.NeoF10ByrefOffsetFlag`
        reference is to an unconditional const).

## 6. Sequencing notes (NOT shipped in this change -- RECORDED unless apply ships)

- [ ] 6.1 DOCUMENT in `neo-deferred-items.md`: the F-7B-SIB / direct-Call sibling
        row is RESOLVED (the F-7 rebase + F-7B promotion ported to the direct
        `Call` case via a runtime-only helper). The LEAD's ship stage owns this
        update.
- [ ] 6.2 DOCUMENT a `virtual`-defeat probe (defeat condition 1, not exercised
        by the triage probes) as a non-load-bearing sequencing note -- the
        try/catch and size defeats suffice to REACH. (A `virtual` IL byref target
        defeats the inliner via `ilm.IsVirtual` and reaches the same `Call`-case
        fixup; the helper is opcode-agnostic so it should already handle it, but
        no probe confirms it.)
- [ ] 6.3 DOCUMENT the value-type-byref-reassign (`ref struct` with reference
        fields) case as out of scope (Step 17 territory). The helper's
        `!elemType.IsValueType` gate routes a value-type-byref to the F-7
        primitive rebase path (flat bytes), which is correct for a ref-struct
        WITHOUT reference fields but may not fully handle a ref-struct WITH
        reference fields (the index+ref sub-slot span) -- unchanged from the
        delegate path's documented limitation.
- [ ] 6.4 DOCUMENT the multi-byref direct-Call limitation: the helper processes
        the FIRST byref param and breaks (matching the delegate path's one-byref
        limitation). A `Foo(ref int a, ref int b)` direct call would only fix up
        `a`. Not reached by any current test; recorded for a follow-up if a real
        multi-byref non-inlinable target surfaces.

## 7. Ship (apply / LEAD stage)

- [ ] 7.1 `openspec validate neo-f7b-sib-direct-call --strict` passes. (The
        LEAD's ship stage owns this; the apply stage's source + tasks.md are
        complete.)
- [ ] 7.2 Ship log written; deferred-items row updated (RESOLVED -- the F-7 +
        F-7B promotion ported to Call). The LEAD's ship stage owns this.

## 8. Apply-stage implementation notes (landed 2026-07-09)

**Files changed (Neo-only, Legacy-neutral):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`:
  - NEW helper `NeoPreCallByrefFixup` (after `InvokeNeoCallTarget`, ~`:642`) --
    a direct-`Call` specialization of `NeoRunDelegateTargetOnThis`'s byref loop
    (`:711-807`). Two channels: F-7 offset rebase (primitive/value byref) + F-7B
    caller-owned-slot promotion (reference byref). Returns pre-call bookkeeping
    via out params.
  - `Call` case (`:2300-2360`): wired the helper between `CopyNeoCallArguments`
    and `InvokeNeoCallTarget` (gated on `targetMethod is ILMethod`), and the
    post-run stamp (reference: stamp caller cell to caller-owned slot) + undo
    (restore targetBase to the raw caller-relative byref) after the run.

**The D1 -> D2 pivot (durable finding):** the design's D1 (JIT flagging of the
IL-callee byref param) assumed the CLR-callee deref model. An IL callee CONSUMES
a byref (its frame sizes it as an 8-byte Ref Slot; its stind/ldind deref it), so
flagging `dstIsByRefParam` would make `CopyNeoCallArguments` deref-and-flatten
the byref -- corrupting the callee's byref. The delegate path (the AUTHORITATIVE
working mechanism) copies the byref VERBATIM and fixes it up at runtime. The
apply stage ported THAT (runtime-only), which (a) is correct for an IL callee,
(b) eliminates the HIGH-risk JIT gate entirely (no JIT change), and (c) is the
faithful mirror of the proven F-7/F-7B delegate mechanism. The design's GOALS
(3 probes pass, Step-17 byte-identical, delegate path unchanged, full smoke
green) are all met.

**Build-cache note:** `Debug_Neo` CLI rebuild + `TestCases` Debug rebuild both
ran clean (outputs newer than sources); the gate-0 FAIL signatures and the
post-fix PASS were both observed on fresh builds.
