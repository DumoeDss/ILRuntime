# Review Report — neo-vt-this-addr

**Reviewer:** adversarial, NON-AUTHOR (independent of the implementer).
**Date:** 2026-07-05.
**Branch:** `features/object-model-overhaul`.
**Smoke at review time:** 99/99 NeoStep green (91 baseline + 8 new TC8-TC15), reproduced.

---

## Overall verdict: **APPROVE WITH ONE MINOR FINDING**

The change is correct on every load-bearing axis I could construct an
adversarial probe for. The implementer's revised hypothesis (the propose-time
D1/D3 frame-native-Ref-Slot mechanism was partly wrong; the TRUE root cause was
the inline-stfld owner-type clobber, fix A) is **independently confirmed**.
Critically, the highest-blast-radius change (fix A) was verified to be a genuine
latent-bug fix, not a regression, by simulating pre-fix-A behavior and
observing a real NRE in a VT instance method.

One **Minor** latent defect found in the Ret-arm copy-back gate (0-prim-size VT
edge case). No Blocker, no Major.

---

## Mandatory probes — results

### Probe 1 — Fix-A blast radius (HIGHEST PRIORITY): VT instance METHOD, 3+ `this.field=` — **PASS (fix A is a genuine fix)**

Scratch test `NeoVtReview_P1_InstanceMethodMultiFieldWrite`: a VT
`NeoVtProbe4 { int a,b,c,d; }` with an **instance method** `SetAll(...)` that
writes all 4 fields via `this.field=`, then a read-back + `Sum()`. The owner
register (param slot 0 / `this`) is a FIXED register reused across all 4 writes
— the exact scenario fix A targets.

- **On HEAD (fix A present):** P1 PASSES. JIT dump shows all four
  `stfld.i4.inline` ops use the SAME owner `r9` and ALL are inline (the owner
  type survives across writes):
  ```
  8:stfld.i4.inline r9, r10, 0x00, NeoVtProbe4(0,0)
  9:stfld.i4.inline r9, r11, 0x04, NeoVtProbe4(4,0)
  10:stfld.i4.inline r9, r12, 0x08, NeoVtProbe4(8,0)
  11:stfld.i4.inline r9, r13, 0x0C, NeoVtProbe4(12,0)
  ```
- **With fix A reverted** (I temporarily patched `IsInlineLdfldDestSeedable` to
  `return true;` to simulate the pre-fix-A clobber, rebuilt only the CLI, ran
  the same probe): P1 **FAILS with `NullReferenceException`**. This is direct
  evidence fix A is a genuine latent-bug fix.
- **Existing Step 12 tests unaffected:** with the same simulated clobber,
  `NeoStep12Test` (6/6) STILL passes. Reason: the Step 12 tests write fields on
  a **local** `v` (`v.x=1f; v.y=2f; v.z=10f;`), whose C# lowering re-emits a
  fresh `ldloca v` before each stfld; the `Ldloca` dest-typing rule
  (line 741-747) re-seeds the owner's VT type per access, overwriting any
  clobber. The clobber only bites when the owner is a `this` PARAM (no
  intervening ldloca) — which the existing Step 12 tests did not exercise but
  VT instance methods and multi-field ctors do.

**Conclusion:** fix A fixes a real latent bug (broke VT instance methods with
2+ `this.field=` writes and multi-field VT ctors), is NOT a regression, and
does not change Step 12 local-field-write behavior. Confirmed independently.

### Probe 2 — Register reuse / silent corruption (Step 17 B1 class) — **PASS**

Scratch `NeoVtReview_P2_TwoNewobjDestsPlusReuse`: TWO simultaneous VT newobj
dests (`s1`, `s2` of `NeoVtProbe4`) interleaved with `new NeoVtProbeWrapper`,
`new int[3]`, another `new NeoVtProbeWrapper`, array writes — then field reads
of BOTH VT dests AND the array. PASS — no stale values. Higher-pressure than
TC9 (which has a single newobj dest).

### Probe 3 — Copy-back Ret-arm ordering, nested calls, ref field — **PASS**

Scratch `NeoVtReview_P3_RefFieldTwoNewobjCopyBack`: THREE ref-field VTs
(`NeoVtProbeRef { int n; string s; }`) constructed in the same frame; each
ctor sets a ref field; caller reads all three refs. Also exercises TC11's
forced-real-newobj factory (`NeoStep18_MakeRef2`). All pass — no stale/double-
free of the ref slot, no aliasing window. The Ret-arm ordering (copy-back
BEFORE the `RemoveRange` pop) is correct: traced that the ctor's slot-0 ref
entries live at `ctorFrameRefBase + thisRefOff` and survive nested calls (each
nested call reserves/pops its OWN frameRefBase on top, never the ctor's).

### Probe 4 — `ExecuteNeo` signature change (4 new optional params) — **PASS**

Grepped every caller of `ExecuteNeo`:
- `ILIntepreter.Neo.cs:251` (`InvokeNeoCallTarget` helper) — 5 args, uses
  defaults (`vtNewobjCallerDst=null`).
- `ILIntepreter.Neo.cs:1725` (new VT newobj branch) — all 8 args.
- `ILIntepreter.cs:112` (top-level entry) — 5 args, uses defaults.

Defaults are `null` / `-1` / `0` / `0`. The Ret-arm gate
`vtNewobjCallerDst != null && vtNewobjCallerPrimSize > 0` is false for the two
defaulting callers, so the copy-back is skipped and the non-newobj path is
byte-identical to before. No caller breaks.

### Probe 5 — TC10 adaptation honesty (`Stfld_Value` deferred) — **VERIFIED, not a regression**

`Stfld_Value` / `Ldfld_Value` (whole-VT-into-VT-field copy) has NO
`case OpCodeREnum.Stfld_Value:` in `ExecuteNeo` — it falls through to the
catch-all Step-6 NIE (`ILIntepreter.Neo.cs:3076`). TC10 was adapted to
construct the nested VT directly rather than store it into an Outer field.
`Stfld_Value` is a genuine separate Step 12b deferred item; the adaptation is
honest (not hiding a bug this change claims to fix).

### Probe 6 — Default-ctor + base-ctor chain — **PASS (within C# 8.0 limits)**

- Default/zero-init ctor: TC3 (`new NeoStep18Default()` — no explicit ctor,
  Initobj semantics) and TC15 (partial init) pass; runtime handles `pCnt=0`
  correctly (copy-back unconditional when `vtNewobjCallerDst != null`).
- Base-ctor chain: C# 8.0 disallows struct `: base()` (CS0522) and
  parameterless struct ctors (CS8400), so the "base-ctor chain" intent is moot
  for IL structs (they derive from `System.ValueType`, no field-setting base
  ctor). TC14 was repurposed to a complex ctor with a static helper call + 2
  fields; PASS. The implementer documented this constraint honestly.

### Probe 7 — Legacy-neutral / shared pass — **PASS**

All changes are in `#if ENABLE_NEO_MODE`-gated functions
(`GatherValueTypes` @161, `TypeSpecializeNeoOpcodes` @533) or the Neo-only
file `ILIntepreter.Neo.cs`. Plain `Debug` build (Legacy) compiles clean
(0 errors). No FCP/BCP/copy-prop/RegisterCleanup change. The 7 pre-existing
Legacy NeoStep-filter failures (NeoStep15_TC6, NeoNaNR8, ...) are unrelated
(present without this change).

---

## Findings

### Minor — F-1: Ret-arm copy-back gate drops ref-half for 0-prim-size VTs

- **File:** `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:1883`.
- **Code:**
  ```csharp
  if (vtNewobjCallerDst != null && vtNewobjCallerPrimSize > 0)
  {
      ...
      if (vtNewobjCallerPrimSize > 0)
          Unsafe.CopyBlock(vtNewobjCallerDst, frameBase + thisPrimOffR, (uint)vtNewobjCallerPrimSize);
      for (int i = 0; i < vtNewobjCallerRefCount; i++)
          mStack[vtNewobjCallerDstRefBase + i] = mStack[frameRefBase + thisRefOffR + i];
  }
  ```
- **Problem:** The ref-half copy-back loop is gated by
  `vtNewobjCallerPrimSize > 0`. A value type with `TotalPrimitiveSize == 0`
  (a struct containing ONLY reference fields, e.g. `struct { string s1; string s2; }`)
  has `vtNewobjCallerPrimSize == 0`, so the ENTIRE block (including the ref
  copy-back) is skipped. If such a VT is constructed via the NON-inlined
  runtime Newobj branch, the ctor's ref-field writes are silently lost — the
  caller reads `null`. (The pre-call prim seeding at line 1709-1710 has the
  same `if (vtPrimSize > 0)` shape, but that one is harmless — there's nothing
  to copy when prim size is 0.)
- **Probe that exposed it:** Architectural reasoning + the JIT dump showing
  `NeoVtProbeRefOnly(0,0)/(0,1)` layout (prim-offset 0, ref-offsets 0/1 →
  `TotalPrimitiveSize == 0`). I could not construct a clean end-to-end
  reproducer because (a) the inliner inlines straight-line ref-only ctors in
  practice (P5 and a 5-string-field P7 both inlined → no runtime branch → no
  copy-back), and (b) the factory-return path that would force a real newobj
  hits a SEPARATE pre-existing NIE at `ILIntepreter.Neo.cs:1862`
  (`isSingleReferenceReturn` — returning a multi-ref VT is not supported).
  So the defect is currently latent / unreachable through user code, but the
  gate is logically wrong and will bite the moment either the inliner budget
  shrinks or the return-NIE is lifted.
- **Severity rationale (Minor):** Not Blocker/Major because (1) it is not
  reachable through any current user code path (inliner + return-NIE shield
  it), (2) ref-only structs are uncommon, and (3) the 99/99 smoke is
  unaffected. It is a latent correctness defect in the NEW code this change
  adds, not a pre-existing issue.
- **Suggested fix:** Decouple the ref-half gate from the prim size:
  ```csharp
  if (vtNewobjCallerDst != null)
  {
      var ctorFrameR = method.CompiledFrame;
      var thisSlotR = ctorFrameR.ParamInfos[0];
      int thisPrimOffR = thisSlotR.Offset;
      int thisRefOffR = thisSlotR.RefOffset;
      if (vtNewobjCallerPrimSize > 0)
          Unsafe.CopyBlock(vtNewobjCallerDst, frameBase + thisPrimOffR, (uint)vtNewobjCallerPrimSize);
      for (int i = 0; i < vtNewobjCallerRefCount; i++)
          mStack[vtNewobjCallerDstRefBase + i] = mStack[frameRefBase + thisRefOffR + i];
  }
  ```
  i.e. gate the block on `vtNewobjCallerDst != null` alone (the strongest
  signal that this ExecuteNeo invocation is a VT-ctor newobj), and keep the
  inner prim copy-block conditioned on `vtNewobjCallerPrimSize > 0`.

### Trivial — T-1: Redundant inner guard

- `ILIntepreter.Neo.cs:1889` — `if (vtNewobjCallerPrimSize > 0)` inside the
  block that is already gated by `vtNewobjCallerPrimSize > 0` (line 1883).
  Dead branch (always true). Will be removed by the F-1 fix above.

---

## Confirmed sound (no action)

- **Fix A (`IsInlineLdfldDestSeedable`):** sound; genuine latent-bug fix;
  independently verified via pre-fix-A simulation (probe 1).
- **D1 (`case Newobj:` dest-typing):** sound; mirrors `Ldloca`/`Ldflda` rules;
  needed for the non-inlined factory path (TC12, P4).
- **Fix C (`GatherValueTypes` `case Newobj:`):** sound; without it a >8-byte VT
  newobj dest temp is undersized and the subsequent `Move` truncates
  (silent corruption). The `NeoStep18Big` (3 ints = 12 bytes) and
  `NeoVtProbe4` (4 ints = 16 bytes) probes exercise this.
- **D2 copy-back runtime:** sound for the prim-and-ref / prim-only / ref-and-
  prim cases (probes 1, 2, 3, 4 + TC8-TC15). The Ret-arm BEFORE-pop ordering is
  correct (the `RemoveRange` at line 1894 destroys the slot-0 ref entries; the
  copy-back at 1883-1893 runs first).
- **D3 (addrAlias VT-`this` root):** correctly NOT implemented; the
  `ResolveLiveAlias` fallback handles direct `this.field=` (the common case).
  Confirmed via the P1 JIT dump (stfld.i4.inline resolves the owner directly).
- **ExecuteNeo signature:** no caller breaks (probe 4).
- **Legacy-neutral:** plain Debug compiles clean; no shared pass touched.

---

## Smoke / build evidence

- `dotnet build ILRuntimeTestCLI -c Debug_Neo`: 0 errors.
- `dotnet build TestCases -c Debug`: 0 errors.
- `dotnet build ILRuntimeTestCLI -c Debug` (Legacy): 0 errors.
- NeoStep smoke: `Ran 99 tests, 0 failed` (reproduced).
- Probe filter `NeoVtReview` (4 independent probes): `Ran 4 tests, 0 failed`.
- Probe `NeoVtReview_P1` with fix A reverted: `Ran 1 tests, 1 failed`
  (`NullReferenceException`) — confirms fix A is load-bearing.

## Probe artifacts

Scratch probes were authored in
`TestCases/NeoVtThisAddrReviewProbe.cs` (P1-P7), built, run, and **removed
before writing this report** (the working tree matches the implementer's diff
exactly; `git diff HEAD --stat` shows only the three intended files).

---

## Re-review round 1 (F-1 delta)

**Reviewer:** adversarial, NON-AUTHOR, NON-FIXER (fresh reviewer; the LEAD
applied the F-1 fix, so this round satisfies the non-author confirmation rule).
**Date:** 2026-07-05.

### F-1 RESOLVED: **YES**

The gate fix is exactly as described in the round-1 finding and the suggested
fix. Verified at `ILIntepreter.Neo.cs:1883`:

```csharp
if (vtNewobjCallerDst != null)              // outer gate: runs for any VT newobj
{
    var ctorFrameR = method.CompiledFrame;
    var thisSlotR = ctorFrameR.ParamInfos[0];
    int thisPrimOffR = thisSlotR.Offset;
    int thisRefOffR = thisSlotR.RefOffset;
    if (vtNewobjCallerPrimSize > 0)          // inner prim guard: RETAINED (T-1)
        Unsafe.CopyBlock(vtNewobjCallerDst, frameBase + thisPrimOffR, (uint)vtNewobjCallerPrimSize);
    for (int i = 0; i < vtNewobjCallerRefCount; i++)   // ref copy: unconditional
        mStack[vtNewobjCallerDstRefBase + i] = mStack[frameRefBase + thisRefOffR + i];
}
```

The outer block now gates on `vtNewobjCallerDst != null` alone (the strongest
signal that this ExecuteNeo invocation is a VT-ctor newobj), the inner
`if (vtNewobjCallerPrimSize > 0)` prim-copy guard is intact, and the ref-copy
loop runs unconditionally inside the block. A 0-prim-size VT (ref-only struct)
now skips the prim `CopyBlock` but still runs the ref-copy loop. Decoupled
correctly.

### F-1 adversarial probe outcome: **LATENT (confirmed), documented precisely**

The probe `NeoStep18_TC16_RefOnlyVtNewobj` was constructed against
`struct NeoStep18RefOnly { string a; string b; }` (TotalPrimitiveSize == 0),
ctor sets both ref fields, caller reads them back. Findings:

1. **The F-1 path (runtime Newobj IL-VT branch -> Ret-arm copy-back) is NOT
   reached by this probe.** The JIT dump for the TC16 caller shows the ctor is
   fully inlined (`inlinestart`/`inlineend` markers, `stfld.ref.inline`),
   preceded by `initobj` — there is NO `newobj` opcode emitted for the
   ref-only-VT local form. So the Ret-arm copy-back (the code F-1 fixes) is
   never entered.
2. **The factory-return path that WOULD force a real newobj is blocked by the
   separate pre-existing return-NIE** at `ILIntepreter.Neo.cs:1861-1862`
   (`isSingleReferenceReturn` rejects a multi-ref / 0-prim return). A factory
   returning `NeoStep18RefOnly` has `returnPrimitiveSize == 0,
   returnRefCount == 2`, which trips the NIE before any construction copy-back.
3. **I exonerated the F-1 fix by reverting it.** With the buggy gate
   (`!= null && primSize > 0`) rebuilt into the CLI, TC16 fails IDENTICALLY
   (same `DivideByZeroException` assertion) and TC11 (ref-field VT,
   `primSize == 4 > 0`) still PASSES — because TC11's primSize>0 means the
   buggy gate lets the copy-back run anyway. This proves (a) the F-1 edge
   (primSize == 0 in the Ret-arm) is genuinely unreachable today, and (b) the
   F-1 fix is not the cause of, nor does it affect, the TC16 failure.

**Why TC16 is NOT shipped as a passing test:** When I added TC16 to
`NeoStep18Test.cs`, it FAILED the smoke (`Ran 100 tests, 1 failed`). The
failure is in the **inlined** ref-only-VT path (`stfld.ref.inline` writes the
dest, the subsequent `ldfld.ref` reads back a value that does not equal the
stored string) — a SEPARATE latent inliner bug specific to ref-only VTs
(prim-size 0), NOT in the F-1 Ret-arm domain. Shipping a known-failing test
would break the ≥99-green smoke gate and provide no guarding value for F-1
(whose path is unreachable). I therefore did NOT add TC16 to the suite; the
F-1 regression guard remains the architectural reasoning locked into this
report plus TC11 (which exercises the Ret-arm ref-copy for the primSize>0
case, the only currently-reachable shape).

**New Minor finding (F-2), pre-existing, OUT OF SCOPE for this change:**
the Neo inliner mis-compiles a ref-only VT (TotalPrimitiveSize == 0) local
`new S(refArgs)`: the inlined `stfld.ref.inline` writes appear not to survive
to the following in-frame `ldfld.ref` read. This is in `JITCompiler.cs` (the
inliner / field-access lowering), which this change does NOT touch — confirmed
pre-existing. It is only observable because my F-1 probe happened to construct
the first ref-only VT in the test suite. Recommendation: file as a deferred
item (e.g. `[INLINER-REFONLY-VT]`); it is unrelated to the VT-THIS-ADDR change
under review and does not block it.

### Smoke / build evidence (re-review round 1)

- `dotnet build ILRuntimeTestCLI -c Debug_Neo`: 0 errors.
- `dotnet build TestCases -c Debug`: 0 errors.
- `dotnet build ILRuntimeTestCLI -c Debug` (Legacy): 0 errors.
- NeoStep smoke: **`Ran 99 tests, 0 failed`** (reproduced, with F-1 fix in
  place and TC1-TC15 restored). TC16 not added (would regress smoke for an
  out-of-scope inliner bug; see F-2).

### T-1 disposition: **CONFIRMED (correctly retained, no longer redundant)**

The inner `if (vtNewobjCallerPrimSize > 0)` guard at line 1889 is NOT
redundant after the F-1 fix — the outer gate (`vtNewobjCallerDst != null`) no
longer implies `primSize > 0`, so the inner guard is now load-bearing (it
prevents a 0-length `Unsafe.CopyBlock` when `primSize == 0`). Correct to keep.
T-1 closed.

### Legacy-neutral: **CONFIRMED**

The F-1 fix is entirely inside `ILIntepreter.Neo.cs` (the Neo-only
interpreter file). Plain `Debug` build (Legacy) compiles clean (0 errors). No
shared pass, FCP/BCP/copy-prop, or RegisterCleanup change.

### Overall verdict: **APPROVE**

F-1 is correctly resolved (gate decoupled, inner prim guard retained, T-1
closed). The F-1 edge is confirmed latent (inliner + return-NIE shield it),
exactly as round 1 documented; the fix is a correct forward-looking
correctness improvement with no observable behavior change today. Smoke
99/99 green, Legacy-neutral. The new F-2 (ref-only-VT inliner bug) is
pre-existing, in untouched code, out of scope, and does not block this
change — recommend filing as a separate deferred item.

### Note on NeoStep18Test.cs

The round-1 review's TC8-TC15 additions to `NeoStep18Test.cs` were living in
the working tree uncommitted (HEAD only had TC1-TC7). They were accidentally
clobbered during this re-review (a `git checkout` to test the buggy gate) and
**fully restored from the reviewer's captured context** (15 TCs, all
`NeoStep18*` probe types, byte-identical comments, 0 CJK corruption). The
restored file builds clean and the 99/99 smoke includes TC8-TC15. The working
tree is left with: `ILIntepreter.Neo.cs` (F-1 fix), `JITCompiler.cs`
(implementer's original change), `NeoStep18Test.cs` (TC1-TC15 restored) — no
scratch.

