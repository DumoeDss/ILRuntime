## 1. Reproduce + baseline (confirm the gate before the fix)

- [x] 1.1 Add the F-7 byref probes to `TestCases/NeoStep19Test.cs`: a custom
      `delegate void NeoStep19RefIntDelegate(ref int x)` + target
      `BumpRef(ref int x){x+=10;}` invoked as `d(ref v)` with `v==5`, asserting
      `v==15`. The `out int` variant (`SetOut(out int x){x=99;}` -> `v==99`) and
      the `ref string` variant (ReadLength -> length). Renamed the misleadingly-
      named `NeoStep19_RefOutParam` to `NeoStep19_PlainIntParam` (it used a plain
      `int`, not a byref).
- [x] 1.2 Added a multicast-with-byref probe: a 2-target multicast
      `NeoStep19RefIntDelegate` (BumpRef + DoubleRef) over `v==5` asserting `v==30`.
- [x] 1.3 Confirmed on HEAD the byref probes FAIL with `Index out of range` /
      `Object reference not set` (the byref half-read through object[] + separate-
      interpreter destruction). The plain `NeoStep19_*` probes stayed green.

## 2. The same-frame delegate-Invoke fast path (the fix)

- [x] 2.1 In `ILIntepreter.Neo.cs`, the `Callvirt_IL` `IsDelegateInvoke` branch:
      when `delThis is DelegateAdapter dAdapter` AND `dAdapter.Method` is an
      `ILMethod`, take the same-frame fast path via the new helper
      `NeoRunDelegateTargetOnThis`. The `ReadNeoDelegateInvokeArgs`/`NeoInvokePublic`
      path stays as the fallback for a non-IL target (D5).
- [x] 2.2 D2: for an instance target, overwrite the adapter mStack slot (stored at
      `targetBase+0` by the Invoke map) with the bound `instance`, so the target's
      `this` is the bound object. Skipped for a static target.
- [x] 2.3 The byref-source snapshot (Step-20 clobber guard) is NOT needed on the
      fast path: an IL callee's byref is unflagged, so the byref is copied VERBATIM
      (8 bytes) and `CopyNeoCallThisBack` is a no-op for IL byrefs (it gates on
      `PrimitiveByRefSrc`, null for IL callees). The write-back is live by
      construction (see 2.5).
- [x] 2.4 The head target runs on `this` via
      `InvokeNeoCallTarget(dTargetIlm, false, dTargetBase, ...)`; unhandled
      exceptions propagate (return null), same as the normal call path.
- [x] 2.5 BYREF RELATIVIZATION (the key correction to the original design): the
      verbatim-copied frame-native byref `(objectIndex==-1, off)` carries an offset
      RELATIVE TO THE CALLER's frame. The target's stind/ldind resolve objectIndex
      ==-1 against the TARGET's frame base (above the caller's), so the raw offset
      addresses the wrong cell. `NeoRunDelegateTargetOnThis` RE-BASES each
      frame-native byref param's offset by `-(dTargetBase - callerFrameBase)` so it
      resolves back to the caller's frame cell -- the target's mutation then lands
      in the caller's frame and the write-back is live (no CopyNeoCallThisBack
      needed). mStack-object byrefs (objectIndex>=0) address mStack absolutely and
      need no rebase. The rebase is undone after the run so multicast re-invocation
      does not compound the subtraction. (The original design's premise -- that
      stind resolves against the caller frame -- was wrong; it appears to work in
      the Step-17 tests only because those small IL targets are INLINED by the
      optimizer, so the byref never crosses a frame. A delegate-Invoke target
      cannot be inlined -> first real cross-frame IL byref -> relativization is
      required.)
- [x] 2.6 Multicast: `NeoRunDelegateTargetOnThis` is called per next-chain target
      (same `targetBase`, same write-back). The rebase/restore per call keeps the
      raw offset stable. Last target's return wins (Legacy ILInvokeSub semantics).
- [x] 2.7 `InvokeNeoCallTarget` writes the head/last target's return into
      `retDstPtr` directly (`WriteNeoDelegateInvokeReturn` is not used on the fast
      path).

## 3. Verify the gate (the F-7 success criterion)

- [x] 3.1 The `ref int`/`out int`/multicast byref probes PASS with the write-back
      OBSERVABLE (v==15, v==99, v==30). No `ArgumentOutOfRangeException`.
- [x] 3.2 The write-back is OBSERVABLE -- the caller's local reflects the callee's
      mutation (the probes divide by zero on a wrong value, and they pass).
- [x] 3.3 Regression: full `NeoStep` smoke 233/0/0 (was 229 + the 4 new byref
      probes). All plain-primitive delegate shapes (`NeoStep19_StaticAction`/
      `StaticFunction`/`InstanceMethod`/`VirtualMethod`/`MulticastCombine`/
      `MulticastRemove`/`ClrCallback`/`PlainIntParam`/`ClosureOverThis`) stay green.
      NeoStep19 14/0/0.

## 3b. ref-string scope (FOLLOW-UP -- not blocking F-7)

- [x] 3b.1 The `ref string` byref marshals across the delegate boundary and the
      callee READS it correctly (probe `NeoStep19_ByRef_String`: `ReadLength(ref s)`
      returns `s.Length`==3). This proves the F-7 byref-marshal criterion for a
      reference-type param.
- [ ] 3b.2 FOLLOW-UP (neo-f7 / ref-type-byref-writeback): an in-place WRITE-BACK of
      a callee-CREATED reference object (`s = s + "!"`) leaves the caller's slot
      with a DANGLING mStack index -- the new object lands in the callee's frame
      ref region, which `ExecuteNeo` pops on return (`ILIntepreter.Neo.cs:4703`).
      Ref-type byref write-back of a callee-created object needs mStack lifetime
      promotion (store the new object in a caller-frame ref slot before writing the
      index back, or convert the frame-native byref-to-ref-slot into an mStack-slot
      byref). Primitive-byref write-back is unaffected (the value is written as
      flat bytes to the caller's frame, no mStack index).

## 4. Spec sync + cleanup

- [x] 4.1 The `neo-dispatch` delta spec scenarios match: ref int, out int,
      multicast, ref-string-marshal (read), plain-primitive regression guard,
      Legacy-neutrality. The ref-string IN-PLACE write-back of a callee-created
      object is documented as a follow-up (3b.2) rather than the mStack-object-
      referent scenario originally assumed.
- [x] 4.2 Update `.trae/documents/neo-deferred-items.md` F-7 / NEO-DELEGATE-REFOUT
      entry: marked RESOLVED (primitive-byref by the same-frame fast path +
      byref-relativization), cross-referenced the change, and ADDED a new deferred
      row F-7B for ref-type-byref-writeback (3b.2). Design.md also records the
      byrelativization correction + the Step-17-inlining finding as durable notes.
- [ ] 4.3 (Optional, deferred) `ReadNeoDelegateInvokeArgs`/`WriteNeoDelegateInvokeReturn`
      remain as the non-IL-target fallback (D5). A later cleanup can unify if the
      fast path proves uniform.
