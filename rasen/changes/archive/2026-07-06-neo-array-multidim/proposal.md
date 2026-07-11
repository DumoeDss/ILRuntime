## Why

The Neo rank-1 array story (Step 16 + step17-completion + neo-array-completion)
explicitly DEFERRED multi-dimensional arrays (rank-2+) to this child as a
`neo-arrays` NON-GOAL. The deferred-items doc (`neo-deferred-items.md`) and the
portfolio run both list `neo-array-multidim` as a remaining smaller completion.

The LEAD's propose-time orientation hypothesized the gap was either (a) a
missing autogen binder for `int[,]`, or (b) the reflection-fallback array-ctor
path in `CLRMethod.Invoke` (`cDef.Invoke(param)` failing for an array
ConstructorInfo). **Both hypotheses are DISPROVEN by the HEAD dump.** A minimal
probe (`int[,] a = new int[2,3]; a[1,2] = 42; return a[1,2];`) PASSES on HEAD
`0aafdb34`. The autogen binder `System_Int32_Array2_Binding` IS registered in
the test harness and emits Neo redirects for the array ctor + `Set`; the
reflection-fallback ctor + `Set` + `Get` ALSO work for primitive element types
(`long[,]`, no binder, full reflection). Rank-3 and metadata (`Rank` /
`Length` / `GetLength`) work too.

The dump instead surfaced THREE real gaps, all in the **reflection-fallback
path** (`CLRMethod.Invoke`), none in the autogen binder or the array ctor:

1. **Gap 1 (Neo-specific, value correctness).** A reference-element multi-dim
   array (`string[,]`) round-trips WRONG on Neo (the assertion fires); Legacy
   is correct. Primitive-element arrays (`int`, `long`) are unaffected.
2. **Gap 2 (Neo-specific, exception contract).** Indexing a null multi-dim
   array does NOT surface `NullReferenceException` on Neo (it throws a
   different exception from the unguarded `mStack[-1]` read); Legacy has the
   explicit null-`this` guard and surfaces NRE correctly.
3. **Gap 3 (shared-engine, exception contract).** `MethodInfo.Invoke` /
   `ConstructorInfo.Invoke` WRAPS the underlying thrown exception (e.g. the
   CLR indexer's `IndexOutOfRangeException`) in `TargetInvocationException` on
   BOTH engines, so a user's `catch (IndexOutOfRangeException)` does not fire.
   Identical failure on Neo and Legacy.

This change closes those three gaps, LIFTS the multi-dim NON-GOAL in
`neo-arrays`, and adds regression-guard probes for the paths that already work.

## What Changes

- **Lift the multi-dim NON-GOAL in `neo-arrays`**: primitive-element rank-2/rank-3
  allocation + element Set + element Get + metadata are declared DELIVERED
  (autogen binder path + reflection-fallback path). New spec Requirements cover
  multi-dim allocation, Get/Set, metadata, and the exception contract.
- **Gap 1 fix (Neo-only): reference-element Get/Set value marshaling in the
  reflection fallback.** The Neo `CLRMethod.Invoke(byte*, AutoList, bool)`
  overload (`CLRMethod.cs:333`) mis-marshals a reference-type element for a
  multi-dim `Get`/`Set` call (no autogen redirect). The implementer MUST
  dump-pin whether the bug is in the Set (reference-param store at `:497-515`)
  or the Get (reference-return write-back in `InvokeNeoClrMethod` at
  `ILIntepreter.Neo.cs:703-711`) via a temp debug print, then fix the Neo
  overload only (Legacy is the reference and is correct). This is a general
  Neo reflection-fallback reference-value gap that multi-dim surfaces.
- **Gap 2 fix (Neo-only): null-`this` guard.** Add the explicit
  `if (instance == null) throw new NullReferenceException();` guard to the Neo
  `CLRMethod.Invoke` `HasThis` branch (mirror Legacy at `:712-713`), handling
  the Neo null-ref sentinel (`thisIdx < 0`).
- **Gap 3 fix (shared, Legacy-neutral gated): unwrap
  `TargetInvocationException`.** Wrap `def.Invoke(instance, param)` /
  `cDef.Invoke(param)` in both `CLRMethod.Invoke` overloads (Neo `:557`/`:569`,
  Legacy `:694`/`:720`) with a `try/catch (TargetInvocationException)` that
  `throw`s `InnerException` (using `ExceptionDispatchInfo.Capture` to preserve
  the original stack). Mirrors the CATCH-COMPLETE / D-IL-EXCEPTION-THROW
  shared-engine precedent. Legacy-neutral verification REQUIRED (plain `Debug`
  + `useRegister=true`; the 519-test Legacy baseline must not regress).
- **Adversarial probes + regression guards** in `TestCases/NeoStep16Test.cs`
  (already scaffolded by the planner): rank-2 int round-trip, multi-cell
  distinct values, rank-3, metadata (`Rank`/`Length`/`GetLength`), `long[,]`
  full-reflection, `string[,]` (FAIL-on-HEAD -> PASS-after), out-of-range
  caught (FAIL-on-HEAD both engines -> PASS-after), null array NRE caught
  (FAIL-on-HEAD Neo -> PASS-after).

## Capabilities

### New Capabilities

_(None.)_ Multi-dim is hosted under the existing `neo-arrays` capability (the
NON-GOAL is lifted in-place).

### Modified Capabilities

- `neo-arrays`: LIFT the "Multi-dimensional arrays (`int[,]`)" NON-GOAL. ADD
  Requirements for multi-dim allocation (`newobj` on the array ctor ->
  autogen redirect for registered types, `Array.CreateInstance`-equivalent via
  reflection for unregistered), element Get/Set (call to the array type's
  `Get`/`Set` -> autogen redirect for registered types, reflection
  `MethodInfo.Invoke` for unregistered), metadata (`Rank`/`Length`/
  `GetLength`), and the exception contract (out-of-range ->
  `IndexOutOfRangeException`; null array -> `NullReferenceException` -- both
  surfacing through the Neo outer exception machinery unwrapped from
  `TargetInvocationException`).

## Impact

- `ILRuntime/CLR/Method/CLRMethod.cs` -- (Gap 1) Neo `Invoke(byte*, AutoList,
  bool)` overload reference-element marshaling fix; (Gap 2) Neo `HasThis`
  null-guard; (Gap 3) shared `TargetInvocationException` unwrap in BOTH
  overloads (Neo + Legacy). The Gap 3 unwrap is the only shared-engine edit;
  gate it Legacy-neutral.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- possibly
  `InvokeNeoClrMethod` (`:634-712`) reference-return write-back IF Gap 1's
  dump pins the bug to the return path (apply-time determination).
- `TestCases/NeoStep16Test.cs` -- the `NeoStep16_MultiDim*` probes (8 already
  scaffolded; the implementer refines/finalizes keeper set; 3 are FAIL-on-HEAD
  load-bearing, 5 are regression guards).
- `openspec/specs/neo-arrays/spec.md` -- merge deltas (archive step).
- `openspec/changes/neo-array-multidim/planning-context.md` -- appended
  findings (already present from propose).
- `.trae/documents/neo-deferred-items.md` -- mark the multi-dim pointer
  RESOLVED (rank-2+).

**Regression risk: MEDIUM.** Gap 1 and Gap 2 are Neo-only (the Neo reflection
fallback, isolated). Gap 3 is a shared-engine behavioral change (Legacy
currently surfaces `TargetInvocationException`; after the fix it surfaces the
inner exception -- strictly more correct, but a Legacy-neutral regression run
is MANDATORY). Gate: full `NeoStep` smoke (190/190 baseline + new probes) +
Legacy-neutral stash-toggle for Gap 3 (the unwrap). Adversarial probes
MANDATORY (Step 17 B1 / OPT-HARDEN K1 / F-10 lessons: a green smoke does NOT
prove an exception-catch contract correct -- construct the FAIL-on-HEAD ->
PASS-after proof for each of Gap 1/2/3).
