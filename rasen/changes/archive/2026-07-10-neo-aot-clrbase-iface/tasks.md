# Tasks: neo-aot-clrbase-iface (Cecil-free CLR base/interface adaptor)

## 1. Probe (the Cecil-free CLR-base type)
- [x] 1.1 `NeoClrProbe.ExceptionProbe` already exists (`: System.Exception`,
  built-in `ExceptionAdaptor`, already compiles+emits per the COMPILE-side
  child). Confirm it carries a method the capstone invokes (`Tag() => 42`).
- [x] 1.2 No new probe type needed for the capstone (ExceptionProbe + its
  Tag() suffice). The CLR-base bridge is asserted via the invariant +
  CLRInstance, not via a CLR-member call (D3).

## 2. Engine fix (the Cecil-free adaptor install)
- [x] 2.1 `ILType.FinalizeFromNeoRecord`: after resolving `baseType` to a
  CLRType, add the Neo-only `CrossBindingAdaptors` lookup + install (mirror
  `InitializeBaseType` :1996-2007). Skip `object`/`Enum`/`ValueType`/
  `MulticastDelegate` (the Cecil path nulls these). Throw
  `TypeLoadException("Cannot find Adaptor for:...")` on a missing adaptor
  (loud, mirrors the Cecil path).
- [x] 2.2 `FinalizeFromNeoRecord`: the same lookup for each `interfaces[i]`
  CLRType (mirror `InitializeInterfaces` :1900-1910). Set `firstCLRInterface`
  via the post-install `ResolveFirstCLRInterface`.
- [x] 2.3 `ResolveFirstCLRBase`: add `if (baseType is CrossBindingAdaptor cba)
  return cba;` as the FIRST check (an adaptor IS the first CLR base; do not
  recurse into its own base).
- [x] 2.4 `ResolveFirstCLRInterface`: add the `is CrossBindingAdaptor` check
  per interface (an adaptor interface IS the first CLR interface).

## 3. Capstone check (host-side DEBUG+Neo self-check)
- [x] 3.1 `NeoStep25ClrBaseIfaceCheck.Run(appdomainA)`: compile
  ExceptionProbe -> `.neo` in A (Cecil-loaded, ExceptionAdaptor resolves ->
  compiles); Cecil-free-load into fresh B; assert:
  - (a) the invariant: `probeB.FirstCLRBaseType is CrossBindingAdaptor`
    (FALSE on HEAD -> the stash-toggle-able core);
  - (b) the bridge: `instB.CLRInstance is System.Exception` (the
    ILTypeInstance ctor built a real CLR Exception via the adaptor);
  - (c) end-to-end: `Invoke(Tag) == 42` under Cecil-free exec == A's JIT.
- [x] 3.2 Adversarial body-mutation cell (M1): mutate Tag's `Ldc_I4 42 -> 555`
  in an independent model2 BEFORE LoadNeoAssembly -> assert 555 (proves the
  genuine `.neo` body runs, not a Cecil fallback).
- [x] 3.3 Wire the check into `ILRuntimeTestCLI/Program.cs` under the
  `NeoStep25ClrBaseIface` filter (mirror the NeoStep25CecilFreeLoad block).
- [x] 3.4 Compute `expected` independently (A's JIT Invoke(Tag)) so a
  "both-garbage" false pass is ruled out.

## 4. Verify (the gates)
- [x] 4.1 `NeoStep25ClrBaseIface` capstone: all cells PASS (the invariant +
  bridge + end-to-end + M1).
- [x] 4.2 Stash-toggle: with the engine fix stashed, the invariant cell FAILs
  (FirstCLRBaseType is NOT an adaptor); with it applied, PASS. (The
  load-bearing proof.)
- [x] 4.3 `NeoStep25CecilFreeLoad` (7/7) + `NeoStep25LoadExec` (28/28)
  regression hold (no IL-base regression).
- [x] 4.4 `NeoStep` full smoke green (baseline 253/0/0 + the new capstone
  filter; no regression).
- [x] 4.5 `NeoStep25ClrAdaptor` (COMPILE side) regression holds.
- [x] 4.6 Legacy-neutral: plain `Debug` build of the CLI = 0 errors (the fix
  is `#if ENABLE_NEO_MODE`).

## 5. Docs
- [x] 5.1 `.trae/documents/neo-deferred-items.md` STEP-25 PARTIAL row: mark
  the CLR base/interface Cecil-free gap CLOSED (reference this change).
