## Context

This child lifts the multi-dim (rank-2+) NON-GOAL from the `neo-arrays`
capability. Unlike prior array children (Step 16 rank-1, step17-completion
ldelema, neo-array-completion rank-1 width matrix), this is NOT a "build the
mechanism" child -- the mechanism already exists. The dump (HEAD `0aafdb34`,
probed by the planner) shows multi-dim is substantially delivered:

- **Allocation (`new T[n,m]`).** The C# compiler lowers this to `newobj instance
  T[0...,0...]::.ctor(int32, int32)`. The Neo `Newobj` arm
  (`ILIntepreter.Neo.cs:2168`) routes a CLR-type newobj to
  `InvokeNeoClrMethod(clrCtor, true, ...)` (`:2242`). When a Neo redirect is
  registered (the test harness registers `System_Int32_Array2_Binding.Register`
  for `int[,]` and `System_Int32_Array3_Binding.Register` for `int[,,]`), the
  autogen `Ctor_*_Neo` delegate runs `new T[a1, a2]` and writes the result to
  the dest mStack ref slot (`System_Int32_Array2_Binding.cs:89-112`). With no
  redirect (`long[,]`, `string[,]`), it falls through to the reflection path
  `CLRMethod.Invoke` -> `cDef.Invoke(param)` (`CLRMethod.cs:557`); an array
  type's `ConstructorInfo.Invoke` DOES allocate the multi-dim array
  (reflection-correct for both engines). Probe `NeoStep16_MultiDimRank2Probe`
  PASSES.
- **Element Set (`a[i,j] = v`).** Lowers to `call instance void Set(int, int,
  T)`. The autogen `Set_*_Neo` redirect (`System_Int32_Array2_Binding.cs:51-60`)
  runs `instance[a1, a2] = a3`. With no redirect, reflection
  `def.Invoke(instance, param)` (`CLRMethod.cs:569`). Works for primitive
  elements.
- **Element Get (`a[i,j]`).** Lowers to `call instance T Get(int, int)`. There
  is NO `Get` redirect in the pre-generated binder (the binder registers only
  `Set` + ctor), so EVERY `Get` falls to reflection `def.Invoke(instance,
  param)`. Works for primitive elements (`int`, `long`).
- **Metadata (`a.Rank`, `a.Length`, `a.GetLength(i)`).** Method/property calls
  on the array -> reflection fallback. Probe `NeoStep16_MultiDimRank2Metadata`
  PASSES.

The autogen codegen for multi-dim ALREADY EXISTS for Neo
(`ConstructorBindingGenerator.cs:77-89` `isMultiArr` ctor branch;
`MethodBindingGenerator.cs:313-355` `isMultiArr` Get/Set/Address branch). The
`Address` (ldelema-equivalent) branch emits `instance.Address(...)` -- but no
binder registers an `Address` redirect, and the byref write-back epilogue is
correctly SKIPPED for multi-arr (`MethodBindingGenerator.cs:439`). `Address`
on a multi-dim array is out of scope (see Non-Goals).

Legacy (`ILIntepreter.Register.cs` `ExecuteR`) is the REFERENCE and is NOT
modified except for the shared Gap 3 unwrap. The Neo smoke baseline at HEAD is
190/190.

## Goals / Non-Goals

**Goals:**

- Declare multi-dim (rank-2/rank-3) primitive-element allocation + Get/Set +
  metadata DELIVERED in the `neo-arrays` spec (lift the NON-GOAL).
- Close Gap 1: Neo reference-element multi-dim Get/Set value correctness
  (mirror Legacy).
- Close Gap 2: Neo null-array-`this` -> `NullReferenceException` (mirror
  Legacy's explicit guard).
- Close Gap 3: unwrap `TargetInvocationException` from the reflection fallback
  on BOTH engines so the user-visible exception contract (`IndexOutOfRange`,
  `NullReference`, etc.) holds for multi-dim element access (and every other
  reflection-fallback CLR-method call).
- Adversarial probes MANDATORY: each of Gap 1/2/3 gets a FAIL-on-HEAD ->
  PASS-after stash-toggle proof; regression guards for the working primitive
  paths.

**Non-Goals:**

- **Multi-dim `Address` (ldelema-equivalent) / `fixed` on a multi-dim element.**
  The autogen `Address` branch exists but no binder registers it and it would
  feed the `neo-byref` Ref Slot model that rank-1 ldelema uses. The byref-
  through-multi-dim-element shape is a `neo-byref` follow-up (rank-1
  ref-array ldelema is itself a Step 17 deferred edge -- see neo-array-completion
  design.md TC15).
- **Non-zero-based lower bounds.** C# `new T[,,]` emits zero-based arrays; the
  `(T[,])Array.CreateInstance(type, lengths, lowerBounds)` shape is not
  reachable from C# element-access syntax and is out of scope.
- **IL value-type-element multi-dim arrays** (`MyStruct[,]`). The element copy
  semantics (primitive + ref copy per element) for a VT element via the
  reflection fallback is a deeper edge (the reflection `Get` returns a boxed
  struct; the Neo return write-back would need to unbox-copy). Not exercised
  by the smoke; deferred (documented in tasks).
- **Re-running the autogen binder generator to add `Get`/`Address` redirects
  for `int[,]`.** Not needed -- the reflection `Get` path works for primitives
  after Gap 1/2/3. Regenerating binders is a tooling concern, not an engine
  concern.

## Decisions

### D1: Gap 1 (reference-element Get/Set) -- Neo reflection-fallback fix, dump-pinned at apply

The `string[,]` probe FAILS on Neo (assertion), PASSES on Legacy. Two
candidate sites, to be pinned by a temp `Console.WriteLine` at apply:

- **Set side (param read):** `CLRMethod.cs:497-515` reads a reference param as
  `param[i] = mStack[idx]` (or null for `idx < 0`). For a `Set(int, int, T)`
  call, the value param is the 3rd arg; if it is read with the wrong cursor
  offset or wrong idx, the reflection `def.Invoke` stores the wrong reference.
- **Get side (return write):** `InvokeNeoClrMethod` (`ILIntepreter.Neo.cs:703-
  711`) writes a reference return as `mStack[targetRetRefBase] = res;
  *(int*)retDstPtr = targetRetRefBase;`. If `targetRetRefBase` collides with a
  live temp slot across the inline reads in the assertion, a later read could
  observe a stale reference.

**Why not design-blind:** F-10 / Q-NEWOBJ lessons -- a propose-time guess at
Set-vs-Get is exactly the kind of half-right hypothesis the dump-gate exists to
reject. The implementer adds a temp print of `(i, j, expected, actual)` for the
string round-trip; the first divergence localizes the bug to Set (store wrong)
or Get (read wrong). The FIX is then a one-line correction in the Neo overload
only. Legacy is byte-unchanged (Legacy passes).

**Discriminator:** this is NOT multi-dim-specific -- it is the general Neo
reflection-fallback reference-value path. But multi-dim is where it surfaces
(rank-1 uses Ldelem_Ref/Stelem_Ref, not reflection). The fix correctly lives in
`CLRMethod.Invoke` (Neo overload), not in any array-specific code.

### D2: Gap 2 (null array `this`) -- mirror Legacy's explicit guard

The Neo `HasThis` read (`CLRMethod.cs:399-404`) is:
```
int thisIdx = *(int*)(targetBase + curPrim);
instance = mStack[thisIdx];     // <-- mStack[-1] throws on null this
curPrim += 4;
```
A null array reference is the Neo null-ref sentinel (`thisIdx == -1`), so
`mStack[-1]` throws `ArgumentOutOfRangeException` (AutoList is a `List<object>`)
-- NOT `NullReferenceException`. Legacy (`:707-713`) reads the instance via
`StackObject.ToObject` (returns null for a null ref) and then explicitly
`if (instance == null) throw new NullReferenceException();`.

**Decision:** mirror Legacy. Read `thisIdx`; if `thisIdx < 0`, set `instance =
null`; then after the `HasThis` block, `if (instance == null) throw new
NullReferenceException();` (placed where Legacy has it, before the reflection
`def.Invoke`/`cDef.Invoke`). This also benefits any non-array reflection-
fallback call on a null `this`. Neo-only.

### D3: Gap 3 (TargetInvocationException unwrap) -- shared, Legacy-neutral gated

`MethodInfo.Invoke` / `ConstructorInfo.Invoke` wrap the target's thrown
exception in `TargetInvocationException` by CLR reflection contract. So when
the CLR multi-dim indexer throws `IndexOutOfRangeException` inside
`def.Invoke(instance, param)` (`CLRMethod.cs:569` Neo / `:720` Legacy), the
caller sees `TargetInvocationException` -- and the user's
`catch (IndexOutOfRangeException)` does not fire. Probe
`NeoStep16_MultiDimRank2OutOfRange` FAILS on BOTH engines identically.

**Decision:** wrap BOTH `def.Invoke` and `cDef.Invoke` calls in BOTH overloads
in a `try/catch (TargetInvocationException tie) { ExceptionDispatchInfo.Capture(
tie.InnerException).Throw(); throw; }`. `ExceptionDispatchInfo` preserves the
original stack trace (the Neo outer exception machinery + `CheckExceptionType`
rely on the real exception type, which is what the user catches). This is the
CATCH-COMPLETE / D-IL-EXCEPTION-THROW precedent (a shared-engine exception-
contract fix that benefits BOTH engines).

**Shared-engine gate (MANDATORY).** Legacy currently surfaces
`TargetInvocationException` for ANY reflection-fallback call that throws; after
the fix it surfaces the inner exception. This is strictly more correct, but it
is a behavioral change for Legacy. Legacy-neutral verification: plain `Debug`
+ `useRegister=true`, run the full 519-test Legacy baseline, confirm the count
of failures does not increase (the pre-existing ~1 long-unlocated failure
unchanged; no NEW failures). Stash-toggle the unwrap for the cleanest signal.

### D4: Lift the NON-GOAL in-place; do not create a new capability

Multi-dim stays under `neo-arrays`. The NON-GOAL block ("Multi-dimensional
arrays (`int[,]`)") is replaced with delivered Requirements. No new capability
spec. This matches the neo-array-completion precedent (which lifted the
generic-token / native-int non-goals in-place).

## Risks / Trade-offs

- **Gap 1 Set-vs-Get uncertainty (D1).** The implementer MUST dump-pin before
  editing. -> Mitigation: temp `Console.WriteLine` of `(i, j, expected, actual)`
  in the string probe path; the first divergence localizes the fix. If neither
  side shows a divergence (i.e. the bug is elsewhere -- e.g. the call-lowering
  param-region layout for a reference element), STOP and re-probe (Q-NEWOBJ
  lesson: a guessed fix is worse than a deferred one).
- **Gap 3 Legacy regression (D3).** The unwrap changes Legacy's surfaced
  exception type. -> Mitigation: Legacy-neutral stash-toggle; full 519-test
  baseline run. If a Legacy test DEPENDS on catching `TargetInvocationException`
  (unlikely -- no normal C# code catches it), that test would need investigation;
  none is expected.
- **Green smoke does not prove an exception-catch contract (Step 17 B1 / F-10).**
  -> Mitigation: each of Gap 1/2/3 gets a FAIL-on-HEAD stash-toggle proof. The
  string/null/outofrange probes FAIL on HEAD (confirmed by the planner) and
  MUST PASS after the fix; the primitive probes PASS throughout (regression
  guards).
- **`ExceptionDispatchInfo` availability.** The runtime targets netstandard2.1
  (`System.Runtime.ExceptionServices`). Available. Fallback if needed: a plain
  `throw tie.InnerException` (loses the original stack but preserves the type --
  sufficient for `CheckExceptionType`).

## Migration Plan

No migration: additive overload edits + spec text. Build CLI `Debug_Neo`
(`--no-incremental` after any host-type churn); build TestCases plain `Debug`
(NEVER `Debug_Neo`); run smoke `-f net8.0`. Rollback = revert the change (no
on-disk format / ABI change).

## Open Questions

- **OQ1 (resolve at apply, D1):** is the `string[,]` value bug on the Set side
  (reference-param read `CLRMethod.cs:497-515`) or the Get side (reference-
  return write `InvokeNeoClrMethod :703-711`)? Dump-pin via temp print; fix the
  Neo overload only. If the dump shows neither, STOP and flag for re-probe
  (possible call-lowering param-region layout issue for a reference element).
- **OQ2 (resolve at apply, D3):** does any Legacy test catch
  `TargetInvocationException` directly (and would therefore regress after the
  unwrap)? Grep the Legacy corpus + run the full 519-test baseline; expected
  answer: no.
