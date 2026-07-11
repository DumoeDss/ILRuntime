# Review Report — implement-neo-step17 (Neo Step 17: byref / 8-byte Ref Slot)

Reviewer: REVIEWER agent (adversarial, author != verifier)
Date: 2026-07-04
Branch: features/object-model-overhaul (change is UNCOMMITTED working-tree edits
on top of HEAD = `e3fa8ef2`)
Smoke at review time: NeoStep 79/0 (Debug_Neo CLI); Legacy NeoStep12 10/0.

## VERDICT: BLOCKER — one silent-corruption defect in the addrAlias gate

I reproduced silent data corruption with a focused adversarial probe. The 79/0
smoke does NOT currently trigger it (no shipped test exercises the failing
register-reuse pattern), but the pattern is a common C# idiom (a struct field
write followed by `ref` to an unrelated local). This is a latent correctness
hole, not a cosmetic gap.

| Severity | Count |
|----------|-------|
| Blocker  | 1     |
| Major    | 0     |
| Minor    | 4     |

---

## Blocker

### B1 — addrAlias gate UNDER-GATES: reused register kept folded while a later
live range escapes through stind/ldind/byref-call -> silent corruption
**File:** `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs`
(the Step-17 consumer-scan gate, ~lines 92-380 inside `LowerNeoOffsets`)

**What / why:**

The mixed-reuse guard keys `CanEvict(r)` off a GLOBAL `hasFoldableUse` set that
spans the entire method. `hasFoldableUse[r]` is set whenever ANY foldable
consumer (`_Inline` field op / `Initobj` / foldable `Ldflda`) reads `r` while
`r` is a live alias, and it is NEVER reset. The liveness walk (step 4) only
toggles `liveAliases` when an address producer redefines a register; it does
NOT clear `hasFoldableUse[dest]`.

Consequence: if register R is reused across two live ranges —
  (range 1) R is a folded in-frame-VT address consumed by `Stfld_*_Inline`
            (sets `hasFoldableUse[R]`),
  (range 2) R is REDEFINED by a new `ldloca R, otherLocal` and consumed by a
            byref escape (`stind` / byref `Call` param),
then `CanEvict(R)` is false (range-1 foldable use persists), so R is NOT
evicted from `addrAlias`. Range 2's `ldloca` is therefore treated as folded-
and-dead at runtime (the runtime `Ldloca` arm is suppressed because R is still
in the alias map), so R is never written with a Ref Slot. The range-2 escape
consumer then reads R expecting an 8-byte Ref Slot and gets junk (a stale
folded frame offset from range 1) -> the store/escape lands on the wrong
memory.

This is exactly the "escaped address WRONGLY kept folded -> silent corruption"
case the review brief asked me to hunt for. The guard's stated premise
("mixed-reuse registers stay folded; their escape consumer reverts to
pre-Step-17 junk/NIE behavior") is UNSOUND for reuse where the escape live
range is a DISTINCT redefinition consumed by a Step-17-IMPLEMENTED opcode
(stind/ldind/stobj/ldobj/ldelema/byref-call): that opcode reads the register
as a real Ref Slot, and "reverts to pre-Step-17 behavior" is wrong because
those opcodes did not exist (as working arms) pre-Step-17.

**Reproduction (I ran this; reverted after):**

Added to `TestCases/NeoStep17Test.cs`:

```csharp
static void ProbeSetX(ref int fx, int v) { fx = v; }
public struct ProbePoint { public int x; public int y; }

public static void NeoStep17Probe_B_MixedFoldThenEscape()
{
    ProbePoint p;
    p.x = 7;                 // stfld inline -> hasFoldableUse[r6]
    p.y = 8;
    int x = 0;
    ProbeSetX(ref x, 99);    // ref to an UNRELATED local -> reuses r6, escapes
    if (p.x != 7 || p.y != 8 || x != 99)   // FAILS: divide-by-zero
    { int z = 1; int d = 0; int _ = z / d; }
}
```

Result: `NeoStep17Probe_B_MixedFoldThenEscape` FAILS (DivideByZero at the
assertion), i.e. one of `p.x`/`p.y`/`x` is wrong. JIT output shows the bug:

```
1:ldloca.s r6, r0           ; range 1: ProbePoint addr (folded)
3:stfld.i4.inline r6, r7, 0 ; foldable -> hasFoldableUse[r6]=true
4:ldloca.s r6, r0
6:stfld.i4.inline r6, r7, 4
7:ldc.i4.0 r1               ; int x
8:ldloca.s r6, r1           ; range 2: REDEFINES r6 for local x, escapes below
9:ldc.i4.s r7,99
10:stind.i4 r6, r7          ; reads r6 as Ref Slot -- but r6 stayed FOLDED
```

Instruction 8's `ldloca` is suppressed (r6 still in `addrAlias`), so stind @10
writes 99 to the stale folded offset of `p` instead of `x` -> `x` stays 0 (or
`p` clobbered) -> assertion fires. (The probe was removed; TestCases.csproj
uses default glob, so re-adding reproduces deterministically.)

A symmetric probe C (escape FIRST, then foldable field access) PASSES —
confirming the bug is specifically the "foldable use earlier in the method
poisons a later escape live range of the same register" direction.

**Why the 79/0 smoke does not catch it:** TC1-TC7 use DEDICATED address
registers (no reuse mixes a folded VT address with a byref escape in the same
register). TC6 (`ref p.x`) is green only because the C# compiler happens to
emit the `ref p.x` address on a register SEPARATE from the `p.x=1; p.y=2`
inline accesses. There is no shipped test that forces the reuse, so the suite
is green despite the hole. This is fragile: any future test (or any real
hotfix code) with the pattern trips it.

**Fix (recommended direction; LEAD/implementer to finalize):**

Make eviction per-LIVE-RANGE, not global. When step 4 establishes a NEW live
range for an address-producer dest (an `ldloca`/`ldflda` whose dest re-enters
`liveAliases`), RESET that dest's bookkeeping: clear `hasFoldableUse[dest]`
for the new range, and re-evaluate foldable/escape uses only within the new
range. Equivalently, scope `hasFoldableUse` to live ranges: a register is
evictable for an escape in range N iff range N (not the whole method) has no
foldable consumer. The minimal change is to clear `hasFoldableUse[dest]`
when an address producer redefines `dest` into a fresh live range, AND to
record the escape decision per-range so a range-2 escape evicts r6 even though
range-1 used it folded.

Concretely the chain taint / BFS section also needs the per-range view: a
register evicted in range 2 must have its alias removed even if it carried a
foldable use in range 1. (The `hasFoldableUse` set as a global veto is the
wrong granularity.)

After the fix, re-run probe B to green, AND add it (or an equivalent) as a
permanent regression test (NeoStep17Test TC8) so the hole can't reopen.

**Severity rationale:** Silent memory corruption on a common idiom. Not a
crash — wrong results. The whole point of the addrAlias gate review was to
catch exactly this. BLOCKER.

---

## Minor

### M1 — Deferred CLR-object stind/ldind surfaces as `InvalidCastException`,
not a Step-17-tagged NIE
**File:** `ILIntepreter.Neo.cs`, `GetNeoILInstance` (~line 2921) reached by
the `stind_*`/`ldind_*`/`stobj`/`ldobj` `objectIndex >= 0` arms.

Spec requirement "Deferred byref sub-cases throw tagged NIE" says a CLR-object
stind/ldind SHALL throw a `NotImplementedException` tagged `Step 17`/`Step 13b`.
Instead, when `mStack[objIdx]` is a CLR object, `GetNeoILInstance` throws
`InvalidCastException` (message "Unable to cast object..."). It IS loud (no
silent mis-handling) and the spec's deferred-list names this exact sub-case
for Step 13b, so it is defensible — but the message is not Step-tagged, which
mildly violates the spec's "tagged NIE" wording. Suggest wrapping the
`GetNeoILInstance` call in the stind/ldind/stobj/ldobj arms (or adding a CLR
type check) to throw the tagged NIE explicitly. Low severity.

### M2 — `Stind_Ref` / `Ldind_Ref` heap-IL ref-field case is a NIE, but no
test exercises either direction of the ref-store path
**File:** `ILIntepreter.Neo.cs`, `Stind_Ref`/`Ldind_Ref` arms.

The frame-native `Stind_Ref`/`Ldind_Ref` path is implemented and is on the
happy path of TC1-TC4 (byref to a frame local holding a ref? — actually TC1-4
are int byrefs, so Stind_Ref is NOT exercised by any shipped test either).
The heap-IL ref-field sub-case NIEs (documented). Net: neither `Stind_Ref`
nor `Ldind_Ref` is covered by a green test this step. Acceptable scope per
the design (ref-typed byrefs are rare), but worth a tracked follow-up test.

### M3 — `Stobj`/`Ldobj` copy primitives only (no ref-slot loop); spec says
copy `TotalReferenceCount` ref slots too
**File:** `ILIntepreter.Neo.cs`, `Stobj`/`Ldobj` arms.

Spec requirement "stind/ldind/stobj/ldobj dispatch" says stobj/ldobj SHALL
copy `TotalPrimitiveSize` bytes AND `TotalReferenceCount` ref slots. The
shipped arms copy primitives only (`Unsafe.CopyBlock` of `primSize`); the
ref-slot portion of a VT is not copied. This is a PARTIAL implementation vs
the spec's literal requirement. Implementer finding 9.7 documents it as
"green target is primitive-field VTs." No shipped test needs the ref-slot
copy (NeoStep17Point / NeoStep17Named have only primitive fields), so the
suite is green. Severity Minor only because the spec text is stricter than
the code; either narrow the spec wording to "primitive-only this step" or
land the ref-slot loop. Track as a follow-up.

### M4 — `Constrained` arm NIE message is honest but the spec mandates a
working VT-box arm; this is an acceptable deferral but the spec requirement
"D-CONSTRAINED" is unmet
**File:** `ILIntepreter.Neo.cs`, `Constrained` arm.

The arm throws a Step-17/13b-tagged NIE. The blocker (callvirt byref-this
not supported) is real (verified: callvirt reads `this` as an mStack object
index and would NIE on a byref `this`). The NIE is loud/honest, not silent.
Acceptable deferral per design sec 6 / 9 — but the spec capability's
"D-CONSTRAINED" requirement is NOT satisfied by this change. Either move the
D-CONSTRAINED requirement to a future change's scope or accept the deferral
explicitly in the change's tasks/proposal. The implementer already removed
TC8 and documents this; just ensure the spec delta reflects that
D-CONSTRAINED is deferred, not delivered. (Spec currently lists it as an
ADDED requirement that SHALL be implemented — minor spec/implementation
drift.)

---

## Explicit verdicts requested by the brief

### (a) addrAlias gate — UNDER-GATE silent-corruption risk: **HOLE FOUND (B1).**
I reproduced silent corruption. The gate over-gates (keeps an address folded)
when a register is reused for a folded VT-field access in one live range and a
byref escape in a later live range, because `hasFoldableUse` is global rather
than per-live-range. The escape consumer (stind/byref-call) then reads the
register as a Ref Slot and gets a stale folded offset. This is BLOCKER B1.
The over-gating-NO (regressing Steps 12-16) is NOT a problem — 79/0 confirms
no green case lost; the problem is the under-gating-YES (corruption on a
plausible idiom). Note the smoke being green does NOT prove the gate correct
here — it only proves no shipped test exercises the failing pattern.

### (b) byref call-ABI correctness (caller sees the mutation): **CORRECT as
implemented.** `AllocateSlotForType` and `AllocateNeoCallParamSlot` both add
the `IsByRef` FIRST branch (Size=8, RefCount=0, align 4 / contiguous). The
existing primitive-copy loop emits one 8-byte entry = the Ref Slot VALUE (not
the referent). The callee `ldarg` is an 8-byte Move; stind/stobj on the
callee's byref temp dispatch back to the caller's frame/object. TC1-TC5 prove
mutation propagates (ref frame local, accumulate, out-param, byref-forwarded,
ref heap field). The `out` path is identical to `ref` (verified TC3 green).
Subject only to B1 (a byref Call param is itself an escape that the gate must
catch — and the gate DOES scan Call params for IsByRef, correctly; B1 is about
the *register reuse* interaction, not the call scan).

### (c) K1-extension Legacy-neutrality: **CONFIRMED.** Both FCP kill-sites
(lines 168-206 and 340-370) are inside `#if ENABLE_NEO_MODE`/`#endif`. Plain-
`Debug` CLI builds clean; Legacy NeoStep12 filter = 10/0. The extension adds
`Ldarga`/`Ldarga_S`/`Ldflda`/`Ldelema` to the kill (plus Ldelema's `ySrc2`
array register) — all Neo-only, compiles out in plain-Debug. No over-kill
observed in the smoke (the kill is conservative: it only suppresses a copy-
prop when the address of the prop source/dest is taken, which is always
safe).

### (d) constrained. deferral: **ACCEPTABLE, but spec drift (M4).** The
blocker (callvirt byref-this) is real; the NIE is loud/tagged. Deferral is
fine. Drift: the spec lists D-CONSTRAINED as a delivered ADDED requirement
but the code NIEs it. Reconcile the spec wording.

### Partial items acceptability:
- Stobj/Ldobj primitives-only (M3): acceptable scope for this step (no test
  needs ref-slot copy) but stricter than the spec text — track follow-up.
- Stind_Ref/Ldind_Ref heap-IL ref-field NIE (M2): acceptable scope; neither
  ref-store direction is test-covered this step — track follow-up test.
- CLR-object stind/ldind via field hash (M1): deferred to Step 13b, loud, but
  surfaces as InvalidCastException not a tagged NIE — minor.

---

## Things verified CORRECT (no finding)

- Legacy `ILIntepreter.Register.cs` is UNTOUCHED (git diff vs HEAD empty).
- `Ldloca`/`Ldloca_S`/`Ldarga`/`Ldarga_S` produce `(-1, absFrameOffset)` —
  matches spec; `ip->SrcOffset` is the source local's frame byte offset
  (lowered from R2). Confirmed against the optimizer's R1/R2 lowering.
- `Ldflda` runtime dispatch on the operand's objectIndex half (deviation from
  design's Operand4 marker, per implementer finding 9.3) is self-describing
  and sound: -1 -> in-frame VT (reads offset half as VT base + fieldPrimOff);
  >=0 -> heap IL (mStack idx, fieldPrimOff). The folded case leaves the dest
  dead. Consistent.
- `stind_*`/`ldind_*` read the Ref Slot from `DstOffset+0/+4` (Stind) /
  `SrcOffset+0/+4` (Ldind) — consistent with the optimizer's address/value
  lowering (Stind: DstOffset=addr, SrcOffset=value; Ldind: DstOffset=dest,
  SrcOffset=addr). objectIndex==-1 -> frame-native; >=0 -> pinned Primitives
  via `Unsafe.ReadUnaligned`/`WriteUnaligned` (design option (b), acceptable).
- The 8-byte Ref Slot is read/written as two `int`s at +0/+4 consistently
  across all producers and consumers (Ldloca, Ldflda, Ldelema, Stind, Ldind).
  No endianness/offset inconsistency.
- `Ldelema`: R1=dest, R2=array, R3=index (JIT emission confirmed);
  optimizer stamps `Operand4 = localInfos[r3].Offset`; runtime reads
  `*(int*)(frameBase + ip->Operand4)` for the index. IL-VT-array path parks
  the element ILTypeInstance on mStack and encodes (idx, 0); CLR-primitive-
  array NIEs (documented). Matches finding 9.4 (R2-not-R1 for array).
- Chain taint (ldloca->ldflda) BFS both directions to fixed point — correct
  in principle; the taint is only undermined by the global `hasFoldableUse`
  veto (B1).
- NeoStep smoke 79/0 re-confirmed at review time (Debug_Neo). Steps 12-16
  preserved. Max per-test time well under the infinite-loop threshold.
- Both Debug_Neo CLI and Debug TestCases build with 0 errors.

---

## Recommended next step (for LEAD)

1. Fix B1 (per-live-range `hasFoldableUse` reset on address-producer
   redefinition; re-evaluate eviction per range). Add probe B as permanent
   regression test NeoStep17Test TC8.
2. Re-run full NeoStep smoke + the new regression test to green.
3. (Optional, low priority) address M1-M4: tagged NIE for CLR-object stind
   (M1), narrow spec text for Stobj/Ldobj ref-slots (M3) and D-CONSTRAINED
   (M4), add a Stind_Ref/Ldind_Ref test (M2).

Do NOT ship until B1 is fixed — it is silent corruption on a common idiom.
