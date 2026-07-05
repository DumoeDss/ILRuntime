## 1. JIT-dump probes (DUMP-GATE before any code change)

- [x] 1.1 Build CLI + TestCases -- confirm 0 errors.
- [x] 1.2 Run the NeoStep smoke baseline -- confirm 117/117 green.
- [x] 1.3 JIT-dump constrained.callvirt on a struct override: confirm `Operand4 & 0x1` is set; determine whether the constrained type token is recoverable on the callvirt (OQ1). LOCKED: Option F' (Constrained arm owns the dispatch; reads the type token from `ip->Operand` and the trailing callvirt at `ip+1` -- NO new operand stamping, NO collision). See design.md D1 resolution.
- [x] 1.4 JIT-dump CLR-struct constrained.callvirt: the boxed `this` BYPASSES `CopyNeoCallArguments` (the Constrained arm writes the boxed mStack index directly into the callee slot). LOCKED: D5 = NIE-guard (no silent mis-copy).
- [x] 1.5 JIT-dump CLR-array ldelema -> stind/ldind: the existing `objectIndex >= 0` arm CANNOT address a CLR array element (it calls GetNeoILInstance). LOCKED: D4 = add a `mStack[objIdx] is Array` branch in Stind_I4/Ldind_I4 (the green target), NIE-tag the rest. Ldelema encodes `(arrIdx, elementIdx)`.
- [x] 1.6 Dump findings LOCKED in review-report.md: D1=F', D4=green-target (Stind_I4/Ldind_I4), D5=NIE-guard.

## 2. (a) constrained.-on-VT dispatch -- JIT fusion (D1)

- [x] 2.1 Option F' (NOT F): no JIT change required. The JIT's existing Constrained re-append produces `[Push..., Constrained, Callvirt]` (Constrained BEFORE the callvirt -- verified by JIT body dump). The runtime Constrained arm reads the type token (`ip->Operand`) + the trailing callvirt (`ip+1`) and owns the dispatch; the trailing callvirt is skipped (`ip += 2`). NO operand stamping => NO collision risk (the existing `Operand4` flag semantics are byte-identical for non-constrained callvirts).
- [x] 2.2 Option R not required (F' supersedes it: no two-phase dance; the Constrained arm runs first and dispatches atomically).
- [x] 2.3 No `Operand4` regression: F' does NOT stamp any new operand. The constrained flag (`0x1`) + slot bits + `thisArgOffset<<16` are untouched. Non-constrained Callvirt_IL/Callvirt_CLR/Callvirt_Interface/Callvirt paths are byte-identical (the Constrained arm only fires for `OpCodeREnum.Constrained`).

## 3. (a) constrained.-on-VT dispatch -- runtime box-once (CLR value type / override-of-Object)

- [x] 3.1 Replaced the Constrained NIE with the dispatch.
- [x] 3.2 Box-once: read the byref `this` (8-byte Ref Slot, `(-1, structByteOff)` from ldloca/ldarga); box the struct once (CLR primitive -> NeoBoxReturnValue; CLR struct -> ReadNeoValueType; the box happens exactly once per dispatch). Already-boxed / ref-type `this` (objIdx>=0) -> no-op box.
- [x] 3.3 Dispatch via `GetVirtualMethod` on the constrained type (resolves the concrete override), then `InvokeNeoClrMethod` / `InvokeNeoCallTarget` on the boxed receiver parked on mStack. Box happens exactly once (no per-virtual-dispatch re-box).
- [x] 3.4 `GetVirtualMethod` on the constrained ILType resolves the constrained TYPE's concrete override (not the static call-site type's).

## 4. (a) constrained.-on-VT dispatch -- runtime direct-call (IL value-type interface impl)

- [x] 4.1 IL value type + ILMethod override -> direct-call: deref the byref, copy the struct's flat primitive bytes into the callee slot-0 frame region (`targetBase + ParamInfos[0].Offset`), ExecuteNeo. The override reads `this.field` via in-frame Ldfld_Inline (no box). REUSES area4's flat-bytes-`this` shape (the Constrained arm is a new caller, not a parallel implementation).
- [x] 4.2 Verified: the constrained IL-VT interface probe reads the field correctly via Ldfld_Inline (the same path `local.VTMethod()` uses).

## 5. (d) CLR primitive-array ldelema (D-LDELEMA remainder)

- [x] 5.1 Ldelema `else` branch: encode `(arrIdx, elementIdx)` for a CLR value-type-element array (the `off` half IS the element index); NIE a reference-type-element array.
- [x] 5.2 Added `mStack[objIdx] is Array` branches to Stind_I4 (`cArr.SetValue(v, off)`) and Ldind_I4 (`(int)cArr.GetValue(off)`) -- the green target. Other stind/ldind variants on CLR arrays remain NIE-tagged in their existing arms.

## 6. (M2 obligation) F-5 / NEO-CALLARG-BOXED-SRC closure (D5)

- [x] 6.1 CopyNeoCallArguments boxed-source branch (`PrimitiveByRefSrc` with `objIdx >= 0`): replaced the defensive `CopyBlock` with a NIE-guard (the constrained box-once BYPASSES CopyNeoCallArguments; the `offset` for a boxed source would be an mStack FIELD offset, NOT a struct address -> a CopyBlock would silently mis-copy). NO silent mis-copy.
- [x] 6.2 CopyNeoCallThisBack comment tightened: "MUTATING INSTANCE METHODS, NOT constructors -- the newobj path (VT-THIS-ADDR) performs its own slot-0 -> caller-dest copy-back in ExecuteNeo's Ret arm and does NOT invoke this."
- [x] 6.3 Stale Constrained NIE text (T1) replaced with an accurate description of the new arm.

## 7. Adversarial probes (MANDATORY) + smoke regression

- [x] 7.1 Added 6 `NeoStep17_*` probes:
  - `NeoStep17_ConstrainedIlVtDirectCall` (IL VT interface impl, direct-call path).
  - `NeoStep17_ConstrainedClrPrimitiveToString` (int.ToString box-once).
  - `NeoStep17_ConstrainedClrStructToString` (CLR struct override box-once).
  - `NeoStep17_ConstrainedOverrideResolvesConstrainedType` (override-of-Object resolution).
  - `NeoStep17_ClrPrimitiveArrayLdelema_StindLdind` (int[] ldelema round-trip).
  - `NeoStep17_AddrAliasRegisterReuseRegression` (Step-17-B1 register-reuse probe).
  - NOTE: IL-struct ToString/GetHashCode overrides that take a field ADDRESS (`ldflda` on the in-frame VT `this`) hit a PRE-EXISTING `ldflda`-on-in-frame-VT gap (out of scope; see review-report + Findings). The constrained DISPATCH is validated via the interface direct-call probe.
- [x] 7.2 Stash-toggle proof: the constrained probes threw the HEAD Constrained NIE during development (observed: "Step 17: constrained.callvirt on a value type is deferred"); the CLR-array probe threw the HEAD Ldelema NIE. With the changes applied, all 6 pass.
- [x] 7.3 FULL NeoStep smoke: 123/123 green (117 baseline + 6 new probes).
- [x] 7.4 NeoOptHardening smoke: 16/16 green (no optimizer-gate regression).
- [x] 7.5 Legacy-neutral: plain `Debug` CLI builds clean (all runtime edits Neo-only); the constrained probes pass on Legacy (plain Debug + useRegister=true) -- Legacy's Constrained arm is the REFERENCE. The 7 pre-existing Legacy NeoStep failures (NeoTestClrStruct..., NeoStep14 TC1/TC5/TC8, NeoStep15 TC6, NeoNaNR8) reproduce identically (NOT caused by this change).

## 8. Ship + archive + portfolio maintenance

- [ ] 8.1 review-report.md written (this file, apply-phase findings). [verify phase]
- [ ] 8.2 ship-log.md (after review clean). [ship phase]
- [ ] 8.3 neo-deferred-items.md update (the shipper does at archive).
- [ ] 8.4 neo-handoff.md update (the shipper does at archive).
- [ ] 8.5 spec delta sync + archive (archive phase).
- [x] 8.6 APPEND durable findings to planning-context.md (this apply phase).
