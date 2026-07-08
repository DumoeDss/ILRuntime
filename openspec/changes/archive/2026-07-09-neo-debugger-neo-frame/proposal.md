## Why

Under `ENABLE_NEO_MODE`, the debugger's variable-inspection paths in
`ILRuntime/Runtime/Debugger/DebugService.cs` (`GetThisInfo` at `:203-261` and
`GetLocalVariableInfo` at `:263-300`) short-circuit with "Neo ... not supported
yet" (`:207-209` and `:268-271`). They are built on the Legacy frame model --
`StackObject*` 12-byte unions read via `StackObject.ToObject` off
`StackFrame.LocalVarPointer`/`BasePointer`. Under Neo the frame is a compact
`byte* frameBase` (`ExecuteNeo`, `ILIntepreter.Neo.cs:858`) whose slots are raw
primitives plus `AutoList mStack` reference indices, and the per-slot layout is
encoded in `CompiledFrame.LocalInfos` (`StackSlotInfo{Offset, RefOffset, Size,
RefCount}`, `JITCompiler.cs:67-73`). The existing `StackObject*` reads mis-read
those raw bytes, so inspection returns wrong data; the guard is correct to
refuse. This change makes Neo frame variable inspection WORK -- mirroring the
F-4 `ILTypeInstance` indexer dispatch (the per-TypeForCLR slot read shipped for
heap fields) against the frame-local slots.

This is the D sub-surface that Step 26 (`neo-step26-perf-validation`) deferred
(neo-deferred-items row "DebugService reads the Neo frame"). The stacktrace
instruction dump is ALREADY Neo-adapted (`DebugService.cs:143-148`) -- NOT part
of this change.

## What Changes

The dump-gate (probe-then-reason on HEAD `50d0e7e2`, see `design.md`) found
that Neo variable inspection is TRACTABLE: a frame local's value+type is
recoverable the SAME way the F-4 indexer / the Step-13b `ReadNeoValueType`
recover a Neo slot -- by a per-TypeForCLR dispatch keyed off the slot's
`StackSlotInfo` + the local's declared `IType`. The frame already carries
everything the dispatch needs: `CompiledFrame.LocalInfos` (the per-slot offsets
+ sizes + ref counts), `StackFrame.ManagedStackBase` (the frame's `frameRefBase`
into `mStack`), and `Definition.Body.Variables[i]` (the local's type, 1:1 with
`LocalInfos[paramCount + i]`). No deep protocol work is required. Concretely:

- **SHIP `GetLocalVariableInfo` Neo arm.** Replace the `:268-271` guard with a
  Neo path that iterates `Definition.Body.Variables`, resolves each local's
  `IType` via `AppDomain.GetType(var.VariableType, declaringType, method)`
  (the SAME resolution the JIT uses at `JITCompiler.cs:1711`/`ILMethod.cs:786`,
  including generic-parameter handling), reads its `StackSlotInfo` at
  `LocalInfos[paramCount + i]`, and dispatches by shape:
  primitive -> read `frameBase + slot.Offset` by the primitive width and box;
  reference -> `mStack[frameRefBase + slot.RefOffset]` (-1 sentinel -> null);
  CLR-struct local (F-MAJ-1 flat bytes) -> `ReadNeoValueType` (the boxed struct);
  IL-value-type local -> a tagged placeholder string ("IL-VT local inspection
  not supported"), SEQUENCED to a follow-on (it spans the primitive + reference
  sub-regions and needs reconstruction).
- **SHIP `GetThisInfo` Neo arm.** Replace the `:207-209` guard with a Neo path
  that recovers the `this` (`ParamInfos[0]`, a reference slot) the SAME way --
  read the slot-0 reference index and unwrap `ILTypeInstance` /
  `CrossBindingAdaptorType` (mirroring Legacy `:218-230`), then enumerate the
  IL type's fields and read each via the F-4 indexer
  (`instance[fieldIndex]`) or the equivalent inline dispatch. The IL field
  read REUSES the F-4 indexer that already shipped (path #4) -- no new
  field-read code is needed beyond the frame-local dispatch.
- **SEQUENCE IL-value-type locals** (the one shape that is NOT a mirror of F-4).
  An IL-VT local occupies a primitive sub-region + a reference sub-region; the
  F-4 indexer's "IL-value-type field reconstruction not supported" tagged NIE
  applies. This change emits a clear placeholder for that local so the rest of
  the inspection stays correct; full IL-VT-local reconstruction is a follow-on.

Legacy is the REFERENCE. All Neo arms live under `#if ENABLE_NEO_MODE`; the
Legacy `StackObject*` arms are byte-identical (untouched). The
`StackFrame.LocalVarPointer` aliasing of `frameBase` (`ILIntepreter.Neo.cs:905`)
is REUSED as the `byte*` entry point for the Neo arms -- no frame-struct change.

## Capabilities

### New Capabilities
- `neo-debugger`: Debugger variable inspection of a Neo execution frame -- the
  requirements that `GetThisInfo` / `GetLocalVariableInfo` read a Neo frame's
  locals (`byte* frameBase` + `AutoList mStack` + `CompiledFrame.LocalInfos`)
  and return the correct value+type for the tractable local shapes (primitive,
  reference, CLR-struct-boxed), with a clear placeholder for IL-value-type
  locals (sequenced).

### Modified Capabilities
<!-- None. The neo-debugger capability is new; no existing capability's
     REQUIREMENTS change. -->

## Impact

- **Engine (Neo-only, `#if ENABLE_NEO_MODE`):**
  `ILRuntime/Runtime/Debugger/DebugService.cs` -- the Neo arms of
  `GetThisInfo` (`:203-261`) and `GetLocalVariableInfo` (`:263-300`), replacing
  the two "not supported yet" guards (`:207-209`, `:268-271`). The dispatch is a
  new private helper `ReadNeoLocalValue` (mirrors the F-4 indexer's per-shape
  branches). The F-4 indexer (`ILTypeInstance.this[index]`, `:387-456`) is
  REUSED as-is for the IL-field read inside `GetThisInfo`.
- **Tests:** `TestCases/NeoStep<N>Test.cs` (or a new `NeoDebuggerTest.cs`) +
  a host-side self-check mirroring the `NeoStep25LoadExecCheck` pattern
  (`NeoStep25LoadExecCheck.cs`) -- the capstone. The natural exercise point is
  an UNHANDLED IL exception thrown in a Neo frame: `ExecuteNeo`'s unwind path
  (`ILIntepreter.Neo.cs:4543`) constructs an `ILRuntimeException`, whose ctor
  calls `DebugService.GetThisInfo` / `GetLocalVariableInfo`
  (`ILRuntimeException.cs:36-40`). A probe method (primitive + reference locals)
  that throws unhandled -> the exception's `ThisInfo`/`LocalInfo` carry the
  correct values (not "not supported yet"). Adversarial: mutate a local before
  the throw -> the inspection reflects the mutated value.
- **Docs:** `.trae/documents/neo-deferred-items.md` -- mark the
  "DebugService reads the Neo frame" row RESOLVED (primitive + reference +
  CLR-struct); SEQUENCE IL-value-type-local reconstruction as a follow-on.
- **Regression risk:** LOW. Both shipped arms are Neo-only (Legacy
  byte-identical; the Legacy `StackObject*` arms are untouched). The risk is
  reading the wrong offset/width for a local -- mitigated by keying the dispatch
  on the SAME `StackSlotInfo` + `AppDomain.GetType(...)` the JIT/storage
  allocator use (single source of truth), and by the existing `try/catch` around
  each local iteration (`:274-293`) keeping one bad local from aborting the
  whole inspection. The full `NeoStep` smoke (226/0/0) is the regression gate;
  the adversarial probe (correct value + reflected mutation) is the correctness
  gate (a green smoke does NOT prove the fix).
