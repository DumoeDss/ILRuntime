# BLOCKED / DISPROVEN — neo-clrstruct-sm-field-layout (B1)

**Date:** 2026-07-10  **State:** PARKED AS DISPROVEN. The alleged B1 field-
layout-collision bug does NOT exist. No engine fix ships (none is needed). The
real VT1 root cause is re-routed to child-4's async-redirect scope.

## TL;DR

The B1 hypothesis ("an async `ValueTask<T>` SM's `<>t__builder` field and its
hoisted int local `v` both get Neo primitiveOffset 4 → the builder clobbers `v`
→ `v` reads 1 not 11") is **FALSE**. The two fields DO share the same
`PrimitiveOffset` value in the `ILType` field-layout, but this is **benign**:
the builder (a CLR-struct-with-ref field) stores its boxed struct at
`ManagedObjects[ReferenceOffset]` (the F-10 path), while `v` (an IL primitive)
stores at `Primitives[PrimitiveOffset]` — **disjoint arrays**. The shared offset
value cannot corrupt. Three independent confirmations (minimal reproducer
passes on HEAD; runtime diagnostic on VT1 shows `v` stores AND loads 11;
applying the proposed fix does not change VT1's outcome). The real VT1 bug is a
**call-argument-marshalling** issue in the async redirect.

## What the field-layout ACTUALLY does (the benign collision)

`ILRuntime/CLR/TypeSystem/ILType.cs`, `InitializeFields`, instance-field loop
(the `#if ENABLE_NEO_MODE` region). A CLR struct WITH a reference field
(`AsyncValueTaskMethodBuilder<T>` / `TestClrStructWithRef { int n; string s; }`)
is NOT an `ILType` and NOT a primitive → it takes branch 3: record
`(PrimitiveOffset=cursor, ReferenceOffset=refCursor)`, then `referenceOffset++`
with NO `primitiveOffset` advance. So the NEXT primitive field reuses the same
`primitiveOffset` value. **This collision-on-the-cursor is real.** But:

- The CLR-struct-with-ref field's `PrimitiveOffset` is **dead** — the runtime
  `Stfld_Ref` / `Ldfld_Ref` F-10 arms (keyed on `Operand4 != 0`) read/write
  `ManagedObjects[Operand3 = ReferenceOffset]`. They NEVER touch `Primitives`.
- The sibling primitive field's `Stfld_I4` / `Ldfld_I4` read/write
  `Primitives[Operand2 = PrimitiveOffset]`.

`Primitives[]` and `ManagedObjects[]` are disjoint. **No corruption.** This is
exactly why the F-10 sibling change chose option (A) (encoding-only, NO
`ILType.cs` layout change) — the layout is correct as-is for the F-10 storage
model.

## The three disproof confirmations

1. **Minimal reproducer PASSES on HEAD.** `TestCases/NeoClrStructFieldTest.cs`:
   `class HolderClrStructWithRefThenPrim { TestClrStructWithRef builder; int v; }`.
   Set `builder = default`, set `v = 11`, read `v` → 11. JIT dump: builder at
   `(primOff 0, refOff 0)`, `v` at `(primOff 0, refOff 1)` — primOff coincides,
   `v` unclobbered. (4 guards shipped; all PASS on HEAD.)
2. **Runtime diagnostic on VT1.** Temp diagnostics in `Stfld_I4` / `Ldfld_I4`
   (gated on the SM type): `stfld.i4 primOff=4 val=11` → `ldfld.i4 primOff=4
   val=11`. `v` stores 11 AND loads 11. (Removed.)
3. **The proposed fix does not help.** Applied "advance `primitiveOffset` by 4
   in branch 3" as a probe. VT1 still failed identically (`resultObj=4`,
   `frameBase[8]=4`). (Reverted; `ILType.cs` byte-identical to HEAD.)

## The REAL VT1 root cause (re-routed to child-4)

`AsyncValueTaskMethodBuilder_T_SetResult_Neo` (`CLRRedirections.AsyncNeo.cs:416`):

```csharp
int curPrim = 0;
curPrim += 8;   // skip the builder byref `this`
object resultObj = ReadResultParam(intp, method, frameBase, ref curPrim, mStack, retRefBase);
                  // reads the int result at frameBase[8]
```

A wider frame dump at SetResult entry:
```
VTSetResult resultObj=4 b4=0 b8=4 b12=<garbage> b16=14 ...
```
**`b16=14`** — the actual `int result` (`v+3` = 14) is at `frameBase[16]`. The
builder byref-`this` occupies **16 call-frame bytes** (8-byte F-10 byref
`(objIdx, offset|flag)` + 8-byte struct flat-bytes copy), but the redirect skips
only 8 → `ReadResultParam` reads `frameBase[8]` (stale residue = 4 = an old
`v=1` computation) instead of `frameBase[16]` (= 14).

This is a **call-argument-marshalling bug for a CLR-struct-byref-`this` followed
by a primitive param** in the async redirect — NOT a field-layout bug. It is
child-4's (`neo-async-valuetask-asyncvoid`) async-redirect scope. Note
`AsyncTaskMethodBuilder_T_SetResult_Neo` (TC8, which WORKS) also does
`curPrim += 8`; why TC8 reads correctly while VT1 does not is a call-convention
difference between the two builder structs' byref-`this` marshalling (likely the
Task builder's byref-`this` is 8 bytes and the ValueTask builder's is 16, OR the
surrounding arg layout differs) — child-4 must resolve.

## What IS shipped by this change

- **4 storage-disjointness regression guards** in
  `TestCases/NeoClrStructFieldTest.cs` (the F-10-family invariant: a
  CLR-struct-with-ref field + a sibling primitive field do not corrupt each
  other via the shared `PrimitiveOffset`). All PASS on HEAD.
- **The disproof record** (`design.md` + this `blocked.md` + `tasks.md`) so no
  future worker re-investigates the B1 field-layout hypothesis.

## What is NOT shipped (and why)

- **No `ILType.cs` change.** The field-layout is correct (the collision is
  benign). Confirmed `git diff HEAD -- ILType.cs` is empty.
- **No VT1/VT2/VT6 fix.** Re-routed to child-4 (the call-arg-marshalling bug
  above).

## Working-tree state (IMPORTANT)

The working tree has the **child-4 stash** (`child4-valuetask-blocked-partial`,
popped during investigation) applied: fixer-1's partial work in
`CLRRedirections.AsyncNeo.cs` (incl. `VTDBG2` diagnostics that MUST be removed
before child-4 ships), `DebugService.cs`, `ILIntepreter.Neo.cs` (the
stackalloc→heap Call-case fix), `TestClass3.cs`, + the VT1-6 probes in
`NeoStep20Test.cs`. **Those are child-4's, NOT this change's.** This change's
ONLY own edit is `TestCases/NeoClrStructFieldTest.cs` (the 4 guards + comments).

The `VTDBG2` diagnostics in `CLRRedirections.AsyncNeo.cs` (fixer-1's, line ~423
+ in GetResult/SetResult/get_Task) are still present (reverted my widening back
to the stash's original b4/b8/b12). Child-4's successor must `grep VTDBG2` and
remove them before child-4 ships.

## Verification

- **NeoStep smoke:** 273 ran, 5 failed. The 5 = VT1/VT2/VT3/VT4/VT6 (the parked
  child-4 cluster; unchanged from the lead-7 baseline). The 4 new guards pass.
  No regression.
- **ClrStructField filter:** 12/0 (8 F-10 + 4 new guards).
- **Legacy-neutral:** by construction — no engine/JIT/layout change ships.
  (`ILType.cs` byte-identical to HEAD.)
- **Stash-toggle:** N/A — there is no fix to toggle (the disproof is the
  deliverable; the guards PASS on HEAD by design, documenting the benign
  invariant).

## Next action (for the LEAD / child-4's successor)

1. **Do NOT pursue a field-layout fix for B1** — it is disproven. The shared
   `PrimitiveOffset` is benign (disjoint storage).
2. **Re-route the VT1/VT2/VT6 fix to child-4's async-redirect scope**: the
   builder byref-`this` skip in `AsyncValueTaskMethodBuilder_T_SetResult_Neo`
   (and likely the matching `SetException` / `AwaitUnsafeOnCompleted` redirects)
   must account for the 16-byte byref-`this` (8-byte F-10 byref + 8-byte struct
   flat-bytes), OR the call-arg marshalling must not inline the struct flat
   bytes for a CLR-struct-byref-`this`. Dump the call-frame layout for both
   `AsyncTaskMethodBuilder<int>` (TC8, works) and `AsyncValueTaskMethodBuilder
   <int>` (VT1, broken) SetResult to find the 8-vs-16-byte difference.
3. **Decide this change's fate**: archive (DISPROVEN, guards shipped) OR keep as
   the parked B1 record. Either way, the real VT fix is child-4's.
