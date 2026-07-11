# Design: neo-step25-s3-clr-registration

> Technical design for the S3-5 TRUE-COMPLETION slice (host-CLR-assembly
> registration for the standalone AOT CLI). Grounded in the dump-gate reproduced
> on HEAD, line-cited. Legacy is the REFERENCE, not a target.

## Context

Step 24 shipped the `ilrt_neoc` standalone precompile CLI. Its documented V1
boundary (Step-24 ship-log, "V1 boundary + the corrected deferral rationale")
is: the full-`TestCases.dll` compile fatal-aborts with a CLR-type-resolution
fatal (`Cannot find Type:ILRuntimeTest.TestFramework.TestCLREnum`), because the
standalone CLI never registers the host CLR assembly that defines that enum
(`ILRuntimeTestBase.dll`). Step 25 S1/S2/S3 shipped the runtime `.neo` loader
+ the ILMethod/ILType Cecil-decoupling dual-path; the load-side CLR-type
resolution already routes through `appdomain.GetType(fullName)`. The remaining
gap is purely on the COMPILE side: the standalone CLI's compile-time AppDomain
cannot resolve host CLR types. This change closes that gap.

## The dump-gate verdict (binding, reproduced on HEAD)

The dump-gate's three questions, answered by reproduction on HEAD `d3092cb7`
(ilrt_neoc built `Debug_Neo`, TestCases built `Debug`, both 0 errors):

### Q1. The EXACT failing resolution site (compile-side / load-side / both?)

**COMPILE-side only.** Reproduced two ways:

1. **Bare run (the documented gap):**
   `ilrt_neoc TestCases.dll out.neo` (no refs) -> exit 1:
   ```
   ilrt_neoc: FATAL: serializer failure: Cannot find Type:ILRuntimeTest.TestFramework.TestCLREnum
     inner: KeyNotFoundException: Cannot find Type:ILRuntimeTest.TestFramework.TestCLREnum
   ```
   - **Throw site:** `ILRuntime/Runtime/Enviorment/AppDomain.cs:1409` ->
     `throw new KeyNotFoundException("Cannot find Type:" + typename);` inside
     `internal IType GetType(object token, IType contextType, IMethod
     contextMethod)` (`AppDomain.cs:1220`), the Cecil-`TypeReference`-token
     resolver the JIT calls when force-compiling a method that references the
     CLR enum.
   - **Root cause:** the base resolution `GetType(typename)` (`AppDomain.cs:
     1371`) dispatches to `GetType(string fullname)` (`AppDomain.cs:948`),
     whose CLR fallback at `AppDomain.cs:1068`
     (`foreach (var i in System.AppDomain.CurrentDomain.GetAssemblies())`) and
     `AppDomain.cs:1059` (`referenceAssemblies`) and the snapshot at
     `AppDomain.cs:117` (`loadedAssemblies`) ALL fail to find
     `ILRuntimeTestBase`, because the standalone CLI process does NOT have it
     loaded. `NeoCompiler.Compile` wraps the failure as
     `NeoCompilerFatal("serializer failure: ...)` (`NeoCompiler.cs:152`).

2. **Naive-fix-is-WRONG run (the shadowing proof):**
   `ilrt_neoc TestCases.dll out.ref.neo ILRuntimeTestBase.dll` (ref passed,
   current `LoadAssembly(refStream)` path) -> exit 1, but a DIFFERENT failure:
   ```
   ilrt_neoc: FATAL: serializer failure: Object reference not set to an instance of an object.
     inner: NullReferenceException: Object reference not set to an instance of an object.
   ```
   The compile gets PAST every `TestCLREnum` reference (the JIT output shows
   `initobj r1, ILRuntimeTest.TestFramework.TestCLREnum`, `box r1, r1,
   ILRuntimeTest.TestFramework.TestCLREnum` for Test00..Test06 + DelegateTest)
   and only NREs much later. This proves `LoadAssembly`-ing the host CLR
   assembly wraps `TestCLREnum` (a CLR enum) as an `ILType` in `mapType`
   (`AppDomain.cs:664`, via `InitializeFromModule` -> `new ILType(t, this)` at
   `AppDomain.cs:690`), which SHADOWS the real CLR enum. `GetType(string)` at
   `AppDomain.cs:956` checks `mapType` FIRST and returns the shadow `ILType`
   before ever reaching the CLR fallback -> the type "resolves" but to the
   WRONG kind, and the IL-wrapped CLR enum then NREs downstream. So the
   `LoadAssembly`-the-ref path is not merely insufficient, it is incorrect.

### Q2. How the IN-PROCESS path resolves host CLR types (the model to mirror)

`ILRuntimeTestCLI.csproj:44` -> `<ProjectReference Include="..\ILRuntimeTestBase\
ILRuntimeTestBase.csproj" />`. So `ILRuntimeTestBase.dll` is loaded into the
host `System.AppDomain` at process start as a normal managed dependency. When
the in-process JIT resolves the `TestCLREnum` Cecil `TypeReference`, the CLR
fallback at `AppDomain.cs:1068` (`System.AppDomain.CurrentDomain.
GetAssemblies()`) finds it, `i.GetType(fullname)` returns the real CLR `Type`,
and `AppDomain.cs:1079` wraps it as a `CLRType` (registered in `clrTypeMapping`
+ `mapType` keyed by fullname + assembly-qualified name). No explicit
registration call is needed in-process -- the host CLR assembly's mere presence
in the host `System.AppDomain` is the registration. The standalone CLI lacks
this only because it takes the host CLR assembly as a FILE PATH it Cecil-loads
but never `Assembly.Load`s.

### Q3. CLI-side registration vs `.neo` format/loader change?

**CLI-side registration ONLY. No `.neo` format change. No loader change.**

- The compile-side fix is to make the standalone CLI's process host the CLR
  assembly, exactly as the in-process model does: `Assembly.LoadFrom(rp)` for
  each reference path. That single call puts the assembly into
  `System.AppDomain.CurrentDomain`, so `GetType(string):1068` resolves its
  types as `CLRType`.
- The LOAD-side needs nothing: `NeoAssemblyLoader.ResolveTypeRefToIType`
  (`ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs:265`) already does
  `appdomain.LoadedTypes.TryGetValue(fullName, ...) -> ILType` else
  `appdomain.GetType(fullName)` (`NeoAssemblyLoader.cs:274`) -- the SAME CLR
  fallback. In any AppDomain where the host CLR assembly is registered (the
  in-process test harness today), a `.neo` carrying a host-CLR-type ref loads
  correctly. (A future standalone Cecil-free LOAD AppDomain would need the same
  `Assembly.LoadFrom` registration -- that is the deferred sub-surface 2
  Cecil-free-load concern, NOT this change.)
- A `.neo` format extension (recording the CLR assembly-qualified name and
  re-resolving at load) would only matter for a Cecil-free standalone LOAD
  that does NOT have the host assembly available; it is out of scope here and
  would couple an unproven format bump to a one-line compile fix.

## Goals / Non-Goals

**Goals:**
- A TestCLREnum-shape input (IL referencing a host CLR enum in a non-BCL host
  assembly) compiles to a `.neo` via the standalone CLI WITHOUT a fatal (exit
  0 clean, or exit 2 partial for unrelated unimplemented-op skips).
- The host CLR enum resolves as a `CLRType` at compile time (not an `ILType`
  shadow) -- the `LoadAssembly`-the-ref shadowing path is removed.
- An end-to-end self-check proves compile + load + run with the enum value
  round-tripping.

**Non-Goals:**
- Multi-hotfix-assembly cross-references (one IL hotfix referencing another IL
  hotfix, which DO need `LoadAssembly`). Deferred V1 limitation.
- A `.neo` format Version bump / Cecil-free standalone LOAD AppDomain (the
  Step-25 S3 sub-surface 2/3 deferral).
- Robust IL-vs-CLR ref classification beyond "host CLR assembly = CLR-loadable
  ref passed on the CLI." The input assembly is the only `LoadAssembly`-ed
  assembly.
- Any change to `AppDomain`'s resolution methods, the JIT, `ExecuteNeo`, the
  optimizer, the Step-22/23/24/25-S1/S2/S3 artifacts.

## Decisions

### D1. The fix: replace `LoadAssembly(refStream)` with `Assembly.LoadFrom(rp)`

In `NeoCompiler.Compile(string inputAssemblyPath, IReadOnlyList<string>
referenceAssemblyPaths, Stream outputStream)` (`NeoCompiler.cs:52`), the
current reference loop (`NeoCompiler.cs:119-136`):

```
foreach (var rp in referenceAssemblyPaths) {
    if (empty/missing) continue;
    try { using (var rs = File.OpenRead(rp)) appdomain.LoadAssembly(rs); }
    catch { /* silently skipped */ }
}
```

is REPLACED by host-CLR registration:

```
foreach (var rp in referenceAssemblyPaths) {
    if (empty/missing) continue;
    // Register the ref with the host CLR so AppDomain.GetType(string)'s CLR
    // fallback (the live GetAssemblies() scan at AppDomain.cs:1068) resolves
    // its types as CLRType. Mirrors the in-process model (host CLR assemblies
    // are project refs resident in the host System.AppDomain). WITHOUT this,
    // a Cecil TypeRef to a host CLR type fatal-aborts at AppDomain.cs:1409.
    try { System.Reflection.Assembly.LoadFrom(rp); }
    catch { /* BCL/already-loaded/unresolvable -- best-effort */ }
}
```

Rationale (why `Assembly.LoadFrom`, not the alternatives):

- **Why not keep `LoadAssembly(refStream)`:** the dump (Q1.2) proves it
  shadows CLR types as `ILType` and NREs. It is the bug, not a partial fix.
- **Why not BOTH (`Assembly.LoadFrom` AND `LoadAssembly`):** `GetType(string)`
  checks `mapType` first (`AppDomain.cs:956`); a `LoadAssembly`-ed ref's
  `ILType` shadow wins over the `Assembly.LoadFrom`-ed `CLRType`. Doing both
  reintroduces the shadow. A ref MUST be registered by exactly one path.
- **Why not classify per-ref (IL vs CLR) and route differently:** there is no
  reliable intent signal at compile time, and for the COMPILE step every ref
  only needs its types to RESOLVE (the `.neo` records type refs; it does not
  execute them). `Assembly.LoadFrom`-ing every ref resolves every ref's types
  as `CLRType`, which is correct for host CLR refs (the TestCLREnum case) and
  the documented V1 scope. Multi-hotfix cross-refs (where a ref's types SHOULD
  be `ILType`) are a separate, already-deferred scenario.

`Assembly.LoadFrom` is best-effort try/catch: a BCL ref already loaded throws
"a duplicate assembly with the same identity" / `FileLoadException`; an
unresolvable path throws `FileNotFoundException` -- both caught, resolution
falls back to the existing CLR scan. This never fatal-aborts.

### D2. The host-side self-check (the round-trip gate)

A new `NeoStep25S3ClrEnumCheck` (`#if ENABLE_NEO_MODE && DEBUG`, mirrors
`NeoStep25LoadExecCheck`), driven via an `ILRuntimeTestCLI` `NeoStep25S3ClrEnum`
hook. It exercises the FILE-PATH `NeoCompiler.Compile` overload (the one the
fix lives in) so the `Assembly.LoadFrom` path is actually driven:

```
1. Write a small TestCLREnum-referencing IL assembly to a temp file, OR reuse
   the existing TestCases.dll + pass ILRuntimeTestBase.dll as a ref (the
   in-process test AppDomain already has ILRuntimeTestBase loaded, which is
   fine -- the point is to drive NeoCompiler.Compile's ref loop).
2. using var ms = new MemoryStream();
   new NeoCompiler().Compile(testCasesDllPath, new[]{ ilRuntimeTestBaseDllPath }, ms);
   // BEFORE the fix: throws NeoCompilerFatal "Cannot find Type:...TestCLREnum".
   // AFTER the fix: returns a NeoCompilerResult (no fatal).
3. Assert no Skip mentions TestCLREnum; assert the enum-bearing methods are in
   the compiled set (not skipped on a CLR-resolution failure).
4. ms.Position = 0; var model = NeoAssemblyReader.Read(ms);
   NeoAssemblyLoader.Attach(appdomain, model);
5. Invoke a method that reads the CLR enum (e.g. TestCases.TestCLREnum.Test06,
   which asserts ILRuntimeTest.TestFramework.TestCLREnumClass.Test ==
   TestCLREnum.Test2) and assert it returns / does not throw -- the enum value
   round-trips through compile -> .neo -> load -> ExecuteNeo.
```

The decisive cell is step 2 + 5 together: step 2 fails (fatal) before the fix
and succeeds after; step 5 proves the resolved `CLRType` is the REAL enum, not
a shadow (a shadow `ILType` would mis-execute the enum equality).

### D3. The V1-B literal-CLI smoke (the standalone-process gate)

The host-side self-check runs in-process (where ILRuntimeTestBase is already
loaded, so it does not ALONE prove the standalone process resolves the host
CLR type). The standalone-process proof is a minimal V1-B smoke (mirrors the
Step-24 V1-B pattern):

```
ilrt_neoc <TestCLREnum-referencing input.dll> out.neo <host-CLR-ref.dll>
assert: exit 0 (or 2 with no TestCLREnum skip); out.neo starts with magic
        4e 52 4c 49 (LE 0x494C524E = "ILRN"); NO "Cannot find Type" on stderr.
```

The input can be the full `TestCases.dll` with `ILRuntimeTestBase.dll` as the
ref (the bare-run reproducer, now expected to succeed), or a small dedicated
probe. The full-`TestCases.dll` run may still exit 2 (partial) for unrelated
Step-19+ unimplemented-op skips (delegates/async) -- the gate is ONLY that no
`TestCLREnum` / CLR-resolution fatal occurs and a valid `.neo` is written.

### D4. Gating: Neo-only, Legacy-neutral, additive

- The ref-loop edit is inside `NeoCompiler.Compile`, which is `#if
  ENABLE_NEO_MODE` (the whole `NeoCompiler.cs` file is). Under plain `Debug`
  the file compiles out -> Legacy byte-identical.
- The fix is a single new caller of `System.Reflection.Assembly.LoadFrom` and
  the existing `AppDomain` CLR-resolution path. No `AppDomain`, `ILMethod`,
  `ILType`, JIT, optimizer, or `.neo` format/loader change.
- `ILRuntimeNeoCompiler/Program.cs` is UNCHANGED.

## Risks / Trade-offs

- **[A host CLR assembly that cannot be `Assembly.LoadFrom`-ed]** -> some
  refs (native hosts, ref-only metadata assemblies) are not CLR-loadable.
  Mitigation: best-effort try/catch; a failed `LoadFrom` is skipped and
  resolution falls back to the existing BCL/CLR scan + the Cecil resolver
  (still set up at `NeoCompiler.cs:80-95`, unchanged). The TestCLREnum case
  (`ILRuntimeTestBase.dll`) is a standard managed assembly and loads cleanly
  (verified: the in-process test CLI loads it as a project ref).
- **[Multi-hotfix cross-refs now resolve as `CLRType` instead of `ILType`]**
  -> ACKNOWLEDGED + INTENTIONAL: a hotfix assembly passed as a ref would, post
  fix, have its types resolve as `CLRType` at compile, which is wrong for a
  true hotfix cross-ref. This is the deferred multi-hotfix-assembly scenario
  (Step-24 D7 / the deferral table). V1's contract is "input = one hotfix
  assembly; refs = host CLR + BCL." Documented as a V1 limitation.
- **[The replaced `LoadAssembly(refStream)` loop had no verified coverage]**
  -> LOW risk. Step-24 V1-B was BCL-refs-only; the ref-`LoadAssembly` path is
  explicitly V1-A-UNVERIFIED in the ship-log. Removing it regresses no
  verified scenario. Mitigation: the NeoStep smoke + the Step-22/23/24/25
  self-checks unchanged.
- **[`Assembly.LoadFrom` pins the file (shadow-copy / lock)]** -> the CLI is a
  short-lived compile tool; a file lock during compile is acceptable (PatchTool
  and the test CLI exhibit the same pattern). No mitigation needed for V1.
- **[A green compile does not prove correct execution]** -> the self-check's
  step 5 (invoke a CLR-enum-reading method after attach) is the load-bearing
  proof that the resolved type is the REAL `CLRType` (a shadow `ILType` would
  mis-execute). Mitigation: step 5 is mandatory.

## Migration Plan

None. The change is additive and Neo-only. Rollback = revert the ref-loop edit
in `NeoCompiler.Compile` to the prior `LoadAssembly(refStream)` form + delete
the self-check + its CLI hook + any probe. No shared code depends on the new
behavior.

## Open Questions

- **OQ1 (self-check input):** drive the self-check with the FULL
  `TestCases.dll` + `ILRuntimeTestBase.dll` ref (maximally adversarial, but
  slow + noisy in `Debug_Neo`), or a small dedicated `NeoStep25S3ClrProbe`?
  Default: a small dedicated probe that references the host CLR enum (fast,
  deterministic, sub-second), PLUS the V1-B literal smoke on the full
  `TestCases.dll` for the standalone-process proof. Confirm at apply.
- **OQ2 (enum round-trip assertion form):** assert via `appdomain.Invoke` on
  `TestCases.TestCLREnum.Test06` (which itself throws on mismatch), or via a
  custom probe method returning the enum value for an explicit equality check?
  Default: the custom probe returning `TestCLREnum.Test2` (explicit `==`
  assertion in the self-check is clearer than relying on the probe's own
  throw). Confirm at apply.
- **OQ3 (ref-ordering / `Assembly.LoadFrom` vs `Assembly.LoadFile`):**
  `LoadFrom` resolves into the load-from context (handles dependencies +
  already-loaded dedup); `LoadFile` loads without context. Default: `LoadFrom`
  (matches the host-AppDomain-resident semantics of the in-process model).
  Confirm at apply.
