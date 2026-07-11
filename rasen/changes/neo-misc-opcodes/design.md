## Context

The Neo interpreter (`ExecuteNeo`, `ILIntepreter.Neo.cs`) is a register VM whose
CIL -> `OpCodeR` translation (`JITCompiler.cs`) and offset-lowering
(`Optimizer.Neo.cs` / `LowerNeoOffsets`) are largely complete for Steps 1-18.
Four independent opcodes nonetheless reach an unimplemented state in the full Neo
smoke (13 combined hits). Each is a small mirror of the Legacy `ExecuteR` arm
(`ILIntepreter.Register.cs`); none requires a new JIT path. The shared risk across
all four is mis-reading the `OpCodeR` field encoding, so each arm is gated behind
an empirical diagnose-first step (the child-1..6 stash-toggle discipline).

The Neo object model has no `StackObject`/`ObjectTypes.StaticFieldReference`
concept (Legacy's `Ldsflda` representation). Addresses are 8-byte byrefs
`(objIdx, off|flag)` consumed by `Stind_*`/`Ldind_*`/`Stobj`/`Ldobj`/
`CopyNeoCallArguments`. This is the load-bearing constraint for the `Ldsflda`
design.

## Goals / Non-Goals

**Goals:**
- Implement all four `ExecuteNeo` arms with Legacy-identical semantics.
- One FAULTING NeoStep probe per opcode (HEAD throws an uncaught NIE; applied
  passes with the correct value).
- Zero Legacy behavior change; no JIT change; no new dependency.

**Non-Goals:**
- CLR-static-field `Ldsflda` end-to-end (deferred with a tagged NIE unless a live
  hit is found -- the no-heap-object CLR-static shape needs its own byref
  sentinel + consumer arms; out of scope for this batch, mirrors the child-4
  deferred-shape discipline).
- The broader unimplemented-opcode surface (children 5/6/7 of the portfolio handle
  other gaps; this child is exactly these 4 opcodes).

## Decisions

### D1. Conv_R_Un -- direct conv-cluster arm, UNSIGNED read, double result
`conv.r.un` converts an unsigned integer to `F`. Confirmed: the opcode is fully
Neo-lowered -- `Operand2` = `InferPrimTag(source)` is stamped
(`JITCompiler.cs:975`), `GetConvResultType(Conv_R_Un)` returns `DoubleType`
(`JITCompiler.cs:1466`), and `LowerNeoOffsets` visits it (`Optimizer.Neo.cs:589`,
grouped with `Conv_R4`/`Conv_R8`). So the arm reads `ip->SrcOffset` + the
`(NeoPrimitiveTypeTag)ip->Operand2` tag and writes `ip->DstOffset`, exactly like
its siblings.
- **Why double, not float**: `GetConvResultType` makes the dest slot 8 bytes; a
  4-byte `float` write would leave the high dword stale. Legacy's `Conv_R_Un`
  (`Register.cs:1258-1294`) writes `Double` for 64-bit sources and `Float` for
  32-bit, but Neo's dest width is fixed by the register allocator at 8 bytes, so
  the arm always produces `double` (the typical C# lowering `(double)(uint)x`
  emits `conv.r.un; conv.r8`, so the intermediate is consumed as R8 anyway).
- **Why UNSIGNED**: the `.un` suffix. Reuse the existing `ReadConvU4`/`ReadConvU8`
  helpers (return `uint`/`ulong`); do NOT use the signed `ReadConvI4`/`ReadConvI8`.
- Alternative considered: tag on the source width to pick float-vs-double. Rejected
  (dest slot is fixed 8 bytes; mismatched width corrupts).

### D2. Switch -- identical to Legacy, diagnose the index-value field
The jump table already exists at runtime: `method.JumpTablesRegister[ip->Operand]`
(populated by `PrepareJumpTable`; targets remapped by `LowerNeoOffsets`
`Optimizer.Neo.cs:1728`). The arm logic is byte-for-byte Legacy
(`Register.cs:2754-2764`): read index, bounds-check `[0, table.Length)`,
`ip = ptr + table[idx]; continue;` else fall through (break, no jump).
- **Open detail (diagnose-first)**: `DstOffset` and `Register1` share the same
  bytes (`OpCode.cs:46-47`, `DstOffset`==`Register1` reinterpreted as `ushort`
  byte offset after `LowerNeoOffsets`). Whether `Switch`'s index-value operand is
  read via `*(int*)(frameBase + ip->DstOffset)` (offset form) or needs
  `ip->Register1` (register form) is confirmed by a 3-case `switch(int)` probe --
  the arm reads the value that makes the probe select the right case.
- Alternative considered: assume offset form. Rejected -- the verify is one stash
  toggle and avoids a silent wrong-case jump.

### D3. Unbox-of-enum -- add the `!isEnumObj` guard Legacy has
Root cause confirmed by code: the Neo Unbox CLR-primitive branch
(`ILIntepreter.Neo.cs:4390-4394`) hands `obj` straight to
`NeoWritePrimitiveToFrame`, which throws at `:6339` when `obj.GetType()` is
`ILEnumTypeInstance`. Legacy guards this: `Register.cs:4046`
`if ((t is CLRType) && clrType.IsPrimitive && !isEnumObj)`, and the enum case is
served by `Register.cs:4129 if (res is ILEnumTypeInstance)
res.CopyToRegister(0, ...)`.
- **Fix**: before the `clrUnboxType.IsPrimitive` primitive write, branch on
  `obj is ILEnumTypeInstance` and write the enum's underlying value into
  `frameBase + ip->DstOffset` (the dest primitive slot).
- **Open detail (diagnose-first)**: where the ILEnumTypeInstance's value lives.
  `ILEnumTypeInstance`'s Neo ctor (`ILTypeInstance.cs:101-104`) allocates
  `fields = new byte[underlyingSize]`; but the Box arm (`ILIntepreter.Neo.cs:3546`)
  and the ILType-enum Unbox arm (`:4357`) read/write `ins.Primitives`. Confirm
  which is authoritative at unbox time (prefer `Primitives` to match the existing
  ILType-enum Unbox arm at `:4352-4359`; fall back to `fields`). Either way the
  value is the enum's underlying primitive bytes -> read as the dest CLR type.
- Alternative considered: register a CLR representation for IL enums. Rejected --
  the value is already materialized; this is a one-branch extraction.

### D4. Ldsflda -- materialize the IL StaticInstance as a byref (reuse consumers)
Neo has no `StaticFieldReference`. A byref is `(objIdx, off|flag)`. The `Stind_*`
arms (`ILIntepreter.Neo.cs:4863-4906`) and `Ldind_Ref` (`:5143-5165`) ALREADY
dispatch `mStack[objIdx] is ILTypeInstance` -> `ins.Primitives[off]` (primitive
field) / `ins.ManagedObjects[off]` (reference field). So:
- For an **IL static field**: `mStack.Add(ilType.StaticInstance)`; resolve the
  static field's `PrimitiveOffset` (primitive field) or `ReferenceOffset`
  (reference field) via the same math Neo `Ldsfld` uses; emit byref
  `(mStack.Count - 1, fieldOffset)`. Then a following `stind.i4 42` writes
  `StaticInstance.Primitives[off] = 42` -- i.e. the static field write -- with NO
  consumer change. Reference-typed statics use the field's `ReferenceOffset`
  (mirror the `Ldflda` `heapIlRefFieldMarker` path).
- For a **CLR static field**: there is no heap object to address
  (`FieldInfo.GetValue(null)`/`SetValue(null, v)`); the object-field consumer arms
  cannot resolve it. Decision: **defer with a tagged NIE** ("Neo Ldsflda: CLR
  static field address deferred (follow-up)") UNLESS diagnose-first shows a live
  CLR-static hit, in which case add a dedicated static-field byref sentinel + a
  consumer arm in a follow-up (not this batch). This mirrors child-4's honest
  deferred-shape tagging.
- **Encoding** (confirmed, `JITCompiler.cs:2506-2510`): `Ldsflda` is emitted
  identically to `Ldsfld` -- `Register1`=dest, `OperandLong` =
  `GetStaticFieldIndex(...)` = `(typeHash<<32)|fieldHash` (child-3 finding). Decode
  `typeHash=(int)((ulong)OperandLong>>32)`, `fieldHash=(int)OperandLong`. **Do not
  stamp `Operand3`** -- it aliases `OperandLong`'s high dword (child-2).
- **Open details (diagnose-first)**: (a) the IL-vs-CLR split of the 5 hits;
  (b) the consumer pattern -- indirect (`Stind`/`Ldind`) vs byref method-arg
  (`CopyNeoCallArguments`, e.g. `Interlocked.Increment(ref sf)` /
  `int.TryParse(s, out sf)`); the materialized-StaticInstance byref must be
  recognized by whichever consumer the hits exercise (verify
  `CopyNeoCallArguments` resolves a `(mStackIdx, primOff)` byref into a heap-IL
  field copy -- if not, extend it); (c) the static field offset resolution (reuse
  Neo `Ldsfld` IL-static offset math).
- Probe-trigger caveat: C# may lower `ref staticField` without emitting `ldsflda`
  (e.g. via a direct `stsfld`). The probe must be written to actually emit
  `ldsflda`; the reliable triggers are a `ref int` helper invoked as
  `Set(ref Sf, v)` or an `out`/Interlocked pattern -- confirm in IL during
  diagnose-first.

## Risks / Trade-offs

- **[Switch index field mis-read]** -> silent wrong-case jump (the probe would
  pass by luck on the value 1). Mitigation: the probe exercises a DISTINCT value
  per case AND an out-of-range default case; diagnose-first confirms the field.
- **[ILEnumTypeInstance value storage]** -> reading the wrong backing array
  (`Primitives` vs `fields`) yields a stale/zero value. Mitigation: diagnose-first
  dump of the enum's value bytes from both arrays; the probe asserts the exact
  enum constant (`E.B == 2`), not just "ran".
- **[Ldsflda byref not recognized by `CopyNeoCallArguments`]** -> a `ref static`
  method arg would still NIE after the arm. Mitigation: diagnose-first exercises
  the actual consumer; if the indirect `Stind`/`Ldind` path is what the 5 hits use
  (most likely), no consumer change is needed. CLR-static shape is deferred, not
  silently broken.
- **[OpCodeR field aliasing]** -> stamping `Operand3`/`Operand2` while
  `OperandLong` is in use clobbers the field path (child-2 landmine). Mitigation:
  the `Ldsflda` arm ONLY reads `OperandLong` and writes the frame byref slots; no
  `OpCodeR` field is stamped.
- All edits are `#if ENABLE_NEO_MODE` -> [Legacy regression] risk is nil.

## Migration Plan

None. Neo-gated additive arms; no API, persistence, or wire-format change.
Rollback = revert the single commit (the 4 arms + probe file are isolated).

## Open Questions

All four are settled empirically by the diagnose-first tasks (D2 field form, D3
value storage, D4 IL/CLR split + consumer + offset). No product-level unknowns.
