# spec.md delta - neo-byref (ADDED + MODIFIED)

## ADDED Requirements

### Requirement: Cross-frame reference-byref write-back lifetime

A frame-native byref (`objectIndex == -1`) with a reference-type referent SHALL NOT leave a dangling callee-frame mStack index in an outer frame's cell when the byref crosses a frame boundary (the callee runs in a nested `ExecuteNeo` whose `frameBase`/`frameRefBase` differ from the byref's owning frame).
The written-back reference object SHALL be PROMOTED into an mStack slot owned by
the frame that observes the byref (the caller frame, i.e. a slot at index
`< caller frameRefBase`), and the caller's frame cell SHALL end up holding an
index into that caller-owned slot. The promotion SHALL run BEFORE the nested
frame's mStack pop (`ExecuteNeo` `Ret` arm truncation) deletes the callee's
frame ref region.

This requirement is the byref-channel analog of the single-reference RETURN
promotion (`ExecuteNeo` `Ret` arm copies `mStack[retSrcIdx]` into the
caller-supplied `retRefBase` slot before the pop). A reference-byref
write-back SHALL enjoy the same lifetime guarantee.

Rationale: the Neo per-frame mStack reservation + `Ret`-arm truncation
(`mStack.RemoveRange(frameRefBase, ...)`) is a load-bearing invariant for the
Neo frame model. Without promotion, a reference-byref write-back stores a
callee-frame index into the caller cell; the callee pop then leaves that index
dangling, and a subsequent dereference (e.g. a CLR binding reading
`mStack[<dead index>]`) throws `Index out of range`. Primitive-byref
write-backs are unaffected (the value is flat bytes, no mStack index).

#### Scenario: reference-byref write-back of a callee-created object

- WHEN an IL method obtains a frame-native byref to a reference-typed local
  (e.g. `string s = "abc"; ... ref s`), passes that byref across a frame
  boundary to a callee that REASSIGNS the referent (`s = s + "!"`), the callee
  returns, and the caller reads the local
- THEN the caller's local SHALL hold the callee-created object (e.g.
  `s == "abc!"`), the local's mStack index SHALL resolve to a slot below the
  callee's `frameRefBase` (i.e. the index survived the callee pop), and the
  read SHALL NOT throw `Index out of range`

#### Scenario: reference-byref READ then WRITE across a frame

- WHEN a callee first READS a reference-typed byref (e.g. returns
  `s.Length`) and a SEPARATE call WRITEs a new object through the byref
- THEN the read path SHALL observe the object present at call entry, and the
  write path SHALL promote the new object into the caller-owned slot; the two
  paths SHALL be consistent (the caller-owned slot is the single source of
  truth for the byref referent across the call)

#### Scenario: primitive-byref write-back is unaffected

- WHEN a callee writes a primitive value (`ref int`, `out int`, `ref long`,
  `ref float`, `ref double`) through a cross-frame frame-native byref
- THEN the write-back SHALL store flat bytes directly into the caller's frame
  cell (no mStack index, no promotion), matching the F-7 primitive-byref
  behavior byte-for-byte; the primitive value SHALL be observable after the
  call with NO dangling index

## MODIFIED Requirements

### Requirement: stind / ldind / stobj / ldobj dispatch

The `stind`/`ldind`/`stobj`/`ldobj` dispatch over a Ref Slot SHALL handle a
reference-typed frame-native byref that crosses a frame boundary by addressing
a CALLER-OWNED mStack slot for the object, so that a write stores the object
into a slot the callee pop does not delete and a read observes a stable index.
The dispatch SHALL distinguish this cross-frame reference-byref shape from the
existing mStack-object field-address shape (a byref whose `objectIndex >= 0`
and whose `offset` is a field hash or a Primitives byte offset) via an
encoding discriminator disjoint from real field hashes and Primitives offsets
(e.g. a high-bit flag on the `offset` half, mirroring the F-10
`NeoF10ByrefOffsetFlag` discriminator idiom). Primitive and value-type
frame-native byrefs SHALL keep their existing dispatch UNCHANGED.

#### Scenario: stind_ref write through a cross-frame reference byref

- WHEN a `stind.ref` executes on a reference-typed byref that was converted
  to the cross-frame caller-owned-slot form before a nested `ExecuteNeo`
- THEN the stored value's object SHALL be copied into the caller-owned mStack
  slot and the caller's frame cell SHALL hold the caller-owned slot index;
  the stored index SHALL be valid after the nested frame's `Ret` pop

#### Scenario: ldind_ref read through a cross-frame reference byref

- WHEN a `ldind.ref` executes on the same cross-frame reference-byref form
- THEN the read SHALL observe the object in the caller-owned mStack slot,
  materializing it into the callee's dest ref slot for the callee's use,
  WITHOUT introducing a dangling index into the caller cell
