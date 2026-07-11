# Review Report — neo-clr-static-vt-slot-overflow

**Reviewer:** author != verifier gate (autonomous LEAD run, Tier A, dispatched/report-only).
**Date:** 2026-07-12.
**Branch:** `features/object-model-overhaul` @ `43a74a85`.
**Diff under review:** 2 files — `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`
(the `AllocateLocalStackSpaces` `maxSize`/`maxAlignment` consumer loop) +
`TestCases/NeoStepClrVtStaticSlotOverflowTest.cs` (2 temp-shape probes).
(An unrelated `rasen/changes/neo-overhaul/planning-context.md` modification is LEAD
portfolio bookkeeping — child-tracking / durable planner findings, not code, not in this
change's declared scope. Not a finding.)

---

## Verdict: **APPROVE**

Minimal, purely-additive, correctly-targeted fix at the genuine root cause. The change
sizes the uniform Neo eval-temp file to fit gathered **CLR** value types (not only IL
value types), using the SAME managed-size source every other Neo VT site uses, so the dest
size and the runtime write size agree byte-for-byte. The slot-overflow AV guard is
byte-for-byte unchanged. All five independent re-verifications pass, including the key
layout-regression gate (broad NeoStep 348/0) and the stash-toggle (2/2 FAIL → 2/2 PASS).

---

## 1. CLRType-arm correctness — CONFIRMED

The added arm (`JITCompiler.cs:2223-2240`) is an `else if (i is CLR.TypeSystem.CLRType ct)`
sibling of the existing `is ILType il` arm:

```csharp
int size = Optimizer.GetNeoValueTypeManagedSize(ct.TypeForCLR);
if (size > maxSize) maxSize = size;
int align = size >= 8 ? 4 : size;
if (align > maxAlignment) maxAlignment = align;
```

- **Size source is canonical.** `Optimizer.GetNeoValueTypeManagedSize` (`Optimizer.Neo.cs:1630`)
  is the `Unsafe.SizeOf<T>`-based, cached, enum-aware managed-byte size — the SAME source the
  F-MAJ-1 CLR-VT LOCAL declaration (`JITCompiler.cs:2153`), the callee param layout, and
  `ReadNeoValueType`/`WriteNeoValueType` use. So the dest `slot.Size` and the write size at the
  Ldsfeld call site (`ILIntepreter.Neo.cs:4522`, `WriteNeoValueType(fldVal, dstSlot,
  GetNeoValueTypeManagedSize(ft))`) agree byte-for-byte. For TestVector3 (12 bytes): dest temp
  grows 8 → 12, write is 12 → no OOB.
- **Alignment mirrors the LOCAL declaration exactly.** The F-MAJ-1 local block uses
  `AlignUp(offset, clrVtSize >= 8 ? 4 : clrVtSize)` (`:2154`); the new arm uses
  `size >= 8 ? 4 : size`. Identical expression → consistent framing. (See Trivial F-2 for an
  observation about its effective value.)
- **Purely additive.** The `is ILType il` arm (incl. its `maxRefCount` growth via
  `il.TotalReferenceCount`), the initial `maxSize = 8 / maxRefCount = 1 / maxAlignment = 4`,
  and the temp-slot allocation loop below (`:2242-2254`, `slot.Size = maxSize; slot.RefCount =
  maxRefCount`) are all byte-for-byte unchanged. `maxRefCount` is intentionally not grown for
  CLR types (design D2): the reachable set is blittable — a ref-field CLR struct is refused
  upstream by `NeoClrStructHasRefFields`, so its managed ref count is 0 and the default-1 slot
  is unaffected.
- **Dest size now satisfies the guard.** At the Ldsfeld/Stsfld sites, `slotSize =
  localInfos[regIdx].Size` (the now-grown temp Size) is compared against
  `GetNeoValueTypeManagedSize(ft)` in `NeoClrVtStaticFieldIsUnsafe`. For a gathered blittable
  CLR VT, `12 > 12` is false → guard passes → the write proceeds into a correctly-sized temp.
  The dest-size / write-size / guard-size triple all derive from one source. Sound.

---

## 2. Layout-regression check (KEY) — CONFIRMED

`AllocateLocalStackSpaces` sizes every eval-temp register in every Neo method, so a frame-
layout bug could subtly corrupt any Neo method. The broad NeoStep smoke is the gate.

- **Broad NeoStep smoke (fix applied): Ran 348, 0 failed** (`Debug_Neo`, `useRegister=true`,
  filter `NeoStep`). This exercises the categories the dispatch names: NeoStep12/13/13b
  (VT / box / Move_Vt), NeoStep14 (EH), NeoStep15 (isinst/castclass), NeoStep16 (arithmetic /
  arrays / ldind), NeoStep17 (byref / Ref Slot), NeoStep19/20 (delegate / async — present but
  some fail only under Legacy, see §5), clr-static, raw-stfld-ldfld, eh-table, ldind-stind,
  addi-on-float, clr-vt-static. Zero regressions across all of them. AOT NeoStep22-26 are not
  present in this TestCases set (no AOT-step probes in the smoke) — not a regression either way.
- The frame-layout change is a **monotonic growth** of `maxSize` (and never of `maxAlignment`
  past its default — see F-2): existing IL-VT code paths see the same or larger temps with the
  same alignment, so an IL-VT-only method is completely unaffected; a method that also gathers
  a CLR VT simply gets a larger temp file. No compaction, no offset-shift in a way that
  desynchronizes byte/ref offsets (both are recomputed consistently in the same loop).

---

## 3. Stash-toggle (author != verifier, causal proof) — CONFIRMED

`git stash push -- ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` (revert ONLY the
fix; retain the new probes; marker grep → 0). Rebuild CLI `Debug_Neo --no-incremental`
(clean). Run the 2 probes by filter:

- **Without the fix: Ran 2, 2 failed**, both with the exact tagged NIE
  `"Neo Ldsfeld: CLR static value-type field One of type ILRuntimeTest.TestFramework.TestVector3
  not supported under Neo (Step-13b ref-field/binder gap or slot-size overflow)"` — the
  slot-overflow guard firing because the temp was the HEAD-default 8 bytes vs the 12-byte
  struct. (EXIT=127 = known pre-existing Dict-NRE, out of scope.)
- `git stash pop` (marker grep → 1, fix restored), rebuild (clean).
- **With the fix: Ran 2, 0 failed** (EXIT=0).

This is a clean 2/2 FAIL-on-HEAD → 2/2 PASS-after round-trip; the fix is provably what makes
the probes pass. Tree left exactly as found (fix applied, probe file present).

---

## 4. Full-smoke spot check — CONFIRMED (14 → 0)

Full un-filtered Neo smoke (`Debug_Neo`, `useRegister=true`, NO filter), captured to file:
- `"CLR static value-type field One of type"` → **0** occurrences (was 14 distinct / 28 lines).
- Distinct `"Neo Ldsfeld: CLR static value-type field One"` → **0**.
- EXIT=127 (known pre-existing full-smoke Dict-NRE crash, out of scope, unchanged). The NIE
  elimination is pre-crash and complete.

---

## 5. Legacy-neutral — CONFIRMED

- **Neo-gated.** The `maxSize`/`maxAlignment` loop at `JITCompiler.cs:2209-2241` sits inside
  the file-level `#if ENABLE_NEO_MODE` block opened at `:807` (all intervening nested
  `#if/#endif` pairs balance before `:2209`). Legacy `ExecuteR` compiles none of it.
- **Legacy NeoStep run** (plain `Debug` CLI + `useRegister=true`, filter `NeoStep`): **Ran 348,
  17 failed == documented baseline.** The 17 failures are ALL pre-existing baseline NeoStep
  tests unrelated to this change (NeoStep13 box-roundtrip divide-by-zero, NeoStep14/15 EH
  IndexOutOfRange, NeoStep16 stelem/multidim-array, NeoStep19 delegate-adapter, NeoStep20
  async/VT divide-by-zero, NeoNaNR8, NeoStepClrStatic TC3). Neither of the two new
  `NeoStepClrVtStaticSlot` probes appears in the failure set (empty grep) → both pass under
  Legacy. (EXIT=127 here too is the pre-existing crash; the summary printed before it.)

---

## Findings (canonical severity)

| # | Severity | Finding |
|---|----------|---------|
| F-1 | **Minor** | **Probe coverage is Ldsfeld-into-temp only.** Both probes exercise the `ldsfld TestVector3.One` → temp → by-value-call-arg shape. Other temp-dest shapes (Stsfld-from-temp, a CLR-VT method return into a temp, a transient field-read off a temp) are not directly probed. This is acceptable: the fix is **structural** (grows the uniform `maxSize` for EVERY temp in a method that gathers the CLR VT), so a single temp-dest probe transitively validates the sizing for all shapes, and the full-smoke 14→0 empirically covers all 14 real-world temp-dest hits the diagnosis found (all `ldsfld`/`stsfld`). Not blocking; recorded as accepted-known. |
| F-2 | **Trivial** | **The `maxAlignment` update in the new arm is observationally inert for all CLR types.** The expression `size >= 8 ? 4 : size` never yields > 4 (it caps at 4 for `size >= 8` and returns `size ≤ 7` otherwise), and the default `maxAlignment` is already 4, so this arm can never grow `maxAlignment` past its default. This is **by design** (design D3): it mirrors the F-MAJ-1 CLR-VT local declaration's alignment expression verbatim for consistency/generality to structs of other sizes. Not a defect; noted only for completeness. |

No Blocker. No Major. No standards violations. Spec axis (§spec): the `neo-optimizer`
`ADDED` requirement — "Neo eval-temp register slots SHALL be sized to accommodate gathered CLR
value types" — is implemented verbatim; all six scenarios hold (ldsfeld-into-temp passes, the
slot-overflow guard is preserved for gather-missed dests, ref-field CLR structs still refused,
IL-VT temp sizing unregressed, Legacy unaffected). Spec axis PASS.

---

## Summary

The change fixes a genuine 12-into-8 OOB root cause (the `maxSize` consumer loop's `is ILType`
filter silently skipped gathered `CLRType` value types, leaving every eval temp at the default
8 bytes) by adding a ~8-line `else if (i is CLRType ct)` arm that grows `maxSize`/`maxAlignment`
via the canonical `GetNeoValueTypeManagedSize`. It is purely additive, Neo-gated, and leaves the
real AV guard (`NeoClrVtStaticFieldIsUnsafe`) byte-for-byte intact. Independent re-verification:
CLRType-arm correct ✓, layout-regression (broad NeoStep 348/0) ✓, stash-toggle 2/2 FAIL→PASS ✓,
full-smoke NIE 14→0 ✓, Legacy-neutral 348 ran / 17 failed == baseline ✓.

**APPROVE.**
