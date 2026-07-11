# Implementer handoff — neo-step25-clr-adaptor (implementer-1)

> Apply stage, 2026-07-08. The adaptor TRUE-COMPLETION IS met (no
> `Cannot find Adaptor` fatal). The binding exit-0/2-on-full-`TestCases.dll`
> bar is NOT met — blocked by a DEEPER, DISTINCT follow-on gap (A2:
> anonymous-type field-init NRE). Handed off for a LEAD decision.

## One-line state

Adaptor fix shipped + green (self-check 7/7; NeoStep 219/0/0; S3ClrEnum 7/7;
Legacy-neutral). The standalone CLI no longer emits `Cannot find Adaptor`.
BUT on the FULL `TestCases.dll` it now exits 1 on a DIFFERENT fatal — the
A2 anonymous-type field-init NRE — so a valid `.neo` is not yet written.

## What is DONE (shipped, verified)

- **The fix** (`ILRuntime/Runtime/NeoAOT/NeoCompiler.cs`, `CompileCore`,
  Neo-only `#if ENABLE_NEO_MODE`): a per-TYPE pre-filter at the TOP of
  `CompileCore` eagerly triggers `type.FirstCLRBaseType` + `type.FirstCLRInterface`
  inside `try/catch(TypeLoadException)`; throwers go to `result.Skipped` via a
  new `MakeTypeSkip(type, ex)` helper (`MethodDisplay = "(type) <FullName>"`).
  A `compilableTypes` survivor list replaces `inputTypes` for BOTH the per-method
  loop AND `writer.Write(...)`; `result.TypesCompiled = compilableTypes.Count`.
  Narrow `TypeLoadException` catch (design D1) — a non-TLE init failure stays a
  loud fatal. `Program.cs` UNCHANGED (reuses the existing `Skipped` report +
  `IsComplete`->exit-2 path). Built-in adaptors (`ExceptionAdaptor` /
  `AttributeAdapter`, AppDomain ctor) keep resolving — NOT skipped (Fix A is a
  no-op; A1 in tasks.md records this).
- **The probe** (`NeoClrProbe/AdaptorProbe.cs`): `NeoClrProbe.ExceptionProbe
  : System.Exception` (built-in adaptor -> resolves) + `NeoClrProbe.AdaptorProbe
  : ILRuntimeTest.TestFramework.TestClass2` (harness adaptor -> skipped). NB: the
  tasks.md literal suggestion (`class AdaptorProbeClrBase {}` in the IL asm)
  would NOT trigger the adaptor lookup — a base defined IN the IL assembly is an
  ILType, and the lookup at `ILType.cs:1593` fires only for a CLRType base.
- **The self-check** (`ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep25ClrAdaptorCheck.cs`,
  `#if ENABLE_NEO_MODE && DEBUG`) + the `NeoStep25ClrAdaptor` CLI hook
  (`ILRuntimeTestCLI/Program.cs`). 7 cells, all assert the skip contract
  explicitly. **7/7 PASS.**
- **Deferred-items** (`.trae/documents/neo-deferred-items.md`): STEP-25-CLR-ADAPTOR
  marked RESOLVED (adaptor scope); NEW `NEO-AOT-FIELDINIT-NRE` row for A2.

## Evidence (all `-f net8.0`)

- `NeoStep25ClrAdaptor`: **7/7 cells, 0 failed.** Decisive cell: `AdaptorProbe
  (type) skip reported: PASS (TypeLoadException: Cannot find Adaptor
  for:ILRuntimeTest.TestFramework.TestClass2)`; `ExceptionProbe not skipped
  (built-in adaptor resolved): PASS`; `ExceptionProbe TypeDef emitted to .neo:
  PASS`; `AdaptorProbe TypeDef omitted from .neo: PASS`.
- NeoStep smoke: `Ran 219 tests, 0 failded, 0 ignored, 0 todos` — ZERO regressions.
- `NeoStep25S3ClrEnum`: **7/7** (S3-5 TestCLREnum shape unchanged — no regression).
- Builds: ILRuntimeTestCLI + ILRuntimeNeoCompiler (`Debug_Neo`) + NeoClrProbe +
  TestCases (`Debug`) + ILRuntime (`Debug`, Legacy-neutral) — all 0 errors.
- Full-`TestCases.dll` standalone CLI (`ilrt_neoc TestCases.dll out.neo
  ILRuntimeTestBase.dll`): the `Cannot find Adaptor for:TestClass2` FATAL that
  occurs on HEAD is **GONE**; the run now exits 1 on the A2 NRE instead (see
  below). Adaptor types ARE skipped into `result.Skipped` by the pre-filter, but
  the NRE throws before `Compile` returns, so no `SKIP (type)` lines are printed.

## A2 — the blocker (anonymous-type field-init NRE; NOT an adaptor issue)

After the adaptor pre-filter closes the `Cannot find Adaptor` fatal, the
standalone CLI on the FULL `TestCases.dll` advances to a NEW, DIFFERENT fatal:

```
ilrt_neoc: FATAL: serializer failure: Object reference not set to an instance of an object.
  inner: NullReferenceException: Object reference not set to an instance of an object.
```

Stack (captured via a temporary stack-diag in the `CompileCore` fatal wrapper,
now reverted):

```
ILType.InitializeFields()                                  ILType.cs:~2292
NeoAssemblyWriter.BuildTypeDef(ILType, NeoRefTableBuilder, ModuleDefinition)  NeoAssemblyWriter.cs:756
NeoAssemblyWriter.Write(ILType[], ILMethod[], ...)         NeoAssemblyWriter.cs:490
NeoCompiler.CompileCore(...)                               NeoCompiler.cs:~315
NeoCompiler.Compile(String, IReadOnlyList<String>, Stream) NeoCompiler.cs:~166
```

- `BuildTypeDef` reads `type.TotalPrimitiveSize` (`NeoAssemblyWriter.cs:756`),
  which triggers `ILType.InitializeFields`.
- Failing type (captured via a temporary per-type diag in the pre-filter, now
  reverted): `<>f__AnonymousType0`2<j,k>` — a compiler-generated ANONYMOUS type,
  an OPEN GENERIC type definition whose fields are generic-parameter-typed.
- Root cause: at `ILType.cs:~2277` `fieldType = appdomain.GetType(field.FieldType,
  this, null)` returns null (`FindGenericArgument` on the OPEN definition yields
  null for the generic-parameter field) -> at `ILType.cs:~2292`
  `fieldType.IsPrimitive` NREs on the null.
- **Why the pre-filter does not catch it:** the design's pre-filter triggers
  ONLY `FirstCLRBaseType` + `FirstCLRInterface` (base/interface init). It does
  NOT trigger field init. The anonymous type's base/interface init SUCCEEDS (it
  has no CLR base/interface needing an adaptor), so it passes the pre-filter and
  NREs later during `Write`/`BuildTypeDef` (field init).
- **Why this change INTENTIONALLY does not catch it:** this change's own spec
  scenario "A non-adaptor type-init failure stays a loud fatal, not a silent
  skip" uses EXACTLY "a genuine NullReferenceException" as its example. The A2
  NRE is a non-`TypeLoadException` init failure -> per spec it stays fatal.
  Catching it would require either broadening the catch (violates the scenario +
  design D1) or an `ILType.cs` change (this change's Non-Goals forbid it).

A2 is logged as `NEO-AOT-FIELDINIT-NRE` in `.trae/documents/neo-deferred-items.md`.

## Decision for the LEAD (pick one)

The adaptor TRUE-COMPLETION (the change's stated scope) is met. The binding
exit-0/2-on-full-`TestCases.dll` bar is blocked ONLY by A2. Three options:

1. **ACCEPT + DEFER (lowest-risk; recommended if the bar is "no adaptor fatal").**
   Ship this change as-is (adaptor gap closed; A2 is a follow-on, exactly as the
   adaptor gap was a follow-on to S3-5). Archive. Open a follow-on change for
   `NEO-AOT-FIELDINIT-NRE` (the recommended fix below). The exit-0/2-on-full-
   TestCases bar then lands with the follow-on.

2. **CLOSE A2 NOW via the clean, spec-preserving fix (recommended if the bar is
   "exit 0/2 now").** Amend THIS change's scope to also touch `ILType.cs` (drop
   the "no ILType change" Non-Goal for this one site): make
   `ILType.InitializeFields` throw `TypeLoadException("Cannot resolve field
   type: ...")` when `appdomain.GetType` returns null (mirroring the adaptor
   sites at `ILType.cs:1505/1568/1593`), AND add `_ = type.TotalPrimitiveSize;`
   to the `CompileCore` pre-filter. Then the EXISTING `TypeLoadException` catch
   skips the anonymous type (exit 2) — NO broadened catch, NO spec-scenario
   violation. Re-run the full-TestCases smoke; if MORE field-init failures
  surface, they now throw TLE and skip too. (Likely other open-generic/anon
   types follow the same pattern — enumerate at apply.)

3. **CLOSE A2 NOW via a broadened catch (NOT recommended).** Add
   `_ = type.TotalPrimitiveSize;` to the pre-filter AND broaden the catch to
   skip field-init NREs. This REQUIRES rewording the spec scenario "a non-
   adaptor type-init failure stays a loud fatal" (it would no longer hold for
   NREs). Weaker signal than Option 2.

**Implementer's recommendation: Option 1** (the change's scope is the adaptor
gap; A2 is a clean, separable follow-on). If the LEAD needs exit-0/2 NOW,
Option 2 is the clean fix (it preserves the narrow-TLE-catch invariant).

## Eliminated hypotheses / dead ends

- "Register the built-in adaptors in the CLI" (the S3-5 planner's guess, recorded
  on the deferred row): NO-OP — the AppDomain ctor already registers the only
  two built-ins; the dump proves no built-in-adaptor type fatals. Fix A is a no-op.
- "Register the HARNESS adaptors in the CLI": REJECTED — would couple a test-
  harness adaptor set into a generic tool. The dump-gate's verdict is graceful-
  SKIP (Design D3). Shipped.
- "The A2 NRE is an adaptor issue in disguise": DISPROVEN — `AdaptorProbe` and
  the `TestClass2`-inheriting TestCases types are skipped cleanly by the pre-
  filter; the A2 type is an anonymous type with NO CLR base needing an adaptor.
  Its failure is field-type resolution on an open-generic definition.
- "Catch the A2 NRE in the pre-filter by also triggering field init": the pre-
  filter triggering `TotalPrimitiveSize` REPRODUCES the A2 NRE there (confirmed
  via the diag), but catching it needs a broadened catch (spec-violating) — so
  the diag was reverted and A2 deferred.

## Files (exact)

Changed:
- `ILRuntime/Runtime/NeoAOT/NeoCompiler.cs` — `CompileCore` pre-filter +
  `compilableTypes` routing + `MakeTypeSkip` (Neo-only).
- `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep25ClrAdaptorCheck.cs` — NEW
  (self-check, `#if ENABLE_NEO_MODE && DEBUG`).
- `NeoClrProbe/AdaptorProbe.cs` — NEW (the two probe types).
- `ILRuntimeTestCLI/Program.cs` — the `NeoStep25ClrAdaptor` CLI hook (mirrors
  `NeoStep25S3ClrEnum`; computes probe + ref paths from the TestCases path).
- `openspec/changes/neo-step25-clr-adaptor/tasks.md` — checkboxes + apply-notes
  (STATUS banner + A1 + A2 + OQ confirmations).
- `.trae/documents/neo-deferred-items.md` — STEP-25-CLR-ADAPTOR RESOLVED +
  NEW `NEO-AOT-FIELDINIT-NRE` row.

UNCHANGED (per design): `ILRuntimeNeoCompiler/Program.cs`, `ILType.cs`,
`AppDomain.cs`, `NeoAssemblyWriter.cs`, the `.neo` format/loader, the JIT, the
optimizer, `ExecuteNeo`.

## sessionHandoff pointer

- This change: `openspec/changes/neo-step25-clr-adaptor/` (`design.md`
  authoritative; `tasks.md` has the STATUS banner + A1/A2 apply-notes).
- Deferred follow-on: `NEO-AOT-FIELDINIT-NRE` in
  `.trae/documents/neo-deferred-items.md`.
- Prior context: `openspec/changes/archive/2026-07-08-neo-step25-s3-clr-registration/`
  (S3-5, which surfaced this adaptor gap; its durable finding #1 / apply-note A1
  is closed HERE for the adaptor scope).
