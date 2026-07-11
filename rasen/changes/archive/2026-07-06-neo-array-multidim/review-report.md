# Review Report — neo-array-multidim

> **Verifier:** review-role (adversarial non-author review), OpenSpec `neo-array-multidim`.
> **Date:** 2026-07-06. **Branch:** `features/object-model-overhaul`. **HEAD:** `21900a3a`.
> **Diff reviewed:** unstaged changes vs HEAD on `ILRuntime/CLR/Method/CLRMethod.cs`,
> `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`,
> `TestCases/NeoStep16Test.cs` (+152/-11 across the 3 files; engine diff is +58/-11).
> **Method:** read the actual diff + surrounding context; constructed 3 additional
> adversarial probes; FAIL-on-HEAD stash-toggle proof; Legacy-neutral baseline.

## Scope check

**CLEAN.** Intent (proposal/design/tasks): close 3 reflection-fallback gaps
(Gap 1 Neo reference-return null encoding; Gap 2 Neo null-`this` NRE; Gap 3 shared
`TargetInvocationException` unwrap) + lift the `neo-arrays` multi-dim NON-GOAL +
8 regression/adversarial probes. Delivered: exactly those 3 engine edits + the 8
`NeoStep16_MultiDim*` probes. No scope creep, no unrelated engine changes, no
missing requirement. The engine edits are minimal and well-scoped to the
reflection-fallback path (no Newobj arm / JIT / autogen binder change).

## Per-gap scrutiny

### Gap 1 — Neo reference-return null encoding (`ILIntepreter.Neo.cs:716-728`) — CORRECT

The reference-return branch of `InvokeNeoClrMethod` now splits on `res == null`:
null → `*(int*)retDstPtr = -1` (the Neo null sentinel); non-null → unchanged
(`mStack[targetRetRefBase] = res; *(int*)retDstPtr = targetRetRefBase`).

**Null-sentinel consistency (the load-bearing question) — verified across all
consumers I could trace:**
- `Ldnull` writes `-1` (`ILIntepreter.Neo.cs:971`, cited by the implementer).
- Reference-param read: `idx < 0 ? null : mStack[idx]` (`CLRMethod.cs:515`) —
  handles `-1` as null.
- Byref reference write-back: `if (pv == null) *(int*)(targetBase + slotOff) = -1`
  (`CLRMethod.cs:621-622`) — the inverse convention, same sentinel.
- `Cgt_Un` null-test (the `x != null` lowering): `cguRes = cguA != -1 && (...)`
  (`ILIntepreter.Neo.cs:1322-1324`) — reads the 4-byte dest index directly and
  keys on `-1`.
- Gap 2's new `this` read (`thisIdx < 0 ? null`, `CLRMethod.cs:413`) — same.

So `-1` is the established, uniformly-handled null encoding. The previous code
(writing the valid index `targetRetRefBase` for a null result) was the outlier
that violated the convention; the fix brings the reflection-return path into
line. No downstream consumer misinterprets `-1`: any path that later reads the
dest as a reference (param read, `this` read, byref write-back, `cgt.un`)
materializes `-1` as null. Rank-1 reference arrays never surfaced this because
they use the dedicated `Ldelem_Ref` opcode (already null-correct), not the
reflection return.

The non-null path is byte-identical to before — the 5 regression-guard probes
(primitive + metadata) and the 190-test Neo baseline stay green (confirmed:
Neo full smoke 198/198).

**Adversarial probe R1 (`NeoStep16_MultiDimReview_NullEqDir`):** tests the
SYMMETRIC direction the keeper misses — a null cell read into a local and tested
via `== null`, plus a non-null cell via `!= null` (rules out a false-negative
encoding the other way). PASS-after, FAIL-on-HEAD (see toggle table below).

### Gap 2 — Neo null-`this` guard (`CLRMethod.cs:402-414`) — CORRECT

`instance = thisIdx < 0 ? null : mStack[thisIdx]` in the reference-type `this`
sub-branch (the VT-`this` branch at `:377-398` is untouched, correctly). I traced
the two null-instance guards that consume this:
- Ctor non-newobj: `if (instance == null) throw new NullReferenceException();`
  at `CLRMethod.cs:551-552`.
- Method: `if (instance == null) throw new NullReferenceException();` at
  `CLRMethod.cs:579-580` (after the `!def.IsStatic` gate at `:575`).

For the array case (null `int[,] a; a[0,0]`): `Get` is an instance method →
`HasThis` true → declaring type is the array CLR type (not VT) → else branch →
`thisIdx == -1` → `instance = null` → method branch `instance == null` → throws
`NullReferenceException`. The probe catches exactly `NullReferenceException`.
Confirmed PASS-after, FAIL-on-HEAD (HEAD threw `ArgumentOutOfRangeException`
from `mStack[-1]` — the masking the fix removes).

`-1` is the only null encoding for `this` (the call-lowering writes the `this`
index; null is always `-1` per `Ldnull`), so `thisIdx < 0` catches exactly the
null case.

### Gap 3 — `TargetInvocationException` unwrap (6 sites, shared) — CORRECT

**Coverage — all 6 sites, no miss.** `grep '\.Invoke\(' ILRuntime/CLR/Method/CLRMethod.cs`
returns exactly 6 lines, each now inside a `try`:
- Neo overload: `:555-556` (non-newobj ctor `cDef.Invoke(instance,param)`),
  `:569-570` (newobj ctor `cDef.Invoke(param)`), `:582-583` (method `def.Invoke`).
- Legacy overload: `:697-698` (non-newobj ctor), `:710-711` (newobj ctor),
  `:737-738` (method).

The `redirectNeo`/`redirect` paths (not reflection) are correctly excluded.

**Stack preservation — correct idiom.** Each site uses
`catch (TargetInvocationException tie) { ExceptionDispatchInfo.Capture(tie.InnerException).Throw(); throw; }`.
This is the canonical stack-preserving rethrow — NOT a bare `throw tie.InnerException`
(which would reset the stack at the rethrow point). `ExceptionDispatchInfo` is in
`System.Runtime.ExceptionServices` (netstandard2.1; `using` added at `CLRMethod.cs:10`).
The trailing `throw;` is unreachable (EDI.Throw always throws) but REQUIRED for
definite-assignment: without it the compiler would see the catch as
fall-through-completing and flag `res` as possibly-unassigned after the try. Idiomatic.

**Type-generality — verified, not just IndexOutOfRange.** The unwrap is fully
type-agnostic (`catch TIE` → rethrow `InnerException` whatever it is).
- Keeper `MultiDimRank2OutOfRange`: IndexOutOfRange via the array `Get` METHOD
  site (`def.Invoke`, `:582`).
- **My R2 (`NeoStep16_MultiDimReview_Gap3CtorOverflow`):** `OverflowException` via
  the NEWOBJ CTOR site (`cDef.Invoke`, `:569`) — `new long[n,3]` with `n=-1`, no
  binder for `long[,]` so full reflection ctor. This hits a DIFFERENT Gap 3 site
  AND a DIFFERENT exception type. PASS-after, FAIL-on-HEAD (TIE on HEAD).
- **My R3 (`NeoStep16_MultiDimReview_Gap3RefElemOOR`):** IndexOutOfRange via a
  reference-ELEMENT array (`string[,]` `Get`) — confirms the unwrap is not
  return-type-sensitive and composes with the Gap 1 reflection-return path.
  PASS-after, FAIL-on-HEAD (TIE on HEAD).

**No double-unwrap.** Each site adds exactly one `catch (TIE)` and removes
exactly the one TIE layer that `MethodInfo.Invoke`/`ConstructorInfo.Invoke`
adds. After unwrapping, the rethrown `InnerException` is not itself a TIE
(unless the target method itself threw a TIE, in which case surfacing that TIE
is correct — one unwrap, not recursive). Analytical confidence; R2/R3 confirm
the surfaced type is exactly the inner type (a typed `catch` fires).

**Legacy-neutrality — verified.**
- Success path: with no exception thrown, the `try { res = ...Invoke(...); }`
  completes normally with the identical assignment; `try/catch` with no throw is
  effectively free in .NET. Byte-identical for the non-exceptional path.
- `grep TargetInvocationException TestCases/` = **0 matches** → no test catches
  TIE directly → unmasking cannot break a catch contract (OQ2 resolved).
- Legacy full baseline (plain `Debug`, `useRegister=true`, no filter):
  **723 ran / 10 failed** — matches the implementer's claim. The 10 are
  pre-existing Neo-under-Legacy failures (NeoOptHardening K1, NeoStep13/14/15/16
  Neo-specific, NeoNaNR8); none mention TIE, none newly introduced. Legacy
  multi-dim filter: 8/8 (incl OutOfRange), independently confirming Gap 3 is
  Legacy-correct.

## Adversarial probe results

3 probes written into `TestCases/NeoStep16Test.cs` (`NeoStep16_MultiDimReview_*`),
run on Neo + Legacy, then REMOVED before returning (test diff back to 105
insertions = 8 keepers only; TestCases rebuilt clean).

| Probe | Targets | Neo (fixed) | Legacy | HEAD engine (no fix) |
|---|---|---|---|---|
| R1 `NullEqDir` | Gap 1 symmetric `== null` / `!= null` | PASS | PASS | **FAIL** (divide-by-zero assertion) |
| R2 `Gap3CtorOverflow` | Gap 3 newobj-ctor site + OverflowException | PASS | PASS | **FAIL** (TIE wrapper leaked) |
| R3 `Gap3RefElemOOR` | Gap 3 on reference-element `string[,]` | PASS | PASS | **FAIL** (TIE wrapper leaked) |

All three are load-bearing (FAIL-on-HEAD → PASS-after), each covering a gap the
keeper set under-tests (symmetric null direction; ctor site + distinct exception
type; reference-element return type). They also pass on Legacy, confirming the
fixes are not Neo-only regressions for these shapes.

## FAIL-on-HEAD stash-toggle (independent proof)

Stashed ONLY the two engine files (kept the test probes), rebuilt CLI `Debug_Neo`
at HEAD engine, ran the multi-dim filter: **6 fail / 5 pass** (11 ran). The 6
failures with their exact root-cause signatures:

| Probe | HEAD message | Maps to |
|---|---|---|
| `MultiDimRank2String` | "Attempted to divide by zero." | Gap 1 (null→valid index false positive) |
| `MultiDimRank2Null` | "Index was out of range... (Parameter 'index')" | Gap 2 (mStack[-1] ArgOutOfRange) |
| `MultiDimRank2OutOfRange` | "Exception has been thrown by the target of an invocation." | Gap 3 (TIE) |
| `MultiDimReview_NullEqDir` (R1) | "Attempted to divide by zero." | Gap 1 |
| `MultiDimReview_Gap3CtorOverflow` (R2) | "Exception has been thrown by the target of an invocation." | Gap 3 (ctor site) |
| `MultiDimReview_Gap3RefElemOOR` (R3) | "Exception has been thrown by the target of an invocation." | Gap 3 |

The 5 passing (Probe/MultiCell/Rank3/Metadata/Long) are the primitive+metadata
regression guards (untouched paths). After `git stash pop` + rebuild: 11/11
pass. **The stash-toggle itself is the build-cache proof** (HEAD engine = 6 fail,
fixed engine = 0 fail → the ILRuntime DLL rebuilt; no stale binary).

## Independent baseline confirmation

- Neo multi-dim (8 keepers): **8/8 pass**.
- Neo full `NeoStep` smoke: **198/198 pass, 0 failed** (190 baseline + 8 probes).
- Legacy multi-dim: **8/8 pass** (incl OutOfRange → Gap 3 Legacy-neutral).
- Legacy full baseline: **723 ran / 10 failed** (no new regressions; matches claim).

## Findings

### Blocker: 0 — Major: 0

No blocking or major issues. All three gaps are correctly closed and
independently verified (FAIL-on-HEAD → PASS-after for each, including 3
additional adversarial probes I constructed).

### Minor — 1 (accepted-known, implementer-flagged, out of scope)

**[M1] `op_Inequality_3_Neo` autogen redirect: null-arg edge.**
`CLRMethod.cs` / autogen reader. The autogen `op_Inequality` redirect reads a
reference arg via `ReadNeoReference`, which does `mStack[idx]` with NO null guard.
A null string arg (`idx == -1`) to `op_Inequality` would throw
`ArgumentOutOfRangeException` (mStack[-1]). Not exercised by the keeper probes
(the `!= null` cell lowers to `cgt.un`, not `op_Inequality`) and not introduced
by this change (pre-existing in the autogen tooling). Flagged by the implementer
in `planning-context.md` "New edge discovered"; correctly deferred to a
neo-byref / autogen-hardening follow-up. Accepted-known.

### Trivial — 1 (defensive, never triggered in practice)

**[T1] `ExceptionDispatchInfo.Capture(tie.InnerException)` — no null-InnerException guard.**
All 6 Gap 3 sites. If a `TargetInvocationException` with a null `InnerException`
were ever caught, `EDI.Capture(null)` throws `ArgumentNullException`, masking the
original TIE. In practice this cannot occur: `MethodInfo.Invoke` /
`ConstructorInfo.Invoke` construct TIE via the `TargetInvocationException(Exception)`
ctor, which always sets a non-null InnerException; the null-InnerException ctor
`TargetInvocationException(string)` is never used by reflection. So this is
purely theoretical. A defensive `catch (TargetInvocationException tie) { var inner = tie.InnerException ?? tie; ExceptionDispatchInfo.Capture(inner).Throw(); throw; }` would
eliminate even the theoretical masking, but it is not required for correctness.
Severity Trivial; optional hardening.

### Non-findings (verified fine, recorded for completeness)

- The `isNewobj` reference-return branch (`ILIntepreter.Neo.cs:654-664`) has no
  null split — correct, because a reference-type ctor never returns null (it
  allocates); `res` from `cDef.Invoke(param)` for a ctor is always non-null. The
  null-encoding fix is correctly scoped to the non-newobj reference-return path.
- The trailing `throw;` after `EDI.Throw()` is unreachable but required for
  definite-assignment (the catch must not complete normally so `res` is
  considered assigned). Idiomatic.
- The unwrap cannot mask a TIE a host wants: no TestCases code catches TIE
  (grep = 0), and reflection's TIE wrapping is a CLR-internal detail that should
  never be user-visible. Strictly more correct.

## Verdict

**APPROVE.** No Blocker or Major findings open. All three gaps (Gap 1/2/3) are
correctly implemented, minimal, well-scoped, and consistent with established Neo
conventions (the `-1` null sentinel; the `instance == null → NRE` guard). The
shared Gap 3 edit is Legacy-neutral (success path byte-identical; 723/10
baseline unchanged; OutOfRange now Legacy-correct). Each gap has an independent
FAIL-on-HEAD → PASS-after proof — the keeper set (8) plus my 3 adversarial
probes (symmetric null direction; ctor-site + OverflowException type;
reference-element array). The one accepted-known Minor (M1) is out of scope
(autogen tooling) and correctly deferred; the one Trivial (T1) is theoretical
defensive hardening. Ship/archive can proceed.
