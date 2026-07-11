# spec.md delta - neo-byref (ADDED + MODIFIED)

## ADDED Requirements

### Requirement: IL-direct-Call byref ABI across a frame boundary

A ref or out parameter of an IL-method callee invoked via a DIRECT Call SHALL
be passed across the frame boundary as a byref that resolves to the CALLER
frame when the callee dereferences it. The callee stind/ldind resolution of
a frame-native byref SHALL address the caller frame cell, not the callee
frame at a caller-relative offset. The byref offset SHALL be rebased by the
frame distance so the deref lands in the caller cell, and the write-back
SHALL propagate the possibly-mutated referent back to that caller cell.

The Neo trivial inliner folds small direct targets so that an inlined byref
never crosses a frame. This requirement governs the NON-inlined case where a
target body has an exception handler, is virtual, or exceeds the inline
instruction-count threshold, forcing a real cross-frame Call. The byref ABI
SHALL be correct in that case.

Rationale: the IL-direct-Call byref channel is currently un-rebased and
un-flagged. The optimizer flags a byref param for a CLR callee but not for an
IL callee, so CopyNeoCallArguments copies the caller 8-byte Ref Slot verbatim
into the callee param slot at a caller-relative offset, and
CopyNeoCallThisBack skips the un-flagged slot. The callee then dereferences
the caller-relative offset against its own frameBase, landing in the wrong
cell. The gap is masked today only by the inliner folding every small IL-byref
target. A non-inlinable probe reaches the gap for both primitive and reference
referents.

#### Scenario: primitive ref param write-back across a non-inlined direct Call

- WHEN an IL method passes a `ref int` (or `ref long` / `ref float` / `ref
  double` / `out int`) local to a NON-inlined IL-method direct `Call`, the
  callee reassigns the referent (`x = x + 10`), and the callee returns
- THEN the caller's local SHALL hold the reassigned primitive value (e.g.
  `v == 15`), the byref offset SHALL have been rebased by the frame distance so
  the callee's deref resolved to the caller cell, and the write-back SHALL
  propagate flat bytes into the caller cell (no mStack index, no promotion)

#### Scenario: reference ref param write-back across a non-inlined direct Call

- WHEN an IL method passes a `ref string` (or `ref <class>`) local to a
  NON-inlined IL-method direct `Call`, the callee REASSIGNS the referent
  (`s = s + "!"`), and the callee returns
- THEN the caller's local SHALL hold the callee-created object (e.g.
  `s == "abc!"`), the object SHALL be PROMOTED into a caller-owned mStack slot
  (at index `< callee frameRefBase`) so the index survives the callee's `Ret`
  pop, and the read SHALL NOT throw `Index out of range` or `NullReference`

#### Scenario: inlined direct Call byref is unaffected

- WHEN an IL-method direct `Call` target is FOLDED by the trivial inliner
  (no exception handler, non-virtual, within the instruction-count threshold)
- THEN the byref SHALL stay in-frame (same-frame dispatch), the call SHALL
  produce NO cross-frame byref, and the existing Step-17 byref behavior
  (`Increment`/`AddInto`/`Produce`) SHALL be byte-identical (full `NeoStep`
  smoke stays green)

## MODIFIED Requirements

### Requirement: Cross-frame reference-byref write-back lifetime

A frame-native byref (`objectIndex == -1`) with a reference-type referent SHALL NOT leave a dangling callee-frame mStack index in an outer frame's cell when the byref crosses a frame boundary (the callee runs in a nested `ExecuteNeo` whose `frameBase`/`frameRefBase` differ from the byref's owning frame).
This SHALL hold for BOTH cross-frame channels: the delegate-Invoke channel
(`NeoRunDelegateTargetOnThis` -> nested `ExecuteNeo`) AND the direct-`Call`
channel (`Call` -> `InvokeNeoCallTarget` -> nested `ExecuteNeo`).
The written-back reference object SHALL be PROMOTED into an mStack slot owned by
the frame that observes the byref (the caller frame, i.e. a slot at index
`< caller frameRefBase`), and the caller's frame cell SHALL end up holding an
index into that caller-owned slot. The promotion SHALL run BEFORE the nested
frame's mStack pop (`ExecuteNeo` `Ret` arm truncation) deletes the callee's
frame ref region.

This requirement is the byref-channel analog of the single-reference RETURN
promotion (`ExecuteNeo` `Ret` arm copies `mStack[retSrcIdx]` into the
caller-supplied `retRefBase` slot before the pop). A reference-byref
write-back SHALL enjoy the same lifetime guarantee on BOTH the delegate and
the direct-`Call` channels.

Rationale: the Neo per-frame mStack reservation + `Ret`-arm truncation
(`mStack.RemoveRange(frameRefBase, ...)`) is a load-bearing invariant for the
Neo frame model. Without promotion, a reference-byref write-back stores a
callee-frame index into the caller cell; the callee pop then leaves that index
dangling, and a subsequent dereference (e.g. a CLR binding reading
`mStack[<dead index>]`) throws `Index out of range`. Primitive-byref
write-backs are unaffected (the value is flat bytes, no mStack index).

#### Scenario: reference-byref write-back via a direct Call (not delegate-Invoke)

- WHEN an IL method passes a `ref string` (or `ref <class>`) local to a
  NON-inlined IL-method direct `Call`, the callee REASSIGNS the referent
  (`s = s + "!"`), and the callee returns
- THEN the caller's local SHALL hold the callee-created object (e.g.
  `s == "abc!"`), the object SHALL be PROMOTED into a caller-owned mStack slot
  below the callee's `frameRefBase`, and the read SHALL NOT throw
  `Index out of range` -- the SAME lifetime guarantee the delegate-Invoke
  channel already provides
