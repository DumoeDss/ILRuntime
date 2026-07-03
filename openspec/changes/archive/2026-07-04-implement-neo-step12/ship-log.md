# Ship Log -- implement-neo-step12

**Change:** `implement-neo-step12` -- Neo Step 12: in-frame value-type storage
(natural alignment) + inline field-access opcodes (`Ldfld_*_Inline` /
`Stfld_*_Inline`) + memset-0 `Initobj` for in-frame VTs + JIT type-based
lowering that selects the inline variant.

**Date:** 2026-07-04
**Branch:** `features/object-model-overhaul`

---

## Ship verdict: CLEAN

No open Blocker or Major findings. All apply-stage tasks complete (28/28);
review-loop converged (3 rounds); accepted-known residuals are Minor and
scoped to Step 12b. Land the change.

---

## Verification evidence

### Builds (0 errors)
- CLI (Neo config):
  `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`
  -> 0 errors (transitively builds ILRuntime / ILRuntimeTestBase / LitJson).
- TestCases (Legacy config; NEVER Debug_Neo for TestCases):
  `dotnet build TestCases/TestCases.csproj -c Debug`
  -> 0 errors -> `TestCases/bin/Debug/netstandard2.1/TestCases.dll`.

### Smoke
- FULL `NeoStep` smoke (regression gate; value types are pervasive so a
  full smoke was the gate, not just the new Step-12 filter):
  `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
     TestCases/bin/Debug/netstandard2.1/TestCases.dll \
     HotfixAOT/Patched/HotfixAOT.patch true NeoStep`
  -> **37 ran / 0 failed / 0 ignored**, including the +5 new NeoStep12
  cases. No regression: all pre-existing NeoStep6-11 cases (which use VT
  locals/params/fields) stayed green.
- New Step-12 filter (`NeoStep12`) alone: 5 ran / 0 failed.

### Regression-gate note
Step 12 changes VT storage/alignment/field-access, which is high-regression
surface (VT locals/params/fields pervade the existing NeoStep smoke). The
full NeoStep smoke staying green across NeoStep6-11 is the strongest
regression signal and it held. The heap-path-no-regression guarantee was
verified empirically: `NeoStep12TestHeapObjectFieldAccessUnchanged` JIT dump
emits `stfld.i4`/`ldfld.i4` (NOT `_Inline`) for a heap-class field, proving
the operand-value-category discriminator leaves heap objects on the heap path.

---

## Review summary

**Severity counts:** Blocker 0 / Major 0 / Minor 5 / Trivial 1.
Full detail in `review-report.md`.

**Review-loop outcome (3 rounds):**
- Minor 2 (alignment-8 VT test) -- KEPT (added `NeoStep12TestLongFieldStruct`,
  probes NaturalAlignment==8 and the `*(long*)`/`*(double*)` arms).
- Minor 3 (Ldflda VT-type propagation) -- KEPT (added a `case Ldflda:` in
  `TypeSpecializeNeoOpcodes` so opcode selection is self-contained rather
  than dependent on a JIT temp-reuse invariant).
- Minor 1 (escaped-address guard) -- REVERTED in round 3. The attempted guard
  was a compound defect: a dead `Register1`/`DstOffset`-union comparison
  (the operands being compared are never populated together for the relevant
  opcodes) plus an over-broad detector that needed a liveness pass to avoid
  false positives. Reverted as worse than no guard. Non-author re-review
  confirmed clean revert and smoke 37/37. The proper home for this is the
  Step 12b byref-pointer model (recorded below as accepted-known).

---

## Accepted-known (carry-forward)

- **escaped-vt-address (Minor):** An escaped VT address -- a `ldloca`/`ldflda`
  dest consumed by an implemented non-field opcode (e.g. `Move` reading it as
  a value) -- is not detected and could silently corrupt. Common escapes
  (`ldind_*`/`stind_*`, `fixed`, byref `Call`) are unimplemented Step-6/13
  opcodes that throw their own `NotImplementedException`, so they are already
  loud; the residual silent case is rare and not triggered by any current
  test. A liveness-aware escape guard / a real byref-pointer model belongs in
  **Step 12b**. (A dead/over-broad guard was attempted in review-loop rounds
  1-2 and reverted as worse than none.)
- **Minor 4:** `addrAlias` is a single-forward-pass over the linearized body;
  cross-block register reuse could alias-stamp a register for the wrong block.
  Matches the existing linear-slot model; flag for 12b.
- **Minor 5:** `NaturalAlignment` is computed for classes too (dead path,
  harmless; never read by the JIT for class slots).
- **Trivial 1:** `OpCodeREnum` ordering -- the 22 new opcodes are inserted
  between `Ldfld_Value` and the Step-6 arithmetic block rather than adjacent
  to their heap siblings. Cosmetic (the VM switches on enum name); no
  behavioral meaning.

---

## Files changed

### Runtime (`ILRuntime/`)
- `CLR/TypeSystem/ILType.cs` -- `NaturalAlignment` (cached, NEO-gated;
  computed in `InitializeFields`; recurses into nested VT, enum-via-backing-
  field, ref->pointer-size; floor 1; excludes static fields).
- `Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- `AlignUp` helper; applied
  at every slot-offset site (HasThis VT, VT local, CLR-VT-as-ref local,
  reference local, primitive local, `StackRegisterCount` temp loop with
  `maxAlignment`, `AllocateSlotForType`); operand-`IType` threading into
  `Code.Ldfld`/`Code.Stfld`; `TryRewriteFieldAccessForInline` discriminator
  (`operandType is ILType && IsValueType && !IsEnum`); inline code selectors;
  `Ldflda` dest type propagation in `TypeSpecializeNeoOpcodes`.
- `Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` -- `addrAlias` ldloca/
  ldflda pre-scan + `ResolveAddressAlias`; `_Inline` Ref-variant absolute
  frame-ref-index stamping; `Operand3` RefOffset stamping for `Initobj`;
  registration of the 22 new opcodes in BCP/FCP source/dest helpers;
  alignment in `AllocateNeoCallParamSlot`.
- `Runtime/Intepreter/RegisterVM/Optimizer.Utils.cs` -- supporting utils for
  the new opcode registrations.
- `Runtime/Intepreter/OpCodes/OpCodeREnum.cs` -- 22 new opcodes
  (`Ldfld_{I1,I2,I4,I8,U1,U2,U4,U8,R4,R8,Ref}_Inline` and `Stfld_..._Inline`).
- `Runtime/Intepreter/OpCodes/OpCode.cs` -- operand metadata for the new
  opcodes.
- `Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- 22 `_Inline`
  `ExecuteNeo` arms (pure pointer arithmetic on `frameBase` for primitives;
  mStack-index copy for the Ref variant); `Initobj` in-frame-VT ref-field
  sub-case (nulls `mStack[frameRefBase + Operand3 + i]`); CLR-VT `Initobj`
  branch left as the Step-13 throw.

### Tests (`TestCases/`)
- `NeoStep12Test.cs` (new) -- 5 cases: Vector3 field read/write/sum; nested
  value type (`struct Outer { Inner i; int y; }`); VT with a reference field
  (incl. null round-trip); `Initobj`-zeros-VT; heap-path-unchanged (asserts
  the heap variant is emitted for a heap-class field). Plus the review-loop
  additions: alignment-8 `long`-field struct.

---

## Git status

All runtime + test changes are **uncommitted** on `features/object-model-
overhaul`. The LEAD commits after this ship + archive step (one clean commit
capturing code + test + synced spec + archived change). The shipper did not
edit source code in this stage.

---

## Notes

- The Rails/JS-centric `openspec-gstack-ship` workflow (`bin/test-lane`,
  `VERSION`, `CHANGELOG`, Greptile, `gh pr create`) does not apply to this C#
  ILRuntime repo and was deliberately NOT invoked. No remote PR.
- Scope discipline held: no `Move_Vt`/whole-VT copy (Step 12b), no CLR VT
  Box/Unbox/`Initobj` (Step 13, still throws "Step 13" at
  `ILIntepreter.Neo.cs:1570`; CLR Box still throws at 1627), no
  `constrained.` handling (Step 18), no IL VT `newobj`. `Ldfld_Value`/
  `Stfld_Value` correctly NOT given inline variants (12b). All new code is
  Neo-path only (`#if ENABLE_NEO_MODE`); Legacy untouched.
