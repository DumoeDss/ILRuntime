# neo-byref Delta -- neo-step17-generic-byref-etc

## MODIFIED Requirements

### Requirement: Deferred byref sub-cases throw tagged NIE

The Neo VM SHALL throw a tagged `NotImplementedException` for byref sub-cases
not supported by the current step. The generic-parameter byref form (`ref T` /
`out T` with `T` a generic parameter) and the interface-on-VT-constrained
dispatch shape (beyond the common `IEquatable<T>` / `IComparable<T>` / box-once
shapes) are NOT deferred defects: they work via the existing type-agnostic byref
model (an 8-byte Ref Slot copied regardless of element type; the generic-param
token is resolved at the call site, not at the byref-marshal level) and the
existing `constrained.callvirt` dispatch arm (the {a,d,M2,b} cohorts already
cover the box-and-interface-dispatch + IL-VT-direct-call + CLR-VT-box-once +
IL-VT-inherited-CLRMethod paths). Regression guards for both SHALL be maintained
(the closure is locked).

The Neo VM is NOT required to support in this step:
(a) ~~the `stobj`/`ldobj` ref-slot portion of a value-type copy through
`stobj`/`ldobj`~~ **RESOLVED** (a value type WITH reference fields is now
correctly copied through `stobj`/`ldobj` for the direct-local and IL-instance
shapes; the nested-field-via-`ldflda` shape throws a tagged NIE),
(b) ~~CLR-object stind/ldind via field hash~~ **RESOLVED** (the
CLR-object-field `stind`/`ldind`/`stobj`/`ldobj` consumer branch + the
`ldflda` CLR-field-identity stamp shipped in `neo-step17-completion`),
(c) ~~CLR-method `ref`/`out` parameters~~ **RESOLVED** (the IL-to-CLR byref
typed-ref bridge shipped in `neo-step13-area4-refandstind`; a CLR value type
WITH reference fields and no binder still throws a Step-13b-tagged NIE on the
reflection path),
(d) ~~generic-byref (`ref T`/`out T` with `T` a generic parameter)~~ **RESOLVED
(this change, NO-OP)** -- the byref model is type-agnostic; the generic-param
form works through the existing 8-byte Ref Slot call ABI + the typed-ref bridge
without a generic-param-specific discriminator. Regression guards
(`Swap<int>`/`Swap<IL-ref>`/`Swap<IL-VT>`) lock the closure.
(e) explicit-interface byref,
(f) `fixed` unmanaged-pinning blocks -- **DEFERRED (out of `neo-byref` scope).**
The `fixed` statement lowers to raw-pointer opcodes (`conv.u` / `Conv_U` to
convert the pinned array reference to a native `int*`, plus raw-pointer
indexing), which are unimplemented Step-6 opcodes outside this capability.
The array-element ADDRESS itself works via `ref arr[i]` (`ldelema` + `stind`/
`ldind`, the TC14 path); GC pinning is a CLR-host concern the interpreter does
not model. A `fixed` probe SHALL throw `NotImplementedException` tagged with
the unimplemented pointer opcode (`Conv_U` / `Conv_I`; `Ldtoken` when an array
initializer is used). Support is routed to a future pointer/`Conv_U` step.
(g) ~~interface-on-VT-constrained beyond the common shape~~ **RESOLVED (this
change, NO-OP)** -- the `constrained.callvirt` dispatch arm's direct-call path
(IL-VT + ILMethod override) and box-once path (CLR-VT + CLRMethod; IL-VT +
inherited CLRMethod) already cover the box-and-interface-dispatch shape. The
{a,d,M2} cohort (`neo-step17-completion`) + the (b) cohort (`neo-step17-stobj-
refloop`) delivered the coverage; regression guards (IL-VT + CLR-VT, both via a
generic constrained caller) lock the closure.
(h) ~~the IL-value-type-with-reference-fields constrained sub-case~~
**RESOLVED** (the Constrained arm seeds the callee slot-0 ref region /
`CopyFrameToIL` with the real ref base + `TotalReferenceCount`, shipped in
`neo-step17-stobj-refloop`).

When a RefSlot targeting one of the remaining deferred sub-cases (`fixed`, (e)
explicit-interface byref) is consumed, the relevant arm SHALL throw a
`NotImplementedException` tagged with `Step 17` (or the unimplemented-opcode tag
for `fixed`'s `Conv_U`/`Ldtoken`) rather than silently mis-handle it.

#### Scenario: generic-byref round-trips through the type-agnostic byref model

- **WHEN** an IL method declares `void Swap<T>(ref T a, ref T b)` and calls it
  with `T = int`, `T = an IL reference type`, and `T = an IL value type`
- **THEN** each call SHALL swap the two referents correctly (the 8-byte Ref Slot
  is copied via the standard byref call ABI; the body's `T tmp = a; a = b; b =
  tmp;` lowers to the correct Move/Move_Vt/ref-Move from the resolved `T`). No
  `NotImplementedException` is thrown and no generic-param-specific discriminator
  is consulted. (This locks the closure discovered by the dump-gate; the form
  was already green on HEAD.)

#### Scenario: interface-on-VT-constrained dispatches via the existing constrained arm

- **WHEN** an IL method calls `constrained.callvirt IFace.M` on a value type `T`
  that implements `IFace` (an IL struct with an ILMethod override, or a CLR
  struct with a CLRMethod), dispatched via a generic caller
  `R F<T>(T v) where T : IFace`
- **THEN** the dispatch SHALL resolve the constrained type's concrete override
  via `constrainedType.GetVirtualMethod(targetMethod)` and route to the direct-
  call path (IL-VT + ILMethod) or the box-once path (CLR-VT + CLRMethod),
  returning the correct result with no `NotImplementedException`. (This locks
  the {a,d,M2,b}-cohort closure; both shapes were already green on HEAD.)

#### Scenario: a fixed statement throws the unimplemented-pointer-opcode NIE

- **WHEN** an IL method uses a C# `fixed (T* p = arr) { ... }` statement (a
  pinned byref)
- **THEN** the JIT SHALL throw a `NotImplementedException` naming the
  unimplemented pointer opcode (`Conv_U` / `Conv_I`; or `Ldtoken` when an array
  initializer is involved), NOT a byref-machinery NIE. The array-element address
  via `ref arr[i]` (the `ldelema` + `stind`/`ldind` path) is unaffected and
  remains usable; the gap is specifically the `fixed` statement's pointer-
  conversion opcodes, which are outside the `neo-byref` capability.

#### Scenario: stobj on a nested-field byref of a VT with reference fields throws tagged NIE

- **WHEN** `stobj`/`ldobj` copies a value type that has one or more reference
  fields through a nested-field byref (produced by `ldflda` of a struct field,
  not a direct local)
- **THEN** the arm SHALL throw a Step-17-tagged `NotImplementedException` (the
  nested-field ref-region recovery is deferred; only the direct-local and
  IL-instance shapes are correctly copied this step).
