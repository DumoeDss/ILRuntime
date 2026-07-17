# Review Report — neo-clr-static-vt-field

**Reviewer:** reviewer-1 (author != verifier gate). **Mode:** dispatched (report-only).
**Change:** child 8 of `neo-overhaul`. Deletes the over-conservative `if (hasBinder) return true;`
clause from child-3's `NeoClrVtStaticFieldIsUnsafe` guard so a blittable CLR value-type static
whose type has a registered `ValueTypeBinder` (`TestVector3.One`) reads/writes under Neo instead
of NIE-ing. Net runtime diff ~1 line (guard body) + 2 call-site cleanups.

**Verdict: APPROVE-WITH-FINDINGS.** The deletion is SAFE (the real AccessViolation stays guarded
by the kept slot-overflow clause), the unmasked byref-field-mutate AV is pre-existing-and-not-a-
regression, and every gate passes (NeoStep 326/0, targeted probes 2/0, stash-toggle FAIL-on-HEAD,
Legacy-neutral 326/17). Two non-blocking Minor findings recorded as accepted-known.

---

## Diff read (independent)

Files: `ILIntepreter.Neo.cs`, `TestClass3.cs`, new `TestCases/NeoStepClrVtStaticFieldTest.cs`.

- **Guard signature + body** (`ILIntepreter.Neo.cs:272`): `static bool NeoClrVtStaticFieldIsUnsafe(Type ft, int slotSize)` — now 2 params (was 3 with `bool hasBinder`). The body has **exactly 2 reject clauses**, byte-for-byte as designed:
  1. `if (NeoClrStructHasRefFields(ft)) return true;` — ref-field rejection (recursive).
  2. `if (slotSize > 0 && Optimizer.GetNeoValueTypeManagedSize(ft) > slotSize) return true;` — slot-overflow rejection (the REAL AV guard).
  - Early-out `if (ft == null || !ft.IsValueType || ft.IsPrimitive) return false;` and `return false;` unchanged.
  - The `if (hasBinder) return true;` clause + its comment are fully deleted.
- **Call sites cleaned** (Stsfld `:4179`, Ldsfld `:4317`): both now call `NeoClrVtStaticFieldIsUnsafe(ft, stSlotSize/ldSlotSize)` with 2 args. The `bool stHasBinder/ldHasBinder = AppDomain.ValueTypeBinders != null && AppDomain.ValueTypeBinders.ContainsKey(ft);` computations are removed at both sites.
- **No dangling references:** `grep` for `hasBinder` and `ValueTypeBinders` in the file returns ZERO hits (only the guard definition + 2 call sites remain, all 2-arg). No stray `ValueTypeBinders` access left behind.
- **Box-roundtrip behind the guard UNCHANGED:** Stsfld arm `:4181-4191` still does `ReadNeoValueType(ft, frameBase, ref vtOff, GetNeoValueTypeManagedSize(ft))` then `ct.SetStaticFieldValue(sIdx, value)`; Ldsfld arm `:4319` still does `WriteNeoValueType(fldVal, dstSlot, GetNeoValueTypeManagedSize(ft))`. The NIE messages unchanged.
- **Root-cause claim (design D1) independently verified:** `CreateNeoVtReader` (`:177`) emits IL `Ldarg_0; Call Unsafe.ReadUnaligned<T>(void*); Box t; Ret`; `CreateNeoVtWriter` (`:200`) emits `Ldarg_0; Ldarg_1; Unbox_Any t; Call Unsafe.WriteUnaligned<T>(void*, T); Ret`. Both are PURE FLAT-BYTE copies, cached per-Type, that **do NOT consult `ValueTypeBinder`** (the binder only exposes Legacy `StackObject*` marshalling). So a blittable binder struct has identical flat bytes with/without a binder — the `hasBinder` rejection was a false correlation. Correct to delete.

**Diff-read verdict:** clean. Guard has exactly 2 clauses; the binder clause, param, and both call-site computations are fully removed with no dangling refs; the box-roundtrip is untouched.

---

## Deletion is SAFE — the real AV stays guarded

The original "VT-binder AV crash" (child-3 ship-log "Bug-fix 2") was the slot-overflow case, not
the binder flag. The kept clause `GetNeoValueTypeManagedSize(ft) > slotSize` catches it. Verified
**live**: in the WITH-FIX full smoke, the single remaining CLR-static-VT NIE is

```
at TestCases.ArrayTest.ArrayTest05() (ArrayTest.cs:76)
Neo Ldsfld: CLR static value-type field One of type ILRuntimeTest.TestFramework.TestVector3 not supported under Neo (Step-13b ref-field/binder gap or slot-size overflow)
```

`ArrayTest05` loads `TestVector3.One` (`ldsfld`) into an undersized `stelem.any` temp r4; the dest
slot is smaller than 12 bytes, so the KEPT slot-overflow clause refuses it (NIE) **before**
`WriteNeoValueType` can write OOB. `TestVector3` is 3 floats (no ref fields), so the ref-field
clause does not fire — the NIE is provably the slot-overflow guard, exactly the desired "real AV
stays guarded" outcome.

**Deletion-safe + AV-guarded verdict: CONFIRMED.**

---

## The unmasked byref-field-mutate AV is NOT a regression (the key check)

With `ldsfld TestVector3.One` now succeeding, the WITH-FIX full smoke reaches a new process-killing
`AccessViolationException`. The implementer calls it a pre-existing latent bug in a different
opcode site. Independent verification:

**(a) The AV path does NOT exist on HEAD** (stash the deletion, run the full smoke): on HEAD,
`TestValueTypeBinding.Test00` is invoked (log line 126808) and at line 126871 throws the CAUGHT NIE
`"Neo Ldsfld: CLR static value-type field One of type ...TestVector3 not supported under Neo"`
(the `hasBinder` guard firing). It never reaches the `ldloca`/`ldflda`/`ldind`/`stind` path. The
smoke continues past Test00 (Test01, Test02, ...) and crashes later at a different test
(`UnitTest_10040`). So on HEAD the AV is absent — ldsfld NIEs first.

**(b) With the fix, the AV is in the byref-field-mutate path, NOT the ldsfld/Stsfld path.**
Test00's JIT dump at the crash:

```
0: initobj r0, TestVector3
1: ldsfld r0, ...        <- TestVector3.One: SUCCEEDS with the fix (the fix's job, proven correct by TC1/TC2)
2: ldloca.s r1, r0
3: ldflda r1, r1, ...    <- take field address of a CLR struct local
4: ldind.r4 r2, r1       <- read float byref  -> AV here
5: addi ...
6: stind.r4 r1, r2       <- write float byref (or AV here)
```

The crash is at instruction 4/6 (`ldind.r4`/`stind.r4` operating on a byref into a CLR struct
local obtained via `ldloca`+`ldflda`). Instruction 1 (`ldsfld` — the site this change touches)
executes correctly. The 2 probes (TC1 `ldsfld` read, TC2 `stsfld`+`ldsfld` round-trip) PASS,
proving the `ldsfld`/`Stsfld` paths correct and NOT the source of the AV.

**Verdict: pre-existing-unmasked (acceptable, follow-up). NOT introduced-regression.**
- The AV is in the `ldind`/`stind`-byref-into-CLR-struct opcode site — a different surface from
  `ldsfld`/`Stsfld`.
- Test00 was NEVER passing on HEAD — it NIE-failed at the ldsfld. No previously-passing test
  regressed; a failing test changed its failure mode (NIE -> AV crash).
- The gating suite (NeoStep) is fully green; the full smoke was already crashing pre-completion
  (pre-existing, per CLAUDE.md the full Neo smoke is in-flight and not a gate).

---

## Gates re-run (all `-f net8.0`; CLI = `Debug_Neo --no-incremental`; TestCases = plain `Debug`)

- **Builds:** CLI `Debug_Neo --no-incremental` = 0 errors; TestCases `Debug` = 0 errors.
- **NeoStep smoke** (`... true NeoStep`): **Ran 326, 0 failed.** (324 baseline + TC1 + TC2.)
- **Targeted probe filter** (`... true NeoStepClrVtStaticField`): **Ran 2, 0 failed.** Both probes PASS.
- **Stash-toggle FAIL-on-HEAD:** `git stash push -- ILIntepreter.Neo.cs` (revert ONLY the guard
  fix; keep probes/infra), rebuild CLI, run the 2 probes:
  - TC1 + TC2 BOTH FAIL with the exact tagged NIE:
    `Neo Ldsfld: CLR static value-type field One of type ILRuntimeTest.TestFramework.TestVector3 not supported under Neo (Step-13b ref-field/binder gap or slot-size overflow)`.
  - `git stash pop` restores the fix; guard back to 2-param form (line 272); tree restored clean;
    my stash dropped. (The remaining `child4-valuetask` stash in the list is pre-existing, not mine.)

**Stash-toggle evidence: 2/2 FAIL on HEAD (NIE) -> 2/2 PASS after restore. CONFIRMED.**

## Full-smoke spot check (captured to file, grepped that)

- **CLR-static-VT NIE count:** HEAD **13** distinct `ldsfld One/TestVector3` binder-NIE lines
  (non-rethrow) -> WITH-FIX **1** distinct (the `ArrayTest05` slot-overflow NIE above). All binder
  NIEs eliminated; only the real-AV guard remains. (The "11->1" in the tasks is the same delta;
  the absolute before-count varies by counting method and is not load-bearing.)
- **Net fatal crashes: 1 (HEAD) / 1 (WITH-FIX) — count unchanged.** Both genuine
  `^Fatal error. System.AccessViolationException` process-killing crashes in `ExecuteNeo`.
- **NUANCE (see finding F2):** the crash POINT moves earlier. On HEAD the fatal crash is at
  `TestValueTypeBinding.UnitTest_10040` (log line ~130465); WITH-FIX it is at
  `TestValueTypeBinding.Test00` (line ~126842). Because Test00 runs before UnitTest_10040 and now
  crashes instead of NIE-ing, the full smoke completes slightly less (the tests in
  (Test00, UnitTest_10040] that ran on HEAD are not reached with the fix). Crash COUNT is
  unchanged; coverage of the non-gating full smoke is marginally reduced.

## Legacy-neutral

Plain `Debug` (ENABLE_NEO_MODE off) + `useRegister=true` (Legacy `ExecuteR`) + NeoStep filter:
**Ran 326, 17 failed** — the pre-existing Legacy failure set (NeoStep13/14 etc. that fail under
Legacy because Legacy lacks Neo features; matches child-6's 320/17 baseline + child-7 + these 2
probes). TC1/TC2 PASS under Legacy too (`... true NeoStepClrVtStaticField` -> Ran 2, 0 failed —
plain CLR static VT field ops work in both modes). **Legacy-neutral CONFIRMED.**

---

## Findings

### F1 — Unmasked byref-field-mutate AV (Minor, accepted-known; follow-up)
Now that `ldsfld TestVector3.One` succeeds, `TestValueTypeBinding.Test00` reaches
`ldloca`+`ldflda`+`ldind.r4`/`stind.r4` on a CLR struct local and AccessViolation-exits the
process (visible in the full, un-filtered smoke only). This is a pre-existing latent bug in the
`ldind`/`stind`-byref-into-CLR-struct opcode site, NOT in `ldsfld`/`Stsfld` (proven by the JIT
dump + the 2 passing probes). Test00 was NIE-failing on HEAD; no previously-passing test
regressed. NeoStep smoke is fully green. **Recommendation:** track as a dedicated follow-up child
(byref field-mutate on a CLR struct local: `ldloca`+`ldflda`+`ldind`/`stind`). Not blocking.

### F2 — Full-smoke fatal crash moves earlier (Minor, context for shipper)
The fix changes the full smoke's process-killing crash from `UnitTest_10040` (HEAD) to `Test00`
(with-fix), an earlier test, so the full smoke completes marginally less. The crash COUNT is
unchanged (1), the full smoke was already crashing pre-completion (pre-existing), and the full
smoke is not the gate (NeoStep 326/0 is). Documented for awareness; no action required to ship.

### No Blocker. No Major.
- Standards axis: CLEAN — deletion is safe, no dangling refs, guard has exactly 2 clauses, box-roundtrip unchanged, root cause independently confirmed.
- Spec axis: CLEAN — the `neo-optimizer` ADDED requirement + all 5 scenarios (Ldsfld read; Stsfld+Ldsfld round-trip; ref-field still refused; slot-overflow still refused; Legacy unaffected) are implemented and verified.

---

## APPROVE-WITH-FINDINGS

**Rationale:** The change correctly deletes a false-correlation guard clause (`hasBinder`) whose
root cause (the Neo flat-byte path does not consult the binder) I independently verified by
reading the emitted IL of `CreateNeoVtReader`/`CreateNeoVtWriter`. The real AccessViolation
protection (slot-overflow clause) is KEPT and proven to still fire on `ArrayTest05`. The unmasked
byref-field-mutate AV is a pre-existing latent bug in a different opcode site (`ldind`/`stind`),
NOT a regression — Test00 was NIE-failing on HEAD and the ldsfld/Stsfld paths are proven correct
by the 2 passing probes. All gates pass: NeoStep 326/0, targeted probes 2/0, stash-toggle
FAIL-on-HEAD -> PASS-after, Legacy-neutral 326/17. The two Minor findings (F1 unmasked AV
follow-up; F2 crash-moves-earlier nuance) are accepted-known, non-blocking, and the implementer
already surfaced F1 honestly as a follow-up.

`DONE`
