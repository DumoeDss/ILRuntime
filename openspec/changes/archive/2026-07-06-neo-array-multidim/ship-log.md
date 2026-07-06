# Ship Log — neo-array-multidim

> Change: `neo-array-multidim` (portfolio child of `neo-completion-portfolio`)
> Capability: `neo-arrays`
> Branch: `features/object-model-overhaul`
> Date: 2026-07-06
> Pipeline: small-feature (auto-decompose childPipeline), Tier A, full autonomy.

## Verdict

**SHIPPED — clean.** Review verdict **APPROVE**, 0 Blocker / 0 Major (review-loop
clean on round 0; 1 Minor + 1 Trivial accepted-known, no fix required).

## Delivered scope

Multi-dimensional array (rank-2+) correctness completion for the Neo engine.
The autogen-binder path (`int[,]` / `int[,,]` ctor + `Get`/`Set` indexer) already
worked on HEAD; this change closed **3 reflection-fallback defects** in
`CLRMethod.cs` / `ILIntepreter.Neo.cs` that surfaced once the dump probed beyond
the autogen path:

1. **Gap 1 (Neo-only, value correctness)** — `InvokeNeoClrMethod` reference-return
   branch (`ILIntepreter.Neo.cs`, `InvokeNeoClrMethod` ~:703-720). A null
   reference return from a CLR method (e.g. reading a null `string[,]` element)
   was encoded as `targetRetRefBase` (a valid mStack index) instead of the null
   sentinel `-1`, so the `cgt.un` null-test (`cguA != -1 && ...`) read a null
   element as "not null" -> false-positive `!= null`. Fix: write `-1` for a null
   return (mirrors `Ldnull` + the `idx < 0 ? null` convention). Non-null path
   byte-identical.
2. **Gap 2 (Neo-only, exception correctness)** — `CLRMethod.Invoke` Neo `HasThis`
   branch (~:401-413): `instance = thisIdx < 0 ? null : mStack[thisIdx]`. A null
   multi-dim array `this` previously read `mStack[-1]` ->
   `ArgumentOutOfRangeException`; now resolves to null and the existing
   null-instance guards throw `NullReferenceException` (matches Legacy).
3. **Gap 3 (SHARED-engine, exception correctness)** — `TargetInvocationException`
   unwrap at **6** reflection `Invoke` sites in `CLRMethod.cs` (both overloads:
   Neo ~:555/569/582, Legacy ~:697/710/737). `MethodInfo.Invoke` /
   `ConstructorInfo.Invoke` wraps a native `IndexOutOfRangeException` (and any
   other thrown exception) in `TargetInvocationException`, so a user
   `catch (IndexOutOfRangeException)` failed on BOTH engines. Each site now does
   `try { ... } catch (TargetInvocationException tie) {
   ExceptionDispatchInfo.Capture(tie.InnerException).Throw(); throw; }`
   (`using System.Runtime.ExceptionServices;`). Stack-preserving; type-general
   (verified with `OverflowException` at the newobj-ctor site, not just
   `IndexOutOfRange`); **Legacy-neutral** (a non-throwing call is byte-identical;
   no existing test catches `TargetInvocationException` directly — `grep` = 0).

**Diff scope:** `CLRMethod.cs` +24/-8, `ILIntepreter.Neo.cs` +23/-3,
`TestCases/NeoStep16Test.cs` +105 (8 keeper probes). No autogen / JIT / Newobj-arm
change.

## Verification evidence

- **Neo `NeoStep` smoke: 198/198** (190 baseline + 8 `NeoStep16_MultiDim*`
  probes). The 3 formerly-failing probes (String/Null/OutOfRange) now PASS.
- **Neo `NeoOptHardening`: 24/24** (no CLR-struct / FCP regression).
- **Legacy multi-dim: 8/8** (incl. OutOfRange -> Gap 3 is Legacy-correct too).
- **Legacy full baseline stash-toggle:** HEAD = 723/11 fail (incl. OutOfRange);
  with fix = 723/10 fail (OutOfRange passes). The 10 remaining are IDENTICAL
  pre-existing Neo-under-Legacy failures -> **zero regression, strict
  improvement** (mirrors CATCH-COMPLETE / D-IL-EXCEPTION-THROW precedent: a
  genuine shared-engine improvement, not a Neo workaround).
- **FAIL-on-HEAD stash-toggle (reviewer, independent):** stashed the 2 engine
  files (kept tests), rebuilt CLI at HEAD -> 6 fail / 5 pass with the EXACT
  root-cause signatures (Gap 1 divide-by-zero assertion, Gap 2
  "Index was out of range (mStack[-1])", Gap 3 "Exception has been thrown by the
  target of an invocation"). After `stash pop` + rebuild: 11/11 pass. Proves all
  3 fixes load-bearing.
- **Adversarial probes (reviewer, removed after verification):** 3 temporary
  `NeoStep16_MultiDimReview_*` probes (symmetric `== null` direction, newobj-ctor
  TIE unwrap with `OverflowException`, no-double-unwrap) verified on Neo + Legacy,
  then removed. Test diff back to 8 keepers.

## Review findings (recorded, not blocking)

- **[M1] Minor, accepted-known, out of scope:** the autogen `op_Inequality_3_Neo`
  redirect reads a null arg via `ReadNeoReference` (`mStack[idx]`, no null guard)
  -> would throw `ArgumentOutOfRangeException` for a null string arg. Pre-existing
  (autogen tooling), NOT exercised by keeper probes, correctly deferred to a
  neo-byref / autogen-hardening follow-up.
- **[T1] Trivial, theoretical:** the 6 Gap 3 sites do
  `EDI.Capture(tie.InnerException)` with no null-InnerException guard; a TIE with
  null InnerException would mask as ArgumentNullException. Cannot occur in
  practice (reflection always sets a non-null InnerException). Optional defensive
  hardening only.
- **New edge discovered (deferred, Non-Goal):** IL value-type element `[,]`
  (`MyStruct[,]`) stays a Non-Goal (element copy semantics not exercised; the
  autogen path's VT-element handling is a separate surface).

## Deferred / Non-goals

- IL VT-element `[,]` (element copy semantics).
- Non-zero-based lower-bound arrays (`Array.CreateInstance(type, lo, len, ...)`)
  -- the C# compiler does not emit these for `new T[,,]`; out of scope.
- The M1 autogen `op_Inequality_3_Neo` null-arg edge -> neo-byref / autogen-
  hardening follow-up.
- The deferred-items doc `neo-deferred-items.md` D-ARR row updated: multi-dim
  RESOLVED (rank-2+ allocation + Get/Set + metadata); the rank-1 F-4
  UIntPtr-primitive / ref-array upstream edges remain as before (unrelated).

## Capability spec delta (to merge at archive)

`openspec/specs/neo-arrays/spec.md`:
- **MODIFIED:** the "Multi-dimensional arrays (`int[,]`)" Non-goal bullet is
  REMOVED (multi-dim is now in-scope).
- **ADDED:** Requirements for (1) multi-dim allocation (`newobj` on the array
  ctor), (2) element Get/Set (call to the array type's `Get`/`Set`), (3) rank /
  `Length` / `GetLength` metadata, (4) `IndexOutOfRangeException` surfacing
  (Gap 3 unwrap), (5) null-array `NullReferenceException` (Gap 2). With
  Scenarios each.

## Lessons reaffirmed

- **Probe before designing (Q-NEWOBJ / K1 / F-10).** The LEAD's orientation
  hypothesis ("the reflection-fallback array ctor likely throws
  NotSupportedException") was DISPROVEN by the planner's HEAD probe: `int[,]`
  Get/Set PASSES via the autogen binder. The dump found the real (3) defects,
  which the propose-time hypothesis entirely missed.
- **A green smoke does not prove a fix correct (Step 17 B1).** The reviewer's
  independent FAIL-on-HEAD stash-toggle + the symmetric-direction / different-
  exception-type / ctor-site probes were the proof; the keeper smoke alone
  would have looked green for the wrong reasons.
- **Shared-engine fixes can be genuine Legacy improvements (CATCH-COMPLETE
  precedent).** Gap 3 was a Legacy bug too (TIE-wrapping is reflection behavior,
  engine-agnostic); fixing both arms is Legacy-neutral-by-improvement, not a Neo
  workaround.
