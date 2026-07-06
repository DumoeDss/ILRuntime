## Why

Step 17 shipped the unified 8-byte Ref Slot / byref model and the primitives-only
`Stobj`/`Ldobj` consumer arms; the `constrained.`-on-VT dispatch ({a,d,M2}) landed
in `neo-step17-completion`. Two byref correctness gaps remain that share a single
root mechanism: **(b)** `Stobj`/`Ldobj` copy only `TotalPrimitiveSize` bytes, so a
value type WITH reference fields copied through a byref (e.g. `*(S*)ptr = local;`
or a `cpobj`/`ldobj` round-trip) SILENTLY TRUNCATES the ref half; and the
**IL-VT-with-ref-fields constrained** sub-case throws a Step-17-tagged NIE (the
byref `this` does not carry the struct's ref-region mStack base) at both the
direct-call path (`ILIntepreter.Neo.cs:3818`) and the inherited-CLRMethod box path
(`:3876`). Both are pre-existing deferrals from `neo-step17-completion`, NOT
regressions. The majority of real-world IL structs have a reference field (a
`string`, an `object`, a boxed value) plus an override or a default
`ToString`/`GetHashCode` -- so closing (b) unblocks realistic struct usage and is
the higher-value, lower-regression-risk target. The remaining (c) edges
(generic-byref, `fixed` pinning, interface-on-VT-constrained beyond the common
shape) are rarer, none are exercised by the smoke, and they are independent
plumbing -- they are deferred to keep the diff reviewable (the explicit
Step-13b/area4 lesson: do not mix unrelated byref correctness surfaces).

## What Changes

- **(b) `Stobj`/`Ldobj` ref-slot loop -- IN.** Extend the `Stobj`/`Ldobj` arms
  (`ILIntepreter.Neo.cs:3514-3570`) so a value type WITH reference fields is
  copied correctly through a byref. For a frame-native byref of a DIRECT local,
  recover the source/dest ref-region mStack base (the byref carries the primitive
  byte offset but NOT the ref base) and mirror `Move_Vt`: a byte `CopyBlock` of
  `primSize` PLUS an mStack-to-mStack copy of `TotalReferenceCount` ref slots.
  For an mStack-object byref (`objectIndex >= 0`), extend the existing
  ILTypeInstance branch with the ref half via `CopyFrameToIL`/`CopyILToFrame`
  (which already iterate `ManagedObjects`). For a frame-native byref that does
  NOT resolve to a direct local (a nested-field address via `ldflda`), keep a
  tagged NIE (the rare edge). Primitive-field value types (the existing green
  target) stay byte-identical.
- **(b) IL-VT-with-ref-fields constrained sub-case -- IN.** Remove the
  `TotalReferenceCount > 0` NIEs at the Constrained arm's IL-VT-direct-call path
  (`:3818`) and the IL-VT-inherited-CLRMethod box path (`:3876`). Seed the
  callee slot-0 ref region (direct-call path) from the recovered source-local
  ref base; extend the inherited-CLRMethod `CopyFrameToIL` call to pass the real
  ref base + `TotalReferenceCount` (it currently passes `refCount=0`). This is
  the same root mechanism as the Stobj/Ldobj ref-loop (recover the byref source's
  ref base), so it shares the recovery logic.
- **(c) generic-byref / `fixed` / interface-on-VT-constrained -- DEFERRED.** These
  remain Step-17-tagged NIEs (or accept-known for `fixed` if a probe shows the
  address works without GC pinning). They are independent plumbing (a generic-
  param type-token discriminator; a pinned-local flag; an interface-dispatch
  branch) that does NOT fall out of (b) and is not exercised by the smoke.
- **No Legacy change.** `ExecuteR` (`ILIntepreter.Register.cs`) is the SEMANTIC
  reference; all edits are Neo-only.

## Capabilities

### New Capabilities
<!-- None -- this change extends an existing capability. -->

### Modified Capabilities
- `neo-byref`: Narrows the "Deferred byref sub-cases throw tagged NIE"
  requirement -- flips (a) the `stobj`/`ldobj` ref-slot portion of a value-type
  copy from DEFERRED (primitives only) to DELIVERED for the direct-local and
  IL-instance shapes (a value type WITH reference fields is correctly copied; the
  nested-field-via-`ldflda` shape stays NIE-tagged); flips (h) the IL-value-type-
  with-reference-fields constrained sub-case from DEFERRED to DELIVERED (the
  slot-0 ref-region seed at the Constrained arm). Removes the "PARTIAL this step
  (primitives only)" note from the `stind`/`ldind`/`stobj`/`ldobj` dispatch
  requirement. (c) generic-byref / `fixed` / interface-on-VT-constrained stay
  DEFERRED (still tagged NIE).

## Impact

- **`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`** -- the
  `Stobj` (`:3514`) and `Ldobj` (`:3544`) arms: add the ref-region copy loop
  (frame-native-direct-local via `localInfos` recovery + IL-instance
  `ManagedObjects` half + nested-field NIE). The `Constrained` arm IL-VT-direct-
  call (`:3818`) and IL-VT-inherited-CLRMethod box (`:3876`) paths: remove the
  ref-fields NIE; seed the callee slot-0 ref region / extend the
  `CopyFrameToIL` call with the real ref base + `TotalReferenceCount`.
- **NO JIT change for the green target** (the ref-base recovery is runtime-only,
  scanning `localInfos` for the local whose `Offset == thisByteOff`). A JIT
  side-stamp of the source local's `RefOffset` is the apply-phase fallback IF the
  `localInfos` scan proves insufficient (e.g. the source is a temp, not a local).
- **`TestCases/NeoStep17Test.cs`** (extend) -- `NeoStep17_*` adversarial probes:
  Stobj of a VT-with-ref-field (dest's stale ref slot MUST be overwritten); Ldobj
  of same (src ref slot MUST be read, not the dest's stale null); a nested VT
  with ref fields (the NIE-tagged edge OR the recovered path, dump-gated); the
  IL-VT-with-ref-fields constrained direct-call override; the IL-VT-with-ref-
  fields constrained inherited-CLRMethod box. Regression: primitives-only
  Stobj/Ldobj still works + the step17-completion paths + full NeoStep smoke.
- **Legacy (`ILIntepreter.Register.cs`) is the SEMANTIC REFERENCE** (the Stobj/
  Ldobj + Constrained arms); NOT modified. All edits Neo-only / behind
  `ENABLE_NEO_MODE`.
