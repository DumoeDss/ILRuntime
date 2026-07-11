## ADDED Requirement: Byref `this` for value-type constructor invocation

The byref call-ABI SHALL additionally serve as the `this` parameter of an IL
value-type constructor invoked via `newobj`. The `newobj` arm SHALL write a
frame-native Ref Slot `(-1, destFrameByteOffset)` into the ctor's callee param
region `this` slot (param slot 0), and the ctor SHALL treat that `this` as a
managed pointer to the caller's frame slot. Field assignments in the ctor
(`this.field = ...`) SHALL resolve through the existing `stind_*`/`stfld` /
`_Inline` machinery into the caller's dest region, exactly as an explicit
byref/out parameter would.

This is a new caller of the byref call-ABI (a byref `this`), not a change to the
ABI itself. The detailed newobj-side contract is owned by the `neo-newobj`
capability; this requirement records that the byref model additionally covers
the value-type-ctor `this` (previously byref `this` was not exercised by
newobj).
