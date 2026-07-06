## Context

Step 17 shipped the unified 8-byte Ref Slot `(objectIndex, offset)` model for the
Neo register VM, plus the primitives-only `Stobj`/`Ldobj` consumer arms and the
`constrained.`-on-VT dispatch. The `neo-step17-completion` cohort (2026-07-05)
landed the `{a,d,M2}` scope: full `constrained.callvirt T.M` dispatch on a value
type, the CLR primitive-array `ldelema` remainder, and the F-5 boxed-source NIE
guard. It explicitly DEFERRED three things to this child (`neo-step17-stobj-
refloop`):

- **(b)** the `Stobj`/`Ldobj` ref-slot loop (a value type WITH reference fields
  copied through a byref);
- **(c)** generic-byref / `fixed` / interface-on-VT-constrained;
- **the IL-VT-with-ref-fields constrained sub-case** (a constrained call on an
  IL struct that has reference fields -- the byref `this` does not carry the
  struct's ref-region mStack base).

Current state at HEAD (`4d9e26f1`), code-grounded:

- The `Stobj` arm (`ILIntepreter.Neo.cs:3514-3543`) and `Ldobj` arm (`:3544-
  3570`) compute `primSize` (`ilType.TotalPrimitiveSize` or
  `AppDomain.GetPrimitiveSize(t)`) and copy ONLY those bytes via
  `Unsafe.CopyBlock`. The `TotalReferenceCount` ref-region half is NOT copied.
  So `stobj` of a struct `{int x; string s;}` truncates `s` (dest ref slot
  untouched -- silent stale/null); `ldobj` reads the dest's stale ref slot.
  Three byref target shapes are handled for the primitive half:
  `objectIndex == -1` (frame-native), `NeoIsClrObject` (4d CLR-object field via
  `NeoReadClrObjectField`/`NeoWriteClrObjectField`), and the else (IL
  ILTypeInstance `Primitives`).
- The Constrained arm IL-VT-direct-call path (`:3807-3833`) throws a tagged NIE
  when `ilConstrained.TotalReferenceCount > 0` (`:3818`), with the comment that
  the byref source does not carry the ref-region mStack base. The IL-VT-
  inherited-CLRMethod box path (`:3842-3890`) throws the symmetric NIE at
  `:3876` and its `CopyFrameToIL` call passes `refCount=0` (`:3885`).

Constraints (load-bearing, from the portfolio memory):

- **Legacy is the REFERENCE, not a target.** `ExecuteR` (`ILIntepreter.Register.cs`)
  has the mature `Stobj`/`Ldobj` + `Constrained` arms via the `ObjectTypes`
  discriminator. Read for SEMANTICS; never modify Legacy to make Neo work.
- **Reuse shipped machinery.** Step 17 Ref Slot `(objectIndex, offset)`; the
  step17-completion Constrained arm + the F-1 IL-VT-inherited-method box
  (`Instantiate(false)` + `CopyFrameToIL` + the ref-region); `Move_Vt`'s byte +
  ref copy loop; `CopyFrameToIL`/`CopyILToFrame` (which already iterate
  `ManagedObjects`); the area4 4d `NeoReadClrObjectField`/`NeoWriteClrObjectField`
  (unchanged for the CLR-object sub-case).
- **The `OpCodeR` `[StructLayout(LayoutKind.Explicit)]` union gotcha** (handoff
  §4): `Register1`/`DstOffset` alias @4, etc.; `LowerNeoOffsets` overwrites
  register indices with byte offsets. A pass needing a register INDEX must run
  BEFORE `LowerNeoOffsets`. `Operand2`/`Operand3`/`Operand4` are standalone
  (non-aliased) and free for `Stobj`/`Ldobj` at runtime (they use only
  `DstOffset`/`SrcOffset`/`Operand`=type token).
- **Stale-DLL gotcha** (earned, opt-harden-2 / il-exception-throw): `dotnet
  build TestCases` reports "0 errors" without re-emitting on a small edit;
  ALWAYS use `--no-incremental` after editing test source, and verify DLL mtime
  > source mtime.
- **CLI filter gotcha**: the `ILRuntimeTestCLI` name filter is a simple
  `Contains` substring (no regex / `|`). Run each probe name separately or use a
  common prefix (`NeoStep17_`).

## Goals / Non-Goals

**Goals:**

- **(b) Stobj/Ldobj ref-loop.** A value type WITH reference fields copied
  through `stobj`/`ldobj` is correct for the direct-local and IL-instance
  shapes: BOTH the primitive bytes AND the ref slots are copied.
- **(b) IL-VT-with-ref-fields constrained.** `constrained.callvirt T.M` on an IL
  value type `T` that has reference fields dispatches correctly (direct-call
  override path seeds slot-0 refs; inherited-CLRMethod box path boxes with the
  ref region).
- **Regression-free.** Primitive-field VT Stobj/Ldobj stays byte-identical; the
  step17-completion Constrained paths stay green; full NeoStep smoke stays
  green; Legacy byte-identical (all edits Neo-only).
- **Adversarial probes MANDATORY** (Step 17 B1 / OPT-HARDEN K1 / F-MAJ-1
  lessons): a green smoke does NOT prove a ref-region copy correct -- construct
  probes where the dest's stale ref slot is non-null and the copy MUST overwrite
  it.

**Non-Goals:**

- **(c) generic-byref** (`ref T`/`out T` with `T` a generic parameter) -- stays
  a Step-17-tagged NIE. RARE; the JIT Stobj/Ldobj stamps only the type token;
  the ref-loop would need the SUBSTITUTED type's `TotalReferenceCount`. A JIT
  side-stamp at type-specialization time is the eventual fix; out of scope.
- **(c) `fixed`** (C# pinned byref) -- stays a Step-17-tagged NIE (or accept-
  known IF a probe shows the address works without GC pinning for the common
  `fixed` over a primitive array). The Neo frame model has no pinned-local flag;
  pinning semantics (GC) are a separate concern. No smoke case.
- **(c) interface-on-VT-constrained beyond the common `IEquatable<T>`/
  `IComparable<T>` shape** -- stays deferred (the Constrained arm already
  handles the common shape via box-once / direct-call).
- **Nested-field via `ldflda` Stobj/Ldobj** (a byref produced by
  `ldloca outer; ldflda innerField` where `innerField` is itself a VT with
  refs) -- the runtime `localInfos` scan will NOT resolve it (the field's offset
  is not a local base). Stays a tagged NIE; defer to a follow-up IF a smoke case
  exercises it.
- **CLR VT with reference fields via `stobj`/`ldobj` without a
  `ValueTypeBinder`** -- stays the area4-accepted-known (the GC refs are not
  materializable without a binder; same constraint as the by-value CLR-struct
  path).

## Decisions

### D1: Scope -- (b) IN, (c) DEFERRED

The two (b) sub-cases (Stobj/Ldobj ref-loop + IL-VT-with-ref-fields constrained)
share a SINGLE root mechanism: **the byref source must carry (or the runtime
must recover) the struct's ref-region mStack base.** Splitting them would force
two passes over the same operand-stamping / Ref-Slot-extension work. Bundling
them is ONE correctness surface. The (c) edges are independent plumbing (a
generic-param discriminator; a pinned-local flag; an interface-dispatch branch)
that does NOT fall out of (b) and would, if bundled, mix unrelated surfaces into
one diff (the explicit Step-13b / area4 scoping lesson). Matches the portfolio
scoping recommendation.

**Alternative considered:** bundle all of (b)+(c). REJECTED -- the (c) edges
have no smoke coverage and would inflate the diff with untested branches (the
reviewer cannot adversarially probe a path with no reproducer; the
untested-dead-code risk from the M3 isinst/castclass lesson).

### D2: The root mechanism -- recover the source local's RefOffset at runtime (R2)

A frame-native byref Ref Slot is `(-1, thisByteOff)` -- it carries the struct's
PRIMITIVE byte offset but NOT the struct's ref-region mStack base (the source
local's `RefOffset`). To copy/seed the ref region, the runtime needs that ref
base. Two recovery options were assessed:

- **R1 (JIT-stamp):** extend the Ref Slot encoding OR stamp the source local's
  `RefOffset` into an operand field at the producer. The `ldloca`/`ldflda`
  producers run BEFORE `LowerNeoOffsets`, so the source register index is
  available and `localInfos[srcReg].RefOffset` is stampable into a standalone
  operand field (like `NeoLdfldaInlineMarker` in `Operand4`). BUT the 8-byte Ref
  Slot is a WIRE format consumed by `stind`/`ldind`/`stobj`/`ldobj`/ref-params
  ACROSS call boundaries -- extending it to 12 bytes ripples through every
  consumer + the call-region copy (`CopyNeoCallArguments` 8-byte entry). A
  standalone operand stamp only works for the OP that owns the slot; `Stobj`/
  `Ldobj` read a generic Ref Slot produced upstream. **R1 is the higher-risk
  path** (touches the Ref Slot contract shared with stind/ldind/ref-params).
- **R2 (runtime-recover):** scan `localInfos` for the local whose `Offset ==
  thisByteOff` and read its `RefOffset`. Works for frame-native byrefs that
  point at a DIRECT local (the common case: `ldloca V; stobj` /
  `constrained.callvirt V.M` where `V` is a local). FAILS for nested-field via
  `ldflda` (the field's offset is not a local base) -- that shape stays a tagged
  NIE. For an mStack-object byref (`objectIndex >= 0`), the ILTypeInstance's
  `ManagedObjects` IS the ref region -- already accessible via
  `GetNeoILInstance`, no recovery needed.

**Decision: R2 for the green target (direct-local + IL-instance shapes); R1 is
the apply-phase fallback IF R2's `localInfos` scan proves insufficient** (e.g.
the source is a temp register, not a named local). R2 is runtime-only (NO JIT
change) -- lowest blast radius, Legacy-neutral by construction.

**Rationale (R2 over R1):** the Ref Slot wire format is load-bearing shared
infrastructure (Step 17 stind/ldind, the byref call ABI, ldelema). R1 would
either extend the wire format (12-byte slot -- ripples everywhere) or stamp a
per-OP side channel that does not survive the call-boundary copy. R2 keeps the
wire format 8 bytes; the recovery is a localized runtime lookup on the byref
path (not a hot path -- byref-of-VT-with-refs is rare). The direct-local shape
is the green target; the nested-field shape is a documented NIE.

### D3: Stobj/Ldobj arm structure (mirrors Move_Vt)

For a value type with `TotalReferenceCount > 0`, after the existing primitive
`CopyBlock`, add the ref-region copy:

- **Frame-native byref (`objectIndex == -1`), direct local:** recover
  `srcRefBase` (for Ldobj) / `dstRefBase` (for Stobj) by scanning `localInfos`
  for `Offset == thisByteOff`. Then copy `TotalReferenceCount` ref slots:
  - Stobj: `mStack[frameRefBase + dstRefBase + i] = mStack[frameRefBase +
    <src-local-RefOffset> + i]` (the src is the value register at `SrcOffset`;
    its ref base is the SOURCE operand's local -- for Stobj the src is a local
    value, recover its RefOffset from the local at `SrcOffset`).
  - Ldobj: the mirror (src is the byref-target local, dst is the dest register's
    local).
  - **Apply-phase dump gate:** confirm which operand (DstOffset vs SrcOffset) is
    the byref and which is the value local for each of Stobj/Ldobj. The JIT
    (`JITCompiler.cs:2293-2298`) sets `Register1 = baseRegIdx-2` (address),
    `Register2 = baseRegIdx-1` (value), so post-lowering `DstOffset` = address,
    `SrcOffset` = value for Stobj; the reverse for Ldobj. DUMP-CONFIRM.
- **IL-instance byref (`objectIndex >= 0`, `GetNeoILInstance`):** the
  ILTypeInstance's `ManagedObjects` is the ref region. For Stobj, write the src
  ref slots into `ins.ManagedObjects[off_ref...]`; for Ldobj, read them out.
  Reuse `CopyFrameToIL` (frame→IL) / `CopyILToFrame` (IL→frame) with the real
  `refOffset` + `refCount` (they already iterate `ManagedObjects`).
- **CLR-object byref (`NeoIsClrObject`):** unchanged (the 4d
  `NeoReadClrObjectField`/`NeoWriteClrObjectField` path handles the WHOLE VT as
  a boxed object via reflection; the ref-field-inside-a-CLR-struct case is the
  area4 accepted-known).
- **Frame-native byref NOT matching a direct local (nested-field via `ldflda`):**
  tagged NIE.

For a value type with `TotalReferenceCount == 0` (primitive-only), the existing
`CopyBlock` is the whole copy -- byte-identical.

**Discriminator insight (re-affirms the opt-harden-2 / area4 pattern):** the
per-arm TYPE TOKEN (`ilType.TotalReferenceCount` from the `Operand` type token)
determines whether the ref-loop runs -- no per-slot runtime flag needs to be
stamped at lowering.

### D4: Constrained arm -- remove the two ref-fields NIEs

- **IL-VT-direct-call path (`:3807-3833`):** remove the
  `ilConstrained.TotalReferenceCount > 0` NIE (`:3818`). After the existing
  primitive `CopyBlock` of `ilConstrained.TotalPrimitiveSize` bytes into the
  callee slot-0 region, ALSO seed the callee slot-0 ref slots: recover the
  source local's `RefOffset` (R2, scanning `localInfos` for `Offset ==
  thisByteOff`), then for `i in [0, TotalReferenceCount)`:
  `mStack[targetFrameRefBase + thisSlotInfo.RefOffset + i] =
   mStack[frameRefBase + srcLocalRefOffset + i]`. The callee override reads
  `this.refField` via in-frame `Ldfld_Ref_Inline` from its `ParamInfos[0]` ref
  region -- now correctly seeded.
- **IL-VT-inherited-CLRMethod box path (`:3842-3890`):** remove the
  `ilBoxType.TotalReferenceCount > 0` NIE (`:3876`). Extend the existing
  `CopyFrameToIL` call (`:3884-3886`) to pass the real `refOffset` (recovered
  via R2) and `refCount = ilBoxType.TotalReferenceCount` (currently passes
  `refOffset=0, refCount=0`). `CopyFrameToIL` already iterates `ManagedObjects`
  -- no new helper. The boxed `ILTypeInstance` then carries the ref fields, and
  the inherited CLRMethod dispatches on it.

**Apply-phase dump gate:** confirm `targetFrameRefBase` and the callee
`ParamInfos[0].RefOffset` are the correct seed target (the
`neo-step17-completion` Ret-arm copy-back precedent -- the seed must run BEFORE
the callee body, and the callee's mStack reservation must not be popped before
the override reads it).

### D5: Adversarial probes (MANDATORY, the binding lesson)

A green smoke does NOT prove a ref-region copy correct (Step 17 B1 / OPT-HARDEN
K1 / F-MAJ-1). Each probe MUST be a FAIL-on-HEAD stash-toggle that PASS-after:

- **Stobj overwrites a non-null stale dest ref slot.** Construct a dest local
  whose ref slot is pre-set to a non-null canary object; `stobj` a VT-with-ref-
  field whose ref field is a DIFFERENT object (or null); assert the dest ref
  slot is the new value, NOT the canary. (Without the ref-loop, the dest keeps
  the stale canary -- silent corruption.)
- **Ldobj reads the src ref slot, not the dest's stale null.** Construct a dest
  whose ref slot is null; `ldobj` from a src VT-with-ref-field whose ref field
  is a non-null object; assert the dest ref slot is the object.
- **Nested VT with ref fields** -- the NIE-tagged edge OR the recovered path
  (dump-gated at apply; the probe documents which shape lands).
- **IL-VT-with-ref-fields constrained direct-call override** -- a struct
  `{int id; string tag;}` overriding `ToString()` that reads `this.tag`; invoke
  via `constrained.callvirt` on a local; assert the override sees the correct
  `tag` (not null).
- **IL-VT-with-ref-fields constrained inherited-CLRMethod box** -- the same
  struct WITHOUT an override; `$"{s}"` / `s.GetHashCode()` must not crash and
  must reflect the ref field's identity where observable.
- **Regression:** primitives-only Stobj/Ldobj still works; the step17-completion
  Constrained probes (K1-K7) still green; full NeoStep smoke green.

## Risks / Trade-offs

- **[R2 `localInfos` scan misses the source shape] → Mitigation:** dump-gate at
  apply (the earned stale-DLL discipline: `--no-incremental`, verify DLL mtime >
  source mtime). If the green-target probe's source does NOT resolve via the
  scan (e.g. the source is a temp register, or the byref is produced by
  `ldflda`-of-a-struct-field), fall back to R1 (a JIT side-stamp of the source
  local's `RefOffset` into a standalone operand field, gated `#if
  ENABLE_NEO_MODE`). The fallback is the apply-phase escape hatch, NOT the
  primary path.
- **[Stobj/Ldobj operand confusion (which is the byref, which is the value)] →
  Mitigation:** dump-confirm at apply via a temporary `Console.WriteLine` inside
  the arm (the earned dump-noise gotcha: `OUTPUT_JIT_RESULT` floods; an
  in-arm `Console.WriteLine` fires only for the probe method). The JIT
  (`JITCompiler.cs:2293-2298`) sets `Register1=address, Register2=value` for
  Stobj; confirm post-lowering `DstOffset`/`SrcOffset` mapping.
- **[Constrained slot-0 ref seed runs after the callee mStack reservation is
  popped] → Mitigation:** the seed runs in the Constrained arm BEFORE
  `InvokeNeoCallTarget` (the callee body has not run yet); the reservation is
  popped on the callee's Ret, AFTER the body reads the refs. Mirror the
  VT-THIS-ADDR Ret-arm copy-back precedent (the copy-back must run before the
  pop; here the SEED runs before the call, so it is safe by construction).
- **[Ref-slot copy shallow-copies object identity] → Mitigation:** this matches
  C# struct-copy semantics (a struct copy shallow-copies ref fields -- both
  copies reference the same object). `Move_Vt` does the same. Document in the
  spec scenario.
- **[The `Operand4` standalone field is already used by Constrained (0x1 flag)
  and Ldflda (NeoLdfldaInlineMarker)] → Mitigation:** R2 is runtime-only (NO
  operand stamp), so this does not collide. R1 (the fallback) would need a
  different standalone field -- confirm availability at apply IF R1 is needed.
- **[Regression on the shared Stobj/Ldobj arms] → Mitigation:** the new ref-loop
  is gated on `ilType.TotalReferenceCount > 0` (primitive-only VTs byte-
  identical); full NeoStep smoke (175/175 baseline) + the step17-completion
  probes + Legacy-neutral (all edits Neo-only). Adversarial probes MANDATORY.

## Migration Plan

- Neo-only change; no migration. Legacy (`ExecuteR`) byte-identical (NOT
  modified). All runtime edits in `ILIntepreter.Neo.cs`; no JIT change for the
  green target (R2 is runtime-only).
- Rollback: revert the `ILIntepreter.Neo.cs` edits; the tagged NIEs reappear
  (the pre-change behavior).

## Open Questions

- **OQ1 (resolve at apply):** does the green-target Stobj/Ldobj byref source
  resolve via the R2 `localInfos` scan (direct local), or does it require R1
  (JIT side-stamp)? Dump-gate the first probe; if R2 misses, escalate to R1.
- **OQ2 (resolve at apply):** for the Constrained IL-VT-direct-call slot-0 ref
  seed, is the target offset `calleeFrame.ParamInfos[0].RefOffset` (analogous to
  `ParamInfos[0].Offset` for the primitive half)? Dump-confirm the callee
  frame layout.
- **OQ3 (resolve at apply):** the nested-VT-with-ref-fields Stobj/Ldobj probe --
  does it hit the NIE (the expected documented edge), or does the R2 scan
  accidentally resolves a nested-field offset? If the latter, tighten the scan
  (match only local bases, not arbitrary offsets) and document.

## Apply-phase resolution (2026-07-06)

**OQ1 RESOLVED: R2 (runtime localInfos scan).** Dump-confirmed via in-arm
`Console.WriteLine` diagnostics inside the Stobj/Ldobj arms. For probe 1
(`NeoStep17_StobjVtWithRefField_OverwritesStaleDestRef`, the `StobjIntoByref`
helper inlined into the caller), the Stobj operands were:
`objIdx=-1 off=0 DstOffset=24 SrcOffset=32`, and the byref's `off=0` matched
`localInfos[0].Offset` exactly (direct local, RefOffset=0). The src value
register `SrcOffset=32` matched `localInfos[7].Offset` (RefOffset=3). Both the
dst-byref target AND the src value local resolved via the scan. **R1 (JIT
side-stamp) was NOT needed.**

**Stobj/Ldobj operand mapping CONFIRMED (Risk 2).** Post-lowering:
- **Stobj:** `DstOffset` = the byref address (8-byte Ref Slot: objIdx + off);
  `SrcOffset` = the value local. Matches the JIT
  (`JITCompiler` sets Register1=address, Register2=value; LowerNeoOffsets maps
  Register1->DstOffset, Register2->SrcOffset).
- **Ldobj:** the REVERSE -- `SrcOffset` = the byref address; `DstOffset` = the
  dest value local. Confirmed by a separate Ldobj-arm dump.

**R2 scope (the byref-crosses-frame boundary -- earned constraint).** R2's
localInfos scan resolves the byref to a direct local ONLY WHEN the byref is
produced AND consumed in the SAME frame. A byref PARAMETER (a `ref` param
passed to a helper) points at the CALLER's frame; the helper's localInfos scan
cannot recover that ref base. The green target is therefore the same-frame
shape: the C# compiler's trivial inliner folds a small byref helper (`static
void M(ref S dst, S src) { dst = src; }`) into the caller, where source +
dest are caller locals and R2 resolves. Probe 2 was restructured from a
returning helper (which hit the separate F-9 / NEO-INLINED-RETURN-MOVE defect
on its return value) to an `out`-param helper that inlines cleanly.

**OQ2 RESOLVED: the slot-0 ref seed target is the CALLEE's frame ref region.**
Dump (`[CONST-DIRECT-DUMP]` in the Constrained direct-call path) for probe 4:
`slot0.Offset=0 slot0.RefOffset=0 calleePrim=4 calleeRef=1 mStack.Count=5
callerFrameRefBase=0`, and `thisByteOff=0 -> li[0].RefOffset=0` (R2 src
recovery). The critical finding: **the seed CANNOT be a pre-call write to
`mStack[mStack.Count + slot0.RefOffset]`** because the callee's ExecuteNeo
reserves its frameRefBase via `mStack.Add(null)` (zeroing slots). A pre-call
write gets clobbered by the reservation. The fix mirrors the VT-THIS-ADDR
copy-back mechanism: 4 new optional hook params on `ExecuteNeo`
(`constrainedSlot0SeedRefOffset / SrcRefBase / RefCount` + an unused base),
seeded INSIDE ExecuteNeo immediately AFTER its mStack reservation (line ~734),
BEFORE the body runs. The direct-call path calls `ExecuteNeo` directly (not
`InvokeNeoCallTarget`) to pass the hook. `constrainedSlot0SeedSrcRefBase =
callerFrameRefBase + srcLocalRefOffset` (R2); the target is
`calleeFrameRefBase (= mStack.Count at entry) + thisSlotInfo.RefOffset`. All
defaults preserve existing callers (the hook is inert when RefCount==0).

**IL-VT-inherited-CLRMethod box path (probe 5).** Simpler than the direct-call:
`CopyFrameToIL` already iterates `ManagedObjects`. Recover `boxSrcRefOffset`
via the same R2 scan and pass the real `refOffset + refCount` (was
`refOffset=0, refCount=0`). The boxed `ILTypeInstance` then carries the ref
fields, and the inherited CLRMethod dispatches on it.

**OQ3 RESOLVED: the genuine nested-VT-field-byref shape is BLOCKED by
pre-existing gaps and stays the documented deferred edge.** Constructing a
byref to a nested field (`ref outer.inner`) and stobj/ldobj through it hits
the Step-6 `Ldfld_Value` NIE (a nested-VT-field read) and the F-6
`Ldflda_Inline` paths BEFORE reaching the stobj/ldobj arm -- these are
independent pre-existing gaps (Step 6 / F-6), NOT the stobj/ldobj ref-loop.
Probe 3 was repurposed to a VT with TWO reference fields
(`NeoStep17VtWithTwoRefs { int n; string a; string b; }`) which exercises the
MULTI-SLOT ref-region copy loop (refCount==2) within the same green-target
same-frame shape -- the load-bearing guard against an off-by-one that copies
only slot 0. The dest's `b` canary MUST be overwritten. The genuine
nested-field-byref stobj/ldobj stays a tagged NIE inside the arm (the
`dstRefBase < 0` / `srcRefBase < 0` throw) for the rare shape that reaches it.

**Actual edit sites (all in `ILIntepreter.Neo.cs`, Neo-only):**
- `ExecuteNeo` signature (line ~684): +4 constrained-slot-0-seed hook params.
- `ExecuteNeo` body (line ~736): seed slot-0 refs right after the mStack
  reservation, before the body.
- `Stobj` arm (line ~3514): ref-region copy for the frame-native-direct-local
  shape (R2 scan) + the IL-instance (`ManagedObjects`) shape; nested-field NIE.
- `Ldobj` arm (line ~3544): the mirror.
- Constrained arm IL-VT-direct-call path (line ~3906): removed the
  `TotalReferenceCount > 0` NIE; R2 scan + direct `ExecuteNeo` call with the
  seed hook params.
- Constrained arm IL-VT-inherited-CLRMethod box path (line ~3982): removed the
  `TotalReferenceCount > 0` NIE; R2 scan + real `refOffset/refCount` to
  `CopyFrameToIL`.

**No JIT change.** R2 is runtime-only. The constrained seed is a runtime hook
on `ExecuteNeo` (optional params, inert by default). Legacy untouched.

**Probes MANDATORY + stash-toggle proofs (Step 17 B1 / OPT-HARDEN K1 lesson).**
6 new probes in `TestCases/NeoStep17Test.cs`. Stash-toggle (runtime reverted via
`git stash push -- ILIntepreter.Neo.cs`, TestCases kept): probes 1/2/3/4/5
FAIL-on-HEAD, PASS-after; probe 6 (primitives-only regression guard) PASS on
both (byte-identical -- correct, it is a guard not a FAIL-on-HEAD probe).
Full NeoStep smoke 181/181 (175 baseline + 6 new). Legacy-neutral: all 41
NeoStep17_ tests pass on plain `Debug` + `useRegister=true` (ExecuteR).

## Review-loop round 1 (M1/M2)

**Reviewer:** APPROVE with 2 Minors (no Blocker/Major). Both fixed post-review;
re-verified Neo 181/181 + NeoStep17 Neo 41/41 + Legacy build clean.

**M1 (IL-instance branch silent-skip -> loud NIE).** The Stobj/Ldobj IL-instance
branches (`objectIndex >= 0`, byref target is the IL `ILTypeInstance`, value
operand is a frame-local register) guarded the ref-region copy with
`if (srcRefBase >= 0) { ... }` / `if (dstRefBase >= 0) { ... }` and SILENTLY
SKIPPED on a `localInfos` scan-miss -- the instance's `ManagedObjects` kept its
stale/null ref slots (silent corruption). The frame-native branches already
threw a clean Step-17-tagged `NotImplementedException` on the same scan-miss.
**Fix:** mirrored the frame-native branch -- the IL-instance branches now throw
the tagged NIE on a scan-miss too. An exotic unresolved-byref shape now fails
LOUD (the green-target probes all resolve, so the NIE does not fire for them).
Edit sites: Stobj IL-instance branch (`ILIntepreter.Neo.cs` ~3603) and Ldobj
IL-instance branch (~3676). The thrown NIE messages tag the IL-instance path
distinctly ("stobj of an IL-instance VT field WITH reference fields from a non-
direct-local value ...") so a future hit is diagnosable.

**M2 (`constrainedSlot0SeedRefBase` param DROPPED).** The `ExecuteNeo` hook had
4 constrained-slot-0-seed params; one (`constrainedSlot0SeedRefBase`) was dead
weight -- the sole caller passed `0 /*unused*/`, and the seed loop read the
callee's OWN `frameRefBase` (= `mStack.Count` at ExecuteNeo entry, captured
inside the function), never the passed base. The guard
`constrainedSlot0SeedRefBase >= 0` was therefore always-true and meaningless.
**Fix:** dropped the param entirely (preferred over documenting) -- removed it
from the `ExecuteNeo` signature, from the seed-loop guard, and from the one
constrained direct-call site. The hook is now 3 load-bearing params
(`constrainedSlot0SeedRefOffset/SrcRefBase/RefCount`) + a clarifying comment
explaining there is intentionally NO base param (the seed target is always the
callee's own `frameRefBase`). All 5 `ExecuteNeo` callers verified: 4 pass the
5-arg form (hook defaults inert); the 1 constrained direct-call site now passes
3 named hook params. The seed loop still runs correctly (probe 4
`NeoStep17_ConstrainedIlVtWithRefFields_DirectCall` PASS).

**Re-verify:** CLI `Debug_Neo --no-incremental` 0 errors; TestCases `Debug
--no-incremental` 0 errors; plain `Debug` CLI 0 errors (Legacy untouched --
Neo-only file). NeoStep smoke 181/181, 0 failed; NeoStep17 filter 41/41, 0
failed. Working tree UNCOMMITTED (LEAD commits after re-review).
