# Review Report — neo-brtrue-on-reference

**Reviewer:** independent (author != verifier), dispatched report-only leaf
**Date:** 2026-07-12
**Branch:** `features/object-model-overhaul` (changes uncommitted in working tree: 5 modified files + 1 new test)
**Change kind:** CORRECTNESS — Neo `Brtrue`/`Brfalse` null-sentinel for reference conditions + coupled F3 (IL-static `Stsfld`/`Ldsfld` raw-`DstOffset`) + a load-bearing Call-case stale-ref-type clear.

## Verdict: **APPROVE**

All five correctness claims were independently re-verified, both load-bearing pieces (the F4 Brtrue_Ref rewrite and the Call-case stale-ref clear) were **toggle-proven** necessary, the F4+F3 coupling was **empirically confirmed**, the gate is green (332/0), and the result is Legacy-neutral. Findings below are all Minor/informational and already candidly disclosed in the design's Risks section; none block shipping.

---

## Scope Check: CLEAN
- **Intent (proposal):** type-specialize Neo `Brtrue`/`Brfalse` to test a reference condition by the referenced object's nullness (`mStack[idx] != null`) instead of the raw mStack-index int32; seed `Ldsfld` dest type; fix the coupled F3 IL-static offset gap in the same child.
- **Delivered:** exactly that, plus a necessary regression repair (Call-case stale-ref clear) the proposal's D2 "verify" step anticipated but the planner did not pre-specify — properly recorded in the tasks completion summary.
- **No scope creep.** All edits are `#if ENABLE_NEO_MODE`-gated. 2 new NeoStep probes added (required by the workflow).

## Spec axis: PASS
The `specs/neo-optimizer/spec.md` ADDED requirement (reference branch tests `mStack[idx] != null`; ceq stays plain; IL-static `Stsfld`/`Ldsfld` resolve via LocalInfos) is implemented verbatim. All 5 scenarios map to verified behavior (see below).

---

## Independent re-verification of each claim

### 1. Deref arm — CORRECT
`ILIntepreter.Neo.cs` Brtrue_Ref/Brfalse_Ref arms:
```csharp
int idx = *(int*)(frameBase + ip->DstOffset);
// Brtrue_Ref:  branch when  (idx >= 0 && mStack[idx] != null)
// Brfalse_Ref: branch when !(idx >= 0 && mStack[idx] != null)
```
All three null encodings verified against the producers:
- `-1` sentinel (`Ldnull` `Neo.cs:1526`; CLR-static `Ldsfld`) → `idx >= 0` false → **falsey**. OK.
- IL-static null (`Ldsfld` `Neo.cs:4346-4353`: `mStack.Add(null)`; stores non-zero index) → `idx >= 0` true, `mStack[idx] == null` → **falsey**. OK.
- real object (valid index, non-null entry) → **truthy**. OK. Legacy-parity with `Register.cs:2053` `mStack[reg1->Value] != null`.
- `ip->DstOffset` is a real byte offset because `Brtrue`/`Brfalse` (and the new `_Ref` variants) ARE lowered by `LowerNeoOffsets` (confirmed: `Optimizer.Neo.cs:698-709` case-list extended).
- **Live in the smoke:** 34 specializations emitted (12 `brtrue.ref` + 22 `brfalse.ref`); TC1 body shows `0:ldsfld r2,...; 1:brtrue.ref` — the exact direct-form pattern, correctly specialized and resolved (TC1 passes).

### 2. Call-case stale-ref clear (the LOAD-BEARING regression repair) — CORRECT and PROVEN NECESSARY
`JITCompiler.cs:1158-1177`, an `else if (IsNeoReferenceSlot(cur))` arm in the Call/Callvirt case. When the dest register held a reference and is overwritten by a call whose return is NOT a reference, the stale ref type is cleared (`SetRegisterType(..., rt2.IsPrimitive ? rt2 : null)`); byref returns are treated as non-reference; a reference-returning call leaves the ref type in place (no over-clear).

**Toggle proof (author != verifier):** I neutralized this one arm (`else if (false && IsNeoReferenceSlot(cur))`), rebuilt, and re-ran the full NeoStep smoke:
- Result: **332 ran, 2 failed** — exactly `NeoStepOrChain_FirstCompareTrue` and `NeoStep16_TC10_GenericTokenLdelemStelem`, both `DivideByZeroException` (the deliberate `1/0` assertion guard). This reproduces the implementer's claimed regression precisely.
- Root cause confirmed in the OrChain Final body: `7:ldfld.ref.inline r6` (r6 = string ref) → `9:call r6,...,op_Inequality` (r6 reused for the **bool** result) → `10:brtrue.s r6` stays **plain** *only because* the clear reset r6's type. With the clear off, r6 keeps the stale ref type → `brtrue.ref` mis-fires → dereferences the bool (0/1) as an mStack index → wrong branch → `div`-by-zero. With the clear on (shipped state): **0** `.ref` branches in the OrChain body.
- **Does not over-clear:** a reference-returning call (`retIsRef == true`) skips the clear, so the dest stays a reference and a following `brtrue` correctly specializes to `brtrue.ref`. Verified by static reading of the `if (!retIsRef)` guard.
- (JITCompiler.cs was backed up before the toggle and restored bit-for-bit afterward; zero residue.)

### 3. Ldsfld registerTypes seeding — CORRECT
`JITCompiler.cs:1243-1258`. IL path: `ilt.StaticFieldTypes[sIdx]` (IType directly, bounds-guarded). CLR path: `ct.GetField(sIdx).FieldType` is a `System.Type` (confirmed `CLRType.GetField(int)` returns `System.Reflection.FieldInfo`), resolved via `AppDomain.GetType(Type)` (overload exists at `AppDomain.cs:1801`).
- Reference static field → seeds a reference type → `IsNeoReferenceSlot` true → rewrite fires (TC1 proves it).
- Primitive static field → seeds a primitive → `IsNeoReferenceSlot` false → Brtrue stays plain. **No mis-seed.**
- Value-type static field → `IsNeoReferenceSlot` false → stays plain. Correct (a VT static is not an mStack index).
- Out-of-range `sIdx` / null `StaticFieldTypes` / null `GetField` → `ft` stays null → no rewrite. Safe.

### 4. Coupling F4+F3 (the only correct state) — EMPIRICALLY CONFIRMED
The planner's D5 called the coupling symmetric; the implementer empirically refined it to **asymmetric** (F3-alone is the trigger, F4 is the fix). I independently reproduced the load-bearing direction:
- **F3-alone toggle:** I neutralized only the Brtrue/Brfalse→`_Ref` rewrite (`if (false && IsNeoReferenceSlot(...))`), keeping F3 (the IL-static offset fix) and the deref arm active. Rebuilt, re-ran NeoStep smoke → **332 ran, 3 failed**: `NeoStep20_Tr2_ActionSideEffect`, `NeoStep20_Tr5_LambdaCallsILMethod`, `NeoStepBrtrueRef_TC1_DelegateCacheLazyInit` — all delegate-cache patterns failing with `ArgumentNullException`/`NullReferenceException` (null delegate invoked). This is exactly the implementer's refined claim: correct `Ldsfld` offset + plain `Brtrue` reads the real non-zero cache index N as truthy → skips init → null delegate → throw.
- **F4+F3 (shipped):** 332/0, Tr2/Tr5/TC1 pass.
- Hence F3 cannot ship without F4 and vice-versa; the combined state is the only correct one. Confirmed.

### 5. F3 (IL-static Stsfld/Ldsfld raw DstOffset) — CORRECT
Both IL-static arms now resolve `int off = (localInfos != null && regIdx < localInfos.Length) ? localInfos[regIdx].Offset : regIdx;` (Stsfld read `Neo.cs:4166-4176`; Ldsfld write `Neo.cs:4316-4325`). This mirrors the proven CLR-static arm byte-for-byte (`Neo.cs:4234-4236`, same defensive fallback). The fallback to the raw index on null/out-of-range `localInfos` is parity with the CLR arm (acceptable; `localInfos` is always non-null in Neo). Stale "CLR-static only" comment trimmed.

---

## Gates re-run (this review, `-f net8.0`)

| Gate | Command | Result |
|---|---|---|
| Build CLI | `dotnet build ILRuntimeTestCLI -c Debug_Neo --no-incremental` | **0 errors** |
| Build TestCases | `dotnet build TestCases -c Debug` | **0 errors** |
| **FIXED Neo smoke** (`... true NeoStep`) | full NeoStep filter | **332 ran / 0 failed** |
| Tr2 / Tr5 | (within 332/0) | **PASS** |
| OrChain_FirstCompareTrue | (within 332/0) | **PASS** |
| NeoStep16_TC10 | (within 332/0) | **PASS** |
| TC1 / TC2 (new probes) | (within 332/0) | **PASS** |
| Toggle: Call-clear OFF | full NeoStep filter | **2 failed** (OrChain + TC10, div-by-zero) — repair proven load-bearing |
| Toggle: Brtrue→_Ref rewrite OFF (F3-alone) | full NeoStep filter | **3 failed** (Tr2 + Tr5 + TC1, null-delegate ANE/NRE) — coupling confirmed |
| **Legacy-neutral** (plain `Debug`, `... true NeoStep`) | full NeoStep filter | **332 ran / 17 failed == baseline**; TC1/TC2 PASS under Legacy |

Baseline matches the review prompt (332 ran / 17 failed). The 2 new probes pass under both Neo and Legacy.

---

## Legacy-neutral: CONFIRMED
- Enum members appended (implicit-numbered; no value shift). `Optimizer.Utils.cs` 5 case-lists (`IsBranching`, `GetOpcodeSourceRegister`, `GetOpcodeDestRegister`, `ReplaceOpcodeSource`, `ReplaceOpcodeDest`) + `Optimizer.Neo.cs:LowerNeoOffsets` all extended with the `_Ref` variants alongside their plain siblings — the standard harmless-enum-addition pattern.
- All logic is `#if ENABLE_NEO_MODE`-gated. Plain-`Debug` NeoStep failure set == 17-failure baseline (no regression). New probes pass under Legacy.

## Scope assessment: WORTH SHIPPING
This child fixes the **direct** `ldsfld ref; brtrue skipInit` lowering (Roslyn's null-test optimization) — the delegate-cache / `if(x==null){init}` pattern. That is a real, live, pervasive correctness gap, and the fix is proven correct + necessary (toggle evidence). It also unblocks and ships the coupled F3 fix.

The **ceq form** (`ld x; ldnull; ceq; brfalse`) is a **clean, distinct, correctly-out-of-scope follow-up**. Independently confirmed on FIXED Neo: `TestStaticFieldInstance` still NREs (`NullReferenceException`); its body is `ldsfld; ldnull; ceq; brfalse.s` — the `brfalse.s` correctly stays plain (D3; `ceq` seeds IntType), but the `ceq` runtime compares the raw mStack index N vs `ldnull`'s -1 as int32 → never equal → "instance != null" → init skipped → NRE. This child's direct-form fix does not touch `ceq`, so leaving it open is correct. Recommended as a separate child (the runtime `ceq`/`beq` ref-vs-ldnull-sentinel comparison).

A second OPEN item (also correctly out of scope): an IL-static int/ref field write+read **round-trip** returns garbage on BOTH HEAD and FIXED — a deeper field-storage gap distinct from the F3 operand-offset fix (F3 itself is correct). Not a finding against this child.

---

## Findings by severity

### Blocker
- None.

### Major
- None.

### Minor (informational; all already disclosed in design Risks / tasks OPEN)
- **M1 — `mStack[idx]` unguarded in the deref arm.** No bounds check (Legacy parity; the index is producer-valid). Residual risk: if the `registerTypes` dataflow ever mis-classifies an int-typed `Brtrue` operand to `Brtrue_Ref` (block-join staleness), the arm would do `mStack[garbageInt]` → OOB throw rather than a wrong-but-bounded result. The single-linear-pass limitation is acknowledged in design D-risk; the 332 smoke + the toggle evidence are the safety net. Acceptable for now; flag for a future dataflow-precision pass if brtrue-related OOBs surface.
- **M2 — The 2 probes pass on HEAD too.** Candidly documented in the test header and tasks summary: the Neo frame is zero-initialized, so the delegate-cache check register reads 0 (falsey) on HEAD → init runs by accident → TC1/TC2 pass without the fix. The probes are therefore **regression guards** (they'd catch a future break of the direct-form path), not HEAD-fault evidence. The load-bearing fault evidence is the F3-alone NRE (Tr2/Tr5/TC1) and the Call-clear toggle (OrChain/TC10) — both independently reproduced in this review. Not a defect; recording so the LEAD does not mistake the probes for fault-proof.
- **M3 — `cm2 == null` (unresolvable call target) defaults to clearing the stale ref type.** If such a call actually returned a reference, the dest would drop to untyped and a following `brtrue` would stay plain (re-introducing the original null-is-truthy bug for that case). Edge case; conservative vs. the regression. Acceptable; note for the future dataflow pass.

### Trivial
- T1 — Comment typos "Ldsfeld"/"Stsfeld" (should be `Ldsfld`/`Stsfld`) scattered through the new comments. Cosmetic.
- T2 — The defensive `localInfos` fallback returns to the old buggy raw-index-as-offset behavior on a null/out-of-range array; parity with the CLR-static arm, so acceptable, but a debug assert there would catch a future invariant violation sooner.

---

## Files reviewed (all absolute)
- `ILRuntime/Runtime/Intepreter/OpCodes/OpCodeREnum.cs` — `Brtrue_Ref`/`Brfalse_Ref` appended (implicit-numbered).
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` — `TypeSpecializeNeoOpcodes`: Ldsfld dest-type seed (1243-1258), Brtrue/Brfalse→`_Ref` rewrite (1271-1282), Call/Callvirt stale-ref clear (1158-1177). `IsNeoReferenceSlot` (1522), `Get/SetRegisterType` (1509/1516).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` — `LowerNeoOffsets` Brtrue case-list (698-709).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Utils.cs` — 5 case-lists extended (IsBranching 351-, GetOpcodeSourceRegister 591-, GetOpcodeDestRegister 865-, ReplaceOpcodeSource 1282-, ReplaceOpcodeDest 1527-).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` — Brtrue_Ref/Brfalse_Ref deref arms (2077-2110); F3 IL-static Stsfld (4166-4176) + Ldsfld (4316-4325) offset resolution; IL-static Ldsfeld null-encoding producer (4346-4353).
- `TestCases/NeoStepBrtrueRefTest.cs` — 2 probes (delegate cache; non-null coalesce).

## Rationale
A delicate change (new opcodes + JIT type-specialization + a load-bearing regression repair + an ABI-adjacent offset fix) that I verified end-to-end: static correctness of all four mechanisms, two independent toggle experiments proving both F4 and the Call-clear are load-bearing, empirical confirmation of the F4+F3 coupling, a green 332/0 gate with all named tests passing, confirmed Legacy-neutrality, and a correctly-bounded scope with the ceq form verified as a clean separate follow-up. No Blocker or Major findings. **APPROVE.**
