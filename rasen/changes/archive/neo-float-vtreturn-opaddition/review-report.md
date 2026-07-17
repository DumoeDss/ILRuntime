# Review Report: neo-float-vtreturn-opaddition (child 28, neo-overhaul)

Reviewer: independent (reviewer-1). Branch: `features/object-model-overhaul`.
Scope: working-tree (uncommitted) changes across 5 modified files + 1 new probe.

## Verdict (up front)

**APPROVE-WITH-FINDINGS.** The root-cause diagnosis is SOUND (stale committed
binding stubs, NOT an engine/generator bug), the fix is correct and minimal, the
generator-repoint + facade is load-bearing and justified, and the stash-toggle
proof reproduces on independent re-run. All findings are Minor or Trivial; none
block ship. The one substantive note is a test-coverage gap (two of the four
ported stubs are faithful but not directly exercised by the probes).

Independent re-runs performed (this review):
- Clean build (Debug_Neo, --no-incremental): **0 errors**.
- NeoStep smoke: **378 ran / 0 failed** (matches design claim).
- Stash-toggle (revert ONLY `ILRuntimeTest_TestFramework_TestVector3_Binding.cs`
  to HEAD, keep facade + generator + helpers, cache-bust rebuild): **3/3 FAIL
  (DivideByZero)**. Pop + cache-bust rebuild: **3/3 PASS**. Airtight.

---

## Dimension 1 -- Root-cause diagnosis sound? YES.

(a) HEAD stubs genuinely stale -- CONFIRMED. `git show HEAD:...TestVector3_Binding.cs`
shows the four `*_Neo` stubs use `@a = default(TestVector3)` + `// TODO: CLR value
type reflection fallback: Step 13` (VT args never read from the frame) and
`// TODO: CLR value type return in reflection fallback: Step 13` (VT return never
written). This is exactly as the design describes.

(b) Runtime reflection-fallback path genuinely correct -- CONFIRMED in
`ILIntepreter.Neo.cs`:
- VT param read: `InvokeNeoClrMethod` arg-build at lines ~356-360
  (`args[i] = ReadNeoValueType(clrT, targetBase, ref cur, sz)`, sized via
  `Optimizer.GetNeoValueTypeManagedSize`).
- VT return write: lines ~1157-1158
  (`int retSz = Optimizer.GetNeoValueTypeManagedSize(...); WriteNeoValueType(res,
  retDstPtr, retSz)`).
- Crucially, when a `*_Neo` redirect exists (`clrMethod.RedirectionNeo != null`,
  line ~1094), the redirect delegate is called and EARLY-RETURNS at line ~1099 --
  the stub owns the dest write. The stale stub did not write, hence zero. So the
  engine is correct; only the committed stub was broken. Diagnosis holds.

(c) "Green smoke uses `TestVector3.One`, never calls the broken stubs" -- CONVINCING.
`One` is a `public static TestVector3 One` FIELD (TestVector3.cs:88), host-initialized
to `(1,1,1)`, read by IL via `ldsfld` (precomputed bytes cross as-is). It is distinct
from `One2`, a static PROPERTY (TestVector3.cs:106) whose getter IS served by
`get_One2_0_Neo`. The operators (`op_Addition`/`op_Multiply`) route to `call.redirect`
-> the stale stub. So the bug is strictly on the autogen-redirect path; children
3/8/15/16/21 used the `One` field + field reads, not operator/ctor calls. Suspicion
reconciled.

**Root-cause-diagnosis-sound verdict: YES (stale stubs, not engine).**

---

## Dimension 2 -- Hand-ported stubs correct? YES (faithful to generator template).

Cross-checked each port against the generator's Step-13b emission template in
`BindingGeneratorExtensions.cs` (param: lines 177-178; return: line 603). No
other binder-VT Neo stub exists in-repo to cross-check, so the generator template
is the reference.

- `op_Addition_2_Neo` (VT,VT args): reads `@a` then `@b` each via
  `ReadNeoValueType` (separate `__sz_0`/`__sz_1`), calls `@a + @b`, writes return
  via `WriteNeoValueType` under `__retDst != null`. Matches template exactly. CORRECT.
- `op_Multiply_1_Neo` (VT arg + scalar arg): reads VT `@a` (advancing cursor by the
  VT size), THEN `ReadNeoFloat(@b)`. The stale stub did `@a = default(...)` WITHOUT
  advancing, so `@b` read the wrong bytes; the fix realigns the cursor. CORRECT
  and fixes a real secondary bug (mis-aligned scalar after a skipped VT param).
- `get_One2_0_Neo`: reads `TestVector3.One2` (pre-existing), adds the VT return
  write. CORRECT (faithful). NOTE: not directly exercised -- see Finding F1.
- `Ctor_0_Neo`: reads 3 floats, constructs `new TestVector3(x,y,z)`, adds the VT
  return write. The `isNewObj` retRefBase-skip logic is preserved unchanged.
  CORRECT in isolation. NOTE: on the struct-newobj path the runtime passes
  `retDst=null` (symptom-1), so the write no-ops there -- see Finding F2.

---

## Dimension 3 -- Generator repoint + facade correct + safe? YES (load-bearing).

(a) Facade delegates to IDENTICAL logic. `ILIntepreter.GetNeoValueTypeManagedSize`
(ILIntepreter.Neo.cs:276-279) is a pure pass-through to
`Optimizer.GetNeoValueTypeManagedSize` (Optimizer.Neo.cs:1630). No behavior change.

(b) The "internal Optimizer" claim is CORRECT, with one precision note: the METHOD
`GetNeoValueTypeManagedSize` is declared `public`, but the containing CLASS
`Optimizer` is `partial class Optimizer` with NO access modifier in any partial
(Optimizer.Neo.cs:12, and all 6 sibling partials) -> default `internal`. A public
method on an internal class is unreachable from another assembly. No
`InternalsVisibleTo` exists in the repo (grep confirms only comments mention it).
So the generator's prior emission (`Optimizer.GetNeoValueTypeManagedSize(...)`)
genuinely never compiled in the consumer (ILRuntimeTestBase). Facade on the public
`ILIntepreter` class (ILIntepreter.cs:24) is the correct fix.

(c) Regression risk = zero. The facade is in ILIntepreter.Neo.cs which is file-gated
`#if ENABLE_NEO_MODE` -> Legacy-neutral. The 5 repointed generator sites are pure
emission-string changes (they only affect FUTURE regen output). All in-assembly
runtime call sites still call `Optimizer.GetNeoValueTypeManagedSize` directly (same
assembly, still fine) -- I enumerated all 30+ references; none in consumer emission
remain.

**In-scope + justified:** the facade is not just future-regen hygiene -- the
hand-ported stubs CALL `ILIntepreter.GetNeoValueTypeManagedSize`, so without the
facade the hand-port would not compile (and `Optimizer` is unreachable from the
consumer). The facade is load-bearing for THIS change. The generator repoint is the
matching enabler so a future regen emits the same compilable form.

**Generator-change-justified verdict: YES.**

---

## Dimension 4 -- Scope discipline (symptom-1 deferral)? HONEST.

Symptom-1 (`new TestVector3(float,float,float)` yields zero) is genuinely a distinct
gap: the Neo optimizer lowers a CLR struct newobj to `initobj; ldloca; push(this
byref); call.redirect .ctor` with dest=`-` (Register1<0), and the runtime
Call_Redirect arm computes `crRetDstPtr = ip->Register1 >= 0 ? ... : null` ->
null, so `Ctor_0_Neo`'s (correctly-added) `WriteNeoValueType`-to-`__retDst` no-ops
on null. The struct dest is communicated via the `this` byref, not retDst -- a
different contract from the regular-call VT-return path fixed here. The two candidate
fixes (optimizer preserves a dest register, OR redirect writes through the byref)
are both genuinely more involved. Deferral is honest.

The `Ctor_0_Neo` port is still correct and worth keeping (it mirrors the generator;
a future fix to the optimizer/redirect contract will rely on the stub writing the
return when retDst is non-null). The final probes do NOT exercise the ctor path
(TC1 uses `One+One`, TC2 uses `One*5f`, TC3 uses `arr[0]+=One`) -- so no claim is
left unproven. The out-of-scope instance-method stubs (`Test_3_Neo`, `Normalize_4_Neo`)
are the Area-4a/4b VT-`this` shape (distinct from the framed VT-arg/return path);
confirmed they still carry their pre-existing `// TODO: ValueType instance in Neo`
markers and were not touched (no regression).

---

## Dimension 5 -- Probe strength? GOOD.

Constants arithmetically reachable (hand-checked, host-side float sums):
- TC1 `One+One=(2,2,2)`; `Sum(c,One)` = (2+2+2)+(1+1+1) = **9**. OK.
- TC2 `One*5f=(5,5,5)`; `Sum(c,One)` = 15+3 = **18**. OK.
- TC3 `arr[0]=(1,1,1)`; `arr[0]+=One=(2,2,2)`; `SumArrElem(arr,0)` = **6**. OK.

All sums are exact small-integer floats -> `(int)` cast is lossless. Probes assert
via HOST-side float arithmetic (`SumTestVector3Fields`/`SumTestVector3ArrElem` at
TestClass3.cs:135 / new), deliberately sidestepping the conv.i4-float-bit-reinterpret
bug; a wrong value trips a deliberate `1/0` (DivideByZero). TC3 uses a HOST-built
array (`BuildTestVector3OneArray`) to avoid the separately-broken stelem.any/ldelem.any
CLR-struct-array path (child-26 note). Smart and self-aware.

FAULT on HEAD + PASS after: CONFIRMED by the stash-toggle (3/3 DivideByZero on stale;
3/3 PASS on fixed).

---

## Dimension 6 -- Regression surface + stash-toggle honesty? CLEAN.

- NeoStep smoke: 378/0 (independently re-run). No regressions.
- Stash-toggle: reverts ONLY the binding file, so it isolates the stub fix. The
  facade + generator changes remain in-tree during the toggle yet the probes still
  FAULT -- proving the stub hand-port is the load-bearing fix (and, inversely, the
  facade is exercised indirectly because the fixed stubs call it). 3/3 FAIL -> 3/3
  PASS on round-trip. Airtight and fair.
- Build: 0 errors with all changes in place. The facade + generator repoint compile
  and do not break the build (the CLI build transitively compiles ILRuntimeTestBase,
  which contains the hand-ported stubs that reference the facade).

---

## Findings by severity

### Blocker
(none)

### Major
(none)

### Minor
- **F1 [this-PR, non-blocking] -- Two ported stubs are faithful but not directly
  exercised by the probe set.** The stash-toggle proves the fix path for the two
  OPERATOR stubs actually hit by TC1/TC2/TC3 (`op_Addition_2_Neo`, `op_Multiply_1_Neo`).
  `get_One2_0_Neo` (serves the `One2` property) and `Ctor_0_Neo` (the struct ctor)
  are not reached by any probe -- the probes use the `One` FIELD (not `One2`) and
  never call `new TestVector3(float,float,float)`. Both ports are byte-faithful to
  the generator template, so this is a coverage observation, not a correctness
  defect. Optional: add a probe reading `TestVector3.One2` to cover `get_One2_0_Neo`
  cheaply (the ctor stub cannot be covered without first fixing symptom-1).

- **F2 [this-PR, non-blocking] -- `Ctor_0_Neo` return-write is effectively dead on
  the only path that reaches it today (struct newobj).** Because symptom-1 passes
  `retDst=null`, the added `if (__retDst != null) {...}` write no-ops. Keeping the
  write is still the right call (it mirrors the generator and will fire once the
  optimizer/redirect contract is fixed), but reviewers/LEAD should be aware the
  ctor stub's correctness is asserted-by-template, not by test. The design is
  upfront about this; no action required beyond the documentation already present.

### Trivial
- **T1 [this-PR] -- Generator's 5 repointed sites are not directly tested** (no
  regen ran). Justified by: (a) they compile, (b) pure emission-string change,
  (c) the hand-port mirrors them and IS proven by the toggle. Acceptable.

- **T2 [PRE-EXISTING doc drift] -- design.md/proposal.md line citations are
  slightly off** vs the current file (param read cited as ":342-345", actual
  ~356-360; return write cited as ":1129-1144", actual ~1143-1158; the null-retDst
  pin cited as ":3079"). The referenced CODE is correct; only the cited line numbers
  drifted. Cosmetic.

- **T3 [this-PR, wording] -- "INTERNAL Optimizer" precision.** The method itself
  is `public`; it is the containing CLASS `Optimizer` that is internal (default
  access). The fix correctly targets the right problem; this is just a wording
  nit in the prose.

---

## Summary answers (per return contract)

1. **Verdict:** APPROVE-WITH-FINDINGS (no Blockers/Majors; ship-ready).
2. **Findings:** 0 Blocker, 0 Major, 2 Minor (F1 coverage of get_One2/Ctor stubs;
   F2 Ctor write dead-until-symptom-1), 3 Trivial.
3. **Report path:** `rasen/changes/neo-float-vtreturn-opaddition/review-report.md`.
4. **Root-cause-diagnosis-sound:** YES -- stale committed binding stubs, not an
   engine or generator bug. Confirmed via HEAD-version read + reflection-path code
   walk + stash-toggle.
5. **Generator-change-justified:** YES -- the facade is load-bearing (hand-ported
   stubs call it; `Optimizer` is unreachable from the consumer), and the 5-site
   repoint is the matching enabler for future regen. Compiles; Legacy-neutral.
6. DONE.
