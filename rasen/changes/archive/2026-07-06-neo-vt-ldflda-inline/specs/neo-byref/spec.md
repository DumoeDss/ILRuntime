## MODIFIED Requirements

### Requirement: ldflda / ldarga address producers

The Neo VM SHALL implement runtime arms for `ldarga`/`ldarga_s` (producing
`(-1, paramFrameOffset)`) and a real `ldflda` arm that dispatches on its
operand. The JIT type-specialization pass SHALL stamp a marker (a flag bit in
the standalone `Operand4` field) on a real `ldflda` whose source operand is an
in-frame IL value type, so the runtime arm distinguishes the in-frame-VT
operand case from the heap-IL / CLR-object operand case. The marker SHALL be
stamped in the pre-lowering type-specialization pass (where the source register
index is still available for type lookup), and SHALL survive `LowerNeoOffsets`
intact (it lives in a standalone, non-union-aliased operand field).

When the marker is present, the runtime arm SHALL produce a frame-native Ref
Slot `(-1, vtBase + field.PrimitiveOffset)` for the in-frame-VT operand,
where `vtBase` is resolved by reading the operand slot's leading int: when the
leading int is `-1`, the operand slot holds a frame-native Ref Slot produced
by a real `ldloca`/`ldarga` (the struct base is the Ref Slot's offset half);
otherwise the operand slot holds the struct's flat primitive bytes (e.g. a
`this` seeded with flat bytes by the `constrained.callvirt` box-once path, or
any in-frame-VT operand whose slot is the struct's primitive region), and the
struct base IS the operand slot's own frame byte offset. When the marker is
absent, the arm SHALL keep the existing dispatch (read the operand slot as a
Ref Slot: `objectIndex == -1` → frame-native; `objectIndex >= 0` → heap-IL /
CLR-object).

The `addrAlias` folding behavior for `ldflda` SHALL remain unchanged: a dest
whose every consumer is foldable stays folded (runtime arm dead); a dest whose
address escapes the folding window stays real (runtime arm fires, now correctly
for the in-frame-VT-flat-bytes operand via the marker branch).

#### Scenario: ldarga of a struct method's this

- WHEN a value-type instance method executes `ldarga.s 0` for `this`
- THEN the arm SHALL produce `(-1, thisParamFrameOffset)` consumable by
  `stind`/`ldind` and by a `constrained.` callvirt.

#### Scenario: ldflda on an in-frame VT whose operand is a Ref Slot (ldloca/ldarga dest)

- WHEN an IL method emits `ldloca V; ldflda f` (or `ldarga this; ldflda f` in a
  struct instance method called via a direct `call`) and the `ldflda` dest's
  address escapes the `addrAlias` folding window (consumed by `stind`/`ldind`/
  `stobj`/`ldobj`/a byref `Call` arg/a `constrained.` box path)
- THEN the runtime `ldflda` arm SHALL read the operand slot's leading int as
  `-1`, resolve the struct base from the Ref Slot's offset half, and produce a
  frame-native Ref Slot `(-1, vtBase + field.PrimitiveOffset)` consumable by
  the byref consumers.

#### Scenario: ldflda on an in-frame VT whose operand holds flat bytes (the constrained-boxed-this shape)

- WHEN an IL struct override (e.g. `ToString()`) is invoked via
  `constrained.callvirt` (the box-once path seeds the override's `this` param
  slot with the struct's flat primitive bytes), and the override body emits
  `ldflda this.field` (e.g. `id.ToString()` lowering to
  `ldflda this.id; constrained.callvirt Int32.ToString`)
- THEN the runtime `ldflda` arm SHALL recognize (via the JIT-stamped marker)
  that the operand is an in-frame VT, read the operand slot's leading int as a
  field value (not `-1`, not an mStack index), and produce a frame-native Ref
  Slot `(-1, operandSlotFrameOffset + field.PrimitiveOffset)` whose offset half
  is the operand slot's own frame byte offset plus the field's primitive
  offset; the consumer (`ldind`/`stind`/`constrained.callvirt`) SHALL read/
  write the field correctly through that Ref Slot.

#### Scenario: IL struct ToString override whose body calls a field method (the Step-17 deferred positive test)

- WHEN an IL struct `S { int id; }` overrides `ToString()` with a body that
  calls `id.ToString()` (which the C# compiler lowers to `ldflda this.id;
  constrained.callvirt Int32.ToString`), and an IL method invokes `s.ToString()`
  on an in-frame `S` local
- THEN the override SHALL receive the correct boxed `this`, the `ldflda`
  SHALL produce a correct frame-native Ref Slot to `id`, the constrained
  `Int32.ToString` SHALL box the field value, and the result SHALL equal
  `"Named:" + id` (no garbage, no crash, no `NotImplementedException`).

#### Scenario: ldflda on a nested struct field

- WHEN an IL method emits `ldloca outer; ldflda inner; ldflda x` (a nested-
  field address chain) and the leaf address escapes the folding window
- THEN the leaf `ldflda` SHALL carry the marker (its operand is the inner
  `ldflda` dest, an in-frame VT) and produce a frame-native Ref Slot at the
  accumulated absolute offset, consumable by byref consumers (provided no
  intervening `Ldfld_Value` whole-VT load — that is the separate Step-12b
  deferral).

#### Scenario: ldflda on a struct's reference-type field (the ref region)

- WHEN an IL method emits `ldflda s.objField` on an in-frame struct `s` whose
  field `objField` is a reference type, and the address escapes the folding
  window
- THEN the produced frame-native Ref Slot's offset half SHALL point at the
  field's slot, and a `ldind.ref`/`stind.ref` consumer SHALL read/write the
  corresponding mStack ref slot (the ref-region sub-case).

#### Scenario: No regression on heap-IL and CLR-object ldflda

- WHEN the existing NeoStep smoke suite is run after this change
- THEN every previously-green heap-IL `ldflda` and CLR-object `ldflda` case
  remains green (the marker is stamped ONLY for an in-frame IL value-type
  operand; a heap-IL or CLR-object operand never carries the marker, so the
  existing heap/CLR branches fire byte-identically).

#### Scenario: Register reuse / escape probe (the Step-17-B1 silent-corruption class)

- WHEN an IL method produces a byref via `ldflda` on an in-frame VT, the dest
  register is then reused by an intervening operation, and FINALLY the byref
  is read
- THEN the read SHALL observe the field's current value (no stale/clobbered
  value from the intervening reuse); the `liveAliasMap` per-instruction
  snapshot correctly tracks the `ldflda` dest's live range across the reuse,
  and the marker branch produces the correct Ref Slot for the dest's own live
  range.
