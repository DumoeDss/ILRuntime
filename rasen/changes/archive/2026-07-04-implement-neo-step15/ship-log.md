# Ship Log — implement-neo-step15

**Change:** `implement-neo-step15` — Neo Step 15: `isinst` / `castclass` runtime
arms + offset-lowering stamping (plus a required Cgt_Un reference-null fix).
**Verdict:** **CLEAN** (0 Blocker / 0 Major / 1 Minor comment-nit, non-blocking).
**Date:** 2026-07-04. **Branch:** `features/object-model-overhaul`.
**Status:** Uncommitted (working tree). LEAD commits + pushes; SHIPPER did not
touch source.

## Change (one-line)

Implement `isinst` and `castclass` in `ExecuteNeo`, with Neo offset-lowering
stamping the reference-slot operands the arms need, plus a reference-null-aware
rewrite of the `Cgt_Un` arm so C# `is` / `!= null` (which lower to
`isinst; ldnull; cgt.un`) work on Neo's mStack-index references (-1 = null).

## Verification evidence

- **CLI `Debug_Neo`:** 0 errors.
- **TestCases `Debug`:** 0 errors (`TestCases/bin/Debug/netstandard2.1/TestCases.dll`).
- **FULL NeoStep smoke:** **65 ran / 0 failed** (was 58 + 7 new Step 15 cases,
  0 regression). Filter `NeoStep` over the shared TestCases.dll + pre-generated
  HotfixAOT.patch, `useRegister=true`, `-f net8.0 --no-build`.
- **Cgt_Un rewrite probe-verified:** a temporary probe
  (`uint 5>3`, `3>5`, `5>5`) emitted plain `cgt.un r,r,r` (confirmed in JIT
  dump; `uint` infers `U4` and is NOT re-typed) and **passed** — genuine
  unsigned-int comparison preserved. Probe removed after; suite restored.
- **Legacy untouched:** `git diff HEAD --stat` on
  `ILIntepreter.Register.cs` = 0 lines. All new code behind file-level
  `#if ENABLE_NEO_MODE`.

## Review summary

REVIEWER (adversarial, author != verifier): **CLEAN**. No Blocker, no Major.
The high-risk `Cgt_Un` rewrite mirrors Legacy's reference-null semantics exactly
**and** preserves genuine unsigned-int correctness (truth-table analysis +
empirical probe). isinst arm, castclass arm, and offset-lowering all verified
correct. `openspec-gstack-review` invoked; cross-model Codex pass unavailable
(401); fell back to first-principles structured + adversarial analysis. No PR /
no Greptile (no PR by design).

## Delivered scope

- `isinst` `ExecuteNeo` arm: type resolution via `AppDomain.GetType(ip->Operand)`,
  source read from register-2 ref slot, in-place result write; `null` source ->
  `null` result; assignability via `ILTypeInstance.CanAssignTo` (IL) or
  `Type.IsAssignableFrom` (CLR, incl. boxed VT); mismatch writes `null`, never
  throws.
- `castclass` `ExecuteNeo` arm: same dispatch; failure throws
  `System.InvalidCastException`; `null` source passes through as `null`.
- Neo offset-lowering (`Optimizer.Neo.cs` `LowerNeoOffsets`): stamps
  `DstOffset`/`SrcOffset`/`Operand3`(dst ref)/`Operand4`(src ref) for
  `Isinst`/`Castclass` as fall-through to the existing `Box`/`Unbox`/`Unbox_Any`
  block (R1==R2 in-place).
- **Cgt_Un arm fix:** reference-null-aware rewrite so C# `is`/`!= null` lower
  correctly on Neo's -1-means-null ref indices; preserves genuine unsigned-int
  compare.
- New test: `TestCases/NeoStep15Test.cs` (7 cases, all green).

## Non-goals (deferred)

- **box-T;isinst-U compile-time peephole fusion** — deferred: no fusion pass
  exists in this codebase; optimization only, not required for correctness.
- **`PatchKind.IsinstResult` generic-parameter patch table** — deferred: patch
  infra does not exist; optimization only.

## Accepted-known

- **cgt-un-comment (Minor-nit, non-blocking):** The `Cgt_Un` divergence comment
  names only the operand=sentinel case; the symmetric source=sentinel case also
  diverges (same sentinel-value collision class, unexercised by the entire
  `TestCases` suite — no uint-max arithmetic). Optional one-line comment tighten.
- **cgt.un/clt.un reference-null soft spot:** known; if a later step adds a
  ref-typed `clt.un` or genuine uint-max arithmetic, revisit (and add the
  analogous Clt_Un rule).
- **Pre-existing carryovers (not introduced here):** K1 (FCP VT propagation);
  K2-family (Move-path boxed-ref VT local); Step 13 area 3 (constrained. ->
  Step 17) + areas 4-5 (Step 13b); Step 14 catch-wrapper +
  CheckExceptionType-NIE-for-non-CLRType.

## Files changed (working tree, uncommitted)

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` — isinst +
  castclass `ExecuteNeo` arms; Cgt_Un reference-null-aware rewrite.
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` — Isinst/Castclass
  offset-lowering stamping (2 fall-through lines).
- `TestCases/NeoStep15Test.cs` — new, 7 cases.

## Git note

All of the above are **uncommitted working-tree changes**. Per workflow, the
SHIPPER did not edit source and did not commit; LEAD commits and pushes.
