## MODIFIED Requirements

### Requirement: Boxed-ref-local to flat-bytes-param bridge (K2-FAM closure)

A CLR value type LOCAL that is passed BY VALUE to a CLR method SHALL be byte
copied from the local's flat-managed-bytes frame region into the callee's flat-
bytes param slot via `CopyNeoCallArguments`, regardless of how the local was
sourced (method return, Box+Unbox, or Initobj). The call param-setup SHALL NOT
copy a 4-byte mStack index for a CLR value-type local (the K2-FAM
"int-as-mStack-index" mis-copy SHALL NOT occur for any source shape).

A Neo CLR value-type LOCAL is stored as flat managed bytes (`Size =
GetNeoValueTypeManagedSize, RefCount = 0, localIsRef = false` under
`ENABLE_NEO_MODE`, per the F-MAJ-1 fix to the `CLR value-type Box with and
without ValueTypeBinder` requirement's deferred note). The Box, Initobj, and
Unbox_Any ExecuteNeo arms SHALL read/write that local as flat bytes (the opt-
harden-2 review-fix arms M1/M2/M3-twin), so a local sourced from Box/Initobj/
Unbox is flat bytes end-to-end.

**Status:** DELIVERED (resolved-by-recent-work). The closure was a side effect of
three changes whose combined effect was re-assessed against K2-FAM by this change
and confirmed via adversarial reproducer probes on HEAD:

- `neo-opt-harden-2` (F-MAJ-1) declared a CLR-VT LOCAL as flat bytes — the OLD
  boxed-ref representation (`Size=4, RefCount=1`) that produced the
  "int-as-mStack-index" corruption NO LONGER EXISTS for a CLR-VT local.
- `neo-opt-harden-2` review-fix (round 1) rewrote the `Initobj` (M1), `Box` (M2),
  and `Unbox_Any`-dest arms to read/write flat bytes — so a local sourced from
  Box/Initobj/Unbox is FLAT BYTES end-to-end.
- `implement-neo-step13b` unified the by-value-param read in
  `CopyNeoCallArguments` to byte-copy N flat bytes from the caller local's
  `Offset`.

The closure holds for ALL source shapes of a CLR-VT local (return / Box / Initobj
/ Unbox) — a local obtained from any of these is flat bytes and passes by value
correctly. The Box/Initobj-source half that was previously DEFERRED is now
realized. A separate, unrelated gap — `[NEO-IL-VT-INSTANCE-COVERAGE]` (an IL-side
`Ldfld` on a CLR struct field, which throws a Step-6 NIE) — is NOT K2-FAM and is
out of scope here.

#### Scenario: CLR struct local sourced from Box then Unbox, passed by value
- **WHEN** an IL method declares a CLR struct local `v`, boxes it (`object o = v`),
  unboxes into a new local (`T t = (T)o`), and passes `t` by value to a CLR method
  that sums its fields
- **THEN** the callee observes the original field values (no mStack-index
  mis-interpretation), and the field sum equals the value computed from the
  original local.

#### Scenario: CLR struct local sourced from Initobj, passed by value
- **WHEN** an IL method declares a CLR struct local via `default(T)` (Initobj)
  and passes it by value to a CLR method that sums its fields
- **THEN** the callee observes all-zero fields (the default), and the field sum
  equals zero.

#### Scenario: CLR struct local re-initobj'd then passed by value
- **WHEN** an IL method declares a CLR struct local, assigns it a value, then
  re-initializes it via `t = default(T)` (re-initobj), and passes it by value
- **THEN** the callee observes the re-initialized (all-zero) fields, and the
  field sum equals zero (the post-re-init value, not the prior assigned value).

#### Scenario: Two CLR struct locals both sourced from Box, both passed by value
- **WHEN** an IL method declares TWO CLR struct locals, boxes and unboxes each
  into distinct locals, and passes BOTH by value to a CLR method that sums their
  fields (the F-MAJ-1 two-live-struct stress, but Box-sourced)
- **THEN** each callee invocation observes its OWN local's field values with NO
  cross-corruption between the two simultaneously-live locals (each field sum
  equals the value computed from its own source local).

#### Scenario: No regression on the return-source shape
- **WHEN** the existing `NeoStep13_K2FamRegression` probe (a CLR struct local
  sourced from a CLR method RETURN, passed by value — the Step 13b return-source
  shape) is run after this change
- **THEN** it remains green (the return-source shape was already closed by Step
  13b; this change adds the Box/Initobj/Unbox source shapes alongside it without
  perturbing the return-source path).

### Requirement: Out-of-scope deferrals (explicit, accepted-known)

The following CLR value-type concerns remain DEFERRED and SHALL continue to
throw a clearly-tagged `NotImplementedException` (NOT silently misbehave):

- CLR-method `ref`/`out` parameters (Area 4c): a byref Ref Slot crossing into a
  CLR `ref T` argument requires a typed-reference bridge (copy-in / call /
  copy-out, or a pinned frame address). The IL-method byref path from
  `neo-byref` remains the green target. The autogen `ByRef` branch stays a
  clearly-tagged NIE.
- CLR-object `stind`/`ldind`/`stobj`/`ldobj` via field hash (Area 4d): a Ref
  Slot whose `objectIndex` addresses a CLR object (not an `ILTypeInstance`)
  needs `Ldflda` field-hash stamping and consumer-arm dispatch to
  `CLRType.Get/SetFieldValue(hash, target)`. The current Step 17 arms dispatch
  only on frame-native (`objectIndex == -1`) and heap-IL (`GetNeoILInstance`);
  a CLR-object target stays a clearly-tagged NIE.
- A CLR struct instance method `this` (or by-value param) WITH reference fields
  and NO registered `ValueTypeBinder`: throw a clearly-tagged
  `NotImplementedException` directing the user to register a binder (the
  Neo-cursor binder API for ref fields does not exist yet).

The value-type instance `this` direct-call lowering (Area 4b) and the
`Unsafe.Unbox<T>` boxed direct-call mode (Area 4a) are NO LONGER deferred --
they are delivered by the `neo-step13-area4` change (see the "CLR value-type
instance method `this` reading" and "CLR value-type instance method boxed
direct-call and write-back" requirements). The Neo wrapper does NOT emit the
Legacy `WriteBackInstance`; the Area 4a value-type-`this` write-back is a
flat-bytes re-box.

The K2-FAM `Boxed-ref-local to flat-bytes-param bridge` is NO LONGER deferred --
it is DELIVERED (resolved-by-recent-work: `neo-opt-harden-2` + its review-fix +
`implement-neo-step13b`); see that requirement's DELIVERED status.

Also accepted-known (not a deferral, a pre-existing bug): the F-2 /
INLINER-REFONLY-VT inliner mis-compile for a ref-only VT local constructed via
`new S(refArgs)` (inlined `stfld.ref.inline` writes do not survive to the
following in-frame `ldfld.ref` read). Suspect: the JIT inliner's ref-fold over a
0-prim-size VT local. Target for a future optimizer-hardening step. (Distinct
from K2-FAM; K2-FAM is closed.)

#### Scenario: Deferred items still throw clearly-tagged NIEs
- **WHEN** an IL method invokes a CLR method with a `ref`/`out` parameter, or
  executes `stind`/`ldind` on a byref to a CLR object field, or passes a CLR
  struct WITH reference fields and NO binder as a by-value param or as the
  instance `this`
- **THEN** the operation throws a clearly-tagged `NotImplementedException`
  (NOT a silent mis-behavior), and the tag directs the user to the relevant
  follow-up (register a binder / typed-ref bridge / field-hash plumbing).

#### Scenario: No regression on existing CLR instance calls
- **WHEN** the existing NeoStep smoke suite (NeoStep6 through NeoStep19 +
  NeoOptHardening + the NeoStep14_ILEx_* probes) is run after this change
- **THEN** every previously-green case remains green; reference-type CLR
  instance method calls are byte-for-byte unchanged (the value-type-`this`
  discriminator keys on `DeclearingType.IsValueType`, which is FALSE for every
  reference type).
