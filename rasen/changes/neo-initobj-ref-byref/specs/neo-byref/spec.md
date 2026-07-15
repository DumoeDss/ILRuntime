# neo-byref spec delta -- neo-initobj-ref-byref

## ADDED Requirements

### Requirement: Initobj of a reference type through a byref MUST deref to the target

When the Neo VM executes `initobj` of a REFERENCE type whose operand is a
genuine byref (the CIL shape `ldarg <byref param>; initobj T`, i.e. the
`result = default(T)` lowering on a `ref T result` parameter where T is a
reference type), it SHALL write the `-1` null sentinel to the byref's TARGET,
NOT to the frame slot that holds the byref. The operand slot at `ip->DstOffset`
holds the 8-byte Ref Slot `(objectIndex, offset)`; direct-writing `-1` there
would clobber the `objectIndex` half and silently lose the `default`
assignment (leaving the reference local non-null).

The Neo JIT SHALL discriminate this shape from the addr-alias-FOLDED
`EqualityComparer<T>.Default.Equals(value, default)` stack-temp shape (whose
predecessor is `ldloca`, whose `DstOffset` resolves to the temp itself, and
which MUST keep the direct write) by a JIT-time CIL-producer signal, NOT by
runtime alias/localIsRef inspection: the marker is stamped at CIL emission
(`case Code.Initobj`) when the resolved T is a reference type AND the immediate
CIL predecessor (`ins.Previous`) is an `ldarg` -- a genuine managed pointer
that is never addr-alias-folded. The marker SHALL be a single bit on the
initobj `Operand4` (`NeoInitobjByRefOperandMarker = 0x1`), disjoint from all
other initobj operand stamps (`Operand`=type token, `DstOffset`=slot byte
offset, `Operand3`=RefOffset for the in-frame-VT path). The marker SHALL
survive `LowerNeoOffsets` (the Initobj case sets Operand3/DstOffset only) and
the Neo inliner (`Optimizer.InlineMethod` copies `OpCodeR` by value and only
remaps registers), so it propagates to the inlined copy that actually executes.

At runtime, the Initobj reference-type arm (the IL-ref, unknown-CLR, and
CLR-ref branches) SHALL, when the marker is set, decode the byref
`(objectIndex, offset)` and write null at the target via the same dispatch as
`Stind_Ref` with `vIdx = -1`: frame-native (`objectIndex == -1`) ->
`*(int*)(frameBase + offset) = -1`; heap-IL-ref-field ->
`ILTypeInstance.ManagedObjects[offset] = null`; CLR-object ->
`NeoWriteClrObjectField(target, offset, null)`; Array -> `SetValue(null,
offset)`; caller-owned-slot (bit-30 flag) -> `mStack[objectIndex] = null`. When
the marker is NOT set, the arm SHALL direct-write `-1` to
`frameBase + ip->DstOffset` (HEAD behavior; correct for the folded-temp shape).

#### Scenario: initobj of a reference type through a `ref T` byref param nulls the target

WHEN an IL method `M<T>(ref T result)` with `result = default;` (T specialized
to a reference type, e.g. `string`) is compiled, AND the resulting CIL
`ldarg result; initobj T` is processed by the Neo JIT, THEN the initobj SHALL
carry `NeoInitobjByRefOperandMarker`, AND at runtime the reference local the
byref points at SHALL be set to null (its frame ref slot becomes `-1`),
without corrupting the byref's `objectIndex` half. A subsequent read of that
local SHALL observe null.

#### Scenario: EqualityComparer default(T) folded temp keeps the direct write

WHEN an `EqualityComparer<T>.Default.Equals(value, default)` call lowers to
`ldloca <temp>; initobj T` (the `default` stack temp, `LocalIsReference=false`,
addr-alias-folded so `DstOffset` resolves to the temp itself), THEN the initobj
SHALL NOT carry `NeoInitobjByRefOperandMarker` (predecessor is `ldloca`, not
`ldarg`), AND the runtime arm SHALL direct-write `-1` to the temp slot, matching
HEAD behavior. `ActivatorCreateInstanceWithArgsTest` and `InheritanceTest20`
SHALL continue to PASS.
