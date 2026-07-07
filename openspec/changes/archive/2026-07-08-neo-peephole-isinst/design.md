## Context

D-PEEP is a Step-15 deferral: fuse the CIL pair `box T; isinst U` (box a value
type then immediately type-check it) into a single direct check that avoids the
box heap allocation. The portfolio handoff (`neo-completion-portfolio/handoff/
lead-1.md` "Remaining") tagged it BLOCKED on patch-infra ("neither the fusion
pass nor `PatchKind` exists") and LOWEST priority (pure optimization). The
`.trae/documents/neo-deferred-items.md` section-3 D-PEEP entry repeats that
rationale. That rationale was written BEFORE `neo-step22-generic-template`
shipped, which introduced a `PatchEntry` struct + a `PatchKind` enum. So the
binding question for this change is a dump-gate on HEAD `70505eba`: does the
Step-22 patch infra NOW host the isinst fusion (SHIP), or is the fusion still
blocked on substantial new infrastructure (STOP-at-blocker / scoped-deferral)?

The dump-gate asked three concrete questions:

1. Does `PatchKind` exist NOW (post-Step-22)? What values? Could an
   `IsinstResult` kind be ADDED to it, or is it generic-template-specific?
2. Does any peephole/fusion pass exist (a pass that pattern-matches adjacent
   opcodes and emits a fused form)? Or only the Step-22 template-patch mechanism?
3. Is `box T; isinst U` even EMITTED by the JIT in a form a fusion pass could
   see, or is `isinst` already specialized away?

The answers (with file:line evidence gathered read-only on HEAD `70505eba`)
decide SHIP vs STOP. STOP-at-blocker is the ACCEPTED outcome for this lowest-
priority pure-optimization child (the planning-context and the portfolio
handoff both say so); this change MUST NOT force substantial new infrastructure
for a non-functional gain.

## Goals / Non-Goals

**Goals:**
- Dump-gate the patch infra on HEAD and record the verdict with file:line
  evidence.
- Decide SHIP-the-fusion vs STOP-at-blocker honestly; STOP is acceptable.
- Record the prerequisite (a future peephole-pass child) durably in the
  canonical spec + the deferred-items map so a future planner does not re-derive
  it.
- Keep the canonical spec honest: D-PEEP stays deferred, NOT promoted to "met".

**Non-Goals:**
- Building a peephole-pass framework (the anti-pattern for this priority).
- Extending `PatchKind` with an `IsinstResult` value (wrong abstraction; see D1).
- Any change to the `isinst`/`Box` runtime arms, the JIT, the optimizer, or any
  opcode. The current 2-arm `box;isinst` path is correct.
- A regression guard proving a fused form equals the un-fused form. There is no
  fused form to guard; the un-fused path is already the correct one and is
  already covered by the Step-15 `NeoStep15_*` smoke.

## Decisions

### D1 -- `PatchKind` exists post-Step-22 but is the WRONG shape for a peephole fusion

The Step-22 patch infra lives in
`ILRuntime/Runtime/Intepreter/RegisterVM/GenericMethodTemplate.cs`:

```
:44  internal enum PatchField { Operand = 0, Operand2 = 1, Operand4 = 2 }
:54  internal enum PatchKind   { TypeToken = 0, MethodToken = 1, IsRefMoveFlag = 2 }
:74  internal struct PatchEntry {
        public int InstrIdx;        // index into the template OpCodeR[] body
        public PatchField Field;    // which standalone OpCodeR field
        public PatchKind Kind;      // how to derive the concrete value
        public int GenericParamIdx; // which generic arg (0-based) drives the value
        public object CecilToken;   // the Cecil token to re-resolve via the instance
     }
```

So `PatchKind` DOES exist (the handoff's "PatchKind does not exist" is STALE on
this one point). But its three values -- `TypeToken`, `MethodToken`,
`IsRefMoveFlag` -- are ALL generic-method-template T-IDENTITY value-substitution
kinds, and the whole `PatchEntry` struct is keyed by `GenericParamIdx` (which
generic arg drives the value) + `CecilToken` (the Cecil token to re-resolve at
AOT-instantiate time). The extractor
(`GenericMethodTemplate.cs:283-365`) and applier (`:596-604`) are built around
"substitute the concrete-T-derived value at a FIXED instruction index." The
canonical `neo-optimizer` requirement "PatchEntry captures only T-identity
operand sites, never byte offsets" codifies this discipline.

A peephole fusion of `box T; isinst U` is a fundamentally DIFFERENT operation:
it is an OPCODE-STREAM REWRITE -- pattern-match the adjacent pair, prove the
`box` dest register is dead after the `isinst`, replace the pair with a single
fused op (a direct `IsAssignableFrom`/`CanAssignTo` check with no box), and
REMOVE the `box`. That is not a value-substitution at a fixed index; the patch
table has no way to express "delete opcode I and merge it into opcode I+1."
Adding an `IsinstResult` value to `PatchKind` would not help -- `PatchEntry`
still has no field for "the second opcode of the pair," "the fused replacement
opcode," or "the dead-register predicate," and the applier has no rewrite logic.
The Step-22 mechanism is the wrong shape by construction.

**Verdict (Q1):** an `IsinstResult` kind CANNOT be added to `PatchKind` to host
the fusion. The Step-22 patch infra is generic-template-specific and does not
generalize to peephole fusion.

### D2 -- NO peephole/fusion pass exists in the optimizer

The optimizer is a fixed set of passes under
`ILRuntime/Runtime/Intepreter/RegisterVM/`:

```
Optimizer.FCP.cs           (Forward Copy Propagation)
Optimizer.BCP.cs           (Backward Copy Propagation)
Optimizer.ELDC.cs          (Eliminate Constant Load)
Optimizer.InlineMethod.cs  (trivial method inlining)
Optimizer.RegisterCleanup.cs
Optimizer.Utils.cs
Optimizer.Neo.cs           (TypeSpecializeNeoOpcodes / AllocateLocalStackSpaces
                            / LowerNeoOffsets -- Neo-only back-half)
```

A repo-wide grep of the `RegisterVM/` tree for
`peephole|Peephole|fuse|Fuse|Fusion|fusion|IsinstResult` returns ZERO matches.
None of the existing passes pattern-matches ADJACENT opcodes to emit a FUSED
form: FCP/BCP propagate copies, ELDC eliminates constant loads, the Neo
back-half specializes opcodes one-at-a-time (Move->Move_Vt, F-10 markers,
Initobj insertion, byte-offset stamping). The Step-22 template mechanism
(`GenericMethodTemplate.cs`) is a generic-instantiation cache, not an opcode-
fusion pass.

**Verdict (Q2):** no peephole/fusion pass exists. A `box T; isinst U` fusion
requires a NEW additive optimizer pass that does adjacent-pattern-match +
def-use/liveness analysis (the `box` dest register must be dead after the
`isinst`, else the box is observable and the fusion is illegal) + emission of a
fused replacement opcode. That is substantial new infrastructure (a pass
framework + liveness), not an additive case on an existing pass.

### D3 -- `box T; isinst U` IS emitted adjacently and runs via two correct arms today

The JIT lowers `box`/`isinst`/`castclass` from the same CIL-translate arm
(`JITCompiler.cs:2637-2645`):

```
case Code.Box:
case Code.Unbox:
case Code.Unbox_Any:
case Code.Isinst:
case Code.Castclass:
    op.Register1 = (short)(baseRegIdx - 1);   // src
    op.Register2 = (short)(baseRegIdx - 1);   // dst
    op.Operand = method.GetTypeTokenHashCode(token);
    break;
```

So a CIL `box T; isinst U` sequence emits TWO adjacent register opcodes:
`Box r_src->r_box token=T` then `Isinst r_box->r_dst token=U`. They run via two
independent `ExecuteNeo` arms:

- `Box` arm (`ILIntepreter.Neo.cs:2660`) -- boxes the value type into an mStack
  object slot (a heap allocation) and writes the mStack index.
- `Isinst` arm (`ILIntepreter.Neo.cs:3072-3096`) -- reads
  `srcIdx = *(frameBase + ip->SrcOffset)`, fetches `obj = mStack[srcIdx]`, and
  tests `isinstType.TypeForCLR.IsAssignableFrom(obj.GetType())` (or
  `ILTypeInstance.CanAssignTo`), writing the original reference on success or
  `-1` on failure; a null source yields `-1`.

There is NO box-fusion fast path in either arm. The pair executes correctly
today (Step 15). Copy-propagation CAN move the `box` away from the immediately
adjacent `isinst`, which means a correct fusion pass cannot be a trivial
adjacency peephole -- it needs real def-use/liveness to find the `box`-produced
register and prove it is dead after the `isinst`. This reinforces D2: the pass
is non-trivial.

**Verdict (Q3):** the pair IS emitted in a form a fusion pass could see, but
only a pass with liveness analysis (not a trivial peephole) could fuse it
correctly. There is no functional gap today; the fusion is a pure speedup
(saves the box heap allocation when the boxed object is unobserved).

### D4 -- SHIP-vs-DEFER decision: STOP-at-blocker / scoped-deferral

Combining D1+D2+D3: hosting the `box T; isinst U` fusion requires substantial
NEW infrastructure that does not exist on HEAD -- (a) a new optimizer pass with
def-use/liveness analysis (D2+D3), AND (b) a new fused opcode on a standalone
`OpCodeR` field (D1 says the patch table cannot express it). The Step-22
`PatchKind`/`PatchEntry` mechanism (the infra the planning-context hoped might
host it) is provably the wrong shape (D1). The fusion is a PURE OPTIMIZATION
(non-functional): the current 2-arm path is correct (D3), so there is no
correctness gate forcing it. This child is the LOWEST-priority portfolio item.

Per the planning-context and the portfolio handoff, forcing substantial new
infrastructure (a peephole-pass framework + a fused opcode + liveness analysis)
for a non-functional gain on the lowest-priority item is the "force a fix past
the dump-gate" anti-pattern. The accepted outcome is STOP-at-blocker /
scoped-deferral: document the prerequisite (a dedicated peephole-pass child)
and close as a scoped-deferral. Precedent: `neo-async-controlflow-iscompleted`
(closed as scoped-deferral / no-op when its premise was disproven at the dump-
gate) and the Q-STRUCT / Q-LONG DEFERRED requirements in `neo-optimizer` (open
items recorded durably until a reproducing case / prerequisite lands). The
portfolio is COMPLETE either way; this is the last child.

**Decision: STOP-at-blocker.** Ship no source change. Record the dump-gate
verdict (D1+D2+D3, file:line) in `design.md`, add a DEFERRED requirement to
`neo-optimizer` codifying the prerequisite + the "PatchKind.IsinstResult is the
wrong abstraction" finding, and update the D-PEEP entry in
`.trae/documents/neo-deferred-items.md` with the post-Step-22 finding.

### D5 -- The OpCodeR-union discipline is binding on any FUTURE fused opcode (out of scope here, recorded for the future child)

Although this change ships no opcode, the DEFERRED requirement records the
contract the future peephole-pass child MUST satisfy so it is not re-derived:
any fused opcode's payload MUST live on a STANDALONE `OpCodeR` field whose byte
range does NOT alias a wide-immediate field a runtime consumer reads
(`OperandLong`/`OperandDouble` @12-19, `OperandFloat` @8-11). The F-8
(`Operand3` @16 clobbered `OperandDouble` high 4 bytes), OPT-HARDEN-K1, and
double-combine defects were ALL OpCodeR-union aliasing -- the recurring sharp
edge called out in the canonical `neo-optimizer` "Combining 2+ double locals"
requirement. A future `box;isinst` fused opcode that packed the two type tokens
into aliased fields would reintroduce that defect class.

## Risks / Trade-offs

- **[D-PEEP stays indefinitely deferred]** -> the optimization never ships until
  a peephole-pass child lands. ACCEPTED: D-PEEP is the lowest-priority pure-
  optimization item; the current path is correct. The DEFERRED requirement + the
  deferred-items update make the prerequisite discoverable so a future planner
  can pick it up.
- **[A future planner re-derives the dump-gate]** -> Mitigated by recording the
  file:line evidence (D1+D2+D3) in this `design.md` + the DEFERRED requirement.
  The finding "PatchKind.IsinstResult is the wrong abstraction" is the load-
  bearing one to preserve (a future planner will be tempted to add the kind;
  D1 explains why it cannot work).
- **[Forcing the fusion past the gate]** -> the anti-pattern this change
  explicitly avoids. The decision is STOP, not SHIP; no executable code ships.
- **[No functional risk]** -> no source changes; the full `NeoStep` smoke
  (218/0/1 at HEAD) is unchanged. Legacy (`ExecuteR`) is untouched.

## Open Questions

- **Does the C# compiler actually emit `box T; isinst U` often enough to be
  worth a future fusion pass?** Out of scope for this change (it ships nothing).
  The Step-15 smoke already exercises `isinst`; a future peephole-pass child
  should dump-gate the frequency in real hot paths before committing to the
  pass. The DEFERRED requirement does not assume the optimization is worthwhile
  -- only that IF it is built, it must be a new pass + a standalone-field fused
  opcode, not a PatchKind extension.
- **Could the fusion instead be done at the CIL level (before register
  lowering)?** Possible (a CIL-level `isinst box(...)` -> direct-check rewrite).
  This is a design alternative the future child should evaluate; it does not
  change this change's STOP verdict (no CIL-level fusion pass exists either).
  Recorded in the DEFERRED requirement as an open design direction, not
  prescribed.
