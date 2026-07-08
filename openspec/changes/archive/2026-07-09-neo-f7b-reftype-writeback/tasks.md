# Tasks - neo-f7b-reftype-writeback (F-7B TRUE COMPLETION)

> Planner-isolated. The reproduction is DONE (the binding probe
> `NeoStep19_ByRef_StringWriteBack` + `AppendBang` are already in
> `TestCases/NeoStep19Test.cs` and FAIL 1/1 on HEAD `12d9e809`). The apply
> stage implements the D1a promotion. Build/test commands ALWAYS use
> `-f net8.0`.

## 1. Reproduction (DONE by planner -- verified STILL FAIL-on-HEAD before edit)

- [x] `NeoStep19_ByRef_StringWriteBack` + `AppendBang(ref string s)` added to
      `TestCases/NeoStep19Test.cs` (caller asserts `r == 4 && s == "abc!"`).
- [x] Reproduced 1/1 FAIL on HEAD `12d9e809`: `Index was out of range` in
      `System_String_Binding.op_Inequality_3_Neo` (line 348) -- the caller's
      `s` holds a dangling mStack index after the delegate call.
- [x] **Apply-stage gate 0 (PASSED):** before any source edit, rebuilt TestCases
      + Debug_Neo CLI and re-ran `NeoStep19_ByRef_StringWriteBack`; confirmed it
      STILL fails with the OOB (`Index was out of range ... List`1.get_Item`,
      `1 tests failed`). Reproduction verified on the apply HEAD.

## 2. Implement the D1a promotion (reference-byref -> caller-owned mStack slot)

- [x] 2.1 In `NeoRunDelegateTargetOnThis` (`ILIntepreter.Neo.cs:668-...`), for
        each frame-native byref param (`objectIndex == -1`) whose referent is a
        REFERENCE type (`!IsPrimitive && !IsValueType` -- recovered via
        `pt.ElementType` of the byref param; ILType sets `byRefType.elementType`
        = the referent at construction), convert the byref in `targetBase` from
        the frame-native form to an mStack-slot form targeting a CALLER-owned
        mStack slot, INSTEAD of the primitive-byref offset rebase (the rebase
        stays for primitive/value byrefs).
- [x] 2.2 The caller-owned slot is reserved ONCE per delegate-Invoke via a new
        `ref int callerOwnedRefSlot` param (initialized `-1` at the call site,
        `Callvirt_IL` `IsDelegateInvoke` branch) and SHARED across the multicast
        chain (head + each `Next`). It is reserved with `mStack.Add(null)` BEFORE
        `InvokeNeoCallTarget` -> the nested `ExecuteNeo` reserves its
        `frameRefBase = mStack.Count` ABOVE it, so the slot survives the callee
        `Ret` pop. Initialized with the byref's CURRENT referent (the caller cell
        at `callerFrameBase + origOff` holds the referent's mStack index; that
        object is copied into the caller-owned slot) so the READ path observes
        the entry value. (No fresh-slot branch for a temp source was needed: the
        single byref slot for a delegate signature is always a named local in the
        binding reproduction; the reservation is uniform -- `mStack.Add` --
        regardless of source.)
- [x] 2.3 Encoded the mStack-slot-byref with the F-10 high-bit discriminator
        `JITCompiler.NeoF10ByrefOffsetFlag = 0x40000000` on the OFFSET half:
        the byref is rewritten to `(objectIndex = callerOwnedRefSlot, offset =
        NeoF10ByrefOffsetFlag)`. Disjoint from real field hashes (CLR-object
        mStack byref), array indices, and Primitives byte offsets -- all well
        below 2^30.
- [x] 2.4 Taught the `Stind_Ref` mStack-object dispatch (`Stind_Ref`, the
        `objIdx >= 0` branch) a new FIRST sub-arm keyed on `(off &
        NeoF10ByrefOffsetFlag) != 0`: it stores the value's object directly into
        `mStack[objIdx]` (the caller-owned slot). After the run,
        `NeoRunDelegateTargetOnThis` re-stamps the caller cell at
        `callerFrameBase + origOff = callerOwnedRefSlot` so the caller's
        subsequent read resolves to the surviving slot (parity with the
        single-reference RETURN promotion).
- [x] 2.5 Taught the `Ldind_Ref` mStack-object dispatch (`Ldind_Ref`, the
        `objIdx >= 0` branch) the matching sub-arm: it reads
        `mStack[objIdx]` (the caller-owned slot's current object) and
        materializes it into the callee's dest ref slot for the READ path.
- [x] 2.6 The conversion is UNDONE / idempotent for multicast: after each run
        the raw frame-native byref is restored in `targetBase` (the rewrite
        targets a stable caller slot, not a frame-distance-dependent offset),
        and `callerOwnedRefSlot` persists across the chain so each subsequent
        target re-promotes into the SAME slot and reads the prior target's
        object (D3, last write wins).

## 3. Probes (the binding probe + the multicast adversarial SHIPPED)

- [x] 3.1 `NeoStep19_ByRef_StringWriteBack` (DONE -- the binding probe; caller
        asserts `r == 4 && s == "abc!"`). FAIL-on-HEAD -> PASS.
- [x] 3.2 Multicast-with-ref-string: `NeoStep19_ByRef_StringMulticastWriteBack`
        -- `delegate void D(ref string s)` with `AppendBangV` (`s = s + "!"`)
        then `AppendQ` (`s = s + "?"`) over "abc" -> "abc!?" (D3, last write
        wins; exercises the `callerOwnedRefSlot` sharing across the chain).
        SHIPPED.
- [~] 3.3 Temp-source byref: SKIPPED -- the single byref slot for a delegate
        signature is always a named local in the binding reproduction; the
        reservation (`mStack.Add`) is source-shape-agnostic (works for any
        frame-native byref source). Recorded as a non-load-bearing sequencing
        note; a `ldloca`-of-temp probe can be added if a temp-source delegate
        shape surfaces.
- [~] 3.4 Disjoint-shape guard: the F-10 discriminator is byte-identical in
        semantics to the established `NeoF10ByrefOffsetFlag` idiom (Stfld_Ref /
        Ldfld_Ref / Ldflda all key on the same high bit). The
        `NeoStep17_*` CLR-object-field-byref probes (the existing mStack-object
        field-address shape) stay green (full NeoStep 235/0/0), proving the
        `Stind_Ref`/`Ldind_Ref` sub-arms dispatch on the flag WITHOUT colliding
        with the Array / CLR-object / IL-instance arms. A dedicated
        `ref h.refField`-through-delegate probe is non-load-bearing (the
        mStack-object shape never crosses the delegate-Invoke fast path -- it is
        already absolute); recorded as a sequencing note.

## 4. Regression (MUST stay green -- ALL GREEN)

- [x] 4.1 `NeoStep19_ByRef_Int` / `_Out` / `_Multicast` (F-7 primitive-byref)
        stay green (D2: no promotion for primitives; the primitive rebase path
        is byte-identical to F-7).
- [x] 4.2 `NeoStep19_ByRef_String` (marshal+READ path, `ReadLength(ref s)`)
        stays green (the READ path now routes through the caller-owned slot and
        observes the same entry object).
- [x] 4.3 Full `NeoStep` smoke: **235/0/0** (was 233; +2 = the writeback probe
        + the multicast-writeback probe). No regression.
- [x] 4.4 Legacy-neutral: the `ILRuntime` library (containing ALL edits) builds
        0 errors in plain `Debug` (Legacy). All edits are Neo-gated
        (`#if ENABLE_NEO_MODE`). (The 4 errors in the plain-`Debug`
        `ILRuntimeTestCLI` build are PRE-EXISTING `/unsafe`-flag issues in
        `ILRuntimeTestCLI/NeoF13NestedProbe.cs`, unrelated to F-7B -- confirmed
        by the clean library build.)

## 5. Sequencing notes (NOT shipped in this change -- RECORDED)

- [x] 5.1 DOCUMENTED in `neo-deferred-items.md` (F-7B row RESOLVED + a NEW row
        for the direct-Call sibling): the direct `Call_IL` /
        `CopyNeoCallThisBack` path has the SAME dangling-index mechanism for a
        cross-frame `ref <reference-type>` reassign; unreachable today because
        the Neo trivial inliner folds small direct targets. Sequenced behind a
        non-inlinable direct-call probe + the analogous promotion in
        `CopyNeoCallThisBack` (copy the OBJECT into the caller's ref slot, not
        the index).
- [x] 5.2 DOCUMENTED the value-type-byref-reassign (`ref struct` with reference
        fields) case as out of scope (Step 17 territory; not reachable by
        current tests).

## 6. Ship

- [x] 6.1 `openspec validate neo-f7b-reftype-writeback --strict` passes
        (apply stage: `Change 'neo-f7b-reftype-writeback' is valid`, exit 0).
- [ ] 6.2 Ship log written; deferred-items F-7B row marked RESOLVED with the
        promotion mechanism + the sequenced direct-Call sibling recorded (apply
        stage marked the row RESOLVED + added the direct-Call sibling row; the
        ship log is the LEAD's ship stage).
