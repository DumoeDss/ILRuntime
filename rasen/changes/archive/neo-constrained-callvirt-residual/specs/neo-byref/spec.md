# spec.md delta -- neo-constrained-callvirt-residual

Capability: `neo-byref` (owns the Step-17 D-CONSTRAINED `constrained.callvirt`
dispatch arm).

## MODIFIED: Requirement: constrained. runtime arm (D-CONSTRAINED)

The Neo VM SHALL dispatch `constrained.callvirt T.M` where the constrained type
`T` is a value type, regardless of whether the C# compiler emits the
`constrained.` prefix as `ldloca v; constrained T; callvirt M` (the struct
`this` arrives as an 8-byte frame-native Ref Slot from `ldloca`/`ldarga`). The
runtime `Constrained` arm SHALL own the dispatch: the JIT emits the order
`[Push..., Constrained T, Callvirt M]` (Constrained runs BEFORE the callvirt),
so the `Constrained` arm carries the type token, reads the trailing callvirt
for the method token / param map / return-slot info, dispatches, and skips the
trailing callvirt (`ip += 2`). No JIT fusion or operand stamping is required;
non-constrained callvirts are byte-identical.

**The `Constrained` arm SHALL accept EITHER a `Callvirt` (any variant) OR a
plain `Call` as the trailing op.** For a constrained REFERENCE-type `T` whose
method `M` is a non-virtual ILMethod (non-abstract, declaring type not an
interface), the JIT lowers the C# `constrained. T; callvirt M` to
`[Constrained T, Call M]` (`JITCompiler.cs` callvirt->Call lowering fires
independent of `hasConstrained`). A plain `Call` carries the SAME operands the
arm reads (`Operand2` method token; `Operand` NeoCallParams index -- the
LowerNeoOffsets call-param case-list includes `Call`; `Register1`/`DstOffset`/
`Operand3` return slot), and the arm performs its OWN dispatch and skips the
trailing op (`ip += 2`), so a `Call` is handled identically to a `Callvirt`.
This mirrors Legacy `ExecuteR`, whose `Constrained` arm does not inspect the
trailing op at all (it only prepares the receiver and lets the next arm
dispatch). Rejecting a `Call` (the pre-fix behavior) is a defect.

For the **reference-type T** sub-case (`T` is a class, not a value type), the
box-once path SHALL dereference the receiver object from the byref `this`
(`mStack[*(int*)(frameBase + thisByteOff)]`) and dispatch on it AS-IS with NO
box (ECMA III.3.19: `constrained.` on a reference type is a plain callvirt on
the pointer). This is the existing Gap A branch; it covers any reference
`ILType`/`CLRType` `T`, not only `string`.

[...the box-required / direct-call / IL-VT-inherited-CLRMethod / IL-VT-with-ref-
fields sub-cases are unchanged from the prior spec text...]

#### Scenario: constrained callvirt lowered to a Call on a reference-type T dispatches without a box

- **WHEN** an IL method emits `constrained. T; callvirt M` (C# lowering for a
  generic `T : SomeBase`) where `T` is bound to a reference-type IL class and
  `M` is a non-virtual instance ILMethod on `SomeBase` (so the JIT lowers the
  callvirt to a plain `Call`)
- **THEN** the `Constrained` arm SHALL accept the trailing `Call`, dereference
  the receiver object from the byref `this` WITHOUT boxing, dispatch `M` on the
  receiver, skip the trailing op, and the call's result SHALL equal `M`'s
  output on that receiver (Legacy parity); the arm SHALL NOT throw
  "Constrained not immediately followed by a callvirt".
