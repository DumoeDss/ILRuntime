## Context

F-6 / NEO-VT-FLDADDR (`neo-deferred-items.md` §3): `ldflda` of a field of an
in-frame IL value type is broken when the operand slot holds **flat bytes**, not
a Ref Slot. Surfaced by the `neo-step17-completion` apply phase — the IL-struct
`ToString()` override probe (whose body calls `id.ToString()`, lowering to
`ldflda this.id; constrained.callvirt Int32.ToString`) returned a wrong result,
so the Step-17 keeper was swapped for an interface direct-call probe. The
constrained DISPATCH is correct (delivered by neo-step17-completion); only the
`ldflda` lowering on the flat-bytes operand is broken.

**Current code (HEAD `7077ea42`, code-grounded):**

- The JIT `Code.Ldflda` Translate (`JITCompiler.cs:2310-2328`) stamps
  `Operand` (type hash), `Operand2` (`field.PrimitiveOffset`),
  `Operand3` (`field.ReferenceOffset`). `Operand4` is **untouched** for
  `Ldflda` (standalone, offset 20 — does not alias any register/byte-offset
  union field, per the comment at `Optimizer.Neo.cs:552`).
- The JIT type-specialization pass `case OpCodeREnum.Ldflda:`
  (`JITCompiler.cs:798-804`) already detects the in-frame-VT source
  (`GetRegisterType(registerTypes, op.Register2) is ILType srcIl &&
  srcIl.IsValueType && !srcIl.IsEnum`) and seeds the dest type. It stamps NO
  marker today.
- The runtime `case OpCodeREnum.Ldflda:` arm (`ILIntepreter.Neo.cs:827-857`)
  reads `objIdx = *(int*)(frameBase + operandSlotOff + 0)` and dispatches:
  `objIdx == -1` → frame-native branch (operand slot holds a Ref Slot produced
  by a real `ldloca`/`ldarga`); `objIdx >= 0` → heap-IL/CLR-object branch
  (treats `objIdx` as an mStack index).
- The `addrAlias` folding (`Optimizer.Neo.cs:65-81`) folds `ldflda` dests whose
  every consumer is foldable; the COEXIST gate (`:83-150`) makes a dest real
  when its address escapes (consumed by `stind`/`ldind`/`stobj`/`ldobj`/`ldelema`
  / a byref `Call`/`Newobj`/`Push` arg / a `constrained.` box path). A real dest
  fires the runtime arm.

**The defect, dump-confirmed at propose.** The runtime arm assumes the operand
slot ALWAYS holds a Ref Slot (8 bytes: `objectIndex`, `offset`). This holds for
two operand shapes:

1. `ldloca V; ldflda f` — the `ldloca` dest holds a frame-native Ref Slot
   `(-1, V_offset)`. `objIdx == -1` → frame-native branch → correct. **VERIFIED
   PASS on HEAD** (`BumpByTen(ref s.x)` with `s` a frame local: the diagnostic
   read `objIdx=-1`).
2. A struct instance method called via a **direct `call`** (`s.M()` lowering to
   `ldloca s; call M`) — the Step-17 byref-`this` call-ABI seeds the callee's
   param slot 0 with a frame-native Ref Slot `(-1, s_offset)`. So inside `M`'s
   body, `ldflda this.field` reads `objIdx == -1` → correct. **VERIFIED PASS on
   HEAD** (`s.ReadIdViaAddress()` doing `return ReadRef(ref id)`: diagnostic read
   `objIdx=-1`).

But a THIRD operand shape produces **flat bytes** at the operand slot, NOT a Ref
Slot:

3. A struct instance method invoked via **`constrained.callvirt` box-once**
   (e.g. `s.ToString()` on a struct override) — the constrained dispatch boxes
   the IL struct into an `ILTypeInstance` (the box-once path) and dispatches the
   override. The override's `this` (param slot 0) is sized as the in-frame value
   (`Size = TotalPrimitiveSize`) and seeded with the struct's **flat primitive
   bytes** (the box's `Primitives` copied in, NOT a Ref Slot). So inside the
   override body, `ldflda this.field` reads `objIdx = <first field's value>`
   (e.g. `42` for `id=42`) → the `>= 0` branch fires → garbage Ref Slot
   `(42, 0)` → the consumer (`ldind`/`stind`/`constrained.callvirt`) reads
   `mStack[42]` as an ILTypeInstance → wrong result / crash. **VERIFIED FAIL on
   HEAD** (`s.ToString()` returning `"Named:" + id.ToString()`: diagnostic read
   `objIdx=42`, test fails). This is the load-bearing reproducer.

The runtime arm CANNOT distinguish shape (1/2) "operand holds a Ref Slot" from
shape (3) "operand holds flat bytes" by inspection — both are 8+ bytes at the
same frame offset, and the leading int is `-1` for (1/2) but a field value for
(3). A **JIT-side marker** is required.

**Legacy reference.** `ILIntepreter.Register.cs:3254-3287` `Ldflda` arm
discriminates via `GetObjectAndResolveReference(reg2)` +
`objRef->ObjectType == ObjectTypes.ValueTypeObjectReference` — a tagged
representation, not "read the bytes and guess." Legacy is NOT modified.

Pre-requisite machinery (all shipped): Step 12 in-frame VT layout + `_Inline`
field opcodes; Step 17 unified 8-byte Ref Slot + `addrAlias` COEXIST gate; area4
byref-`this` direct-call (seeds slot-0 Ref Slot for shape 2);
neo-step17-completion constrained box-once (produces shape 3); VT-THIS-ADDR
in-frame-VT typing. The `neo-byref` spec's "ldflda / ldarga address producers"
requirement ALREADY SPECIFIES the marker ("The optimizer SHALL stamp a marker
(e.g. `Operand4`) on a real `ldflda` so the arm distinguishes the in-frame-VT
case from the heap-IL case") — it was simply never implemented for the flat-
bytes operand.

## Goals / Non-Goals

**Goals:**
- `ldflda` of a field of an in-frame IL value type produces a correct frame-
  native Ref Slot `(-1, vtBase + field.PrimitiveOffset)` REGARDLESS of whether
  the operand slot holds a Ref Slot (shape 1/2) or flat bytes (shape 3).
- The IL-struct `ToString()` override whose body calls `id.ToString()` (the
  Step-17 deferred positive test) works end-to-end on Neo.
- The existing operand shapes (heap-IL `ldflda`, CLR-object `ldflda`,
  ldloca-Ref-Slot `ldflda`) are byte-identical.
- Full `NeoStep` smoke stays green (146/146 baseline); Legacy-neutral.

**Non-Goals:**
- `ldflda` on a CLR value type's field from inside an IL method body (the
  `[NEO-IL-VT-INSTANCE-COVERAGE]` Step-6 `Ldfld`-on-CLR-struct gap — separate).
- `fixed` to a struct field of an unmanaged type that requires genuine pinning
  beyond the Ref-Slot model (the Step-17 `fixed` deferral —
  `neo-step17-stobj-refloop`).
- `stobj`/`ldobj` ref-slot loop for a VT with reference fields (the Step-17
  deferral — `neo-step17-stobj-refloop`).
- `Ldfld_Value` (whole-VT-field load) — separate Step-12b deferred item
  (surfaced by the nested reproducer probe; out of scope).
- Generic-byref / interface-on-VT-constrained beyond the common shape (Step-17
  deferrals).

## Decisions

### D1. The marker: stamp a flag bit in `Operand4` for an in-frame-VT operand

In the JIT type-specialization pass `case OpCodeREnum.Ldflda:`
(`JITCompiler.cs:798-804`), when the source `Register2` is an in-frame IL value
type (the SAME condition that already seeds the dest type), ALSO stamp a marker
flag bit on `op.Operand4`. The marker indicates "the operand of this `ldflda`
is an in-frame VT (its slot may hold flat bytes); produce the frame-native Ref
Slot from the operand slot's frame byte offset directly."

Rationale:
- The type-spec pass runs PRE-lowering, so `op.Register2` (the source register
  index) is still available for `GetRegisterType`. After `LowerNeoOffsets`,
  `Register2` is overwritten with the source byte offset (`SrcOffset`) — too
  late to type-check. So the marker MUST be stamped in the type-spec pass (not
  in `LowerNeoOffsets`).
- `Operand4` is the standalone field at offset 20 (not aliased with any
  register or byte-offset union field — `Optimizer.Neo.cs:552`). It is currently
  UNUSED for `Ldflda`, so stamping it is collision-free. Other opcodes use
  `Operand4` for their own purposes (`Move_Vt` = dst ref count; `Ldelem`/
  `Stelem`/`Ldelema` = index/value byte offset; `Constrained`-callvirt flag
  `0x1`); `Ldflda`'s usage is opcode-scoped, so no conflict.
- A flag BIT (e.g. `0x1` in `Operand4`) is preferred over consuming the whole
  field, leaving room for future `Ldflda` operand metadata. Pick the exact bit
  during apply (confirm `0x1` does not collide with any existing `Ldflda`
  `Operand4` write — there is none today).

REJECTED alternatives:
- **Read the operand slot and heuristic-detect a Ref Slot vs flat bytes.**
  Unsound: `-1` is a valid field value (e.g. `int x = -1`), and a field value
  of `-1` would mis-trigger the frame-native branch. The marker is the only
  sound discriminator (mirrors Legacy's tagged `ObjectTypes`).
- **Make the constrained box-once seed slot-0 as a Ref Slot instead of flat
  bytes.** This would change the override's `this` representation, breaking the
  `_Inline` field accesses in the override body (which read slot-0 as flat
  bytes via the VT-THIS-ADDR in-frame-address root). The constrained box-once
  deliberately seeds flat bytes so the override's `_Inline` `this.field` reads
  work; the fix belongs in `ldflda`, not the box-once.
- **Always produce a frame-native Ref Slot when the type-spec pass typed the
  dest as an in-frame VT (no marker), keying the runtime arm on the dest type.**
  The runtime arm has no access to the dest type (it operates on raw byte
  offsets post-lowering). The marker is the carrier.

### D2. Runtime arm: consult the marker, add the flat-bytes branch

The runtime `case OpCodeREnum.Ldflda:` arm
(`ILIntepreter.Neo.cs:827-857`) gains a marker check BEFORE the existing
`objIdx` dispatch:

```
if ((ip->Operand4 & LDFLDA_INLINE_MARKER) != 0)
{
    // Operand slot holds the in-frame VT's flat bytes (shape 3), OR a Ref Slot
    // (shape 1/2). Either way, the operand slot's FRAME BYTE OFFSET IS the
    // struct's base. Produce a frame-native Ref Slot directly.
    *(int*)(frameBase + dst + 0) = -1;
    *(int*)(frameBase + dst + 4) = operandSlotOff + fieldPrimOff;
}
else
{
    // existing dispatch: objIdx == -1 -> frame-native; >= 0 -> heap/CLR.
}
```

Key insight (the marker is sound for BOTH shape 1/2 AND shape 3): for shape 1/2
the operand slot holds a Ref Slot `(-1, V_offset)`, and the struct base IS
`V_offset` = `operandSlotOff`'s referent... NOT `operandSlotOff` itself. WAIT
— this is the one subtlety. For shape 1/2, `operandSlotOff` is the
`ldloca`/`this` TEMP slot holding the Ref Slot; the struct base is the Ref
Slot's `offset` half (`*(frameBase + operandSlotOff + 4)`). For shape 3,
`operandSlotOff` IS the struct base (the slot holds flat bytes). So the marker
branch MUST differ by shape.

**Resolution (D2-refined): the marker means "operand is an in-frame VT"; the
arm STILL reads the leading int to distinguish Ref-Slot-vs-flat-bytes, but with
the marker's guarantee that a non-(-1) leading int is a FIELD VALUE (shape 3),
not an mStack index.** Concretely:

```
if ((ip->Operand4 & LDFLDA_INLINE_MARKER) != 0)
{
    int leadingInt = *(int*)(frameBase + operandSlotOff + 0);
    if (leadingInt == -1)
    {
        // Shape 1/2: operand slot holds a frame-native Ref Slot. Resolve
        // through it (existing frame-native branch).
        int vtBase = *(int*)(frameBase + operandSlotOff + 4);
        *(int*)(frameBase + dst + 0) = -1;
        *(int*)(frameBase + dst + 4) = vtBase + fieldPrimOff;
    }
    else
    {
        // Shape 3: operand slot holds the struct's flat bytes. The slot's
        // frame byte offset IS the struct base.
        *(int*)(frameBase + dst + 0) = -1;
        *(int*)(frameBase + dst + 4) = operandSlotOff + fieldPrimOff;
    }
}
else
{
    // existing non-marker dispatch (heap-IL / CLR-object operand).
}
```

This is sound: with the marker set, `leadingInt == -1` unambiguously means
"Ref Slot" (shape 1/2), and any other value unambiguously means "flat bytes"
(shape 3) — because the marker guarantees the operand is an in-frame VT, so a
non-(-1) leading int cannot be an mStack index (the heap/CLR path is
non-marker). CONFIRM at apply via the reproducer dump (`objIdx=42` for shape 3;
`objIdx=-1` for shape 1/2).

### D3. addrAlias folding unchanged

The `addrAlias` folding + COEXIST gate (`Optimizer.Neo.cs:25-150`) already
handles `ldflda` dests: a dest whose every consumer is foldable stays folded
(runtime arm dead); a dest whose address escapes stays real (runtime arm
fires). The marker does NOT change folding — it only disambiguates the runtime
arm when the arm DOES fire. So no `addrAlias` change. CONFIRM at apply that the
marker-stamping in the type-spec pass does not perturb the folding (the type-
spec pass runs before `addrAlias`; the marker is on `Operand4`, which the
folder does not read).

### D4. Edge cases

- **Nested struct field (`ref outer.inner.x`):** the C# compiler lowers to
  `ldloca outer; ldflda inner; ldflda x` (a chain). Each `ldflda` is typed
  in-frame; the chain folds via `addrAlias`. If the address escapes, the leaf
  `ldflda` fires with the accumulated offset. The marker on the leaf `ldflda`
  indicates the operand (the inner `ldflda` dest) is an in-frame VT. Probe
  REQUIRED. NOTE: a `Ldfld_Value` (whole-nested-VT load) may intervene — if so,
  the nested probe hits the separate Step-12b `Ldfld_Value` NIE; scope the
  probe to a nested struct accessed only via address (no whole-VT load).
- **Reference-type field (`ref s.objField`):** the field's `PrimitiveOffset` is
  in the primitive region but the field lives in the ref region. The produced
  Ref Slot's `offset` half points at the primitive-region slot; the consumer
  (`ldind.ref`/`stind.ref`) reads/writes the mStack ref slot. Probe REQUIRED
  (the ref-region sub-case).
- **Register reuse / escape (Step-17-B1 class):** an `ldflda`-produced byref
  whose dest register is reused by an intervening op, then the byref is read.
  The `liveAliasMap` per-instruction snapshot already handles this for
  `ldloca`; the marker does not change the live-range logic. Probe REQUIRED.
- **Heap-IL `ldflda` regression:** the marker is NOT stamped for a heap-IL
  operand (the type-spec pass only stamps when the source is an in-frame VT),
  so the existing heap branch fires unchanged. Probe REQUIRED (byte-identical).
- **CLR-object `ldflda` regression:** same — the source is a CLR reference
  type, not an IL VT, so no marker; existing CLR-object branch unchanged.
  Probe REQUIRED.
- **`fixed` to a struct field:** if reachable, lowers to `ldflda` + the fixed
  pattern; the marker branch handles the address. May hit the Step-17 `fixed`
  deferral — scope OUT if it does.

## Risks / Trade-offs

- **[Runtime `Ldflda` arm shared by every `ldflda` — heap-IL, CLR-object,
  Ref-Slot, flat-bytes]** → Mitigation: the marker gates the new branch; the
  existing branches are byte-identical when the marker is absent. The marker is
  stamped ONLY when the type-spec pass proves the source is an in-frame IL
  value type, so a heap-IL/CLR-object operand never carries the marker. Full
  `NeoStep` smoke (146/146) + Legacy-neutral stash-toggle. Adversarial probes
  MANDATORY (the flat-bytes-vs-Ref-Slot ambiguity is the corruption class a
  green smoke can miss).
- **[`Operand4` collision with a future/present `Ldflda` use]** → Mitigation:
  `Operand4` is currently UNUSED for `Ldflda` (verified at HEAD). A flag BIT
  (`0x1`) leaves the remaining bits free. Confirm at apply via a `grep` for
  `Operand4` writes in the `Ldflda` Translate / lowering paths (there are
  none today).
- **[Nested-field `ldflda` chain + `Ldfld_Value` interaction]** → Mitigation:
  scope the nested probe to address-only access (no whole-VT load). If the C#
  compiler emits `Ldfld_Value` for the chosen shape, adapt the probe (the
  `Ldfld_Value` Step-12b gap is out of scope; D5 non-goal).
- **[Constrained-boxed-`this` shape is the ONLY flat-bytes operand today]** →
  Mitigation: the marker is general (any in-frame-VT operand), so future
  producers of flat-bytes operands are covered automatically. The reproducer
  exercises the constrained-boxed-`this` shape (the load-bearing one).
- **[Shared-pass (FCP/BCP/copy-prop) perturbation]** → Mitigation: the marker
  is on `Operand4` (standalone), not on a register/offset union field, so
  offset-lowering + copy-prop do not read or clobber it. Low expected risk;
  probe during apply, add a `#if ENABLE_NEO_MODE` guard only if a reproducer
  fails.

## Open Questions

- Exact marker bit (`0x1` in `Operand4`) — confirm no collision at apply (there
  is no existing `Ldflda` `Operand4` write, so any bit works; `0x1` mirrors the
  `Constrained`-callvirt `0x1` flag convention).
- Does the constrained box-once ALWAYS seed the override's slot-0 with flat
  bytes, or are there sub-shapes (e.g. IL-VT + inherited CLRMethod box-into-
  ILTypeInstance, vs IL-VT + ILMethod override direct-call) that seed
  differently? The direct-call shape (ILMethod override) seeds a Ref Slot
  (shape 2 — already works); the box-once shapes seed flat bytes (shape 3 —
  the gap). Confirm via the reproducer dump for both sub-shapes at apply.
- Does any `NeoStep` smoke case (or the broader suite) currently AVOID the
  struct-`ToString()`-overrides-calling-field-methods pattern that would turn
  green with this fix? (Probe at verify; the IL-struct `ToString` override is
  the high-value green-up — the Step-17 deferred positive test.)

## Apply-phase findings (2026-07-06)

**RESOLVED.** F-6 / NEO-VT-FLDADDR fixed. The marker is
`JITCompiler.NeoLdfldaInlineMarker = 0x1` (bit 0x1 of the standalone
`Operand4`, offset 20). DUMP-GATE confirmed (Risk 2): `Operand4` is untouched
for `Ldflda` everywhere at HEAD -- NOT in `Code.Ldflda` Translate
(`JITCompiler.cs:2310-2328`, writes only Operand/Operand2/Operand3), NOT in
the type-spec `case Ldflda:` (only seeded the dest type), NOT in any optimizer
`Ldflda` site (`Optimizer.Neo.cs:65/132/176/853/1323`, `Optimizer.Utils.cs`
helpers, `Optimizer.FCP.cs:182/350`). Bit 0x1 mirrors the existing
`Constrained`-callvirt `Operand4 |= 0x1` convention. Stamped via OR
(`op.Operand4 |= NeoLdfldaInlineMarker`), leaving the upper bits free.

**Edit sites (all confirmed, working tree UNCOMMITTED; all Neo-only):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- (a) the named
  const `NeoLdfldaInlineMarker = 0x1` next to `CallRegisterParamCount`; (b)
  `case OpCodeREnum.Ldflda:` in `TypeSpecializeNeoOpcodes` stamps the marker
  when the source is an in-frame IL value type (the SAME condition that seeds
  the dest type). `TypeSpecializeNeoOpcodes` is called under
  `#if ENABLE_NEO_MODE` (`:517`), so the stamp is Neo-only.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- the
  `case OpCodeREnum.Ldflda:` arm gains a 3-way dispatch: objIdx == -1 ->
  frame-native Ref Slot (resolve through the offset half; shape 1/2, marker or
  not); else if inlineMarker -> shape 3 flat-bytes (struct base =
  operandSlotOff); else -> existing heap/CLR (objIdx, fieldPrimOff). The
  marker branch reads the leading int FIRST (objIdx), then keys on it -- a
  non-(-1) leading int with the marker set is a FIELD VALUE (shape 3), never an
  mStack index (the heap path is non-marker). This matches design D2-refined.
- `TestCases/NeoStep17Test.cs` -- 8 adversarial keeper probes
  (`NeoStep17_LdfldaInline_*`).

**No shared-pass change.** The marker is on the standalone `Operand4`, which
FCP/BCP/copy-prop/RegisterCleanup do not read or clobber for `Ldflda`. The
addrAlias folding (D3) is unchanged -- the marker only disambiguates the
runtime arm; an ldflda-produced address is still an addrAlias root per
Step 12/17.

**Load-bearing stash-toggle proof (F-6 shape 3).** The probe
`NeoStep17_LdfldaInline_StructMethodFlatBytes` exercises the flat-bytes operand
shape: an IL struct `ToString()` override invoked via a generic constrained
caller (`CallToStringConstrained<T>(T v) where T : struct` -> `v.ToString()`),
whose body takes `ref id` via ldflda and passes it to an IL byref helper
(`ReadViaRef(ref id)` -- NOT a CLR method, see the deviation below). The
runtime diagnostic confirmed objIdx=42 slotOff=0 fieldPrimOff=0 (slot-0 holds
the struct flat bytes; id=42 read as the leading int). On HEAD (fix stashed)
the probe FAILS with `Index was out of range` (the ldflda reads 42 as an mStack
index -> mStack[42] OOB). With the fix, the marker branch produces (-1, 0)
(frame-native, offset 0 = the struct base = where id flat bytes live) and the
probe PASSES. The 7 regression-guard probes (4.1, 4.2, 4.4-4.8) PASS on BOTH
HEAD and HEAD+fix (the marker is absent for shape 1/2/heap/CLR operands; the
existing branches are byte-identical).

**DEVIATION from design task 4.3 (the reproducer body).** The design literal
reproducer body was `return "Named:" + id.ToString();` (the override calls
`id.ToString()` directly). The shipped probe instead routes the ldflda-produced
address through an IL byref helper (`ReadViaRef(ref id)`), chosen to isolate
the ldflda correctness from the unrelated CLR-call path. **NOTE (review
softening, 2026-07-06):** the original apply-phase rationale claimed the literal
body was BLOCKED by a separate F-3 / NEO-BYREF-THIS callvirt-on-CLR-struct gap
(`Int32.ToString()` receiving the frame-native byref as `this` and reading 0).
Independent reconstruction during review (F-R1) shows that claimed interaction
DOES NOT REPRODUCE -- the literal `id.ToString()` body (with `id` an `int`
field) PASSES in BOTH configurations (with the fix AND with the fix stashed):
for an `int` field the C# compiler emits a by-value `ldfld` + a value-`this`
`call Int32.ToString()`, NOT a `ldflda` + byref-`this` call, so no frame-native
byref is ever passed to the CLR method and the F-3 gap is never engaged. F-6
correctness is unaffected. The `ReadViaRef` probe is a valid reproducer that
isolates the ldflda correctness cleanly; the literal body was avoided out of
caution / probe-isolation preference, NOT because of a real F-3 gap. A future
Step-17 D-CONSTRAINED follow-up need NOT chase a literal-`id.ToString()` gap
here.

**Probe 4.3 call-shape subtlety (earned).** A NON-virtual instance method call
(`s.ReadIdViaLdflda()` direct on a local) does NOT produce shape 3 -- the C#
compiler emits ldloca; call (direct call), so the callee slot-0 holds a
frame-native Ref Slot (shape 2, objIdx == -1), which already works. Shape 3
(flat bytes at slot-0) is produced ONLY by constrained.callvirt box-once,
which the C# compiler emits for virtual overrides (`s.ToString()` on a struct
override -> constrained.callvirt Object.ToString). So the load-bearing probe
MUST dispatch via a constrained caller. (An interface-method dispatch on an
IL-struct-via-box-once was ALSO attempted but hits the separate
`ResolveNeoCallvirtInterfaceTarget` struct-does-not-implement-interface gap --
the `[NEO-IL-VT-INSTANCE-COVERAGE]` Step-6/11 family; not F-6.)

**Probe 4.4 (nested field) -- green, address-only.** `ref outer.inner.x` via a
chain works; the probe accesses the leaf field only via address (no whole-VT
load), so it does NOT hit the `Ldfld_Value` Step-12b NIE. The chain folds via
addrAlias; when the address escapes (the byref param), the leaf ldflda fires
with the marker (the inner struct is an in-frame VT) and produces the correct
offset.

**Probe 4.5 (reference-type field) -- green.** `ref s.h` (a `NeoStep17Holder`
field) -- the field lives in the ref region; the ldflda-produced address points
at the primitive-region slot, and the consumer byref helper reads/writes the
mStack ref slot. Works with the marker (the struct is an in-frame VT with a ref
field).

**Probe 4.6 (register-reuse / escape, the Step-17-B1 class) -- GREEN.** An
ldflda-produced byref whose dest register is reused by an intervening unrelated
byref (`LdfldaReuseRead(ref x)`), then the ldflda-produced byrefs are re-read.
The addrAlias COEXIST gate keeps the field-address ldfldas real; the marker
branch yields the FRESH values (7, 8), not stale. No silent corruption observed
-- the per-instruction `liveAliasMap` snapshot (Step 17 B1 fix) handles the
reuse correctly and the F-6 marker does not perturb it.

**Probe 4.8 (CLR-object regression) -- scoped to a byref-of-primitive.** A
literal `ldflda` on a CLR object field would hit the Step-17 CLR-field-hash
stind/ldind deferral. The probe instead exercises the non-marker heap branch
via a byref of a CLR-primitive local (`ReadPointX(ref n)`), proving the
non-marker path is byte-identical. The CLR-object-field `ldflda` is left to the
Step 17 stind/ldind follow-up (out of scope).

**Verification.** NeoStep smoke 154/154 (146 baseline + 8 new probes), all
green. K5 (`NeoStep17_ConstrainedIlVtOverrideStillWorks`) still green (it
exercises the marker via the existing `NeoStep17Named` path -- the marker is
now correctly stamped for its ldflda too). Plain `Debug` CLI builds clean (0
errors; all changes Neo-only -- the marker stamp is under
`#if ENABLE_NEO_MODE` via `TypeSpecializeNeoOpcodes`; the runtime change is in
the Neo-gated `ILIntepreter.Neo.cs`; the named const is a harmless `public
const int` never referenced in Legacy). Stash-toggle: probe 4.3 FAILS on HEAD
(Index out of range), PASSES with the fix; the 7 regression guards PASS on
BOTH.

**Did NOT git commit/push** (per process discipline; LEAD commits after review).
