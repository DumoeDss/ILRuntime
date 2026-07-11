## ADDED Requirements

### Requirement: The box T; isinst U peephole fusion is deferred behind a dedicated peephole-pass optimizer child (D-PEEP)

The Neo optimizer SHALL NOT fuse the CIL pair `box T; isinst U` (box a value
type then immediately type-check it) into a single direct check until a
dedicated additive peephole-pass child lands the prerequisite infrastructure
(an adjacent-opcode fusion pass with def-use/liveness analysis of the `box`
dest register + a fused replacement opcode on a standalone `OpCodeR` field).
(Status: DEFERRED - the prerequisite infrastructure does not exist on HEAD; the
Step-22 `PatchKind`/`PatchEntry` mechanism is the wrong shape to host it; no
fix ships. The fusion is a pure optimization: the current two-arm `box;isinst`
path is functionally correct, so this deferral has no correctness cost.)

The dump-gate on HEAD `70505eba`
(`openspec/changes/neo-peephole-isinst/design.md` decisions D1-D3) established
three load-bearing findings a future peephole-pass planner MUST NOT re-derive
the hard way. (1) The Step-22 `PatchKind` enum
(`GenericMethodTemplate.cs:54`: `TypeToken`, `MethodToken`, `IsRefMoveFlag`) and
`PatchEntry` struct (`:74-81`, keyed by `GenericParamIdx` + `CecilToken`) are a
generic-method-template T-IDENTITY VALUE-SUBSTITUTION mechanism -- they record
"substitute the concrete-T-derived value at a FIXED instruction index." A
peephole fusion is an OPCODE-STREAM REWRITE (pattern-match the pair, prove the
`box` dest is dead after the `isinst`, replace the pair with one fused op,
remove the `box`); the patch table has no field for "the second opcode of the
pair" / "the fused replacement" / "the dead-register predicate" and its applier
(`GenericMethodTemplate.cs:596-604`) has no rewrite logic. (2) NO peephole/
fusion pass exists in the optimizer (the `RegisterVM/` tree has zero matches
for `peephole|fuse|fusion|IsinstResult`; the existing passes -- FCP, BCP, ELDC,
InlineMethod, RegisterCleanup, the Neo back-half `TypeSpecializeNeoOpcodes`/
`AllocateLocalStackSpaces`/`LowerNeoOffsets` -- do not pattern-match adjacent
opcodes into a fused form). (3) `box T; isinst U` IS emitted as two adjacent
register opcodes from the same CIL-translate arm (`JITCompiler.cs:2637-2645`)
and runs correctly today via two independent `ExecuteNeo` arms (`Box`
`ILIntepreter.Neo.cs:2660`, `Isinst` `:3072-3096`); copy-propagation can move
the `box` away from the `isinst`, so a correct fusion pass needs real def-use/
liveness, not a trivial adjacency peephole.

A future peephole-pass child that lands this fusion SHALL satisfy ALL of: (a)
it is a NEW additive optimizer pass with adjacent-pattern-match + def-use/
liveness analysis of the `box` dest register (the fusion is legal ONLY when the
boxed object is unobserved after the `isinst`); (b) the fused replacement
opcode's payload lives on a STANDALONE `OpCodeR` field whose byte range does NOT
alias a wide-immediate field a runtime consumer reads (`OperandLong`/
`OperandDouble` @12-19, `OperandFloat` @8-11) -- the F-8 / OPT-HARDEN-K1 /
double-combine OpCodeR-union-aliasing discipline; (c) it MUST NOT be implemented
as a `PatchKind.IsinstResult` extension to the Step-22 patch table (wrong
abstraction; see finding 1); (d) it is gated `#if ENABLE_NEO_MODE` (Neo-only)
and a stash-toggle plain-`Debug` + `useRegister=true` NeoStep-filter run shows
the SAME pre-existing Legacy failure set; (e) an adversarial equivalence probe
proves the fused form produces the SAME result as the un-fused `box;isinst` for
the null case, the wrong-type case, the subclass case, AND the case where the
boxed object IS observed after the check (fusion correctly declined). The
canonical `neo-optimizer` "PatchEntry captures only T-identity operand sites"
requirement is UNCHANGED and still governs the Step-22 patch table; this
requirement only records that D-PEEP is not a PatchEntry.

#### Scenario: No box;isinst fusion ships without a peephole-pass child
- **WHEN** the optimizer runs on HEAD `70505eba` (no peephole-pass child has
  landed)
- **THEN** a CIL `box T; isinst U` sequence MUST execute as two opcodes (a `Box`
  followed by an `Isinst`) with NO fused form emitted, because no fusion pass
  exists in the optimizer
- **AND** the result MUST be correct (the two-arm path is the only path), so the
  absence of fusion is a perf cost only, not a correctness gap

#### Scenario: The Step-22 PatchKind is not extended with IsinstResult
- **WHEN** a future change proposes to fuse `box T; isinst U` by adding an
  `IsinstResult` value to the Step-22 `PatchKind` enum
- **THEN** that proposal MUST be rejected: `PatchEntry`
  (`GenericMethodTemplate.cs:74-81`) is a value-substitution at a fixed
  instruction index (keyed by `GenericParamIdx` + `CecilToken`); a peephole
  fusion is an opcode-stream rewrite (delete the `box`, merge into the `isinst`)
  that the patch table's fields and applier cannot express
- **AND** the future fusion MUST instead be a new additive optimizer pass +
  a fused opcode on a standalone `OpCodeR` field

#### Scenario: A correct fusion requires liveness, not adjacency
- **WHEN** a future fusion pass considers fusing a `Box` into a following
  `Isinst`
- **THEN** the pass MUST prove the `Box` dest register is dead after the
  `Isinst` (the boxed object is not observed by any later instruction), because
  copy-propagation can move the `Box` away from the immediately-adjacent
  `Isinst` and an observed box makes the fusion illegal
- **AND** a fusion that fires only on trivial adjacency (no liveness) MUST be
  rejected as incorrect

#### Scenario: A future fused opcode obeys the OpCodeR-union discipline
- **WHEN** a future fusion lands a fused replacement opcode carrying both the
  `box` type token T and the `isinst` type token U
- **THEN** the opcode's payload MUST live on standalone `OpCodeR` fields whose
  byte ranges do NOT alias `OperandLong`/`OperandDouble` (@12-19) or
  `OperandFloat` (@8-11)
- **AND** the pass MUST verify per-opcode-kind disjointness (the F-8
  `LowerNeoOffsets` `Operand3` @16-clobbers-`OperandDouble` discipline), so the
  fusion does not reintroduce the OpCodeR-union-aliasing defect class

#### Scenario: The current box;isinst path is correct without fusion
- **WHEN** an IL method under `ENABLE_NEO_MODE` evaluates `boxedLocal is U` or
  `( boxedLocal as U )` on a value-type-typed local (the C# compiler emits
  `box T; isinst U`)
- **THEN** the result MUST be correct on HEAD without any fusion (null source
  -> null; matching type -> the reference; non-matching type -> null), proving
  the fusion is a pure optimization with no correctness gap
- **AND** the Step-15 `NeoStep15_*` smoke exercising `isinst` MUST stay green
  (it already covers this shape through the un-fused path)

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM
- **THEN** the Legacy NeoStep-filter smoke MUST show the SAME pre-existing
  failures with and without any future fusion change, because the fusion pass +
  the fused opcode SHALL be gated `#if ENABLE_NEO_MODE` and compile out of
  plain `Debug`

#### Scenario: Activation requires the prerequisite child
- **WHEN** no dedicated peephole-pass optimizer child has landed (the
  prerequisite -- a new fusion pass + liveness + a standalone-field fused
  opcode -- does not exist)
- **THEN** this requirement stays DEFERRED and no fusion code SHALL ship
- **AND WHEN** a peephole-pass child lands that satisfies conditions (a)-(e)
  above
- **THEN** this requirement becomes ACTIVE for the fused shapes that child
  covers, and the child's adversarial equivalence probe is the gate
