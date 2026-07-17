# Review Report: neo-callvirt-gettype-vtable

Wave-2 child C4 of `neo-overhaul`. Branch `features/object-model-overhaul`.
Reviewer: independent (did NOT write this code). Mandate: verify against real code + run.
Date: 2026-07-14.

## Verdict: APPROVE-WITH-FINDINGS

The change correctly fixes both sub-classes (4a callvirt GetType VTable-miss; 4b
stale Step-7 .cctor suppression) for the ACTIVE Cecil-based Neo path (`Debug_Neo`,
`ExecuteNeo` + Cecil load). All verification gates pass:
- NeoStep 380/0/0 (no regression).
- 4a spot tests (StaticTest01, ReflectionTest03) PASS under Neo.
- 4b spot tests (SimpleTest.StaticTest, GenericMethodTest18) PASS under Neo.
- All 4 spot tests still PASS under plain Debug + useRegister=true (Legacy-neutral).
- 2 "progressed" tests (DelegateTest36, ReflectionTest11) confirmed to now fail with
  DIFFERENT-cluster errors (not C4).

One Major finding (NeoAOT double-.cctor, unexercised by `Debug_Neo`) + two Minor
stale-comment findings. None block the C4 change for the active path. Recommend the
implementer address the NeoAOT finding (or explicitly defer it to Step 22-26).

---

## Findings

### MAJOR-1: NeoAOT (LoadNeoAssembly) path now double-invokes the .cctor

C4 lifts the Step-7 `.cctor` suppression at the lazy `StaticInstance` getter
(ILType.cs:214-232) so the getter now invokes `staticConstructor` under
`ENABLE_NEO_MODE`. This is correct for the Cecil path (the getter is the SOLE trigger
there). But the Cecil-free NeoAOT path (`AppDomain.LoadNeoAssembly`, AppDomain.cs:869-
890) ALREADY invokes the .cctor explicitly and was designed around the OLD invariant
that the getter does NOT invoke.

Evidence (load-bearing):
- ILType.cs:337 -- `StaticConstructorForNeoAOT { get { return staticConstructor; } }`
  returns the SAME `staticConstructor` field the getter checks.
- ILType.cs:1521 -- the Cecil-free builder SETS `t.staticConstructor` for any type
  with a `.cctor`, so for NeoAOT types `staticConstructor != null`.
- ILType.cs:209-213 -- for `isNeoAotType`, `cctorEligible` is unconditionally `true`.
- AppDomain.cs:882-883 -- LoadNeoAssembly does `_ = t.StaticInstance; Invoke(cctor,
  null, null);` and the explicit `Invoke` is NOT guarded by `staticConstructorCalled`.

Old behavior (site-1 suppressed under Neo): `_ = t.StaticInstance` set the flag without
invoking; the explicit `Invoke` ran the .cctor exactly once. New behavior: `_ =
t.StaticInstance` invokes the .cctor (getter, site 1) AND sets the flag; the explicit
`Invoke` then runs it a SECOND time. Net: double .cctor execution on the NeoAOT path.

This directly breaks the Step-25 S3-4 design invariant documented at ILType.cs:1511-
1513 ("the lazy StaticInstance getter's #if ENABLE_NEO_MODE suppression stays in
place ... S3-4 seeds ONLY the Cecil-free path") and ILType.cs:205-208.

Severity calibration: Major (real correctness regression + broken documented
invariant + in-tree/compiled into `Debug_Neo`), NOT Blocker because:
- The `Debug_Neo` test path uses Cecil loading (`LoadAssembly`), NEVER
  `LoadNeoAssembly`, so no test exercises this. NeoStep 380/0 confirms.
- NeoAOT (`ilrt_neoc`) is explicitly the Step 22-26 future optimization layer.
- Most .cctors (`= new Dictionary(...)`) are idempotent; a .cctor with external side
  effects (e.g. global registration) would double-fire.

Suggested fix (route to implementer): either (a) guard LoadNeoAssembly's explicit
`Invoke(cctor, ...)` with `if (!t.staticConstructorCalled)`, or (b) set
`t.staticConstructorCalled = true` BEFORE `_ = t.StaticInstance` so the getter's
site-1 Invoke is skipped and only the explicit seed runs (preserving the S3-4
"seed runs it exactly once" contract). Option (b) most closely preserves the
original design.

### MINOR-1: Stale Step-25 comment at ILType.cs:201-208 (getter)

The pre-existing Step-25 comment block ends: "The #if ENABLE_NEO_MODE suppression
below STAYS ... this guard only makes the CONDITION Cecil-free-safe, it does NOT lift
the suppression." C4 lifted exactly that suppression, so the last sentence is now
factually wrong and contradicts the new C4 comment block immediately below it
(ILType.cs:217-230). Update or remove the stale sentence.

### MINOR-2: Stale Step-25 comment at ILType.cs:1508-1516 (Cecil-free builder)

Same invariant, other side: "the legacy lazy StaticInstance getter's #if
ENABLE_NEO_MODE suppression stays in place on the Cecil ctor path -- S3-4 seeds ONLY
the Cecil-free path." No longer accurate after C4 (see MAJOR-1). Update to reflect
that the getter now also fires under Neo, and document the intended double-invoke
behavior (or the fix chosen for MAJOR-1).

### TRIVIAL-1: Review brief path typo (not a code defect)

The review brief located `ObjectGetTypeNeo` at `CLRBinding/CLRRedirections.cs`; the
actual file is `Runtime/Enviorment/CLRRedirections.cs` (the Legacy `ObjectGetType` it
mirrors is at the same file, line 1283). No impact; noted only so future readers find
it.

---

## Dimension-by-dimension verification

### Dim 1 -- Fix 4a (callvirt fallback) correctness + no-overfire: PASS

Routing confirmed end-to-end against real code:
- Generic `Callvirt` arm (ILIntepreter.Neo.cs:3671-3694) calls
  `ResolveNeoGenericCallvirtTarget` (:1285), which for an ILTypeInstance `this`
  delegates to `ResolveNeoCallvirtILTarget` (:1204).
- The new fallback (:1229-1230) returns `declaredMethod` (the CLRMethod) when
  `TryGetNeoVTableSlot` fails AND `declaredMethod is CLRMethod`.
- The returned CLRMethod flows to `InvokeNeoCallTarget` (:785) -> the `CLRMethod`
  branch -> `InvokeNeoClrMethod` (:1091), which serves `clrMethod.RedirectionNeo`
  (:1093-1100) before the reflection fallback (`clrMethod.Invoke`).
- `RedirectionNeo` (CLRMethod.cs:145-155) lazily resolves from
  `appdomain.RedirectMapNeo`; `RegisterCLRMethodRedirectionNeo` (AppDomain.cs:1141)
  populates that map, and AppDomain.cs:288 registers `ObjectGetTypeNeo` for
  `Object.GetType()`. So GetType hits the Neo redirect; an unregistered non-virtual
  inherited method (e.g. MemberwiseClone) would fall to reflection Invoke --
  reasonable, not a mask.

NO-OVERFIRE confirmed (the critical concern):
- `IsNeoVTableCandidate` (ILType.cs:711-726) returns true ONLY for virtual methods
  (ILMethod.IsVirtual / CLRMethod.MethodInfo.IsVirtual). `Object.GetType` is
  non-virtual -> not a candidate -> gets NO VTable slot -> `TryGetNeoVTableSlot`
  fails -> fallback. Correct.
- Virtual `System.Object` methods (ToString/Equals/GetHashCode) ARE virtual ->
  candidates -> `AddNeoBaseVirtualSlots` (ILType.cs:576-587) adds them as base
  virtual slots -> `TryGetNeoVTableSlot` SUCCEEDS -> returns the VTable entry (the
  override or the base) -> fallback NOT reached. So the fallback cannot swallow a
  legitimate IL-virtual dispatch.
- The fallback is gated on `declaredMethod is CLRMethod`. `Callvirt_IL` declares an
  ILMethod, and any IL virtual override is an ILMethod; a failing ILMethod lookup
  still throws the `MissingMethodException` (:1231). So an IL-virtual miss is never
  masked.
- The only theoretical mask: an INHERITED virtual CLRMethod (e.g. `Object.ToString`
  via an `object`-typed reference) whose VTable slot is somehow missing. That
  requires a separate VTable-construction bug (AddNeoBaseVirtualSlots would have to
  fail to add the base slot); it does not occur in practice and would fall back to
  the inherited CLR behavior, which is the correct degenerate result anyway.

`ObjectGetTypeNeo` semantics (CLRRedirections.cs:764-797): reads `this` via
`ReadNeoReference(frameBase, ref curPrim=0, mStack)`; for ILTypeInstance /
ILEnumTypeInstance returns `Type.ReflectionType`, else the CLR type. This mirrors
Legacy `ObjectGetType` (CLRRedirections.cs:1283-1298) exactly. The thisArgOffset=0
premise is confirmed by the JIT dump from the live run:
`callvirt r3, r0, System.Object::System.Type GetType(), vslot=65535, thisArg=0`.

### Dim 2 -- Fix 4b (.cctor change) safety: PASS for the Cecil path (the active path)

Stale-suppression claim verified: `git show` of the diff confirms both sites
previously had `#if ENABLE_NEO_MODE` blocks annotated "TODO Step 7: restore once Step
7 lands". Step 7 landed long ago (Stsfld IL-static arm at ILIntepreter.Neo.cs:4524,
Ldsfeld at :4693 -- both read `ilt.StaticInstance` at :4531/:4700). So the suppression
was genuinely stale.

Lazy-vs-eager split, verified safe for the Cecil path:
- Site 2 (InitializeMethods, ILType.cs:2434-2443) is now wrapped in
  `#if !ENABLE_NEO_MODE`, so under Neo the eager invocation (and its premature
  `staticConstructorCalled = true`) is SKIPPED. This is the key fix: the OLD eager
  site set the flag without invoking (suppressed), which then BLOCKED the lazy getter
  -- so the .cctor never ran and inline-initialized static fields stayed null.
- Site 1 (StaticInstance getter, :186-236) now invokes unconditionally when eligible,
  guarded by `staticConstructorCalled` (set at :200 before the Invoke) -> fires at
  most once per type and cannot recurse into itself.
- The lazy getter fires BEFORE any static-field read/write: Stsfld/Ldsfeld
  (:4524/:4693) read the `StaticInstance` property, whose getter materializes
  `staticInstance` (:196) THEN invokes the .cctor (:231). The .cctor's own Stsfld
  re-reads the property but sees `staticConstructorCalled == true` and returns the
  already-materialized storage. So no static field is observed null before its
  initializer runs. (The prior Fixed64..cctor Stsfld-VT-arm NRE -- caused by the eager
  site re-entering init before staticFieldTypes/storage was ready -- is resolved by
  deferring to the lazy getter, which runs only after InitializeMethods/Fields
  complete. Not papered over.)
- Side-effect ordering: lazy .cctor (fire-on-first-static-field-access) is strictly
  MORE CLR-correct than the old never-fire behavior, and matches the CLR
  beforefieldinit contract for field access. A static method that touches no static
  field will not trigger the .cctor under Neo -- but that is identical to the OLD Neo
  behavior (which never fired), so it is NOT a regression; it is a pre-existing
  limitation.
- Legacy-neutral: confirmed by construction AND empirically. Under `!ENABLE_NEO_MODE`
  site 1 invokes (unchanged) and site 2 runs (the `#if !ENABLE_NEO_MODE` block is
  active). Both are guarded by `staticConstructorCalled`, identical to the old Legacy
  behavior. Empirically: all 4 spot tests PASS under plain Debug + useRegister=true.

CAVEAT (see MAJOR-1): the safety argument above is for the Cecil path. The NeoAOT
(Cecil-free) path now double-invokes -- does not affect `Debug_Neo` tests but should
be addressed.

### Dim 3 -- No regression in init-heavy / static-field tests: PASS

NeoStep 380/0/0 (ran under `Debug_Neo`, useRegister=true). Static-field-specific spot
tests all green under Neo: SimpleTest.StaticTest (Dictionary inline initializer),
StaticTest01, GenericMethodTest18. See Dim 4.

### Dim 4 -- Full-smoke delta 153 -> 140: spot-confirmed

Cannot cheaply re-run the entire full smoke, but every named C4 test was run
individually and the regression guard (NeoStep) is green:

| Test | Cluster | Neo (Debug_Neo) | Legacy (Debug+reg) |
|---|---|---|---|
| StaticTest01 | 4a | PASS (1/0) | PASS (1/0) |
| ReflectionTest03 | 4a | PASS (1/0) | PASS (1/0) |
| SimpleTest.StaticTest | 4b | PASS (1/0) | PASS (1/0) |
| GenericMethodTest18 | 4b incidental | PASS (1/0) | PASS (1/0) |
| NeoStep (regression) | -- | 380/0/0 | -- |

The GetType VTable-slot error is gone (StaticTest01 JIT dump shows GetType dispatched
as `vslot=65535, thisArg=0` -- the fallback path -- and the test passes).

### Dim 5 -- "Progressed to other cluster" honesty: CONFIRMED (not regressed)

Spot-ran two "progressed" tests under Neo; both now fail with a DIFFERENT-cluster
error, not the C4 GetType/this-null:
- DelegateTest36: `System.NotSupportedException: Derived classes must provide an
  implementation` (ExecuteNeo, ILIntepreter.Neo.cs:3642). Genuinely past the C4
  GetType path; now a separate abstract-dispatch issue.
- ReflectionTest11: `System.NotSupportedException: Not supported opcode Add_I8`
  (ExecuteR, ILIntepreter.Register.cs:5325). Different cluster (C12/add-i8 opcode),
  consistent with the tasks.md follow-up `neo-add-i8-opcode`.

Both progressed, neither regressed.

---

## Build / run evidence

- `dotnet build ILRuntimeTestCLI -c Debug_Neo --no-incremental`: 0 errors (268
  warnings, all pre-existing CS0219/CS1668).
- `dotnet build TestCases -c Debug`: 0 errors (87 warnings).
- `dotnet build ILRuntimeTestCLI -c Debug --no-incremental` (Legacy build): 0 errors.
- Neo spot tests + NeoStep: run via `dotnet run -c Debug_Neo -f net8.0 --no-build`.
- Legacy spot tests: run via `dotnet run -c Debug -f net8.0 --no-build`, useRegister=true.

## Recommendation

APPROVE-WITH-FINDINGS. Ship the C4 change for the active Cecil-based Neo path. Route
MAJOR-1 (NeoAOT double-.cctor) + MINOR-1/MINOR-2 (stale Step-25 comments) to the
implementer. MAJOR-1 fix is trivial (guard or flag-ordering at LoadNeoAssembly:882-
883) and can land in this child or be explicitly deferred to the Step 22-26 NeoAOT
work; either way the invariant at ILType.cs:1511-1513 must be re-documented to match
the new reality.
