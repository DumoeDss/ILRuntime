## Why

This change closes the Step 17 **(c) edges** + the latent **F-10-R1** JIT-
discriminator gate. The LEAD seed named 4 sub-items and mandated a HEAD dump-
gate per sub-item (the array-child lesson: the orientation hypothesis is often
wrong). The dump-gate **disproved 3 of the 4 hypotheses** and pinned the single
real gap. Per-sub-item dump verdict (Debug_Neo, HEAD `0aafdb34`, NeoStep 198/198
baseline):

1. **generic-byref** (`ref T` / `out T` with `T` a generic parameter) -- **NO-OP.**
   Three probes (`Swap<int>`, `Swap<IL-ref-class>`, `Swap<IL-value-type>`) all
   PASS on HEAD unmodified. The byref model is type-agnostic: a byref is an 8-
   byte Ref Slot `(objectIndex, offset)` copied as 8 bytes regardless of the
   element type; the generic-param type token needs no special resolution at the
   byref level (the JIT resolves `T` at the call site via generic substitution and
   emits the right Move/Move_Vt/ref-Move body). The byref typed-ref bridge
   (area4 4c) already handles the concrete-typed form; the generic-param form
   "just works" through it. The seed's hypothesis ("confirm the generic-param
   form fails and pin where") is DISPROVEN.

2. **`fixed` unmanaged-pinning** -- **BLOCKED, OUT OF SCOPE.** A `fixed (int* p =
   arr)` probe FAILS on HEAD with `Neo: opcode Conv_U not yet implemented (Step
   6)` (and `Ldtoken` if an array initializer is used -- also unimplemented). The
   C# compiler lowers the `fixed` statement to raw-pointer opcodes: `conv.u`
   (`Conv_U`) converts the pinned array reference to a native `int*`, then pointer
   indexing dereferences it. This is a fundamentally different model from the Neo
   VM's managed Ref Slot. The array-element ADDRESS itself already works via
   `ref arr[i]` (TC14 `ClrPrimitiveArrayLdelema_StindLdind` is green via `ldelema`
   + `stind`/`ldind`); GC pinning is a CLR-host concern the interpreter does not
   model. So the gap is NOT in the byref machinery -- it is the unimplemented
   `Conv_U`/`Conv_I` pointer-conversion opcodes (and optionally `Ldtoken` for
   initialized arrays). Implementing those is outside the `neo-byref` capability.
   **DEFER** (accepted-known; route to a future pointer/`Conv_U` step). Do NOT
   propose a `Conv_U` implementation here (out of scope; a guessed fix risks the
   K1/Q-NEWOBJ lesson).

3. **interface-on-VT-constrained beyond the common shape** -- **NO-OP.** Two
   probes (an IL struct implementing an interface dispatched via a generic
   constrained caller; a CLR struct implementing `IComparable<int>` dispatched via
   a generic constrained caller) both PASS on HEAD unmodified. The {a,d,M2}
   cohort (`neo-step17-completion`) + the (b) cohort (`neo-step17-stobj-refloop`)
   already cover the box-and-interface-dispatch + the IL-VT-direct-call + the
   CLR-VT-box-once + the IL-VT-inherited-CLRMethod-box paths. There is no
   reachable "beyond the common shape" interface-on-VT-constrained gap today. The
   seed's hypothesis ("the box-and-interface-dispatch branch may still NIE") is
   DISPROVEN.

4. **F-10-R1 JIT-discriminator gate** -- **REAL GAP (the only engine change).**
   The latent F-6/F-10 both-stamp shape IS reachable, and it crashes. An IL value
   type `struct V { int prefix; TestVector3NoBinding field; }` whose method does
   `ldflda this.field`, invoked via `constrained.callvirt` direct-call (a generic
   caller `T v where T:struct,IFace`), FAILS on HEAD with `NullReferenceException`
   at `NeoMarshalByrefFieldToSlot` (ILIntepreter.Neo.cs:466). Root cause: the
   `constrained.callvirt` direct-call path copies the struct's FLAT PRIMITIVE
   bytes (just `prefix`) into callee slot-0; the body's `ldflda this.field` reads
   slot-0's leading int = `prefix` value (e.g. 7) as the byref `objectIndex`. BOTH
   markers are stamped (`Operand4 = 0x3`: F-6 in-frame-VT `0x1` OR F-10 CLR-
   struct-field `0x2`), and the runtime Ldflda arm checks F-10 first
   (`clrStructFieldMarker && objIdx >= 0`) -- so it produces
   `(objIdx=7, ReferenceOffset | flag)`, and the consumer reads `mStack[7]` (a
   garbage slot, NOT the struct -- it was not boxed) -> NRE. The defect was
   "latent" only because no prior probe constructed the constrained-VT-flat-bytes-
   slot-0 shape; this change constructs it (sub-item #3's constrained-VT shape is
   the trigger).

## What Changes

- **The F-6 and F-10 `ldflda` markers SHALL be made mutually-exclusive at the JIT
  producer.** The F-10 marker (`NeoLdfldaClrStructFieldMarker = 0x2`) SHALL NOT
  be stamped when the source operand is an in-frame IL value type (the F-6
  condition: `srcType is ILType && IsValueType && !IsEnum`). The gate is applied
  in the `TypeSpecializeNeoOpcodes case Ldflda:` pass (where the F-6 marker is
  already stamped and the source register's type is available): when F-6 is
  stamped, CLEAR any F-10 marker the main-JIT body emission set earlier. This
  makes the both-stamp shape (`Operand4 = 0x3`) impossible at the producer, so
  the runtime Ldflda arm's F-10-first check can never mis-fire on an in-frame-VT
  operand. With F-10 suppressed, the runtime routes to F-6 shape-3
  `(-1, operandSlotOff + fieldPrimOff)` -- which the dump-gate PROVED produces a
  correct byref for this shape (the probe PASSES). Neo-only (the type-spec pass
  is `#if ENABLE_NEO_MODE`); Legacy is the reference and is NOT modified.
- **The box-required heap-IL CLR-struct-field shape is UNCHANGED.** The gate
  clears F-10 ONLY when F-6 is stamped (source is an in-frame VT). A heap IL
  reference instance (`HolderOne`, a `class`) or a BOXED IL value type (operand is
  an mStack object, not an in-frame VT) never stamps F-6, so F-10 stays stamped
  for those shapes -- the existing F-10 path (`ManagedObjects[ReferenceOffset]`)
  fires byte-identically. (NOTE: a naive `!type.IsValueType` declaring-type gate
  was considered and REJECTED at propose -- it would silently break the boxed-IL-
  VT-with-CLR-struct-field case, where the declaring type is a value type but the
  operand is a heap boxed object that correctly needs F-10. The type-spec-pass
  gate keys on the OPERAND's value-category, not the declaring type, so it is
  correct for both the in-frame-VT and the boxed-VT cases.)
- **3 generic-byref regression guards SHALL be added** (`Swap<int>`,
  `Swap<IL-ref>`, `Swap<IL-VT>`) -- TEST-ONLY, all PASS on HEAD unmodified. They
  lock the closure (the byref model is type-agnostic) so a future change cannot
  silently regress the generic-param form. Mirrors `neo-k2fam-bridge` (TEST-ONLY
  outcome).
- **2 interface-on-VT-constrained regression guards SHALL be added** (IL-VT and
  CLR-VT, both dispatched via a generic constrained caller) -- TEST-ONLY, both
  PASS on HEAD unmodified. They lock the closure (the {a,d,M2,b} cohorts cover
  the box-and-interface-dispatch path).
- **1 adversarial keeper SHALL be added** for F-10-R1
  (`NeoStep17_F10R1_ConstrainedVtLdfldaClrField`) -- FAIL-on-HEAD (NRE) ->
  PASS-after. This is the load-bearing evidence the both-stamp defect is real and
  the gate fixes it.

## Non-Goals

- **`fixed` statement support is OUT OF SCOPE** (deferred). It requires the
  unimplemented `Conv_U`/`Conv_I` pointer-conversion opcodes (and optionally
  `Ldtoken` for initialized arrays), which are outside the `neo-byref`
  capability. The array-element address works via `ref arr[i]` (TC14 green). A
  future pointer/`Conv_U` step owns this.
- **The runtime Ldflda arm's F-10-first check order is NOT changed.** The
  reviewer's recommended runtime F-6-before-F-10 reorder was DISPROVEN (broke 6
  NeoStep17 F-6-only probes, 190->184 -- F-6 shape 3 vs shape 1/2 produce
  different byrefs). The fix is at the JIT producer (the gate), NOT a runtime
  reorder. The runtime order stays as shipped.
- **The Stfld_Ref / Ldfld_Ref F-10 discriminator is NOT changed** (it reads
  `Operand4 != 0` as the field-type hash, not the F-10 marker bit, for the heap
  box/unbox path). For an in-frame-VT operand, the type-spec pass rewrites
  Stfld/Ldfld to the `_Inline` variant (which does not consult the F-10 hash), so
  the F-10 hash stamped on the pre-rewrite Stfld_Ref/Ldfld_Ref is never consumed
  for the in-frame-VT case. The apply phase SHALL verify this via JIT dump (if an
  in-frame-VT Stfld_Ref/Ldfld_Ref path is found that DOES consume the F-10 hash,
  the same type-spec-pass gate is extended to clear it -- but no probe exercises
  it today).
- **The cross-frame byref-parameter limitation, F-2, F-7, F-9** and other
  accepted-known upstream gaps are NOT in scope.

## Regression risk: LOW-MEDIUM.

The only engine change is the JIT type-spec-pass gate (Neo-only, Legacy-neutral
by construction -- the pass is `#if ENABLE_NEO_MODE`). It clears the F-10 marker
ONLY when F-6 is stamped (an in-frame-VT source), a shape that was BROKEN before
(the F-10-R1 NRE). The existing heap-IL F-10 path (8 NeoClrStructField probes,
NeoStep20 async, the Step-20 builder-byref hot path) never stamps F-6, so F-10
stays -- byte-identical. The dump-gate confirmed: with the gate applied,
NeoClrStructField 8/8, full NeoStep smoke clean (only the deferred-`fixed`
probes fail on `Conv_U`). Gate: full `NeoStep` smoke (198/198 + the new probes)
+ NeoClrStructField 8/8 + NeoStep20 9/9 + NeoOptHard 24/24 + the Step 17 Legacy
filter (Legacy-neutral confirmation).

## Adversarial-probe plan

- **F-10-R1 (FAIL-on-HEAD -> PASS-after):** `NeoStep17_F10R1_ConstrainedVtLdfldaClrField`
  -- the both-stamp shape via a constrained-VT direct-call. Stash-toggle proof:
  revert the gate -> the probe NREs again.
- **F-10 heap-IL regression (PASS on HEAD, MUST stay PASS):** the 8
  `NeoClrStructField_*` probes + `NeoStep17_Probe_F10R1...` shape's heap
  counterpart. The gate MUST NOT change their result.
- **generic-byref (PASS on HEAD):** the 3 `NeoStep17_Probe_GenericByRef_*` guards
  stay green (lock the type-agnostic closure).
- **interface-on-VT (PASS on HEAD):** the 2 `NeoStep17_Probe_InterfaceOn*VtConstrained`
  guards stay green (lock the {a,d,M2,b} closure).
- **F-6-only probes (PASS on HEAD, MUST stay PASS):** the 6 NeoStep17 F-6 probes
  (`_LdfldaInline_*`, `_TC6_RefInFrameVtField`). The DISPROVEN runtime reorder
  broke these; the JIT gate does NOT touch them (F-6 stamping unchanged; only F-
  10 is gated). They are the regression guard against any accidental F-6
  perturbation.
