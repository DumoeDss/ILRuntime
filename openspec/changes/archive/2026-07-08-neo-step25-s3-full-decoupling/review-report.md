# Review Report: neo-step25-s3-full-decoupling (S3 PARTIAL — sub-surface 1)

> Reviewer = verifier (author != verifier). Independent re-run of all gates +
> adversarial probing of the rebuild. HEAD at review: working tree of
> `features/object-model-overhaul` (S3 partial-ship, uncommitted).
> Method per CLAUDE.md: CLI built `Debug_Neo`, TestCases built plain `Debug`,
> every run `-f net8.0`, each NeoStep filter run separately (`Contains`, no `|`).

## VERDICT: APPROVE-WITH-FINDINGS

The partial-ship is SOUND and HONEST. Sub-surface 1 (ILType AOT-init rebuild
from `NeoTypeDefRecord`) is delivered exactly as specified; sub-surfaces 2/3/4/5
are honestly deferred (no promotion). The CRITICAL shared-`ILType.cs` risk is
**Legacy-neutral PROVEN** by stash-toggle. The mutation cells are **load-bearing
PROVEN** by a gold-standard adversarial break. No Blocker, no Major. Two
INFORMATIONAL forward-compat notes (both already documented in design.md /
the spec delta; neither blocks this slice).

| # | Finding | Severity | Disposition |
|---|---------|----------|-------------|
| I1 | `naturalAlignment` re-derivation handles only simple named field types (array / byref / generic-instance field types skipped on miss). | Informational | Accepted — documented (design D2 gap a); structural-equiv cell would catch a mismatch; probe is simple (int/long/string). Forward-compat for sub-surface 2. |
| I2 | `ResolveVTableFromRecord` skips `mr.IsGenericInstance` method refs (counted `unresolved`). | Informational | Accepted — documented forward signal for sub-surface 2 (a Cecil-free load needs every slot); no generic virtuals in the probe VTable. |

---

## 1. Independent gate re-runs (all green, match implementer's claims)

CLI build (`Debug_Neo`) = **0 errors**. TestCases build (plain `Debug`) = **0 errors**.

| Gate | Result | Implementer claim | Match |
|------|--------|-------------------|-------|
| `NeoStep25LoadExec` capstone | **28/28 cells, 0 failed** (21 S1/S2 + 7 S3; all 7 S3 cells PASS) | 28/28 | YES |
| `NeoStep` smoke (`Debug_Neo` + useRegister=true) | **Ran 218, 0 failed, 1 ignored** | 218/0/1 | YES |
| `NeoStep22SelfCheck` | **55/55, 0 failed** | 55/55 | YES |
| `NeoStep23Roundtrip` | **15/15, 0 failed** | 15/15 | YES |
| `NeoStep24CliRoundtrip` | **5/5, 0 failed** | 5/5 | YES |

The 7 S3 cells (capstone tail):
- S3 compile (NeoStep25S3Probe -> .neo): PASS
- S3 TypeDef record located: PASS
- S3 layout structural-equiv (rebuild == Cecil): PASS
- S3 VTable structural-equiv (rebuild == Cecil): PASS
- S3 interface map structural-equiv (rebuild == Cecil): PASS
- S3 mutation: field offset (PASS = divergence, rebuild reads record): PASS
- S3 mutation: VTable slot swap (PASS = divergence, rebuild reads record): PASS

Build-cache gotcha checked: the capstone reports 28/28 (not the pre-S3 21), so the
Debug_Neo DLL genuinely reflects the change (a stale-cache build would still report 21).

---

## 2. Legacy-neutral (CRITICAL gate — ILType.cs is SHARED): PROVEN

The binding rigor #2 concern: the Neo-only builder must compile out of plain
`Debug`, and the Legacy failure set must be unchanged by the change. Verified by
stash-toggle of the 3 engine files
(`ILType.cs`, `NeoAssemblyLoader.cs`, `NeoStep25LoadExecCheck.cs`):

| Run | Plain-`Debug` build | NeoStep filter (useRegister=true) | Failure set |
|-----|---------------------|-----------------------------------|-------------|
| WITH change | 0 errors | Ran 218, 8 failed, 1 ignored | 8 tests (see below) |
| WITHOUT change (stashed) | 0 errors | Ran 218, 8 failed, 1 ignored | 8 tests (IDENTICAL) |

`diff` of the two failure sets = **empty**. The 8 pre-existing failures (identical
both ways) are all NeoStep6/13/14/15/16, unrelated to S3:
`NeoStep13Test.NeoTestClrStructNoBindingBoxRoundTrip`,
`NeoStep13Test.NeoTestClrStructWithBinderBoxRoundTrip`,
`NeoStep14Test.NeoStep14_TC1_BasicTryCatch`,
`NeoStep14Test.NeoStep14_TC5_NestedInnermostWins`,
`NeoStep14Test.NeoStep14_TC8_NullRefCatch`,
`NeoStep15Test.NeoStep15_TC6_CastclassFailureCaught`,
`NeoStep16Test.NeoStep16_TC8_StelemI_NIntArray`,
`NeoStep6Test.NeoNaNR8`.

Also confirmed by code reading: the entire `RebuildFromNeoRecord` builder + the
`NeoVTableSlotKeysForAOT` accessor are within a single `#if ENABLE_NEO_MODE` block
(ILType.cs:761 opens, :916 closes); the following shared method
(`TryGetInterfaceMethodSlot`, :922) resumes unchanged. No shared-runtime statement
is altered. Legacy-neutral is proven both empirically (stash-toggle) and structurally
(`#if` boundary). Stash popped cleanly; builder restored.

---

## 3. The rebuild does NOT install on a live ILType (binding rigor #3): CONFIRMED

`RebuildFromNeoRecord` is `internal static`, returns a `NeoTypeRebuild` struct (a
pure-data DTO in NeoAssemblyLoader.cs). Inside the builder the `iltype` parameter is
**only read**: `var domain = iltype.AppDomain;` (ILType.cs:834) and passed to
`ResolveVTableFromRecord` (ILType.cs:851, which itself only reads `iltype.AppDomain`
and calls `declType.GetMethod`). No field on the live `ILType` is written — the
builder writes exclusively to the returned `rb` struct.

Independently confirmed by the mutation cell's behavior: cell (4) reads
`cecilF0 = s3ProbeType.GetFieldOffset(start+0).PrimitiveOffset` AFTER rebuilding
from a MUTATED `model2`. If the rebuild had overwritten `s3ProbeType.fieldOffsets[0]`
to match the mutated record, `cecilF0` would equal `mutatedPO` and `div0` would be
false -> the cell would FAIL. The cell PASSES (`div0` true), so no overwrite. The
rebuild is comparison-only; the Cecil init path stays the sole init path.

---

## 4. The mutation cells are load-bearing (binding rigor #4): GOLD-STANDARD PROVEN

The binding concern: structural-equivalence alone is INSUFFICIENT (the record is
built FROM Cecil's values at serialize time, so equality can hold trivially even if
the rebuild ignores the record). The mutation cells must prove the rebuild genuinely
reads the record. Verified by an adversarial break:

**Break:** temporarily changed `RebuildFromNeoRecord` (ILType.cs:824) to read the
Cecil primitive offset instead of the record's:
`rb.FieldPrimitiveOffsets[i] = iltype.GetFieldOffset(iltype.FieldStartIndex + i).PrimitiveOffset;`
(simulating "rebuild ignores the record"). Rebuilt `Debug_Neo`, ran the capstone.

**Result (exactly as design D3 predicted):**
- S3 layout structural-equiv: **STILL PASS** (Cecil == Cecil -> proves structural-equiv alone is insufficient)
- S3 layout mutation (field offset): **FAIL** (the cheated rebuild reads Cecil, so `readMutated = rb2.F[0] == mutatedPO` is false)
- Tally: **27/28, 1 failed**

**Reverted the break -> capstone restored to 28/28.** This is decisive:
1. The structural-equivalence cell passes EVEN WHEN the rebuild ignores the record
   (confirming it cannot be the sole proof).
2. The mutation cell FLIPS to FAIL when the rebuild ignores the record (confirming
   it is the load-bearing proof).

The mutation cells genuinely prove the rebuild reads the `NeoTypeDefRecord`. The
VTable-slot-swap mutation cell (cell 5) was not broken (only the layout read was
touched) and remained PASS, consistent with the break being scoped to the layout path.

Additional reasoning for cell (4): `mutatedPO = origPO + 9999` is a value no Cecil path
produces (offsets are tiny), so `readMutated = rb2.F[0] == mutatedPO` can only be true
if the rebuild copied `rec.Fields[0].PrimitiveOffset`. And model2's record does NOT
share memory with `s3ProbeType` (model2 is freshly deserialized from its own
MemoryStream), so the divergence cannot be a Cecil-cache artifact. Cell (5)'s
`headNowIsOrigTail = VTableSlotEqual(rb3.VTable[0], cecVT[last])` similarly proves the
swap into the record is observable in the rebuilt VTable.

---

## 5. VTable rebuild covers INHERITED slots (binding rigor #5): CONFIRMED

The probe (`NeoStep25S3Probe : NeoStep25S3Base, INeoStep25S3Iface`) has a non-trivial
VTable. Temporary diagnostic dump of the structural-equiv cell showed:

```
VTable len=6 unresolved=0
VT[0] System.Object::Finalize           (inherited CLR-Object)
VT[1] System.Object::ToString           (inherited CLR-Object)
VT[2] System.Object::Equals(Object)     (inherited CLR-Object)
VT[3] System.Object::GetHashCode        (inherited CLR-Object)
VT[4] TestCases.NeoStep25S3Probe::BaseVirtual(Int32)   (overridden base-IL virtual)
VT[5] TestCases.NeoStep25S3Probe::IfaceMethod(Int32)   (interface impl)
```

All 6 slots matched Cecil slot-by-slot, `unresolved=0`. `ResolveVTableFromRecord`
(NeoAssemblyLoader.cs:300-336) is **hierarchy-aware**: each slot's `MethodRef`
declaring type is compared to `iltype.FullName`; when it differs (the 4 Object
slots, declaring on `System.Object`), the declaring type is resolved by name via
`LoadedTypes` / `appdomain.GetType`, then `GetMethod(name, paramCount, false)` on
that type. The fallback to `iltype.GetMethod(...)` (which walks the full base
hierarchy) covers any unresolvable declaring-type name. So inherited CLR-Object +
base-IL + interface-impl slots are all reconstructed from the record, not dropped.
(Diagnostic reverted; tree clean.)

---

## 6. Deferred-items honesty (binding rigor #6): CONFIRMED

`.trae/documents/neo-deferred-items.md` STEP-25-PARTIAL row (master table line 97)
records: "S3 PARTIAL 2026-07-08 ... sub-surface 1 ... SHIPPED; sub-surfaces 2/3/4/5
deferred" with the specific deferral reasons:
- 2: Cecil-free AppDomain load (`LoadAssembly(Stream)` needs a Cecil module + a new
  ILType factory + injection into mapType/mapTypeToken).
- 3: cross-AppDomain APPROACH-1 token-hash re-resolution (record compile-time
  `GetHashCode()` per ref under a `.neo` Version bump; Approaches 2/3 REJECTED).
- 4: static `.cctor` seeding (`.cctor` suppressed under Neo + per-static-field
  offsets NOT in the record).
- 5: full CLR aqname / host-CLR-assembly registration (the Step-24 `TestCLREnum` gap).

The spec delta (`specs/neo-optimizer/spec.md`) has exactly 2 ADDED requirements:
(A) the rebuild is proven by structural-equiv + mutation (SHIP), and
(B) the Cecil-free load / cross-AppDomain / `.cctor` / CLR registration REMAIN
DEFERRED with explicit "SHALL NOT" / "SHALL remain" language and the recorded
follow-ups. No deferral is promoted to "met". The SHIP-vs-DEFER split is honest.

---

## 7. Spec coherence (binding rigor #7): CONFIRMED

The implementation matches the spec delta:
- Rebuild reconstructs instance layout (`TotalPrimitiveSize`,
  `TotalReferenceCount`, per-field `PrimitiveOffset`/`ReferenceOffset`) + Neo VTable
  (`IMethod[]` slots + re-derived slot-key map + interface offset map). All present.
- Builder does NOT install on a live ILType (spec scenario "rebuild is consumed ONLY
  by the DEBUG self-check"). Confirmed (section 3).
- `naturalAlignment` re-derived + compared; per-static-field offset gap deferred
  with sub-surface 4. Matches design D2.
- The capstone-hook scenario: the existing `NeoStep25LoadExecCheck.Run` runs the new
  cells; loader/helper additions additive + Neo-only; no new CLI hook; S1/S2 attach
  flow unchanged. Confirmed.
- Scenarios for layout / VTable / mutation / capstone hook all delivered; deferral
  scenarios (cross-AppDomain APPROACH 1; `.cctor`; rebuild is the ONLY ILType
  decoupling) all honored. No scope creep, no missing SHIP-slice requirement.

---

## 8. Standards + Spec axis (two-pass over the diff)

**Standards — CLEAN.** All engine edits are `#if ENABLE_NEO_MODE`; the self-check is
`#if ENABLE_NEO_MODE && DEBUG`. The builder is a static, side-effect-free (on the live
type) function returning a POD struct. `ResolveNamedIType` / `NaturalSizeOfFieldType`
mirror `InitializeFields`' alignment accumulator and floor at 1. `ResolveVTableFromRecord`
reports misses via `unresolved` (loud, not silent) rather than crashing. Mutation cells
use independent `model2`/`model3` (good isolation) and assert LOCALIZED divergence
(F[0] diverges, F[1] still equal; head+tail swap observable). Helpers
(`FindTypeDefByName`, `VTableSlotEqual`, `MethodLabel`, `StringArrayEqual`,
`IntArrayEqualNullTolerant`) are clean and null-tolerant. `NeoTypeRebuild` is a mutable
struct with array fields (a minor smell), but it is an internal DEBUG-only DTO, acceptable.

**Spec — CLEAN.** Diff faithfully implements proposal.md / design.md / tasks.md for the
SHIP slice; sub-surface 5 stretch task explicitly took the documented ELSE/DEFER branch
(tasks 5.1/5.2 marked DEFERRED with rationale). No requirement missing, no scope creep.

---

## 9. Artifacts touched (all absolute paths)

- `E:\AI\ChatAI\Agents\VibeCodingProjects\ILRuntime\ILRuntime\CLR\TypeSystem\ILType.cs` (SHARED; Neo-only block :761-916)
- `E:\AI\ChatAI\Agents\VibeCodingProjects\ILRuntime\ILRuntime\Runtime\NeoAOT\NeoAssemblyLoader.cs` (Neo-only helper :300-336 + `NeoTypeRebuild` struct)
- `E:\AI\ChatAI\Agents\VibeCodingProjects\ILRuntime\ILRuntime\Runtime\Intepreter\RegisterVM\NeoStep25LoadExecCheck.cs` (DEBUG+Neo; 5 new cells + helpers)
- `E:\AI\ChatAI\Agents\VibeCodingProjects\ILRuntime\TestCases\NeoStep25S3Probe.cs` (new probe; 3 instance fields + base virtual override + interface impl)
- `E:\AI\ChatAI\Agents\VibeCodingProjects\ILRuntime\.trae\documents\neo-deferred-items.md` (STEP-25-PARTIAL row)
- `E:\AI\ChatAI\Agents\VibeCodingProjects\ILRuntime\.trae\documents\neo-handoff.md` (HEAD/status)

## 10. Reviewer state on exit

Working tree restored to the implementer's exact change (adversarial break + VTable
diagnostic reverted; `grep REVIEWER-ADVERSARIAL|REVIEWER-DIAG` = clean). Debug_Neo CLI
artifacts rebuilt from the restored source. Capstone re-confirmed 28/28 after restore.
No edits left behind by the reviewer.
