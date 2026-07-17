# Review Report: `neo-clr-static-fields`

**Reviewer:** reviewer-clrstatic-1 (author != verifier gate, dispatched leaf, Tier A)
**Date:** 2026-07-11
**Change:** child 3 of the `neo-overhaul` portfolio (re-prioritized #1). Fills the
`Stsfld`/`Ldsfld` CLR-declaring-type `else` branches in `ExecuteNeo`.
**Diff (3 files):** `ILIntepreter.Neo.cs` (+145), `TestClass3.cs` (+8),
`NeoStepClrStaticFieldTest.cs` (new, 3 probes).

---

## VERDICT: **APPROVE-WITH-FINDINGS**

The change is correct, well-scoped, and empirically verified end-to-end. Both
recovery-worker bug-fixes are real and correctly applied (I reproduced the
wrong-slot failure that bug-fix 1 prevents). The one substantive concern — the
`brtrue`-on-reference latent gap that the scoping decision deliberately avoids —
is **not introduced by this change** and does not regress anything (Tr2/Tr5
verified green); it should be tracked as a **separate child**, not a blocker
here. The findings below are Minor/Trivial and do not block ship.

---

## 1. Independent re-verification

### 1a. Bug-fix 1 — runtime offset resolution (CONFIRMED CORRECT)

**Premise verified statically:**
- `OpCodeR` is `[StructLayout(LayoutKind.Explicit)]`; **`Register1` and
  `DstOffset` are aliased at offset 4** (`Optimizer.Neo.cs:546-553`).
- JIT emission (`JITCompiler.cs:2506-2514`): `Ldsfld` -> `op.Register1 =
  baseRegIdx++`; `Stsfld` -> `op.Register1 = --baseRegIdx`. So the JIT writes a
  **raw register index** into the `Register1`/`DstOffset` alias. It does NOT set
  a byte offset.
- `LowerNeoOffsets` (`Optimizer.Neo.cs`) has **no** `Stsfld`/`Ldsfld` case — a
  `grep` for `Stsfld|Ldsfld` in `Optimizer.Neo.cs` returns zero case labels.
  They fall to the lowering switch `default: handled = false; break;`
  (`Optimizer.Neo.cs:1454-1456`) -> `WarnUnhandledNeoLoweringOpcode` ->
  **no `LowerR1` runs**.
- `LowerR1` (`Optimizer.Neo.cs:1513-1526`) is what normally converts the index
  to a byte offset: `op.DstOffset = localInfos[r1].Offset`. Because it never
  runs for Stsfld/Ldsfld, **`ip->DstOffset` retains the raw register index** at
  execution time.
- The JIT disassembly of TC1 confirms it concretely: `stsfld r6,
  0x2000008A85B5ED88` / `ldsfld r6, 0x2000008A85B5ED88`. `r6` is a register
  **index**; the type hash `0x2000008A` resolves to `TestClass3` (CLR); the low
  32 bits are the field hash. `frameBase + 6` is not where r6's value lives.

**Conclusion:** the new CLR arms' runtime resolution
`int off = localInfos[regIdx].Offset; byte* slot = frameBase + off;` is the
**correct** fix and mirrors `LowerR1` exactly. Reading `frameBase + ip->DstOffset`
directly (the dead worker's code) reads the wrong slot. The IL-static arms above
do read the raw index directly (`ILIntepreter.Neo.cs:3882,3975`) — a real
**pre-existing** gap the implementer explicitly acknowledges and scopes out (see
finding F3).

**Empirical proof (surgical toggle of bug-fix 1 only):** I reverted the two
`localInfos[...].Offset` resolutions back to the raw index
(`int off = regIdx;`), keeping both CLR branches otherwise intact, rebuilt, and
ran TC1. Result: **TC1 fails with `DivideByZeroException`** — the deliberate
`1/0` fault tripped by the `h != 12345 || v != 12345` value-assertion. This is a
**wrong-VALUE** failure (not an NIE): the branches execute but read/write the
wrong slot, so the 12345 round-trip is corrupted. Restoring the resolution ->
TC1 passes. This reproduces exactly the silent wrong-slot bug the recovery worker
found.

### 1b. Bug-fix 2 — `NeoClrVtStaticFieldIsUnsafe` guard (CONFIRMED CORRECT, not over-blocking)

The guard (`ILIntepreter.Neo.cs:~252-295`) refuses a CLR VT static with a tagged
NIE when: (a) the type has a registered `ValueTypeBinder`
(`AppDomain.ValueTypeBinders.ContainsKey(ft)`), (b) it has reference fields
(`NeoClrStructHasRefFields`, recursive), or (c) its flat managed size overflows
the register's eval-slot (`GetNeoValueTypeManagedSize(ft) > slotSize`). Simple
blittable structs/enums (`IntPtr`) pass.

- **Not over-blocking:** TC3 (`IntPtr.Zero` VT read) **passes** in the 314/0
  smoke. `IntPtr` is `IsValueType && !IsPrimitive`, has no registered binder, no
  ref fields, fits the slot -> guard returns false -> `WriteNeoValueType` runs.
- **Binder/ref/overflow refused, no crash:** the full smoke (848 tests)
  **completes with no real AccessViolation** (see §3). The pre-fix flat-VT path
  AV-exited on binder structs like `TestVector3.One`; the guard converts that to
  a tagged NIE instead.
- **Recursion safety:** `NeoClrStructHasRefFields` recurses only into nested
  *value-type* fields; by-value value-type cycles are impossible under the CLI
  type system (infinite size), so the recursion terminates. Enums have one
  primitive instance field -> return false (allowed). No stack-overflow risk.

### 1c. Per-category marshalling (CONFIRMED against spec + Legacy parity)

Both arms cast `declType as CLRType`, resolve `f = ct.GetField(sIdx)`,
`sIdx = (int)ip->OperandLong`, with a tagged-NIE null guard clearer than the
downstream NRE. Stsfld writes via `ct.SetStaticFieldValue`; Ldsfld reads via
`ct.GetFieldValue(sIdx, null)` and unwraps `CrossBindingAdaptorType` -> `ILInstance`
(Legacy parity, `ILIntepreter.Register.cs:3334-3335`). Category dispatch keys on
`f.FieldType` (a `System.Type`), which is correct for CLR statics and matches
the helpers' `System.Type` signatures. Reference branch uses the `mStack.Add`
temp-ref convention matching the IL-static Ldsfld ref branch (no `dstRefOffset`
operand exists for Stsfld/Ldsfld, unlike Box/Unbox). Consistent with the spec
delta's three categories.

---

## 2. brtrue-on-reference gap verdict — REAL separate bug; scoping decision is CORRECT

**The gap is real.** The `Brtrue` arm tests
`if (*(int*)(frameBase + ip->DstOffset) != 0)` (`ILIntepreter.Neo.cs:2038`). But
the Neo reference encoding is **never 0 for a null reference**: the IL-static
Ldsfld ref branch stores `mStack.Count - 1` (a valid non-negative index) **even
when the value is null** (`:4002`), and the new CLR-static Ldsfld ref branch
stores `-1` for null (`:4049`). So a raw object reference reaching `brtrue` is
always non-zero -> null is misclassified as truthy.

This is normally masked because Roslyn lowers object truthiness to a `ceq`/`cgt.un`
int32 0/1 **before** `brtrue` (the arm comment at `:2033-2037` states this as the
assumption). The exception is the Roslyn delegate-cache pattern
(`ldsfld cache; brtrue/brfalse slowPath`), which tests the reference directly
with no preceding `ceq`.

**Why the IL-static arm currently survives Tr2/Tr5:** its raw-index read
(`frameBase + ip->DstOffset`, the pre-existing bug) reads a zeroed slot for the
null cache on first call -> `brtrue` (`!= 0`) correctly does not branch ->
delegate is built. "Fixing" the IL-static read to the correct slot would feed
`brtrue` a non-zero mStack index for the null cache -> `brtrue` wrongly branches
-> Tr2/Tr5 break. So the IL-static raw-index bug and the brtrue gap are
**mutually masking**.

**Verdict:** the implementer's decision to fix CLR-static only (via runtime
offset resolution in the new branches) and NOT add a global Stsfld/Ldsfld
lowering is the **correct** scoping — it does not unmask the latent gap and keeps
Tr2/Tr5 green (verified, §3). The gap should be filed as a **new child**
("neo-brtrue-on-reference" + "neo-il-static-raw-dstoffset": correctly reading
IL-static fields requires also teaching `brtrue`/`brfalse` the null sentinel). It
is **not** a blocker for this change. See F3/F4.

---

## 3. Gate results (re-run independently)

| Gate | Result | Claim | Match |
|------|--------|-------|-------|
| Build CLI `Debug_Neo --no-incremental` | 0 errors | 0 errors | yes |
| Build TestCases `Debug` | 0 errors | 0 errors | yes |
| NeoStep smoke (`... true NeoStep`) | **314 ran, 0 failed, 0 ignored** | 314/0 (311+3) | yes |
| - of which `NeoStep20_Tr2_ActionSideEffect` | ran + passed (no fail line) | unaffected | yes |
| - of which `NeoStep20_Tr5_LambdaCallsILMethod` | ran + passed (no fail line) | unaffected | yes |
| - 3 new probes TC1/TC2/TC3 | ran + passed | PASS-after | yes |
| Full smoke (no filter) | **848 ran, 249 failed, 20 ignored, 6 todos**, run COMPLETES | 848/249, completes | yes |
| - `"CLR static field not implemented"` NIE count | **0** | 0 (was 88) | yes |
| - real AccessViolation / segfault | **0** (the 15 `AccessViolation` text hits are all `newobj System.AccessViolationException` JIT-disassembled IL of an exception-type test, not crashes) | no crash | yes |

The design's "Activator crash" premise was indeed wrong: the full smoke completes
at HEAD with no crash. Failures dropped 252 (HEAD) -> 249 (this change) = exactly
the 3 new probes flipping fail->pass; no regressions (the curated NeoStep suite
is the independent regression check and is 314/0). I relied on the dispatch's
HEAD=252 baseline rather than re-running HEAD's full smoke a second time.

---

## 4. Stash-toggle evidence (load-bearing + bug-fix-1 isolation)

| Experiment | ILIntepreter.Neo.cs state | Rebuild | Run `NeoStepClrStatic` | Result |
|------------|--------------------------|---------|------------------------|--------|
| A. Branches reverted to HEAD (`git stash push -- <file>`) | both CLR `else` = `throw "...not implemented"` | 0 err | 3 probes | **3/3 FAIL — NIE** (`Neo Stsfld/Ldsfld: CLR static field not implemented`) |
| B. Bug-fix-1 only off (offset resolution -> raw index; branches intact) | `int off = regIdx;` in both arms | 0 err | 3 probes | **1/3 FAIL — TC1 `DivideByZeroException`** (wrong-value; TC2/TC3 still pass — see F2) |
| C. Fix fully restored (from backup) | as-committed | 0 err | 3 probes | **3/3 PASS** |
| D. Fix fully restored, full NeoStep | as-committed | (same) | 314 tests | **314/0 PASS** |

Tree restored identical to the fixed backup after every experiment (verified via
`diff -q`; 0 `TMP-TOGGLE` markers; stash dropped; no stray edits).

---

## 5. Legacy-neutral spot check (CONFIRMED)

- `ILIntepreter.Neo.cs` is **wholly `#if ENABLE_NEO_MODE`-gated** (file-level
  `#if` at line 1). Legacy `ExecuteR` lives in `ILIntepreter.Register.cs` and is
  not touched. The two CLR-static branches, the guard, and the helper are all
  inside this gate.
- `Optimizer.Neo.cs` is **unchanged** (no lowering added) — `git diff` on the
  optimizer is empty.
- `TestClass3.cs` adds a `public static int NeoClrStaticProbe` field + a host
  `HostReadNeoClrStaticProbe()` helper in `ILRuntimeTestBase` (test infra). This
  is semantically inert for existing Legacy tests (no existing test reads the
  field). I did not re-run the full Legacy 519-test regression (expensive); the
  gating + inert field addition is sufficient proof. (Note for the LEAD: if a
  Legacy green run is desired for the portfolio record, it is low-risk.)

---

## 6. Findings by severity

### Minor

- **F1 — TC2/TC3 are weak probes for the wrong-slot regression.** With bug-fix 1
  toggled OFF, only TC1 fails; TC2 (`string.Empty`) and TC3 (`IntPtr.Zero`) still
  pass because their expected values (empty/zero) are indistinguishable from the
  zeroed/incorrect slot the wrong-offset read produces. The slot-resolution
  correctness therefore rests **entirely on TC1**. TC1 does cover it solidly
  (distinctive non-zero 12345, Stsfld write + host read + Ldsfld read), so this
  is acceptable, but a future hardening probe with a non-zero struct/reference
  value would make TC2/TC3 independently load-bearing. (Coverage; does not block.)

- **F3 — IL-static Stsfld/Ldsfld arms read the raw `DstOffset` index (pre-existing
  bug, out of scope but should be tracked).** `ILIntepreter.Neo.cs:3882` (Stsfld
  IL) and `:3975` (Ldsfld IL) do `frameBase + ip->DstOffset` directly. Since
  Stsfld/Ldsfld are not lowered, this reads the wrong slot — the same class of
  bug bug-fix 1 fixes for the CLR path. The capstone IL-static tests pass only
  because (a) the tested `.cctor` sources land in a register whose index
  coincides with a zero/low offset and (b) the brtrue-on-reference masking (§2).
  Correctly fixing it requires also fixing F4. Recommend a dedicated child.

- **F4 — `brtrue`/`brfalse`-on-reference latent gap (real, separate child).** See
  §2. `Brtrue` tests low-int32 `!= 0` but a null reference is encoded as a
  non-zero mStack index (IL-static) or -1 (this change's CLR-static Ldsfld ref
  branch), so `brtrue` on a raw reference misclassifies null. Currently masked by
  Roslyn's `ceq` lowering and by F3. The delegate-cache `ldsfld cache; brtrue`
  pattern is the trigger. F3 and F4 must be fixed together (the implementer's
  scoping avoids destabilizing them). Not a blocker for this change.

### Trivial

- **F5 — Null-sentinel inconsistency between IL-static and CLR-static ref
  branches.** IL-static Ldsfld ref (`:4002`) stores `mStack.Count - 1` for null;
  the new CLR-static Ldsfld ref (`:4049`) stores `-1` for null. Both are non-zero
  (so both trip F4 equally), and the Stsfld/other readers guard with
  `idx >= 0 ? mStack[idx] : null`, so neither causes a standalone bug. Cosmetic
  inconsistency; align when F4 is addressed.

- **F6 — Overflow check silently skipped when `slotSize` falls back to 0.** If
  `localInfos` were null or `regIdx` out of range, `stSlotSize`/`ldSlotSize`
  default to 0 and the `slotSize > 0 &&` guard skips the overflow check (allows a
  potentially oversized VT). In practice `localInfos` is populated for every
  ExecuteNeo frame (the IL-VT arm relies on it), so this is defensive-only. No
  observed trigger. (Robustness nit.)

---

## 7. Spec axis

Spec = `proposal.md` + `tasks.md` + `specs/neo-optimizer/spec.md`. All ADDED
requirements and all four scenarios are met: primitive round-trip (TC1),
reference read `string.Empty` (TC2), VT read `IntPtr.Zero` (TC3), and the
"no longer throws the capstone NIE" scenario (stash-toggle A confirms it throws
without the fix). The CrossBindingAdaptor unwrap is implemented (`:4024`). No
scope creep — the diff touches exactly the three files named, with the documented
deviation notes (runtime offset, VT guard) recorded in `tasks.md`. The two
deviations are improvements over the original design (which assumed
`frameBase + ip->DstOffset` was a byte offset) and are correctly described.

---

## 8. Summary

The change does what it claims, the two recovery bug-fixes are real and
correctly applied (bug-fix 1 reproduced empirically), the gates are green
(NeoStep 314/0 incl. Tr2/Tr5; full smoke completes at 848/249 with the
CLR-static NIE eliminated 88->0 and no crash), and it is Legacy-neutral by
construction. The brtrue-on-reference gap is a real but pre-existing, separately-
masked latent issue correctly left out of scope. **APPROVE-WITH-FINDINGS** — ship
as-is; file F3/F4 as a follow-up child, optionally harden TC2/TC3 (F1).
