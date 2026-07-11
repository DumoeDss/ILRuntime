# Design — neo-aot-generic-cecilfree (child 8)

> Capability: `neo-optimizer` (the Neo AOT Cecil-free path). Parent portfolio:
> `neo-completion-portfolio`. **Status: SHIPPED (re-audit UNBLOCKED).** This was
> the LAST PARKED child; the prior "multi-step 5-site rework" framing was a
> MIS-ATTRIBUTION in the SAME family as child 4 (a marshalling bug + a registration
> miss) and child 17 (a dest-slot-sizing bug). The real fix is a coherent set of
> SMALL, additive, Neo-gated patches -- one per genuine gap -- NOT a back-half
> rework. The child-4/child-17 re-audit skepticism (applied here) held: every site
> had a tractable 5-15-line fix; none required redesigning the Step-22 template
> machinery or the ABI.

## Mandate (lead-6 pin)

Cecil-free AOT (`.neo`) load + exec of GENERIC method instances. The
cross-section of the **S3-2 Cecil-free load** (`AppDomain.LoadNeoAssembly` into a
fresh Cecil-free AppDomain) and the **S2 generic-instantiation-at-load**
machinery (`GenericMethodTemplateOps.BuildFromNeoRecord` + the S2 template-bind
loop in `NeoAssemblyLoader.Attach`).

## The reproducer (the forward signal)

- `TestCases/NeoStep25CecilFreeGenericProbe.cs` — `NeoStep25CecilFreeGenericProbe`
  with `T Echo<T>(T v)` + `int ConstGeneric<T>()` + parameterless wrappers. The
  trivial `Echo<T>` is JIT-inlined into the wrappers (the documented S2-probe-
  inlining pitfall), so the functional wrapper cells alone do NOT exercise the
  Cecil-free generic dispatch.
- `ILRuntime/.../NeoStep25CecilFreeGenericCheck.cs` — the host-side capstone.
  Guards **G1** (template-bind coverage: both generic defs' template cache is
  bound post-load) + **G2** (template body-mutation: mutate ConstGeneric's
  deserialized TemplateBody `Ldc_I4` before load, drive a FRESH
  `ConstGeneric<string>` via `MakeGenericMethod`, assert the MUTATED value). G1/G2
  are the load-bearing proofs; the functional cells pass via inlining.

## Re-audited root cause (the 5-site framing was RIGHT on the SITE COUNT, WRONG on the EFFORT)

The prior PARK note framed this as a "bounded 5-site JIT back-half rework". The
RE-AUDIT (child-4/child-17 lens) found: the 5 sites are each REAL blockers (not
mis-attributed -- unlike child 4/17's single-bug root, this genuinely spans the
generic-instance load+exec path), BUT each site fix is a SMALL additive patch
(no ABI change, no redesign, no shared-cache risk). The "multi-step rework"
verdict was over-cautious: it conflated "5 distinct sites" with "a risky
back-half rework". The sites:

1. **`ILMethod.GenericParameterCount` (`:189`)** — a Cecil-free generic-def shell
   returned 0 (could not report it is generic). Blocks the S2 `MatchGenericDefinition`
   loop. **Fix:** a `.neo`-stamped `neoShellGenericParamNames` field; the shell
   reports its length. (5 lines.)
2. **The generic-def SHELL was never CREATED at Cecil-free load.** (This was the
   REAL first blocker the PARK note UNDER-counted -- it is NOT in the original 5.)
   The .neo MethodDefs table carries NON-generic methods only (Step-24 partition;
   generic defs live ONLY in the TemplateTable), so the Cecil-free type build
   never created a shell for `Echo`/`ConstGeneric`. **Fix:** the S2 bind loop
   builds + registers the generic-def shell from the template's open-def MethodRef
   when the matcher misses (`BuildAndRegisterGenericDefShell`). Generic-param
   params resolve to `ILGenericParameterType(name)`.
3. **`ILMethod.MakeGenericMethod` (`:1504`)** — read `def.GenericParameters` +
   built a `GenericInstanceMethod(reference)` (Cecil; null on a shell). NREs on a
   shell. **Fix:** a Cecil-free `MakeGenericMethodShell` branch (sets
   genericDefinition/genericArguments/genericParameters + T-substituted params/
   return directly; no Cecil). (35 lines.)
4. **`ILMethod.InitCodeBody` (`:847`)** — `if (def.HasBody)` NREs on a shell
   instance BEFORE the template path. **Fix:** a Cecil-free shell-instance guard
   that routes straight through `TryInstantiate` -> CloneAndPatch (never touches
   def). (20 lines.)
5. **`JITCompiler.BuildInitialRegisterTypes` (`:1267`)** +
   **`AllocateLocalStackSpaces` (`:1740`)** — read `def.Body.Variables` /
   `def.Parameters` (Cecil; null on a shell). This was the "BIG one". **Fix (the
   child-4/17-style insight):** the template ALREADY has the local types
   (`VariableTypes`, Cecil `TypeReference[]`, captured at serialize) and the shell
   ALREADY has the T-substituted params. The back-half is Cecil-free'd by reading
   `template.VariableTypes[i]` + `method.Parameters[i]` when `def == null`, both
   flowing through the EXISTING `appdomain.GetType(token, declaringType, method)`
   (which resolves a generic-param Cecil `TypeReference` via the instance's
   `FindGenericArgument`). NO new type representation, NO ABI change. (Parameterized
   by 3 small helpers: `GetTemplateVariableTypes` / `GetLocalCount` / `GetLocalType`.)
6. **`NeoAssemblyLoader.ResolveVariableType` (`:221`)** — branch (a) read
   `definition.Definition.GenericParameters` (Cecil; NRE on a shell) for a
   generic-param local; branch (b) CLR type did `module.ImportReference` (needs a
   Cecil module; a Cecil-free AppDomain B has NONE). **Fix:** branch (a) synthesizes
   a Cecil `GenericParameter` from the .neo-stamped names (a tiny
   `IGenericParameterProvider` owner; no Cecil module); branch (b) CLR fallback
   builds a BARE `TypeReference` (namespace+name, no module) whose FullName resolves
   via `appdomain.GetType`. (40 lines.)

Plus the `.neo` format genuinely lacked 2 fields the Cecil-free path needs (the
non-generic-instance `MethodReferencePatchInfo` carries only Name/DeclaringType/
Parameters/IsStatic): the open def's **GenericParamNames** + **ReturnTypeRefIdx**
(the MethodRef omits the return type; a generic-param return "T" must resolve so
the instance's RunNeoBackHalf return-slot sizing is correct). Both are a `.neo`
**V5** addition (Version 4 -> 5), additive + backward-incompatible only via the
Version guard.

## Why this is NOT a back-half rework (the disproof of the PARK framing)

The Step-22 template machinery (`RunNeoBackHalf` / `CloneAndPatch` / `TryInstantiate`)
is UNCHANGED. The captured template's `VariableTypes` is EXACTLY
`def.Body.Variables[i].VariableType` (the open def's local types) -- so swapping
`def.Body.Variables[i].VariableType` for `template.VariableTypes[i]` is a
byte-for-byte-equivalent substitution when `def == null`. The T-resolution
(`appdomain.GetType(genericParamRef)` -> `FindGenericArgument`) is ALREADY the
path the Cecil-present back-half uses. No alignment / ref-counting / F-MAJ-1
CLR-VT logic was touched -- it reads the resolved `IType` identically.

## What the prior PARK note got right

- **The S2-probe-inlining pitfall** (a trivial generic method's body is JIT-inlined
  into the caller, so functional cells pass WITHOUT exercising the Cecil-free
  generic dispatch). G1 (template-bind) + G2 (fresh-instance template-mutation)
  are the load-bearing proofs. CORRECT + carried forward.
- **The 5 blocker sites are real** (the count was right; the EFFORT estimate was
  wrong). Each is a small additive patch.

## What the prior PARK note got wrong

- **"multi-step rework ... risks subtle regressions in the 80+ Step-22/Step-23
  cells."** DISPROVEN: the changes are gated `def == null` (a Cecil-free shell) OR
  additive `.neo` fields ignored by the same-AppDomain S1/S2/S3 path. NeoStep25
  LoadExec 28/28, NeoStep22SelfCheck 55/55, NeoStep23Roundtrip 15/15 all HELD.

## Legacy impact

None. Every engine change is `#if ENABLE_NEO_MODE`-gated. The `.neo` V5 format
change is additive (the Version guard rejects old files; the same-AppDomain path
ignores the new fields). Plain-`Debug` build = 0 errors.

## Verification (LEAD-verify pending)

- **NeoStep25CecilFreeGeneric capstone: 12/12 cells passed** (G1 + G2 + the 4
  functional wrappers + compile + load + the 4 A-JIT reference cells). Attach: 8,
  0 skipped.
- **Stash-toggle (load-bearing):** stash the 7 engine files (keep the check) ->
  G1+G2 FAIL (10/12, the HEAD state); pop -> 12/12 GREEN.
- **NeoStep25 gate: 0 failed.** NeoStep25LoadExec 28/28, NeoStep22SelfCheck 55/55,
  NeoStep23Roundtrip 15/15 (no .neo-format regression from V5).
- **NeoStep smoke: 278 ran, 0 failed** (the child-17 baseline; no regression).
- **Legacy-neutral:** plain `Debug` build 0 errors.

## Follow-ups (out of scope)

- **Generic TYPE instances** (`List<ILType>` field): the method-instance path is
  the core delivered here. A generic TYPE field's Cecil-free load is a separate
  surface (the ILType field-layout path); noted for a follow-up if a probe needs it.
- **T-identity-token generic methods** (Box T / Ldobj T / a T-qualified callvirt):
  the S2 slice covers IsRefMoveFlag-only + empty patch tables (the probe's generic
  methods are T-invariant bodies). A T-identity-token method would still hit the
  S3 reject (the `hasIdentityToken` return-null in `BuildFromNeoRecord`) -- a
  Cecil-free T-identity patch applier keyed on `GenericParamIdx` is the S3 follow-up.
