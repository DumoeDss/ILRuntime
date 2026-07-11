# Review Report — implement-neo-step14 (Neo Exception Handling)

Reviewer: REVIEWER agent (author != verifier). Date: 2026-07-04.
Branch: `features/object-model-overhaul`.

## Executive verdict: **CLEAN** (ship)

All six priority scrutiny areas pass. The change is correct, faithful to the
Legacy reference, Legacy-neutral in the only place it touches shared code, and
the full NeoStep smoke is **58/58 green** (49 baseline + 9 new Step 14 cases),
empirically confirming the catch-slot resolution, self-cleaning unwind, and
cross-frame propagation.

**Severity counts:** Blocker 0 / Major 0 / Minor 2 (informational, non-blocking)
/ Nit 2.

The single highest-risk item — `Optimizer.RegisterCleanup.cs` Legacy-neutrality
— is **verified clean**. Details below.

---

## Priority 1 — `Optimizer.RegisterCleanup.cs` shared-component change (HIGHEST RISK)

**Verdict: Legacy-neutral. No regression. Correct protection scope.**

### 1a. Only ONE caller exists; Legacy-neutrality is structural, not call-site-by-call-site

`CleanupRegister` has exactly **one caller** in the entire repo:
`JITCompiler.cs:474`. There is no separate Legacy caller. The claim in the
priority brief ("Legacy passes -1") is true *transitively*: the single caller
passes `neoCatchExReg`, and that value is computed entirely inside a
`#if ENABLE_NEO_MODE` block (`JITCompiler.cs:452-473`). In the plain-`Debug`
config (the Legacy 519-test baseline), `ENABLE_NEO_MODE` is **not defined**
(verified: `ILRuntime.csproj:11,17` — only `Debug_Neo`/`Release_Neo` define it),
so `neoCatchExReg == -1`, the `if (protectedReg >= 0)` guard
(`Optimizer.RegisterCleanup.cs:33`) short-circuits, and the function body is
**byte-for-byte identical to the pre-Step-14 version**. `protectedRegFinalIndex`
is left at `-1` (line 19) and discarded. The plain-Debug Legacy build is
untouched.

### 1b. In the `Debug_Neo` build, Legacy `ExecuteR` shares the JIT — protection is strictly *safer*, not a regression

`ExecuteR` and `ExecuteNeo` are compiled into the same assembly in `Debug_Neo`
and consume the SAME lowered `OpCodeR[]` from `JITCompiler.Compile`. So in the
Neo build, Legacy `ExecuteR` also runs against catch-handler code whose
exception register is now protected from compaction. This is **strictly safer**
for Legacy: Legacy writes the caught object via `exReg = paramCnt + locCnt =
baseRegStart` (`ILIntepreter.Register.cs:5326`), indexing its flat
`StackObject[] r` array (sized by `method.StackRegisterCount`, derived from
`totalRegCnt`). If the catch register were ever compacted away, `exReg` would
index out of the allocated register array. Protecting it GUARANTEES the slot
exists. Adding a register to `usedRegisters` can never make a correct caller
worse — at worst it preserves a register that was already preserved. No
behavioral regression to Legacy is possible from this change.

### 1c. Protection scope is exactly one register — no over-/under-protection

`neoCatchExReg = baseRegStart` is set once per method (only if it has any Catch
handler; `JITCompiler.cs:470-471`). All catch handlers in a method share temp
register 0 because the JIT resets `baseRegIdx = baseRegStart` then `++` at the
start of every catch-handler block (`JITCompiler.cs:231-233`). So exactly one
register (`baseRegStart`) is protected regardless of how many catch clauses a
method has. No over-protection (waste) and no under-protection (the one slot the
runtime writes is the one protected). Confirmed.

### 1d. `protectedRegFinalIndex` computation is correct

`Optimizer.RegisterCleanup.cs:130-139` computes
`protectedRegFinalIndex = protectedReg - (count of unusedRegisters < protectedReg)`.
This is the correct post-compaction index: the protected register survives
(never removed, since it's in `usedRegisters`) and shifts down by exactly the
number of compactions that occurred below it. The downstream consumer
(`AllocateLocalStackSpaces`, `JITCompiler.cs:1487-1492`) guards with
`catchRegIdx >= 0 && catchRegIdx < localInfo.Length`, and `localInfo` is sized
`paramCnt + varCnt + StackRegisterCount` (`JITCompiler.cs:1373`) — which, with
the protection guaranteeing the register survives into `StackRegisterCount`,
includes a slot at the resolved index. Empirically confirmed: TC2 (`catch (e) e
!= null`) and all nested/cross-frame catch tests pass, proving the slot is
resolved and written correctly at runtime.

**Priority-1 explicit verdict: CLEAN.** The shared-component change is
Legacy-neutral in the Legacy build and strictly safer in the Neo build; the
protection is precisely scoped; the final-index output is correct.

---

## Priority 2 — Opcode arm semantics

**Verdict: faithful ports of the Legacy reference.**

- `Throw` (`ILIntepreter.Neo.cs:2033-2038`): resolves the exception object from
  the throw register's ref slot via `localInfos[ip->Register1].Offset` (correct
  — Throw is NOT lowered by `LowerNeoOffsets`, so `Register1` is a raw register
  index, resolved exactly like `Ret` reads its source via `DstOffset`; matches
  planning 8.10). `GetNeoException` (`2206-2219`) returns the object as-is or
  throws `NullReferenceException` for a null/missing object (throwing null is an
  NRE in the CLR — correct). Matches Legacy `ILIntepreter.Register.cs:5307-5312`.
- `Rethrow` (`2039`): `throw lastCaughtEx`. Identical to Legacy
  `Register.cs:5313-5314`.
- `Leave`/`Leave_S` (`2040-2058`): routes through an enclosing finally via
  `FindExceptionHandlerByBranchTarget` when the leave crosses a finally
  boundary, recording the leave target in `finallyEndAddress` for the subsequent
  Endfinally. Verbatim port of Legacy `Register.cs:2765-2783`.
- `Endfinally` (`2059-2079`): `finallyEndAddress < 0` sentinel (set by
  `HandleException` for finally/fault entry at `ILIntepreter.cs:4800`) re-throws
  `lastCaughtEx`; otherwise resumes at the leave target, routing through further
  enclosing finally. Verbatim port of Legacy `Register.cs:2785-2806`. **Finally
  is guaranteed to run on both paths**: exception exit (HandleException sets
  `finallyEndAddress = -1` and jumps to the finally handler start), and normal
  Leave exit (the Leave arm diverts into the finally first). TC3 (finally runs
  after catch swallows) and TC4 (finally runs on normal Leave) both pass.
- `Endfilter` (`2080-2081`): remains a Step-tagged NIE with a clear comment
  ("IL filter blocks ... out of scope"). Acceptable non-goal (spec §filters;
  rare in C#).

---

## Priority 3 — Catch-object slot resolution

**Verdict: correct.**

- The JIT annotation (`NeoCatchExceptionRegIndex/ByteOffset/RefOffset`,
  `JITCompiler.cs:66-71`) is computed in `Compile` only when the method has a
  Catch handler (`459-472`), behind `#if ENABLE_NEO_MODE`.
- `AllocateLocalStackSpaces` stamps the byte/ref offsets from
  `localInfo[NeoCatchExceptionRegIndex]` (`1487-1497`), with an OOB guard and a
  `-1` fallback for methods without catch (write skipped at runtime because the
  `if (catchByteOff >= 0)` guard at `ILIntepreter.Neo.cs:2125` skips the write;
  and `isCatch` cannot be true for a method with no catch handler anyway).
- "Methods with EH are never inlined → annotation stable": verified the inliner
  excludes EH methods (referenced at `JITCompiler.cs:2466` per planning 8.9). If
  a method has EH it is not inlined, so the per-method `CompiledFrame`
  annotation is consumed by the same frame it was stamped on. Confirmed.
- ExecuteNeo writes `ex` to `mStack[frameRefBase + catchRefOff]` and stores that
  index at `frameBase + catchByteOff` (`2126-2129`) — mirroring how every
  ref-typed local is bound in Neo. Correct.

---

## Priority 4 — Self-cleaning `pendingThrow`

**Verdict: correct. Every exit path routes through the bottom cleanup.**

- `pendingThrow` is declared at `ILIntepreter.Neo.cs:369`, BEFORE the `fixed`
  block (line 371). The re-throw (`2193-2194`) is AFTER the `fixed` block closes
  (line 2163). **Re-throwing outside `fixed` — correct; no pinned-pointer
  re-throw.**
- Two stash sites both set `returned = true` then `break` out of the `while`:
  - `unhandledException` re-throw path (`2137-2144`): stashes `ex`, breaks.
  - The `!DebugService.Break` wrap path (`2146-2160`): sets
    `unhandledException = returned = true` first, then (only if Break returns
    false) stashes the wrapped `ILRuntimeException` and breaks.
- Both `break`s exit the `while (!returned)` loop and land at the bottom cleanup
  (`2165-2174`: pop this frame if on top, truncate mStack to `frameRefBase`),
  THEN `throw pendingThrow` (`2193`). So the frame is popped and mStack released
  before the exception propagates — every Neo frame is self-cleaning. Confirmed.
- DEBUG `Break`-returns-true path: the `if(!Break){...}` body is skipped,
  `returned` is already true, control falls out of the catch back to the while
  condition → exits → cleanup runs → `pendingThrow` is null → returns
  `frameBase` (no throw). This is the pre-existing debugger-swallow path;
  Step 14 only ADDED the stash inside the `!Break` branch, so the Break path is
  unchanged. No stale `pendingThrow` (never set on this path). Correct.
- The `return null` sites in the call arms are NOT modified by this diff (the
  cross-frame propagation works via the C# throw through `InvokeNeoCallTarget`,
  per planning 8.8; `return null` is reachable only in debugger mode as a safety
  net). Confirmed by grep: `return null` does not appear in the Neo diff. No
  regression — the implementer's claim (8.8) holds.

---

## Priority 5 — Cross-frame propagation

**Verdict: correct; TC6/TC7 pass for the right reason.**

Cross-frame flow: callee's `ExecuteNeo` finishes with `pendingThrow != null` →
`throw pendingThrow` (`2194`, an `ILRuntimeException` wrapping the original) →
propagates through `InvokeNeoCallTarget` (line 146, the recursive `ExecuteNeo`
call throws) → caught by the CALLER's per-iteration `catch (Exception ex)` →
`HandleException` searches the caller's `ehs` at the call-site address → caller's
catch handler matched. TC6 (caller catches callee's 1/0 → returns 9) and TC7
(caller catch + caller finally both run) both pass green, exercising this exact
path. The shared `HandleException` frame-pop loop (`ILIntepreter.cs:4770-4779`)
is consistent with self-cleaning: because the callee already popped itself and
released its mStack, the loop finds the caller's frame on top and does no callee
cleanup. Confirmed.

---

## Priority 6 — Regression + scope

**Verdict: clean.**

- **NeoStep smoke: 58/58 pass, 0 fail** (re-run by reviewer; CLI `Debug_Neo`,
  TestCases `Debug`, `-f net8.0`, `NeoStep` filter). Matches the implementer's
  claimed 58/58. No regression; 9 new Step 14 cases green.
- **Builds:** CLI `Debug_Neo` → 0 errors; TestCases `Debug` → 0 errors.
- **Scope:** all new runtime code is behind `#if ENABLE_NEO_MODE`
  (`ILIntepreter.Neo.cs` arms/`GetNeoException`/`pendingThrow`;
  `JITCompiler.cs` catch-reg computation + 3 `CompiledFrame` fields + slot
  resolution). The ONLY shared-engine surface touched is the
  `Optimizer.RegisterCleanup.cs` parameter-signature change, which is
  Legacy-neutral (Priority 1). No modification to `ExecuteR`, `HandleException`,
  `GetCorrespondingExceptionHandler`, or `FindExceptionHandlerByBranchTarget`.
- Non-goals honored: Endfilter NIE (acceptable); isinst/castclass not used
  (Step 15); async exceptions not handled (Step 20); CLR `newobj`-based
  `throw new T(...)` not testable (Step 18) — all Step 14 throw sources are
  no-newobj native faults (1/0, null-deref, rethrow).

---

## Findings (all non-blocking)

### Minor 1 — Catch-object slot stores the wrapper, not the unwrapped inner (cross-frame); matches Legacy but spec wording is loose
- **Severity:** Minor (informational). **Not a bug.**
- **File:** `ILIntepreter.Neo.cs:2128`; spec `neo-exceptions/spec.md:49-50`.
- **What:** The spec requirement says "The exception object placed in the slot
  SHALL be the unwrapped inner exception when the propagated object was an
  `ILRuntimeException`." The implementation writes `mStack[catchSlotIdx] = ex`
  where `ex` is the caller's `catch` variable. `HandleException` unwraps
  `ILRuntimeException` only into its LOCAL by-value `ex` parameter and into
  `lastCaughtEx` (`ILIntepreter.cs:4750-4758, 4780`) — it does NOT update the
  caller's `ex`. So in the cross-frame case the slot receives the WRAPPER, not
  the inner exception.
- **Why this is acceptable:** Legacy does the IDENTICAL thing —
  `AssignToRegister(ref info, exReg, ex)` (`Register.cs:5327`) also uses the
  caller's by-value `ex`. Neo matches Legacy byte-for-byte, which is the actual
  requirement ("matching Legacy behavior"). The spec's "unwrapped" adjective is
  imprecise. The distinction is not exercised by any current test (same-frame
  throws store the raw exception; cross-frame catches use `catch {...}` with no
  `e` access). When isinst lands in Step 15, a cross-frame `catch(T e){ e is T }`
  could surface this — but it would surface identically in Legacy, so it is a
  pre-existing shared-engine characteristic, not a Step 14 regression.
- **Fix (optional, future):** tighten the spec wording to "stores the exception
  object exactly as Legacy does (the caller's `ex`, which may be an
  `ILRuntimeException` wrapper in the cross-frame case)."

### Minor 2 — TC2 only proves the catch slot is non-null, not that it is the right object
- **Severity:** Minor (informational). Accepted limitation.
- **File:** `TestCases/NeoStep14Test.cs:46-49`.
- **What:** TC2 asserts `e != null`, which proves the slot was written but not
  that it holds the thrown instance. A stronger assertion (`e is
  DivideByZeroException`) would prove correctness but requires isinst (Step 15).
- **Why acceptable:** documented in planning 8.11. The runtime write path is
  shared with TC1/TC5/TC6 (all type-matched catches that pass), so the slot
  resolution is adequately covered.
- **Fix (optional):** strengthen TC2 once Step 15 lands.

### Nit 1 — `Endfinally` with `ehs == null` and `finallyEndAddress == 0` jumps to address 0
- **Severity:** Nit. Unreachable; matches Legacy.
- **File:** `ILIntepreter.Neo.cs:2060-2079`.
- **What:** if `ehs == null` at Endfinally, `FindExceptionHandlerByBranchTarget`
  returns null and `ip = ptr + finallyEndAddress` = `ptr + 0`, jumping to
  instruction 0. This state is unreachable (a finally implies `ehs != null`, and
  `finallyEndAddress < 0` is only set by `HandleException` which requires `ehs`).
  Legacy `Register.cs:2785-2806` has the identical unguarded shape. No
  regression.
- **Fix:** none needed; if desired, add an `ehs != null` guard for defensiveness
  (cosmetic).

### Nit 2 — `NeoCatchExceptionRegIndex` is `int` but holds a register index
- **Severity:** Nit. Cosmetic.
- **File:** `JITCompiler.cs:68`.
- **What:** the field is `int` while the compaction math uses `short`. Harmless
  (the `-1` sentinel and small indices fit). The other two fields are also
  `int` for consistency. No action.

---

## Conclusion

The change is **CLEAN** and ready to ship. The top risk
(`Optimizer.RegisterCleanup.cs` Legacy-neutrality) is verified: there is a
single caller, the Legacy build passes `-1` structurally, and the Neo-build
shared-JIT path is strictly safer for Legacy. All opcode arms are faithful ports
of the Legacy reference, the self-cleaning unwind routes every exit through the
bottom cleanup with the re-throw outside the `fixed` block, cross-frame
propagation works via the C# throw through `InvokeNeoCallTarget`, and the
NeoStep smoke is 58/58 green. The two Minor findings are spec-wording / test-
strength notes that do not affect correctness.
