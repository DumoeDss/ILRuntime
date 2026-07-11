# Planning Context — implement-neo-opt-hardening

Seeds the planner. Read FIRST, then research only what is missing. Append
durable findings to section 8 after propose.

## 1. User intent
> "按照你建议的顺序，继续按当前流水线推进后续内容！"

This run = **[OPT-HARDEN]** only — the first phase of the deferred-items
sequence (see `.trae/documents/neo-deferred-items.md`). It fixes three
pre-existing Neo optimizer/arith correctness bugs surfaced (NOT introduced) by
Steps 12b and 16. Committed AND pushed after review clean (user pre-authorized
commit+push per phase).

Prior state (committed + pushed): Steps 11-16 + the deferred-items doc. HEAD=`0a6bd46a`. NeoStep smoke baseline = 72/72.

## 2. Decompose decision (LEAD, already taken)
**SKIP decompose.** Three small, same-theme correctness fixes (Neo optimizer +
arith). They touch different passes (FCP, BCP/copy-prop, conv/compare) but are
one coherent "optimizer/arith hardening" slice, independently testable. Pipeline:
propose → apply → verify → review-loop → ship → archive → (LEAD commits + pushes).

## 3. Scope — the three bugs (from `.trae/documents/neo-deferred-items.md`)

### K1 — FCP mis-propagates value-type Moves (HIGHEST VALUE: silent correctness)
After `b = a`, FCP rewrites later `b.field` reads to `a.field` even AFTER
`a.field` is mutated, because FCP's kill condition (`Optimizer.FCP.cs`) only
checks a whole-register write, not a field write via `ldloca; stfld`. Real
`b = a; mutate(a); read(b.field)` is silently wrong.
**Fix:** add a field-write kill to FCP — a `stfld` (via `ldloca`/`ldflda`) that
writes a field of a propagated SOURCE register must kill that propagation
(because the source's field changed, so the dest's copied value is now stale).
Surfaced by Step 12b (ship-log K1); documented in
`openspec/changes/archive/2026-07-04-implement-neo-step12b/`.

### Q-STRUCT — struct-local + field-mutation + element-read optimizer temp-renumber
A struct local, followed by a field mutation, followed by an element read, hits
an optimizer temp-renumber quirk in BCP/copy-prop. Basic struct store/load
round-trips correctly (Step 16 TC5 green); the quirk is in optimizer temp
renumbering. Surfaced by Step 16 (ship-log quirk-struct-renumber); documented in
`openspec/changes/archive/2026-07-04-implement-neo-step16/`.

### Q-LONG — long default-zero compare (conv.i8) quirk
A long default-zero compare (involving `conv.i8` + compare) mis-evaluates. Long
store/load itself round-trips correctly (Step 16 TC2 green); the quirk is in
`conv.i8` + long-compare evaluation. Surfaced by Step 16 (ship-log
quirk-long-compare).

## 4. RESEARCH REQUIRED (planner — root-cause each precisely)
These are BUGS, so the planner must produce a precise ROOT-CAUSE + MINIMAL FIX
for each, grounded in the actual code (not a guess). For each:
- Reproduce the failure (write a probe test that fails on the current HEAD, to
  confirm the root cause before designing the fix — the implementer will keep a
  version of this probe as a regression test).
- Locate the exact code at fault (FCP kill logic in `Optimizer.FCP.cs`; the
  BCP/copy-prop temp-renumber logic; the `conv.i8`/long-compare evaluation in
  `ILIntepreter.Neo.cs` and/or the JIT lowering of `Conv_I8`).
- Design the MINIMAL fix (kill condition, temp-renumber correction, compare
  widening) that does NOT regress the existing 72 NeoStep cases.

Read the relevant Step 12b + Step 16 `planning-context.md` §8 findings (they
pinpoint the suspect code) and the ship-logs for the repro descriptions. Do NOT
modify Legacy (`ExecuteR`).

## 5. Hard constraints (from CLAUDE.md — obey exactly)
- Branch `features/object-model-overhaul`; all new/changed runtime code behind
  `#if ENABLE_NEO_MODE` (the bugs are Neo-specific; Legacy is the reference).
  NOTE: FCP/BCP/copy-prop optimizer passes are SHARED (used by Legacy too via
  `ExecuteR`) — a change there MUST be Legacy-neutral (the Step 14 lesson: a
  shared-pass change must not alter Legacy behavior). If a fix can't be made
  Legacy-neutral in the shared pass, gate it Neo-only or find a Neo-specific
  intervention point.
- **Build:** CLI `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` → 0 errors. TestCases `dotnet build TestCases/TestCases.csproj -c Debug` (NEVER Debug_Neo).
- **Run tests:** FULL NeoStep smoke (regression): `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` → all-green (was 72/72; your hardening tests add; NO existing case regresses). `-f net8.0`. `Debug_Neo` prints lots of JIT/optimizer output — normal.
- ALSO run the **Legacy baseline** to confirm shared-pass changes are
  Legacy-neutral: `dotnet run -c Debug -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` is Neo; for Legacy, build CLI with plain `Debug` and run a Legacy smoke (the implementer/reviewer should confirm the shared optimizer pass change does not regress Legacy — at minimum a code-read argument +, if feasible, a Legacy filter run).
- Tests = `public static` parameterless; optional `[ILRuntimeTest]`. Add `TestCases/NeoOptHardeningTest.cs` (or extend an existing NeoStep test), ASCII. Each bug gets a regression test that FAILS before the fix and PASSES after.
- Test >10s = infinite loop — kill and investigate.
- **REGRESSION CAUTION:** optimizer/arith changes are the most regression-prone (they affect EVERY method). The full NeoStep smoke is the gate; Legacy-neutrality is the second gate.
- **CJK write caveat:** Author ASCII.

## 6. What the planner must produce (propose stage)
Invoke `openspec-propose` (Skill) OR author directly. Artifacts under `openspec/changes/implement-neo-opt-hardening/`:
- `proposal.md` — Why / What Changes / Impact. For each of K1/Q-STRUCT/Q-LONG: the root cause (file:line) + the minimal fix + whether the fix point is Neo-only or shared-pass (and the Legacy-neutrality argument if shared).
- `design.md` — concrete, code-grounded per bug: root cause, the exact fix (kill condition / temp-renumber correction / compare widening), the regression test that pins it, and the Legacy-neutrality argument for any shared-pass change.
- `specs/<capability>/spec.md` — ADDED requirements. Capability: `neo-optimizer` (new) or extend an existing one. Requirements: "FCP respects value-type copy independence after a field mutation", "BCP/copy-prop temp-renumber is stable across struct field mutation + element read", "long (conv.i8) zero-compare evaluates correctly". Each with a scenario.
- `tasks.md` — checkbox tasks (one block per bug: repro probe → root-cause confirm → minimal fix → regression test → smoke).

Author != verifier: you ONLY propose.

## 7. Append-only findings log
(planner appends durable decisions/constraints discovered during propose here)

## 8. Planner findings (2026-07-04, propose stage)

Environment built and the three bugs probed on current HEAD
(`features/object-model-overhaul`). CLI `Debug_Neo` 0 errors; TestCases
`Debug` 0 errors. Probes live in `TestCases/NeoOptHardeningProbe.cs` (the
implementer promotes the K1 probe to `TestCases/NeoOptHardeningTest.cs` and
keeps the Q-STRUCT/Q-LONG probes as non-asserting documentation cases).

### 8.1 K1 - CONFIRMED on HEAD (DivideByZero fires). FIX designed.

- **Repro:** `ProbeK1_FcpVtPropagation` fails: `OptHardK1Struct a; a.n=11;
  b = a; a.n=999; b.n` returns 999. DivideByZero at the `b.n` read.
- **Root cause (file:line):** `Optimizer.FCP.cs` `ForwardCopyPropagation`.
  The whole-VT `Move b=a` is recorded as a propagation (xDst=b, xSrc=a). A
  later `b.n` read is a `Ldfld_*_Inline` whose single source `Register2 ==
  xDst` triggers `ReplaceOpcodeSource(.., xSrc)` -> rewrites to `a.n`
  (in-block Optimizer.FCP.cs:76-99; cross-block :235-252). The KILL only
  fires on a whole-register write (`xDst == yDst`, :153-167 / :293-300), but
  an intervening `stfld a.n` is a `Stfld_*_Inline` and
  `GetOpcodeDestRegister` returns FALSE for the whole `Stfld_*` family
  (Optimizer.Utils.cs:908-932) -> the source register is NEVER invalidated
  by a field write -> propagated `a.n` read observes the mutation. Silent
  correctness bug for in-frame VT copies.
- **Minimal fix (Neo-only, Legacy-neutral):** add a field-write kill to BOTH
  FCP loops. When `Y` is a `Stfld_*_Inline` and `GetOpcodeSourceRegister`
  reports `sR1 == xSrc` OR `sR1 == xDst`, abort the propagation
  (`postPropagation=false`/`ended=true` in-block; `cannotRemove=true`
  cross-block). A small `#if ENABLE_NEO_MODE` helper `IsNeoInlineStfld`
  lists the 12 `_Inline` codes (mirror Optimizer.Utils.cs:922-932).
- **Fix-point scope: SHARED pass, but the gate is Neo-only.** FCP is shared
  with Legacy `ExecuteR`. The fix is Legacy-neutral by THREE independent
  guarantees: (1) the opcode gate matches ONLY `_Inline` stfld codes, which
  are emitted exclusively by Neo `TypeSpecializeNeoOpcodes`
  (JITCompiler.cs:537) and never exist in a Legacy build; (2) the heap
  `Stfld_*` family is untouched, so Legacy reference-alias propagation is
  unchanged; (3) the whole check is inside `#if ENABLE_NEO_MODE`, so a
  Legacy build does not compile it. The K1 fix ALSO kills on `sR1 == xDst`
  (mutating the dest's field after the copy) - conservative and correct.
- **Regression test:** `NeoOptHardTest_K1_*` (the probe). FAILS before, PASSES
  after.

### 8.2 Q-STRUCT - NOT reproducible on HEAD. DEFERRED (no fix this change).

- **6 probes, all PASS** on HEAD: `ProbeQStruct_ElementReadAfterMutation`
  (the exact Step 16 TC5 pattern: `s.v=11; arr[0]=s; s.v=999; r=arr[0];
  if (r.v != 11)`) and `ProbeQStruct_BranchAfterMutation` (high register
  pressure). JIT Final shows `ldelem.any r2,r0,r9` then `ldfld.i4 r3,r2` -
  the ldelem dest (r2) is DISTINCT from the source local (r1); the optimizer
  correctly tracks the copy, so the bnei.un reads 11 not 999.
- **Decision:** the BCP/copy-prop/temp-renumber paths are SHARED and
  high-blast-radius. Shipping a fix without a reproducing test = a guessed
  change to a pass affecting every method. DEFER. Tracked as a spec
  requirement with a "confirm reproducibility first" gate. Resurfaces most
  likely in `Optimizer.BCP.cs` `BackwardsCopyPropagation` (the `xSrc == yDst`
  renumber at :97-141).

### 8.3 Q-LONG - NOT reproducible on HEAD. DEFERRED (no fix this change).

- **3 probes, all PASS** on HEAD: `ProbeQLong_ConvI8ZeroCompare`
  (`long[] arr; arr[0]=7L; if (arr[0]==0L)` + `if (arr[1]!=0L)`),
  `ProbeQLong_ScalarZeroCompare` (scalar long locals + register pressure),
  `ProbeQLong_DefaultFieldZeroCompare` (`default(QLongHolder).v == 0L`).
  JIT Final: `ldelem.i8`; `ldc.i4.0; conv.i8 r10,r10`; `ceq r1,r9,r10` -
  conv.i8 widens the int 0 to a long, the compare reads both as long.
  Correct. The conv readers (`ReadConvI8`, ILIntepreter.Neo.cs:2663) and the
  I8 compare/branch arms (Ceq_I8/Cgt_Un_I8/Bne_Un_I8 at :684-697, :1203-1216)
  all read `*(long*)` correctly, and `InferPrimTag` ->
  `GetTypedCompareOpcode`/`GetTypedBranchOpcode` (JITCompiler.cs:632-661,
  946-980) correctly widens to `_I8` when the operand register is LongType.
- **Decision:** no layout found where the compare mis-evaluates. DEFER.
  Tracked as a spec requirement. Resurfaces most likely in JIT branch
  type-specialization when the `0L` operand's register tag fails to inherit
  LongType from the `conv.i8` dest, or in a 4-byte/8-byte temp-slot overlap
  in `AllocateLocalStackSpaces`.

### 8.4 Scope outcome

Ship K1 (confirmed silent correctness bug) with a Neo-only, Legacy-neutral
FCP field-write kill. DEFER Q-STRUCT and Q-LONG (not reproducible; tracked in
spec, not guessed). Capability = new `neo-optimizer`. `openspec validate`
passes; `openspec status` isComplete=true (all 4 artifacts done, apply-ready).

## 9. Implementer findings (2026-07-04, apply stage) — K1 fix DID NOT WORK; root cause was MIS-DIAGNOSED

The design.md / §8.1 root cause is INCORRECT for the actual JIT layout. The
implemented fix (kill when a `Stfld_*_Inline` has `Register1 == xSrc` or
`== xDst`) is a NO-OP for the K1 repro and was reverted. STOP per hard
constraints (fix failed to make the probe pass; would widen scope to fix
correctly). Details below so the next pass designs the RIGHT fix.

### 9.1 Actual JIT layout (Final Results of `NeoOptHardTest_K1_FcpVtPropagation`)

```
0:initobj r0        # a
1:initobj r1        # b
4:ldloca.s r7, r0   # r7 = &a (ADDRESS HANDLE to a's storage)
5:ldc.i4.s r8,11
6:stfld.i4.inline r7, r8   # a.n = 11   <- Register1 = r7 (the ADDRESS), NOT r0
7:move r1, r0       # b = a   (the whole-VT copy FCP records: xDst=r1, xSrc=r0)
8:ldloca.s r7, r0   # r7 = &a again
9:ldc.i4 r8,999
10:stfld.i4.inline r7, r8  # a.n = 999  <- Register1 = r7 (still the ADDRESS)
11:ldfld.i4.inline r2, r0  # b.n read, FCP REWROTE source r1 -> r0 (THE BUG)
```

### 9.2 Why the design's kill never fires

`Stfld_*_Inline` `Register1` is the **owning slot** ONLY in the design's mental
model. In the actual lowered IR, the field store goes through a `ldloca.s`
address handle: `ldloca.s r7, r0` loads an address-to-r0 into temp r7, then
`stfld.i4.inline r7, r8` stores via that handle. So `Register1 == r7`, which is
neither `xSrc(r0)` nor `xDst(r1)`. The kill condition `sR1 == xSrc || sR1 == xDst`
is never satisfied -> no kill -> FCP still rewrites line 11 to `r0` -> reads the
mutated 999 -> DivideByZero. (Confirmed: after applying the design's fix, the
K1 probe STILL failed identically; reverted.)

This was verifiable BEFORE coding: in the Final dump, the inline stfld is
`stfld.i4.inline r7, r8` (Register1 = r7), not `stfld.i4.inline r0, r8`.

### 9.3 What a CORRECT K1 fix requires (for the next pass — NOT done here)

The source's field is reached *indirectly*: `ldloca.s rAddr, rBase` makes rAddr
an address-alias of rBase's in-frame storage; a subsequent
`Stfld_*_Inline rAddr, ...` therefore writes a field of rBase. A correct FCP
kill must resolve this indirection:

- Track `ldloca.s` aliases during the FCP scan: maintain a map
  addrTemp -> baseLocal (and invalidate when the addrTemp or base is redefined).
- In BOTH FCP loops, when `Y` is a `Stfld_*_Inline` whose `Register1` is an
  address temp currently aliased to `xSrc` (or `xDst`), abort the propagation.
  ALSO kill on a plain `Stind_*` / `Stobj` whose address source is such an alias
  (the `ldloca; stind` path), since C# may lower `a.n = v` via `ldloca; stind`
  instead of `stfld` in some layouts.

Equivalently / more conservatively: kill the propagation whenever ANY
`Stfld_*_Inline` / `Stind_*` / `Stobj` executes whose address operand traces
(through a `ldloca.s` chain) back to `xSrc` or `xDst`. This is materially more
state than the design's single-register compare and touches the shared FCP pass,
so it carries real Legacy/regression risk and must be re-scoped + re-reviewed.

### 9.4 Verification of the mis-diagnosis

- HEAD (no fix): K1 probe -> DivideByZero (bug present). [confirmed]
- Design's fix applied (kill on Stfld_*_Inline Register1 == xSrc/xDst, both
  loops + helper, #if ENABLE_NEO_MODE): K1 probe -> STILL DivideByZero. The
  Final dump shows the stfld Register1 is r7 (the ldloca temp), proving the kill
  never matches. [confirmed] Fix then reverted; tree restored to HEAD.

### 9.5 State left behind

- All code changes REVERTED: `Optimizer.FCP.cs` restored to HEAD; the temporary
  `TestCases/NeoOptHardeningProbe.cs` (propose-stage artifact) was removed (its
  patterns are not yet needed); no `NeoOptHardeningTest.cs` was shipped.
- tasks.md K1 block remains UNCHECKED (fix did not land).
- Q-STRUCT / Q-LONG: untouched (already deferred). Legacy: untouched.
- RECOMMENDATION: re-open the design. The K1 root cause is the `ldloca.s`-
  aliasing blind spot in FCP, NOT a missing single-register stfld kill. Re-plan
  the kill around ldloca-alias resolution (or a Neo-specific intervention at the
  point `TypeSpecializeNeoOpcodes`/offset-lowering emits the inline stfld, where
  the true owning base register is still known) before re-attempting.

### 9.6 Re-attempt SUCCESS (2026-07-04, corrected ldloca kill) — K1 LANDED

The corrected fix from §9.3/§9.5 recommendation (the conservative sub-option)
WORKED. No alias-map machinery was needed: the kill keys on the `ldloca`
itself, not on resolving the alias chain to the eventual stfld.

- **Fix shipped:** in BOTH FCP loops of `Optimizer.FCP.cs`, after the
  whole-register kill, add (gated `#if ENABLE_NEO_MODE`): if `Y.Code` is
  `Ldloca`/`Ldloca_S` and its source register `ySrc` (= `op.Register2`, the
  base local) equals `xSrc` OR `xDst`, kill the propagation
  (`postPropagation=false; ended=true; break` in-block;
  `cannotRemove=true; break` cross-block). 39 insertions, all gated. NO
  helper, NO Utils change (the Ldloca source was already enumerable at
  Optimizer.Utils.cs:463-464 + :500).
- **Why it is sound:** taking the address of a local means it can be mutated
  through that address by any later `stfld`/`stind`, so any copy-propagation
  involving that local (`xSrc` or `xDst`) is potentially stale. Conservative —
  it may disable a valid optimization in the rare "address taken but only
  read" case, but that is a missed optimization (perf), never a correctness
  regression.
- **Verification (2026-07-04):**
  - CLI `Debug_Neo` 0 errors; TestCases `Debug` 0 errors.
  - K1 regression: 3/3 PASS after fix (`NeoOptHardTest_K1_FcpVtPropagation`,
    `NeoOptHardTest_K1_MutateDestAfterCopy`,
    `NeoOptHardTest_K1_SourceAndEndAfterMutation`). On HEAD: 2 of 3 FAIL
    (DivideByZero) — the third passes trivially.
  - NeoStep smoke: 72/72 all-green, ZERO regressions (conservative kill did
    not break any existing case).
  - Legacy-neutrality: plain-`Debug` CLI compiles the kill out
    (`#if ENABLE_NEO_MODE`). Empirically verified — Legacy NeoStep smoke =
    7 failures IDENTICAL with and without the fix (stash test on HEAD);
    those 7 are pre-existing Neo-feature tests (try/catch internals, CLR-
    struct boxing, NaN compare), unrelated to copy propagation.
- **Docs updated:** design.md §K1 root-cause/fix/edge-cases corrected to the
  ldloca kill; proposal.md K1 bullet corrected; spec.md K1 requirement
  corrected (kill fires on ldloca source == xSrc/xDst, not on stfld
  Register1); tasks.md Block 1 + Block 4 checked.
- **Out of scope (untouched, confirmed):** Q-STRUCT, Q-LONG (still deferred —
  not reproducible on HEAD); Legacy `ExecuteR`; heap `Stfld_*`/`Ldfld_*`.

