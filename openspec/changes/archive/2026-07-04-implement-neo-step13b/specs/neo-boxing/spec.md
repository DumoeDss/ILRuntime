# neo-boxing

Boxing and unboxing of value types in the Neo register VM (`ENABLE_NEO_MODE`),
covering both IL value types and CLR value types (with and without a registered
`ValueTypeBinder`), plus CLR value-type `Initobj` and `constrained.` callvirt
specialization on a value-type `this`.

This capability is distinct from `neo-value-types` (in-frame storage, inline
field access, and whole-struct copy): boxing bridges the flat in-frame value-
type representation to a heap representation (`ILTypeInstance` for IL types, a
boxed CLR object for CLR types), and `constrained.` is a dispatch concern that
layers on top of `neo-dispatch`.

The CLR-call-ABI aspects of value-type handling (CLR struct by-value
parameters, return values, and instance `this`) were deferred to a Step 13b
follow-up in the base capability. This change closes the **by-value parameter
and return-value** portion of that deferral; the byref CLR crossing, the
value-type instance `this` direct-call (Area 4), and CLR-object `stind`/`ldind`
via field hash remain deferred.

## Requirements

### Requirement: IL value-type Box and Unbox

The `Box` and `Unbox`/`Unbox_Any` `ExecuteNeo` arms SHALL box an in-frame IL
value type into a new `ILTypeInstance` and unbox an `ILTypeInstance` back into
an in-frame IL value type, reusing the frame<->heap copy helpers
(`CopyFrameToIL` / `CopyILToFrame`). (Unchanged from base.)

### Requirement: CLR value-type Box / Unbox / Initobj (locals)

The `Box`, `Unbox`/`Unbox_Any`, and `Initobj` `ExecuteNeo` arms SHALL handle a
CLR value type LOCAL, stored as a boxed-object reference (a 4-byte mStack
index), via `PerformMemberwiseClone` for structs and the existing
`NeoBoxReturnValue` / `NeoWritePrimitiveToFrame` for primitives/enums.
(Unchanged from base.)

### Requirement: CLR value-type by-value parameter reading (call ABI)

<!-- ADDED by implement-neo-step13b (closes the Step 13 call-ABI deferral). -->

A CLR value type passed BY VALUE as a method parameter SHALL be laid out in the
callee param region as flat bytes sized by `GetPrimitiveSize(type)` via the
SAME `AllocateNeoCallParamSlot` callee-layout helper used for all other
parameter types. The call-lowering SHALL NOT use the caller source register's
slot shape for a CLR struct parameter (the temporary caller-temp-slot fallback
is REMOVED).

Both CLR-param readers -- the reflection fallback `CLRMethod.Invoke(byte*)` and
the autogen redirect delegate emitted by `AppendArgumentCodeNeo` -- SHALL read
a CLR struct parameter by its actual slot width via the `ReadNeo*`/`ReadNeoValueType`
helpers, advancing the cursor by exactly the parameter's flat-byte size, so
that the reader layout and the callee layout are byte-consistent.

- A CLR struct WITH a registered `ValueTypeBinder` SHALL be read via the binder
  (the binder maps ref fields).
- A pure-primitive CLR struct (no ref fields) WITHOUT a binder SHALL be read
  via `ReadNeoValueType` (flat bytes -> boxed `T`).
- A CLR struct WITH ref fields and NO binder SHALL throw a clearly-tagged
  `NotImplementedException` directing the user to register a binder.

#### Scenario: CLR struct by-value parameter round-trip

- WHEN a method declares a CLR value type parameter (e.g.
  `int Sum(TestVector3NoBinding v)`) and is called with a CLR struct argument
- THEN the callee receives the struct's primitive bytes unchanged (the sum of
  its fields equals the value computed from the argument), and no
  `ArgumentOutOfRangeException` is thrown (the K2 regression).

### Requirement: CLR value-type by-value return value (call ABI)

<!-- ADDED by implement-neo-step13b (closes the Step 13 call-ABI deferral). -->

A CLR value type RETURN value SHALL be written into the caller's dest frame
slot (sized by `AllocateLocalStackSpaces`) as flat bytes via
`WriteNeoValueType` (or the binder for structs with refs), by both the
reflection return path (`InvokeNeoClrMethod`) and the autogen
`GetReturnValueCodeNeo` return path. The K2-family "reads a primitive field
value as an mStack index" mis-copy SHALL NOT occur for a struct return.

#### Scenario: CLR struct return value round-trip

- WHEN a method returns a CLR value type (e.g. `TestVector3NoBinding Make(...)`)
- THEN the caller's dest local holds the struct's primitive bytes unchanged,
  and a subsequent field read on the returned value is correct.

### Requirement: Boxed-ref-local to flat-bytes-param bridge (K2-FAM closure)

<!-- ADDED by implement-neo-step13b. DEFERRED (D3 safety valve) during the
     apply pass: a clean reproducer needs ldfld/stfld on CLR struct fields,
     which is itself deferred. The flat-bytes-source K2 case (a CLR struct
     local obtained from a CLR method RETURN, which is stored as flat bytes by
     the D6 return path) IS closed this pass and is covered by the by-value
     parameter scenario above. The Box/Initobj-source half (a 4-byte boxed-ref
     mStack-index local passed by value) remains deferred. -->

When a CLR value type LOCAL (stored as a boxed-object reference per the base
capability) is passed BY VALUE to a CLR method, the call param-setup SHALL
unbox the local's primitive bytes into the callee's flat-bytes param slot
(reusing the base capability's unbox helpers), NOT copy the 4-byte mStack
index. This closes the K2-FAM pre-existing bug where a scalar/boxed-ref CLR
value-type local was mis-read as an mStack index at the call boundary.

**Status (apply pass 2026-07-04):** DEFERRED. The boxed-ref-local source shape
(a local created via Box/Initobj, then passed by value) is not exercised by any
green scenario this pass and has no clean test surface (it needs IL-side
`ldfld`/`stfld` on CLR struct fields, a separate deferred concern). A CLR struct
local obtained from a CLR method RETURN is flat bytes (the return-value
requirement writes flat bytes), so THAT source shape already works without a
bridge and is covered by the by-value parameter scenario. The Box/Initjob-
source half remains a documented accepted-known deferred item (silent
wrong-result for that specific source shape), NOT a regression of a previously-
green case.

#### Scenario: CLR struct local passed by value

- WHEN a CLR struct is declared as a local, assigned, and then passed by value
  to a CLR method
- THEN the callee observes the assigned field values (no mStack-index
  misinterpretation). (DEFERRED for the Box/Initobj-source shape; the
  return-source shape is covered by the by-value parameter scenario.)

### Requirement: Out-of-scope deferrals (explicit, accepted-known)

<!-- MODIFIED by implement-neo-step13b: narrows the base capability's blanket
     call-ABI deferral. The by-value param/return portion is now IN; the rest
     stays deferred. -->

The following CLR value-type concerns remain DEFERRED and SHALL continue to
throw a clearly-tagged `NotImplementedException` (NOT silently misbehave):

- The value-type instance `this` direct-call lowering (`Unsafe.Unbox<T>` /
  Area 4): a CLR struct *instance* method call where `this` is a struct is not
  yet supported in the autogen wrapper (`// TODO: ValueType instance in Neo`).
- CLR-method `ref`/`out` parameters: a byref Ref Slot crossing into a CLR
  `ref T` argument (the IL-method byref path from `neo-byref` remains the
  green target).
- CLR-object `stind`/`ldind`/`stobj`/`ldobj` via field hash: a Ref Slot whose
  `objectIndex` addresses a CLR object (not an `ILTypeInstance`) throws a
  Step-17/13b-tagged NIE.
