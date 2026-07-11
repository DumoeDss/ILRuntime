# Design -- implement-neo-step13 (Box/Unbox complete)

Grounded in the branch state at propose time (HEAD after Step 12b,
`6a8d1d2c`). All file:line refs are to that state.

## Context

The Neo VM already stores value types as **flat bytes** in the frame byte region
plus an out-of-line ref run in the frame mStack region (Step 12). That layout is
*identical in shape* to an `ILTypeInstance` (`byte[] Primitives` + `AutoList
ManagedObjects`). Step 5 exploited this for IL value types: the existing
`Box`/`Unbox` `ExecuteNeo` arms already do:

- IL enum / IL primitive Box/Unbox: a single sized `CopyBlock`.
- IL value-type (with refs) Box: `CopyFrameToIL(...)` (ILIntepreter.Neo.cs:1640)
  = CopyBlock the primitives + copy `refCount` mStack slots into the new
  `ILTypeInstance.ManagedObjects`.
- IL value-type Unbox: `CopyILToFrame(...)` (ILIntepreter.Neo.cs:1892) = the
  reverse.

The two helpers (ILIntepreter.Neo.cs:2124 / 2146) are the *authoritative*
frame<->heap copy primitives -- the same pair Step 12b's `Move_Vt` mirrors for
frame<->frame copies.

What remains is the **CLR** half (Box/Unbox/Initobj arms throw NIE at lines
1604/1662/1905) and the **`constrained.` value-type specialization** (the
callvirt lowering treats a constrained-`this` uniformly today; it does not
specialize the "box once then dispatch" case for value types).

A critical prior finding shapes this design: **the Neo path does not consume
`ValueTypeBinder` at all.** A full-repo grep of `ILIntepreter.Neo.cs` returns
zero `ValueTypeBinder` references; the binder is wired only into the Legacy
`StackObject*` paths (`StackObject.cs:125`, `RuntimeStack.cs`,
`ILIntepreter.Register.cs`). So area 2 is not "switch the Neo path to the
binder" -- it is "wire the binder into the Neo Box/Unbox arms for the first
time".

## Goals / Non-Goals

**Goals (IN this pass):**
- Area 1: verify + edge-case-harden the existing IL Box/Unbox (already works;
  add coverage).
- Area 2: CLR value-type Box/Unbox/Initobj, WITH and WITHOUT `ValueTypeBinder`.
- Area 3: `constrained.` callvirt specialization on a value-type `this`.

**Non-Goals (DEFERRED to Step 13b):**
- Area 4: binding codegen overhaul (`Unsafe.Unbox<T>`, eliminate
  `WriteBackInstance`). The Neo redirect path (`RedirectionNeo` /
  `RegisterCLRMethodRedirectionNeo`) is the target; today it inherits Legacy
  `WriteBackInstance` semantics for instance writeback.
- Area 5: CLRMethod unified Neo param layout (reuse
  `AllocateNeoCallParamSlot` for CLR structs; read via `ReadNeo*` by width;
  remove the caller-temp-slot fallback at `Optimizer.Neo.cs:646-655`). This
  also owns the Step 12b K2 fix.

**Non-Goals (other steps):**
- `byref`/`ref`/`out` parameters = Step 17 (a pointer model, not a copy).
- IL value-type `newobj` (`new VT()`) = Step 18.
- async state machines (e.g. `TaskAwaiter.GetResult()` by value) = Step 20 --
  these exercise area 5 and are blocked on Step 13b.
- Unboxing-into-a-byref (`unbox.any T` used as a mutable address) = Step 17.
- `isinst`/`castclass` on boxed value types = Step 15.

## Decisions

### D1 -- Area 1 (IL Box/Unbox): no code change, coverage only

The IL value-type Box/Unbox arms (ILIntepreter.Neo.cs:1608-1664 and 1856-1901)
are already complete via `CopyFrameToIL`/`CopyILToFrame`. The lowering
(`Optimizer.Neo.cs:430-445`) already stamps `DstOffset`/`SrcOffset`/
`Operand3`(dst ref)/`Operand4`(src ref). Edge cases to confirm with tests:
multi-ref IL VT box then unbox round-trip; ref-identity preservation (boxed
instance's ref fields share objects with the source); null-source Unbox throws
`NullReferenceException` (already handled at 1861-1865). No runtime change
expected; if a test reveals a bug it is reported, not silently widened.

### D2 -- Area 2 (CLR Box/Unbox/Initobj): two paths keyed on ValueTypeBinder

The Box/Unbox/Initobj arms branch on `t as ILType` first; the `else` (CLR)
branch is what we fill in. Within the CLR branch, the discriminator is whether
the `CLRType` has a registered `ValueTypeBinder`:

```csharp
// In the Box arm, CLR branch (replaces ILIntepreter.Neo.cs:1659-1663):
var clrType = t as CLRType;
var binder = clrType?.ValueTypeBinder;
object boxed;
if (binder != null)
{
    // WITH binder: materialize a real CLR struct from the frame flat bytes.
    // binder.ToObjectNeo(frameBase, ip->SrcOffset, srcRefOffset) -> boxed struct.
    // (New Neo helper on ValueTypeBinder: reads TotalPrimitiveSize bytes +
    //  TotalReferenceCount ref slots the same way CopyFrameToIL does, into a
    //  boxed T via FormatterServices or a stackalloc+Unsafe.As.)
    boxed = binder.BoxFromFrame(frameBase, ip->SrcOffset, srcRefOffset,
                                mStack, frameRefBase);
}
else
{
    // WITHOUT binder: keep the value boxed in mStack as a raw CLR struct.
    // Allocate a boxed instance (Activator.CreateInstance) and memcpy the
    // frame flat bytes into it via Marshal/Unsafe. Ref fields are NOT
    // representable here (no binder => we cannot map frame ref slots to CLR
    // ref fields), so this path is only valid for pure-primitive CLR structs
    // (e.g. Vector3). A CLR struct WITH ref fields and no binder is an
    // error -> throw with a clear Step-13b message.
    Type clr = clrType.TypeForCLR;
    boxed = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(clr);
    int sz = AppDomain.GetPrimitiveSize(clrType);
    if (sz > 0)
        Unsafe.CopyBlock(Unsafe.AsPointer(ref boxed) /*or a boxed-primitive write*/,
                         frameBase + ip->SrcOffset, (uint)sz);
}
dstIdx = frameRefBase + dstRefOffset;
mStack[dstIdx] = boxed;
*(int*)(frameBase + ip->DstOffset) = dstIdx;
```

The exact boxed-write mechanism (a boxed struct's field bytes are not writable
through a normal pointer in the CLR; `TypedReference`/`makeref` is restricted)
is the one implementation subtlety. The implementer MAY use the binder's
`CopyValueTypeToStack` reverse, or -- for the no-binder pure-primitive case --
construct the box by reading the frame bytes into a local `T` then
`(object)T` (which boxes by value). The **preferred** no-binder implementation
is therefore *read the frame flat bytes into a managed `T` local via a typed
helper, then box it* (`object o = Unsafe.ReadUnaligned<T>(...)` then the
implicit box), avoiding raw writes into a boxed object. This is correct for
pure-primitive CLR structs. The WITH-binder path delegates to the binder.

Unbox (replaces ILIntepreter.Neo.cs:1902-1906): the reverse. `obj = mStack[srcIdx]`
is a boxed CLR struct.

```csharp
// WITH binder: binder.AssignToFrame(frameBase, DstOffset, dstRefOffset,
//                                   obj, mStack, frameRefBase) writes primitives
//   + refs back into the frame.
// WITHOUT binder (pure-primitive only): unbox-copy the struct's bytes back.
//   T value = (T)obj;  (unboxes by value)
//   Unsafe.WriteUnaligned(frameBase + ip->DstOffset, value);
```

Initobj (replaces ILIntepreter.Neo.cs:1604-1606): zero the frame byte region
for the CLR VT.

```csharp
// WITH binder: binder.ZeroFrame(frameBase + ip->DstOffset) (primitives) +
//   null the TotalReferenceCount ref slots if the CLR VT has ref fields.
// WITHOUT binder: Unsafe.InitBlock(frameBase + ip->DstOffset, 0,
//   AppDomain.GetPrimitiveSize(clrType)); and null any ref slots.
```

**Why two paths, not one.** The binder exists precisely to map a CLR struct's
fields (including ref fields) to/from the runtime's stack representation. The
Neo frame has no `StackObject*` descriptor, so the binder needs new Neo-shaped
helpers (`BoxFromFrame`/`AssignToFrame`/`ZeroFrame`) operating on `byte*` +
mStack instead of `StackObject*`. The no-binder path can only ever handle
pure-primitive CLR structs (no way to discover ref-field layout without the
binder), and is sufficient for the `foreach(List<int>)` no-per-iteration-alloc
goal and `Vector3`-style structs.

**Alternative considered.** Require a binder for ALL CLR value types and throw
otherwise. Rejected: pure-primitive CLR structs (`Vector3`, `Point`) are common
and should not require a binder registration to box. The no-binder path is
narrow (pure-primitive) and clearly errors otherwise.

### D3 -- Area 3 (`constrained.` callvirt on a value type)

`constrained.` precedes a `callvirt`. The JIT already detects it
(`JITCompiler.cs:1709-1715`, `hasConstrained`) and stamps `op.Operand4 = 1`
(line 1765). Today the callvirt lowering handles the constrained-`this` as a
generic dispatch; the **value-type-this specialization** (box-once-then-dispatch
for inherited methods, direct-call for value-type-declared methods) is the gap.

Two sub-cases per ECMA-335 III.4.3 (`constrained.` semantics), resolved at JIT
time from the constrained token's type `T`:

1. **`T` declares/overrides the method** (e.g. `T.ToString()` where `T`
   overrides): emit a **direct `Call`** to the value-type method, passing the
   in-frame `this` by its address. No box. (The value-type method's `this` is
   the frame byte region; the existing Neo call machinery for value-type
   `this` already passes the address.)
2. **`T` does not declare the method** (inherited from `object`, e.g. the
   default `GetHashCode`/`ToString`/`Equals`): the method expects a boxed
   `this`. Emit a **box** of `T`'s in-frame value into a temp, then a normal
   reference callvirt on the boxed object.
3. If `T` is a reference type: `constrained.` is a no-op (current behavior) --
   unchanged.

The specialization lives in the `Code.Callvirt` JIT case
(`JITCompiler.cs:1700-1800`), keyed on `hasConstrained` and the resolved
`constrainedType`. It emits either a lowered `Box` + `Callvirt` sequence (case
2) or downgrades the `callvirt` to a `Call` with a value-type `this` (case 1).
The constrained `Constrained` pseudo-op is already removed and re-appended at
1766-1772; the specialization extends that block.

**`Callvirt_Interface` exclusion (Step 11).** Step 11 added
`Callvirt_Interface` to the `hasConstrained` exclusion set
(`Optimizer.Neo.cs:574-577`). A constrained callvirt that resolves to an
interface method is rare for value types (a struct implementing an interface
and being called via the interface). The implementer confirms the value-type
specialization does not fire for the interface path (it stays a box + interface
dispatch), matching Step 11's design.

### D4 -- K2 relationship (Step 12b bug, area 5 overlap)

The Step 12b K2 bug (planning-context §8 Finding K.2 / design Finding K):
**VT-by-value CLR parameter passing is broken.** The call param-setup emits a
plain `Move` for a value-type argument; that Move reads `*(int*)(SrcOffset)` as
an mStack index, but for an in-frame VT the byte region holds only primitives,
so it reads a primitive field VALUE (e.g. 77) as an mStack index ->
`ArgumentOutOfRangeException` at ExecuteNeo Move arm. Even a primitive-only VT
param fails. The call param path does not route through `Move_Vt`.

**Step 13's relationship to K2:**
- K2 lives in the **CLR/IL call param-setup** (`Optimizer.Neo.cs:637-660`, the
  caller-temp-slot fallback; `CopyNeoCallArguments` ILIntepreter.Neo.cs:130).
- That is exactly **area 5's territory** (DEFERRED). Area 5 removes the
  caller-temp-slot fallback and routes CLR struct params through a proper
  callee layout read by `ReadNeo*`.
- Therefore **Step 13 does NOT fix K2** (it is deferred to 13b with area 5).
  Step 13's new tests must NOT pass a CLR value type by value (that path is
  still broken by K2); they exercise box/unbox and `constrained.` only, which
  do not touch the call param-setup.
- When 13b lands area 5, K2 is resolved as a natural consequence of the
  unified param layout. The 13b proposal MUST reference this design's D4 as
  the K2 closure point.

A related note: the **IL** VT-by-value param path (IL caller -> IL callee) is a
*separate* concern from K2 (CLR callee). IL->IL VT params go through
`NeoCallParamMap` (Step 8 + Step 12) and are reported working in the Step 12b
research, though Finding K.1 (FCP mis-propagates VT Moves) can still bite. Step
13 does not change IL->IL VT param passing.

### D5 -- Capability placement

New capability `neo-boxing` (not extending `neo-value-types`). Rationale:
boxing is a distinct concern from in-frame storage/field-access/copy
(`neo-value-types`). When area 5 lands in 13b and touches the call ABI, *that*
change will MODIFY `neo-value-types` (the param-layout requirements). Keeping
box/unbox in its own capability keeps the two changes cleanly separable in the
archive.

## Risks / Trade-offs

- **[CLR Box of a no-binder struct WITH ref fields is impossible]** -> Mitigation:
  the no-binder path detects ref fields (via `clrType` field scan or
  `TotalReferenceCount > 0` analogue) and throws a clear Step-13b-tagged
  `NotImplementedException` directing the user to register a binder. Pure-
  primitive structs work without a binder; this matches Legacy behavior (Legacy
  also requires a binder for structs with refs to avoid descriptor allocation).
- **[Writing into a boxed object's bytes is restricted in the CLR]** ->
  Mitigation: the no-binder Box path reads frame bytes into a managed `T` local
  then boxes by value (`(object)value`), never writing into an existing box.
  This is correct and allocation-shaped exactly like a real box.
- **[`constrained.` specialization perturbs the callvirt lowering shared by all
  virtual calls]** -> Mitigation: the specialization only fires when
  `hasConstrained && constrainedType.IsValueType`; the reference-type
  constrained case and the unconstrained case are byte-identical to today. Full
  NeoStep smoke (was 41/41) is the gate; the dispatch tests (NeoStep10/11) are
  the direct canary.
- **[Deferral leaves `foreach(List<CLRStruct>)` and `TaskAwaiter.GetResult()`
  broken]** -> Mitigation: `foreach(List<int>)` (the stated validation target)
  works via area 2 (no-binder pure-primitive box) and the existing List
  enumerator path; only CLR-struct-element foreach needs area 5. Documented as
  accepted-known in tasks.md. async (Step 20) was always a later step.
- **[Value-type binder Neo helpers add new surface to `ValueTypeBinder.cs`]**
  -> Mitigation: new helpers are `#if ENABLE_NEO_MODE` and additive; Legacy
  binder methods are untouched. The Neo helpers mirror the existing
  `CopyValueTypeToStack`/`AssignFromStack` shape but on `byte*`/mStack.

## Migration Plan

No migration: all changes are behind `ENABLE_NEO_MODE` and additive (replacing
NIE throws with implementations). Legacy (`ExecuteR`) is untouched. Rollback =
revert the change directory; no data format changes.

## Open Questions

- Exact boxed-write mechanism for the no-binder Box path (D2): confirm
  `(object)Unsafe.ReadUnaligned<T>(frameBytes)` produces a correctly-shaped box
  for the CLR structs the test suite uses (`Vector3`-like). The implementer
  verifies with a round-trip test (box -> unbox -> field equals).
- Whether the `constrained.` direct-call (case 1) needs a new opcode or reuses
  `Call` with a value-type `this` address. The implementer confirms the
  existing value-type-`this` call path (Step 8) already accepts an in-frame
  address; if so, no new opcode.
- Whether any NeoStep smoke case currently passes a CLR struct by value "by
  luck" through the caller-temp-slot fallback and would change behavior. The
  implementer watches the smoke diff carefully; since area 5 is deferred, NO
  call-ABI change lands in this pass, so this risk is nil for Step 13 itself.
