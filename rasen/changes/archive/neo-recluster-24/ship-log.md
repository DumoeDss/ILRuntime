# Ship Log: neo-recluster-24 (Wave-2 child of neo-overhaul)

Branch `features/object-model-overhaul`. Neo = `ExecuteNeo` under `ENABLE_NEO_MODE`.
Worker: neo-recluster-24 (PLANNER+IMPLEMENTER, BACKGROUND run).

## Phase 1 -- FRESH GROUNDING (the current 24)
- Built CLI (Debug_Neo --no-incremental) + TestCases (Debug), 0 errors each.
- Ran the FULL smoke (no filter): `Ran 935 tests, 24 failded, 20 ignored, 7 todos`.
- Re-clustered the 24; wrote `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-24.md`.
  The 24 are deeply fragmented: 10 test-internal-assertion grab-bag (distinct roots),
  5 autogen-binding arg-marshall, 2 collection-struct field access, 2 byref out-STRUCT,
  2 hotfix bridge, 1 ret-vt OOB, 1 ldlen-on-null. No single shared root > 2 tests.
- Confirmed NeoStep 401/0 (environment normal).

## Phase 2 -- fix the most tractable pin-able item (DelegateTest43 via Cgt_Un)
### Pinned root (JIT-dump-confirmed)
DelegateTest43's `if (OnIntEvent != null) throw` (after `OnIntEvent -= ...`) fires.
The CIL lowering is `ldsfld OnIntEvent; ldnull; cgt.un r1,r1,r2; brfalse.s r1, skip;
throw`. The `cgt.un` stays PLAIN (the TypeSpecializeNeoOpcodes Cgt/Cgt_Un block at
`JITCompiler.cs:1111` DELIBERATELY does not add a _Ref variant -- its comment claims
"Cgt_Un already has its Step-15 null-sentinel runtime arm").

The runtime `Cgt_Un` arm (`ILIntepreter.Neo.cs:2554`) computed:
`cguRes = cguA != -1 && ((uint)cguA > (uint)cguB || cguB == -1)`.
For the `!= null` idiom, ldnull lowers to the -1 sentinel so `cguB == -1` selects the
null-check path -> `cguRes = cguA != -1`. BUT under the Neo object model an IL-STATIC
reference/event field holding null is encoded as a NON-ZERO mStack index whose entry
IS null (the `mStack.Add(null)+index` Ldsfeld path, pinned in child-11). So after
`Delegate.Remove` nulls the event backing field, `ldsfld` loads a non-zero index -> the
`cguA != -1` check reads it as "not null" -> cgt.un returns 1 -> brfalse does not skip
-> throw. WRONG. (For Ldnull / CLR-static Ldsfeld, null IS the -1 sentinel, so those
paths worked -- the bug is specific to the IL-static non-zero-null-index encoding.)

### The fix (runtime-only, ~16 added / 3 removed, single file)
Mirror the sibling `Ceq_Ref` arm (`ILIntepreter.Neo.cs:2542-2550`, which resolves the
referenced object via `cra >= 0 ? mStack[cra] : null`). When `cguB == -1` (the
`ldnull; cgt.un` "!= null" idiom), resolve cguA's referenced object and test nullness:
`aIsNull = cguA == -1 || (cguA >= 0 && cguA < mStack.Count && mStack[cguA] == null)`;
`cguRes = !aIsNull`. The `cguB != -1` (genuine unsigned integer compare) path keeps the
prior logic (minus the now-dead `|| cguB == -1` clause). The `cguB == -1` case was
ALREADY special-cased by the prior `|| cguB == -1` clause as the null-sentinel path;
this only makes that null-check correct for BOTH null encodings (-1 sentinel AND
non-zero-index-to-null). No JIT / optimizer / object-model change.

### Why this is safe
- The `cgt.un` for FLOAT operands is specialized away to `Cgt_Un_R4`/`Cgt_Un_R8`
  (TypeSpecializeNeoOpcodes:2104) -- the plain `Cgt_Un` arm is never hit for floats.
- The `cguB == -1` integer edge (`cgt.un intX, -1`) was ALREADY divergent under the
  prior code (the comment documents the accepted divergence); this change does not
  introduce a new divergence for that edge -- it only corrects the reference null-check.
- Bounded mStack access (`cguA < mStack.Count`) guards the index read; a stale/out-of-
  range index falls through to the `cguA == -1` check (treated as null).
- The change is entirely inside `ILIntepreter.Neo.cs` (file-gated `#if
  ENABLE_NEO_MODE`) -> Legacy-neutral by construction.

## Verify (truth = full-smoke number)
- **FULL SMOKE: 24 -> 23.** `dotnet run -c Debug_Neo -f net8.0 --project
  ILRuntimeTestCLI --no-build -- TestCases/.../TestCases.dll HotfixAOT/.../HotfixAOT.patch true`
  -> `Ran 935 tests, 23 failded, 20 ignored, 7 todos`. The 23 are a STRICT SUBSET of the
  24 (only DelegateTest43 removed; zero new failures). DelegateTest43 alone (name-filter)
  -> `Ran 1 tests, 0 failded` (PASS).
- **NeoStep 0-failures: YES** -- `Ran 401 tests, 0 failded, 0 ignored, 0 todos` (no
  regression; the change only alters the `cguB == -1` null-check path).
- **Legacy-neutral: YES** -- `ILIntepreter.Neo.cs` is file-gated under `ENABLE_NEO_MODE`;
  the change is not compiled into the plain Debug (Legacy) build.

## Files (NOT committed -- LEAD commits)
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (Cgt_Un arm: cguB==-1
  null-check resolves the referenced object like Ceq_Ref).
- `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-24.md` (fresh re-cluster table).
- `rasen/changes/neo-recluster-24/ship-log.md` (this file).

## Remaining (reported, NOT addressed this child -- all distinct deep roots)
The 23 survivors (full table + pinned roots in fullsmoke-ground-24.md):
- Cluster A grab-bag (10): DelegateTest42, UnitTest_TestInline01 (ref-arg alias plain
  Call), UnitTest_TestFCP (struct-ctor reflection arg read -- JIT-pinned, exact line
  elusive), UnitTest_TestStackRegisterTransition3, ReflectionTest25,
  UnitTest_StaticTest05, StructTest12, TestForEach (ExpectException framework gap),
  UnitTest_10046 (delegate VT-arg / struct-newobj retDst), UnitTest_10051 (nested-
  struct constrained-callvirt property read).
- Cluster B autogen-binding (5): GenericMethodTest11, ReflectionTest10, StructTest11,
  MyTest.Test, TestGenericMethod2 (typeof(generic-param) mis-resolution).
- Cluster C (2): TestStructDictionary (ldfld.i4 NRE), RegisterVMTest04 (stfld.ref OOB).
- Cluster D (1): DelegateTest19 (ret-vt OOB on CLR enum return).
- Cluster E (1): ReflectionTest14 (ldlen-on-null, ambiguous source).
- Cluster G (2): StructTest6, UnitTest_NestedGenericRefOut (byref out-STRUCT).
- Cluster H (2): HotfixBasicTestCases.Test04/Test05 (Neo field-layout vs Legacy bridge).
