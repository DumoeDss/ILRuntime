# Review Report — neo-step24-ilrt-neoc (Step 24 `ilrt_neoc` CLI)

> Adversarial non-author code review. Reviewer != implementer (HARD rule honored).
> Method: read proposal/design/tasks/planning-context + handoff §4; read every Step-24
> artifact; reproduce the V1-A + V1-B gates + the regression smokes; construct
> adversarial probes (full-`TestCases.dll` CLI run; Legacy-neutral build; sample
> emission). The handoff's lesson applied: a green smoke + a passing self-check do
> NOT prove correctness -- probes were constructed to falsify the load-bearing claims.

**Branch:** `features/object-model-overhaul`
**Verdict:** **APPROVE-WITH-FINDINGS** — no Blocker, no Major code defect. Two Major
items are **documentation / coverage-scope accuracy** (no V1 code change required);
the rest are Minor/Trivial. The code is correct for its stated V1 scope (BCL-only-ref
assemblies). All gates reproduce green.

---

## Summary of verification performed

| Check | Command / action | Result |
|---|---|---|
| Build CLI runtime (`Debug_Neo`) | `dotnet build ILRuntimeTestCLI -c Debug_Neo` | 0 errors |
| Build tool standalone (`Debug_Neo`) | `dotnet build ILRuntimeNeoCompiler -c Debug_Neo` | 0 errors (both TFMs) |
| Build `TestCases` (`Debug`) | `dotnet build TestCases -c Debug` | 0 errors |
| NeoStep smoke | `... NeoStep` | **205/205, 0 failed** |
| V1-A gate | `... NeoStep24CliRoundtrip` | **5/5 cells PASS** |
| Step-23 roundtrip (regression) | `... NeoStep23Roundtrip` | **15/15** (comparator widening did not regress) |
| Legacy-neutral | `dotnet build ILRuntimeTestCLI -c Debug` (NeoCompiler compiles out) | **0 errors** |
| V1-B literal CLI | `ilrt_neoc Step24V1BSample.dll out.neo` | **exit 0**, magic bytes present, process exits cleanly |
| **Adversarial: full `TestCases.dll`** | `ilrt_neoc TestCases.dll out.neo` | **exit 1 FATAL** (see Major-1) — does NOT stall |

---

## Findings

### MAJOR-1 — The full-`TestCases.dll` V1-B deferral rationale is mis-diagnosed (documentation accuracy; no code change)

**Files:** `tasks.md` §5.1 (the "NOTE: the full-`TestCases.dll` V1-B is NOT run to
completion -- it stalls on delegate/async methods ... a JIT path that loops does not
throw. This is a Step 19 gap" claim) and `planning-context.md` OQ2 resolution (the same
"stalls on incomplete delegate/async JIT paths" claim).

**Observation (adversarial probe — the one the brief told me to construct):** I ran
the shipped `ilrt_neoc.exe` on the real `TestCases.dll` with no refs:

```
ilrt_neoc: FATAL: serializer failure: Cannot find Type:ILRuntimeTest.TestFramework.TestCLREnum
  inner: KeyNotFoundException: Cannot find Type:ILRuntimeTest.TestFramework.TestCLREnum
EXIT=1   (0-byte out.neo)
```

The run **completes in ~1-2 minutes; it does NOT stall.** The documented rationale is
wrong on every count:

1. **No stall, no JIT loop.** The process terminates (exit 1). The historical "stall"
   was almost certainly the **pre-`Dispose`-fix exit-hang** (the foreground
   `AsyncJITCompileWorker` thread at `AppDomain.cs:79`, which I confirmed starts a
   non-`IsBackground` thread that only exits on `Dispose()`). That hang is now fixed
   (see Minor note under the hang-fix verification) — so the "stall" framing is stale.
2. **Not a delegate/async gap.** Delegate-bearing types compile cleanly:
   `TestCases.DelegateInnerTest/<>c..ctor`, `TestCases.DelegateContainer..ctor` etc.
   all appear in the JIT output with no error (24 JIT-log lines for delegate types).
   Step-19 delegate `Newobj` is unimplemented for *invocation*, but the *compile* path
   does not loop on them here.
3. **The real failure is a non-BCL CLR type-resolution gap.** `TestCLREnum` is a **CLR
   enum** in `ILRuntimeTestBase/TestFramework/TestCLREnum.cs:3` — a host CLR assembly
   the standalone CLI never registers. `AppDomain.GetType` (`AppDomain.cs:1409`) throws
   `KeyNotFoundException`. This is precisely design **D7's documented "V1 = BCL refs
   only" boundary** (non-BCL CLR refs unresolvable; robust IL-vs-CLR classification is
   Step 25).
4. **The skip-with-warning / partial-`.neo` (exit 2) contract does NOT engage.** The
   `KeyNotFoundException` escapes `CompileCore` via the `NeoAssemblyWriter.Write` path
   (the outer `catch (Exception ex) { throw new NeoCompilerFatal("serializer failure: ...", ex); }`
   at `NeoCompiler.cs:150-153`), so the whole run fatals (exit 1, 0-byte output). The
   design's claim that "report-and-skip lets the usable subset ship" (`proposal.md`
   D5 / `design.md` D6) therefore does **not** hold for any assembly containing a
   non-BCL CLR type ref — the usable subset does NOT ship; the serialize aborts.

**Distinguishing Step-19 gap vs Step-24 bug (the brief's question):** it is **neither**.
It is not a Step-19 delegate/async JIT gap (delegates compile) and not a Step-24
skip-policy bug (the per-method skip policy is fine for per-method NIEs; the failure is
a whole-assembly serialize-time type resolution that the skip policy structurally cannot
catch). It is a Step-23-serializer/Step-25-CLR-resolution scope limit, surfaced as a
fatal — consistent with D7's deferral.

**Recommended fix (documentation only — no V1 code change):** correct the rationale in
`tasks.md` §5.1 and `planning-context.md` OQ2 to:

> The full-`TestCases.dll` V1-B fatals with `Cannot find Type:
> ILRuntimeTest.TestFramework.TestCLREnum` (a non-BCL CLR enum in the host test
> framework; the standalone CLI registers no host CLR assemblies — the D7 "V1 = BCL
> refs only" boundary). The historical "stall" was the pre-`Dispose` exit-hang (foreground
> JIT-worker thread), now fixed. The skip-with-warning contract does not cover non-BCL
> CLR type refs (a serialize-time fatal). CLR-ref robustness + the usable-subset-ships
> contract are Step 25.

**Severity: Major** — the current rationale would misdirect a future session into
hunting a non-existent Step-19 delegate/async JIT loop. The work-deferral itself is
correct; only the reason is wrong.

---

### MAJOR-2 — V1-A does NOT exercise the CLI's module-filter / ref-resolution / Dispose path (the load-bearing coverage question)

**Files:** `NeoCompiler.cs:52-160` (`Compile(string,...)` — the CLI-only logic) vs
`NeoCompiler.cs:170-185` (`Compile(IReadOnlyList<ILType>, Stream)` — the V1-A entry).
Both converge on `CompileCore` (`NeoCompiler.cs:197-262`).

**Observation:** The two public overloads share `CompileCore`, so V1-A **does**
exhaustively prove the shared core (enumeration → partition → template-capture →
per-method force-compile → `Write`). But V1-A **bypasses** everything CLI-specific:

| CLI-only step (`Compile(string,...)`) | Exercised by V1-A? |
|---|---|
| Cecil `DefaultAssemblyResolver` + `AddSearchDirectory` | No |
| `ModuleDefinition.ReadModule(path, ReaderParameters)` | No |
| `appdomain.InitializeFromModule(inputModule)` | No |
| Per-ref `appdomain.LoadAssembly(rs)` try/catch heuristic | No |
| **`it.TypeDefinition.Module == inputModule` filter** | **No** (V1-A hands types explicitly) |
| `finally { appdomain.Dispose(); }` (hang-fix) | No |

V1-B (the `Step24V1BSample` run, which I reproduced) covers the **happy path** of all
six on a self-contained DLL with **zero IL refs and only BCL refs** — so the module
filter is *trivially* correct there (every type is an input type; there is nothing to
exclude). **No gate stresses the module filter's *exclusion* correctness** (V1-A bypasses
it; V1-B has no IL ref to exclude) and no gate exercises the ref `LoadAssembly`
heuristic with a real IL ref.

The `NeoStep24CliRoundtripCheck` header's claim — "proves the driver wiring (enumeration
+ force-compile + template capture + serialize + **the input-set filter**)" — is accurate
for `CompileCore` and the *explicit-types* filter, but should not be read as covering the
CLI's *module* filter or ref-resolution.

**Assessment:** Acceptable for V1 — cross-IL-assembly ref-filtering is explicitly a
Step-25 deferral (design D7), and V1-A does cover the bulk of the logic. The brief's
"the V1 gate is weaker than claimed" concern is **real but bounded**: V1-A proves
`CompileCore`; the CLI file-path path is proven only by the V1-B happy-path smoke (which
I confirmed: exit 0, valid `.neo`, clean exit). **No proven bug** — the module filter is
a simple reference-equality `==` (correct by inspection, since `inputModule` is the exact
object registered by `InitializeFromModule`).

**Recommended fix (documentation; optional future gate):**
1. Qualify the self-check docstring + planning-context: "`CompileCore` is V1-A-gated;
   the CLI file-path path (resolver / module-read / `InitializeFromModule` / ref-load /
   module filter / `Dispose`) is V1-B smoke-only."
2. (Defer to Step 25 if not trivial) A cheap stress gate: feed the CLI an input that
   references a second IL assembly and assert the ref's types are **absent** from the
   `.neo` (proves the module filter excludes).

**Severity: Major** (the brief's load-bearing question) **with acceptable V1 scope** —
flagged so the Step-25 owner knows the module-filter/ref path is not yet gate-tested.

---

### MINOR-1 — Multi-generic-param + generic-method-on-generic-type capture is unverified

**File:** `NeoCompiler.cs:277-310` (`CaptureTemplate`).

**Observation:** The synthesis loop handles N params by construction
(`for (int i=0; i<GenericParameterCount; i++) captureArgs[i]=IntType`), and
`IsCaptureEligible` (`GenericMethodTemplate.cs:411-421`) accepts all-int. But the V1-A
probe has only **1-param** generics (`GenericProbe<T>`, `GenericNested<U>`). Two cases
are unexercised:
- a generic method with **2+** generic params;
- a generic method on an **open generic type** (e.g. `Outer<T>.Bar<U>()`) — here
  `MakeGenericMethod([int])` leaves the class param `T` unbound, and whether
  `InitCodeBody`'s capture hook handles an open class param is unverified.

**Assessment:** Low risk for V1 (neither the probe nor the sample has such a method) and
the design's D4 reasoning ("the front-half is T-invariant") covers it in principle. But
it is a genuine unverified edge.

**Recommended fix:** Add one 2-param generic method and one generic method on a generic
type to `NeoStep24CliProbe` (two lines), rebuild, re-run V1-A — cheap. If capture fails,
the method is silently skipped and Cell3/Cell5 counts shift, surfacing it.

**Severity: Minor** (test-gap / latent risk).

---

### MINOR-2 — Compiler-generated / synthetic types are emitted into the `.neo`

**Observation:** The `Step24V1BSample` (1 declared class `Probe`) reports
"compiled 3 methods, 1 templates, **2 types**". The 2nd type is a compiler-generated
synthetic (e.g. `<PrivateImplementationDetails>`). The driver emits every type from
`module.GetTypes()` with no synthetic-name filter.

**Impact:** Harmless for V1 (the Step-25 loader would carry an extra typedef), but it is
noise and will balloon the `.neo` for real assemblies (which always synthesize types).

**Recommended fix (optional, defer to Step 25):** skip types whose name starts with
`<>` / `<PrivateImplementationDetails>` or whose `TypeDefinition.IsSpecialName` /
compiler-generated flag is set, in `CompileCore`.

**Severity: Trivial.**

---

### MINOR-3 — Enumeration of private / virtual / abstract methods is unverified by the probe (covered by API by construction)

**Observation:** `ILType.InitializeMethods` (`ILType.cs:1554`) iterates
`definition.Methods` with **no visibility filter** — so private/internal/virtual/abstract
methods ARE enumerated by construction. The probe, however, declares only public-static
+ public-instance + ctors, so it does not *exercise* non-public method compilation through
the driver.

**Assessment:** Low risk — the API covers it, and the 205/205 NeoStep smoke compiles
many private/virtual methods through the same JIT. Not a V1-gating gap.

**Severity: Trivial.**

---

### MINOR-4 — `NeoCompilerResult.IsComplete` has an unreachable `Skipped == null` branch

**File:** `NeoCompiler.cs:347-348`.

```csharp
public List<MethodSkip> Skipped = new List<MethodSkip>();
public bool IsComplete => Skipped == null || Skipped.Count == 0;
```

`Skipped` is field-initialized and never nulled, so `Skipped == null` is dead. Harmless;
pure style. The CLI also null-guards (`result.Skipped != null ? ...`), consistent.

**Severity: Trivial.**

---

### Verified-clean items (explicitly checked, no finding)

- **The `NeoCompiler` seam correctness.** Partition is correct: a generic **definition**
  (`IsGenericInstance == false && GenericParameterCount > 0`) routes to `templates[]`
  via `CaptureTemplate`; a generic **instance** is skipped (`if (ilm.IsGenericInstance)
  continue;`); non-generic routes to `methods[]` (`NeoCompiler.cs:223-248`). A generic
  definition can NEVER reach `methods[]`. Exit codes 0/2/1 are correct
  (`Program.cs:93`, `:75`, `:80`). Enumeration covers nested types (Cecil flattens;
  `module.GetTypes()`), ctors + static `.cctor` (`GetConstructors()`), and all
  non-ctor methods (`GetMethods()`).
- **The AppDomain.Dispose hang-fix.** Confirmed necessary AND correct:
  `AsyncJITCompileWorker` (`AsyncJITCompileWorker.cs:16-21`) starts a **foreground**
  thread (no `IsBackground=true`) that loops on an `AutoResetEvent` until `Dispose()`
  sets `exit` (`:31-35`). The file-path overload's `try/finally { appdomain.Dispose(); }`
  (`NeoCompiler.cs:156-159`) is the fix. Verified: (a) the V1-B CLI run **exits cleanly**
  (no leftover process); (b) it disposes a **fresh** AppDomain the CLI itself created
  (`:69`) — the runtime's long-lived AppDomains are unaffected; (c) the host-side
  `Compile(IReadOnlyList<ILType>, Stream)` overload does **not** dispose (it reuses the
  caller's AppDomain — `:170-185`, no `Dispose()`). All three sub-claims hold.
- **The 3 comparator visibility widenings.** `MethodDefsEqual` / `TemplatesEqual` /
  `TypeDefsEqual` are `internal static string` in `NeoStep23RoundtripCheck.cs:306/478/443`
  (pure visibility — `OpCodeRsEqual` was already `internal`). **Step-23 roundtrip still
  15/15** after the widening (reproduced) — no logic change. The file is
  `#if ENABLE_NEO_MODE && DEBUG` so it compiles out of plain `Debug` regardless.
- **Additive + Legacy-neutral.** `NeoCompiler.cs` is `#if ENABLE_NEO_MODE` (compiles out
  of plain `Debug` — reproduced 0 errors). `NeoCompilerResult` / `MethodSkip` /
  `NeoCompilerFatal` are inside the same `#if`. The tool project is Neo-only (a plain-Debug
  stub prints a clear error). The only non-`#if`-gated runtime-assembly edits are the 3
  comparator widenings in `NeoStep23RoundtripCheck.cs`, which is itself `#if`-gated.
- **The new tool project builds standalone**, NOT in the sln; `ENABLE_NEO_MODE` is set on
  `Debug_Neo`/`Release_Neo` (`ILRuntimeNeoCompiler.csproj:15-21`); `ProjectReference` to
  `ILRuntime.csproj` + Cecil `Reference` hints present. Builds 0 errors for both TFMs.

---

## Step24V1BSample keep/delete recommendation

**DELETE** (LEAD decides at commit). Rationale:
- Referenced by **no committed automation** — only by `planning-context.md`, `tasks.md`,
  and its own source (verified by grep). The V1-B "smoke" it supports is a **manual**
  one-off invocation, not an automated gate.
- The load-bearing gate (V1-A `NeoStep24CliRoundtripCheck`) is automated and green; the
  manual V1-B *result* is already recorded in `planning-context.md` OQ2.
- A stray project that is not in the sln adds build-surface noise with zero gate value.
- `planning-context.md:419` itself says "can be deleted post-ship."

If the LEAD wants a reproducible manual-smoke fixture, keep it but mark it as such;
**default = delete.**

---

## Accepted-known items (recorded, not blocking)

1. **Full-`TestCases.dll` V1-B deferral** — ACCEPTED as a justified deferral (the CLI
   cannot resolve TestCases.dll's non-BCL CLR refs; Step-25 scope per D7). **BUT the
   rationale must be corrected** (Major-1): the failure is a `KeyNotFoundException` for
   a CLR enum, not a Step-19 delegate/async JIT loop stall, and the skip-with-warning
   contract does not cover it.
2. **V1-A-vs-CLI coverage limitation** — ACCEPTED as consistent with the V1 scope
   (Major-2): V1-A gates `CompileCore`; the CLI file-path/module-filter/ref path is
   V1-B-smoke-only. No proven bug.

---

## Verdict

**APPROVE-WITH-FINDINGS.**

- **Blocker open:** none.
- **Major open:** 2 — both are **documentation / coverage-scope accuracy**, not code
  defects. They do NOT block the V1 ship; they require (a) correcting the
  full-`TestCases.dll` deferral rationale (Major-1) and (b) qualifying the V1-A coverage
  claim w.r.t. the CLI module-filter/ref path (Major-2). Neither demands a V1 code
  change. The Step-25 owner should pick up both as scope notes.
- The Step-24 code is **correct for its stated V1 scope** (BCL-only-ref assemblies),
  additive, and Legacy-neutral. All automated gates reproduce green (Neo 205/205,
  NeoStep23Roundtrip 15/15, V1-A 5/5, Legacy 0 errors). The V1-B literal CLI reproduces
  exit 0 + valid `.neo` + clean exit. The full-`TestCases.dll` run is the one probe that
  falsified a documented claim — and it falsified the *rationale*, not the *code*.

**Return to LEAD:**
- Verdict: **APPROVE-WITH-FINDINGS** (no Blocker; no Major code defect).
- Findings by severity: Major-1 (deferral rationale mis-diagnosed — doc fix),
  Major-2 (V1-A coverage scope — doc/qualification), Minor-1 (multi-param/generic-on-
  generic-type capture unverified), Minor-2 (synthetic types emitted), Minor-3 (non-public
  method enumeration unverified by probe), Minor-4 (dead `Skipped == null` branch).
- Blocker/Major open gating the review-loop: **none open as code**; the two Majors are
  doc/scope and can be addressed in-flight.
- V1-A-vs-CLI coverage assessment: V1-A proves `CompileCore` exhaustively; the CLI
  module-filter + ref-resolution + Dispose path is V1-B happy-path smoke only (no IL ref
  to exclude) — acceptable for V1 per D7, but the module-filter exclusion is unverified
  by any gate.
- Full-`TestCases.dll` deferral verdict: **NOT a Step-24 bug and NOT a Step-19 gap.** It
  is a non-BCL CLR type-resolution fatal (D7 Step-25 scope). The original "stall" was the
  pre-`Dispose` exit-hang (now fixed). Deferral justified; rationale must be corrected.
- Step24V1BSample: **recommend DELETE** (no committed automation references it; planning-
  context already says "can be deleted post-ship").
