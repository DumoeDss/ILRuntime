# Tasks -- neo-latent-edges

> Triage + document change. The dump-gate was performed in the PROPOSE stage
> (each edge reproduced -- or not -- on HEAD `6d0efe68` with temporary probes
> that were then deleted). This change ships NO source. The APPLY stage is
> doc-only (the deferred-items map update) + the spec delta is already written.

## 1. Dump-gate (DONE in propose; recorded here for the apply worker)

- [x] **F-9 / NEO-INLINED-RETURN-MOVE** -- reproduced 3 variants (no-defeat,
      via-local, defeated) on HEAD via temporary `NeoLatentEdgesProbeTest.cs`;
      3/3 PASS; JIT-body control disproves the inliner return-move
      mis-classification. Verdict: NON-REPRODUCIBLE -> CLOSE.
- [x] **F-7 / NEO-DELEGATE-REFOUT** -- reproduced 3 variants (ref int @3726,
      out int @3636, ref string @3831) on HEAD; 3/3 FAIL; two-site destruction
      proof (ReadNeoDelegateInvokeArgs + separate pooled interpreter) shows the
      documented small fix is insufficient. Verdict: REPRODUCIBLE but NOT a
      small fix -> CLOSE-needs-larger-change (route to future byref-delegate
      child).
- [x] **F-2 / INLINER-REFONLY-VT** -- reproduced the literal documented shape
      (0-prim-size `struct S{string a;string b;}`, `new S(refArgs)`, field-read)
      + 2 controls (with-prim, single-ref) on HEAD; 3/3 PASS; JIT-body control
      disproves the inliner ref-fold. Verdict: NON-REPRODUCIBLE on the
      documented hypothesis -> CLOSE.
- [x] **F-11 / NEO-AOT-GENERIC-EAGER-COMPILE** -- confirmed NOT a behavioral
      bug (stale body is CORRECT JIT); not reproducible via the smoke harness
      (no `.neo` load); S2 capstone workaround (`MakeGenericMethod`) exists.
      Verdict: accepted optimization -> CLOSE (sequenced under Step 25 S3).
- [x] Temporary probe file `TestCases/NeoLatentEdgesProbeTest.cs` DELETED (no
      source ships). NeoStep baseline 229/0/0 before and after.

## 2. Spec + design artifacts (DONE in propose)

- [x] `proposal.md` -- finalized with independently-verified per-edge verdicts.
- [x] `design.md` -- per-edge dump-gate detail, file:line citations, JIT-body
      controls, the F-7 two-site destruction proof, and the F-7 required
      (future) fix shape.
- [x] `specs/neo-dispatch/spec.md` -- F-7 accepted-known-limitation future-fix
      SHALL (PURE ASCII; SHALL-first; Neo-only; Legacy-neutral with the
      Legacy-neutrality scenario).

## 3. Apply-stage tasks (doc-only; NO source)

- [ ] **Update the F-9 row** in `.trae/documents/neo-deferred-items.md` (lines
      ~825-848): change the resolution to "CLOSED 2026-07-09 (neo-latent-
      edges): NON-REPRODUCIBLE on HEAD `6d0efe68`; JIT-body control disproves
      the inliner return-move mis-classification (3/3 PASS, the `+0` defeat
      defeats nothing). See neo-latent-edges design.md section 1."
- [ ] **Update the F-7 row** in `.trae/documents/neo-deferred-items.md` (lines
      ~720-757): change the resolution to "CLOSED-needs-larger-change 2026-07-09
      (neo-latent-edges): REPRODUCIBLE on HEAD (3/3 FAIL @ 3636/3726/3831); the
      documented small fix (WriteNeoCallSlot byref-aware) is INSUFFICIENT --
      the byref is destroyed in ReadNeoDelegateInvokeArgs (object[] funnel) AND
      the target runs on a separate pooled interpreter; routed to a future
      byref-delegate child (Step-19-sized frame-to-frame delegate-invoke).
      Future-fix SHALL recorded in neo-dispatch. See neo-latent-edges design.md
      section 2."
- [ ] **Update the F-2 row** in `.trae/documents/neo-deferred-items.md` (lines
      ~450-465): change the resolution to "CLOSED 2026-07-09 (neo-latent-
      edges): NON-REPRODUCIBLE on HEAD `6d0efe68` for the documented ref-fold
      hypothesis (3/3 PASS; JIT-body control shows stfld.ref.inline writes DO
      survive to ldfld.ref.inline reads). The re-characterized 'multi-combine'
      optimizer defect (if real) belongs to the F-8/F-MAJ-1 track, not the
      inliner ref-fold. See neo-latent-edges design.md section 3."
- [ ] **Update the F-11 row** in `.trae/documents/neo-deferred-items.md` (row
      ~100): append "CONFIRMED accepted-optimization 2026-07-09 (neo-latent-
      edges): stale body is CORRECT JIT; not a behavioral bug; not reproducible
      via the smoke harness; sequenced under Step 25 S3 production-AOT. See
      neo-latent-edges design.md section 4."

## 4. Verify (apply stage)

- [ ] Confirm `NeoStep` smoke stays 229/0/0 (this change ships no source; the
      only edits are doc + spec, so the smoke is a regression-of-omission
      guard, not a behavioral check).
- [ ] Confirm `git status` shows NO source files touched by this change (only
      `.trae/documents/neo-deferred-items.md` + the `openspec/changes/neo-
      latent-edges/` artifacts).
- [ ] Confirm `specs/neo-dispatch/spec.md` delta is PURE ASCII (0 non-ASCII
      bytes) -- already verified in propose (4706 bytes, 0 non-ASCII).

## 5. Out of scope (explicitly NOT done by this change)

- The F-7 frame-to-frame delegate-invoke fix (future byref-delegate child).
- The F-2 re-characterized multi-combine optimizer defect (F-8/F-MAJ-1 track).
- The F-11 production-AOT load-order optimization (Step 25 S3).
- Any source edit to `ILRuntime/` or `TestCases/`.
