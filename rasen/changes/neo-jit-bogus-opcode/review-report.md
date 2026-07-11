# Review Report: `neo-jit-bogus-opcode`

**Reviewer:** reviewer-1 (author != verifier gate, dispatched report-only)
**Change:** `neo-jit-bogus-opcode` (child 1 of `neo-overhaul` portfolio)
**Branch:** `features/object-model-overhaul`
**Date:** 2026-07-11
**Verdict:** **APPROVE-WITH-FINDINGS** (0 Blocker, 1 Major [deferred/pre-existing], 2 Minor, 1 Trivial)

The confirmed root-cause fix is correct and minimal; the regression probe deterministically
detects the defect (FAIL-on-HEAD -> PASS-after, independently reproduced); the dispatch guard
is sound and ships in all Neo builds; Legacy is byte-identical by construction. The one
substantive item is a **pre-existing, correctly-deferred** EH-table landmine the implementer
already flagged, not a defect introduced here.

---

## 1. Diff read (independent confirmation of the fix)

### 1a. The Leave remap is correct and mirrors the branch arm exactly
`Optimizer.Neo.cs:1662-1700` `FixBranchTargetsAfterRemove`:

```
bool isLeave = op.Code == OpCodeREnum.Leave || op.Code == OpCodeREnum.Leave_S;
if (IsBranching(op.Code) || isLeave)
{
    if (op.Operand > removedIndex) { op.Operand--; body[i] = op; }
}
```

- **Direction / off-by-one:** identical to the existing `IsBranching` arm and the
  `IsIntermediateBranching` (`Operand4`) / `Switch` arms — strict `>`, decrement by 1.
  The new Leave arm is OR-ed into the SAME condition with the SAME `>` / `--` semantics.
  This is exactly the "mirror the branch arm" discipline the task asked for. Confirmed
  consistent across all four target categories (branch / intermediate / switch / leave).
- **Leave was genuinely uncovered before.** `IsBranching` (Optimizer.Utils.cs:341) covers
  Br/Br_S/Brtrue*/Brfalse*/Blt*/Ble*/... and `IsIntermediateBranching` (378) covers the
  `*i` variants; neither lists Leave/Leave_S. So the gap was real and the fix targets it.
- **Leave consumes `Operand` as a body index.** Confirmed in ExecuteNeo (ILIntepreter.Neo.cs:5038-5054):
  `case Leave/Leave_S: ... ip = ptr + ip->Operand; continue;` and
  `finallyEndAddress = ip->Operand;`. So remapping `Operand` on deletion is exactly right.
- **`>` vs `>=` at `Operand == removedIndex`:** a Leave targeting the *deleted* index is not
  decremented and would then point at the instruction that shifted into `removedIndex`. This
  is vacuous in practice (a Leave target is an EH/finally boundary, never a synthetic mid-call
  `Push`) and mirrors the branch arm's long-shipped behavior. See Trivial F-4.

### 1b. Guard logic is sound
- **Loop-head bounds check** (`ILIntepreter.Neo.cs:~1414-1426`): `if (_ipIdx >= body.Length)
  throw InvalidOperationException("Neo: ip ran past body end in <method> at index <i>/<len> ...")`.
  Correct: fires before the `switch` dispatches, so an overrun never silently aliases a real arm.
- **`default:` range check** (~5374-5396): `if (_rawCode < 0 || _rawCode >= NeoOpCodeCount)
  throw "Neo: corrupt opcode <raw> at <method>:<i>/<len> <field-dump>"`; named-but-unimplemented
  codes (e.g. Ldtoken) fall through to the existing Step-6 message. Correctly distinguishes
  garbage from named-unimplemented. `NeoOpCodeCount = Enum.GetValues(typeof(OpCodeREnum)).Length`
  is `static readonly`, computed once. See Minor F-3 for the implicit-enum assumption.

### 1c. Edge cases the fix might miss
- The only in-arm edge (`Operand == removedIndex`) is covered above (vacuous + mirrors branch arm).
- Switch jump-table entries are remapped per-entry (unchanged, correct).
- The `symbols` map is remapped (unchanged, correct).
- No other `Operand`-carrying control-flow opcode is missed: `Endfinally` uses
  `finallyEndAddress` (set from a Leave `Operand` that is now correctly remapped), not its own
  body target, so it transitively benefits from the Leave fix.

---

## 2. NeoStep smoke (no-regression gate) — independently re-run

Build: CLI `Debug_Neo --no-incremental` (0 errors) + TestCases `Debug` (0 errors).
Run: `... true NeoStep` -> **`Ran 306 tests, 0 failed, 0 ignored, 0 todos`** (exit 0).

Confirms 306/0 = 301 baseline + 5 new probe TCs, **zero regressions**. All 5 probe TCs
(VOID6/VOID4/RET6/RET5/ASN6) were observed executing in the log.

---

## 3. Stash-toggle of the probe (the load-bearing check) — independently reproduced

**Toggle used (per task CAUTION):** `git stash push -- .../Optimizer.Neo.cs` only
(the Leave remap), **keeping** the guard in `ILIntepreter.Neo.cs`, so the guard catches the
overrun loudly. (Stashing both would have re-introduced the original silent garbage-opcode
fault instead of the loud guard throw; this toggle isolates the Leave fix cleanly.)

**FAIL-on-HEAD (Leave remap removed, guard kept):**
```
1 tests failed
Test name: TestCases.NeoStepBogusLeaveTest.NeoStepBogusLeave_TC_RET5_Return5ArgRef,
Message: Neo: ip ran past body end in ...NeoStepBogusLeave_TC_RET5_Return5ArgRef()
         at index 12/11 (unterminated body or a control-flow target past the end)
...
Ran 5 tests, 1 failed, 0 ignored, 0 todos
   at ILRuntime...ILIntepreter.ExecuteNeo(...) in ILIntepreter.Neo.cs:line 1426
```
This **exactly** matches the implementer's recorded claim (TC_RET5, `index 12/11`).
The other 4 TCs pass even without the fix (their Leave target happens to land in-range
post-deletion; only TC_RET5's target lands exactly at the post-deletion body end ->
deterministic overrun). TC_RET5 is confirmed as the load-bearing detector.

**PASS-after (Leave remap restored via `git stash pop` + rebuild):**
```
0 tests failed
Ran 5 tests, 0 failed, 0 ignored, 0 todos
```

**Toggle is clean:** stash popped/dropped; worktree restored to the intended 2-file diff +
untracked probe. (A pre-existing unrelated stash `child4-valuetask-blocked-partial` was already
in the stash list before this review and was not touched.)

---

## 4. Legacy-neutral spot check — CONFIRMED

Both edited files are **entirely** fenced `#if ENABLE_NEO_MODE` ... `#endif`:
- `Optimizer.Neo.cs`: `#if ENABLE_NEO_MODE` at line 1, `#endif` at EOF (line 1749). The Leave
  remap is inside.
- `ILIntepreter.Neo.cs`: `#if ENABLE_NEO_MODE` at line 1, matching `#endif` at EOF. The
  `NeoOpCodeCount` field, the loop-head bounds check, and the `default:` range check are all
  inside.

Plain `Debug` (no `ENABLE_NEO_MODE`) compiles both files out entirely -> Legacy `ExecuteR`
binary is byte-identical -> Legacy-neutral **by construction**. (The implementer's separate
plain-`Debug` run reporting the same 16 pre-existing Legacy NeoStep failures is consistent
with this; not independently re-run here because the `#if` fence is conclusive.)

---

## 5. Adversarial: the EH-table landmine (verdict + rationale)

**The implementer flagged:** `method.ExceptionHandlerRegister` (each `ExceptionHandler`'s
`TryStart`/`TryEnd`/`HandlerStart`/`HandlerEnd`) is body-indexed and is NOT remapped by
`LowerNeoOffsets`. Is this a real risk THIS change should address, or correctly deferred?

**Confirmed real and latent (independent trace):**
- `exceptionHandlerR` is populated once in `ILMethod.InitCodeBody` (ILMethod.cs:975-999) via
  `e.HandlerStart = addr[eh.HandlerStart]`, `TryStart = addr[eh.TryStart]`,
  `TryEnd = addr[eh.TryEnd]-1`, `HandlerEnd = ...-1` -> **register-VM body indices**.
- The Neo back-half (`JITCompiler.RunNeoBackHalf`, JITCompiler.cs:701-714) runs
  `TypeSpecializeNeoOpcodes` -> `frame.NeoExecuteBody = CodeBody.Clone()` ->
  `Optimizer.LowerNeoOffsets`. `LowerNeoOffsets` deletes synthetic `Push` instructions and
  remaps branch/intermediate/switch/**now Leave** targets, but the EH table is **not** an
  argument to `FixBranchTargetsAfterRemove` and is **not** remapped anywhere in the back-half.
- `ExecuteNeo` consumes the EH table against the **lowered** body: `ip = ptr + eh.HandlerStart`
  (ILIntepreter.Neo.cs:5048, 5072) and `FindExceptionHandlerByBranchTarget(addr, ..., ehs)`.
- => Trigger condition: a method with (a) an EH block (try/catch/finally/fault) AND
  (b) a call/`newobj` with >3 register params whose deleted `Push` shifts an EH boundary AND
  (c) an exception actually thrown (or a finally entered) so a stale `HandlerStart`/`TryEnd` is
  dereferenced. Result: mis-routed exception (in-range stale index -> wrong instruction) or,
  if the stale index lands past the end, the new guard now throws loudly.

**Verdict: REAL but correctly DEFERRED — not a Blocker for this change.** Rationale:
1. **Pre-existing, not introduced here.** `LowerNeoOffsets` has always deleted `Push`es without
   remapping the EH table; this change only *added* the Leave remap and *flagged* the EH gap.
   No regression.
2. **Distinct defect, distinct symptom.** This change kills the *garbage-opcode* overrun
   (the confirmed 0x23F70C crash). The EH-table staleness produces *wrong exception routing*,
   a different failure class on a different code path (thrown-exception routing, not the
   Leave/normal-exit path the probe exercises).
3. **Out of scope by design.** design.md Non-Goals explicitly exclude "Rewriting
   `LowerNeoOffsets` or the optimizer passes broadly." Fixing this requires plumbing
   `ILMethod.exceptionHandlerR` (4 int fields x N handlers) into `LowerNeoOffsets` /
   `FixBranchTargetsAfterRemove` (the optimizer currently has no `ILMethod` reference) — a
   broader change than this surgical fix.
4. **Partially mitigated by the guard shipped here.** A stale EH index that lands past the
   shortened body now hits the loop-head bounds check and throws a locatable
   "ip ran past body end" instead of silently aliasing. Only the *in-range* stale case
   (off-by-one mis-target) remains uncaught — and that needs the (a)+(b)+(c) trigger, which
   the current green 306/0 smoke does not contain (no throwing EH + >3-arg-call combo).
5. **The probe does not cover it** (no probe TC throws), so it is genuinely untested — which
   is acceptable for a deferred item but means the follow-up must add a *throwing* try/catch +
   >3-arg-call probe.

Recommend opening a sibling change (e.g. `neo-jit-eh-target-remap`) under the portfolio to
remap `ExceptionHandlerRegister` in `LowerNeoOffsets` and add a throwing-EH regression probe.

---

## Findings (canonical severity)

| # | Severity | File:area | Finding | Disposition |
|---|----------|-----------|---------|-------------|
| F-1 | **Major** (deferred / pre-existing) | `ILMethod.cs:975-999` (EH build) consumed in `ILIntepreter.Neo.cs:5048/5072` | EH table `TryStart/TryEnd/HandlerStart/HandlerEnd` is body-indexed and NOT remapped by `LowerNeoOffsets`'s `Push`-deletion. Trigger: try/catch/finally + >3-arg call/newobj that throws -> mis-routed (or now loudly-guarded) exception. Real latent correctness bug; pre-existing; correctly out of scope here. See section 5. | Record as follow-up change; NOT blocking. |
| F-2 | **Minor** | `ILIntepreter.Neo.cs:~1415-1426` (loop-head guard) | Bounds check is placed AFTER `OpCodeREnum code = ip->Code;`. On an overrun, `ip->Code` reads one 24-byte slot past `body.Length` (benign adjacent managed-heap read, no AV) before the check throws. The throw still fires before `switch(code)`, so the no-silent-aliasing invariant holds. Cleaner: check `(int)(ip-ptr) >= body.Length` BEFORE the dereference. | Optional polish; not blocking. |
| F-3 | **Minor** | `ILIntepreter.Neo.cs:161` / `default:` range check | `NeoOpCodeCount = Enum.GetValues(typeof(OpCodeREnum)).Length` assumes the enum is a contiguous implicit `0..<count>` range (true today; the proposal documents "all-implicit 0..328, zero explicit-value members"). If a future member ever carries an explicit value `>= Length` (or a gap), a legitimate high opcode would false-positive as "corrupt opcode". Self-maintains only for appended implicit values. | Add a one-line assertion or comment near the enum / the cache documenting the contiguous-implicit invariant. Not blocking. |
| F-4 | **Trivial** | `Optimizer.Neo.cs:1676-1682` (Leave arm) | `Operand == removedIndex` exactly is untested (Leave not decremented; would point at the shifted-in instruction). Mirrors the branch arm's long-shipped `>` semantics and is vacuous in practice (a Leave never targets a synthetic `Push`). | No action; noted for completeness. |

No Blockers. No Scope drift (Standards + Spec axes both pass: the diff does exactly what
proposal.md / spec.md ask — Leave remap + permanent guard + probe — nothing more).

---

## Overall verdict

**APPROVE-WITH-FINDINGS.** The root-cause fix is correct, minimal, and consistent with the
existing remap arms; the regression is independently reproduced (FAIL `index 12/11` on HEAD ->
PASS after fix); the smoke is 306/0; Legacy is fenced off by construction. The findings are one
correctly-deferred pre-existing landmine (F-1, follow-up) plus minor guard-polish/assumption
notes (F-2, F-3) and one vacuous edge (F-4). Ship it; track F-1 as a sibling portfolio change.

---

## Durable findings (for future planning)

- **EH-table landmine (F-1) exact trigger:** a method that has BOTH an EH block AND a call/
  `newobj` with >3 register params, where the deleted synthetic `Push` shifts an EH boundary,
  AND an exception is thrown / finally entered. `LowerNeoOffsets` mutates body length and
  remaps branch/intermediate/switch/Leave targets via `FixBranchTargetsAfterRemove`, but the
  `ILMethod.exceptionHandlerR` (TryStart/TryEnd/HandlerStart/HandlerEnd, body-indexed) is never
  remapped; the optimizer has no `ILMethod` handle today, so the fix must plumb it through.
  The shipped loop-head guard converts the over-run flavor to a loud throw but does NOT catch
  the in-range off-by-one mis-route flavor.
- **Leave consumes `Operand` as a body index** (`ip = ptr + ip->Operand`, `finallyEndAddress =
  ip->Operand`) and was the sole control-flow target category `FixBranchTargetsAfterRemove`
  missed; any future body-length-mutating optimizer pass must remap Leave too (now covered).
- **`NeoOpCodeCount` guard** relies on `OpCodeREnum` staying a contiguous implicit
  `0..<count>` range; an explicit-value/non-contiguous member would break the range check.
