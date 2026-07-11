# Ship Log — neo-step22-generic-template

**Date:** 2026-07-07
**Branch:** features/object-model-overhaul
**Capability:** neo-optimizer (additive Requirements)
**Outcome:** SHIPPED. The in-memory generic-method template mechanism is
implemented, additive + Neo-only + Legacy-neutral, and CloneAndPatch is
structurally equivalent to the per-occurrence JIT for the full V1 matrix.

## Delivered

- **RunNeoBackHalf factoring** (`JITCompiler.cs`): the Neo-only T-dependent
  back-half (TypeSpecializeNeoOpcodes + AllocateLocalStackSpaces + LowerNeoOffsets
  + NeoExecuteBody clone) factored out of `Compile` into a callable method.
  `Compile` calls it inline (byte-identical). Shared JIT; Legacy-neutral.
- **PatchEntry + enums + holder** (`GenericMethodTemplate.cs`, new, Neo-only):
  `PatchField` (Operand/Operand2/Operand4), `PatchKind` (TypeToken/MethodToken/
  IsRefMoveFlag), `PatchEntry { InstrIdx, Field, Kind, GenericParamIdx, CecilToken }`,
  `GenericMethodTemplate` holder.
- **PatchEntry extractor** (`ExtractPatches`): records T-identity type-token
  sites via the body's `Symbols` (body-index -> Cecil instruction); auto-Initobj
  prefix excluded (rebuilt separately).
- **Template capture + per-definition cache**: the FIRST capture-eligible
  (ref/primitive typeArgs) concrete instantiation runs the per-occurrence JIT
  WITH a `templateCapture` hook (grabs the register-index body after
  CleanupRegister + addr + symbols + the auto-Initobj prefix registers) and
  stores the template on the open-definition `ILMethod`. (Compiling the open
  definition directly was rejected -- it corrupts shared AppDomain caches; see
  planning-context Findings.)
- **CloneAndPatch** (`DoCloneAndPatch`): clone template -> apply PatchEntry
  (re-resolve each Cecil token via the instance) -> rebuild the Initobj prefix
  for the concrete T (struct-T) -> shift branch Operands / SwitchTargets / addr
  by the prefix delta -> re-run RunNeoBackHalf.
- **Discrimination** (`TryInstantiate`): all-ref AND no T-identity token -> share
  ONE cached ref body; else CloneAndPatch.
- **Integration** (`ILMethod.InitCodeBody`): additive Neo-only hook; falls
  through to per-occurrence JIT when no template is cached.
- **V1 structural-equivalence self-check** (`NeoStep22SelfCheck.cs`, new,
  `#if ENABLE_NEO_MODE`, invoked via the CLI `NeoStep22SelfCheck` filter):
  compiles each matrix instance via BOTH paths + compares with `BodiesEqual`.
- **V2 functional roundtrip** (`TestCases/NeoStep22GenericTemplateTest.cs`, new):
  invokes the matrix generics via the template path + asserts correct results.

## Verification evidence

### V1 structural-equivalence self-check (the load-bearing gate): 55/55 PASS

Matrix: 11 methods x 5 T (int, long, object, IL-ref-class, IL-value-struct) =
55 cells. Each asserts `CloneAndPatch(template,T) == Compile(MakeGenericMethod(T))`
(length + Code + Register1/2/3 + Operand/2/3/4 per index -- all 24 bytes of
OpCodeR). All 55 pass. The 11 methods cover the T-dependence space:
- ProbeBasic, MakeArray, StoreRef, LoadRef, BoxIt (the round-0 set: T local/
  param/return, T[], ref T, Box T).
- **HashIt, EqualsIt** (`constrained.callvirt T.GetHashCode/Equals`) -- the
  BLOCKER-1 regression guard (the Constrained type-token).
- **CompareThem** (`constrained.callvirt IComparable<T>::CompareTo`) -- the
  MAJOR-2 regression guard (the T-qualified method token).
- **BranchIt, SwitchIt, TryCatch** -- struct-T + if/switch/EH control-flow (the
  Leave/Leave_S delta-shift guard).
Notably:
- ProbeBasic<Struct> body len=7 (vs 5) -- the Initobj-prefix rebuild correctly
  inserts the 2 extra struct-T Initobj ops; indices align.
- HashIt/EqualsIt/CompareThem report patches=2-3 (Constrained type-token + the
  paired callvirt method-token) -- both token sites patched + re-emitted.

### Regression gates

- NeoStep smoke: **205/205** (was 204/204 + the new V2 test; ZERO regressions).
- NeoOptHardening: **24/24**.
- NeoStep20: **9/9**.

### Legacy-neutral proof

Stash-toggle of the three Step-22 source files (JITCompiler.cs + ILMethod.cs +
GenericMethodTemplate.cs), plain `Debug` + `useRegister=true` NeoStep-filter:
SAME 8 pre-existing failures with and without the change. The RunNeoBackHalf
refactor is Neo-only + a behavior-preserving reorder of the CodeBody assignment;
the template mechanism compiles out `#if ENABLE_NEO_MODE`. Legacy is
byte-identical.

## Design-premise corrections at apply (3)

All fixable; none triggered the STOP discipline. Full detail in
planning-context.md `## Findings -- ... (apply, 2026-07-07)`.

1. **TypeSpecialize does NOT insert Initobj** (the propose-phase note said it
   does). The auto-Initobj prefix is a FRONT-HALF step (`JITCompiler.cs:346-381`).
   CloneAndPatch rebuilds it (struct-T case).
2. **The back-half does NOT re-derive T-identity tokens** (the open question's
   premise). Tokens are set in Translate; CloneAndPatch applies the PatchEntry
   table to re-resolve them. So PatchEntry-apply + re-run-TypeSpecialize are
   COMPLEMENTARY, not equivalent.
3. **Compiling the open definition corrupts shared caches.** The template is
   captured from the first concrete capture-eligible instantiation instead (the
   proven Compile path, with a capture hook).

## Edges discovered (out of scope)

- **Pre-existing runtime bug: Box on a generic-param ref-T** NREs at
  `ILIntepreter.Neo.cs:2749` in BOTH paths (not a Step-22 regression; confirmed
  by disabling the template path). The V1 structural self-check covers
  BoxIt<object>/BoxIt<RefClass> body equivalence; the V2 covers BoxIt<int>.
  Runtime fix is a separate Neo task.
- **struct-T x inliner CloneAndPatch gap (Step-23 follow-up):** the CallIt<Struct>
  probe (per-occ len 9 vs template len 8) surfaced that the inliner inserts an
  Initobj for struct temps that the prefix-rebuild does NOT reproduce. Not a
  round-1 regression (no smoke hits it; capture-eligible instantiations are
  ref/primitive). Must be re-audited at Step 23 -- the serializer cannot assume
  the Initobj prefix is the ONLY Initobj site.

## Review-loop round 1 (the dump-gate earned its keep)

Round-0 review found **BLOCKER-1** (the `Constrained T` type-token was never
patched -- `ExtractPatches`' symbol pointed at the trailing callvirt, not the
`constrained.` prefix's TypeReference -> stale-hash runtime dispatch for the
2nd+ constrained-on-T instantiation; a LIVE Neo regression, green smoke hid it).
Round-1 fixer (non-author) fixed it by capturing the `constrained.` prefix's
TypeReference + the paired callvirt's MethodReference directly from the CIL body
at capture time (`ConstrainedTypeTokens`/`ConstrainedMethodTokens`, in body
order), recording TypeToken + MethodToken patches on both ops, re-emitting via
`GetTypeTokenHashCode`/`GetMethodTokenHash`. Also fixed MAJOR-2 (the same
defect class for T-qualified callvirt method tokens) + a 4th bug (the EH
`Leave`/`Leave_S` opcodes were excluded from the CloneAndPatch delta-shift ->
struct-T EH leave targets unshifted). Matrix expanded 25->55 cells to guard all
three. Round-1 re-review (non-author) CONFIRMED all fixes via adversarial probes
(a two-Constrained-in-one-body probe, IComparable<T>::CompareTo, TryCatch<Struct>).
**Lesson reaffirmed:** the green smoke + 25-cell V1 matrix hid the Constrained
regression -- only the round-0 reviewer's blast-radius sweep + the expanded
matrix caught it. V1 byte-identical => correct-runtime is SOUND (the runtime
Constrained arm reads only in-body state; the type/method caches are keyed by
hash VALUE, not the capture instance; no path-dependent side-cache).

## Deferred (per plan)

- `.neo` binary format + serializer (Step 23).
- `ilrt_neoc` precompile CLI (Step 24).
- Runtime `.neo` loader + Cecil-decoupling (Step 25).
- Perf benchmarks (Step 26).

## Files

- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- RunNeoBackHalf,
  templateCapture, CaptureTemplate, capture call in Compile.
- `ILRuntime/Runtime/Intepreter/RegisterVM/GenericMethodTemplate.cs` (new) --
  PatchField/PatchKind/PatchEntry/GenericMethodTemplate + GenericMethodTemplateOps
  (ExtractPatches, StoreFromCapture, IsCaptureEligible, DoCloneAndPatch,
  TryInstantiate) + DEBUG test-hooks (CompilePerOccurrenceNeoBody,
  CompileViaTemplateNeoBody, ForceBuildTemplate, BodiesEqual).
- `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep22SelfCheck.cs` (new) --
  host-side V1 self-check.
- `ILRuntime/CLR/Method/ILMethod.cs` -- genericMethodTemplate field,
  GenericMethodTemplateCache accessor, StoreGenericTemplate, InitCodeBody hook.
- `ILRuntimeTestCLI/Program.cs` -- NeoStep22SelfCheck filter.
- `TestCases/NeoStep22GenericTemplateTest.cs` (new) -- matrix generic methods +
  V2 functional roundtrip.
- `openspec/changes/neo-step22-generic-template/{design.md(probe-removed n/a),
  specs/neo-optimizer/spec.md, tasks.md, ship-log.md, planning-context.md}`.
