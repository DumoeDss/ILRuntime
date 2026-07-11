## Why

Steps 12b and 16 surfaced three pre-existing Neo optimizer / arithmetic
correctness bugs (documented as deferred items K1, Q-STRUCT, Q-LONG in
`.trae/documents/neo-deferred-items.md`). K1 is a **silent correctness bug**:
Forward Copy Propagation (FCP) rewrites `b.field` reads to `a.field` even after
`a.field` is mutated, so `S b = a; mutate(a.field); read(b.field)` returns the
mutated value instead of the copied value. This change fixes the bugs that can
be precisely root-caused and reproduced on current HEAD, and honestly reports
the ones that cannot, so we do not ship guessed fixes into a shared optimizer
pass that affects every method.

## What Changes

- **K1 (FIXED — corrected re-attempt): FCP respects value-type copy
  independence after a field mutation.** A whole value-type `Move` (`S b = a`)
  propagates field reads (`b.field` -> `a.field`); today an intervening
  `a.field = v` does NOT kill that propagation, so the dest's copied field
  reads as stale. The field store reaches the base local INDIRECTLY via a
  `ldloca.s` address handle (`ldloca rAddr, rBase` then `stfld.*.inline
  rAddr, ...`), so `Stfld_*_Inline.Register1` is the address temp, never the
  base — a kill keyed on it (the original, reverted attempt) is a no-op. The
  corrected fix kills the propagation when a `Ldloca`/`Ldloca_S` takes the
  address of the propagation source (`xSrc`) or dest (`xDst`); the base local
  is the Ldloca source register (`op.Register2`), already enumerated by FCP.
  Neo-only (`#if ENABLE_NEO_MODE`); Legacy `ExecuteR` compiles it out.
- **Q-STRUCT (DEFERRED - not reproducible on current HEAD):** the documented
  "struct-local + field-mutation + element-read optimizer temp-renumber" quirk
  could not be reproduced with the exact array/mutation/branch patterns from
  the Step 16 findings nor with high-register-pressure variants (6 probes,
  all pass). Tracked as a spec requirement with a "confirm reproducibility
  first" gate; no fix shipped this change.
- **Q-LONG (DEFERRED - not reproducible on current HEAD):** the documented
  "long default-zero compare (conv.i8)" quirk could not be reproduced on
  current HEAD with array-element, scalar-local, or default-field patterns
  (the conv.i8 + Cgt_Un/Ceq_I8/Bne_Un_I8 arms all evaluate correctly). Tracked
  as a spec requirement; no fix shipped this change.

## Capabilities

### New Capabilities
- `neo-optimizer`: correctness invariants of the Neo code-path through the
  shared optimizer passes (FCP / BCP / copy-prop) and the conv/compare
  type-specialization. Establishes the K1 field-write-kill invariant as a
  hard requirement and records Q-STRUCT / Q-LONG as tracked, deferred-until-
  reproduced requirements.

### Modified Capabilities
<!-- none -- no existing capability's spec-level behavior changes. -->

## Impact

- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.FCP.cs` - the K1 fix
  (field-write kill). Gated to Neo-only opcodes; the shared pass keeps its
  Legacy behavior verbatim.
- `TestCases/NeoOptHardeningTest.cs` (new) - the K1 regression test (the
  ProbeK1 probe, promoted to a permanent case). Plus the Q-STRUCT / Q-LONG
  probes as non-asserting documentation cases so the patterns stay under test.
- No change to Legacy `ExecuteR`, BCP, copy-prop, or the JIT conv/compare
  lowering (Q-STRUCT/Q-LONG are not fixed this change).
- Regression gate: full NeoStep smoke stays all-green (was 72/72; the K1 fix
  adds zero new failures and turns the K1 probe green); Legacy smoke is
  unaffected (Neo-only opcode gate).
