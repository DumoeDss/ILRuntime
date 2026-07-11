## 0. Scope confirmation (this is a STOP-at-blocker / scoped-deferral)

- [ ] 0.1 Re-confirm the dump-gate verdict on the apply-time HEAD (the propose
      verdict was recorded against `70505eba`): `PatchKind` exists at
      `GenericMethodTemplate.cs:54` with values `TypeToken`/`MethodToken`/
      `IsRefMoveFlag` (generic-T-identity-specific); grep `RegisterVM/` for
      `peephole|fuse|fusion|IsinstResult` still returns zero matches (no fusion
      pass); `box;isinst` still emits as two adjacent opcodes
      (`JITCompiler.cs:2637-2645`) running via the two arms
      (`ILIntepreter.Neo.cs:2660` + `:3072-3096`). If any of these changed since
      propose, STOP and re-evaluate (the verdict may flip to SHIP); otherwise
      proceed -- this is a documentation + spec-delta-only change, NO `ILRuntime/`
      source edit.
- [ ] 0.2 Do NOT add an `IsinstResult` value to `PatchKind`, do NOT add a fused
      opcode, do NOT add a fusion pass. The decision is STOP
      (`design.md` decision D4). The anti-pattern to avoid: forcing a peephole-
      pass framework + a fused opcode for a non-functional gain on the lowest-
      priority child.

## 1. Spec delta (the load-bearing deliverable)

- [ ] 1.1 Confirm `specs/neo-optimizer/spec.md` (the ADDED DEFERRED requirement
      "The box T; isinst U peephole fusion is deferred behind a dedicated
      peephole-pass optimizer child (D-PEEP)") is the canonical, PURE-ASCII
      delta. Verify every requirement body starts with "... SHALL ..." on its
      FIRST hard-wrapped line (the validator scans only the first line for
      SHALL/MUST). Run a pure-ASCII codepoint check on the file (the CJK/
      U+FFFD corruption gotcha does not apply since it is ASCII-primary, but
      verify no stray non-ASCII byte slipped in).
- [ ] 1.2 Sanity-run the openspec validator on this change ONLY as a heuristic
      (`openspec validate neo-peephole-isinst` or `openspec status --change
      neo-peephole-isinst --json`). The handoff warns the validator is flaky
      (pre-existing 9/10 false-failures on non-`neo-optimizer` capabilities);
      `neo-optimizer` is the one capability that validates cleanly, so a real
      failure here SHOULD be fixed. A false-positive does not block -- the LEAD
      does a manual archive-merge.

## 2. Deferred-items map update (`.trae/documents/neo-deferred-items.md`)

- [ ] 2.1 Update the D-PEEP section-2 master-table row (currently "opportunistic
      / unblocked by patch-infra step / optimization (non-functional)"): change
      the "Unblocked by" cell to reflect the post-Step-22 finding -- "a dedicated
      peephole-pass child (a new optimizer fusion pass + liveness + a standalone-
      field fused opcode); the Step-22 PatchKind is the wrong shape (generic-T-
      identity value-substitution, not an opcode-stream rewrite)". Keep the
      Severity as "optimization (non-functional)".
- [ ] 2.2 Update the section-3 D-PEEP detail (currently lines ~1223-1230):
      replace the stale "neither the fusion pass nor `PatchKind` exists" rationale
      with the post-Step-22 finding -- `PatchKind` NOW EXISTS
      (`GenericMethodTemplate.cs:54`: `TypeToken`/`MethodToken`/`IsRefMoveFlag`)
      but is generic-template-T-identity-specific and the wrong shape for a
      peephole fusion; the fusion-PASS prerequisite remains (zero matches for
      `peephole|fuse|fusion` in `RegisterVM/`). Point the "Resolution" at a
      future dedicated peephole-pass child. Cite this change
      (`openspec/changes/neo-peephole-isinst/`) + `design.md` decisions D1-D4.
- [ ] 2.3 Use a SMALL targeted edit (Edit tool on the specific lines), NOT a
      full-file Write -- the file is CJK-heavy and the Write tool corrupts ~0.5%
      of CJK on large payloads (the `cjk-write-encoding-corruption` memory).
      After the edit, run a pure-ASCII-codepoint PowerShell check on the edited
      region if any non-ASCII regression is suspected.

## 3. Build + smoke gate (no source change -> smoke unchanged)

- [ ] 3.1 Build the CLI with `Debug_Neo` to confirm the change introduced no
      build breakage (it should not -- no `ILRuntime/` source edited):
      `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`
      (0 errors expected).
- [ ] 3.2 Run the `NeoStep` smoke to confirm no regression (no source change ->
      the smoke is byte-identical to HEAD):
      ```
      dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
        TestCases/bin/Debug/netstandard2.1/TestCases.dll \
        HotfixAOT/Patched/HotfixAOT.patch true NeoStep15
      ```
      plus a full `NeoStep` run. Expected: identical to the HEAD baseline
      (218/0/1 at HEAD `70505eba`; NeoStep15 stays green). This is a CONFIRMATION
      gate only -- a difference would mean an accidental source edit (investigate;
      do not ship). TestCases need NOT be rebuilt (no TestCases edit).

## 4. Closeout (scoped-deferral)

- [ ] 4.1 Stage precisely: the four openspec artifacts (`proposal.md`,
      `design.md`, `specs/neo-optimizer/spec.md`, `tasks.md`) + the
      `.trae/documents/neo-deferred-items.md` edit + the existing
      `planning-context.md`/`README.md`/`.openspec.yaml`. EXCLUDE the usual churn
      (.pdb/.gitignore/nuget.config/.claude/.vscode/CLAUDE.md and the two stray
      `C\357\200\272UsersSayostep15_tc*.log` files at the repo root).
- [ ] 4.2 Commit message:
      `Neo D-PEEP (scoped-deferral): box;isinst peephole fusion deferred -- PatchKind wrong shape, no fusion pass`
      + the `Co-Authored-By: Claude Opus 4.8 (1m context) <noreply@anthropic.com>`
      trailer. `git push origin features/object-model-overhaul`.
- [ ] 4.3 LEAD archive-merge: merge the ADDED DEFERRED requirement from
      `specs/neo-optimizer/spec.md` into the canonical
      `openspec/specs/neo-optimizer/spec.md` (append to the Requirements
      section), then move `openspec/changes/neo-peephole-isinst/` to
      `openspec/changes/archive/2026-07-08-neo-peephole-isinst/`. Record in the
      portfolio that child 5 (the LAST child) closed as scoped-deferral -- the
      portfolio is COMPLETE.
