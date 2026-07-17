# Ship Log — neo-raw-ldfld-stfld-clr-struct-seeding (child 21)

**Change:** seed `registerTypes` for a Call returning a primitive float/double/long + raw `Ldfld`
of a CLR-struct primitive field (the float-corruption seeding sibling of child-16 `neo-addi-on-float`;
lead-12's highest-value latent follow-up).
**Capability:** `neo-optimizer` (ADDED requirement).
**Pipeline:** small-feature (propose -> apply -> verify -> review-clean -> ship).
**Date:** 2026-07-13. **Branch:** `features/object-model-overhaul`. **Base HEAD:** `fc2baa26`.

## What shipped
A purely-additive Neo-only fix (71 added / 0 removed lines, single engine file) to
`TypeSpecializeNeoOpcodes` in `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`, all under
`#if ENABLE_NEO_MODE` (Legacy-neutral by construction):
1. **Raw `Ldfld` seeding case** (~:1075-1105) — decodes `(typeHash<<32)|fieldHash` from
   `OperandLong`, resolves `CLRType.GetField(fieldHash)`, seeds `registerTypes[op.Register1]` for a
   primitive float/double/long field. Reached ONLY for a CLRType declaring owner (ILType owners are
   already rewritten to typed `Ldfld_R4`/`R8`/`I8` by the splitter). Byte-identical hash decode to
   child-4's runtime handler + the ExecuteNeo raw-Ldfld arm.
2. **Call trailing primitive-ReturnType seed** (~:1311-1329) — after the existing stale-VT/stale-ref
   clearing, seeds `registerTypes[dest]` with the resolved primitive `ReturnType`. All 5 call variants
   (`Call`/`Callvirt`/`Callvirt_IL`/`Callvirt_CLR`/`Call_Redirect`) share the same case-label block, so
   a single seed covers CLR callees (best-effort; null ReturnType -> dest stays unseeded = HEAD).
3. **`NeoClrPrimitiveTypeToIType` helper** (~:1581-1599) — `float->FloatType, double->DoubleType,
   long/ulong->LongType, other-primitive->IntType (preserves null->I4 fallback), non-primitive->null`.

Plus the permanent regression probe `TestCases/NeoStepFloatSeedingProbe.cs` (4 probes).

## Why
Neo's frame is UNTYPED; typed-arithmetic specialization (`Addi->Addi_R4`, `Mul->Mul_R4`, ...) keys
SOLELY on `registerTypes[Register2]`. Child-16 closed `Ldc_*`/`Ldfld_*`/`Ldind_*`/`Ldelem_*` but
explicitly deferred two producer families: a Call returning a primitive float, and raw `Ldfld` of a
CLR-struct field. Both left the dest unseeded -> specialization no-oped -> a plain INTEGER op on the
raw IEEE bits -> silent garbage (e.g. `0x3F800000 + 0x3F800000 = 0x7F000000` instead of `1+1=2`).

## Re-audit verdict (5-for-5/12-for-12 lesson applied; this one CONFIRMED real)
The planner built 4 reproducer probes and DUMPED the JIT output before proposing:
- **Call returning float (TC1) / double (TC2):** REPRODUCES — DivideByZero on HEAD; JIT dump showed
  plain `addi r7,r7,1120403456` (bits of 100.0f) instead of `addi.r4`.
- **Raw Ldfld CLR-struct field, mul+add (TC3) / reg-reg add (TC4):** REPRODUCES — DivideByZero; JIT
  dump showed plain `muli`/`addi` on float bits. TC4 runtime dump: `TestVector3 a = (1.7014118E+38, 1, 1)`.
- **Raw `Stfld`:** DISPROVEN-in-scope — it CONSUMES a value; does not produce one feeding arithmetic.

## Verification
- **NeoStep smoke: 358/0** (354 baseline + 4 new probes), ZERO regressions across EH/byref/VT/box/
  arithmetic/float categories.
- **Stash-toggle (airtight):** `git stash push -- JITCompiler.cs` (probe kept) -> rebuild -> **4/4 FAULT**
  (DivideByZero at the deliberate `1/0` guards); `git stash pop` -> rebuild -> **0/4 PASS**. Proves the
  fix (not the probe) is load-bearing. Probes are observably-wrong (DivideByZero), not just wrong-value.
- **JIT dump (post-fix):** `addi.r4`, `addi.r8`, `muli.r4`, `add.r4` now fire (typed specialization).
- **Legacy-neutral:** 100% `#if ENABLE_NEO_MODE`; identical Legacy NeoStep result with and without the
  change; the 4 probes pass under Legacy (0/4).

## Review
**APPROVE-WITH-FINDINGS** (reviewer != implementer; independent re-run of smoke + stash-toggle).
- **0 Blocker / 0 Major.**
- Minor M1: `appdomain.GetMethod(op.Operand2)` not hoisted (perf nit, negligible, idempotent, no
  correctness impact) — accepted-known.
- Minor M2: CLR-callee (`Callvirt_CLR`/`Call_Redirect`) primitive-float return path has no dedicated
  probe (O1). Worst case = no-regression (dest stays unseeded = byte-identical to HEAD); the
  implementer's analysis shows all 5 call variants share the case block so the seed does fire —
  accepted-known, optional future probe.
- 3 Trivial: `ulong->LongType(I8)` is a strict improvement over HEAD's `null->I4`; hash-decode cast
  form cosmetic; pre-existing CLI `failded` typo.
- Pre-existing P1/P2 (IL-VT-return-to-fresh-temp gap; registerTypes single-pass no-phi-merge) — noted,
  not introduced, out of scope.

## Delivery
**Mode:** local commit (portfolio per-child delivery; push per the parent directive
"commit+push after each clean child"). Committed alongside the portfolio-run.json + planning-context.md
record updates. No PR (long-running feature branch).

## Durable findings (for future planning)
1. The Neo typed-arithmetic specialization keys SOLELY on `registerTypes[Register2]`; ANY primitive
   float/double/long producer that does not seed its dest silently falls back to a plain INTEGER op on
   the raw IEEE bits. Closed producers: `Ldc_*`/`Ldfld_*`/`Ldind_*`/`Ldelem_*` (child-16) + `Call`-
   primitive-return + raw-`Ldfld`-CLR-struct (child-21). Fix pattern = "add the producer to the
   `TypeSpecializeNeoOpcodes` seeding switch" whenever the type is knowable at JIT time (it is, for
   both new cases); reach for the child-15 `0x8`-in-`Operand4` JIT marker ONLY when the type is
   unknowable at JIT time.
2. The `Call` case in `TypeSpecializeNeoOpcodes` (`JITCompiler.cs:1217-1329`) only CLEARS stale
   VT/ref dest types; it NEVER seeds the resolved `ReturnType` — so any primitive-float method return
   into a fresh temp was unseeded. All 5 call variants share one case-label block.
3. The raw-`Ldfld` CLR-struct-owner escaping shape is the JIT-time mirror of child-4's runtime handler
   — the identical `(typeHash<<32)|fieldHash` encoding and `CLRType.GetField(fieldHash)` work at JIT
   time exactly as at runtime.
