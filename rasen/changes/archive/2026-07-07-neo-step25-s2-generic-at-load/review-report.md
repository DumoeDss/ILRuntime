# Review Report: neo-step25-s2-generic-at-load

**Reviewer:** independent verify-stage (author != verifier)
**Date:** 2026-07-07
**Verdict:** **APPROVE-WITH-FINDINGS** (no Blockers; Minor + Trivial notes only)

The change is correct, the load-binding proof is decisive, regression is clean,
Legacy-neutral is confirmed, and the implementation matches the spec delta. The
findings below are hardening/documentation nits, none of which block ship.

---

## 1. Independent re-run counts (capstone + regression + Legacy)

Re-ran every filter myself (NOT the implementer's counts). All match the claim.

| Filter | Implementer claim | Independent re-run | Match |
|---|---|---|---|
| NeoStep25LoadExec (capstone) | 21/21 | **21/21, 0 failed** | yes |
| NeoStep (Debug_Neo) | 215/215 | **215 ran, 0 failed** | yes |
| NeoStep22SelfCheck | 55/55 | **55/55** | yes |
| NeoStep23Roundtrip | 15/15 | **15/15** | yes |
| NeoStep24CliRoundtrip | 5/5 | **5/5** | yes |
| NeoStep (plain Debug, useRegister=true) | 215, 8 pre-existing fail | **215, 8 fail (all pre-existing)** | yes |

The 8 Legacy failures are NeoStep6Test.NeoNaNR8, NeoStep13Test x2,
NeoStep14Test x3, NeoStep15Test x1, NeoStep16Test x1 — all pre-existing
(Legacy ExecuteR limitations). **None** are the new S2 probe methods
(Echo / ConstGeneric / WrapEchoInt|Long|Ref|Struct / NeoStep25ProbeVal).

CLI built `Debug_Neo` (0 errors); TestCases built plain `Debug`. Build-cache
gotcha NOT triggered: verified `ILRuntime.dll` mtime tracks source (the stash
rebuild took 6.25s = real recompile, and the capstone flip confirmed the
binary changed).

---

## 2. Mutation-cell proof strength — DECISIVE (the binding lesson holds)

The prompt's central question: is the mutation cell load-bearing, or could it
read a JIT path by accident? Probed directly by stashing the single
load-bearing line `def.InitTemplateFromNeo(tpl)` in the loader (replaced with
`_ = tpl;`), rebuilding, and re-running the capstone.

Result with S2 overwrite **DISABLED**:
- capstone drops to **20/21, 1 failed**;
- the failing cell is exactly `ConstGeneric<T> template body-mutation`, with
  message: **`expected MUTATED=7654321 got=1234567 (a JIT-template run would
  yield CONST=1234567)`**;
- every OTHER cell stays green.

With S2 **ENABLED**: the cell observes MUTATED=7654321 (PASS).

Conclusion: the mutation cell genuinely distinguishes the AOT template from the
JIT-captured template. It is NOT reading a JIT path by accident — when the AOT
overwrite is removed, it falls back to observing the un-mutated JIT value
exactly as predicted. The fresh-instance mechanism (`constDef.MakeGenericMethod
(string)` -> new ILMethod with bodyRegister=null -> BodyRegister getter re-runs
InitCodeBody -> Step-22 hook reads the now-overwritten definition cache ->
CloneAndPatch against the mutated AOT template) is sound. Verified
`MakeGenericMethod` returns a fresh instance (ILMethod.cs:1367) and the
BodyRegister getter re-enters InitCodeBody when bodyRegister is null
(ILMethod.cs:408-409).

**Important caveat (see Finding T2):** only the mutation cell is load-bearing
for proving AOT-ran. The functional cells and the 4 structural-equiv cells
PASS regardless of whether S2 is enabled (confirmed by the same stash run).
This is consistent with the design's explicit "a green JIT==AOT comparison is
INSUFFICIENT" statement, but a reader should not over-credit those green cells.

---

## 3. F-11 (NEO-AOT-GENERIC-EAGER-COMPILE) assessment — ACCEPTED-KNOWN, not corruption

F-11 = a generic instance force-compiled before the loader binds keeps a stale
JIT bodyRegister. Assessment: this is a **missed optimization**, NOT silent
corruption, and is correctly accepted-known + S3-routed.

Reasoning (verified in code):
- The stale body cached on the pre-Attach instance is a **correct per-
  occurrence JIT body** (the reference path) — it yields correct results for
  the instance's type args. The AOT body also yields correct results. So both
  are correct; F-11 loses the AOT optimization, not correctness.
- `DoCloneAndPatch` COPIES the template body into a new array
  (`frame.NeoExecuteBody`); it does not share the template object's body by
  reference into the instance.
- The S2 setter overwrites the definition's `genericMethodTemplate` **field**
  (points to a new GenericMethodTemplate object); it does not mutate the old
  template object's contents in place. So an already-built instance (which
  holds its own copied body, and at most a ref to the OLD template object) is
  unaffected by the overwrite.
- The capstone deliberately avoids the scenario via fresh instances
  (WrapEchoLong for value-T; MakeGenericMethod(string) for the mutation cell),
  so it does not mask F-11 — it simply does not exercise it.

No ship-blocker. A force-compile-before-attach instance re-invoked after attach
would reuse its correct JIT body; only the AOT speedup is missed for that
instance. S3 can address it (e.g., invalidate instance bodies on attach).

---

## 4. Findings

### M1 (Minor) — T-identity rejection / skip path is correct-by-inspection but UNTESTED
The rejection path in `RebuildPatchesNoCecil` (a `TypeToken`/`MethodToken`
patch with non-`none` CecilTokenKind -> `hasIdentityToken=true` ->
`BuildFromNeoRecord` returns null -> loader skips -> generic keeps JIT) is the
key safety gate that keeps a T-identity template from silently running with a
missing patch (the F-10 / Step-22 Constrained-token silent-corruption class).
It is correct by inspection:
- `PatchKind`: TypeToken=0, MethodToken=1, IsRefMoveFlag=2
  (GenericMethodTemplate.cs:54-66).
- Writer pairs Kind->CecilTokenKind: MethodToken->1, TypeToken->0, else->2
  (NeoAssemblyWriter.cs:862-876); `CecilTokenKind` "2 = none" (NeoAssembly.cs:
  229). So writer-produced TypeToken/MethodToken patches always have
  CecilTokenKind != 2 -> rejected. Correct.
But the probe's generic methods (`Echo<T>`, `ConstGeneric<T>`) have
`Patches=[]`, so NO patch-rebuild branch (rejection OR IsRefMoveFlag
acceptance) is exercised — only the empty case is. This is exactly the "green
smoke does not prove a gate correct" situation the design warns about, applied
to the gate itself. Not a Blocker: the S2 slice explicitly does not claim
T-identity coverage, the code is simple, and a miss is a skip (additive).
**Recommend:** add a synthetic-record host-side cell (construct a
`NeoTemplateRecord` with a TypeToken patch, assert `BuildFromNeoRecord`
returns null) in S3 or as a follow-up.

### T1 (Trivial) — redundant `CecilTokenKind != 2` clause is slightly backwards
The check `(Kind == TypeToken || Kind == MethodToken) && CecilTokenKind != 2`
(NeoAssemblyLoader via RebuildPatchesNoCecil, GenericMethodTemplate.cs:504-505):
the `&& CecilTokenKind != 2` is redundant for writer-produced records (Kind
already determines CecilTokenKind). Worse, it is slightly backwards — a
hypothetical malformed record (Kind=TypeToken, CecilTokenKind=2) would NOT
trigger rejection, would be accepted with CecilToken=null, and would then be
silently SKIPPED at DoCloneAndPatch (:485) -> a missing-patch (wrong type-token)
body. The writer never produces such a record, so no real impact. **Recommend:**
simplify to `Kind == TypeToken || Kind == MethodToken` (reject regardless of
CecilTokenKind) for robustness, or keep and document the writer-consistency
assumption.

### T2 (Trivial) — document that only the mutation cell is load-bearing
The stash run showed the 4 functional cells + 4 structural-equiv cells pass
whether or not S2 is enabled. The structural-equiv cell compares
`CompilePerOccurrenceNeoBody` vs `CompileViaAotTemplateNeoBody(inst,
echoDef.GenericMethodTemplateCache)` — but with S2 stashed that cache holds the
JIT-captured template, so the comparison is JIT-vs-JIT (still equal). The cell
proves "cache-template CloneAndPatch == per-occurrence JIT", which holds for
both templates; it does not distinguish AOT from JIT. This matches the design
intent but should be stated in the ship-log so the green structural cells are
not mistaken for AOT-ran proof. The mutation cell is the sole load-bearing
proof (and it is decisive).

### T3 (Trivial) — MatchGenericDefinition does not disambiguate by full signature
`MatchGenericDefinition` (NeoAssemblyLoader.cs) matches by name + paramCount +
`GenericParameterCount > 0 && !IsGenericInstance`; a 2+ collision -> null ->
skip. Safe (collision -> JIT fallback, additive), and fine for the probe (no
collisions). Full signature / generic-arity disambiguation is documented as
round-2. No action needed for S2.

---

## 5. Blast radius / gating

Every engine edit is `#if ENABLE_NEO_MODE`:
- `NeoAssemblyLoader.cs`: entire file `#if ENABLE_NEO_MODE` (line 1).
- `GenericMethodTemplate.cs`: entire body `#if ENABLE_NEO_MODE` (line 13 ->
  line 804 `#endif`); `BuildFromNeoRecord` (line 424) and
  `CompileViaAotTemplateNeoBody` (line 769) are inside.
- `ILMethod.cs`: `InitTemplateFromNeo` (line 1414) is inside the Neo block
  (1382-1419); its `if (IsGenericInstance) return;` guard is consistent with
  `StoreGenericTemplate` and the `GenericMethodTemplateCache` getter (returns
  null for instances).
- `NeoStep25LoadExecCheck.cs`: `#if ENABLE_NEO_MODE && DEBUG`.
Plain `Debug` build = 0 errors (all Neo code compiles out). Legacy `ExecuteR`
byte-identical. `StoreGenericTemplate`'s overwrite guard is unchanged for the
JIT capture path. The Step-22 hook, `ExecuteNeo`, the optimizer, the JIT, the
Step-23 format, and the Step-24 tool are NOT modified.

---

## 6. Spec coherence

The implementation matches `specs/neo-optimizer/spec.md` (4 ADDED requirements):
- Loader consumes `model.Templates` + `BuildFromNeoRecord` + `InitTemplateFromNeo`
  overwrite -> verified.
- "A miss is skipped, not fatal" -> verified (loader `continue` +
  `report.Skipped`; cache stays null -> Step-22 hook falls through to JIT).
- No-T-identity-token slice boundary; T-identity/EH/cross-AppDomain/decoupling
  deferred to S3 -> honestly tagged in design D3 + the spec; NOT promoted to
  "met".
- `VariableTypes` re-resolved from `VariableTypeRefIdxs` via the loader closure
  (`ResolveVariableType`); `Addr`/`Symbols`/Constrained tokens null;
  `CecilToken = null` on every rebuilt patch -> all verified.
- Capstone reuses the same `NeoCompiler`/`NeoAssemblyReader`/
  `NeoAssemblyLoader` seams (no test-only compile path) -> verified.

Deferred items are tagged honestly; no scope inflation.

---

## 7. Summary

The S2 slice does what it claims: the `.neo` `TemplateTable` is consumed at
load, generic-method templates are reconstructed and bound to the live
definitions (overwriting the JIT-captured template), and a generic call on an
AOT-loaded type runs Step-22 `CloneAndPatch` from the AOT template. The
mutation cell — proven load-bearing by the stash probe — establishes that the
AOT template body (not a JIT-cached one) drives `CloneAndPatch`. Regression is
clean, Legacy is byte-identical, the spec is coherent, and F-11 is an accepted
missed-optimization rather than corruption. Ship-ready; address M1 (a
rejection-path test) as a follow-up.
