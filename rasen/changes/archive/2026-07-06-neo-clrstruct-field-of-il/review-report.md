# Review Report -- neo-clrstruct-field-of-il (F-10)

**Reviewer:** adversarial, non-author. **Date:** 2026-07-06.
**Base:** `master`. **Working tree:** UNCOMMITTED (reviewed `git diff HEAD`).
**Verdict: APPROVE WITH MINOR FINDINGS.** The change is correct, well-scoped,
and the load-bearing blast-radius claim (Stfld_Ref/Ldfld_Ref byte-identical
for every non-F-10 shape) is verified. No Blocker. One Major latent gap
(F-10/F-6 marker not actually mutually-exclusive at the JIT discriminator --
unexercised by smoke) + two Minor doc/coverage items. None block archive.

---

## Summary of verification (independently reproduced)

| Claim | Reproduced | Evidence |
|---|---|---|
| NeoStep smoke 190/190 | YES (twice -- before & after stash-restore) | `Ran 190 tests, 0 failed` |
| NeoStep20 smoke 9/9 | YES | `Ran 9 tests, 0 failed` |
| F-10 probes 7/7 pass | YES | `Ran 7 tests, 0 failed` (`NeoClrStructField` filter) |
| Stash-toggle: F-10 probes FAIL-on-HEAD | YES | discriminator -> `false`: `Ran 7, 6 failed` (the 7th = `OtherFieldTypes_Regression`, a non-F-10 guard, correctly passes both ways) |
| Stash-toggle: TC4/TC6 FAIL-on-HEAD | YES | with fix stashed: NeoStep20 `Ran 9, 2 failed` (TC4 + TC6, both `IndexOutOfRangeException` at `ILIntepreter.Neo.cs:2119` -- the F-10 OOB); TC1/TC7 stay green (the layout-accident guards) |
| Restore verified | YES | after restore: NeoStep 190/190 again |
| Build (CLI `Debug_Neo` + TestCases `Debug`) | YES | 0 errors both |

Build-cache gotcha earned: `dotnet build` reported "0 errors" in ~1.8s
WITHOUT recompiling TestCases (the DLL did not contain the new probe
symbols). Fixed via `--no-incremental` on BOTH projects. The smoke then
ran the real probes (JIT body dump of `NeoClrStructField_RegisterReuseEscape`
showed `stfld.ref ... 0x00000004` + `ldflda r9, r0, 0x00000004` confirming
the F-10 opcodes are emitted). This is the documented
`strings -e l`-unreliable / incremental-hash-hit gotcha -- the implementer
appears to have hit it too; non-blocking but worth re-flagging.

---

## MANDATORY PROBE 1 -- Stfld_Ref/Ldfld_Ref blast-radius sweep (HIGHEST PRIORITY)

The design premise ("Stfld_Ref/Ldfld_Ref already correct; only ldflda broken")
was DISPROVEN by the implementer's Block-0 dump and the fix correctly makes
all three arms consistent. Verified the discriminator `ip->Operand4 != 0`
fires ONLY for the F-10 case and is byte-identical for every other shape.

| # | Field shape | Opcode selected | Operand4 at JIT | Runtime path | Correct? |
|---|---|---|---|---|---|
| a | IL-instance ref-type field (string, IL class) | `Stfld_Ref`/`Ldfld_Ref` | 0 (`IsClrStructFieldOfIL` false: fieldType not IsValueType) | existing `mStack[srcIdx]` / `ManagedObjects[Operand3]` | BYTE-IDENTICAL (existing path) |
| b | CLR-ref field | `Stfld_Ref`/`Ldfld_Ref` | 0 | existing path | BYTE-IDENTICAL |
| c | CLR-object field | `Stfld_Ref`/`Ldfld_Ref` | 0 | existing path | BYTE-IDENTICAL |
| d | IL-VT field | `Stfld_Value`/`Ldfld_Value` (NOT Ref) | n/a (different opcode) | not reached | BYTE-IDENTICAL |
| e | F-10 CLR-struct field of IL instance | `Stfld_Ref`/`Ldfld_Ref` | `fieldType.GetHashCode()` | new box/unbox path | CORRECT (new) |
| -- | IL-primitive field | `Stfld_I4`/`Ldfld_I4`/etc (NOT Ref) | n/a | not reached | BYTE-IDENTICAL |

Discriminator logic verified at `JITCompiler.cs:2405, 2458` (Operand4 stamped
only inside `if (type is ILType) { ... if (IsClrStructFieldOfIL(...)) }`) and
`ILIntepreter.Neo.cs:2778-2802` (Ldfld_Ref) / `2844-2862` (Stfld_Ref):
`if (ip->Operand4 != 0) { F-10 branch } else { existing }`. At HEAD these arms
never read Operand4 (confirmed via `git show HEAD:...ILIntepreter.Neo.cs`),
so the change is additive and byte-identical when Operand4==0.

Probe 4.6 (`NeoClrStructField_OtherFieldTypes_Regression`) directly exercises
shapes (a) CLR-ref + IL-primitive and is green; it is the one F-10-suite probe
that PASSES on HEAD (correct -- it does not touch a CLR-struct field).
The hash-zero edge case ("a field whose type hash is exactly 0") is
astronomically rare and falls back to the existing path (does not corrupt
other fields) -- accepted-known, documented in design.md OQ2.

**Blast radius: CONFIRMED SAFE.** No regression to any existing ref-field
store/load. This is the load-bearing review and it passes.

---

## MANDATORY PROBE 2 -- F-10 encoding non-collision

- **Operand4 bit 0x2 (F-10) vs bit 0x1 (F-6):** the design CLAIMS mutual
  exclusivity (F-6 = in-frame VT source; F-10 = heap IL ref source). The F-6
  marker is stamped in the type-spec pass on the SOURCE register
  (`srcType is ILType && IsValueType`, line 865). The F-10 marker is stamped
  in Translate on the DECLARING type + field type (`IsClrStructFieldOfIL`,
  line 2433). These two conditions are NOT logically mutually exclusive --
  see Major finding F-10-R1 below. For the heap-IL-ref source (the F-10
  target shape) F-6 does not fire, so the shipped case is collision-free.
- **`NeoF10ByrefOffsetFlag = 0x40000000` (bit 30):** confirmed safe. Real
  `ReferenceOffset` values are tiny ref-slot indices (a type with N ref
  fields -> max N-1; bit 30 ~= 1.07e9 is unreachable). Bit 30 avoids the
  sign bit (bit 31) so the offset stays a positive int. Consumers mask with
  `off & ~flag` to recover the index. No collision with real offsets.
- **No other Ldflda/Stfld/Ldfld Operand4 use collides:** Operand4 writers at
  `JITCompiler.cs:487,517` are `IsIntermediateBranching` opcodes (branches,
  not field access); `1960,2032,2055-2064,2713-2733` are Call/Callvirt/etc.
  opcodes. The Ldfld/Stfld/Ldflda Operand4 stamp is gated solely by
  `IsClrStructFieldOfIL`. Clean.

---

## MANDATORY PROBE 3 -- elemType-recovery fallback (null/uninit case)

`NeoMarshalByrefFieldToSlot` write branch (`ILIntepreter.Neo.cs:393-410`):
when `elemType == null`, recovers `boxType = ili.ManagedObjects[refOff].GetType()`.
- **Common case (boxed struct present):** works -- `existing.GetType()` is the
  CLR struct type, `ReadNeoValueType` boxes the slot's flat bytes into it.
- **Null/uninit slot AND null elemType:** `boxType` stays null -> `throw new
  NotImplementedException("neo-clrstruct-field-of-il: ... cannot box the CLR
  struct")`. **LOUD, not silent corruption.** Confirmed by reading the code.
- **Read branch with null boxed struct (`ILIntepreter.Neo.cs:420-424`):**
  `Unsafe.InitBlock(slot, 0, sz)` -- returns `default(struct)`. This is a
  defined default for a zeroed field (matches CLR semantics), NOT silent
  corruption. Acceptable.

Probe 3 verdict: the null/uninit write case fails loud (tagged NIE). Good.

---

## MANDATORY PROBE 4 -- TC2/TC3/TC5 re-trimmed Step-20 edges

TC2/TC3/TC5 were REMOVED from `TestCases/NeoStep20Test.cs` (only TC1/TC4/TC6/TC7
ship). The file comment (lines 22-39) documents each as a DISTINCT downstream
Step-20 redirect-coverage edge:
- TC2 SyncTask: non-generic `Task`'s `Start` redirect -- `Value cannot be null.
  Parameter 'stateMachine'`.
- TC3 SyncValueTaskOfT: ValueTask builder path -- NRE in the redirect.
- TC5 MultipleAwaits: multi-await `Task<int>.get_Result` redirect --
  `Method 'Task.Result' not found`.

These messages are credible Step-20 redirect-coverage gaps (NOT the F-10 OOB
signature `IndexOutOfRangeException`), and the stash-toggle confirms TC4/TC6
are the F-10 OOB cases (they fail with `IndexOutOfRange` on HEAD, pass after
fix). TC2/TC3/TC5 cannot be independently reproduced here (the probe code is
removed), but: (a) the green TC4/TC6 prove F-10 unblocks the async-void +
async-exception paths end-to-end; (b) the documented edges are
non-F-10-shaped; (c) they are explicitly scoped to `neo-step20-async` resume,
not silently dropped. **Consistent with "note it, don't force."** Minor
finding F-10-R3 (coverage): they are not regression-guarded in-tree.

---

## MANDATORY PROBE 5 -- TC4/TC6 genuine F-10 end-to-end

- **TC4 (`NeoStep20_TC4_AsyncVoidSync`):** async void SM -- its `<>t__builder`
  is a CLR-struct field of the heap IL SM instance. Asserts the host-side
  cell is set to 7 (the SM body ran). Stash-toggle: FAILs on HEAD with
  `IndexOutOfRangeException` at `ILIntepreter.Neo.cs:2119` (the F-10 OOB),
  PASSES after fix. Genuine F-10 end-to-end.
- **TC6 (`NeoStep20_TC6_AsyncExceptionFaultsTask`):** sync-thrown async -> the
  returned task must be `IsFaulted`. The SM's builder is a CLR-struct field of
  an IL instance. Stash-toggle: FAILs on HEAD (same OOB), PASSES after fix.
  Genuine F-10 end-to-end (the fault-propagation needs the builder-byref
  `SetException` redirect, which goes through the F-10 byref).

Both exercise the F-10 fix through the async SM builder-byref hot path. Not
trivially passing.

---

## MANDATORY PROBE 6 -- field-type regression (NeoStep12/12b/13/13b/17/19)

NeoStep 190/190 includes NeoStep12/12b (value types), NeoStep13/13b (CLR
struct), NeoStep17 (byref), NeoStep19 (delegates -- ref fields). All green
after the fix. The blast-radius table (Probe 1) confirms IL-primitive / IL-VT
/ CLR-ref / CLR-object fields are byte-identical. No regression.

---

## MANDATORY PROBE 7 -- NeoStep 190/190 independent reproduction

Reproduced twice (before stash + after restore): `Ran 190 tests, 0 failed`.

---

## MANDATORY PROBE 8 -- stash-toggle >=3 F-10 probes

Stashed `IsClrStructFieldOfIL -> false`, rebuilt CLI `--no-incremental`, ran:
- `NeoClrStructField` filter: `Ran 7, 6 failed` (>=3 probes FAIL-on-HEAD:
  LdfldaByValDeref, MultipleFields, StructWithRefField, StfldLdfld_Regression,
  ByValAfterLdfldaDeref, RegisterReuseEscape; the 7th `OtherFieldTypes_Regression`
  is the non-F-10 guard and correctly passes).
- `NeoStep20`: TC4 + TC6 FAIL-on-HEAD (the async-void + async-exception SMs).

All FAIL-on-HEAD -> PASS-after (confirmed by the 190/190 + 9/9 + 7/7 green
after restore). Confirmed.

---

## Findings

### F-10-R1 -- Major -- F-10/F-6 marker not actually mutually-exclusive at the JIT discriminator (latent)

**File:** `JITCompiler.cs:2433` (F-10 Translate stamp) + `:865-877` (F-6
type-spec stamp) + `ILIntepreter.Neo.cs:1100` (runtime `clrStructFieldMarker &&
objIdx >= 0` checked BEFORE F-6).

**Probe / reasoning:** The design and the in-code comment (JITCompiler.cs:142,
ILIntepreter.Neo.cs:1100-1111) claim F-6 (bit 0x1) and F-10 (bit 0x2) are
"mutually exclusive shapes (F-6 source is an in-frame VT, F-10 source is a
heap IL ref)." But the JIT discriminator does NOT check the source shape --
F-10 stamps whenever `IsClrStructFieldOfIL(declaringType, fieldType)` is true
(declaring=ILType, field=CLR value type, !primitive), regardless of source.
For an IL **value type** `struct V { TestVector3NoBinding f; }` with a method
taking `ref this.f` (e.g. `ldflda` inside a VT instance method):
  - declaringType `V` is ILType, fieldType is CLR struct -> F-10 marker 0x2 stamped.
  - source `Register2` is the in-frame VT `this` -> F-6 marker 0x1 ALSO stamped.
  - At runtime the operand slot holds the in-frame VT's FLAT BYTES; `objIdx =
    *(int*)(slot)` is the first field's value as an int (likely >= 0). The
    F-10 arm fires FIRST (`clrStructFieldMarker && objIdx >= 0`) -> produces
    `(flatByteGarbage, ReferenceOffset | flag)` -> `mStack[garbage]` ->
    IndexOutOfRange or wrong ILTypeInstance. The F-6 arm (shape 3, correct for
    in-frame VT) is never reached.

**Severity rationale:** Major, NOT Blocker. No smoke probe exercises an
IL-VT-with-CLR-struct-field + ldflda (the Step-12/12b structs are field-only;
the F-6 archive probe 4.8 is a byref-of-primitive local). The defect is latent.
The shipped F-10 case (heap IL ref source) is correct and collision-free. But
the "mutually exclusive" claim is wrong and the runtime precedence (F-10 before
F-6) would mis-dispatch the in-frame-VT case if/when it arises.

**Fix (narrow, future-proof):** either (a) gate the F-10 Translate stamp on the
source NOT being an in-frame VT (mirror the F-6 source check: only stamp F-10
when the source is a heap IL ref -- but Translate runs pre-type-spec, so the
source type may not be fully resolved there; safer in the type-spec pass), OR
(b) at runtime, check `inlineMarker` (F-6) FIRST and only fall to the F-10 arm
when `!inlineMarker`. Option (b) is a 1-line reorder and makes the precedence
match the documented mutual-exclusivity. Recommend (b).

### F-10-R2 -- Minor -- spec delta stale re: Stfld_Ref/Ldfld_Ref

**File:** `openspec/changes/neo-clrstruct-field-of-il/specs/neo-value-types/spec.md`
("The existing Stfld_Ref/Ldfld_Ref heap arms ... already handle a CLR-struct
field correctly ... this requirement does NOT change them") + `proposal.md:50-57`
(same claim).

The apply-phase resolution (design.md "Apply-phase resolutions") DISPROVED this
-- Stfld_Ref/Ldfld_Ref were also broken and the fix changed them. The spec
delta and proposal still assert the old (disproven) premise. The code is
correct; the docs are stale. Shipper should sync the spec delta to match the
apply resolution at archive (the design.md already records the truth, so this
is a doc-consistency nit, not a correctness issue).

### F-10-R3 -- Minor -- TC2/TC3/TC5 not in-tree regression-guarded

**File:** `TestCases/NeoStep20Test.cs` (TC2/TC3/TC5 removed). The three probes
progress past F-10 to distinct Step-20 redirect edges and are documented in the
file comment, but they are not kept (even as `[Ignored]`/NIE-expected) in-tree,
so a future regression that re-introduces the F-10 OOB for their SM shapes
would not be caught until `neo-step20-async` resume re-adds them. The green
TC4/TC6 + the 7 F-10 probes adequately guard the F-10 fix itself, so this is
coverage-polish, not a defect. Acceptable as-scoped (the design explicitly
defers TC2/TC3/TC5 to `neo-step20-async`).

### F-10-R4 -- Trivial -- extra blank line in TestClass3.cs

**File:** `ILRuntimeTestBase/TestFramework/TestClass3.cs:175` -- a double blank
line after `GetAsyncVoidCell`. Cosmetic.

---

## Verdict

**APPROVE WITH MINOR FINDINGS.** The change is correct, the highest-blast-radius
claim (Stfld_Ref/Ldfld_Ref byte-identical for all non-F-10 shapes) is verified,
the F-10 fix is genuine (TC4/TC6 + 7 probes FAIL-on-HEAD -> PASS-after, stash-
toggle reproduced independently), and NeoStep 190/190 + NeoStep20 9/9 are
green. The Major finding (F-10-R1) is a latent discriminator-precedence gap
unexercised by the smoke -- recommend the 1-line runtime reorder (check F-6
`inlineMarker` before the F-10 arm) as a follow-up or before archive; it does
NOT block this change because no reachable code path hits it today. The spec
staleness (F-10-R2) should be synced at archive. F-10-R3/R4 are cosmetic.

Recommend: ship after the F-10-R1 1-line fix (or file it as an accepted-known
follow-up with the same priority as the other `[NEO-...]` tracked edges), and
sync the spec delta at archive.
