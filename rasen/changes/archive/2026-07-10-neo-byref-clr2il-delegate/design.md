# Design - neo-byref-clr2il-delegate (child 14)

## Context

F-7 (`archive/2026-07-09-neo-f7-delegate-byref` + F-7B `-reftype-writeback` +
`-sib-direct-call`) shipped the **IL->IL** cross-frame delegate-invoke byref
(`del(ref v)` invoked FROM IL via `Callvirt_IL IsDelegateInvoke`). The byref
crosses a frame boundary via `NeoRunDelegateTargetOnThis` (offset relativization
for primitives; mStack-slot promotion for reference referents). The IL CALLER
frame owns the byref source cell, and the write-back is the F-7 mechanism.

Child 14 is the **REVERSE direction**: a **CLR->IL** delegate callback with a
byref param. A CLR host helper invokes a delegate whose target is an IL method
that takes a `ref`/`out` param; the IL callee mutates the byref; the mutation
MUST be observable on the CLR side after the call returns. This routes through
`DelegateAdapter.NeoInvokeSub` (the CLR->IL delegate callback, e.g.
`List.ForEach(ilAction)`), NOT the `Callvirt_IL` branch.

## The two-layer gap (confirmed empirically on HEAD `48af2123`)

A reproducer was constructed: a custom delegate type
(`TestCLRBinding.Clr2IlRefIntDelegate(ref int x)`) + a CLR host helper
(`InvokeRefCallback(del, seed)`) + an IL method (`Clr2IlBumpRef(ref int x){x+=10}`).
On HEAD the probe FAILS at TWO layers.

### Layer 1 (BIND): the byref param never matches a per-arity adapter

`DelegateManager.FindDelegateAdapter` (DelegateManager.cs:304) matches an IL
method against the registered per-arity adapters (`methods`/`functions`) by
comparing `parameterTypes[j] != method.Parameters[j].TypeForCLR` (:326). The
per-arity adapters are the generic `MethodDelegateAdapter<T1>` /
`FunctionDelegateAdapter<T1,TResult>` family, whose `T1` is a by-VALUE type
(`Action<>`/`Func<>` cannot carry `ref`/`out` -- ref types are not legal generic
args). So an IL method `Clr2IlSetOut(out int x)` (param `System.Int32&`) NEVER
matches any `Action<int>` adapter, and `FindDelegateAdapter` falls back to
`DummyDelegateAdapter` (:373).

### Layer 2 (CONVERT): GetConvertor crashes on the Dummy before the converter

`DelegateAdapter.GetConvertor(type)` (DelegateAdapter.cs:1561) accesses
`NativeDelegateType` FIRST (`type.IsAssignableFrom(NativeDelegateType)`, :1563).
`DummyDelegateAdapter.NativeDelegateType` THROWS `ThrowAdapterNotFound`
(:887). So even though a converter for the delegate TYPE could exist, it is
never consulted -- the byref param's failure to bind a per-arity adapter makes
the whole path unreachable. The on-HEAD error:

```
KeyNotFoundException: Cannot find Delegate Adapter for:
  TestCases.NeoStep19Test.Clr2IlSetOut(System.Int32& x),
  Please add following code:
  appdomain.DelegateManager.RegisterMethodDelegate<System.Int32>();
  ... at DummyDelegateAdapter.get_NativeDelegateType (:887)
      at DelegateAdapter.GetConvertor (:1563)
      at Extensions.CheckCLRTypes (:287)  [the delegate param check]
```

### Why F-7 is unaffected (direction asymmetry)

F-7's IL->IL path never touches `NeoInvokeSub`/`GetConvertor`: the delegate is
constructed and invoked ENTIRELY in IL, so the byref stays inside the IL frame
model (the `Callvirt_IL IsDelegateInvoke` branch + `NeoRunDelegateTargetOnThis`).
The CLR-side converter/wrapper is never built. Child 14 hits the CLR-side
wrapper construction (`GetConvertor` at the `InvokeOutCallback(del)` call's
param check), which F-7's path does not traverse.

## The fix (MEDIUM -- three coordinated changes)

### D1: a byref-aware converter registration (adapter-keyed)

**Decision:** Add `DelegateManager.RegisterDelegateByRefConvertor<T>
(Func<IDelegateAdapter, Delegate> action)` + a `clrByRefDelegates` dict.
Unlike the standard `RegisterDelegateConvertor<T>(Func<Delegate,Delegate>)`
(which receives only the adapter's by-value `Delegate` -- unable to carry a
`ref`), the byref converter receives the `IDelegateAdapter` itself, so it can
route the byref through `NeoInvokeByRef` (D3) and read the write-back.

`ConvertToDelegate` prefers `clrByRefDelegates` over `clrDelegates`. A new
`HasByRefConvertor(type)` lets `GetConvertor` consult it.

### D2: unblock the Dummy path for a byref-registered delegate type

**Decision:** `GetConvertor` checks `HasByRefConvertor(type)` BEFORE accessing
`NativeDelegateType`, and short-circuits to `ConvertToDelegate`. `ConvertToDelegate`
checks `clrByRefDelegates` BEFORE the `DummyDelegateAdapter` throw. This lets a
byref-registered delegate type reach its converter EVEN when the IL method
bound to a `DummyDelegateAdapter` (the common case -- no by-value per-arity
match). The converter runs the IL target via `NeoInvokeByRef`, so the Dummy's
"register an adapter" guard does NOT apply (the adapter's `method`/`instance`
fields are valid for a direct ExecuteNeo run).

**Rationale:** The byref converter is keyed on the delegate TYPE (not a per-
arity param match), so the bind-layer Dummy fallback is harmless -- the
converter owns the invocation. Layer 2 is the only thing standing in its way.

### D3: the byref-aware callback -- self-referencing scratch cells + write-back

**Decision:** Add `DelegateAdapter.NeoInvokeByRef(object[] args)` (on
`IDelegateAdapter` under `#if ENABLE_NEO_MODE`), which calls a new
`NeoInvokeSub(args, marshalByRef: true)`. When `marshalByRef` is true:

- For each byref param (detected via `method.Parameters[i].IsByRef` + the
  8-byte Ref-Slot layout), reserve a SCRATCH CELL past `nf.TotalStructSize`
  (4-aligned, sized to the element width via `NeoByrefElemSize`), stage the
  CLR value into it (`WriteNeoByrefScratchValue`), and make the param's 8-byte
  Ref Slot SELF-REFERENCING: `(objIdx == -1, off == scratchCellOff)`.
- Non-byref params use the existing `WriteNeoCallSlot` byte-for-byte.
- After `ExecuteNeo` returns, read the (possibly mutated) scratch cell back
  into `args[i]` (`ReadNeoByrefScratchValue`), giving the CLR converter the
  write-back channel.

**Why self-referencing:** `NeoInvokeSub` builds an ISOLATED frame on a fresh
pooled interpreter (there is NO IL caller frame -- the caller is CLR). So the
F-7 "rebase the offset back to the caller frame" mechanism does NOT apply
(there is no caller frame). Instead the byref must point at a cell WITHIN the
isolated frame that survives the run: the scratch cell, at `frameBase +
scratchOff`, which the callee's `ldind`/`stind` (resolving `objIdx == -1`
against this frame's `frameBase`) read and write. `NeoInvokeSub` reads it back
before tearing down the frame. The frame is grown by the scratch region so the
self-references stay in-bounds for the whole run.

**Multicast (D4):** the multicast next-chain re-invocation passes
`marshalByRef` through (`n.NeoInvokeSub(args, marshalByRef)`), so each
subsequent target re-reads `args[]` (now carrying the prior target's write-back)
-- multicast ref-semantics hold (each target sees the accumulated mutation).

### D2-scope (element types)

The scratch path handles primitive referents (`int`/`long`/`float`/.../enums)
and CLR value types (via `WriteNeoValueType`/`ReadNeoValueType`). A REFERENCE-
typed referent (`ref string` / `ref <class>`) would need an mStack-slot stage
(the F-7B promotion analog) -- RECORDED as a sequencing note; the common
`ref int`/`out int`/`ref long` callback shape is the core and is covered by
the 4 probes.

## Goals / Non-Goals

**Goals:**
- `ref int` / `out int` CLR->IL delegate callback: the IL callee's mutation is
  observable on the CLR side (write-back round-trips). Probe
  `NeoStep19_Clr2Il_ByRef` (5->15) + `NeoStep19_Clr2Il_Out` (->77) PASS.
- `ref long` (8-byte element): the scratch cell sizes to the element width.
  Probe `NeoStep19_Clr2Il_ByRefLong` (10L->1010L) PASSES.
- Multicast ref-int: each target sees the prior mutation.
  Probe `NeoStep19_Clr2Il_Multicast` (+10, *2; 5->30) PASSES.
- The F-7 IL->IL delegate-byref cases + all NeoStep smoke stay green
  (regression guard).

**Non-Goals:**
- A REFERENCE-typed referent (`ref string`/`ref <class>` reassign) -- the F-7B
  mStack-slot promotion analog. Sequencing note; the primitive/value core is
  shipped.
- AOT (`ilrt_neoc`) wire-up of the byref converter registration. The JIT path
  is in scope; AOT follows the standard pattern (out of scope unless the AOT
  smoke regresses).
- A byref to a CLR struct FIELD via this path (the F-10 ldflda shape). The
  scratch path stages a flat value; a field-aliased byref is Step-17/F-10
  territory.

## Risks / Trade-offs

- **[Risk] Growing the NeoInvokeSub frame by the scratch region.** -> The
  scratch region is reserved ONLY when `marshalByRef` is true AND there is a
  byref param (empty for `NeoInvoke`/`NeoInvokePublic`, which pass false). The
  frame base/size accounting for `ExecuteNeo` uses the passed `frameBase` +
  the callee's own `TotalStructSize` for ITS locals -- the scratch region sits
  PAST the callee's frame (after `TotalStructSize`), so the callee never
  addresses it except through the self-referencing byref. The return slot is
  placed at `newEsp = frameBase + scratchCur` (the grown size), matching the
  original layout. Verified by the 4 probes + full smoke (267/0/0).
- **[Risk] GetConvertor bypasses NativeDelegateType for byref-registered
  types.** -> Narrow: the bypass is taken ONLY when `HasByRefConvertor(type)`
  is true (a type with an explicit `RegisterDelegateByRefConvertor`). All
  standard delegate types are unaffected. The full smoke (267/0/0, +4 from
  263) is the regression guard.
- **[Risk] The Dummy-adapter bypass in ConvertToDelegate hides a genuine
  "missing adapter" for a byref type with no converter.** -> If a byref
  delegate type has NO registered converter at all, the path still reaches
  `clrByRefDelegates.TryGetValue` (miss) then the Dummy throw -- the throw is
  preserved for the un-registered case. Only a REGISTERED byref converter
  bypasses the Dummy throw.

## Implementation Notes / Corrections

- The converter lives in `ILRuntimeTestBase/Adapters/helper.cs` under
  `#if ENABLE_NEO_MODE` (the converter calls `adapter.NeoInvokeByRef`, which is
  on `IDelegateAdapter` only under `ENABLE_NEO_MODE`). `RegisterDelegateByRefConvertor`
  + `HasByRefConvertor` (in `DelegateManager.cs`) are NOT guarded -- they are
  pure-additive public API compiled in all configs (Legacy-safe; never called
  in Legacy).
- The probe delegate types + host helpers live in
  `TestFramework.TestCLRBinding` (TestClass3.cs), NOT `TestClass3` (which is a
  tiny 2-method class at the top of the file). The first implementation attempt
  placed them in `TestClass3`'s body and hit CS0426; moved to `TestCLRBinding`.
- The `NeoInvokeSub(args, bool)` overload is internal; the single-arg
  `NeoInvokeSub(args)` delegates with `marshalByRef: false` (the legacy hot
  path, byte-for-byte unchanged -- `NeoInvoke`/`NeoInvokePublic` never read
  `args[]` back).
