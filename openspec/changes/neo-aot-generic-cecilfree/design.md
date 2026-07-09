# Design — neo-aot-generic-cecilfree (child 8)

> Capability: `neo-optimizer` (the Neo AOT Cecil-free path). Parent portfolio:
> `neo-completion-portfolio`. **Status: PARKED (multi-step rework).** This design
> documents the reproducer, the precise gap, the 5 blocker sites, and the
> achievable-slice analysis so a successor ships it in bounded scope.

## Mandate (lead-6 pin)

Cecil-free AOT (`.neo`) load + exec of GENERIC method/type instances. The
cross-section of the **S3-2 Cecil-free load** (child: neo-step25-s3-cecil-free-
load — `AppDomain.LoadNeoAssembly` into a fresh Cecil-free AppDomain) and the
**S2 generic-instantiation-at-load** machinery (child: neo-step25-s2-generic-at-
load — `GenericMethodTemplateOps.BuildFromNeoRecord` + the S2 template-bind loop
in `NeoAssemblyLoader.Attach`, CloneAndPatch from `.neo` templates).

## The reproducer (built this child, FAILs on HEAD — the forward signal)

- `TestCases/NeoStep25CecilFreeGenericProbe.cs` — `NeoStep25CecilFreeGenericProbe`
  with a generic method `T Echo<T>(T v)` + `int ConstGeneric<T>()` + parameterless
  wrappers (`WrapEchoInt/Long/Ref/Struct`, `WrapConstRef`). Top-level + BCL-refs-
  only + struct arg `NeoStep25CegVal`.
- `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep25CecilFreeGenericCheck.cs` —
  the host-side capstone (`#if ENABLE_NEO_MODE && DEBUG`). Compiles the probe ->
  `.neo` in AppDomain A (Cecil), Cecil-free-LOADS into a fresh `new AppDomain()`
  B, invokes the wrappers via `domainB.Invoke`, asserts each == expected. Guards:
  **G1** (template-bind coverage: the Cecil-free generic def shell has its
  `GenericMethodTemplateCache` bound post-load) + **G2** (template body-mutation:
  mutate `ConstGeneric`'s deserialized TemplateBody `Ldc_I4` before load, drive a
  FRESH `ConstGeneric<string>` via `MakeGenericMethod`, assert the MUTATED value —
  proves CloneAndPatch ran the genuine AOT template).
- Wired as CLI special-mode `NeoStep25CecilFreeGeneric` (`ILRuntimeTestCLI/
  Program.cs`).

## HEAD behavior (the reproducer finding — the durable result)

With the **trivial** (JIT-inlinable) `Echo<T>`:
- **10/12 cells PASS, 2 FAIL (G1 + G2).** Attach: 6 attached, 2 skipped
  (`generic def not matched: ...Echo`, `...ConstGeneric`).
- The 4 functional wrapper cells (`WrapEchoInt/Long/Ref/Struct`) **PASS — but NOT
  load-bearing for the generic path**: the JIT inlines the trivial `Echo<T>` into
  the wrapper body at compile time (verified: the wrapper's `Final Results` body
  contains `inlinestart`/`inlineend` + the Echo logic, NO `call` opcode). So the
  wrapper's `.neo` body is self-contained; the generic call never reaches
  ExecuteNeo's generic dispatch. This is the **S2-probe-inlining pitfall**
  (durable finding below).
- **G1 FAILs**: `echoBound=False constBound=False`. The Cecil-free generic def
  shell returns `GenericParameterCount=0` (`ILMethod.cs:189` `isNeoAotShell`
  guard) -> `NeoAssemblyLoader.MatchGenericDefinition` requires
  `GenericParameterCount > 0` -> never matches -> the S2 template-bind loop skips
  both generic defs -> their `GenericMethodTemplateCache` stays null.
- **G2 FAILs (NRE)**: driving a FRESH instance via `MakeGenericMethod` on the
  Cecil-free shell NREs at `ILMethod.cs:1439` (`def.GenericParameters[i].Name`,
  `def==null`).

With a **non-inlinable** `Echo<T>` (9 generic-param locals, > 20 IL ops):
- The wrapper bodies now carry a real `call Echo<T>` (NOT inlined).
- **B Cecil-free generic WrapEchoInt/Long/Struct THROW (ILRuntimeException:
  Object reference not set)** — confirming the genuine Cecil-free generic
  dispatch path is broken end-to-end.
- (A long-T artifact from the over-engineered Echo was set aside; the trivial
  Echo + G1/G2 is the cleaner forward signal.)

## The gap: 5 Cecil-dependency blocker sites in the generic-instance path

A Cecil-free generic-method INSTANCE call (non-inlined) routes: wrapper Call ->
`mapMethod` hash -> the open def shell -> `MakeGenericMethod` -> instance
`BodyRegister` getter -> `InitCodeBody` -> Step-22 hook -> `TryInstantiate` ->
`DoCloneAndPatch` -> `RunNeoBackHalf`. Cecil is read at 5 sites:

1. **`ILMethod.GenericParameterCount` (`:189`)** — `if (isNeoAotShell) return 0`.
   A Cecil-free generic def shell CANNOT report it is generic. Blocks G1 (the S2
   `MatchGenericDefinition` loop) + any generic dispatch.
2. **`ILMethod.MakeGenericMethod` (`:1434-1472`)** — reads `def.GenericParameters
   [i].Name` (`:1439`) + builds a `GenericInstanceMethod(reference)` (`reference`
   is Cecil, null on a shell) + `new ILMethod(gim, def, ...)` (the Cecil ctor).
   NREs on a shell. Blocks G2 + every non-inlined generic call.
3. **`ILMethod.FindGenericArgument` (`:415`)** — `def.HasGenericParameters` NRE on
   a shell. Used by `BuildInitObjPrefix` (struct-T Initobj) + the runtime T
   resolution.
4. **`JITCompiler.BuildInitialRegisterTypes` (`:1220-1234`)** + **`AllocateLocal
   StackSpaces` (`:1648-1800`)** — read `def.Body.Variables[i].VariableType` +
   `def.Parameters[i].ParameterType` (Cecil `TypeReference`), then
   `appdomain.GetType(cecilTypeRef, ...)`. The whole `RunNeoBackHalf`
   specialization (the T-dependent back-half) is Cecil-fed. This is the BIG one.
5. **`NeoAssemblyLoader.ResolveVariableType` (`:221-257`)** — the S2 closure
   resolves a `VariableTypeRefIdx` to a Cecil `TypeReference`: branch (a) reads
   `definition.Definition.GenericParameters` (Cecil; null on a shell); branch (b)
   returns `ILType.TypeReference` / `module.ImportReference` (Cecil). The closure
   feeds `BuildFromNeoRecord` -> `tpl.VariableTypes: TypeReference[]` ->
   `BuildInitObjPrefix` (`vt.IsGenericParameter`/`vt.Name` on Cecil).

Plus the `.neo` format does NOT carry the open def's `GenericParameterCount` or
generic-parameter NAMES (the non-generic-instance `MethodReferencePatchInfo`
carries only Name/DeclaringType/Parameters/IsStatic — `AssemblyInfo.cs:296`).

## Why PARK (scope verdict)

The fix is a coherent but **multi-site, multi-file rework** of the JIT back-half
to accept Cecil-free type sources:
- `ILMethod.cs`: a Cecil-free `MakeGenericMethod` + `FindGenericArgument` guard +
  `GenericParameterCount` from a new shell field + a generic-param-names field on
  the shell (sourced from a `.neo` V4 `NeoTemplateRecord.GenericParamNames`).
- `JITCompiler.cs`: `BuildInitialRegisterTypes` + `AllocateLocalStackSpaces`
  reading Cecil-free `IType[]` (params from the shell's `neoShellParameters`,
  locals from the template's `VariableTypes` resolved Cecil-free) instead of
  `def.Body.Variables`/`def.Parameters`. Subtle: alignment, ref-counting, the
  F-MAJ-1 CLR-VT-flat-bytes path must all be preserved.
- `NeoAssemblyLoader.cs`: the S2 bind loop + `ResolveVariableType` closure
  shell-aware (Cecil-free).
- `NeoAssembly.cs`/`NeoAssemblyWriter.cs`/`NeoAssemblyReader.cs`: the V4
  `GenericParamNames` field (Version 3 -> 4) + Cecil-free variable-type
  resolution.
- `GenericMethodTemplateOps.BuildFromNeoRecord`: closure plumbing.

This touches the **load-bearing Step-22 template machinery** (exercised by
NeoStep25LoadExec 28/28 + NeoStep22SelfCheck 55/55 + the S2 cells). Grinding
through it in one child risks subtle regressions in those 80+ cells and cannot
be adversarially verified within the one-child scope. Per the PARK fallback
("needs multi-step rework"), this child PARKS.

## The achievable slice (the value already delivered + the successor's path)

- **Inlining already covers the trivial case.** A small generic method whose
  body is JIT-inlined into its caller at compile time runs Cecil-free correctly
  TODAY (the wrapper's `.neo` body is self-contained). The functional cells
  passing on HEAD proves this. This is a real (if narrow) capability.
- **The reproducer is a correct forward signal.** It FAILs on HEAD at exactly
  the right sites (G1: template-bind miss; G2: `MakeGenericMethod` NRE) and will
  go green when a successor lands the Cecil-free back-half. The stash-toggle
  (remove the probe/check -> green; restore -> G1/G2 FAIL) is the load-bearing
  proof.
- **The successor's bounded plan** (above) is a single coherent surface ("make
  the generic-instance path Cecil-free") split across the 5 sites. Recommend a
  dedicated child `neo-aot-generic-cecilfree-backhalf` that lands it with the
  full adversarial discipline (stash-toggle per site + the 80+ regression cells).

## Legacy impact

None. The probe is additive (TestCases always compiles). The check class is
`#if ENABLE_NEO_MODE && DEBUG`. The Program.cs branch fires only on the new
filter. Plain-`Debug` build = 0 errors. No engine file was modified this child
(the reproducer is probe + check + CLI wiring only).
