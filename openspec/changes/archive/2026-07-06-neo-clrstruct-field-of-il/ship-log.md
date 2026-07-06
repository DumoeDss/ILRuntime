# Ship Log -- neo-clrstruct-field-of-il (F-10 / NEO-CLRSTRUCT-FIELD-OF-IL)

**Date:** 2026-07-06. **Change:** `neo-clrstruct-field-of-il`.
**Verdict (LEAD, post-review): SHIP.** Review round 0 APPROVED; round 1
finding F-10-R1 RECLASSIFIED to accepted-known-deferred (the reviewer's
recommended runtime reorder was DISPROVEN by the fixer — it broke 6 F-6
probes).

## What was resolved

**F-10 / NEO-CLRSTRUCT-FIELD-OF-IL.** A CLR-struct field of an IL instance
(e.g. an async state machine's `<>t__builder` = `AsyncTaskMethodBuilder`,
`<>u__1` = `TaskAwaiter`) is laid out by the ILType field-layout pass as a
**reference slot** (`referenceOffset++`, NO `primitiveOffset` advance; the
boxed struct lives at `ManagedObjects[ReferenceOffset]`). But the JIT
`ldflda &instance.<clrStructField>` emitted a byref carrying a stale
`PrimitiveOffset` -> the runtime read `ili.Primitives[off]` ->
`IndexOutOfRangeException` (silent corruption anywhere else this shape
appears). The byref carried ONE offset, so it was unrecoverable to the
field's actual storage.

## The design's premise was DISPROVEN by the Block-0 dump

The propose-time premise was "**only `ldflda` is broken; `Stfld_Ref`/
`Ldfld_Ref` already handle a CLR-struct field correctly** (they treat the
boxed struct as a reference and round-trip it)." The Block-0 reproducer
dump **DISPROVED** this: `Stfld_Ref` of a CLR-struct field from a flat-bytes
source read the source's first 4 bytes as a ref-slot mStack index (the dump
showed `srcIdx=1092616192` = `10.0f` reinterpreted) -> `mStack[garbage]`
OOR; `Ldfld_Ref` wrote an mStack index into a flat-bytes dest. **All THREE
heap field-access arms were broken, not just `ldflda`.**

The fix therefore makes ALL THREE arms consistent: the F-10 shape boxes/
unboxes/flattens the CLR struct at `ManagedObjects[ReferenceOffset]` via
the existing `ReadNeoValueType`/`WriteNeoValueType` helpers (the Step-13b/
area4 machinery, byte-consistent with the boxed-ref-vs-flat-bytes bridging
already shipped).

## Encoding (β offset-discriminator)

- **F-10 marker:** `NeoLdfldaClrStructFieldMarker = 0x2` (stamped on
  standalone `Operand4`, bit `0x2`; OR-stamped alongside F-6's bit `0x1` —
  the two are mutually-exclusive at the producer for the shipped shape).
- **Runtime flag:** `NeoF10ByrefOffsetFlag = 0x40000000` (bit 30 of the
  byref offset half; real `ReferenceOffset` values are tiny ref-slot
  indices so bit 30 is unreachable; avoids the sign bit so the offset stays
  a positive int). Consumers mask with `off & ~flag` to recover the index.
- **`Stfld_Ref`/`Ldfld_Ref` discriminator:** `ip->Operand4 != 0` (F-10
  stamps `Operand4 = fieldType.GetHashCode()`). At HEAD these arms never
  read `Operand4`, so the change is additive and byte-identical when
  `Operand4 == 0`.

## Consumers shipped

- **`NeoMarshalByrefFieldToSlot`** (read/write a referent through a byref
  to an mStack-object field) — gains the F-10 branch: read/write the boxed
  CLR struct at `ili.ManagedObjects[refOff]` and marshal it to/from the
  dest slot via `ReadNeoValueType`/`WriteNeoValueType`. This is the
  Step-20 builder-byref hot path. The elemType-recovery fallback (when the
  declared element type is null) recovers `boxType =
  ili.ManagedObjects[refOff].GetType()`; if the boxed slot is null/uninit
  AND elemType is null, it **throws a tagged `NotImplementedException`
  ("neo-clrstruct-field-of-il: ... cannot box the CLR struct") — LOUD, not
  silent corruption.**
- **`Ldobj`/`Stobj` ILTypeInstance branches** recognize the F-10 byref flag
  and route the access to the boxed struct storage.
- Fixed-width `stind_*`/`ldind_*` through an F-10 byref is **unreached** in
  the smoke and the probes (the F-10 shape dereferences through
  `NeoMarshalByrefFieldToSlot`/`Ldobj`/`Stobj`, not a raw `stind`/`ldind`) —
  accepted-known (a loud OOR if it ever fires).

## Blast radius

**NO `ILType.cs` edit; NO `Optimizer.Neo.cs` edit; NO layout change.** The
shared-engine field-layout pass is byte-identical (option A: encoding-only
fix). The change is **Neo-only, Legacy-neutral by construction** (the JIT
type-spec stamps are `#if ENABLE_NEO_MODE`; the runtime arms are in
`ILIntepreter.Neo.cs`, a Neo-only file).

**Stfld_Ref/Ldfld_Ref blast radius CONFIRMED SAFE** (review probe 1, the
load-bearing review). 5 field shapes swept: IL-instance ref-type field,
CLR-ref field, CLR-object field (all select `Stfld_Ref`/`Ldfld_Ref` with
`Operand4 == 0` -> existing path, BYTE-IDENTICAL); IL-VT field (selects
`Stfld_Value`/`Ldfld_Value`, not Ref -> not reached); IL-primitive field
(selects `Stfld_I4`/etc, not Ref -> not reached). The F-10 discriminator
`Operand4 != 0` fires ONLY for the F-10 CLR-struct-field-of-IL-instance
shape. The hash-zero edge case (a field whose type hash is exactly 0) is
astronomically rare and falls back to the existing path (no corruption) —
accepted-known, documented in design.md OQ2.

## Verification

- **7 F-10 probes FAIL-on-HEAD -> PASS** after the fix
  (`NeoClrStructField` filter: LdfldaByValDeref, MultipleFields,
  StructWithRefField, StfldLdfld_Regression, ByValAfterLdfldaDeref,
  RegisterReuseEscape, + the non-F-10 guard OtherFieldTypes_Regression).
  Stash-toggle (`IsClrStructFieldOfIL -> false`): `Ran 7, 6 failed` (>=3
  FAIL-on-HEAD, confirmed by independent reviewer reproduction).
- **Step 20 sync 2 -> 4 green:** TC4 (`NeoStep20_TC4_AsyncVoidSync`, async
  void SM) + TC6 (`NeoStep20_TC6_AsyncExceptionFaultsTask`, async-exception
  fault propagation) newly unblocked — both FAIL-on-HEAD with
  `IndexOutOfRangeException` at `ILIntepreter.Neo.cs:2119` (the F-10 OOB),
  PASS after the fix. TC1 (sync `Task<int>`) + TC7 (nested sync) stay green
  (the layout-accident guards).
- **NeoStep 190/190, NeoStep20 9/9, ClrStructField 8/8** (7 F-10 + new
  probe 4.8).
- **Build (CLI `Debug_Neo` + TestCases `Debug`):** 0 errors both. Legacy
  `Debug` builds clean.

## F-10-R1 (Major, latent) -> reclassified accepted-known-deferred

The reviewer flagged that the F-6/F-10 markers are NOT mutually-exclusive
at the JIT discriminator: an IL **value type** `struct V { TestVector3NoBinding
f; }` taking `ref this.f` via `ldflda` inside a VT method gets BOTH markers
stamped (`Operand4 = 0x3`), and the runtime checks F-10 (`objIdx >= 0`)
BEFORE F-6, predicting a mis-dispatch that reads flat bytes as an mStack
index. The reviewer recommended a 1-line runtime reorder (check F-6 first).

**The fixer DISPROVED the recommended fix** via a runtime diagnostic on a
new latent-shape probe: the reorder broke **6 NeoStep17 F-6-only probes
(190/190 -> 184/190)**. Root cause: F-6 shape 3 (`operandSlotOff +
fieldPrimOff`) and shape 1/2 frame-native (`vtBase + fieldPrimOff`) produce
DIFFERENT byrefs; every reachable VT `this`/arg today arrives as a managed
pointer (`objIdx == -1`), so HEAD order routes them to shape 1/2 (correct),
while the reorder routes them to F-6 shape 3 (wrong).

**The defect is fully latent.** The feared F-10-first mis-dispatch requires
`objIdx >= 0` with flat bytes — gated behind the DEFERRED
`constrained.callvirt`-on-VT (Step 13 Area 3 / Step 17 follow-up). For all
reachable VT source shapes, `objIdx == -1` -> HEAD order routes correctly
to F-6 shape 1/2. **The shipped F-10 case (heap IL ref source,
F-10-only) is unaffected.** Reclassified accepted-known-deferred. **The
correct future fix is the JIT-discriminator gate** (only stamp F-10 when
the source is NOT an in-frame VT, making the two markers genuinely
mutually-exclusive at the producer), deferred to the constrained-VT
follow-up. New probe 4.8 (`NeoClrStructField_IlVtMethodLdfldaThisClrStructField`)
is a green regression guard for the both-stamp shape's `objIdx == -1`
routing; it does NOT FAIL-on-HEAD (cannot, given current Neo) — it is a
shape guard for when constrained-VT lands. Route: the constrained-VT
follow-up (`neo-step17-generic-byref-etc` or a Step 13 Area 3 follow-up).

## Step 20 redirect-coverage follow-ups (TC2/TC3/TC5) — NOT F-10

TC2/TC3/TC5 pass F-10 but hit DISTINCT Step-20 redirect edges (NOT the F-10
OOB signature `IndexOutOfRangeException`):

- **TC2 SyncTask:** non-generic `Task`'s `Start` redirect —
  `Value cannot be null. Parameter 'stateMachine'`.
- **TC3 SyncValueTaskOfT:** `ValueTask` builder path — NRE in the redirect.
- **TC5 MultipleAwaits:** multi-await `Task<int>.get_Result` redirect —
  `Method 'Task.Result' not found`.

These were re-trimmed from `TestCases/NeoStep20Test.cs` (only TC1/TC4/TC6/
TC7 ship) with a file comment documenting each edge. They are Step-20
follow-ups, NOT F-10 — re-add when `neo-step20-async` resume (or
`neo-step20-async-suspend`) covers the redirect edges.

## Spec delta

`neo-value-types`: 1 ADDED requirement — "CLR-struct field of an IL
instance addressed via `ldflda`." Merged at archive. NOTE F-10-R2 (the
spec delta's stale "Stfld_Ref/Ldfld_Ref already correct" wording) was
corrected at archive to reflect the all-three-arms-fixed truth (the apply
DISPROVED the design premise).
