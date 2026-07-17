# Review Report — neo-ceq-null-sentinel (child 12 of neo-overhaul)

**Reviewer:** independent (author != verifier), dispatched report-only.
**Branch:** `features/object-model-overhaul`. **Base of diff:** `bb412dac` (child-11 Brtrue_Ref).
**Verdict:** **APPROVE-WITH-FINDINGS** (see rationale at the end).

The change adds three type-specialized Neo equality opcodes (`Ceq_Ref`, `Beq_Ref`,
`Bne_Un_Ref`) that compare the *referenced objects'* identity/nullness instead of the
raw mStack-index `int32`s, closing the **ceq form** of the reference null-comparison gap
left open by child-11 (which closed the direct-`brtrue` form). Diff = 5 source files,
**140 insertions, 0 deletions** (purely additive) + 1 new probe file.

---

## Standards axis — code review

### S1. The three runtime deref arms are correct [PASS — verified]

All three arms live next to their plain siblings in `ILIntepreter.Neo.cs`.

`R(v) = v >= 0 ? mStack[v] : null` covers all three Neo null encodings:

| operand encoding | `v` | `R(v)` | correct? |
|---|---|---|---|
| `-1` sentinel (`Ldnull`, CLR-static `Ldsfld`) | `-1` | `null` (v<0 branch) | yes |
| IL-static null (valid index `N`, `mStack[N]==null`) | `N≥0` | `mStack[N]` = `null` | yes |
| real object (valid index `N`, `mStack[N]==obj`) | `N≥0` | `obj` | yes |

C# `object ==` then gives Legacy-parity semantics (`Register.cs` Ceq `:4557-4603`,
Beq `:2090-2136`, Bne_Un `:2172-2220`): `null==null`→true, `obj==null`→false,
`obj1==obj2`→identity.

- **`Ceq_Ref`** (`:1973-1986`): reads `SrcOffset`/R2 (`cra`) and `OperandOffset`/R3
  (`crb`), resolves both, writes `rra == rrb ? 1 : 0` to `DstOffset`/R1. Operand
  mapping matches the plain `Ceq` arm exactly. Writes a real `int32` 0/1.
- **`Beq_Ref`** (`:2177-2190`): reads `DstOffset`/R1 (`bra`) and `SrcOffset`/R2
  (`brb`) — the same two slots the plain `Beq` arm reads — resolves both, branches
  (`ip = ptr + ip->Operand; continue;`) when equal. Parity with plain `Beq`.
- **`Bne_Un_Ref`** (`:2197-2210`): identical resolve, branches when **not** equal.
  Parity with plain `Bne_Un`.

The branch arms reuse the exact `ip = ptr + ip->Operand; continue;` branch idiom of
the plain arms — offset semantics are identical. Two real-object refs resolve through
`mStack[v]` so identity (same instance at two indices) compares equal — empirically
confirmed by TC2 (which faults without the fix precisely because the raw-index compare
mis-handles the same-instance-at-two-indices case).

### S2. JIT specialization is correct and runs after the typed rewrite [PASS]

`TypeSpecializeNeoOpcodes` (`JITCompiler.cs:902-931`, `:932-972`):

- **Ceq→Ceq_Ref**: gated on `op.Code == Ceq && (IsNeoReferenceSlot(R2) || IsNeoReferenceSlot(R3))`.
  Inserted **after** `GetTypedCompareOpcode` + `SetRegisterType(R1, IntType)`, so an
  `I8`/`R4`/`R8` primitive compare keeps its typed opcode (the rewrite only fires on a
  *plain* `Ceq` whose `InferPrimTag` fell back to `I4` because the operand is a
  reference). `IsNeoReferenceSlot` (`:1563`) = `!IsPrimitive && !IsValueType`, so a
  primitive operand never triggers it. Confirmed in the TC1 JIT dump: a plain
  `ceq r10,r10,r11` (int-vs-0) stayed unspecialized while `ldsfld; ldnull; ceq.ref`
  specialized.
- **Beq/Bne_Un → _Ref**: same shape, gated on R1/R2 reference slots, after
  `GetTypedBranchOpcode`. Correctly leaves `Blt`/`Bgt`/… untouched (ref ordering is
  invalid CIL).
- The "either operand" disjunction is the robust choice (valid IL never mixes ref/int
  in these compares, and "either" survives a single stale-type at a join).

### S3. Orthogonality to child-11 (Brtrue_Ref) [PASS — verified two ways]

1. **Ceq_Ref dest stays `IntType`** → a following `brtrue`/`brfalse` on the result
   stays a **plain** branch (not `Brtrue_Ref`). Confirmed in the JIT dumps:
   `IsInstNull` → `ceq.ref; br.s`; TC3 → `ceq.ref; brfalse.s r4`. No double-
   specialization (a `Brtrue_Ref` on the 0/1 int would wrongly dereference it as an
   mStack index). This is the D3 property, empirically held.
2. **child-11's Brtrue/Brfalse→_Ref still fires**: the Neo smoke emits many
   `brtrue.ref`/`brfalse.ref`. All child-11 canaries pass (see Gates).
3. **The Call-case stale-ref clear (child-11, `:1156-1221`) needs no extension (D5)**:
   I read the full block — it operates on the **producing Call's dest register**
   (`op.Register1`), clearing a stale reference type when the call does not return a
   reference (`retIsRef` at `:1215-1217`). It is producer-side and consumer-agnostic,
   so it already protects *every* downstream consumer — `brtrue` (child-11) **and**
   `ceq`/`beq`/`bne.un` (this child). No edit required; correct claim.

### S4. Optimizer bookkeeping is complete [PASS — with one safe non-issue]

Audited every categorization list that contains the base `Ceq`/`Beq`/`Bne_Un`. The
`_Ref` variants are present in **all** layout/categorization lists whose semantics
apply to a reference equality compare:

| list | `Ceq_Ref` | `Beq_Ref`/`Bne_Un_Ref` | correct? |
|---|---|---|---|
| `LowerNeoOffsets` R1R2R3 (`Optimizer.Neo.cs:459`) | yes | n/a (R1R2) | yes |
| `LowerNeoOffsets` R1R2 (`:655`) | n/a | yes | yes |
| `IsBranching` (`Utils:376`) | **no** (Ceq isn't a branch) | yes | yes |
| `GetOpcodeSourceRegister` (`:635`,`:664`) | yes (R2/R3 cluster) | yes (R1/R2 cluster) | yes |
| `GetOpcodeDestRegister` (`:892`,`:991`) | yes (R1 dest) | yes (no-dest cluster) | yes |
| `ReplaceOpcodeSource` (`:1217`,`:1333`) | yes | yes | yes |
| `ReplaceOpcodeDest` (`:1405`,`:1565`) | yes | yes | yes |
| `SupportIntemediateValue` (`:17`) | **no** | **no** | yes — refs aren't foldable immediates |
| `NeoNormalizeOpCode` typed→base (`:1608`+) | **no** | **no** | yes — `_Ref` is itself a base ref variant |
| `HasInverseOpcode`/`GetInverseOpcode` (`:248`) | no | no | yes — base Beq/Bne_Un absent too |
| `SupportOperandSwap` (`:237`) | **no** | **no** | **safe — see F2** |

### F1. [Minor / accepted-known] Beq_Ref / Bne_Un_Ref have zero test coverage; TC3 is non-discriminating

TC3 (`NeoStepCeqNull_TC3_BranchFormRefs`) was intended to exercise the branch form.
Inspecting its JIT dump, **Roslyn lowered all three of its comparisons to `ceq.ref` +
`brfalse`/`ceqi`**, never to `beq.ref`/`bne.un.ref`. So:

- TC3 does **not** exercise `Beq_Ref` or `Bne_Un_Ref` at all.
- TC3 is **non-discriminating**: it passes *with and without* the fix (stash-toggle:
  disable specialization → TC3 still passes; only TC1+TC2 fault). It therefore does
  not prove the fix is needed, and does not test the branch _Ref arms.

Net: only `Ceq_Ref` is exercised by the probe suite (TC1 + TC2, both load-bearing).
`Beq_Ref`/`Bne_Un_Ref` ship with **no runtime test**. The design explicitly anticipates
this (Open Question + Risk "Beq/Bne_Un may not be exercised… arms ship regardless"),
and the arms are trivially-correct symmetric siblings of `Ceq_Ref` (identical resolve
logic, byte-parity with the plain `Beq`/`Bne_Un` arms), so the residual risk is low.
Recording as Minor because it is a real coverage gap on a correctness (CORRECTNESS-tier)
change, even though accepted.

*Suggested (optional, not blocking):* a synthetic probe that emits a forced `beq`/`bne.un`
on two references whose result is consumed as a value (e.g. via a `ref-returning` helper
or a `switch`-shaped lower) would close the gap. Given Roslyn's preference for
`ceq`/`cgt.un`, this may be hard to hit from C# — the acceptance is defensible.

### F2. [Trivial / non-issue] SupportOperandSwap omits the _Ref variants

`SupportOperandSwap` (`Utils:237`) lists the base `Beq`/`Bne_Un`/`Ceq` but not the `_Ref`
variants. This is **safe by construction**: the sole caller
(`Optimizer.ELDC.cs:61-95`) gates the entire operand-swap + constant-fold path behind
`SupportIntemediateValue(Y.Code)` (line 61), which excludes the `_Ref` variants, so the
`SupportOperandSwap` branch (line 77) is **unreachable** for a `_Ref` opcode. Reference
operands are runtime mStack indices (never compile-time constants), so swap-driven
folding would never apply anyway. No action needed; recorded only for completeness.

## Spec axis — proposal/design/tasks fidelity [PASS]

- Proposal "What Changes" (3 opcodes + JIT specialize + runtime arms + bookkeeping):
  all present and as described.
- Design D1–D6 implemented verbatim. D5 (no Call-case extension) verified by reading
  the Call-case block. D3 (Ceq_Ref dest IntType → plain following branch) verified in
  JIT dumps.
- Spec delta `neo-optimizer/spec.md` scenarios: (1) ceq-form null lazy-init → true;
  (2) non-null vs null → false; (3) two-ref identity; (4) primitive unchanged;
  (5) ceq result feeding a branch stays plain. All five empirically or structurally
  confirmed (scenarios 1/3 by TC1/TC2; 4 by the unspecialized int-ceq in the TC1 dump;
  5 by `ceq.ref; brfalse.s` in TC3; 2 follows from the resolve logic + null==null).

## Probe validity [PASS for TC1/TC2; TC3 weak — see F1]

- **TC1 (null-static-ref ceq, bool-check redesign):** the redesign to a returned-`bool`
  check (`IsInstNull`) is valid and load-bearing. It tests the null *detection*
  directly — the returned bool is the `ceq.ref` result. Stash-toggle confirms it FAULTs
  (DivByZero at line 75) without the fix because `wasNull` is wrongly false. The
  implementer's note that an IL-static field *value* read-back is separately broken (the
  `TestStaticFieldInstance` downstream `InvalidCastException`) is corroborated (see
  below) and is genuinely orthogonal — the bool-check sidesteps the value read-back by
  testing the comparison result, not the field value. Not a spurious pass.
- **TC2 (two-ref identity):** load-bearing. Stash-toggle confirms it FAULTs without the
  fix (line 99) — the same instance reaches the helper at two distinct mStack indices
  (arg passing), so the raw-int compare mis-handles identity; only the resolved
  `R(a)==R(b)` gives the right answer. This proves the fix is needed beyond the null
  case.
- **TC3:** see F1 — non-discriminating; neither exercises Beq_Ref/Bne_Un_Ref nor faults
  without the fix.

## Stash-toggle evidence [PASS]

Disabled both JIT specialization blocks (`if (false && …)` on `Ceq→Ceq_Ref` and
`Beq/Bne_Un→_Ref`), rebuilt `Debug_Neo --no-incremental` (0 errors), ran the 3 probes:

- **TC1 FAULT** — `DivideByZeroException` at `NeoStepCeqNullSentinelTest.cs:75`
  (`!wasNull` guard fires: raw-int `ceq` of index-N vs `ldnull`-(-1) → "not equal" →
  `x==null` wrongly false → null mis-detected).
- **TC2 FAULT** — `DivideByZeroException` at `:99` (`!sameAB` guard fires: same-instance
  at two indices mis-compares by raw index).
- TC3 passes (non-discriminating).
- Result: `Ran 3 tests, 2 failed`.

Restored the blocks (verified no residual `false &&`, diff vs `bb412dac` back to 140
pure additions), rebuilt, re-ran: `Ran 3 tests, 0 failed`. Round-trip clean.

## Gates [ALL PASS]

- Build CLI `Debug_Neo --no-incremental`: **0 errors**.
- Build TestCases plain `Debug`: **0 errors**.
- **NeoStep smoke (`Debug_Neo`, `true NeoStep`): `Ran 335 tests, 0 failed`** (332 + 3
  probes). All child-11 canaries pass as a subset: `NeoStepOrChain_*` (incl.
  `FirstCompareTrue`, `SecondCompareTrue`), `NeoStep16_TC10`, `NeoStep20_Tr2`,
  `NeoStep20_Tr5` — none in the failure list; `brtrue.ref`/`brfalse.ref` still emit.
  Orthogonality confirmed.
- **Legacy-neutral (plain `Debug`, `true NeoStep`): `Ran 335 tests, 17 failed`** ==
  implementer-reported baseline. The 17 are pre-existing Legacy failures unrelated to
  this change (e.g. `NeoNaNR8` NaN-compare semantics). The 3 new probes are **absent**
  from the Legacy failure list → they pass under `ExecuteR` (Legacy handles reference
  compares via `reg->ObjectType`). All edits are `#if ENABLE_NEO_MODE`-gated →
  Legacy-neutral by construction; confirmed empirically.
- **Bonus corroboration — `TestStaticFieldInstance` progressed**: under Neo it now emits
  `ceq.ref` and fails with `InvalidCastException: … ILTypeInstance → System.String`
  (the IL-static-ref-field value read-back gap), **not** the previous NRE. The ceq
  null-sentinel NRE is gone; the failure moved downstream to the out-of-scope gap.
  Confirms the fix is load-bearing for the originally-reported defect.

## Scope check [CLEAN]

Intent: fix the Neo ceq/beq/bne.un null-sentinel mis-compare (sibling of child-11).
Delivered: exactly that — 3 opcodes, JIT specialize, 3 runtime arms, full bookkeeping,
3 probes. No scope creep; no unrelated edits (the `Dependencies/*.pdb` noise is
pre-existing binary drift). Neo-gated, Legacy-neutral, no public API change.

---

## Verdict: APPROVE-WITH-FINDINGS

The change is **correct** (3 deref arms verified against all null encodings + identity;
Legacy byte-parity), **orthogonal** to child-11 (Ceq_Ref dest stays IntType → plain
following branch; Brtrue_Ref still fires; Call-case clear is producer-side general),
**fully bookkept** (every applicable list; the two deliberate omissions are safe), and
**empirically validated** (NeoStep 335/0; stash-toggle proves TC1+TC2 load-bearing;
Legacy 335/17 == baseline; `TestStaticFieldInstance` ceq-NRE gone).

The single substantive finding (**F1, Minor**) is the zero test coverage of the
`Beq_Ref`/`Bne_Un_Ref` runtime arms — TC3 does not exercise them and does not fault
without the fix. This is explicitly accepted in the design (the arms are symmetric
siblings of the tested `Ceq_Ref` with identical resolve logic), so it does not block
shipping. APPROVE-WITH-FINDINGS rather than clean APPROVE only because this is a
CORRECTNESS-tier change and the coverage gap is real; the LEAD may optionally request a
synthetic `beq`/`bne.un`-forcing probe, or accept the documented risk (consistent with
the established Neo "ship the symmetric arm, test the reachable one" precedent).

No Blocker or Major findings.
