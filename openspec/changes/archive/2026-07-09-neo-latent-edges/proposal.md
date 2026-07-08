# Proposal -- neo-latent-edges

## Why

The Neo deferred-items map (`.trae/documents/neo-deferred-items.md`) lists four
"latent edge" items (F-9, F-7, F-2, F-11) flagged by prior review passes but
never dump-gated on current HEAD. Each carries a hypothesized root cause and a
suspect code site. The recurring pattern in this codebase (Q-STRUCT, Q-LONG,
Q-NEWOBJ, F-13, the byref-3/4 and array-2/3 cohorts) is that a large fraction
of these hypothesized edges DISAPPEAR or RE-CHARACTERIZE when probed on HEAD:
the documented suspect is stale, the defeat no longer defeats, or the real
trigger is a different defect class entirely. This change performs the binding
dump-gate for each of the four and records an honest verdict -- fix the
reproducible ones whose fix is in-scope small, close the rest with the
disproving evidence so the deferred-items map stops carrying dead weight.

## What (per-edge dump-gate verdict on HEAD `6d0efe68`, independently re-probed)

This is a TRIAGE + DOCUMENT change. The dump-gate (construct the reproducer,
run on HEAD via `ILRuntimeTestCLI -c Debug_Neo -f net8.0`, cite file:line) was
executed for all four edges with fresh probes built into a TEMPORARY
`TestCases/NeoLatentEdgesProbeTest.cs` (built + run, then deleted -- NO source
shipped). The verdicts:

- **F-9 / NEO-INLINED-RETURN-MOVE -- NON-REPRODUCIBLE (close).** The documented
  hypothesis ("the trivial-inliner mis-classifies an inlined IL method's int
  return as a reference move -> mStack[intValue] OOB, defeatable by `+ 0`") is
  DISPROVEN on HEAD. A control probe reproduces the JIT body of the call site:
  `NeoLatentEdgesProbe.NeoLatent_F9_ReturnMoveNoDefeat` JIT body is
  `ldc.i4.s r5,42; move r6,r5; br.s 3; move r5,r6; move r0,r5; ceqi r5,r0,42`
  -- the `ReturnIntDirectly` call IS inlined (no `call`), the int return value
  `42` moves correctly through PRIMITIVE registers (`r0` holds `42`, `ceqi`
  succeeds), and there is NO reference move and NO OOB. The bare `return x;`,
  the `int v = x; return v;` local form, AND the documented `+ 0` defeat ALL
  PASS on HEAD (3/3). The trivial-inliner does NOT mis-classify the int return
  on HEAD. Closed honestly with the disproving JIT-body control.

- **F-7 / NEO-DELEGATE-REFOUT -- REPRODUCIBLE but NOT a small fix
  (close-needs-larger-change).** A delegate with a `ref`/`out` param invoked
  via Neo reproducibly OOBs -- confirmed by three independent probes:
  `NeoLatent_F7_DelegateRefInt` FAILs @ `ILIntepreter.Neo.cs:3726` (the
  `Ldind_I4` arm reading the byref param's Ref Slot `objectIndex` half),
  `NeoLatent_F7_DelegateOutInt` FAILs @ `ILIntepreter.Neo.cs:3636`, and
  `NeoLatent_F7_DelegateRefString` FAILs @ `ILIntepreter.Neo.cs:3831`. The
  documented mechanism is CONFIRMED. However the documented fix ("make
  `WriteNeoCallSlot` byref-aware") is INSUFFICIENT: the byref is DESTROYED
  before `WriteNeoCallSlot` sees it. The delegate-Invoke path at
  `ILIntepreter.Neo.cs:2443-2454` routes through
  `ReadNeoDelegateInvokeArgs` (`ILIntepreter.Neo.cs:246-287`), which funnels
  every delegate-Invoke argument through an `object[]` -- for a byref param it
  reads ONLY the first 4 bytes of the 8-byte Ref Slot (the `objectIndex` half)
  and boxes `null`/garbage; the offset half is never read. Then
  `NeoInvokeSub` (`DelegateAdapter.cs:1006-1019`) runs the target on a SEPARATE
  POOLED interpreter (`appdomain.RequestILIntepreter()`), so even a correctly
  preserved byref Ref Slot could not point back at the caller's frame (the
  target's `frameBase`/`mStack` are unrelated to the caller's). A real fix
  requires a NEW frame-to-frame delegate-invoke fast path that preserves the
  byref Ref Slot and propagates the `ref`/`out` write-back to the caller's
  frame -- a Step-19-sized mechanism, not a small byref-awareness tweak. Closed
  with the reproduction + the two-site destruction proof + the reason the small
  fix is insufficient; routed to a future byref-delegate change. The
  accepted-known limitation is recorded as a future-fix SHALL in
  `specs/neo-dispatch/spec.md`.

- **F-2 / INLINER-REFONLY-VT -- NON-REPRODUCIBLE on the documented hypothesis
  (close).** The documented hypothesis ("the JIT inliner's ref-fold over a
  0-prim-size VT local; the inlined `stfld.ref.inline` writes do NOT survive to
  the following in-frame `ldfld.ref` read") is DISPROVEN on HEAD. The literal
  documented shape -- `struct S { string a; string b; }` (TotalPrimitiveSize
  == 0), constructed via `new S(refArgs)`, then field-read -- PASSES on HEAD.
  The JIT body of `NeoLatent_F2_RefOnlyReadFields` is
  `initobj r0; ldloca.s r9,r0; ldstr r10,"hello"; ldstr r11,"world";
  stfld.ref.inline r9,r10,(0,0); stfld.ref.inline r9,r11,(0,1);
  ldfld.ref.inline r1,r0,(0,0); ldfld.ref.inline r2,r0,(0,1)` followed by two
  `callvirt.clr get_Length` reads -- this is the EXACT documented shape
  (inlined `stfld.ref.inline` writes followed by in-frame `ldfld.ref.inline`
  reads), and the writes DO survive to the reads (both fields return correct
  values). A struct WITH a primitive field (`int X` + 2 refs) and a 1-ref-only
  struct ALSO PASS (3/3). The documented ref-fold defect does NOT reproduce on
  HEAD. Closed honestly with the disproving JIT-body control. (A re-
  characterized "multi-combine" defect was hypothesized by a prior session but
  never backed by a shipped failing probe; it is out of scope for this triage
  and, if it surfaces, belongs to the F-8/F-MAJ-1 optimizer-hardening track,
  NOT the inliner ref-fold.)

- **F-11 / NEO-AOT-GENERIC-EAGER-COMPILE -- NOT A BUG (close; accepted
  optimization).** A generic instance force-compiled before the `.neo` loader
  binds the AOT template keeps a stale `bodyRegister` -- but that stale body is
  CORRECT JIT (the deferred-items row states so explicitly: "a generic instance
  compiled before load keeps JIT", "NOT an S2 bug"). It is a missed
  optimization (the instance runs JIT instead of the AOT template), NOT
  corruption. It is NOT reproducible via the interpreter smoke harness
  (`ILRuntimeTestCLI` runs pure JIT -- there is no `.neo` load in the smoke
  path), and the S2 capstone already worked around it with a fresh
  `MakeGenericMethod` instance (`MakeGenericMethod` returns a NEW `ILMethod`,
  `bodyRegister` null -> `InitCodeBody` -> `CloneAndPatch` against the freshly-
  bound AOT template). Reproducing the load-order staleness would require the
  production-AOT-load path (the standalone `ilrt_neoc` + a live `.neo` loader
  bind), which is out of scope for a "small edges" triage change. Closed/
  accepted as a known S3 production-AOT optimization; a fix (load-order /
  per-instance refresh) is low-value, moderate-risk, and out of scope.

## Outcome

Net: 0 source fixes shipped (none of the four is a small in-scope fix -- F-9
and F-2 are disproven on HEAD, F-11 is an accepted optimization with no
behavioral bug, F-7 is reproducible but needs a Step-19-sized frame-to-frame
delegate-invoke mechanism, not a small marshal tweak). 4 honest closures
recorded in this change's `design.md` + 1 accepted-known limitation
specification (F-7's delegate-byref callback) recorded as a future-fix SHALL in
`specs/neo-dispatch/spec.md`. The four deferred-item rows in
`.trae/documents/neo-deferred-items.md` are to be updated by the APPLY stage to
reflect the dump-gate verdicts (a doc-only edit, gated out of the Neo code
paths). This is the TRUE-COMPLETION outcome the prior-session pattern predicts:
the dump-gate disproves the documented hypotheses; closing honestly is the
correct resolution (the same disposition as Q-STRUCT / Q-LONG / Q-NEWOBJ /
F-13).

## Impact

- Affected specs: `neo-dispatch` ADDED (one future-fix SHALL recording the
  F-7 delegate-byref-callback accepted-known limitation). This change ships NO
  source; the spec delta constrains a FUTURE fix, not current behavior.
- Legacy impact: NONE (no source touched).
- Neo impact: NONE behaviorally (no source touched; the closed items remain
  deferred/routed as documented). The closed items are NOT closed as "fixed" --
  F-7 is closed as "needs-larger-change", F-9/F-2 as "needs-reproducer (none
  exists on HEAD)", F-11 as "accepted optimization".
