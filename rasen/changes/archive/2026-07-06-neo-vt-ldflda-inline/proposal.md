## Why

`ldflda` of a field of an **in-frame IL value type whose operand slot holds
flat bytes** (not a Ref Slot) mis-reads the operand: the runtime `Ldflda` arm
treats the operand slot's first 4 bytes as a Ref-Slot `objectIndex`, but for an
in-frame VT those bytes are the struct's primitive field data → garbage. This is
the F-6 / NEO-VT-FLDADDR gap surfaced by the `neo-step17-completion` apply phase
(the IL-struct `ToString` override probe) and tracked in
`.trae/documents/neo-deferred-items.md` §3. It blocks every IL-struct method
body that takes a field address when the `this` arrives as flat bytes — most
prominently a struct `ToString()` override whose body calls `id.ToString()`
(which the C# compiler lowers to `ldflda this.id; constrained.callvirt`), the
natural positive test that Step 17 had to swap out for an interface direct-call
probe. The constrained DISPATCH itself is correct (delivered by
neo-step17-completion); only the `ldflda` lowering on the flat-bytes operand is
broken. The fix is small, isolated, and unblocks the IL-struct field-address
idioms (`ref struct.field` read/write via the address, `fixed`, struct-method-
takes-field-address).

## What Changes

- The Neo JIT type-specialization pass (`TypeSpecializeNeoOpcodes`, the
  `case OpCodeREnum.Ldflda:` dest-typing rule in `JITCompiler.cs`) SHALL
  additionally stamp a marker on a real `Ldflda` opcode when its source operand
  (`Register2`) is an in-frame IL value type, so the runtime arm can distinguish
  the in-frame-VT-flat-bytes operand from a Ref-Slot operand. The marker lives
  in a standalone (non-union-aliased) operand field (e.g. a flag bit in
  `Operand4`) so it survives `LowerNeoOffsets` intact.
- The Neo runtime `Ldflda` arm (`ILIntepreter.Neo.cs`) SHALL consult that marker:
  when the marker indicates an in-frame-VT operand, the arm SHALL produce a
  frame-native Ref Slot `(-1, operandSlotOff + field.PrimitiveOffset)` directly
  (the operand slot IS the struct's flat-byte region; its frame byte offset is
  the struct base). When the marker is absent, the arm SHALL keep the existing
  dispatch (read the operand slot as a Ref Slot: `objectIndex == -1` → frame-
  native; `objectIndex >= 0` → heap-IL / CLR-object).
- The `addrAlias` folding (`Optimizer.Neo.cs`) behavior for `ldflda` is
  unchanged: a `ldflda` dest whose every consumer is foldable stays folded
  (runtime arm dead); a dest whose address escapes the folding window stays real
  (runtime arm fires, now correctly for the in-frame-VT operand).
- Adversarial keeper probes SHALL be added to `TestCases/NeoStep17Test.cs`
  (`NeoStep17_*` names) covering: (1) `ref s.x` read; (2) `ref s.x` write; (3)
  `ldflda` on a nested struct field (`ref outer.inner.x`); (4) `ldflda` on a
  struct with a reference-type field (`ref s.objField` — the ref-region); (5)
  the IL-struct `ToString()` override whose body calls `id.ToString()` (the
  Step-17 deferred positive test, the load-bearing reproducer); (6) the
  register-reuse/escape probe (an `ldflda`-produced byref whose dest register is
  reused by an intervening op, then the byref is read — the Step-17-B1 silent-
  corruption class); (7) regression — heap-IL `ldflda` + CLR-object `ldflda`
  still produce byte-identical Ref Slots.
- Legacy (`ExecuteR`, `ILIntepreter.Register.cs`) is the SEMANTIC reference
  (its `Ldflda` arm uses `GetObjectAndResolveReference` +
  `ObjectTypes.ValueTypeObjectReference` to discriminate) and is NOT modified.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `neo-byref`: the existing "ldflda / ldarga address producers" requirement
  already SPECIFIES the marker ("The optimizer SHALL stamp a marker (e.g.
  `Operand4`) on a real `ldflda` so the arm distinguishes the in-frame-VT case
  from the heap-IL case") and the in-frame-VT operand behavior ("an in-frame
  value-type operand produces a frame-native Ref Slot `(-1, vtBase +
  field.PrimitiveOffset)`"). That requirement was DELIVERED for the
  ldloca/ldarga-produced Ref-Slot operand shape (the operand slot holds a Ref
  Slot from a real `ldloca`/`ldarga`), but the flat-bytes-operand shape (the
  operand slot holds the struct's primitive data, e.g. a constrained-boxed
  `this` in an IL-struct method body) was an unrealized gap. This change
  DELIVERS the marker stamping + the flat-bytes-operand branch so the requirement
  holds for BOTH operand representations.

## Impact

- **Code (all Neo-only; Legacy `ExecuteR` byte-identical):**
  - `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` — the
    `TypeSpecializeNeoOpcodes` `case Ldflda:` dest-typing rule (currently
    `JITCompiler.cs:798-804`): when the source is an in-frame IL value type,
    also stamp the in-frame-VT marker on the opcode. Plus a `Code.Ldflda`
    Translate consistency check if needed (the marker is stamped in the
    type-spec pass, which runs pre-lowering, so register indices are still
    available).
  - `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` — the
    `case OpCodeREnum.Ldflda:` arm (`ILIntepreter.Neo.cs:827-857`): consult the
    marker; add the in-frame-VT-flat-bytes branch that produces
    `(-1, operandSlotOff + fieldPrimOff)`.
  - Possibly `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` —
    confirm `LowerNeoOffsets` preserves the marker field (the standalone
    `Operand4` survives lowering; verify no pass clobbers it for `Ldflda`).
- **Tests:** `TestCases/NeoStep17Test.cs` (extend) — 7+ adversarial keeper probes
  (`NeoStep17_*`); the `NeoStep` filter catches them.
- **Specs:** `openspec/specs/neo-byref/spec.md` — the "ldflda / ldarga address
  producers" requirement MODIFIED delta (marker stamping + flat-bytes-operand
  branch DELIVERED); a new scenario for the flat-bytes operand (the constrained-
  boxed-`this`-in-IL-struct-method shape).
- **Deferred-items doc:** `.trae/documents/neo-deferred-items.md` F-6 entry →
  RESOLVED.
- **Regression risk: MEDIUM.** The runtime `Ldflda` arm is shared by every
  `ldflda` (heap-IL, CLR-object, ldloca-Ref-Slot, in-frame-VT-flat-bytes). The
  marker gates the new branch; the existing branches are byte-identical when the
  marker is absent. Gate: full `NeoStep` smoke (146/146 baseline) + Legacy-
  neutral stash-toggle. Adversarial probes MANDATORY (Step-17-B1 / OPT-HARDEN-K1
  / F-MAJ-1 lessons: a green smoke does NOT prove a runtime/lowering gate
  correct without a probe for the specific corruption class — here the
  flat-bytes-vs-Ref-Slot ambiguity).
