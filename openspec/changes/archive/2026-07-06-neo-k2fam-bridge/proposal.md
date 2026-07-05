## Why

The K2-FAM deferred item ("a CLR value-type LOCAL sourced from Box/Initobj, passed
by value to a CLR method, mis-reads an int as an mStack index" — a silent-wrong-result
defect from the old boxed-ref CLR-VT-local representation) was probed on HEAD
`f7539642` and is **no longer reproducible**: every adversarial reproducer for the
Box/Initobj/Unbox source shapes PASSES. The defect was **subsumed** as a side effect
of `neo-opt-harden-2` (F-MAJ-1: declared a CLR-VT local as flat bytes) and its
review-fix (rewrote the Box/Initobj/Unbox_Any arms to read/write flat bytes), plus
the Step 13b unified by-value-param read. The closure is real but currently
UNGUARDED — no smoke probe exercises a Box/Initobj/Unbox-sourced CLR-VT local passed
by value, so a future regression that re-introduces a boxed-ref local representation
(or breaks the flat-bytes Box/Initobj/Unbox arms) would slip past the green smoke
unchanged. This change locks the closure in with adversarial regression guards and
closes the deferred item.

## What Changes

- **Add 6 adversarial regression probes** to `TestCases/NeoStep13bTest.cs` (the
  existing K2-FAM home), each realizing a distinct Box/Initobj/Unbox-sourced
  CLR-VT-local → by-value-param shape. The `NeoStep` smoke filter catches them:
  - `NeoStep13_K2Fam_BoxSourceByValue` — local sourced from Box���Unbox, by value.
  - `NeoStep13_K2Fam_InitobjSourceByValue` — local sourced from `default(T)`, by value.
  - `NeoStep13_K2Fam_BoxMoveByValue` — Box→Unbox→struct-copy(Move)→by value.
  - `NeoStep13_K2Fam_ReinitThenByValue` — local re-initobj'd via `= default(T)`, by value.
  - `NeoStep13_K2Fam_BoxUnboxByValueToHost` — Box→Unbox→by-value to a host static helper.
  - `NeoStep13_K2Fam_TwoBoxedStructLocalsByValue` — TWO Box-sourced struct locals both
    passed by value (the F-MAJ-1 two-live-struct stress, but Box-sourced — the
    strongest guard against a boxed-ref-neighbour-corruption regression).
- **NO source change** to the runtime / JIT / optimizer. The fix already shipped
  (`neo-opt-harden-2` + its review-fix + `implement-neo-step13b`). This change is
  test-only.
- **Spec delta**: the `neo-boxing` requirement "Boxed-ref-local to flat-bytes-param
  bridge (K2-FAM closure)" is MODIFIED — its PARTIAL/DEFERRED Box/Initobj-source
  half is DELIVERED (resolved-by-recent-work); the K2-FAM bullet in the Out-of-scope
  deferrals requirement is REMOVED.
- **Deferred-items doc**: `neo-deferred-items.md` §2 master-table row K2-FAM and
  the §3 K2-FAM entry are updated to RESOLVED (subsumed). The F-2 /
  INLINER-REFONLY-VT cross-reference is left intact (F-2 is a distinct inliner
  ref-fold defect class — still deferred).

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `neo-boxing`: the "Boxed-ref-local to flat-bytes-param bridge (K2-FAM closure)"
  requirement moves from PARTIAL/DEFERRED to DELIVERED (the Box/Initobj/Unbox
  source shapes are now realized); the K2-FAM bullet is removed from the
  "Out-of-scope deferrals" requirement.

## Impact

- **Code**: `TestCases/NeoStep13bTest.cs` only (6 additive probe methods; the
  existing `NeoStep13_K2FamRegression` return-source probe stays). No
  `ILRuntime/` runtime / JIT / optimizer / CLR-binding file is touched.
- **Specs**: `openspec/specs/neo-boxing/spec.md` (delta merged at archive time).
- **Docs**: `.trae/documents/neo-deferred-items.md` (K2-FAM → Resolved).
- **Smoke**: NeoStep 140/140 → 146/146 (additive; all 6 probes PASS on HEAD).
  Legacy-neutral by construction (no source change; the probes pass on Legacy too
  because their assertions are representation-agnostic — they assert field-sum
  correctness, which holds on both engines).
- **Regression risk**: NONE (test-only).
