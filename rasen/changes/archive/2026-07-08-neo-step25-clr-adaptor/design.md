# Design: neo-step25-clr-adaptor

> Technical design for the STEP-25-CLR-ADAPTOR TRUE-COMPLETION slice: make the
> standalone `ilrt_neoc` AOT CLI ROBUST to IL types whose CLR-class base (or
> CLR interface) needs a `CrossBindingAdaptor` that is not registered in the
> compile AppDomain. Grounded in the dump-gate reproduced on HEAD, line-cited.
> Legacy is the REFERENCE, not a target.

## Context

Step 24 shipped the `ilrt_neoc` standalone precompile CLI. Step 25 S3-5 closed
the host-CLR-type-RESOLUTION gap (`Cannot find Type:TestCLREnum`): the CLI now
`Assembly.LoadFrom`-es each reference assembly so the AppDomain CLR fallback
resolves host CLR types as `CLRType` (S3 design D1, in `NeoCompiler.cs:138-154`
on HEAD). With that fix in place, the full-`TestCases.dll` compile gets PAST
every `TestCLREnum` reference and advances to a NEW, deeper fatal at the
serializer stage: `TypeLoadException: Cannot find Adaptor
for:ILRuntimeTest.TestFramework.TestClass2`. This is the S3-5 durable finding
#1 (its apply note A1) -- a DISTINCT gap from host-CLR-type resolution. An
enum is a primitive (no adaptor); a CLR CLASS base needs a `CrossBindingAdaptor`.
The standalone CLI registers the built-in adaptors but none of the test-harness-
specific ones, so any IL type inheriting a harness-adaptor CLR class fatal-
aborts the whole compile (exit 1, no `.neo`). This change closes that gap by
making the CLI robust: RESOLVE built-in-adaptor types, SKIP harness-adaptor
types gracefully.

## The dump-gate verdict (binding, reproduced on HEAD)

The dump-gate's three questions, answered by reproduction on HEAD `6ee984ea`
(`ilrt_neoc` built `Debug_Neo`, `TestCases` built `Debug`, both 0 errors).

### Q1. The EXACT failing adaptor-lookup site + the call chain

**Reproduction:**
```
ilrt_neoc TestCases.dll out.neo ILRuntimeTestBase.dll   ->  exit 1
ilrt_neoc: FATAL: serializer failure: Cannot find Adaptor for:ILRuntimeTest.TestFramework.TestClass2
  inner: TypeLoadException: Cannot find Adaptor for:ILRuntimeTest.TestFramework.TestClass2
```

**Throw site (THREE sibling sites in `ILType.cs`, all keyed on
`appdomain.CrossBindingAdaptors`):**

1. `ILRuntime/CLR/TypeSystem/ILType.cs:1593` -- the NON-GENERIC CLR-base case
   (the TestClass2 fatal):
   ```
   baseType = appdomain.GetType(definition.BaseType, this, null);   // :1573
   if (baseType is CLRType) {                                       // :1574
       ... // Enum/object/ValueType/MulticastDelegate skip          // :1576-1584
       CrossBindingAdaptor adaptor;                                 // :1587
       if (appdomain.CrossBindingAdaptors.TryGetValue(baseType.TypeForCLR, out adaptor))  // :1588
           baseType = adaptor;                                      // :1590
       else
           throw new TypeLoadException("Cannot find Adaptor for:" + baseType.TypeForCLR.ToString());  // :1593
   }
   ```
2. `ILType.cs:1568` -- the GENERIC-INSTANCE CLR-base case (e.g.
   `class X : ClassInheritanceTest2<X>`), inside the `specialProcess` branch
   of `InitializeBaseType`: `throw new TypeLoadException("Cannot find Adaptor
   for:" + definition.BaseType.FullName)` when no registered adaptor's
   generic-construction matches (`ILType.cs:1541-1568`).
3. `ILType.cs:1505` -- the CLR-INTERFACE case, inside `InitializeInterfaces`:
   `throw new TypeLoadException("Cannot find Adaptor for:" +
   interfaces[i].TypeForCLR.ToString())` when an IL type implements a CLR
   interface with no registered adaptor (`ILType.cs:1486-1505`).

**Trigger (lazy):** all three throws live inside `InitializeBaseType`
(`ILType.cs:1512`) / `InitializeInterfaces` (`ILType.cs:1486`), which run
LAZILY via the `BaseType` getter (`ILType.cs:154-159`), the `FirstCLRBaseType`
getter (`ILType.cs:254-259`), and the `FirstCLRInterface` getter
(`ILType.cs:264-269`). The init is memoized by `baseTypeInitialized` /
`interfaceInitialized`; a type that throws STAYS uninitialized and re-throws
on every subsequent getter access.

**Call chain to the fatal:**
- `NeoCompiler.CompileCore` (`NeoCompiler.cs:215`) enumerates `inputTypes`,
  calling `type.GetMethods()` / `type.GetConstructors()` (`NeoCompiler.cs:227-
  235`) -- which (transitively, via `InitializeMethods`) and/or the subsequent
  `NeoAssemblyWriter.Write` touch `type.BaseType`.
- `NeoAssemblyWriter.BuildTypeDefRecord` accesses `type.BaseType` at
  `NeoAssemblyWriter.cs:755` (`type.BaseType is ILType bt ? ...`) and
  `BuildInterfaces(type, b)` at `:762` -- so the serializer's TypeDef emission
  ALSO triggers the lazy adaptor resolution.
- Whichever access fires first for a harness-adaptor type -> `InitializeBaseType`
  -> `CrossBindingAdaptors.TryGetValue` misses -> `TypeLoadException` at
  `ILType.cs:1593` (or `:1568` / `:1505`).
- The throw propagates OUT of the per-method try/catch (which wraps only
  `ilm.BodyRegister` at `NeoCompiler.cs:257-265`) and OUT of `CompileCore` to
  the outer wrapper at `NeoCompiler.cs:168-170`:
  `throw new NeoCompilerFatal("serializer failure: " + ex.Message, ex)`.
- The CLI maps `NeoCompilerFatal` to exit 1 (`Program.cs:70-76`).

### Q2. The BUILT-IN vs HARNESS-specific adaptor audit

**BUILT-IN adaptors (ship with ILRuntime; registered in the AppDomain ctor
`AppDomain.cs:237`/`:244`, so present in EVERY AppDomain including the
standalone CLI's `new AppDomain()` at `NeoCompiler.cs:69`):**

| Adaptor | Registered | Resolves an IL type inheriting |
|---|---|---|
| `Adapters.AttributeAdapter` | `AppDomain.cs:237` | `System.Attribute` |
| `Adapters.ExceptionAdaptor` (Step 14) | `AppDomain.cs:244` | `System.Exception` |

These RESOLVE TODAY in the standalone CLI -- no additional registration is
needed. An IL `class X : System.Exception` (the Step-14 test shape) compiles
cleanly because the ctor already registered `ExceptionAdaptor`. **Fix A
(register built-ins) is a NO-OP: already satisfied by the ctor.**

**HARNESS-specific adaptors (test-only; registered by `ILRuntimeHelper.Init`
in `ILRuntimeTestBase/Adapters/helper.cs:22-29`, which `TestSession.cs:65`
calls in the test harness ONLY -- the standalone CLI never calls it):**

| Adaptor (helper.cs line) | Resolves an IL type inheriting |
|---|---|
| `ClassInheritanceTestAdaptor` (:22) | `ClassInheritanceTest` |
| `InterfaceTestAdaptor` (:23) | a CLR interface (the interface-adaptor test) |
| `TestClass2Adapter` (:24) | `TestClass2` |
| `TestClass3Adaptor` (:25) | `TestClass3` |
| `TestClass4Adaptor` (:26) | `TestClass4` |
| `IDisposableAdapter` (:27) | `System.IDisposable` |
| `ClassInheritanceTest2Adaptor` (:28) | `ClassInheritanceTest2<T>` |
| `IAsyncStateMachineClassInheritanceAdaptor` (:29) | `IAsyncStateMachine` |

These live in the `ILRuntimeTestBase` test-framework assembly. The generic CLI
(`ILRuntimeNeoCompiler`) does NOT reference `ILRuntimeTestBase` and MUST NOT
(the CLI is a generic tool; coupling a test-harness adaptor set is wrong).
Types needing one of these adaptors CANNOT be resolved by the generic CLI ->
they MUST be skipped gracefully. The `TestCases.dll` skip set (from a grep of
`TestCases/`) spans at least: `InheritanceTest16SubCls : TestClass2`,
`InheritanceTest16SubCls2 : TestClass4`, `CrossClass : TestClass3`,
`TestCls5/6/7 : TestClass2`, `TestCls/2 : ClassInheritanceTest`,
`TestCls4 : ClassInheritanceTest2<TestCls4>`, `GenericInheritanceTestCls<T> :
ClassInheritanceTest`, several `: IDisposable` types, plus interface-based
ones -- `TestClass2` is merely the FIRST fatal. The implementer's skip report
will enumerate the exact set at apply time.

### Q3. The fix surface: register-builtins (no-op) + skip-harness-gracefully

**Fix A (register built-ins): NO-OP.** The AppDomain ctor already registers
the only two built-in adaptors (`AttributeAdapter` + `ExceptionAdaptor`). The
compile AppDomain is constructed by `new AppDomain()` (`NeoCompiler.cs:69`),
which runs the ctor, so both built-ins are present. An IL type whose CLR base
needs a built-in adaptor resolves TODAY. There is no third built-in adaptor to
register.

**Fix B (skip harness-specific gracefully): the entire fix surface.** The
generic CLI cannot resolve a type needing a harness-specific adaptor (it would
have to reference `ILRuntimeTestBase`), so the robust contract is to SKIP the
whole type: record it in the skip report, omit all its methods, write the
`.neo` for the survivor subset, and return exit 2 (partial) -- NOT exit 1
(fatal). This is the SAME additive-skip contract as the existing per-method
try/catch in `CompileCore` (`NeoCompiler.cs:257-265`), lifted from METHOD
granularity to TYPE granularity.

## Goals / Non-Goals

**Goals:**
- `ilrt_neoc TestCases.dll out.neo <refs>` produces a VALID `.neo` (magic
  `0x494C524E`) with exit code 0 (clean) or 2 (partial, for unrelated
  unimplemented-op skips AND for harness-adaptor type skips) and emits NO
  `Cannot find Adaptor` fatal on stderr.
- An IL type whose CLR base/interface needs an UNREGISTERED (harness-specific)
  adaptor is SKIPPED at the TYPE level (all its methods omitted), recorded in
  the skip report with a clear type-level marker, and the compile continues.
- An IL type whose CLR base needs a BUILT-IN adaptor (Exception, Attribute)
  KEEPS resolving (NOT skipped) -- the ctor's registration is unchanged.
- The S3-5 TestCLREnum shape still works (no regression -- the host-CLR-type
  resolution path is untouched).
- The fix is Neo-only and Legacy-neutral; `Program.cs` is byte-identical.

**Non-Goals:**
- Coupling ANY test-harness-specific adaptor into the generic CLI. The
  harness adaptor set is test-only; the generic CLI skips types needing one.
- Resolving harness-adaptor types (compiling their methods). A skipped type's
  methods are omitted; at `.neo` load time the loader's existing additive
  skip (a method/type it cannot bind falls back to JIT) handles them.
- A `.neo` format change, a `.neo` loader change, or any `AppDomain` /
  `NeoAssemblyWriter` change. The skip is primarily a `CompileCore` concern.
  (SCOPE AMENDMENT, fixer-a2 A2 closure: ONE narrow `ILType` change IS in
  scope -- the Neo-gated `TypeLoadException` throw in `InitializeFields` for
  an unresolvable field type; see D5. No other `ILType` change, and no
  `AppDomain` / `NeoAssemblyWriter` / loader / format change.)
- Changing which adaptors are BUILT-IN (shipping a new built-in adaptor for
  e.g. `IDisposable`). That is a product decision, not a robustness fix; this
  change only ensures the CLI does not FATAL on the absence.
- Multi-hotfix-assembly cross-refs (the S3 deferral) -- unchanged.

## Decisions

### D1. The fix: a per-type pre-filter in `CompileCore` that eagerly triggers the adaptor resolution and drops throwers

`CompileCore` (`NeoCompiler.cs:215`) currently iterates `inputTypes` directly:
`GetMethods`/`GetConstructors` -> per-method partition + force-compile (per-
method try/catch) -> `writer.Write(inputTypes, ...)`. The adaptor throw fires
during that iteration (or inside `Write`), OUTSIDE the per-method try/catch,
and propagates to the `NeoCompilerFatal("serializer failure")` wrapper.

The fix inserts a PRE-FILTER at the TOP of `CompileCore`, BEFORE any
enumeration, that eagerly triggers the lazy adaptor resolution for each type
inside a per-type try/catch, builds a SURVIVOR list, and routes ONLY the
survivors to the existing per-method loop AND to `Write`:

```
var compilableTypes = new List<ILType>();
foreach (var type in inputTypes) {
    if (type == null) continue;
    try {
        // Force the lazy CLR-base/interface adaptor resolution NOW (the
        // lookup at ILType.cs:1505/1568/1593). A type whose CLR base/interface
        // needs an UNREGISTERED cross-binding adaptor throws TypeLoadException
        // here -- BEFORE method compile or serialization -- so the WHOLE type
        // can be skipped cleanly and the .neo still written for the survivors.
        _ = type.FirstCLRBaseType;   // triggers InitializeBaseType (sites 1568/1593)
        _ = type.FirstCLRInterface;  // triggers InitializeInterfaces (site 1505;
                                     // also touches BaseType internally at :1510)
        compilableTypes.Add(type);
    } catch (TypeLoadException ex) {
        result.Skipped.Add(MakeTypeSkip(type, ex));   // harness-adaptor skip
    }
}
// compilableTypes replaces inputTypes for BOTH the per-method loop AND Write.
```

Then the existing per-method loop iterates `compilableTypes` (not `inputTypes`),
and `writer.Write(compilableTypes.ToArray(), methods.ToArray(), templates.ToArray(),
outputStream)` replaces the current `writer.Write(inputTypes, ...)`. Because
the init is MEMOIZED, a survivor's later `BaseType` access (in the loop and in
`Write` at `NeoAssemblyWriter.cs:755`/`:762`) does NOT re-throw -- the init
already completed successfully. `result.TypesCompiled` is set to
`compilableTypes.Count` (the emitted count, consistent with `MethodsCompiled`/
`TemplatesCaptured` being emitted counts).

**Why a pre-filter (not catching inside the loop / inside Write):**
- The throw can fire in TWO places -- the per-method loop (`GetMethods`)
  AND `Write` (TypeDef emission). Catching inside the loop leaves `Write`
  exposed (it iterates the same `inputTypes`); catching inside `Write` would
  require modifying `NeoAssemblyWriter` (violating "no serializer change") and
  cannot cleanly drop a single type mid-serialize. A pre-filter that produces
  a survivor list consumed by BOTH the loop and `Write` closes both doors with
  ONE mechanism and touches ONLY `CompileCore`.
- The pre-filter runs the EXACT resolution the compile would run moments later
  (the lazy init is the same code path); it merely runs it EARLIER and inside
  a try/catch. There is no behavior change for resolving types -- only the
  THROW path flips from fatal to skip.

**Why touch BOTH `FirstCLRBaseType` and `FirstCLRInterface`:** together they
trigger all three throw sites' initializers. `FirstCLRBaseType` ->
`InitializeBaseType` (sites 1568 + 1593). `FirstCLRInterface` ->
`InitializeInterfaces` (site 1505), which ALSO reads `BaseType` at
`:1510` (re-triggering `InitializeBaseType` if it somehow had not run). Touching
both is explicit and robust; touching only one could miss a site.

**Why catch `TypeLoadException` (not broader `Exception`):** the adaptor
absence throws `TypeLoadException` precisely (`ILType.cs:1505/1568/1593`).
Catching `TypeLoadException` is the narrow, intent-revealing scope: any type-
init `TypeLoadException` means "this type cannot be AOT-compiled in this
AppDomain" -> skip. A broader catch could mask a genuine compile bug as a skip;
the narrow catch keeps that signal loud (a non-`TypeLoadException` compile
failure still propagates to the existing per-method try/catch or the fatal
wrapper, unchanged).

### D2. The skip reuses the existing `Skipped` report + exit-2 path (no CLI change)

A type-level skip is recorded in the EXISTING `NeoCompilerResult.Skipped` list
(`List<MethodSkip>`, `NeoCompiler.cs:365`) via a new `MakeTypeSkip(ILType type,
Exception ex)` helper (mirrors the existing `MakeSkip` at `:330`, but the
`MethodDisplay` is a type-level marker: `"(type) " + type.FullName`). The
CLI's existing report printer (`Program.cs:88-92`, `SKIP <display>: <ex>:
<msg>`) and the exit-code logic (`Program.cs:93`, `result.IsComplete ? 0 : 2`
where `IsComplete => Skipped.Count == 0`) apply UNCHANGED. So a type-level
skip drives exit 2 with NO `Program.cs` edit -- honoring the S3 design D4
"`Program.cs` is byte-identical" principle and keeping the CLI generic.

A distinct `TypeSkip` list + `TypesSkipped` count was considered and REJECTED:
it would require a CLI change to print it and to fold into the exit code, for
no semantic gain (the existing `Skipped` list already drives exit 2 and is
human-readable with the `(type)` marker).

### D3. Built-in adaptors stay registered (Fix A is a no-op) -- documented, not coded

The design RECORDS that the AppDomain ctor registers the built-ins
(`AppDomain.cs:237`/`:244`), so the compile AppDomain (constructed via
`new AppDomain()` at `NeoCompiler.cs:69`) already has `ExceptionAdaptor` +
`AttributeAdapter`. No code change is made for built-ins. The dump PROVES this:
the only fatal on the full `TestCases.dll` is for a HARNESS-specific adaptor
(`TestClass2`); no built-in-adaptor type fatals. If a future input has an IL
type inheriting `System.Exception`, it resolves today and is NOT skipped (the
pre-filter's `FirstCLRBaseType` access finds `ExceptionAdaptor` in
`CrossBindingAdaptors` and succeeds).

### D4. Gating: Neo-only, Legacy-neutral, additive, no-coupling

- The pre-filter + `MakeTypeSkip` live inside `CompileCore`, which is in
  `NeoCompiler.cs` -- the whole file is `#if ENABLE_NEO_MODE`. Under plain
  `Debug` the file compiles out -> Legacy byte-identical.
- The fix touches ONLY `CompileCore` (a new pre-filter block + the survivor
  list routed to the loop and `Write`) + a new `MakeTypeSkip` helper. NO change
  to `AppDomain`, `NeoAssemblyWriter`, `ILMethod`, the JIT, the optimizer, the
  `.neo` format/loader, or `Program.cs`. (SCOPE AMENDMENT, fixer-a2: ONE Neo-
  gated `ILType.InitializeFields` TLE throw is added for the A2 closure; see
  D5. No other `ILType` change.)
- The fix does NOT reference `ILRuntimeTestBase` or any test-harness adaptor.
  It is GENERIC: it skips whatever type the compile AppDomain cannot adaptor-
  resolve, regardless of WHY (test-harness, a user app's own adaptor, etc.) --
  the robust contract for a generic tool.

### D5. A2 closure: a Neo-gated field-type `TypeLoadException` in `ILType.InitializeFields` + a `TotalPrimitiveSize` trigger in the pre-filter (fixer-a2)

The implementer's dump (handoff/implementer-1.md A2) found that AFTER the
adaptor pre-filter closes the `Cannot find Adaptor` fatal, the standalone CLI
on the FULL `TestCases.dll` advances to a NEW, DISTINCT fatal: an anonymous
OPEN-generic type (`<>f__AnonymousType0`2<j,k>`, fields generic-parameter-
typed) reaches `NeoAssemblyWriter.BuildTypeDef` -> `type.TotalPrimitiveSize` ->
`ILType.InitializeFields`, where `fieldType` is null (`FindGenericArgument` on
the OPEN definition yields null) and `fieldType.IsPrimitive` NREs. This is NOT
a CrossBindingAdaptor issue -- it is a field-type RESOLUTION failure on an
open-generic definition.

**The clean, design-D1-preserving fix (the implementer's recommended Option 2,
shipped verbatim):**

1. `ILType.InitializeFields` (`ILRuntime/CLR/TypeSystem/ILType.cs`) now throws
   `TypeLoadException("Cannot resolve field type '...' for type '...'")` when
   `fieldType` is null (and the static-field sibling when `staticFieldType` is
   null), placed at the TOP of the existing `#if ENABLE_NEO_MODE` block, BEFORE
   the `IsPrimitive` NRE site. This mirrors the adaptor-lookup throw sites at
   `ILType.cs:1505/1568/1593` (a LOAD failure throws TLE). **Neo-gated**: the
   guard sits inside the same `#if ENABLE_NEO_MODE` block that already gated
   the NRE, so under plain `Debug` it compiles out -> Legacy byte-identical
   (the null-fieldType path was already latent in Legacy and is NOT reached by
   the Neo-only AOT CLI). The NRE becomes a TLE (a load failure), which the
   narrow catch already handles.
2. `NeoCompiler.CompileCore` pre-filter adds `_ = type.TotalPrimitiveSize;`
   (after `FirstCLRBaseType`/`FirstCLRInterface`). `TotalPrimitiveSize`'s
   getter calls `InitializeFields` when `fieldMapping==null`, so the TLE fires
   INSIDE the existing `TypeLoadException` catch -> the type enters
   `result.Skipped` (exit 2), NEVER reaching `Write`/`BuildTypeDef`.

**Why this preserves D1 (the narrow-TLE-catch invariant):** the catch is STILL
scoped to `TypeLoadException` only -- it is NOT broadened. A genuine non-TLE
init failure (e.g. a real `NullReferenceException` from a logic bug) still
propagates to the `NeoCompilerFatal` wrapper (exit 1). The A2 NRE was only
"non-TLE" because `InitializeFields` NREd on a null; with the guard, that null
becomes a TLE (a load failure) -- the same category as the adaptor absence.
The spec scenario "a non-adaptor type-init failure stays a loud fatal" is
AMENDED (see spec.md): an unresolvable field type is now a TLE (a load failure)
and is skipped; a genuine non-TLE init failure still stays fatal.

### D6. A3 extension: a `!HasBody` silent-skip in the CompileCore per-method loop (fixer-a2)

During the A2 verify, the NEXT fatal after A2 closed was a delegate
`Invoke`/`BeginInvoke`/`EndInvoke` method (`TestCases.ArrayTest/
TestGenericDelegate`1::Invoke(T)`) with `MethodDefinition.HasBody == false`.
`ILMethod.InitCodeBody` guards `if (def.HasBody)`, so `BodyRegister` returns
null WITHOUT throwing; the per-method force-compile loop silently admitted the
method to `methods[]`, and `NeoAssemblyWriter.CompileFresh`'s JIT NREd on the
null body (`JITCompiler.Compile` :360, `body.Variables.Count`). Same binding
bar (exit 0/2, no fatal).

**The fix:** `if (ilm.Definition != null && !ilm.Definition.HasBody) continue;`
in the CompileCore per-method loop, mirroring the existing `IsGenericInstance`
silent-skip. A method with no Cecil body (delegate runtime methods, abstract,
extern/PInvoke) has NO IL to AOT-compile. This is a PRE-COMPILE FILTER, not a
broadened catch -- a body-bearing method whose JIT NREs still throws in the
force-compile try and is recorded as a skip; a genuine `CompileFresh` bug on a
body-bearing method still stays a loud fatal. The `.neo` loader's additive JIT
fallback handles these methods at load time.

## Risks / Trade-offs

- **[A type that SHOULD compile gets skipped]** -> the pre-filter's eager init
  runs the SAME resolution the compile runs; a type skipped here would have
  fatal-aborted under the old code too. So the skip set is EXACTLY the set the
  CLI could never have compiled. Mitigation: the skip report names each skipped
  type + the `TypeLoadException` message, so the operator sees precisely which
  types need an adaptor the CLI does not have.
- **[A skipped type's methods are absent from the `.neo`]** -> at load time,
  `NeoAssemblyLoader`'s existing additive skip (a method/type it cannot bind
  falls back to per-occurrence JIT) handles them -- UNCHANGED by this change.
  The `.neo` is a PARTIAL AOT artifact by contract (exit 2); a type needing a
  harness adaptor was never going to be AOT-compiled by the generic CLI.
- **[Catching `TypeLoadException` masks a different type-init bug]** -> NARROW
  scope: only `TypeLoadException` is caught; any OTHER exception during init
  (a genuine `NullReferenceException`, etc.) still propagates to the fatal
  wrapper (exit 1), keeping the failure signal loud. Mitigation: the skip
  report's `ExceptionType` + `Message` distinguish an adaptor skip from noise.
- **[The pre-filter eagerly inits EVERY type, even those never compiled]** ->
  the init is lazy-by-design and would run anyway during the per-method loop /
  `Write` for every EMITTED type. Running it once more in the pre-filter is a
  no-op for survivors (memoized) and the GATE for throwers. Cost: one
  `BaseType` + one `FirstCLRInterface` resolution per type -- negligible vs the
  JIT cost already paid per method. No mitigation needed.
- **[A green full-`TestCases.dll` compile does NOT prove AOT correctness for
  the survivor methods]** -> the gate is ROBUSTNESS (no adaptor fatal; a valid
  `.neo` written), NOT functional correctness of every survivor method. The
  NeoStep smoke + the Step-22/23/24/25 self-checks remain the functional gates
  (unchanged). Mitigation: the success criterion is explicitly "exit 0/2 + no
  `Cannot find Adaptor` fatal", not "every method AOT-runs correctly".

## Migration Plan

None. The change is additive and Neo-only. Rollback = revert the `CompileCore`
pre-filter (restore direct `inputTypes` iteration + `writer.Write(inputTypes,
...)`) + delete `MakeTypeSkip`. No shared code depends on the new behavior;
`Program.cs` is unchanged.

## Open Questions

- **OQ1 (MakeTypeSkip display form):** `"(type) " + type.FullName` (a clear
  type-level marker reusing the `MethodSkip` slot), or a separate
  `TypeSkip` record + a `TypesSkipped` count + a CLI print change? Default: the
  `(type)` marker (reuses the existing report + exit-2 path; `Program.cs`
  unchanged). Confirm at apply.
- **OQ2 (self-check input):** drive the host-side self-check with the FULL
  `TestCases.dll` (maximally adversarial -- exercises many harness-adaptor
  skips at once -- but slow + noisy in `Debug_Neo`), or a small dedicated
  probe assembly with one IL type inheriting a CLR class (fast, deterministic,
  sub-second, and the skip is asserted explicitly)? Default: a small dedicated
  probe (a `NeoClrProbe`-style assembly with a `class X : SomeClrClass` type
  whose adaptor is NOT registered in the compile AppDomain) for the explicit
  skip assertion, PLUS the V1-B literal smoke on the full `TestCases.dll` for
  the standalone-process no-fatal proof. Confirm at apply.
- **OQ3 (whether to ALSO touch the host-side `Compile(IReadOnlyList<ILType>,
  Stream)` overload):** it calls the SAME `CompileCore`, so it gets the pre-
  filter for free -- no separate edit. Confirm at apply (no action expected).
