# Tasks -- neo-step17-generic-byref-etc

Implementation checklist. Dump-gate verdicts (propose phase):
- generic-byref: NO-OP (3/3 probes PASS on HEAD).
- `fixed`: DEFER (blocked by `Conv_U`, out of scope).
- interface-on-VT-constrained: NO-OP (2/2 probes PASS on HEAD).
- F-10-R1: REAL GAP (NRE on HEAD; fix = JIT-discriminator gate).

## 1. Engine: F-10-R1 JIT-discriminator gate (the ONLY engine change)

- [x] 1.1 In `JITCompiler.cs TypeSpecializeNeoOpcodes case OpCodeREnum.Ldflda:`
      (~line 862), inside the `if (srcType is ILType srcIl && srcIl.IsValueType
      && !srcIl.IsEnum)` block (the F-6 stamping site), add
      `op.Operand4 &= ~NeoLdfldaClrStructFieldMarker;` (clear F-10 when F-6
      stamps). Add the explanatory comment from design.md. Neo-only (the pass is
      `#if ENABLE_NEO_MODE`).
- [x] 1.2 Verify via JIT dump (temp `Console.WriteLine` inside the type-spec
      `case Ldflda:` arm, OR a `strings -e l` on a stamped literal) that the
      both-stamp `Operand4 = 0x3` becomes `0x1` for the F-10-R1 probe's
      `ldflda this.field` site. Remove the temp probe.
- [x] 1.3 Confirm the Stfld_Ref / Ldfld_Ref F-10 hash is NOT consumed for an
      in-frame-VT operand (the `_Inline` rewrite diverts it). JIT-dump the F-10-R1
      probe's `SetAndSumViaLdflda` body; confirm no `Stfld_Ref`/`Ldfld_Ref`
      (non-`_Inline`) on `this.field`. If a path IS found that reads the F-10
      hash for an in-frame VT (not expected), extend the gate to clear it on the
      `_Inline` rewrite.

## 2. Tests: graduate the probe keepers (TestCases/NeoStep17Test.cs)

- [x] 2.1 Rename the 3 generic-byref probes off the `_Probe_` prefix to their
      keeper names: `NeoStep17_GenericByRef_SwapInt`, `_SwapIlRef`, `_SwapIlVt`.
      Confirm each PASSES on HEAD unmodified (TEST-ONLY; mirrors `neo-k2fam-
      bridge`).
- [x] 2.2 Rename the 2 interface-on-VT probes: `NeoStep17_InterfaceOnIlVtConstrained`,
      `_InterfaceOnClrVtConstrained`. Confirm each PASSES on HEAD unmodified.
- [x] 2.3 Rename the F-10-R1 probe to `NeoStep17_F10R1_ConstrainedVtLdfldaClrField`.
      Confirm it FAILS on HEAD (NRE at `NeoMarshalByrefFieldToSlot`) and PASSES
      after task 1.1 (the load-bearing FAIL-on-HEAD -> PASS-after keeper).
- [x] 2.4 DROP the 2 `fixed` probes (`NeoStep17_Probe_FixedPrimitiveArray` /
      `_FixedElementAddress`) from the keeper set -- they stay FAIL-on-HEAD via
      `Conv_U` (out of scope). Record the `fixed`/`Conv_U` blocker in the ship
      log + `neo-deferred-items.md` instead (keep the smoke green). (If the LEAD
      prefers to retain them as explicit expected-fail documentation, mark them
      clearly and exclude from the `NeoStep` filter -- but dropping is cleaner.)

## 3. Verify (the regression gate)

- [x] 3.1 Build CLI (`dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c
      Debug_Neo`) + TestCases (`dotnet build TestCases/TestCases.csproj -c Debug`).
- [x] 3.2 Run the F-10-R1 keeper alone: FAIL-on-HEAD -> PASS-after (load-bearing).
- [x] 3.3 Run NeoClrStructField filter: 8/8 MUST stay green (heap-IL F-10 path
      unchanged).
- [x] 3.4 Run full NeoStep smoke: 198 baseline + 5 new keepers (3 generic-byref +
      2 interface-on-VT) = 203 green. The F-10-R1 keeper is a 6th (FAIL-on-HEAD
      -> PASS-after, so it contributes +1 after the fix -> 204 total green after
      the fix, depending on whether the baseline recount includes it).
- [x] 3.5 Run NeoStep20 9/9 (F-10 is load-bearing for Step 20 sync) + NeoOptHard
      24/24.
- [x] 3.6 Run the Step 17 Legacy filter (plain `Debug` + `useRegister=true`):
      Legacy-neutral confirmation (the gate is Neo-only by construction; this is
      a belt-and-suspenders check).
- [x] 3.7 Stash-toggle: revert task 1.1 -> the F-10-R1 keeper NREs again; the 6
      F-6-only probes are UNAFFECTED (the gate touches only F-10, not F-6).
      Re-apply.

## 4. Docs

- [x] 4.1 Update `.trae/documents/neo-deferred-items.md`:
      - D-CONSTRAINED master-table row + §3 (c) prepend: mark (c) generic-byref
        + interface-on-VT-constrained CLOSED (no-op, regression guards); reroute
        `fixed` to a future `Conv_U`/pointer step (was "accept-known for fixed
        if the address works"; refined: the address works via `ref arr[i]`, but
        the `fixed` statement needs `Conv_U`/`Conv_I`).
      - F-10-R1 §3 entry (~line 896): RESOLVED (the JIT-discriminator gate
        shipped; the constrained-VT reproducer constructed; the latent defect
        closed).
- [x] 4.2 Write `ship-log.md`: the dump verdicts, the single engine change, the
      6 keepers (5 TEST-ONLY + 1 FAIL->PASS), the `fixed` deferral, the
      verification evidence (NeoStep smoke + NeoClrStructField + NeoStep20 +
      NeoOptHard + Legacy-neutral).
