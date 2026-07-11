## MODIFIED Requirements

### Requirement: The box T; isinst U peephole fusion is deferred behind a dedicated peephole-pass optimizer child (D-PEEP)

The Neo optimizer SHALL NOT fuse the CIL pair `box T; isinst U` (box a value
type then immediately type-check it) into a single direct check. The fusion
remains a deferred optimization: the current two-arm `box;isinst` path is
functionally correct, and the fusion is gated on a demonstrated hot path that
actually emits the adjacency, not on missing infrastructure.

(Status: DEFERRED - on HEAD `162ce992` the fusion is deferred on ZERO observed
payoff, not on missing infrastructure. The live-range def-use framework a fusion
would reuse EXISTS (the Step-17 `addrAlias`/`liveAliases`/`liveAliasMap`
machinery in `Optimizer.Neo.cs:35-428`), and the fused-opcode encoding is
available (`Operand` @8 carries T, `Operand2` @12 carries U, both standalone and
F-8-disjoint from wide-immediates; `Operand3` @16 MUST be avoided as it aliases
the `OperandDouble` high 4 bytes). A Cecil-based frequency scan of TestCases.dll
+ ILRuntime.dll + ILRuntimeTestBase.dll found 0 adjacent `box;isinst` pairs
across 1071 boxes and 1568 isinsts: the C# compiler elides the box when the
source is already `object` (the overwhelmingly common case, e.g. `NeoStep15_
TC4_BoxedValueTypeIs`) and folds value-type checks via constrained-virtual /
`Unbox_Any`, so the JIT-time adjacency rate is zero. No fix ships until a real
hot path that emits the adjacency is demonstrated; this deferral has no
correctness cost.)

The dump-gate on HEAD `70505eba` (prior child
`archive/2026-07-08-neo-peephole-isinst/design.md` decisions D1-D3) and the
re-gate on HEAD `162ce992` (`neo-peephole-pass/design.md` decisions D1-D4)
establish these load-bearing findings a future peephole-pass planner MUST NOT
re-derive the hard way. (1) The Step-22 `PatchKind` enum
(`GenericMethodTemplate.cs:54`: `TypeToken`, `MethodToken`, `IsRefMoveFlag`) and
`PatchEntry` struct (`:74-81`, keyed by `GenericParamIdx` + `CecilToken`) are a
generic-method-template T-IDENTITY VALUE-SUBSTITUTION mechanism -- they record
"substitute the concrete-T-derived value at a FIXED instruction index." A
peephole fusion is an OPCODE-STREAM REWRITE (pattern-match the pair, prove the
`box` dest is dead after the `isinst`, replace the pair with one fused op,
remove the `box`); the patch table has no field for "the second opcode of the
pair" / "the fused replacement" / "the dead-register predicate" and its applier
(`GenericMethodTemplate.cs:596-604`) has no rewrite logic. (2) NO peephole/
fusion pass fires today (the `RegisterVM/` tree has zero matches for
`peephole|fuse|fusion|IsinstResult`); the existing passes -- FCP, BCP, ELDC,
InlineMethod, RegisterCleanup, the Neo back-half `TypeSpecializeNeoOpcodes`/
`AllocateLocalStackSpaces`/`LowerNeoOffsets` -- do not pattern-match adjacent
opcodes into a fused form. CORRECTION to the prior child: the live-range def-use
FRAMEWORK such a pass would build on IS now present as the Step-17
`liveAliases`/`liveAliasMap` machinery (`Optimizer.Neo.cs:126-428` -- a forward
walk with def/use/kill + an escape predicate + per-instruction live-range
snapshots that handle eval-stack-register reuse), so the blocker is no longer a
missing framework; it is the zero observed payoff. (3) `box T; isinst U` IS
emitted as two adjacent register opcodes from the same CIL-translate arm
(`JITCompiler.cs:2637-2645`) and runs correctly today via two independent
`ExecuteNeo` arms (`Box` `ILIntepreter.Neo.cs:2892`, `Isinst` `:3413`); copy-
propagation can move the `box` away from the `isinst`, so a correct fusion pass
needs the Step-17-style liveness, not a trivial adjacency peephole. (4) The
fused check has a real semantic hazard: it MUST answer "is value-type T's
identity (or its implemented interfaces) assignable to U" -- NOT `obj.GetType()`
on a non-existent boxed object (a boxed `int` is assignable to `object`/
`ValueType`/`int`/its interfaces, NOT to `long`).

A future peephole-pass child that lands this fusion SHALL satisfy ALL of: (a)
it is a NEW additive optimizer pass with adjacent-pattern-match + def-use/
liveness analysis of the `box` dest register (reusing the Step-17
`liveAliases`/`liveAliasMap` machinery), legal ONLY when the boxed object is
unobserved after the `isinst`; (b) the fused replacement opcode carries the TWO
type tokens on `Operand` @8 (T) and `Operand2` @12 (U) -- both STANDALONE int
fields whose byte ranges do NOT alias a wide-immediate field a runtime consumer
reads (`OperandLong`/`OperandDouble` @12-19, `OperandFloat` @8-11) per the F-8 /
OPT-HARDEN-K1 / double-combine OpCodeR-union-aliasing discipline; `Operand3`
@16 MUST NOT be used (it aliases the `OperandDouble` high 4 bytes -- the F-8
clobber); (c) it MUST NOT be implemented as a `PatchKind.IsinstResult` extension
to the Step-22 patch table (wrong abstraction; see finding 1); (d) it is gated
`#if ENABLE_NEO_MODE` (Neo-only) and compiles out under plain `Debug` so a
stash-toggle plain-`Debug` + `useRegister=true` NeoStep-filter run shows the SAME
pre-existing Legacy failure set; (e) BEFORE building the pass, the child SHALL
re-run a Cecil-based adjacency scan on the target workload and document a hot
path that actually emits `box;isinst` adjacency -- without a demonstrated hit,
the pass is premature optimization (the canonical workloads at HEAD show 0/1071);
(f) an adversarial equivalence probe proves the fused form produces the SAME
result as the un-fused `box;isinst` for the null case, the wrong-type case, the
subclass case, the boxed-`int`-vs-`long` identity case (finding 4), AND the case
where the boxed object IS observed after the check (fusion correctly declined).
The canonical `neo-optimizer` "PatchEntry captures only T-identity operand sites"
requirement is UNCHANGED and still governs the Step-22 patch table; this
requirement only records that D-PEEP is not a PatchEntry.

#### Scenario: No box;isinst fusion ships without a demonstrated hot path
- **WHEN** the optimizer runs on HEAD `162ce992` (no peephole-pass child has
  landed; the Step-17 live-range framework exists but no fusion pass uses it)
- **THEN** a CIL `box T; isinst U` sequence MUST execute as two opcodes (a `Box`
  followed by an `Isinst`) with NO fused form emitted, because no fusion pass
  exists in the optimizer
- **AND** the result MUST be correct (the two-arm path is the only path), so the
  absence of fusion is a perf cost only, not a correctness gap
- **AND** a Cecil-based adjacency scan over the canonical workloads (TestCases +
  ILRuntime + ILRuntimeTestBase) MUST show zero adjacent `box;isinst` pairs,
  confirming the fusion would fire on no path today

#### Scenario: The Step-22 PatchKind is not extended with IsinstResult
- **WHEN** a future change proposes to fuse `box T; isinst U` by adding an
  `IsinstResult` value to the Step-22 `PatchKind` enum
- **THEN** that proposal MUST be rejected: `PatchEntry`
  (`GenericMethodTemplate.cs:74-81`) is a value-substitution at a fixed
  instruction index (keyed by `GenericParamIdx` + `CecilToken`); a peephole
  fusion is an opcode-stream rewrite (delete the `box`, merge into the `isinst`)
  that the patch table's fields and applier cannot express
- **AND** the future fusion MUST instead be a new additive optimizer pass +
  a fused opcode on standalone `Operand` (T) + `Operand2` (U) fields

#### Scenario: The fused-opcode encoding respects the F-8 union-aliasing discipline
- **WHEN** a future fusion opcode carries the two type tokens T and U
- **THEN** T MUST be encoded on `Operand` @8 and U on `Operand2` @12 (both
  standalone int fields), and `Operand3` @16 MUST NOT be used because it aliases
  the `OperandDouble` high 4 bytes (the F-8 clobber)
- **AND** neither field's byte range SHALL overlap a wide-immediate field a
  runtime consumer reads (`OperandLong`/`OperandDouble` @12-19, `OperandFloat`
  @8-11)
