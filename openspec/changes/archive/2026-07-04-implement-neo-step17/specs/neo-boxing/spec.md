# neo-boxing (delta)

## MODIFIED Requirements

### Requirement: constrained. callvirt specialization on a value type

A `constrained.` + `callvirt` sequence whose constrained token resolves to a
value type `T` SHALL exhibit the following OBSERVABLE behavior: when `T`
declares or overrides the target method, the call SHALL execute on the
value-type `this` address with no boxing allocation; when the target method is
inherited from `System.Object` (or otherwise not declared on `T`), the
value-type `this` SHALL be boxed once and the call SHALL dispatch on the boxed
object. The reference-type constrained case and the unconstrained callvirt
case SHALL be byte-for-byte unchanged from the prior behavior. The interface-
dispatch path (`Callvirt_Interface`) SHALL be excluded from this value-type
specialization (a constrained callvirt resolving to an interface method stays a
box + interface dispatch).

This requirement is MODIFIED to permit realization either (a) by compile-time
JIT lowering (the original phrasing) OR (b) by a runtime `Constrained` arm
executed after the callvirt (the realization chosen by Step 17, because the Neo
JIT currently re-appends the `Constrained` opcode after the callvirt and cannot
perform the compile-time lowering). Both realizations MUST satisfy the same
observable scenarios.

#### Scenario: constrained callvirt to an inherited object method on a struct
- WHEN a generic method calls `constrained. T` then `callvirt ToString()` where
  `T : struct` and `T` does not override `ToString`
- THEN the value-type `this` is boxed once and `object.ToString()` is invoked on
  the box; the result is the struct's default string representation.

#### Scenario: constrained callvirt to a struct-declared method
- WHEN a generic method calls `constrained. T` then `callvirt` a method that
  `T` overrides
- THEN the call executes on the in-frame `this` address (a Ref Slot produced by
  `ldarga`/`ldloca` under the `neo-byref` capability) with no boxing allocation.

#### Scenario: No regression on existing callvirt and dispatch behavior
- WHEN the existing NeoStep smoke suite (NeoStep6 through NeoStep16) is run
  after this change
- THEN every previously-green case remains green; the reference-type
  constrained path and the unconstrained callvirt path are unchanged.

## ADDED Requirements

### Requirement: Constrained runtime arm resolution basis

The runtime `Constrained` arm SHALL be enabled by the `neo-byref` value-type
address model: the value-type `this` address required for both the no-box
direct-call case and the box-once case is a Ref Slot produced by
`ldarga`/`ldloca` (see the `neo-byref` capability). Where the constrained
value-type specialization needs more than the byref model provides (e.g.
interface-on-VT constrained callvirt with cross-model signature matching), the
arm SHALL throw a Step-17-tagged `NotImplementedException` rather than
silently mis-dispatch.
