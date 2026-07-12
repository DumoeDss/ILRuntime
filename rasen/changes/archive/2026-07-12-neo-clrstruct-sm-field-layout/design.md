# Design — neo-clrstruct-sm-field-layout (B1)

**Date:** 2026-07-10  **State:** INVESTIGATED → the alleged B1 bug is DISPROVEN.
No field-layout fix ships (none is needed). The real VT1 root cause is re-routed
to child-4's async-redirect scope. See `blocked.md`.

## Context (the mandate as received)

This change was chartered as the "B1 unblocker" for the parked child 4
(`neo-async-valuetask-asyncvoid`). The B1 hypothesis (from child-4 fixer-1 /
fixer-2): an async `ValueTask<T>` state machine (`<Method>d__N`, an IL CLASS)
has a `<>t__builder` field (a CLR struct WITH a reference field — it wraps a
Task) AND a hoisted primitive local `v` (int). The claim: in the Neo `ILType`
field-layout, BOTH `v` and `<>t__builder` get `primitiveOffset 4` → the builder
field's storage CLOBBERS `v` (silent wrong result: `v` reads 1 not 11, so
`v+3` = 4 not 14). The identical `Task<int>` SM (TC8, builder =
`AsyncTaskMethodBuilder<int>`) was said NOT to collide (different managed size
→ `v` lands elsewhere). The proposed fix: advance the primitive offset cursor
for a CLR-struct-with-reference field so a sibling primitive field does not
reuse its offset.

## The ILType field-layout allocation (the suspected site)

`ILRuntime/CLR/TypeSystem/ILType.cs`, `InitializeFields`, the instance-field
loop (the `#if ENABLE_NEO_MODE` region, ~:2881-2942). Three branches per field:

1. `fieldType.IsPrimitive` → record `(PrimitiveOffset=cursor, ReferenceOffset=
   refCursor)`; `primitiveOffset += GetPrimitiveSize(fieldType)`.
2. `fieldType.IsValueType && fieldType is ILType it` (an IL value type) →
   record both; `primitiveOffset += it.TotalPrimitiveSize;
   referenceOffset += it.TotalReferenceCount`.
3. `else` (a CLR struct that is NOT an ILType, OR a reference type) → record
   `(PrimitiveOffset=cursor, ReferenceOffset=refCursor)`; `referenceOffset++`,
   **`primitiveOffset` NOT advanced**.

A CLR struct WITH a reference field (`AsyncValueTaskMethodBuilder<T>` /
`TestClrStructWithRef { int n; string s; }`) is NOT an `ILType` and NOT a
primitive, so it takes **branch 3**: it occupies a reference slot
(`referenceOffset++`) and does NOT advance `primitiveOffset`. So the NEXT
primitive field reuses the same `primitiveOffset` value the struct field
recorded. **This part of the hypothesis is TRUE** — the offsets DO collide on
the cursor.

## The disproof: the collision is BENIGN (disjoint storage)

The F-10 sibling change (`archive/2026-07-06-neo-clrstruct-field-of-il`)
established the storage model for a CLR-struct field of an IL instance, and
crucially chose **option (A): encoding-only fix, NO `ILType.cs` layout change**.
Under that model:

- A CLR-struct-with-ref field's storage is the **boxed struct at
  `ManagedObjects[ReferenceOffset]`**. `Stfld_Ref` / `Ldfld_Ref` (the F-10 arms,
  keyed on `Operand4 != 0`) read/write `ManagedObjects[Operand3 =
  ReferenceOffset]` via `ReadNeoValueType` / `WriteNeoValueType`.
- A sibling IL-primitive field's storage is **`Primitives[PrimitiveOffset]`**.
  `Stfld_I4` / `Ldfld_I4` read/write `Primitives[Operand2 = PrimitiveOffset]`.

`Primitives[]` and `ManagedObjects[]` are **disjoint arrays** on
`ILTypeInstance`. So the fact that a CLR-struct field and a sibling primitive
field record the SAME numeric `PrimitiveOffset` value is **benign**: the struct
field never touches `Primitives` (its `PrimitiveOffset` is dead — only its
`ReferenceOffset` is used), and the primitive field touches only `Primitives`.
There is no storage overlap to clobber.

## Verification of the disproof (three independent confirmations)

1. **Minimal reproducer PASSES on HEAD.** A plain IL class
   `{ TestClrStructWithRef builder; int v; }` (the exact shape, no async
   machinery): set `builder`, set `v = 11`, read `v` → reads 11 correctly. The
   JIT dump confirms `builder` is at `(primOff 0, refOff 0)` and `v` is at
   `(primOff 0, refOff 1)` — the primOff values coincide, yet `v` is unclobbered
   (Primitives[0] vs ManagedObjects[0]). Added as permanent guards
   (`NeoClrStructField_ClrStructWithRefThenPrim_NoClobber` etc.); all PASS on
   HEAD. (Probes in `TestCases/NeoClrStructFieldTest.cs`.)

2. **Runtime diagnostic on the REAL VT1 SM.** Temporary diagnostics in
   `Stfld_I4` / `Ldfld_I4` (gated on the SM type name + primOff 4/8) printed:
   - `stfld.i4 primOff=8 val=11` (the temp hoist `<v>5__1`)
   - `ldfld.i4 primOff=8 val=11`
   - `stfld.i4 primOff=4 val=11 prim4=0` (`v` stored 11; Primitives[4] was 0
     before)
   - `ldfld.i4 primOff=4 val=11` (**`v` LOADS 11**)
   So `v` stores 11 AND loads 11. The field-layout is NOT the corruption site.

3. **The proposed B1 fix does NOT change VT1's outcome.** Applied the hypothesized
   fix (advance `primitiveOffset` by 4 in branch 3) and re-ran VT1: identical
   failure (`VTSetResult resultObj=4`, `frameBase[8]=4`). The layout change
   moved `v`'s primOff but VT1 still read the wrong result — proving the layout
   is not the cause. (Reverted; `ILType.cs` is byte-identical to HEAD.)

## The REAL VT1 root cause (re-routed to child-4 async-redirect scope)

`AsyncValueTaskMethodBuilder_T_SetResult_Neo`
(`CLRRedirections.AsyncNeo.cs:416`) reads the `int result` param via
`ReadResultParam` after skipping the builder byref `this`:

```csharp
int curPrim = 0;
curPrim += 8;                                  // skip builder byref `this`
object resultObj = ReadResultParam(... ref curPrim ...);  // reads frameBase[8]
```

A wider frame dump (`b4/b8/b12/b16/b20/b24`) at SetResult entry showed:
```
VTSetResult resultObj=4 b4=0 b8=4 b12=<garbage> b16=14 b20=<...> b24=<...>
```
**`b16=14`** — the actual `int result` (`v+3` = 14) is at `frameBase[16]`, not
`frameBase[8]`. The builder byref-`this` occupies **16 call-frame bytes**
(8-byte F-10 byref `(objIdx, offset|flag)` + 8-byte struct flat-bytes copy),
but the redirect skips only 8 → `ReadResultParam` reads `frameBase[8]` (stale,
= 4 = 1+3, i.e. an old `v=1` computation residue) instead of `frameBase[16]`.

This is a **call-argument-marshalling bug for a CLR-struct-byref-`this` followed
by a primitive param** in the async redirect — NOT a field-layout bug. It is
squarely child-4's (`neo-async-valuetask-asyncvoid`) async-redirect scope. The
`AsyncTaskMethodBuilder_T_SetResult_Neo` (TC8, the one that WORKS) also does
`curPrim += 8`; why TC8 reads correctly while VT1 does not is a call-convention
difference between the two builder structs' byref-`this` marshalling that child-4
must resolve (likely the Task builder's byref-`this` is 8 bytes and the ValueTask
builder's is 16, OR the surrounding arg layout differs).

## Goals / non-goals (revised)

**Goals:**
- Ship the durable storage-disjointness regression guards (the F-10-family
  invariant: a CLR-struct-with-ref field + a sibling primitive field do not
  corrupt each other via the shared `PrimitiveOffset`).
- Record the disproof definitively so no future worker re-investigates the B1
  field-layout hypothesis.

**Non-goals:**
- A field-layout change in `ILType.cs` — NONE needed (the collision is benign).
  `ILType.cs` is byte-identical to HEAD.
- Fixing VT1/VT2/VT6 — that is the call-arg-marshalling bug in child-4's async
  redirect (see above), re-routed there.
- B2 (the binder NIE for a CLR-struct-with-ref byref `this`) and B3 (the
  `CreateFaultedValueTask` AmbiguousMatch) — separate, as chartered.

## Decision

**D1: No `ILType.cs` change. The B1 field-layout-collision hypothesis is
DISPROVEN; the shared `PrimitiveOffset` is benign (disjoint Primitives vs
ManagedObjects storage).**

**D2: Ship 4 storage-disjointness regression guards** in
`TestCases/NeoClrStructFieldTest.cs` (the F-10-family invariant). They PASS on
HEAD and must stay green.

**D3: Re-route the real VT1 root cause** (the 8-vs-16-byte builder
byref-`this` skip in `AsyncValueTaskMethodBuilder_T_SetResult_Neo`) to child-4's
async-redirect scope. NOT this change.

## Risks / trade-offs

- **The guards do NOT FAIL-on-HEAD** (they pin a benign invariant, not a bug).
  This is documented in their header so a future worker does not mistake them
  for load-bearing reproducers.
- **No regression risk** — no engine/JIT/layout change ships. NeoStep smoke:
  273/5 (the 5 = the parked child-4 VT cluster, unchanged). Legacy-neutral by
  construction (no shared-engine edit).

## Migration plan

None (no engine change). The guards are additive test-only.
