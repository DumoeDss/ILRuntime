# Ship Log — neo-peephole-isinst (D-PEEP, scoped-deferral)

> D-PEEP — the `box T; isinst U` peephole fusion. Shipped 2026-07-08 as a
> **SCOPED-DEFERRAL** (no code/test change). Capability: `neo-optimizer`. Parent
> portfolio: `neo-completion-portfolio`. This is the LAST portfolio child.

## What shipped (scoped-deferral — the dump-gated outcome)

A HEAD `70505eba` dump-gate updated the handoff's premise and reached a
**STOP-at-blocker / scoped-deferral**:

- **`PatchKind` EXISTS post-Step-22** (`GenericMethodTemplate.cs:54`:
  `enum PatchKind { TypeToken, MethodToken, IsRefMoveFlag }` + `PatchEntry` keyed
  by `GenericParamIdx` + `CecilToken`) but is the **WRONG shape** for a peephole
  fusion. It is a generic-method-template T-identity VALUE-SUBSTITUTION mechanism
  ("substitute the concrete-T value at a fixed instruction index"); a `box;isinst`
  fusion is an OPCODE-STREAM REWRITE (delete the box, merge into the isinst) that
  the patch table's fields/applier cannot express. Adding an `IsinstResult` kind
  would not help.
- **NO peephole/fusion pass exists** (grep of `RegisterVM/` for
  `peephole|fuse|fusion|IsinstResult` = zero matches; the optimizer passes are
  FCP/BCP/ELDC/InlineMethod/RegisterCleanup + the Neo back-half, none pattern-
  match adjacent opcodes).
- **`box T; isinst U` IS emitted adjacently** (`JITCompiler.cs:2637-2645`) and
  runs correctly via two arms (`Box` + `Isinst`) with NO box-fusion fast path.
  Copy-prop can move the box away from the isinst, so a correct fusion needs real
  def-use/liveness, not a trivial adjacency peephole.

**Resolution = DEFERRED.** Hosting the fusion requires substantial NEW infra (a
peephole-pass framework + liveness + a fused opcode on a standalone `OpCodeR`
field per the F-8 union discipline). Forcing that for a non-functional gain on
the lowest-priority item is the "force a fix past the dump-gate" anti-pattern
(the `neo-step20-async-suspend` / `neo-async-controlflow-iscompleted` / Q-STRUCT /
Q-LONG precedent). The `box;isinst` path stays CORRECT un-fused.

## Files changed (DOC-ONLY — no code/test/smoke change)

- `.trae/documents/neo-deferred-items.md` — the D-PEEP §2 row + §3 detail updated
  (replaced the stale "PatchKind does not exist" with the post-Step-22 finding:
  PatchKind exists but is the wrong shape; no fusion pass; needs a peephole-pass
  framework + liveness).
- `openspec/specs/neo-optimizer/spec.md` — 1 ADDED DEFERRED requirement (the
  fusion is deferred; `PatchKind.IsinstResult` is the wrong abstraction; the
  contract a future peephole-pass child must satisfy).
- (NO `ILRuntime/` source change. NO runtime/JIT/optimizer/opcode/PatchKind
  change. NO test change. Smoke is byte-identical to HEAD.)

## Verification

The change is doc-only; the smoke is byte-identical to HEAD (a confirmation gate,
not a regression gate). The dump-gate verdict is code-grounded (file:line-cited
in `design.md`): `PatchKind` exists at `GenericMethodTemplate.cs:54`; no fusion
pass (grep zero matches); `box;isinst` emits adjacently at `JITCompiler.cs:2637-
2645` and runs correctly via the two arms. No code to regress.

## Route (the future peephole-pass child)

A future peephole-pass child SHALL: add a new ADDITIVE optimizer pass (adjacent-
opcode pattern-match + fused emit) + a liveness/def-use analysis (copy-prop can
move the box away) + a fused opcode on a STANDALONE `OpCodeR` field (the F-8
union discipline — never alias a wide-immediate) + an adversarial equivalence
probe (the fused form produces the SAME result as the un-fused `box;isinst`,
incl. the null / wrong-type / subclass cases) + Neo-only `#if ENABLE_NEO_MODE`
+ Legacy-neutral.

## Legacy impact

None. No code change. The deferred-items + spec delta are documentation.
