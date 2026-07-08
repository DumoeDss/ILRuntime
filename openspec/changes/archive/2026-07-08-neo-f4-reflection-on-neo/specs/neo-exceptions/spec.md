## ADDED Requirements

### Requirement: Object.GetType redirect works under Neo for a caught object

Under `ENABLE_NEO_MODE`, a `callvirt` on `System.Object.GetType()` (the CLR
`GetType` instance method) against any receiver reachable from interpreted IL --
including a caught IL exception (the adaptor's `Adapter`), a recovered
`ILTypeInstance`, or a plain CLR object -- SHALL return the correct `System.Type`
and SHALL NOT throw. `Object.GetType` has a Legacy redirect
(`CLRRedirections.ObjectGetType`) registered in the `AppDomain` constructor; the
Neo path uses a SEPARATE redirect map (`RedirectMapNeo`), so a Legacy redirect
does NOT auto-populate the Neo path. A Neo redirect (`ObjectGetType_Neo`) SHALL be
registered alongside the Legacy one and SHALL mirror its semantics: when the
receiver is an `ILTypeInstance`, the returned `Type` SHALL be the IL type's
`ReflectionType` (the IL type identity projected to CLR); otherwise the returned
`Type` SHALL be the CLR `object.GetType()` of the receiver. The redirect SHALL
read the reference receiver from slot 0 of the Neo callee frame (the
reference-`this` read pattern), write a non-null `Type` into the return ref slot,
and encode a null receiver as the Neo null sentinel (`*(int*)retDst = -1`). This
redirect SHALL be Neo-only; the Legacy `ObjectGetType` redirect and the Legacy
`ExecuteR` path SHALL be byte-for-byte unchanged.

#### Scenario: GetType on a caught IL exception returns the IL type identity
- **WHEN** an IL method under `ENABLE_NEO_MODE` catches an IL-defined exception
  (whose catch-slot object is the adaptor's `Adapter`) and calls `e.GetType()`
- **THEN** the call SHALL return a non-null `System.Type` representing the IL
  exception type (its CLR projection), and SHALL NOT throw
  `ArgumentOutOfRangeException` / `NotImplementedException` / `InvalidCastException`
- (On HEAD `b0041e74` this throws `ArgumentOutOfRangeException` because no Neo
  redirect exists; the autogen `clrMethod.Invoke` then mis-reads the reference
  `this`. ADVERSARIAL PROBE: `NeoStep14_ILEx_GetType` -- a caught `MyEx`, call
  `e.GetType()`, assert non-null and `typeof(MyEx).IsAssignableFrom(t)`.)

### Requirement: ILTypeInstance field indexer reads IL-declared fields under Neo

Under `ENABLE_NEO_MODE`, the `ILTypeInstance.this[int index]` indexer `get` SHALL
return the value of the IL-declared field at `index`, reading from the Neo object
model (`byte[] Primitives` for primitive/enum fields; `AutoList ManagedObjects`
for reference fields and boxed CLR-struct fields), and SHALL NOT return `null` for
a populated field. The indexer is the standard CLR-side bridge for reading an
IL-declared field off a recovered `ILTypeInstance` (a direct cast to the IL type
is impossible -- the two are unrelated CLR types -- so field read-off a recovered
instance goes through the indexer; this is the path generated
cross-binding-adaptor property forwarders and host reflection use).

The indexer `get` SHALL gate IL-field vs CLR-inherited by
`index >= 0 && index < type.TotalFieldCount` (the analogue of the Legacy
`index < fields.Length` gate; under Neo `fields` is the primitive BYTE array, so
the byte-length MUST NOT be used as the field-count gate), resolve the field
offset via `type.GetFieldOffset(index)` and the field type via
`type.GetField(index, ...)` (both recurse through the IL base-type chain), and
dispatch on the field's TypeForCLR: primitive fields SHALL be read from
`Primitives` at the field's `PrimitiveOffset` by width and boxed; enum fields
SHALL be read as the underlying primitive and boxed as the enum; reference fields
SHALL be read from `ManagedObjects[ReferenceOffset]`; CLR-struct fields of an IL
instance (the `neo-clrstruct-field-of-il` layout, stored boxed at
`ManagedObjects[ReferenceOffset]`) SHALL be returned from that slot. An index in
the CLR-inherited range SHALL fall through to the existing `FirstCLRBaseType`
branch (`clrType.GetFieldValue(index, clrInstance)`), byte-identical to Legacy.
An IL value-type field (reconstruction required) SHALL throw a TAGGED
`NotImplementedException` rather than return wrong data (accepted-known edge;
rare for exception types). The `set` arm SHALL mirror `get` for the supported
shapes. The Legacy `get`/`set` arms (the `StackObject[] fields` path) SHALL be
byte-for-byte unchanged.

#### Scenario: Reading an IL-declared field off a caught exception via the indexer
- **WHEN** an IL method under `ENABLE_NEO_MODE` catches an IL-defined exception,
  recovers its `ILTypeInstance` via the `CrossBindingAdaptorType.ILInstance`
  bridge, and reads an IL-declared reference/primitive field through the
  `ILTypeInstance` indexer
- **THEN** the indexer SHALL return the field's actual value (the value the
  constructor / field-set stored), and SHALL NOT return `null` for a populated
  non-null field
- (On HEAD `b0041e74` the indexer `get` returns `null` unconditionally under
  `ENABLE_NEO_MODE`. ADVERSARIAL PROBE: `NeoStep14_ILEx_IndexerFieldRead` -- throw
  `new MyEx("idx-msg")`, recover `ili`, read the `Msg` field through a bound CLR
  helper via `ili[fieldIndex]`, assert the value equals `"idx-msg"`.)

## MODIFIED Requirements

### Requirement: Throw opcode executes under Neo

The `Throw` `ExecuteNeo` arm behavior is unchanged. This change RECORDS that the
companion reflection-read path #1 (recovering the IL view of a caught exception via
`((CrossBindingAdaptorType)e).ILInstance` -- a `callvirt` on the CLR interface
`CrossBindingAdaptorType::get_ILInstance` against the caught `Adapter` receiver)
WORKS on HEAD `b0041e74` (probe-confirmed: returns a non-null `ILTypeInstance`
without throwing). The prior F-4 deferral characterisation that this path throws
`InvalidCastException` is STALE -- an intervening change (Step 19/20 cross-binding
+ callvirt-CLR dispatch) closed it. No engine edit is made for this path; the
deferred-items doc is corrected so a future worker does not re-investigate a
non-bug. (This requirement body is otherwise identical to the shipped
`neo-il-exception-throw` form; the modification is the addition of this
clarifying paragraph documenting the path #1 already-works finding.)

## NOTES (non-normative)

- **Path #3 -- `appdomain.Invoke(instanceMethod, e)` instance re-entry remains
  DEFERRED.** Under `ENABLE_NEO_MODE` the public `ILIntepreter.Run(method,
  instance, p)` entry shim (`ILIntepreter.cs:104-137`) marshals NEITHER `instance`
  NOR `p` into the Neo frame (the Step-6 parameterless-only entry), so an IL
  instance-method override invoked via `AppDomain.Invoke` has no `this` and NREs.
  The fix is the parametrized-Run ABI extension (marshal `p` via
  `WriteNeoCallSlot` per parameter; push `instance` as the slot-0 `this`; extend
  `NeoBoxReturnValue` to the reference-return shape, F-12 / NEO-RUN-REF-RETURN) --
  a LARGE change recorded on STEP-25-PARTIAL and F-12. It is NOT unblocked by
  `neo-async-movenext-fix` (that change routes through `DriveMoveNextCore` + a
  fresh interpreter calling `ExecuteNeo` directly, NOT the public `Run`). It is
  tracked as a follow-on child (`neo-f4-parametrized-run-entry`, or the
  STEP-25-PARTIAL S3 prerequisite) that the LEAD SHALL drive next (TRUE-COMPLETION:
  not parked). Reading an IL instance METHOD off a caught exception therefore
  remains blocked until that child ships; reading the IL TYPE (path #2) and
  IL-declared FIELDS (path #4) are closed by THIS change.
