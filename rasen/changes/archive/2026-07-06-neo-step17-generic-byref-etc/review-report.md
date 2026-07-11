# Review Report -- neo-step17-generic-byref-etc

**Reviewer:** non-author verifier (adversarial review, `openspec-gstack-review`).
**Date:** 2026-07-06.
**Branch:** `features/object-model-overhaul`.
**HEAD under review:** working tree (gate applied at `JITCompiler.cs:913`); HEAD
commit `0aafdb34` used as the stash-toggle baseline.
**Skill:** `openspec-gstack-review` (loaded; this report follows its structure;
the task-specific F-10-R1 adversarial mandate overrides the generic
scope-drift / design / coverage sub-steps, which are N/A for a 1-line JIT gate +
6 TEST keepers).

## Scope check

- **Intent:** close Step 17 (c) edges + the latent F-10-R1 JIT-discriminator gate
  (capability `neo-byref`). Per-sub-item dump-gate scoped 3 of 4 sub-items as
  no-op/defer; the single engine change is the F-10-R1 gate.
- **Delivered:** exactly that. One Neo-only JIT-pass edit
  (`TypeSpecializeNeoOpcodes case Ldflda:` clears F-10 when F-6 stamps,
  `JITCompiler.cs:913`, ~2 code lines + rationale comment) + 6 test keepers in
  `TestCases/NeoStep17Test.cs` (3 generic-byref + 2 interface-on-VT TEST-ONLY +
  1 F-10-R1 FAIL->PASS). Runtime arm untouched. Docs (`neo-deferred-items.md`)
  updated (D-CONSTRAINED (c) CLOSED; F-10-R1 RESOLVED; `fixed` rerouted).
- **Scope drift:** NONE. No out-of-scope engine edits; the `fixed`/`Conv_U`
  deferral is correctly rerouted, not silently implemented (respects the
  K1/Q-NEWOBJ "don't guess" lesson).
- **Scope Check: CLEAN.**

## Verdict: APPROVE-WITH-FINDINGS

No Blocker or Major remains open. One Minor (design-reasoning imprecision on the
boxed-IL-VT case + a latent risk for a future feature; document-only, no code
change required now) and one accepted-known Trivial (the `fixed`/`Conv_U`
deferral, out of `neo-byref` scope). The gate is correct for every shape
reachable today; the change ships a clean, independently-verified
FAIL-on-HEAD -> PASS-after reproducer with the full regression set intact.

---

## What I independently verified (evidence)

### 1. The gate form is correct (keys on the operand's value-category, NOT `!declaringType.IsValueType`)

- The single engine line is `JITCompiler.cs:913`
  `op.Operand4 &= ~NeoLdfldaClrStructFieldMarker;`, placed INSIDE the F-6
  stamping block (`JITCompiler.cs:865`):
  `if (srcType is ILType srcIl && srcIl.IsValueType && !srcIl.IsEnum)`.
  `srcType = GetRegisterType(registerTypes, op.Register2)` -- the SOURCE OPERAND
  register's static type, i.e. exactly the F-6 stamp condition. The gate fires
  in the F-6 branch and nowhere else. (Confirmed by reading
  `JITCompiler.cs:862-916`.)
- The naive `!declaringType.IsValueType` gate was correctly REJECTED. I
  confirmed the predicate the naive form would key on is wrong:
  `IsClrStructFieldOfIL` (`JITCompiler.cs:2648-2651`) returns true for an IL
  VALUE-type declaring type too (`declaringType is ILType && !(fieldType is
  ILType) && ...` -- it does NOT check `!IsValueType`). So an IL VT with a CLR-
  struct field qualifies for F-10 at the body-emission site
  (`JITCompiler.cs:2469-2470`, `op.Operand4 |= NeoLdfldaClrStructFieldMarker`),
  AND qualifies for F-6 at the type-spec site when the operand is in-frame ->
  both stamp (`Operand4 = 0x3`). The gate makes that impossible. Sound.

### 2. Gate placement vs `LowerNeoOffsets` -- correct; markers survive lowering

- `TypeSpecializeNeoOpcodes` is called at `JITCompiler.cs:574`, BEFORE
  `Optimizer.LowerNeoOffsets` at `JITCompiler.cs:594`. The gate runs pre-
  lowering. (Confirmed by reading `JITCompiler.cs:574,594`.)
- `OpCodeR` is `[StructLayout(LayoutKind.Explicit)]`; `Operand4` is at
  **offset 20** (`OpCode.cs:70-71`), STANDALONE -- not aliased with
  `Register1`/`DstOffset` (off 4), `Register2`/`SrcOffset` (off 6), or
  `Register3`/`OperandOffset`/`Operand` (off 8). Lowering the register indices
  to byte offsets cannot clobber the F-6/F-10 marker bits.
- I confirmed `LowerNeoOffsets`'s `case OpCodeREnum.Ldflda:`
  (`Optimizer.Neo.cs:891-900`) writes ONLY `DstOffset`/`SrcOffset` -- it does
  NOT touch `Operand4`. (Other opcodes -- `Move_Vt`, `Ldelem`, `Stelem` -- do
  reuse `Operand4` for their own post-lowering payloads at
  `Optimizer.Neo.cs:579,873,972,1045,1068`; that reuse is irrelevant to `Ldflda`,
  whose marker is stamped ONLY by the JIT and read by the runtime arm at
  `ILIntepreter.Neo.cs:1115-1116`.)

### 3. F-10-R1 reproducer genuinely FAILs on HEAD -> PASSes after the gate (load-bearing, independently verified)

- **Stash-toggle proof:** I stashed ONLY `JITCompiler.cs` (reverting the gate to
  HEAD; confirmed the `&= ~NeoLdfldaClrStructFieldMarker` line gone, the
  body-emission `|=` stamp at `:2434` remaining), rebuilt the CLI `Debug_Neo`,
  and ran `NeoStep17_F10R1_ConstrainedVtLdfldaClrField` against the SAME
  `TestCases.dll`:
  - **HEAD (gate reverted):** `1 tests failed` --
    `System.NullReferenceException: Object reference not set to an instance of
    an object. at ILIntepreter.NeoMarshalByrefFieldToSlot(...):line 466`.
    Matches the design's root cause exactly (reads `prefix` value 7 as the
    byref `objectIndex` -> `mStack[7]` is a garbage slot -> NRE).
  - **Gate applied:** `Ran 1 tests, 0 failded` (returns 60, the correct sum).
  - Gate restored (`git stash pop`); line 913 back.
- This is the implementer's central claim, independently reproduced.

### 4. The regression set is intact (independently re-run, gate applied)

| Filter | Result |
|---|---|
| `NeoStep17_F10R1_ConstrainedVtLdfldaClrField` | 1/1 PASS |
| `NeoClrStructField` (heap-IL F-10 path -- the 8 keepers) | 8/8 PASS |
| `NeoStep17_LdfldaInline` (6 F-6-only probes, the DISPROVEN-reorder guard) | 8/8 PASS |
| `NeoStep17_TC6` (F-6-only) | 1/1 PASS |
| `NeoStep20` (F-10 load-bearing for Step 20 sync) | 9/9 PASS |
| Full `NeoStep` smoke | **204/204, 0 failed** |

The 6 F-6-only probes (the guard against any accidental F-6 perturbation -- the
runtime reorder that was DISPROVEN broke exactly these) are untouched. The gate
clears F-10 only; F-6 stamping is byte-identical.

### 5. Legacy-neutral (confirmed)

- `TypeSpecializeNeoOpcodes` is `#if ENABLE_NEO_MODE`. I built the CLI with
  plain `Debug` (compiles the gate out) + `useRegister=true` and ran the Step 17
  filter: `NeoStep17` -> **47/47, 0 failed**. Legacy byte-identical by
  construction and by test.

### 6. Runtime arm unchanged; the F-10-first check order is safe post-gate

- The runtime `Ldflda` arm (`ILIntepreter.Neo.cs:1086-1169`) is untouched. Its
  dispatch order is: (1) `clrStructFieldMarker && objIdx >= 0` (F-10 shape 4);
  (2) `objIdx == -1` (shape 1/2 frame-native); (3) `inlineMarker` (F-6 shape 3
  flat bytes); (4) else (heap/CLR).
- Pre-gate, the both-stamp `Operand4 = 0x3` let arm (1) mis-fire on the
  constrained-VT flat-bytes shape (`objIdx = prefix = 7 >= 0`). Post-gate,
  `clrStructFieldMarker` is false whenever `inlineMarker` is true, so arm (1)
  can never fire on an in-frame-VT operand -- arms (2)/(3) handle them. The
  DISPROVEN runtime reorder (F-6-before-F-10) is correctly NOT applied.

---

## Findings

### [Minor] F-1 -- Design's "boxed-IL-VT-keeps-F-10" reasoning is imprecise; latent risk for a future feature (no reachable bug today)

**Where:** `proposal.md:86-91`, `design.md:200-222`, `planning-context.md:277-
287` (apply Findings), and the `neo-deferred-items.md` F-10-R1 row
("`!declaringType.IsValueType` ... would break the boxed-IL-VT-with-CLR-struct-
field case").

**Observation:** The design argues the type-spec-pass gate is correct for the
boxed-IL-VT-with-CLR-struct-field case while a naive `!declaringType.IsValueType`
gate would "silently break" it, because the type-spec gate keys on the
OPERAND's value-category (in-frame VT vs heap/boxed) whereas the naive gate
keys on the declaring type. That distinction is illusory for `ldflda
this.clrField` inside a VT method body:

- `BuildInitialRegisterTypes` (`JITCompiler.cs:1090-1091`) types `this` as
  `declaringType` -- for a VT method body, `this` is ALWAYS `ILType(
  IsValueType=true)` at JIT time, REGARDLESS of whether the runtime call is
  in-frame (constrained direct-call) or boxed (heap ILTypeInstance). The
  register-type tracking is STATIC; it does not know the runtime calling
  convention.
- So for a boxed-VT method body's `ldflda this.clrField`, F-6 WOULD stamp
  (`srcType is ILType && IsValueType`), and the gate WOULD clear F-10 --
  exactly what the naive declaring-type gate would do. The two gates have
  IDENTICAL behavior for this case; they differ only in WHERE the clear
  happens, not WHETHER.

**Why this is NOT a Blocker/Major:** the boxed-IL-VT-method-dispatch path is
UNREACHABLE today. I constructed the load-bearing adversarial probe the review
mandated -- `NeoStep17_Review_BoxedIlVtLdfldaClrField` (an IL value type with a
CLR-struct field, boxed into an interface reference, then `callvirt` the method
on the boxed instance so the body's `ldflda this.field` runs with `this` = the
boxed heap object). Result:

- **HEAD (gate reverted):** `MissingMethodException: Neo Callvirt_Interface:
  type ...NeoStep17ReviewBoxedVt does not implement interface
  ...INeoStep17SetAndSum (method slot 0).` -- a SEPARATE pre-existing upstream
  gap (Neo's interface-map dispatch on boxed IL value types is unimplemented).
- **Gate applied:** IDENTICAL `MissingMethodException`.

The gate changes nothing for this path (it fails upstream of the gate). The
canonical heap-F-10 shape that IS reachable -- a heap IL CLASS instance with a
CLR-struct field (declaring type `IsValueType == false`, so F-6 never stamps,
F-10 stays) -- is verified preserved (`NeoClrStructField` 8/8).

**Latent risk (the real point):** when/if Neo implements boxed-IL-VT method
dispatch (interface `callvirt` on a boxed struct), the gate as written would
clear F-10 for the body's `ldflda this.clrStructField`, mis-routing it to F-6
shape 3 (frame bytes) instead of the F-10 heap path -> wrong byref -> silent
corruption or NRE. At that point EITHER the gate must be revisited (e.g. do not
type `this` as `IsValueType` for a boxed entry point) OR the runtime arm needs
an additional discriminator. The current change is correct ONLY because the
path is unreachable.

**Recommended fix (document-only, no code change now):** add a forward-looking
note to `.trae/documents/neo-deferred-items.md` (F-10-R1 row, or a new D-*
entry "boxed-IL-VT-method-dispatch") recording that the F-10-R1 gate's
correctness relies on "boxed-IL-VT method dispatch is unimplemented" and MUST
be re-audited when that path lands. This prevents a future implementer from
shipping the boxed-VT-dispatch feature and silently breaking the CLR-struct-
field-byref path.

**Probe disposition:** the two temporary probes
(`NeoStep17_Review_BoxedIlVtLdfldaClrField`,
`NeoStep17_Review_DirectIlVtLdfldaClrField`) and the helper struct
`NeoStep17ReviewBoxedVt` were REMOVED before returning; the test file is back
to the implementer's 6-keeper state (`git diff --stat` = +145, the
implementer's insertions only; 0 `NeoStep17_Review` matches remain).

### [Trivial] F-2 -- `fixed`/`Conv_U` deferral (accepted-known, out of scope)

**Where:** `proposal.md:21-35,107-112`; `design.md:43-78`;
`neo-deferred-items.md` D-CONSTRAINED (c).

**Observation:** confirmed accepted-known. `fixed (int* p = arr)` fails on HEAD
with `Conv_U not yet implemented (Step 6)` (and `Ldtoken` for an initialized
array). Correctly rerouted to a future pointer/`Conv_U` step (outside
`neo-byref`); the array-element address works via `ref arr[i]` (TC14 green via
`ldelema` + `stind`/`ldind`). No action.

### [Observation, not a finding] Boxed-IL-VT interface-callvirt is a separate pre-existing Neo gap

Per "See Something, Say Something" (collaborative repo mode): the adversarial
probe construction surfaced that boxing an IL value type into an interface
reference and `callvirt`-ing its method throws `MissingMethodException: Neo
Callvirt_Interface: type ... does not implement interface ... (method slot 0)`.
This is PRE-EXISTING (identical on HEAD, gate reverted) and UNRELATED to this
change -- it is Neo's interface-map dispatch on boxed IL value types. It is the
upstream blocker that prevented end-to-end positive verification of F-1's
boxed-VT case. Flagging for awareness; it belongs to whatever future change
implements boxed-IL-VT method dispatch (the same change that must re-audit
F-1's gate).

---

## Accepted-known Minors (out of scope, noted)

- **`fixed`/`Conv_U`:** see F-2 above. Accepted-known; rerouted.
- **`Stfld_Ref`/`Ldfld_Ref` F-10 hash on in-frame-VT (design.md:262-274,
  tasks 1.3):** the type-spec pass rewrites `Stfld`/`Ldfld` to the `_Inline`
  variant for in-frame-VT operands, whose runtime arm does NOT consult
  `Operand4`; no probe exercises an in-frame-VT `Stfld_Ref`/`Ldfld_Ref` that
  reads the F-10 hash. Out of scope; the apply-phase JIT-dump verified no such
  path for the F-10-R1 probe body (pure `ldflda this.field; call`).

## Test-coverage note (Step 4.75 of the skill)

The change is a 1-line JIT discriminator + 6 keepers. Coverage of the engine
change is appropriate and adversarial:
- FAIL-on-HEAD -> PASS-after keeper (`NeoStep17_F10R1_ConstrainedVtLdfldaClrField`)
  proves the defect is real and the gate fixes it (stash-toggle independently
  re-verified).
- 6 F-6-only probes (the DISPROVEN-reorder guard) prove the gate does not
  perturb F-6.
- 8 `NeoClrStructField` probes prove the heap-IL-class F-10 path is preserved.
- NeoStep20 9/9 proves F-10 stays load-bearing for Step 20.
- The ONE gap in positive coverage is the boxed-IL-VT-keeps-F-10 property (F-1
  above) -- blocked by an upstream `MissingMethodException`, not by this
  change. The heap-IL-CLASS counterpart (the reachable shape) IS covered.

No additional keeper is required to ship THIS change (the boxed-VT path is
unreachable). F-1's recommended action is a doc note tying the gate's
correctness to the boxed-VT-dispatch gap, so the re-audit happens when it
matters.

## Summary

- **APPROVE-WITH-FINDINGS.** No Blocker. No Major. 1 Minor (F-1, document-only)
  + 1 Trivial (F-2, accepted-known deferral) + 1 observation (boxed-VT
  interface-callvirt gap, pre-existing, unrelated).
- **The load-bearing finding:** the gate is correct for every shape reachable
  today, AND the design's stated reason it is safe for the boxed-IL-VT case is
  imprecise -- both the shipped type-spec gate and the rejected naive gate
  would clear F-10 for a boxed-VT method body's `this`. This is harmless TODAY
  only because boxed-IL-VT method dispatch is unimplemented in Neo
  (`MissingMethodException` upstream, empirically confirmed identical on HEAD
  and gate-applied). The heap-IL-CLASS F-10 path (the reachable heap shape) is
  verified preserved. Document the dependency; re-audit when boxed-VT dispatch
  lands.
- **The change is safe to ship.** The implementer's claims held under
  independent adversarial verification (stash-toggle FAIL-on-HEAD, full
  regression set, Legacy-neutral).
