## ADDED Requirement: IL value-type construction via newobj

The Neo VM SHALL support construction of an IL value type via the `newobj`
instruction, using the caller's frame byte region (the dest register's slot,
sized and aligned for the value type by the frame allocator) as the construction
site. The `newobj` SHALL zero-initialize the dest region before invoking the
ctor, and the ctor's `this` SHALL be a frame-native Ref Slot so that field
assignments inside the ctor propagate into the caller's frame slot with no
post-ctor copy-back. This is the value-type analog of the Step 8b reference-type
newobj and relies on the Step 17 byref/Ref-Slot model for the ctor `this`.

The detailed contract (frame zero-init, Ref-Slot `this`, ctor writeback, base-
ctor chain, default-ctor and ctor-with-args scenarios) is owned by the
`neo-newobj` capability. This requirement records that IL value-type
`newobj` -- previously an explicit Step-18 deferral / non-goal of this
capability -- is now supported.
