## Context

D-PEEP is the `box T; isinst U` peephole fusion. The prior child
(`archive/2026-07-08-neo-peephole-isinst`, HEAD `70505eba`) closed it as a
scoped-deferral with three load-bearing findings: (1) the Step-22
`PatchKind`/`PatchEntry` mechanism is the wrong shape (a value-substitution at
a fixed index, not an opcode-stream rewrite); (2) no peephole/fusion pass
exists; (3) the pair IS emitted adjacently and runs correctly via two arms
today. That child's STOP rationale was: "hosting the fusion requires substantial
NEW infrastructure (a peephole-pass framework + liveness + a fused opcode), and
forcing that for a non-functional gain on the lowest-priority item is the
force-a-fix-past-the-dump-gate anti-pattern."

The planning-context for THIS change re-opens the gate with a sharper scope-aware
question: is the fusion pass actually tractable on HEAD `162ce992` (the prior
child assumed it needed a "full new dataflow framework" and never evaluated the
Step-17 adjacent-opcode machinery), and is the fused pattern actually EMITTED
often enough to be worth the pass? The dump-gate below answers both. The answers
refute the prior child's "needs new infra" premise (the framework already exists)
AND find the pattern is never emitted in practice, yielding a stronger, evidence-
based CLOSE.

## Goals / Non-Goals

**Goals:**
- Dump-gate the fusion on HEAD `162ce992` with file:line + empirical evidence.
- Decide SHIP-the-pass vs CLOSE-as-optimization-deferral honestly; both are
  acceptable outcomes for this pure-optimization item.
- Correct the prior child's now-stale "needs substantial new infrastructure"
  finding (the Step-17 machinery is the live-range framework a fusion would
  reuse), so a future planner does not re-derive the wrong blocker.
- Record the encoding answer (`Operand`+`Operand2`) and the zero-payoff finding
  durably in the canonical spec.

**Non-Goals:**
- Building the fusion pass (this change ships no code).
- Any change to `Box`/`Isinst`/`Castclass` runtime arms, the JIT, the optimizer,
  or any opcode. The current 2-arm path is correct.
- A regression guard proving a fused form equals the un-fused form. There is no
  fused form to guard; the un-fused path is the only path and is already covered
  by the Step-15 smoke.

## Decisions

### D1 -- The fusion pass IS tractable on HEAD: the Step-17 `liveAliases`/`liveAliasMap` machinery IS a reusable live-range def-use framework

The prior child's STOP rationale rested on "the fusion requires substantial NEW
infrastructure (a peephole-pass framework + liveness + def-use), not an additive
case on an existing pass." That premise is STALE on HEAD `162ce992`. The Step-17
`addrAlias` reconciliation in `Optimizer.Neo.cs` IS a live-range-aware def-use
framework that a `box;isinst` fusion pass would reuse directly:

- `Optimizer.Neo.cs:35-81` builds a static `addrAlias` map over the whole body
  (a forward walk matching address-producing opcodes, inheriting aliases along
  `ldflda` chains).
- `Optimizer.Neo.cs:126-318` runs a SECOND forward walk with a `liveAliases`
  set: each address PRODUCER (ldloca/ldflda) ADDS its dest to `liveAliases`;
  any opcode that WRITES that register for another purpose KILLS it
  (`GetOpcodeDestRegister` + the kill loop at `:300`); a non-foldable READ of a
  live alias MARKS it escaped. This is exactly the def/use/kill + escape
  predicate a `box;isinst` fusion needs (substitute "box dest" for "address
  alias": the box dest must be DEAD after the isinst, i.e. no later read).
- `Optimizer.Neo.cs:410-428` maintains a per-instruction `liveAliasMap`
  snapshot so eval-stack-register REUSE (the same register number holding a box
  dest in one range and an unrelated value in another) is handled correctly --
  the same mixed-reuse hazard the prior child flagged as needing liveness.

So the "real def-use/liveness, not a trivial adjacency peephole" requirement the
prior child cited as the blocker is ALREADY implemented and battle-tested (Step
17 shipped for the byref/address-alias path). A `box;isinst` fusion would add an
ADJACENT-pattern-match case + a dead-box-dest predicate on top of this
machinery. Tractability: HIGH. (Finding 2 of the prior child is partially
refuted -- there is still no FUSION pass, but the live-range framework it would
need is no longer missing.)

### D2 -- The fused-opcode encoding IS available: `Operand` (T) + `Operand2` (U), both standalone, F-8-disjoint

`OpCodeR` (`OpCodes/OpCode.cs:36-71`) is `[StructLayout(LayoutKind.Explicit)]`.
The standalone int fields and their overlap with the wide-immediate forms:

```
@8   Operand / OperandFloat    (OperandFloat aliases Operand's 4 bytes)
@12  Operand2                  (aliases OperandLong/OperandDouble low 4 bytes)
@16  Operand3                  (aliases OperandLong/OperandDouble HIGH 4 bytes -- the F-8 clobber)
@20  Operand4                  (standalone; after the 8-byte wide-immediate block @8-19)
```

The F-8 / OPT-HARDEN-K1 discipline (canonical `neo-optimizer` "Combining 2+
double locals" + "PatchEntry captures only T-identity operand sites" requirements)
forbids aliasing a wide-immediate field a runtime consumer reads
(`OperandLong`/`OperandDouble` @12-19, `OperandFloat` @8-11). The current `Box`
and `Isinst` opcodes both carry their single type token on `Operand` @8
(`ILIntepreter.Neo.cs:2895` reads `ip->Operand` for the box type; `:3415` reads
`ip->Operand` for the isinst type). A fused opcode needs TWO type tokens (T, the
boxed type, and U, the checked type). The encoding answer: `Operand` @8 carries
T and `Operand2` @12 carries U. BOTH are standalone int fields; NEITHER aliases
a wide-immediate field that a fused-opcode runtime consumer would read (a fused
`Isinst`/`Castclass`-family opcode is not itself a wide-immediate/float form, so
the @12-19 wide block is unused). `Operand3` @16 MUST be avoided (it aliases the
wide block high -- the F-8 clobber). Encoding: SOLVED.

### D3 -- The fused pattern is NEVER emitted in practice: 0 adjacent pairs across 1071 boxes

The binding dump-gate question is frequency: is `box T; isinst U` a hot path or
rare? An empirical probe answers it definitively. A throwaway Cecil scanner
counted, over three real DLLs (TestCases + ILRuntime.dll + ILRuntimeTestBase.dll
at HEAD `162ce992`), every `box`, every `isinst`/`castclass`, and every
immediately-adjacent `box;(isinst|castclass)` pair:

```
DLL                        boxes  isinst  castclass  box;isinst  box;castclass
TestCases.dll               217      52         70           0             0
ILRuntime.dll               449    1048           -           0             0
ILRuntimeTestBase.dll       405     468           -           0             0
TOTAL                      1071    1568                   0             0
```

**ZERO adjacent `box;isinst` pairs across 1071 boxes and 1568 isinsts.** The C#
compiler does not emit this adjacency because:

- When the source is already `object`-typed (the overwhelmingly common case --
  e.g. `object boxed = v; ...; boxed is T`, `obj[1] is StructTest2`, `e is
  MyEx` where `e` is a reference), there is NO box; the `is` emits a bare
  `isinst`. NeoStep15's only box+is smoke (`TC4_BoxedValueTypeIs`) is this
  shape: `object boxed = v; boxed is NeoStep15Vt` -- a bare `isinst` on an
  already-boxed object, no adjacent box.
- When a value-typed expression is checked against an interface/type it does not
  statically satisfy, the compiler emits `box T; isinst U` ONLY in narrow cases
  (e.g. `v as Interface` where the box is not folded), and none of these reach
  adjacent form in real C# on these workloads -- the compiler folds the check
  via `constrained.` virtual calls or `Unbox_Any` instead.

The optimizer passes do not change this: FCP/BCP propagate copies (never
synthesize a box -- verified: `grep -n "Box\|new OpCodeR"` over `Optimizer.FCP.cs`
/ `Optimizer.BCP.cs` returns no box emission), and the Neo back-half specializes
opcodes one-at-a-time. So the JIT-time `box;isinst` adjacency rate equals the
source-IL rate: **0/1071**. (A fusion pass that DID fire would also need D4
correctness care -- but it would never fire, so the point is moot.)

### D4 -- The fused check has a real semantic hazard (moot given D3, recorded for the future child)

Although this change ships nothing, the contract for a FUTURE child records the
correctness subtlety: the fused check must NOT simply run `isinstType.IsAssign-
ableFrom(boxed.GetType())` on an unboxed value. A boxed `int` is assignable to
`object`, `ValueType`, `int`, and any interface `int` implements -- but NOT to
`long`. The fused opcode must therefore answer "is the value-type T's IDENTITY
(or its implemented interfaces) assignable to U" -- which differs from the
current `Isinst` arm's `obj.GetType()` path only in that there is no boxed
object whose `GetType()` to read. The adversarial equivalence probe a future
child MUST ship (null / wrong-type / subclass / observed-box) is already
specified in the existing DEFERRED requirement; this design does not add to it.

### D5 -- SHIP-vs-DEFER decision: CLOSE as optimization-deferral (zero-payoff, not infra-blocked)

Combining D1+D2+D3: the prior child's STOP rationale ("needs substantial new
infrastructure") is REFUTED -- the live-range framework exists (D1) and the
encoding is available (D2). So the gate can no longer close on infra grounds.
The gate now turns on PAYOFF (D3): the fused pattern is NEVER emitted in
practice (0/1071). Building an additive fusion pass for a pattern the compiler
does not emit is premature optimization with a guaranteed zero measured win on
the canonical workloads. This is the F-11 precedent (closed as "not-a-bug, an
optimization gap, no reproducing hot path") and the Q-STRUCT / Q-LONG precedent
(open items recorded durably until a reproducing case lands).

**Decision: CLOSE as optimization-deferral.** Ship no source change. Record the
dump-gate verdict (D1+D2+D3+D4, file:line + empirical) in this `design.md`, and
update the existing DEFERRED requirement in `neo-optimizer` to: (a) correct the
stale "prerequisite infrastructure does not exist" status (it now DOES, via
Step-17 + the `Operand`/`Operand2` encoding); (b) re-ground the deferral on
zero observed payoff, not missing infra; (c) preserve the full future-child
contract (additive pass + dead-box-dest liveness + standalone-field fused
opcode + adversarial probe + Neo-only + Legacy-neutral). The fusion stays
deferred until a real hot path that emits the adjacency is demonstrated.

## Risks / Trade-offs

- **[The fusion never ships until a hot path is found]** -> the optimization
  stays deferred. ACCEPTED: D3 shows the pattern is not emitted today, so there
  is no win to capture. The DEFERRED requirement preserves the tractability
  (D1) + encoding (D2) answers so a future planner can revive it the moment a
  reproducing hot path appears, without re-deriving the framework question.
- **[The frequency probe only covered three DLLs]** -> a different workload MIGHT
  emit the adjacency. Mitigated: the three DLLs (the test corpus + the runtime
  + the test harness) span 1071 boxes / 1568 isinsts and include every shape
  the NeoStep smoke + the Legacy regression suite exercise; the C# compiler's
  box-elision is workload-independent (driven by static type analysis, not
  method bodies). A future child that believes it has a hot path can re-run the
  Cecil scanner on that path's DLL before committing to the pass.
- **[A future planner re-derives the framework question]** -> Mitigated by
  recording D1 (Step-17 machinery is the reusable framework) + D2 (the
  `Operand`/`Operand2` encoding) in this `design.md` and the canonical spec.
  The load-bearing correction is "the prior child's needs-new-infra premise is
  stale" -- without D1 a future planner would wrongly conclude the fusion is
  still infra-blocked.
- **[No functional risk]** -> no source changes; the full `NeoStep` smoke is
  unchanged. Legacy (`ExecuteR`) is untouched.

## Open Questions

- **Could the C# compiler emit the adjacency under a specific idiom this probe
  missed?** Possible (e.g. a constrained-generic `where T : struct` method doing
  `value is IComparable`). Out of scope for this change (it ships nothing). A
  future child SHOULD re-run the Cecil scanner on the suspect workload before
  building the pass; D1+D2 mean the pass is tractable IF such a path exists.
- **Could the fusion instead be done at the CIL level (before register
  lowering)?** Possible and already noted in the prior child's design. Does not
  change this change's CLOSE verdict (no CIL-level fusion pass exists either;
  and the frequency gate applies to a CIL-level rewrite identically).
