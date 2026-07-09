# Ship Log — neo-aot-multi-hotfix (child 10; LAST AOT child)

**Date:** 2026-07-10  **Capability:** neo-optimizer (AOT)  **Wave:** completion-3, child 10
**Status:** SHIPPED (LEAD-verified). The AOT Cecil-free cluster is now complete (7 shipped, 8 parked,
9 shipped, 10 shipped).

## Delivered
Cecil-free **multi-hotfix-assembly cross-references** (one IL hotfix referencing a type in another).
**Root cause:** A-before-B load FAILS — A's field type B resolves to NULL via `ResolveNamedIType` at
build time (B not loaded yet) ��� `A.fieldTypes[BField]` silently NULL → NRE at exec. B-before-A worked
(B already in `mapType`). **Fix = LAZY cross-assembly re-resolution** (Neo-gated, additive):
- `ILType.ReResolveCrossAssemblyRefs(model, rec)`: re-resolves still-NULL instance/static field
  types + baseType + interfaces **BY NAME** (with `CrossBindingAdaptor` install, mirroring
  `FinalizeFromNeoRecord`); recomputes `fieldStartIdx`/`totalFieldCnt`/`firstCLRBaseType`/
  `firstCLRInterface` when the base resolves. Idempotent, per-slot-null-guarded, no-op on non-AOT.
- `AppDomain.neoAotBuilt`: tracks each Cecil-free `ILType` + its record/model; `LoadNeoAssembly` calls
  re-resolve after each load → **ORDER-INDEPENDENT** (earlier-built types re-resolve once later-loaded
  types exist). Best-effort (skips on throw, never fatal). No `.neo` format change.

## Verification (LEAD-verify)
- NeoStep **253/0/0** (unchanged). NeoStep25 gate (LEAD re-ran): **11 tests, 0 failed**.
- **NeoStep25CecilFreeMultiHotfix 5/5** (JIT ref; partitioned-compile; B-before-A; A-before-B; M1
  body-mutation against an inlined `1000` constant).
- Held: NeoStep25CecilFreeLoad 7/7, NeoStep25ClrBaseIface 4/4.
- Stash-toggle (implementer): A-before-B cells 3/5 on HEAD → 5/5 after. Load-bearing.
- **Legacy-neutral:** LEAD confirmed `AppDomain.cs` `neoAotBuilt` field (884-889) + the
  `LoadNeoAssembly` re-resolve loop (783-800) are inside `#if ENABLE_NEO_MODE` (region opened :700;
  field region :859-890). `ILType.cs` `ReResolveCrossAssemblyRefs` is in the Neo region (before the
  :1661 `#endif`). Plain `Debug` builds 0 errors.

## Durable findings
1. **Cecil-free cross-assembly resolution is purely NAME-BASED + ORDER-INDEPENDENT** (the cross-
   assembly analog of `ReRegisterTokenBindings` for body-token operands). The engine has no "assembly"
   concept for Cecil-free types — it accumulates `.neo`-built types into one AppDomain by name.
2. **Silent NULL field types** (resolved to null at build by `ResolveNamedIType`) are the Cecil-free
   generic failure mode (non-fatal, but wrong dispatch at exec). The `ReResolveCrossAssemblyRefs`
   pattern (re-resolve NULL slots by record at each load) is the template for any future cross-load
   reference gap.

## Honest residual
- **Simulated multi-assembly** (two `.neo` models from one DLL, not two distinct DLLs) — the harness
  AOT-compiles from a single `TestCases.dll`. The `.neo` format + name-based resolution is
  assembly-agnostic, so this exercises the same load-time cross-model resolution as a real two-DLL
  setup. Both load orders covered.
- Out of scope (documented): generic-instance cross-assembly refs (child 8); cross-process
  multi-assembly (child 9 proved portability); static `.cctor` cross-assembly seeding (sub-surface 4).

## Review
LEAD-verify (`AppDomain.cs` + `ILType.cs` Neo-gating region-confirmed; NeoStep25 gate re-ran 0-fail;
Legacy-neutral by construction).
