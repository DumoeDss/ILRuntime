# Ship Log — neo-aot-clrbase-iface (child 7)

**Date:** 2026-07-10  **Capability:** neo-optimizer (AOT Cecil-free)  **Wave:** completion-3, child 7
**Status:** SHIPPED (LEAD-verified)  **lead-6 ref:** MEDIUM#6. Proceeded despite child 6 PARKED
(AOT is independent of the ValueTask cluster).

## Delivered
Cecil-free AOT (`.neo`) load of a type whose base/interface is a CLR type needing a
`CrossBindingAdaptor`. **Root cause = 3 coupled gaps:**
- **WRITE** `NeoAssemblyWriter.BuildTypeDef`/`BuildInterfaces`: dropped the CLR base/interface for
  an adaptor-resolved base (the `type.BaseType is ILType` check missed it — `CrossBindingAdaptor :
  IType`, NOT `ILType`) → `BaseTypeRefIdx` stayed `-1`. Fix: record the adaptor's `BaseCLRType`
  (`module.ImportReference` → Cecil TypeRef → `IndexTypeRef`). No `.neo` Version bump (additive).
- **LOAD** `ILType.FinalizeFromNeoRecord`: resolved a CLR base to a raw `CLRType` but did NO
  `CrossBindingAdaptors` lookup → no adaptor installed (`CLRInstance` = `this`, `TypeForCLR` wrong).
  Fix: `appdomain.CrossBindingAdaptors[clr]` lookup + install (`TypeLoadException` on missing —
  loud, mirrors `InitializeBaseType`/`InitializeInterfaces`).
- **RESOLVE** `ResolveFirstCLRBase`/`Interface`: missed an adaptor base (neither `CLRType` nor
  `ILType`) → returned null. Fix: `if (baseType is CrossBindingAdaptor cba) return cba;` short-circuit.

## Verification (LEAD-verify)
- NeoStep smoke: **253/0/0** (no regression). NeoStep25 gate (LEAD re-ran): **11 tests, 0 failed**.
- **NeoStep25ClrBaseIface 4/4** (capstone: compile+emit; `FirstCLRBaseType is CrossBindingAdaptor`
  invariant; `CLRInstance is Exception` bridge; `Invoke(Tag)==42` end-to-end; M1 body-mutation guard).
- NeoStep25CecilFreeLoad 7/7, NeoStep25LoadExec 28/28, NeoStep25ClrAdaptor 7/7, NeoStep23Roundtrip
  15/15 — all held.
- Stash-toggle (implementer): invariant cell FAILs on HEAD (`FirstCLRBaseType null`) → PASSes with
  fix. Load-bearing.
- **Legacy-neutral:** LEAD confirmed all `ILType.cs` changes are inside `#if ENABLE_NEO_MODE`
  (region opened at :1270, closed at :1661 — `FinalizeFromNeoRecord`, `ResolveFirstCLRBase`,
  `ResolveFirstCLRInterface` all within) → plain `Debug` compiles them out. `NeoAssemblyWriter.cs`
  is whole-file Neo-gated. Plain `Debug` builds 0 errors.

## Durable findings
- **`CrossBindingAdaptor : IType` (NOT `ILType`).** Any base/interface chain walk MUST short-circuit
  on `is CrossBindingAdaptor` or it returns null (the adaptor is neither `CLRType` nor `ILType`). The
  Cecil path replaces a CLR base/interface with its adaptor at init (`InitializeBaseType` :1999 /
  `InitializeInterfaces` :1905); the Cecil-free path must do the same at `FinalizeFromNeoRecord`, and
  the WRITE side must record the adaptor's `BaseCLRType` name.

## Follow-ups (out of scope)
- Harness-adaptor Cecil-free load (e.g. `: TestClass2`) — skip-listed at compile
  (`neo-step25-clr-adaptor`) → never reaches a Cecil-free load; a missing adaptor throws
  `TypeLoadException` (loud, documented).
- **NeoStep24CliRoundtrip is 1/5 PRE-EXISTING** (NOT introduced — identical failure set with/without
  this change per stash-toggle). lead-6's baseline said 5/5 at a different HEAD; flag for a Step-24
  regression investigation + baseline refresh.
- Generic Cecil-free = child 8 (`neo-aot-generic-cecilfree`).

## Review
LEAD-verify (Neo-gating confirmed via the `ILType.cs:1270-1661` region; NeoStep25 gate re-ran 0-fail;
Legacy-neutral by construction). No capability-spec sync (the Cecil-free AOT path is covered by the
`neo-optimizer` spec from the S3 children; this is the CLR-base sub-case — documented in the
archived `design.md`).
