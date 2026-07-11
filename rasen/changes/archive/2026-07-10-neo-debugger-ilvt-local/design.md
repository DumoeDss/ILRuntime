# Design - neo-debugger-ilvt-local

> Scope: reconstruct an IL-value-type LOCAL's fields in the Neo debugger frame
> inspection. This child extends the shipped neo-debugger-neo-frame (which left
> the IL-VT local as a placeholder string -- `"<IL value-type local: reconstruction
> deferred>"`) to walk the in-frame VT's fields from its split primitive +
> reference sub-regions. Probed-by-reasoning on HEAD `d06a67fe`, then confirmed
> by a reproducer (the placeholder IS returned for a real IL-VT local), fixed,
> and stash-toggle-verified. PURE ASCII; SHALL-first. Legacy is the REFERENCE.
> Every shipped edit is Neo-only (`#if ENABLE_NEO_MODE`).

## 0. The gap (confirmed on HEAD)

The neo-debugger-neo-frame child shipped `DebugService.ReadNeoLocalValue`
(`ILRuntime/Runtime/Debugger/DebugService.cs`) with a per-shape frame-local
dispatch (primitive / reference / CLR-struct / IL-VT) that MIRRORS the F-4
`ILTypeInstance.this[index]` indexer (`ILTypeInstance.cs:421-444`). The IL-VT
shape returned a PLACEHOLDER string:

```
// value type
if (localType is CLR.TypeSystem.ILType)
    return "<IL value-type local: reconstruction deferred>";
```

So for `struct VtLocal { int X; string S; }` declared as a local, the debugger
rendered `VtLocal vt = <IL value-type local: reconstruction deferred>` -- the
struct's fields were NOT expanded. This child closes that gap.

**Reproducer (constructed + confirmed on HEAD):** added a probe IL method
`NeoDebuggerFrameProbe.ProbeVtLocal` that declares a `VtLocal vt` local, sets
`vt.X = 4242; vt.S = "vt-field-A";`, then throws unhandled. The unhandled-throw
unwind (`ILIntepreter.Neo.cs:4543`) constructs an `ILRuntimeException` whose ctor
(`ILRuntimeException.cs:40`) calls `DebugService.GetLocalVariableInfo`, stashing
`.LocalInfo`. On HEAD the stashed string is:

```
VtLocal vt = <IL value-type local: reconstruction deferred>
```

-- the struct's `X` (4242) and `S` ("vt-field-A") are absent. That is the gap.

## 1. The in-frame IL-VT layout (source of truth)

Under `ENABLE_NEO_MODE`, the storage allocator
(`JITCompiler.AllocateLocalStackSpaces`, `JITCompiler.cs:1716-1728`) lays out an
IL value-type local across BOTH frame regions:

- **Primitive sub-region**: `slot.Offset` = the byte offset within `frameBase`;
  `slot.Size = ilType.TotalPrimitiveSize` flat bytes (the struct's primitive
  fields, sized/aligned exactly like a heap instance's `byte[] Primitives`).
- **Reference sub-region**: `slot.RefOffset` = the index RELATIVE to
  `frameRefBase` (the frame's reference-region base); `slot.RefCount =
  ilType.TotalReferenceCount` slots (the struct's reference fields, laid out
  exactly like a heap instance's `AutoList ManagedObjects`).

This is the SAME split storage a HEAP `ILTypeInstance` uses (`byte[] Primitives`
+ `AutoList ManagedObjects`), only the region bases are frame-relative instead
of instance-relative. The evidence is `Move_Vt` (`ILIntepreter.Neo.cs:1330-1334`):
a whole-VT copy reads a ref field at relative index `r` from
`mStack[frameRefBase + vtRefBase + r]` -- so the in-frame VT's ref field at
relative index `r` lives at `mStack[frameRefBase + slot.RefOffset + r]`.

A field F at ILType index `i` therefore lives at:
- **primitive**: `frameBase + slot.Offset + off.PrimitiveOffset`
- **reference**: `mStack[frameRefBase + slot.RefOffset + off.ReferenceOffset]`

where `off = ilType.GetFieldOffset(i)` (`ILType.cs:2657-2665`) returns the SAME
`ILTypeFieldOffset{PrimitiveOffset, ReferenceOffset}` a heap instance's F-4 read
uses (`ILTypeInstance.cs:423`). The per-field shape test keys on
`ilType.GetField(i, out FieldDefinition _)`'s returned `IType`
(`ILType.cs:2706-2717`), identical to the F-4 indexer
(`ILTypeInstance.cs:424`).

## 2. The fix

### 2.1 Pass `frameRefBase` into `ReadNeoLocalValue`

The reference sub-region base (`frameRefBase + slot.RefOffset`) needs
`frameRefBase`. The caller `GetLocalVariableInfo` already recovers it from the
`StackFrame` (`topFrame.ManagedStackBase = frameRefBase`,
`ILIntepreter.Neo.cs:909`) for the Neo arm but did not forward it. Change:
capture `int frameRefBase = topFrame.ManagedStackBase;` and pass it as a new
parameter to `ReadNeoLocalValue`.

### 2.2 Replace the placeholder with a field-walk

`ReadNeoLocalValue`'s IL-VT branch now calls a new helper
`ReadNeoIlVtLocalFields` instead of returning the placeholder. The helper walks
`i` in `[0, ilType.TotalFieldCount)` and, for each field, dispatches on the
field's `IType` -- MIRRORING the F-4 indexer branches
(`ILTypeInstance.cs:425-443`):

- **primitive** (`ft.IsPrimitive`): `ReadNeoFramePrimitive(vtPrimBase +
  off.PrimitiveOffset, ft, domain)` -- the SAME `byte*`-overload helper
  neo-debugger-neo-frame shipped (keyed on the AppDomain primitive singletons,
  byte-consistent with the allocator). `vtPrimBase = frameBase + slot.Offset`.
- **reference / enum (boxed) / CLR-struct (F-10 boxed)** (else branch):
  `mStack[vtRefBase + off.ReferenceOffset]` where `vtRefBase = frameRefBase +
  slot.RefOffset`. A null sentinel renders as "null". (The mStack index here is
  frame-relative, NOT the absolute-index convention of a top-level reference
  LOCAL -- because this index is RELATIVE to the VT's own ref sub-region base,
  which itself is frame-relative. Confirmed by `Move_Vt:1332`.)
- **nested IL-VT field** (`ft.IsValueType && ft is ILType`): recurse ONE level
  by synthesizing a sub-slot at the nested VT's primitive/ref bases (the nested
  field's split storage lives at the parent's field offset). Deeper nesting
  (depth > 1) falls back to a placeholder + note. (The F-4 indexer THROWS for an
  IL-VT field; the frame-local path recurses instead -- a strictly richer
  behavior, bounded and cheap.)

The rendered string is `"{ Type Name = Value, ... }"`, format-parity with how a
caller would want a struct expanded. A field read that throws renders
`"<unreadable fN>"` (swallowed) so one bad field does not abort the whole struct
-- mirrors the per-iteration resilience of the caller's loop
(`DebugService.cs:368-372`) and `GetThisInfo` (`:259`).

### 2.3 Why this is byte-correct by construction

The field-walk reuses the SAME three sources of truth the storage allocator and
the F-4 indexer use, so it CANNOT disagree with the actual slot layout:
1. `ilType.GetFieldOffset(i)` -- the SAME offsets a heap instance's F-4 read uses
   (`ILTypeInstance.cs:423`), computed by the SAME `InitializeFields` pass that
   sized the local's `TotalPrimitiveSize` / `TotalReferenceCount`.
2. `ilType.GetField(i, ...)` -- the SAME field-type resolution the F-4 indexer
   keys on (`ILTypeInstance.cs:424`).
3. `ReadNeoFramePrimitive` -- the SAME AppDomain-singleton-keyed switch
   neo-debugger-neo-frame shipped (the `byte*` analogue of the F-4 `byte[]`
   `ReadNeoPrimitive`, `ILTypeInstance.cs:547-579`).

The ONLY structural difference from a heap F-4 read is the region base: `byte[]
Primitives` -> `frameBase + slot.Offset`, `AutoList ManagedObjects` ->
`mStack[frameRefBase + slot.RefOffset]`. The per-field offsets and shape test are
identical.

## 3. The capstone (the self-check gate)

Extended `NeoDebuggerFrameCheck` (`ILRuntime/Runtime/Debugger/
NeoDebuggerFrameCheck.cs`) with two new cells (Cells 4 + 5), mirroring the
existing `RunValueCell` shape:

- **Cell 4 (`ProbeVtLocal`)**: the VT local holds `X = 4242` (primitive
  sub-region) + `S = "vt-field-A"` (reference sub-region). Asserts BOTH field
  values appear in the reconstructed `.LocalInfo`.
- **Cell 5 (`ProbeVtLocalMutate`, ADVERSARIAL)**: assigns `vt.X = 1111` then
  MUTATES to `8888`. Asserts the reconstruction reads the LIVE frame bytes
  (8888), NOT the stale 1111 -- proving the field-walk reads the live primitive
  sub-region, not a default/cached value.

The probe type gains a `VtLocal` struct (`int X; string S;`) and the two probe
methods (`TestCases/NeoDebuggerFrameProbe.cs`). The methods are INSTANCE +
PARAMETERLESS (so `appdomain.Invoke(m, inst)` drives them and `GetThisInfo` is
exercised) and throw UNHANDLED (so the `ILRuntimeException` ctor inspects THIS
frame).

**Stash-toggle (the load-bearing gate):** with the fix's IL-VT branch reverted to
the placeholder, Cells 4 + 5 FAIL (4/6) -- `.LocalInfo` shows `<IL value-type
local: reconstruction deferred>` and both field values are absent. With the fix
restored, all 6 cells PASS (6/6). This binds the fix to the behavior.

## 4. Scope boundary

- **IL-VT LOCALS (in-frame)**: the core. SHIPPED.
- **Nested VT field (a VT field that's itself a VT)**: recurses ONE level if
  cheap; deeper nesting -> placeholder + note. SHIPPED (bounded).
- **CLR-VT local**: a SEPARATE shape (F-MAJ-1 flat managed bytes), already
  handled by `ReadNeoValueType` in the neo-frame child. UNCHANGED.
- **AOT bodies**: `registerSymbols` stays null on AOT (`ILMethod.cs:997-998`);
  this child is JIT-path only (reads `Definition.Body.Variables`, present on the
  JIT path). Out of scope (separate child `neo-debugger-aot-body`).

## 5. Verification plan

- **NeoDebuggerFrame gate**: 6/6 cells (4 original held + 2 new VT-local).
  Stash-toggle: placeholder -> 4/6 (the 2 VT-local cells fail); fix -> 6/6.
- **NeoStep smoke**: 253/0/0 (no regression; the change is Neo-only and touches
  only the debugger inspection path, not the interpreter/JIT).
- **Legacy-neutral**: plain `Debug` build of `ILRuntimeTestCLI` = 0 errors (all
  Neo arms under `#if ENABLE_NEO_MODE`).

## 6. Durable findings (carry forward)

- The in-frame IL-VT local's reference sub-region is `mStack[frameRefBase +
  slot.RefOffset + field.ReferenceOffset]` -- FRAME-RELATIVE indexing (relative
  to the VT's own ref sub-region base), NOT the absolute-mStack-index convention
  a top-level reference LOCAL uses. The two conventions coexist in the same
  `ReadNeoLocalValue`: top-level reference local -> absolute `mStack[idx]`;
  IL-VT-local's reference FIELD -> frame-relative `mStack[frameRefBase +
  slot.RefOffset + off.ReferenceOffset]`. This is NOT a contradiction: a
  top-level reference local's slot word STORES an absolute index (written by the
  executor), while an IL-VT local's reference FIELDS are laid out contiguously
  from the VT's frame-relative ref base (by the allocator + Move_Vt). Confirmed
  by `Move_Vt:1332` and the allocator at `JITCompiler.cs:1716-1728`.
- The F-4 indexer THROWS a tagged NIE for an IL-VT FIELD
  (`ILTypeInstance.cs:437`), but the frame-local path can RECURSE (it has the
  field's offsets + the frame bases). So the debugger's IL-VT-local
  reconstruction is STRICTLY RICHER than the heap IL-VT-field read. A future
  child could lift the same recursion into the F-4 indexer to close the heap
  IL-VT-field gap (the placeholder is the same shape, only the bases differ).
- `StackSlotInfo` is an `internal` struct in
  `ILRuntime.Runtime.Intepreter.RegisterVM` (`JITCompiler.cs:67`) with public
  fields `{Offset, RefOffset, Size, RefCount}` -- constructible via
  `new StackSlotInfo { Offset = ..., ... }` from `DebugService` (same assembly,
  `using` the namespace). This makes synthesizing a sub-slot for a nested VT
  field trivial.
