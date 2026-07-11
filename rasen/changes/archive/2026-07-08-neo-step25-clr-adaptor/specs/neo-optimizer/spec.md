## ADDED Requirements

### Requirement: Standalone AOT CLI gracefully skips IL types whose CLR base or interface needs an unregistered CrossBindingAdaptor

The standalone `ilrt_neoc` precompile CLI SHALL NOT fatal-abort when an input
IL type's CLR-class base or CLR interface needs a `CrossBindingAdaptor` that
is not registered in the compile AppDomain. The skip SHALL be implemented in
`NeoCompiler.CompileCore` (the shared core both the file-path
`Compile(string, IReadOnlyList<string>, Stream)` overload and the host-side
`Compile(IReadOnlyList<ILType>, Stream)` overload call). The trigger is the
lazy CLR-base or CLR-interface adaptor resolution that throws
`TypeLoadException("Cannot find Adaptor for:...")` (the throw sites at
`ILType.cs:1505` / `:1568` / `:1593`, keyed on `appdomain.CrossBindingAdaptors`).
Such a type -- whose CLR base or
CLR interface needs a `CrossBindingAdaptor` that is NOT registered in the
compile AppDomain (the typical case for a test-harness-specific or
application-specific adaptor the generic CLI does not carry) -- SHALL be
SKIPPED at the TYPE level: every method on the type SHALL be omitted from the
`.neo`, the type SHALL be recorded in the `NeoCompilerResult.Skipped` report
with a clear type-level display marker, and the compile SHALL continue with
the survivor subset. The `.neo` SHALL still be written (a valid file with the
survivor methods, templates, and type defs), and the CLI SHALL return exit
code `2` (partial) -- NOT exit code `1` (fatal). This is the SAME additive-skip
contract as the existing per-method try/catch in `CompileCore` (a method that
throws during compile is recorded as a skip and omitted), lifted from METHOD
granularity to TYPE granularity.

The skip SHALL be implemented as a per-type PRE-FILTER at the top of
`CompileCore`: each input type's lazy adaptor resolution SHALL be eagerly
triggered (by touching the type's `FirstCLRBaseType` and `FirstCLRInterface`
getters, which drive `InitializeBaseType` / `InitializeInterfaces`) inside a
per-type try/catch that catches `TypeLoadException`; a type whose init throws
SHALL be excluded from the survivor list, and ONLY the survivor list SHALL be
passed to the per-method compile loop AND to `NeoAssemblyWriter.Write`.
Because the ILType init is memoized (`baseTypeInitialized` /
`interfaceInitialized`), a survivor's later `BaseType` access -- including
inside `NeoAssemblyWriter.BuildTypeDefRecord` (`NeoAssemblyWriter.cs:755` /
`:762`) -- SHALL NOT re-throw. The skip SHALL catch `TypeLoadException` (the
adaptor-absence exception) specifically; any OTHER exception during type init
SHALL propagate unchanged (a genuine non-`TypeLoadException` type-init failure
-- e.g. a real `NullReferenceException` from a logic bug -- remains a loud
fatal, exit 1, not a silent skip).

The pre-filter SHALL ALSO eagerly trigger the type's FIELD init (by touching
`type.TotalPrimitiveSize`, whose getter calls `ILType.InitializeFields` when
`fieldMapping == null`) inside the SAME per-type try/catch. An input IL type
with a field whose type fails to resolve -- e.g. a compiler-generated
anonymous OPEN generic type definition (`<>f__AnonymousType0`2<j,k>`) whose
generic-parameter field yields a null field type via `FindGenericArgument`, or
any type with an otherwise-unresolvable field type -- SHALL be SKIPPED at the
type level by the SAME mechanism: `ILType.InitializeFields` SHALL throw
`TypeLoadException("Cannot resolve field type: ...")` (mirroring the adaptor-
lookup throw sites at `ILType.cs:1505` / `:1568` / `:1593`) when a field's
resolved type is null, INSTEAD of null-dereferencing; the pre-filter's
`TypeLoadException` catch then records the skip and the `.neo` is still
written for the survivor subset (exit 2). An unresolvable field type is a
LOAD failure (a `TypeLoadException`), in the same category as the adaptor
absence -- it is NOT a "non-adaptor type-init failure" that must stay fatal.
The `InitializeFields` TLE throw SHALL be `#if ENABLE_NEO_MODE`-gated (it
sits inside the same Neo block that already gated the null-deref), so Legacy
is byte-identical.

A method with no Cecil body -- a delegate's `Invoke` / `BeginInvoke` /
`EndInvoke` (runtime-implemented), or any abstract / extern / PInvoke method
(`MethodDefinition.HasBody == false`) -- has no IL to AOT-compile. The
CompileCore per-method loop SHALL silently omit such methods (mirroring the
`IsGenericInstance` silent-skip), so they never reach
`NeoAssemblyWriter.CompileFresh` (whose JIT would null-deref the absent body).
This is a PRE-COMPILE filter, not a broadened catch: a body-bearing method
whose JIT throws is still recorded as a per-method skip, and a genuine
`CompileFresh` bug on a body-bearing method still stays a loud fatal.

The CLI SHALL NOT couple, reference, or register any test-harness-specific
adaptor (the adaptors `ILRuntimeHelper.Init` registers in
`ILRuntimeTestBase/Adapters/helper.cs:22-29`, or any application-specific
adaptor). The generic compile tool SHALL be robust to the ABSENCE of any
adaptor it does not ship: a type needing such an adaptor is SKIPPED, never
resolved, by this mechanism.

#### Scenario: Full TestCases.dll compiles to a valid .neo with no adaptor fatal

- **WHEN** `ilrt_neoc TestCases.dll out.neo <refs>` is run on the full
  `TestCases.dll` (which contains IL types inheriting harness-adaptor CLR
  classes such as `TestClass2`, `TestClass3`, `TestClass4`,
  `ClassInheritanceTest`, `ClassInheritanceTest2<T>`, and `IDisposable`) and
  the host CLR assembly is passed as a reference
- **THEN** the CLI SHALL write a valid `out.neo` (magic `0x494C524E`) and
  return exit code `0` (clean) or `2` (partial)
- **AND** the CLI SHALL NOT emit `Cannot find Adaptor` as a FATAL on stderr
  (the `ilrt_neoc: FATAL: serializer failure: Cannot find Adaptor for:...`
  line that occurs on HEAD before this change SHALL NOT appear)
- **AND** every IL type whose CLR base/interface needs an unregistered
  adaptor SHALL appear in the skip report (a `SKIP (type) <FullName>:
  TypeLoadException: Cannot find Adaptor for:...` line per skipped type)

#### Scenario: An adaptor-requiring IL type is skipped at the type level, not fatal

- **WHEN** the input contains an IL type `X` whose non-generic CLR base is a
  class `B` for which NO `CrossBindingAdaptor` is registered in the compile
  AppDomain (the `TestClass2` shape), alongside other IL types that have no CLR
  base (or a built-in-adaptor CLR base)
- **THEN** the compile SHALL skip `X` entirely: NO method of `X` SHALL appear
  in the `.neo` `MethodDefTable`, NO template of `X` SHALL appear in the
  `TemplateTable`, and NO `NeoTypeDefRecord` for `X` SHALL be emitted
- **AND** `X` SHALL be recorded in `NeoCompilerResult.Skipped` with a type-
  level display marker (e.g. `(type) <X.FullName>`) and the
  `TypeLoadException` message
- **AND** the other (resolvable) IL types SHALL compile and be emitted
  normally (the skip is scoped to the adaptor-requiring type, not contagious)
- **AND** `NeoCompilerResult.IsComplete` SHALL be `false`, driving exit code
  `2` (the `.neo` is written; the run is partial)

#### Scenario: The generic-instance CLR-base adaptor case is also skipped

- **WHEN** the input contains an IL type whose base is a GENERIC-INSTANCE CLR
  class needing an adaptor (e.g. `class X : ClassInheritanceTest2<X>`, the
  `ILType.cs:1568` throw site) and no matching adaptor construction is
  registered
- **THEN** the compile SHALL skip the type (record it in `Skipped`, omit its
  methods/templates/type-def, continue with survivors, exit `2`) -- NOT fatal

#### Scenario: The CLR-interface adaptor case is also skipped

- **WHEN** the input contains an IL type that IMPLEMENTS a CLR interface for
  which no adaptor is registered (the `ILType.cs:1505` throw site)
- **THEN** the compile SHALL skip the type (record it in `Skipped`, omit its
  methods/templates/type-def, continue with survivors, exit `2`) -- NOT fatal

#### Scenario: A type whose CLR base resolves via a built-in adaptor is NOT skipped

- **WHEN** the input contains an IL type `class X : System.Exception` (or
  `class Y : System.Attribute`) and the compile AppDomain is constructed via
  `new AppDomain()` (whose ctor registers `Adapters.ExceptionAdaptor` at
  `AppDomain.cs:244` and `Adapters.AttributeAdapter` at `AppDomain.cs:237`)
- **THEN** the pre-filter's eager `FirstCLRBaseType` access SHALL find the
  built-in adaptor in `appdomain.CrossBindingAdaptors` and SUCCEED
- **AND** the type SHALL NOT be skipped -- its methods SHALL be compiled and
  emitted (a built-in-adaptor type resolves TODAY; this change preserves that)
- **AND** no additional built-in adaptor registration SHALL be added by this
  change (the ctor's registration is the single source)

#### Scenario: A type with an unresolvable field type is skipped, not fatal (A2 closure)

- **WHEN** the input contains an IL type with a field whose type fails to
  resolve in the compile AppDomain -- e.g. a compiler-generated anonymous OPEN
  generic type definition (`<>f__AnonymousType0`2<j,k>`) whose generic-
  parameter field yields a null field type via `FindGenericArgument`, or any
  type whose `appdomain.GetType(field.FieldType)` returns null
- **THEN** `ILType.InitializeFields` SHALL throw `TypeLoadException("Cannot
  resolve field type: ...")` (mirroring the adaptor-lookup throw sites)
  INSTEAD of null-dereferencing the field type
- **AND** the pre-filter (which eagerly touches `type.TotalPrimitiveSize`,
  triggering `InitializeFields`) SHALL catch that `TypeLoadException` and
  record the type as a type-level skip
- **AND** the compile SHALL write the `.neo` for the survivor subset and
  return exit `2` (partial) -- NOT exit `1` (fatal) on a `NullReferenceException`
- **AND** the `InitializeFields` TLE throw SHALL be `#if ENABLE_NEO_MODE`-gated
  (Legacy byte-identical under plain `Debug`)

#### Scenario: A method with no Cecil body is silently omitted, not fatal (A3 extension)

- **WHEN** the input contains a method with `MethodDefinition.HasBody == false`
  -- a delegate's `Invoke` / `BeginInvoke` / `EndInvoke` (runtime-implemented),
  or an abstract / extern / PInvoke method
- **THEN** the CompileCore per-method loop SHALL silently omit it (it has no
  IL to AOT-compile), so it never reaches `NeoAssemblyWriter.CompileFresh`
  (whose JIT would null-deref the absent body)
- **AND** the `.neo` loader's existing additive JIT fallback SHALL handle it
  at load time
- **AND** this SHALL be a pre-compile filter (a body-bearing method whose JIT
  throws is still recorded as a per-method skip; a genuine `CompileFresh` bug
  on a body-bearing method stays a loud fatal)

#### Scenario: A genuine non-TypeLoadException type-init failure stays a loud fatal, not a silent skip

- **WHEN** a type's lazy init throws an exception that is NEITHER an adaptor-
  absence `TypeLoadException` NOR a field-type-resolution `TypeLoadException`
  -- i.e. a genuine non-`TypeLoadException` failure (e.g. a real
  `NullReferenceException` from a compile-tool LOGIC bug, not an
  unresolvable-type load failure)
- **THEN** the pre-filter SHALL NOT catch it (the catch is scoped to
  `TypeLoadException`); it SHALL propagate to `CompileCore`'s outer wrapper
  and surface as `ilrt_neoc: FATAL: ...` (exit `1`)
- **AND** the skip report SHALL NOT contain a spurious entry for it (a real
  bug is not masked as a graceful skip)
- **NOTE** an unresolvable field type (an open-generic definition's generic-
  parameter field, or any field whose `appdomain.GetType` returns null) is a
  LOAD failure: `ILType.InitializeFields` throws `TypeLoadException` for it
  (mirroring the adaptor-lookup sites), so the pre-filter DOES skip it (exit
  2). It is NOT an example of this "stays fatal" scenario -- only a non-TLE
  failure stays fatal.

#### Scenario: No test-harness-specific adaptor is coupled into the generic CLI

- **WHEN** the change is built and the `ILRuntimeNeoCompiler` project's
  references are inspected
- **THEN** the project SHALL NOT reference `ILRuntimeTestBase` or any
  test-framework assembly
- **AND** no `RegisterCrossBindingAdaptor` call for a test-harness-specific
  adaptor (`TestClass2Adapter`, `TestClass3Adaptor`, `TestClass4Adaptor`,
  `ClassInheritanceTestAdaptor`, `ClassInheritanceTest2Adaptor`,
  `InterfaceTestAdaptor`, `IDisposableAdapter`,
  `IAsyncStateMachineClassInheritanceAdaptor`) SHALL be added to `NeoCompiler`
  or the CLI
- **AND** the robustness SHALL come entirely from the graceful-SKIP mechanism
  (a type needing such an adaptor is skipped), NOT from coupling the adaptor

#### Scenario: The skip is purely compile-side; the .neo loader is unchanged

- **WHEN** a `.neo` produced by this change (a partial `.neo` missing the
  adaptor-requiring types) is loaded by `NeoAssemblyLoader.Attach`
- **THEN** the loader SHALL behave exactly as before this change: it SHALL
  attach every method def it can bind and SKIP every method/type it cannot
  bind (the existing additive load contract)
- **AND** no `.neo` format change, no `NeoAssemblyLoader` change, no
  `NeoAssemblyWriter` change, and no `AppDomain` change SHALL be introduced by
  this requirement. (The ONE `ILType` change in scope is the Neo-gated
  `InitializeFields` field-type `TypeLoadException` throw described above; no
  other `ILType` change. The loader is unchanged -- the throw is a compile-time
  load failure that drives a type-skip, not a loader behavior.)

#### Scenario: Legacy ExecuteR is unaffected

- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and the Legacy
  register VM runs the NeoStep-filter smoke
- **THEN** the smoke SHALL show the SAME pre-existing Legacy failure set with
  and without this change (stash-toggle proof), because the pre-filter and the
  `MakeTypeSkip` helper are inside `NeoCompiler.cs` (gated `#if
  ENABLE_NEO_MODE`, compiles out under plain `Debug`), AND the
  `ILType.InitializeFields` field-type `TypeLoadException` throw is inside the
  SAME `#if ENABLE_NEO_MODE` block that already gated the null-deref -- so
  plain `Debug` is byte-identical (the plain-`Debug` ILRuntime build is 0
  errors; the null-fieldType path was already latent in Legacy and is not
  reached by the Neo-only AOT CLI)

#### Scenario: The host-side Compile overload gets the skip for free

- **WHEN** the host-side `NeoCompiler.Compile(IReadOnlyList<ILType>, Stream)`
  overload is invoked (the V1-A self-check path)
- **THEN** it SHALL route through the SAME `CompileCore` pre-filter, so an
  adaptor-requiring type in the explicit type set is skipped (recorded in
  `Skipped`, omitted, exit-2-equivalent) with NO separate edit
