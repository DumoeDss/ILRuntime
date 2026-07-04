# neo-value-types (delta)

## MODIFIED Requirements

### Requirement: addrAlias folding is conditional on consumer foldability

The optimizer's `addrAlias` folding (which lets the in-frame-VT field-access
fast path resolve `ldloca`/`ldflda` address chains to compile-time absolute
frame offsets) SHALL remain in effect for any address-producing dest whose
EVERY consumer is a foldable opcode (`_Inline` field opcodes, `Initobj`, or
another foldable `ldflda`). The folding SHALL NOT apply to a dest when any
consumer is a byref-escape opcode (`stind_*`, `ldind_*`, `stobj`, `ldobj`,
`ldelema`, a `Call`/`Newobj`/`Push` argument whose declared parameter
`IsByRef`, or a `constrained.` box path); such a dest is realized as a real
Ref Slot under the `neo-byref` capability. The `ldloca`/`ldflda` runtime arms
SHALL therefore be real Ref-Slot producers, and the no-op-at-runtime behavior
SHALL apply only to dests the folding resolved.

This MODIFIES the prior phrasing (Step 12) under which `ldloca`/`ldflda` were
unconditionally runtime no-ops because no genuine byref path existed. The
in-frame-VT field-access fast path itself (Steps 12-16) is unchanged for the
pure `ldloca;ldflda;stfld/ldfld/initobj` pattern.

#### Scenario: pure inline field access stays zero-overhead
- WHEN `ldloca V; stfld/ldfld _Inline` accesses an in-frame VT field and no
  byref-escape consumer exists
- THEN the dest stays in the `addrAlias` map, the inline opcode uses the folded
  absolute offset, and the `ldloca` arm is dead at runtime (unchanged from
  Steps 12-16).

#### Scenario: address escapes the folding window
- WHEN the same `ldloca V` also feeds a byref `Call` argument or a `stind`
- THEN the dest is removed from the `addrAlias` map and the real `ldloca` arm
  produces a Ref Slot consumable by `neo-byref` opcodes, while any inline field
  consumers that did NOT reference the dest at runtime continue to use their
  own folded offsets.

## ADDED Requirements

### Requirement: K1 ldloca-kill soundness preserved across the real ldloca arm

The OPT-HARDEN `ldloca-kill` in FCP (which kills a copy-propagation when its
source or dest is addressed by an `ldloca`) SHALL remain sound once `ldloca`
produces a real Ref Slot for the non-folded case. Taking an address is a
potential-mutation escape regardless of whether the address is later folded or
realized as a Ref Slot, so the kill SHALL continue to fire on the `Ldloca`
opcode. The new real address producers (`ldflda`, `ldarga`, `ldelema`) SHALL be
treated as escapes by the same kill when they address a propagation's source or
dest.

#### Scenario: copy-prop killed when dest is addressed by a real ldloca
- WHEN `b = a` is followed by `ldloca b` feeding a byref-escape consumer
- THEN FCP SHALL NOT rewrite a later `b.field` read to `a.field`, because the
  address-escape makes `b`'s value potentially mutated through the Ref Slot.
