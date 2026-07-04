# Design - implement-neo-opt-hardening

Bug-fix step. Each bug is grounded in real code with a confirmed (or, for
Q-STRUCT/Q-LONG, non-reproducible) root cause and a minimal fix. Author does
NOT verify - the implementer confirms.

## Scope decision (from propose)

Three candidate bugs. After reproduction on current HEAD:

| Bug | Reproduced on HEAD? | Action this change |
|-----|--------------------|--------------------|
| K1  | YES (DivideByZero fires) | FIX (minimal FCP field-write kill) |
| Q-STRUCT | NO (6 probes pass) | DEFER - track in spec, no fix |
| Q-LONG   | NO (3 probes pass)  | DEFER - track in spec, no fix |

The honest call: ship K1 (a confirmed silent correctness bug) with confidence;
do NOT guess at Q-STRUCT/Q-LONG - the shared optimizer passes affect every
method, and an un-reproduced fix there is a net regression risk. They stay
tracked so they are not lost.

---

## K1 - FCP mis-propagates value-type Moves (FIXED — corrected ldloca kill)

### Reproduction (confirmed on HEAD = features/object-model-overhaul)

Probe `ProbeK1_FcpVtPropagation` (in the probe file, promoted to a permanent
regression test):

```csharp
OptHardK1Struct a = default; a.n = 11;
OptHardK1Struct b = a;   // whole-VT Move; FCP records propagation b<=a
a.n = 999;               // stfld a.n
int got = b.n;           // MUST be 11
if (got != 11) { /* div by zero */ }
```

HEAD result: `Attempted to divide by zero.` at the `b.n` read - `b.n` returns
999, the mutated source value. After the fix: passes (returns 11).

### Root cause (file:line) — CORRECTED (ldloca address-handle indirection)

> NOTE: the original (pre-implementation) root cause below this heading was
> MIS-DIAGNOSED. The first attempt shipped a `Stfld_*_Inline` source-register
> kill that was a no-op and was reverted. See `planning-context.md` §9 for the
> full postmortem. The CORRECTED root cause and fix follow.

`Optimizer.FCP.cs`, `ForwardCopyPropagation`. The propagation `Move b=a`
records `xDst=b, xSrc=a`. A later field READ `b.field` is a `Ldfld_*_Inline`
whose single source is `op.Register2`; `ySrc == xDst` triggers
`ReplaceOpcodeSource(.., xSrc)` -> rewrites the read to `a.field`
(Optimizer.FCP.cs:76-99 in-block, :235-252 cross-block).

The field WRITE that invalidates this does NOT reach the source register
directly. In the actual lowered IR, the field store goes through a `ldloca.s`
address handle:

```
move      r1, r0           # b = a   (xDst=r1, xSrc=r0)
ldloca.s  r7, r0           # r7 = &a (r0 == xSrc)
ldc.i4    r8, 999
stfld.i4.inline r7, r8     # a.n = 999 (Register1 = r7, the ADDRESS, NOT r0)
ldfld.i4.inline r2, r0     # b.n read; FCP rewrote source r1 -> r0 (THE BUG)
```

`Stfld_*_Inline` `Register1` is the address temp `r7`, never the base local
`r0`/`r1`. So any kill keyed on `Stfld_*_Inline.Register1 == xSrc/xDst` (the
original design) can never fire — confirmed empirically in the reverted
attempt. The existing whole-register kill (`xDst == yDst`,
Optimizer.FCP.cs:152-167 / :293-300) does not fire either, because
`Stfld_*_Inline` reports no dest register (Optimizer.Utils.cs:908-932).

The TRUE aliasing signal is the `ldloca.s` itself: `ldloca rAddr, rBase` makes
`rBase`'s in-frame storage addressable, after which a `stfld/stind` through
`rAddr` mutates `rBase`. For K1, `ldloca.s r7, r0` takes the address of `r0`
(== xSrc), so the `b.n -> a.n` propagation is unsound.

### The minimal fix (Neo-only, Legacy-neutral) — CORRECTED: ldloca kill

Kill the propagation when a `Ldloca`/`Ldloca_S` takes the address of the
propagation source OR dest. `GetOpcodeSourceRegister` already reports the
Ldloca base local as `r1 = op.Register2` (Optimizer.Utils.cs:463-464 + :500),
which FCP reads into `ySrc`. So inside the existing scan, after the
whole-register kill, add (gated `#if ENABLE_NEO_MODE`):

```csharp
#if ENABLE_NEO_MODE
// K1: taking the address of xSrc or xDst means it can be mutated through
// that address by a later stfld/stind -> kill the propagation conservatively.
// The base local is the Ldloca source register (ySrc = op.Register2). The
// existing whole-register kill cannot see a field write (stfld reports no
// dest), and the field store reaches the base indirectly via this address
// handle, not through Register1.
if (Y.Code == OpCodeREnum.Ldloca || Y.Code == OpCodeREnum.Ldloca_S)
{
    if (ySrc >= 0 && (ySrc == xSrc || ySrc == xDst))
    {
        postPropagation = false; ended = true; break;   // in-block loop
        // (cross-block loop: cannotRemove = true; break;)
    }
}
#endif
```

Both the in-block loop and the cross-block loop get the kill (with their
respective abort variables). No helper or Utils change is needed: the Ldloca
source register is already enumerable, and the gate makes the kill Neo-only.
The kill matches BOTH `xSrc` and `xDst`: taking the address of the source
(the K1 case) and of the dest (mutating the dest's field after the copy) both
make any already-propagated field read stale.

### Fix-point scope: SHARED pass, but the gate is Neo-only

`ForwardCopyPropagation` is a shared pass (Legacy `ExecuteR` runs it too).
The fix is Legacy-neutral because the entire kill is inside
`#if ENABLE_NEO_MODE`. A plain-`Debug` (Legacy) build compiles the check out
entirely, so the Legacy FCP control flow is byte-identical to before this
change. (Verified empirically: a Legacy `Debug` NeoStep smoke shows the SAME
pre-existing 7 failures with and without the fix — they are Neo-feature tests
that don't run correctly on Legacy `ExecuteR`, unrelated to copy propagation.)

Note: `Ldloca`/`Ldloca_S` are NOT Neo-specific opcodes (Legacy emits them
too). The Neo-only gate here is a conservative choice: the kill is only KNOWN
to be required under Neo (where the inline-field-store path makes the
value-type-copy independence observable); applying it Legacy-wide would be a
broader behavior change to a shared pass and is out of scope. The gate keeps
Legacy untouched.

### Edge cases (K1)

- **Copy then mutate source PRIMITIVE field:** `a.n = 11; b = a; a.n = 999;
  read b.n` -> must read 11. Covered by `NeoOptHardTest_K1_FcpVtPropagation`
  (FAILS on HEAD, PASSES after fix).
- **Copy then mutate source, read BOTH source and dest:** `a.n` MUST be 999
  (mutation visible through the source's own address), `b.n` MUST be 11 (the
  copy snapshot). Covered by `NeoOptHardTest_K1_SourceAndDestAfterMutation`
  (FAILS on HEAD, PASSES after fix). Guards that the ldloca kill does not
  also suppress the source's own (correct) field reads.
- **Mutate the DEST field** (`b.n = ...` after the copy): the kill also fires
  on `ySrc == xDst`, conservatively ending the propagation. Covered by
  `NeoOptHardTest_K1_MutateDestAfterCopy`.
- **`ldloca` of an UNRELATED register:** `ySrc` matches neither `xSrc` nor
  `xDst` -> no kill, propagation continues (correct).
- **Cross-block propagation:** the same ldloca kill is applied in the
  cross-block pending-FCP loop (Optimizer.FCP.cs:210-340) — a `Ldloca` of
  `xSrc`/`xDst` in any successor block sets `cannotRemove = true`.

### Regression test that pins it

`TestCases/NeoOptHardeningTest.cs` — three K1 tests
(`NeoOptHardTest_K1_FcpVtPropagation`,
`NeoOptHardTest_K1_MutateDestAfterCopy`,
`NeoOptHardTest_K1_SourceAndDestAfterMutation`). The first and third FAIL on
HEAD (DivideByZero); all three PASS after the fix.

---

## Q-STRUCT - struct-local + field-mutation + element-read (DEFERRED)

### Reproduction attempt (FAILED to reproduce on HEAD)

Six probe variants were run on current HEAD, all PASS:

1. `ProbeQStruct_ElementReadAfterMutation` - the exact Step 16 TC5 pattern:
   `QStructItem[] arr; s.v=11; arr[0]=s; s.v=999; r=arr[0]; if (r.v != 11)`.
   PASS. JIT shows `ldelem.any r2,r0,r9` then `ldfld.i4 r3, r2` - the dest
   (r2) is distinct from the source local (r1); the optimizer correctly tracks
   the ldelem dest, so the bnei.un reads the loaded copy (11), not the
   mutated source (999).
2. `ProbeQStruct_BranchAfterMutation` - high-register-pressure variant with
   live pad locals and a field mutation between comparisons. PASS.

### Why deferred

The Step 16 ship-log (archive/2026-07-04-implement-neo-step16/ship-log.md
quirk-struct-renumber) describes a `bnei.un` reading the mutated source slot,
but the exact register layout that triggered it is not pinned, and it does
not reproduce on current HEAD with the documented patterns. The BCP / copy-prop
/ temp-renumber paths are shared and high-blast-radius; shipping a fix without
a reproducing test would be a guessed change to a pass that affects every
method. **Action: track as a spec requirement; revisit only when a reproducing
pattern is found (e.g. via a future fuzz case or a real-world DLL).**

### Where to look when it resurfaces

`Optimizer.BCP.cs` `BackwardsCopyPropagation` (the `xSrc == yDst` renumber at
lines 97-141) and the copy-prop / temp-allocation interaction. The reported
symptom (a branch reading the mutated source slot) implies a Move whose
renumbered dest alias collided with a live source field slot.

---

## Q-LONG - long default-zero compare (conv.i8) (DEFERRED)

### Reproduction attempt (FAILED to reproduce on HEAD)

Three probe variants were run on current HEAD, all PASS:

1. `ProbeQLong_ConvI8ZeroCompare` - `long[] arr; arr[0]=7L; if (arr[0] == 0L)`
   and `if (arr[1] != 0L)`. PASS. JIT Final shows `ldelem.i8`,
   `ldc.i4.0; conv.i8 r10,r10`, `ceq r1,r9,r10` - the conv.i8 widens the int
   0 to a long in r10, and the compare reads both operands as long. Correct.
2. `ProbeQLong_ScalarZeroCompare` - scalar long locals with register pressure
   (`a=1;b=2;c=3;d=4;e=a+b+c+d; if (e==0L) ...`). PASS.
3. `ProbeQLong_DefaultFieldZeroCompare` - `default(QLongHolder).v == 0L` and
   `h.v = 42L; if (h.v == 0L)`. PASS.

### Why deferred

The Step 16 ship-log describes a `conv.i8 + long-compare` mis-evaluation
"worked around in TC2", but the conv readers (`ReadConvI8`,
ILIntepreter.Neo.cs:2663) and the I8 compare/branch arms
(Ceq_I8 / Cgt_Un_I8 / Bne_Un_I8 at ILIntepreter.Neo.cs:684-697, 1203-1216) all
read `*(long*)` correctly, and the JIT type-specialization
(`InferPrimTag` -> `GetTypedCompareOpcode` / `GetTypedBranchOpcode`,
JITCompiler.cs:632-661, 946-980) correctly widens to the `_I8` variants when
the operand register is `LongType`. No layout was found where the compare
mis-evaluates. **Action: track as a spec requirement; revisit when a
reproducing pattern is found.**

### Where to look when it resurfaces

- JIT type-specialization of the branch when the literal-`0L` operand's
  register tag is inferred (the `conv.i8` dest is seeded `LongType` at
  JITCompiler.cs:712 `GetConvResultType`, so the dependent branch should
  resolve to `_I8`; a case where the branch reads `Register1` whose type was
  NOT propagated from the conv dest is the suspect).
- A layout where a 4-byte int 0 and an 8-byte long share/overlap a temp slot
  (AllocateLocalStackSpaces sizing).

---

## Verification plan (implementer)

1. Build CLI `Debug_Neo` (0 errors) and TestCases `Debug` (0 errors).
2. K1 regression test FAILS before the FCP edit, PASSES after.
3. Full NeoStep smoke (was 72/72): 0 regressions, K1 probe now green.
4. Legacy-neutrality: the FCP edit is `#if ENABLE_NEO_MODE` + matches only
   `_Inline` opcodes Legacy never emits; confirm with a Legacy `Debug` CLI
   build NeoStep smoke (no behavior change) - or, at minimum, the code-read
   argument above (the Legacy control flow is byte-identical).
5. Q-STRUCT / Q-LONG probes stay green (they were already green; they become
   permanent non-asserting documentation cases so the patterns are watched).

## Non-goals

- No fix for Q-STRUCT / Q-LONG (not reproducible; tracked, deferred).
- No change to Legacy `ExecuteR`, BCP, copy-prop, or the JIT conv/compare
  lowering.
- No change to the heap `Stfld_*` / `Ldfld_*` families (reference-alias
  propagation for heap objects is correct and unchanged).
- Step 17+ items (ldelema, byref, CLR-VT param layout) are out of scope.
