# Tasks - neo-aot-byref-wireup (child 16)

## Status: DONE (the wiring was already correct; no engine change needed)

The gap (lead-6 / MEDIUM#8) hypothesized the standalone `ilrt_neoc` CLI does
NOT register/use the byref converter when precompiling a byref-delegate
assembly. **Probe-first investigation disproved this**: the byref converter is
a RUNTIME-only mechanism (`GetConvertor` is never called from the JIT), so the
CLI needs NO converter registration to PRECOMPILE. The `.neo` is emitted from
the IL bodies; the converter is consulted when the AOT body RUNS, registered by
whoever loads/executes the `.neo` (the same standard pattern as every other
delegate converter / CLR-redirection registration).

## Tasks

### 1. Static analysis: is `GetConvertor` (the byref-converter site) ever called from the JIT/precompile path? -- DONE
- Grep `JITCompiler.cs` for `GetConvertor`/`CheckCLRTypes`/`DelegateAdapter`/
  `FindDelegateAdapter` -> ZERO references. The JIT purely translates IL to
  `OpCodeR`; it never builds or consults a delegate converter.
- Grep the whole `ILRuntime/` tree for `GetConvertor` callers -> ONLY runtime
  paths (`CLRRedirections.cs` Delegate.Combine/Remove/op_Equality +
  `Extensions.CheckCLRTypes`). CONFIRMED runtime-only.

### 2. Empirical reproducer: does `ilrt_neoc` fatal/skip on a byref-delegate assembly on HEAD? -- DONE
- Build the standalone CLI:
  `dotnet build ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj -c Debug_Neo` -> 0 errors.
- Precompile `TestCases.dll` (holds the byref-delegate methods) WITH the
  `ILRuntimeTestBase.dll` ref:
  `ilrt_neoc TestCases.dll <out.neo> ILRuntimeTestBase.dll`
  -> `compiled 1616 methods, 98 templates, 470 types (191 skipped)`, EXIT=2
  (partial: .neo WRITTEN). NOT fatal.
- Grep the SKIP list for the byref-delegate methods (`Clr2IlBumpRef`,
  `Clr2IlSetOut`, `Clr2IlBumpLong`, `Clr2IlAddTen`, `Clr2IlDouble`,
  `NeoStep19_Clr2Il_*`) -> EMPTY. The byref methods precompiled cleanly.
- FINDING: NO precompile gap. The wiring is already correct.

### 3. Add a permanent AOT-wireup probe to lock the finding -- DONE
- New file `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep25ByrefWireupCheck.cs`
  (`#if ENABLE_NEO_MODE && DEBUG`; Legacy stub in the `#else`).
- CLI special-mode wiring in `ILRuntimeTestCLI/Program.cs` (mode
  `NeoStep25ByrefWireup`; passes `TestCases.dll` path + `ILRuntimeTestBase.dll`
  ref path).
- Cells:
  - **Cell 1 (core gate):** file-path `NeoCompiler.Compile` over `TestCases.dll`
    + ref -> assert the 4 byref entry points + 5 IL callees are NOT skipped.
  - **Cell 2:** `NeoStep19Test` TypeDef emitted to the `.neo`.
  - **JIT cells:** the 4 byref entry points run clean on JIT bodies (runtime
    converter registered).
  - **CONTROL cells:** `NeoStep19_ClrCallback` + `NeoStep19_PlainIntParam`
    (NON-byref delegate shapes) on AOT bodies -- the triage signal.
  - **Byref AOT-exec cells (DIAGNOSTIC):** the 4 byref entry points on AOT bodies.
  - **Triage verdict cell:** classifies the AOT-exec gap direction (byref-
    specific vs general) from control vs byref failure counts.

### 4. Triage the AOT-exec round-trip direction -- DONE
- Ran `NeoStep25ByrefWireup`: the 4 byref AOT-exec cells FAIL, BUT both
  non-byref CONTROL shapes ALSO fail. Verdict cell records: "AOT-exec gap is
  GENERAL (non-byref control also fails: 2 control / 4 byref) -> NOT byref-
  specific; the byref-wireup COMPILE gate (Cell 1) is this child's scope and
  PASSED. AOT-exec delegate parity is a separate Step-24 follow-up."
- The broader AOT-exec delegate/callback parity is a PRE-EXISTING Step-24 gap
  (lead-7 handoff: Step 24 PARTIAL; NeoStep24CliRoundtrip 1/5). NOT this child.

### 5. Regression check -- DONE
- NeoStep smoke: `267 tests, 0 failed` (baseline held; no regression).
- Legacy-neutral: `dotnet build ILRuntime/ILRuntime.csproj -c Debug` -> 0 errors
  (the probe is `#if ENABLE_NEO_MODE && DEBUG`; the Legacy stub compiles clean).

## Verification results
- Core gate (Cell 1, compile no-skip on byref-delegate methods): **PASS**.
- TypeDef emitted (Cell 2): **PASS**.
- JIT exec (4 byref entry points, runtime converter): **4/4 PASS**.
- AOT-exec triage verdict: **PASS** (verdict produced; gap is GENERAL, not byref).
- AOT-exec diagnostic cells: 6 FAIL (pre-existing general Step-24 gap; CONTROL
  shapes fail too -- NOT byref-specific, NOT this child).
- NeoStep smoke: 267/0/0 (no regression).
- Legacy build: 0 errors (Legacy-neutral).

## Follow-ups (out of scope)
- AOT-exec delegate/callback parity (the broader pre-existing Step-24 gap where
  attaching a full-`.neo` to a Cecil-based AppDomain + running delegate-callback
  methods diverges from JIT). Both non-byref control shapes AND byref shapes
  fail; the fix is general (AOT-body-vs-JIT divergence on delegate/closure
  paths), not byref-specific. Track under Step-24 / a dedicated
  `neo-aot-delegate-exe-parity` follow-up.
- The full-`TestCases` precompile partial status (191 skips; exit-2 not exit-0)
  -- the broader Step-24 completion work.
