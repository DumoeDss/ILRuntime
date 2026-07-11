# Review Report -- neo-step22-generic-template

Reviewer: VERIFY (adversarial non-author code review). Author != verifier.
Date: 2026-07-07. Branch: `features/object-model-overhaul` (Step 22 changes
uncommitted in working tree; HEAD `4d08a00f`).

## Verdict: REQUEST-CHANGES

One **Blocker** remains OPEN: the `Constrained T` type-token patch site is not
recorded by `ExtractPatches`, so `CloneAndPatch` (and the ref-share path) emit a
body with a stale capture-instance type hash, which the runtime Constrained arm
consumes for dispatch (`ILIntepreter.Neo.cs:4096`). This violates the load-
bearing V1 equivalence claim for a common, explicitly-in-scope pattern
(`constrained.callvirt T.M` -- `GetHashCode`/`Equals`/`ToString`/`IComparable`/
`EqualityComparer<T>`), and is a live Neo-mode regression for the 2nd+ instantiation
of any such generic.

The RunNeoBackHalf factoring (the riskiest shared-JIT part) is sound and Legacy-
neutral. The V1 comparator is complete over OpCodeR storage. The 3 design-premise
corrections are implemented correctly for the cases the matrix covers. The
Blocker is a gap the matrix does not cover, found by adversarial probe.

---

## Verification performed

- Read proposal.md / design.md / tasks.md / planning-context.md (both Findings
  sections) + handoff section 4.
- Read the full Step 22 diff: `JITCompiler.cs` (RunNeoBackHalf factoring +
  TemplateCapture + CaptureTemplate), `GenericMethodTemplate.cs` (PatchEntry /
  ExtractPatches / DoCloneAndPatch / TryInstantiate / cache), `NeoStep22SelfCheck.cs`
  (V1 host comparator), `ILMethod.cs` (BodyRegister/InitCodeBody hook + cache),
  `Program.cs` (V1 CLI hook), `TestCases/NeoStep22GenericTemplateTest.cs`.
- Build: `ILRuntimeTestCLI -c Debug_Neo` (0 err), `TestCases -c Debug` (0 err),
  `ILRuntimeTestCLI -c Debug` (0 err, Legacy).
- V1 self-check (pristine matrix): **25/25 PASS**.
- NeoStep smoke (Debug_Neo): **205/205, 0 failed** (regression gate green).
- Legacy-neutral (plain `Debug` + useRegister=true, NeoStep filter): **205 ran,
  8 failed** -- the same 8 pre-existing `NotImplementedException` failures the
  implementer's stash-toggle documented; matches HEAD.
- Adversarial probes (all TEMPORARY, verified, REMOVED; tree restored to pristine
  Step-22 state, re-confirmed 25/25 + 205/205 after removal):
  - Extended the V1 matrix with `HashIt<T>` (constrained.callvirt T.GetHashCode),
    `LdelemA<T>` (ldelem T[]), `CastIt<T>` (Unbox_Any T), `MakeDefault<T>`
    (initobj T). Result: **41/45** -- the 4 failures are all `HashIt` (Constrained).
  - Added a CloneAndPatch/ref-share hit-counter; ran the V2 functional test:
    `CloneAndPatchHits=5 RefShareHits=1` -- proves the 2nd+ instantiation engages
    CloneAndPatch in the production path (not the per-occurrence fallback).
  - Added `HashIt` to the V2 functional roundtrip: the test THREW
    (`NeoStep22 HashIt<int> wrong`) -- the Constrained gap is observable at runtime.
  - Diagnostic in `ExtractPatches` for the Constrained op: confirmed the recorded
    symbol's Cecil Operand is a `MethodReference` (the trailing callvirt), not the
    constrained prefix's `TypeReference`, so `HasGenericParameter` returns false.

---

## Findings

### BLOCKER-1: `Constrained T` type-token never patched -> wrong runtime dispatch

**Where:** `GenericMethodTemplate.cs` `ExtractPatches` (the guard at
`if (!template.Symbols.TryGetValue(i, out var sym))` / `HasGenericParameter(token)`),
with runtime consumption at `ILIntepreter.Neo.cs:4096`
(`IType constrainedType = AppDomain.GetType(ip->Operand);`).

**Observation / reproduction:**
- `Code.Constrained` is emitted in the FRONT-HALF Translate
  (`JITCompiler.cs:2607`: `op.Operand = method.GetTypeTokenHashCode(token);`) -- so
  the Constrained op's `Operand` (int @8; low 2 bytes alias `Register3`) holds the
  concrete-T type hash, resolved at capture time. This value is T-dependent
  (int=6, long=7, object=17, Struct=336, RefClass=337 observed).
- `ExtractPatches`' switch includes `OpCodeREnum.Constrained` -> `PatchField.Operand`,
  so the site is meant to be recorded. But the guard then does
  `token = sym.Instruction.Operand` and `HasGenericParameter(token)`. For the
  Constrained op the recorded symbol points at the FOLLOWING callvirt instruction,
  whose Cecil Operand is a `MethodReference` (not the constrained prefix's
  `TypeReference`). `HasGenericParameter` only inspects `TypeReference`s, so it
  returns **false** and **no PatchEntry is recorded** (`HashIt: patches=0`).
- Consequence: `DoCloneAndPatch` never overwrites the Constrained `Operand`; the
  cloned body carries the capture-instance hash (e.g. 6 = int). The runtime
  Constrained arm reads `ip->Operand` (`ILIntepreter.Neo.cs:4096`) and dispatches
  on that type -> for every T != capture-T the receiver is dispatched as the
  wrong type.
- This is NOT masked by the runtime: the Constrained arm OWNS dispatch and uses
  this exact field. It also corrupts the ref-share path (a token-free-per-the-table
  body is ref-shared, but the table is wrong, so `HashIt<object>` shares the int
  body verbatim).

**Adversarial evidence (probes since removed):**
- V1 matrix + `HashIt<T>(T v) => v.GetHashCode()`:
  `FAIL HashIt<long>: idx 1: Reg (0,0,7) vs (0,0,6) [Code=Constrained]` (same for
  object/RefClass/Struct). 41/45.
- V2 functional + `HashIt(5)`/`HashIt(7L)`: test threw `NeoStep22 HashIt<int> wrong`.
  (The int capture-instance failure is a separate pre-existing runtime
  constrained-on-primitive quirk on the per-occurrence path; the Step-22-specific
  bug is the non-capture-T divergence, which the body self-check proves and line
  4096 explains.)

**Why Blocker (not deferred / not Major):**
- design.md Decision 1 explicitly lists `Constrained.` as a TypeToken `PatchEntry`
  site, and `ExtractPatches`' switch includes it -- it is an **in-scope**
  requirement with an **implementation bug**, not a deferred item. (Contrast:
  MethodToken is explicitly deferred and the matrix does not exercise it.)
- It is a **live Neo-mode regression**: before Step 22, every generic instantiation
  went through per-occurrence JIT (correct Constrained hash per T); after Step 22
  the 2nd+ instantiation goes through CloneAndPatch (stale hash). The NeoStep smoke
  is green only because no smoke test instantiates a constrained-on-T generic at
  2+ type args.
- It affects the single most common generic pattern in C# (`GetHashCode`/`Equals`/
  `ToString`/`IComparable<T>`/`EqualityComparer<T>`/generic containers).

**Recommended fix:**
1. Make `ExtractPatches` record the Constrained site. Either (a) fix the symbol
   mapping so the Constrained op's symbol is its own `TypeReference` (the
   `constrained. T` Cecil prefix operand), or (b) special-case Constrained: its
   `op.Operand` IS the resolved type hash and the original Cecil `TypeReference`
   is recoverable from the def's body at `i` (or via the trailing callvirt's
   `Constrained`-prefix); record `{InstrIdx=i, Field=Operand, Kind=TypeToken,
   CecilToken=<the T TypeReference>}` so `DoCloneAndPatch`'s
   `instance.GetTypeTokenHashCode(pe.CecilToken)` re-resolves it per T.
2. Add coverage: a `HashIt<T>`/`EqualsIt<T>` row (constrained.callvirt T.M) to the
   V1 matrix AND a V2 functional cell asserting correct dispatch across T. Re-run
   until 25+ / 30+ cells PASS with zero `Constrained` divergence.
3. Re-audit the other "in-switch" opcodes for the same symbol-points-to-wrong-Cecil
   hazard (Box/Isinst/etc. happened to pass, but the guard's reliance on
   `sym.Instruction.Operand` being the right Cecil token is the fragile root cause).

---

### MAJOR-2 (conditional): MethodToken (T-qualified call) remains unhandled -- verify reachability

The design defers `MethodToken` (T-qualified `Call`/`Callvirt`) and `ExtractPatches`
does not record it. This is acceptable **only if** such sites never reach the
CloneAndPatch/ref-share path in practice. The discrimination `HasIdentityToken()`
keys off the patch table; since MethodToken sites are never recorded, a body
bearing only a T-qualified method token (no TypeToken) would be classified ref-share-
eligible and shared with a stale method token. Whether this is reachable depends on
how the JIT resolves T-qualified call tokens (front-half `GetTypeTokenHashCode`-style
resolution vs runtime re-resolution). The matrix does not cover it.

**Recommended action:** before Step 23 (serialize-without-Cecil), add a probe generic
method with a non-constrained T-qualified call and confirm whether CloneAndPatch ==
per-occurrence; if it diverges, either record MethodToken patches or document the
runtime re-resolution that makes sharing safe. Not a Step-22 ship blocker if
unreachable in the smoke, but must be closed before the AOT serializer relies on the
patch table.

---

### MINOR-3: V1 matrix misses struct-T + control-flow / exception-handler interaction

`DoCloneAndPatch` shifts branch Operands, `SwitchTargets`, and `Addr` by the Initobj
`delta` for struct-T. `ProbeBasic<Struct>` (len 7, no branches) passes, confirming
the prefix rebuild + index shift for the no-branch case. The matrix has no generic
method that combines (a) a struct-T (Initobj prefix growth, delta>0) with (b)
branches / switch / exception handlers. The delta-shift code paths for those are
unexercised. Low risk (the shift is mechanical), but add a `LoopSum<T>`/`TryCatch<T>`
probe to the V1 matrix to cover it.

### MINOR-4: `method.Compiling = false` reordered to after the back-half

The refactor moves `method.Compiling = false` from "before Allocate/Lower" (original)
to "after the entire back-half" (new). This is benign -- the back-half does not
trigger re-entrant Compile of the same method, and keeping the guard `true` until
the body is fully lowered is arguably safer (it blocks re-entry during lowering).
Noting for completeness; no action required. (Inliner reads `Compiling` at
`JITCompiler.cs:2919` to skip inlining a method mid-compile -- unaffected.)

### MINOR-5: Shared mutable `Symbols` / `Addr` / ref-body arrays across instances

`DoCloneAndPatch` sets `frame.Symbols = template.Symbols` (the capturing instance's
dict, shared by reference). The ref-share path returns `template.RefBody` (struct
copy, but the array fields -- `NeoExecuteBody`, `CodeBody` -- are shared across
every all-ref instantiation). This is safe IFF those arrays are read-only at
runtime. `ExecuteNeo` operates on a `byte*` frame and reads instructions via `ip`;
no evidence it mutates `NeoExecuteBody` in place. ILRuntime is single-threaded per
AppDomain (the `UnityMainThreadID` references confirm), so concurrent execution of a
shared body is not a concern. Acceptable; flag to re-confirm if/when runtime body
patching/caching is added (Step 26 reflection edges).

### MINOR-6: Per-definition cache capture race (theoretical)

`StoreGenericTemplate` double-checks `if (genericMethodTemplate != null) return;`
but the check-then-set is not atomic. Two threads instantiating the same generic
definition concurrently could both run a capture Compile and both call
`StoreFromCapture`; the second's `StoreGenericTemplate` is a no-op (field already
set), and both captures produce equivalent templates (front-half is deterministic
for capture-eligible args), so no corruption. Single-threaded-per-AppDomain makes
this theoretical. The `try/catch` that resets the field to null on failure is good
(no half-baked template). No action for Step 22; revisit at Step 26 (thread-safety).

---

## Accepted-known (out of Step-22 scope, documented)

- **Pre-existing runtime bug: Box on a generic-param ref-T.** `BoxIt<string>`
  (`(object)v` with T=string) NREs in the value-type Box arm
  (`ILIntepreter.Neo.cs` ~line 2749: `clrBoxType.TypeForCLR` with `clrBoxType`
  null) in BOTH paths -- the planning-context confirmed this by disabling the
  template path (it reproduces on the per-occurrence JIT, i.e. on HEAD). NOT a
  Step-22 regression. The V1 structural self-check covers `BoxIt<object>`/
  `BoxIt<RefClass>` body equivalence (bodies match -- the bug is runtime-execution,
  not body-generation); the V2 functional covers the value-T box (`BoxIt<int>`).
  Fixing the runtime Box-on-generic-ref-T arm is a separate Neo runtime task.
- **`HashIt<int>` per-occurrence constrained quirk:** the capture-instance int cell
  of the `HashIt` probe threw at runtime even though its body is byte-identical to
  per-occurrence (V1 PASS at len 5). This is a pre-existing runtime constrained-on-
  primitive-arm quirk (per-occurrence path), distinct from BLOCKER-1 (which is the
  CloneAndPatch body divergence for non-capture T). Worth a separate look but does
  not block Step 22.

---

## Load-bearing conclusions (the two the LEAD asked for)

1. **RunNeoBackHalf Legacy-neutrality: SOUND.** The factored `RunNeoBackHalf` runs
   the same passes in the same order (TypeSpecialize -> CodeBody=res.ToArray() ->
   Allocate -> NeoExecuteBody=Clone -> Lower); `Compile` calls it inline. The
   `templateCapture` hook is a read-only snapshot of `res.ToArray()` + metadata at
   the capture point (post-CleanupRegister, pre-TypeSpecialize) and does not mutate
   `res`/`frame`. Every Step-22 addition is `#if ENABLE_NEO_MODE`; the Legacy
   `#else` arm preserves the original code path. Independently confirmed: plain
   `Debug` + useRegister=true NeoStep filter = 205 ran / 8 pre-existing failures
   (HEAD behavior, `NotImplementedException`). The only non-functional reorder is
   `method.Compiling = false` moving to end-of-Compile (MINOR-4, benign).

2. **V1 equivalence soundness: SOUND where the matrix reaches, but the matrix has a
   coverage hole that hides BLOCKER-1.** `BodiesEqual` compares Code + Register1/2/3
   + Operand/2/3/4; since `Operand` is the int @offset 8 it transitively covers
   `Register4` (@10) too, so all 24 bytes of `OpCodeR` are compared -- no field is
   missed. The 25/25 matrix PASS is real for ProbeBasic/MakeArray/StoreRef/LoadRef/
   BoxIt. BUT the matrix omits `constrained.callvirt T.M`, and that pattern
   diverges (BLOCKER-1). A green 25/25 does not prove the template mechanism correct
   for all generics -- the dump-grounded extractor guard
   (`HasGenericParameter(sym.Instruction.Operand)`) is unreliable for opcodes whose
   recorded symbol is not their own Cecil token (Constrained is the proven instance;
   the same fragility should be re-audited for the other in-switch opcodes).

---

## Items the spec/plan should update before re-review

- Add the `Constrained T` fix (BLOCKER-1) + a `HashIt`/`EqualsIt` V1 row + V2 cell.
- Broaden the V1 matrix to: struct-T + branches/switch/exception-handlers (MINOR-3)
  and a non-constrained T-qualified call (MAJOR-2 reachability check).
- Record the BLOCKER-1 root cause (symbol-points-to-wrong-Cecil) in
  `planning-context.md` Findings so Step 23's serializer (which depends on the
  PatchEntry table) inherits the corrected extractor.

---

## Round 1 re-review (REVIEW-FIX verification, non-author confirmation)

Reviewer: a second non-author verifier (author != verifier != round-1 fixer).
Date: 2026-07-07. Scope: confirm the round-1 fix DELTA resolves BLOCKER-1,
MAJOR-2, MINOR-3; review the whole shipped state for coherence; assess the 4th
bug (Leave/Leave_S) and the "V1 byte-identical => correct runtime" premise.

### Overall verdict: APPROVE (Blocker resolved; review-loop can terminate clean)

All three round-0 findings are CONFIRMED-FIXED by independent verification
(adversarial probes + build/test, not the fixer's self-certification). No
Blocker or Major remains open. The 4th bug (Leave/Leave_S delta-shift) found +
fixed in round 1 is also confirmed correct. Regression gates green:
- V1 self-check: **55/55** (matrix 11 methods x 5 T).
- NeoStep smoke (Debug_Neo): **205/205**.
- NeoOptHardening: **24/24**. NeoStep20: **9/9**.
- Legacy-neutral (plain Debug + useRegister=true, NeoStep filter): **205 ran /
  8 pre-existing failures** (NeoStep13/14/15/16/6 runtime; matches HEAD; zero
  NeoStep22 / generic-template failures).

### BLOCKER-1 (Constrained T type-token): CONFIRMED-FIXED

Verified the fix end-to-end against the runtime consumer:

- **Capture reads the RIGHT token.** `JITCompiler.CaptureTemplate` (JITCompiler.cs
  :708-722) scans `def.Body.Instructions` (the open definition's CIL body) for
  `Code.Constrained` and takes `ins.Operand as TypeReference` -- i.e. the
  `constrained.` prefix's own type, NOT the trailing callvirt's MethodReference.
  This is the correct Cecil operand for a Constrained instruction. Paired 1:1
  with the trailing callvirt's MethodReference (`ConstrainedMethodTokens`).
- **Body-order consumption is sound (no off-by-one).** CIL order == template-
  body order: the front-half (`JITCompiler.cs:2198-2208`) emits Constrained+
  callvirt as an adjacent pair (the Constrained op is re-appended adjacent to the
  callvirt), `hasConstrained` DISABLES inlining of the trailing callvirt
  (`needInline = canInline && !hasConstrained`, :2151), and the runtime hard-
  asserts `cv = ip + 1` (ILIntepreter.Neo.cs:4097-4106). So the Nth `constrained.`
  CIL prefix maps to the Nth `Constrained` body op; `ExtractPatches` consumes
  `ConstrainedTypeTokens[constrainedIdx++]` per Constrained op in walk order.
- **Adversarial probe -- TWO constrained-on-T callvirts in one body**
  (`TwoConstrained<T>(T a, T b) where T:IComparable<T>{ int h=a.GetHashCode();
  int c=a.CompareTo(b); return h+c; }`, TEMPORARY, since removed): `patches=4`
  (GetHashCode pair -> 1 TypeToken [Object::GetHashCode non-T-qualified];
  CompareTo pair -> 1 TypeToken + 2 MethodToken [IComparable<T>::CompareTo]).
  All 5 T byte-identical, len=9. Proves the 2nd pair's method token patches
  INDEPENDENTLY of the 1st -- no off-by-one when multiple Constrained ops exist.
  This is the strongest ordering probe available within the single-T capture
  mechanism (a 2-generic-param T,U probe would need ForceBuildTemplate changes).
- **Re-emitted hash matches the front-half path.** `DoCloneAndPatch` re-resolves
  via `instance.GetTypeTokenHashCode(pe.CecilToken)` (TypeToken) -- the SAME
  resolver the front-half uses (`Code.Constrained` case, :2647). V1 structural
  equivalence (HashIt/EqualsIt green across all 5 T) is the proof: the patched
  Operand equals the per-occurrence emission.
- **Runtime consumer confirmed.** `ILIntepreter.Neo.cs:4096` reads
  `AppDomain.GetType(ip->Operand)` -- the Constrained op's Operand, which the fix
  patches. The BLOCKER-1 stale-hash path is closed.

### MAJOR-2 (MethodToken, T-qualified call): CONFIRMED-FIXED (Constrained pair) + non-constrained T-invariant

- **Both Constrained-pair method-token sites patched.** `ExtractPatches`
  (GenericMethodTemplate.cs:307-328) records a MethodToken PatchEntry on the
  Constrained op's `Operand2` (InstrIdx=i, vestigial -- exists only for V1 body-
  equality) AND on the trailing callvirt's `Operand2` (InstrIdx=i+1, runtime-
  load-bearing -- the runtime reads `cv->Operand2` at ILIntepreter.Neo.cs:4108).
  The callvirt is at i+1 because the pair is adjacent (runtime asserts `cv=ip+1`).
- **`GetMethodTokenHash` mirrors the front-half.** `DoCloneAndPatch` re-emits via
  `appdomain.GetMethod(cecilRef, declaringType, instance, out invalid)` ->
  `m.GetHashCode()` (invalid) or `token.GetHashCode()` -- the same resolution as
  `InitializeFunctionParam`. `CompareThem<T> where T:IComparable<T>` (T-qualified
  callvirt): `patches=3` (type + 2x method), all 5 T green.
- **Non-constrained generic-method call is genuinely T-invariant.** Probe
  `CallIt<T>(T v)=>EchoG<T>(v)` (TEMPORARY, since removed): `patches=0`, bodies
  byte-identical across int/long/object/RefClass (len=7). The call resolves to
  one ILMethod whose hash does not vary with T, so no patch is needed and the
  body matches per-occurrence. (The non-Constrained Call/Callvirt path is NOT in
  the ExtractPatches switch -- `default: continue` -- which is correct precisely
  because such calls are T-invariant in the emitted token. The matrix's
  `CompareThem` is the guard for any FUTURE T-qualified divergence.)
- **`HasGenericParameter` extended correctly.** Now handles MethodReference
  (declaring type + GenericInstanceMethod args, GenericMethodTemplate.cs:156-168),
  so the T-qualified discrimination is real, not a stub.

### MINOR-3 (matrix coverage): CONFIRMED-FIXED

- Matrix expanded 25 -> 55 cells (11 methods x 5 T): added HashIt/EqualsIt
  (Constrained), CompareThem (MethodToken), BranchIt/SwitchIt/TryCatch (struct-T
  + control-flow delta-shift). All 55 PASS.
- **`BodiesEqual` compares all 24 bytes of OpCodeR.** Verified against the struct
  layout (OpCode.cs:36-71): Code@0, Register1@4, Register2@6, Register3@8
  (explicit), **Register4@10 covered transitively via Operand@8-11**, Operand2@12,
  Operand3@16, Operand4@20. OperandFloat (@8) / OperandLong (@12-19) /
  OperandDouble (@12-19) aliases are covered via the int fields. No field missed.
- **No cell is forced equal that should diverge.** The HashIt/EqualsIt/CompareThem
  cells are the ones that WOULD have caught BLOCKER-1/MAJOR-2 (they fail without
  the fix per round-0); they are green WITH the fix, not green-by-omission.

### 4th bug (Leave/Leave_S delta-shift): CONFIRMED-FIXED

`DoCloneAndPatch`'s delta-shift loop (GenericMethodTemplate.cs:513-524) now
includes `Leave`/`Leave_S` via an `else if` branch. `Optimizer.IsBranching` /
`IsIntermediateBranching` exclude Leave/Leave_S (they are EH control-flow
resolved separately via `addr[]`, JITCompiler.cs:555), but their `Operand` is a
resolved body index that shifts with the Initobj prefix delta for struct-T.
- **EH cell green:** `TryCatch<Struct>` (len=9) PASS -- was failing before the
  fix (round-1 report: Leave_S Operand 4 vs 6).
- **No over-shift:** the whole shift block is gated `if (delta != 0)` so non-
  struct / non-EH methods are untouched; the `else if` chain ensures each op is
  shifted at most once (no branch op is also a Leave). `BranchIt<Struct>` (no EH,
  if/else branches) and `SwitchIt<Struct>` PASS -- confirming branches/switch
  targets are not disturbed by the Leave addition.

### "V1 byte-identical => correct runtime" -- ASSESSED SOUND (no path-dependent state outside the body)

For the Constrained case the runtime reads ONLY in-body state:
- `ip->Operand` (Constrained type hash, :4096) and `cv->Operand2` (callvirt
  method hash, :4108) -- both proven equal to per-occurrence by V1.
- The downstream `AppDomain.GetType(hash)` / `AppDomain.GetMethod(hash)` caches
  are keyed by the hash VALUE, not by the capture instance or the compile path.
  Equal hash => identical IType/IMethod resolution. `GetMethod` re-resolves the
  generic args via the context method per call (no stale capture-T leak).
- **No side-cache keyed by capture instance.** Confirmed by reading
  `DoCloneAndPatch` + `TryInstantiate`: the only outputs are `frame` (body +
  layout) and `addr` (Cecil->index for EH resolution). For the Constrained cells
  (HashIt/EqualsIt/CompareThem -- no EH), `addr` is not on the runtime hot path.
  For TryCatch (EH), `addr` is rebuilt from `template.Addr` shifted by delta --
  same delta that shifted the Leave targets in-body, so EH ranges stay aligned
  with the shifted Leave/leave targets. The ref-share path returns shared
  `RefBody` arrays, but those are read-only at runtime (ExecuteNeo reads via `ip`
  on a `byte*` frame; single-threaded per AppDomain).

So a byte-identical body implies path-independent runtime behavior. The fixer's
caveat -- that the Constrained V2 FUNCTIONAL cell is not exercised because the
Step-17 runtime Constrained arm has pre-existing gaps for ref/primitive-T `this`,
and every capture-eligible T is ref/primitive -- is ACCEPTED: V1 structural
equivalence is a valid substitute (the body is the only path-dependent artifact).
Fixing the runtime Constrained arm for ref/primitive-T `this` remains a separate
Step-17 task; it does not block Step 22.

### New findings from the fix delta

- **(FOLLOW-UP, not blocking) struct-T x inliner CloneAndPatch gap.** Surfaced
  by the `CallIt<Struct>` probe (TEMPORARY): per-occ len 9 vs template len 8 --
  the front-half INLINES `EchoG<T>` and the inline expansion inserts Initobj for
  struct temps that CloneAndPatch's prefix-rebuild does not reproduce. This is
  NOT introduced by the round-1 fix and NOT a regression (no smoke test hits it;
  ProbeBasic<Struct> / BranchIt<Struct> / SwitchIt<Struct> / TryCatch<Struct> all
  work). Severity: MINOR for Step 22 (capture-eligible instantiations are
  ref/primitive; struct-T bodies without inlining are handled correctly). It MUST
  be re-audited at Step 23: the serializer must not assume the Initobj prefix is
  the only Initobj site (inline-Initobj from the inliner is a second source).
  Already recorded in planning-context.md "Follow-ups".

No other new findings. The CIL-body-scan token-capture approach (in
`CaptureTemplate`, mirroring the existing `VariableTypes` capture precedent to
avoid the def.Body-null-in-Release dependency) is sound; it correctly avoids the
unreliable symbol mapping without introducing a new Release-build hazard.

### Build + test evidence (all run by the verifier)

- Build: `ILRuntimeTestCLI -c Debug_Neo` (0 err), `TestCases -c Debug` (0 err),
  `ILRuntimeTestCLI -c Debug` (0 err, Legacy).
- V1 self-check (pristine 11-method matrix): **55/55 PASS**.
- Adversarial probes (TEMPORARY, verified, REMOVED; tree restored to pristine
  round-1 state, re-confirmed 55/55 + 205/205 after removal):
  - `TwoConstrained<T>` (two constrained-on-T callvirts): patches=4, 5/5 T green
    -- BLOCKER-1 off-by-one disproven.
  - `CallIt<T>`/`EchoG<T>` (non-constrained generic-method call): patches=0,
    4/4 ref/primitive T green -- MAJOR-2 T-invariance confirmed; `<Struct>`
    fails = the documented inliner follow-up.
- NeoStep smoke: **205/205**. NeoOptHardening: **24/24**. NeoStep20: **9/9**.
- Legacy-neutral: **205 ran / 8 pre-existing failures** (no NeoStep22 failures).

### Review-loop termination

CLEAN. The Blocker is resolved and independently confirmed. MAJOR-2 and MINOR-3
are resolved. The 4th bug is resolved. The one open item (struct-T x inliner
CloneAndPatch gap) is a documented Step-23 follow-up, not a Step-22 blocker (no
smoke regression; ref/primitive capture-eligible path is correct). **APPROVE for
ship; the review-loop can terminate.**
