# Ship Log — neo-aot-byref-wireup (child 16)

**Date:** 2026-07-10  **Capability:** neo-optimizer (AOT)  **Wave:** completion-3, child 16
**Status:** SHIPPED — doc-only/TEST-ONLY close (no engine change). LEAD-verified.

## Delivered + the KEY FINDING (corrects lead-6/MEDIUM#8)
**There is NO precompile gap.** The standalone `ilrt_neoc` CLI compiles a byref-delegate assembly with
NO error/skip of the byref methods:
```
ilrt_neoc TestCases.dll <out.neo> ILRuntimeTestBase.dll
-> compiled 1616 methods, 98 templates, 470 types (191 skipped), EXIT=2 (.neo WRITTEN)
```
A grep of the skip-list for the byref methods (`Clr2IlBumpRef`/`Clr2IlSetOut`/`Clr2IlBumpLong`/
`NeoStep19_Clr2Il_*`) is EMPTY. **Static analysis confirms why:** `GetConvertor` (the site that builds
the CLR delegate wrapper + consults the byref convertor, `DelegateAdapter.cs:1561`) is called ONLY at
RUNTIME (`CLRRedirections` Delegate.Combine/Remove/op_Equality + `Extensions.CheckCLRTypes`) — it has
**NO caller in `JITCompiler.cs`**. The byref convertor (child 14's `RegisterDelegateByRefConvertor` +
`NeoInvokeByRef`) is a **runtime-only mechanism**; precompile (JIT) never queries it. The `.neo` is
emitted from the IL method bodies; the convertor is registered by the host that loads/executes the
`.neo` (the standard pattern for ALL delegate convertors). **Corrects lead-6/MEDIUM#8's framing**
("CLI doesn't register the convertor" → not needed; not a gap).

## What shipped (locks in the finding, no engine change)
- `NeoStep25ByrefWireupCheck.cs` — a permanent host-side probe (`#if ENABLE_NEO_MODE && DEBUG`; Legacy
  stub in `#else`). CLI special-mode `NeoStep25ByrefWireup`. Cells: (1) the core compile gate —
  `NeoCompiler.Compile` on `TestCases.dll`+`ILRuntimeTestBase.dll` refs, asserts byref methods are NOT
  skipped (PASS); (2) emitted TypeDef (PASS); (3-6) 4 byref entry points' JIT execution (PASS — the
  runtime convertor IS registered); (7-8) 2 CONTROL non-byref delegate shapes' AOT execution
  (classification signal); (9-12) 4 byref AOT-execution cells (diagnostic); (13) classification cell.
- `ILRuntimeTestCLI/Program.cs` — the special-mode wiring (passes TestCases.dll + ILRuntimeTestBase.dll
  ref paths).

## Verification (LEAD-verify)
- **NeoStep smoke: 267/0/0** (unchanged; the probe is a host-side special-mode gate, not in the smoke
  filter).
- **`NeoStep25ByrefWireupCheck`:** 7/13 cells PASS — the core compile gate (cell 1, child-16's scope) +
  TypeDef + 4 JIT-execution + the classification cell. NeoStep19 (the runtime byref gate) held.
- **Legacy-neutral:** plain `Debug` build ILRuntime = 0 errors (probe `#if ENABLE_NEO_MODE && DEBUG`;
  Legacy stub).

## The AOT-execution roundtrip classification (a SEPARATE follow-up, not byref-specific)
The 4 byref AOT-execution cells FAIL (DivideByZero/NRE), but the 2 NON-byref CONTROL shapes
(`NeoStep19_ClrCallback` List.ForEach; `NeoStep19_PlainIntParam` IL delegate invoke) ALSO fail.
Verdict: the AOT-execution gap is **GENERAL (non-byref controls fail too)**, NOT byref-specific. The
byref-wireup COMPILE gate (cell 1) is child-16's scope and PASSES. The general AOT-vs-JIT delegate/
callback body parity is a **separate Step-24 follow-up** (`neo-aot-delegate-exe-parity`) — consistent
with lead-7's note that Step 24 is PARTIAL (NeoStep24CliRoundtrip 1/5 pre-existing).

## Durable findings
1. **The byref delegate convertor (child 14) is RUNTIME-ONLY — no AOT precompile wire-up gap exists.**
   `GetConvertor` has no JIT caller; `ilrt_neoc` doesn't need to register the convertor to precompile.
   (Corrects lead-6/MEDIUM#8.)
2. **The explicit-type `NeoCompiler.Compile(IReadOnlyList<ILType>, Stream)` overload LACKS the Cecil
   resolver search-dir setup + host-CLR `Assembly.LoadFrom` that the file-path overload has.** For
   types with cross-assembly refs (e.g. ILRuntimeTestBase), the explicit-type overload fatals
   (AssemblyResolutionException); the file-path overload resolves. Any AOT probe compiling types that
   reference host-CLR types must use the file-path overload WITH ref paths.
3. **Full-TestCases AOT execution has a pre-existing GENERAL AOT-vs-JIT body discrepancy for delegate/
   callback shapes** (both non-byref controls and byref shapes) — Step-24 domain, not byref-specific.
   Worth a dedicated `neo-aot-delegate-exe-parity` follow-up.

## Review
LEAD-verify (no engine change; the finding is self-validating — ilrt_neoc compiles byref methods
without skip + GetConvertor has no JIT caller; NeoStep 267/0/0; Legacy build 0 errors; probe
Neo+DEBUG-gated). Doc-only/TEST-ONLY close.
