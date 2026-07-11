# Design - neo-aot-byref-wireup (child 16)

## Context

Child 14 (`archive/2026-07-10-neo-byref-clr2il-delegate`) shipped the RUNTIME
CLR->IL delegate-byref machinery: a CLR host helper
(`TestCLRBinding.InvokeRefCallback`) invokes a delegate whose target is an IL
method taking a `ref`/`out` param, routed through
`DelegateAdapter.NeoInvokeByRef` -> `NeoInvokeSub(marshalByRef:true)`. The
converter that bridges the CLR delegate type to the IL target is registered
RUNTIME-SIDE (`ILRuntimeHelper.Init` in `helper.cs`, under
`#if ENABLE_NEO_MODE`):

```csharp
app.DelegateManager.RegisterDelegateByRefConvertor<TestCLRBinding.Clr2IlRefIntDelegate>(
    (adapter) => new Clr2IlRefIntDelegate((ref int x) => { ... adapter.NeoInvokeByRef(args); ... }));
```

The GAP (lead-6 / MEDIUM#8): does the standalone `ilrt_neoc` precompile CLI --
which builds a FRESH `AppDomain` with NO converter registration -- fatal/skip on
an assembly that USES a delegate-byref call, or emit a `.neo` that lacks the
byref wiring?

## The finding (probe-first: NO precompile gap; the byref map is RUNTIME-only)

**There is NO AOT precompile gap for the byref map.** Confirmed by BOTH static
analysis and an empirical reproducer.

### Static analysis: `GetConvertor` is RUNTIME-only, NEVER called from the JIT

The site that builds the CLR-side delegate wrapper + consults the byref
converter is `DelegateAdapter.GetConvertor(Type)` (`DelegateAdapter.cs:1561`).
A code-wide grep shows it is called ONLY from runtime interpreter paths:

- `CLRRedirections.cs` lines 544/589/660/708/753/794/856/899/940/980 -- the
  `Delegate.Combine`/`Delegate.Remove`/`op_Equality`/`op_Inequality` CLR
  redirections (runtime interpreter opcodes).
- `Extensions.CheckCLRTypes` (`Extensions.cs:287`) -- a runtime value-coercion
  path (also called from field-set runtime sites in `ILIntepreter.Register.cs`).

`JITCompiler.cs` (the CIL->`OpCodeR` translator the precompiler drives) has
**ZERO** references to `GetConvertor`, `CheckCLRTypes`, `DelegateAdapter`,
`FindDelegateAdapter`, or any delegate-converter registration. The JIT purely
translates IL to `OpCodeR`; it never builds or consults a delegate converter.

The precompiled IL method (e.g. `NeoStep19_Clr2Il_ByRef`) is ordinary IL --
`ldftn Clr2IlBumpRef`, delegate `newobj`, `call InvokeRefCallback` -- none of
which consults the converter at JIT time. The converter is a RUNTIME concern,
consulted when the AOT body RUNS (the `del(ref x)` call inside the host helper
hits `GetConvertor` at runtime), regardless of whether the method body was
JIT-compiled or AOT-loaded.

### Empirical reproducer (the decisive test)

The standalone `ilrt_neoc` CLI was built (`dotnet build
ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj -c Debug_Neo`) and driven over
`TestCases.dll` (which contains the child-14 byref-delegate IL callees + entry
points in `NeoStep19Test`) WITH the `ILRuntimeTestBase.dll` reference (the host
CLR assembly holding `TestCLRBinding`):

```
ilrt_neoc TestCases.dll <out.neo> ILRuntimeTestBase.dll
-> compiled 1616 methods, 98 templates, 470 types (191 skipped)
-> EXIT=2 (partial: some methods skipped, .neo WRITTEN)
```

**Zero of the byref-delegate methods were skipped.** A targeted grep of the
SKIP list for `Clr2IlBumpRef` / `Clr2IlSetOut` / `Clr2IlBumpLong` /
`Clr2IlAddTen` / `Clr2IlDouble` / `NeoStep19_Clr2Il_*` returned EMPTY -- the
byref-delegate methods precompiled cleanly. `NeoStep19Test`'s TypeDef was
emitted to the `.neo`.

(CAVEAT: the 191 skipped methods + the partial status are a PRE-EXISTING,
unrelated AOT limitation -- many `TestCases` methods use opcodes not yet
AOT-supported. None of them are the byref-delegate methods. A full-`TestCases`
precompile WITHOUT the `ILRuntimeTestBase` ref fatals at the serializer on
`TestVector3` -- also pre-existing, unrelated; the ref fixes the cross-assembly
resolution the same way `NeoStep25ClrBaseIfaceCheck` does.)

## The fix = NONE required (the wiring is already correct)

Because the byref converter is a RUNTIME-only mechanism, the standalone
`ilrt_neoc` CLI needs **NO converter registration to PRECOMPILE** a byref-
delegate assembly:

- The `.neo` is emitted from the IL bodies (JIT), which never consult the
  converter.
- The converter is registered in the EXECUTION AppDomain (whoever LOADS and
  RUNS the `.neo`), exactly as the test harness registers it via
  `ILRuntimeHelper.Init`.

This mirrors how the standalone CLI already handles EVERY other delegate
converter / CLR-redirection registration: the CLI compiles bodies; the host
registers converters at load time. The byref map is no different. The
"standard pattern out of the box" note in child 14's ship-log follow-ups is
confirmed: no CLI-side wiring is needed.

## What WAS added: a permanent AOT-wireup probe (`NeoStep25ByrefWireupCheck`)

To LOCK IN this finding (so a future regression that makes the precompile
fatal/skip on byref-delegate methods is caught), a permanent host-side
self-check was added: `NeoStep25ByrefWireupCheck` (CLI special mode
`NeoStep25ByrefWireup`). It:

1. **Cell 1 (the core gate):** drives the SAME public `NeoCompiler` driver the
   `ilrt_neoc` CLI uses (the file-path overload -- the EXACT entry the binary
   calls) over `TestCases.dll` + the `ILRuntimeTestBase.dll` ref, and asserts
   the byref-delegate methods (4 entry points + 5 IL callees) are NOT in the
   SKIP list. A byref-delegate-UNAWARE precompiler would skip them -- this is
   the gap's hypothetical symptom, now guarded.
2. **Cell 2:** asserts `NeoStep19Test`'s TypeDef is emitted to the `.neo`.
3. **JIT cells (pre-Attach):** runs the 4 byref-delegate entry points on their
   JIT bodies (DivideByZero-s on a wrong result) -- establishes the runtime
   converter is registered + working for the JIT path (the precondition).
4. **CONTROL cells (AOT-exec, non-byref):** runs `NeoStep19_ClrCallback`
   (CLR->IL `List.ForEach` delegate callback, the non-byref analog) +
   `NeoStep19_PlainIntParam` (IL-delegate-Invoke, plain int) on AOT bodies --
   the TRIAGE signal.
5. **Byref AOT-exec cells (DIAGNOSTIC):** runs the 4 byref entry points on AOT
   bodies.
6. **Triage verdict cell:** classifies the AOT-exec gap direction from the
   control vs byref failure counts.

## The AOT-exec round-trip: a SEPARATE, GENERAL (non-byref) pre-existing gap

The byref AOT-exec cells FAIL (DivideByZero / NullReferenceException). BUT the
triage reveals this is NOT byref-specific: **both non-byref CONTROL shapes
ALSO fail on AOT-exec** (`NeoStep19_ClrCallback` -> "Step 17/13b: field/element
access on a CLR object via the IL-instance path is deferred"; `_PlainIntParam`
-> DivideByZero). The verdict cell records:

> "AOT-exec gap is GENERAL (non-byref control also fails: 2 control / 4 byref)
> -> NOT byref-specific; the byref-wireup COMPILE gate (Cell 1) is this child's
> scope and PASSED. AOT-exec delegate parity is a separate Step-24 follow-up."

This aligns with the lead-7 handoff: Step 24 (the `ilrt_neoc` CLI) is PARTIAL
(NeoStep24CliRoundtrip 1/5 pre-existing; 69 type-skips + 181 method-skips; exit-0
not achieved). Attaching a full-`TestCases` `.neo` to a Cecil-based session
AppDomain + running delegate-callback methods surfaces AOT-body-vs-JIT
divergences that hit ALL delegate-callback shapes, not just byref. That broader
AOT-exec delegate parity is **out of this child's scope** (the brief: "if the
gap is a deep engine bug, PARK").

## Goals / Non-Goals

**Goals (ACHIEVED):**
- Confirm the `ilrt_neoc` standalone CLI precompiles a byref-delegate assembly
  WITHOUT fatal/skip on the byref methods. (Cell 1 PASS; empirical `ilrt_neoc`
  run: 0 byref skips.)
- Lock the finding with a permanent probe so a future precompile regression on
  byref-delegate methods is caught.
- Triage the AOT-exec round-trip direction (byref-specific vs general) and
  record the verdict.

**Non-Goals:**
- AOT-exec delegate/callback parity (the broader pre-existing Step-24 gap;
  CONTROL shapes fail too). Separate follow-up.
- Any engine/NeoCompiler change. NONE was needed (the byref map is
  runtime-only; the CLI wiring is already correct).

## Risks / Trade-offs

- **[Risk] The probe compiles the FULL `TestCases.dll` (file-path overload),**
  which is slow (~30-60s) + emits heavy JIT debug output. -> Mitigation: the
  probe is a special-mode gate (not in the NeoStep smoke), invoked explicitly;
  the cell filter isolates the byref methods from the 174 unrelated skips.
- **[Risk] The AOT-exec cells FAIL (6 fails in the report).** -> These are
  DIAGNOSTIC (the triage verdict cell PASSES; the verdict message records that
  the gap is general, not byref-specific). The child's DONE criterion is the
  COMPILE gate (Cell 1) + the NeoStep smoke, not AOT-exec parity.
- **[Risk] The full-`TestCases` Attach (1176 attached / 555 skipped) has broad
  side-effects.** -> The CONTROL shapes (non-byref) isolate the byref signal:
  if ONLY byref failed, it would be byref-specific; since control ALSO fails,
  the gap is general. The triage cell automates this read.

## Implementation Notes

- `NeoStep25ByrefWireupCheck` is `#if ENABLE_NEO_MODE && DEBUG` (the standard
  gate for the NeoStep25 host-side checks); a Legacy stub keeps the file
  compile-clean in plain Debug/Release.
- The file-path `NeoCompiler.Compile(path, refs, stream)` overload is used (NOT
  the explicit-types overload) because it is the EXACT entry the `ilrt_neoc`
  binary calls + it sets up the Cecil resolver search dirs + the host-CLR
  `Assembly.LoadFrom` for the ref (so `TestCLRBinding` resolves). The
  explicit-types overload lacks the resolver setup and fatals at the serializer
  on the cross-assembly `TestVector3` reference.
- The probe locates `NeoStep19Test` in the SESSION AppDomain (which has the
  converter registered via `ILRuntimeHelper.Init` + `TestCases` Cecil-loaded)
  for the exec/attach; the `.neo` model comes from the file-path compile.
