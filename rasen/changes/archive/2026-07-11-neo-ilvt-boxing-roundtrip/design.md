# Design — neo-ilvt-boxing-roundtrip

**Date:** 2026-07-11  **Capability:** neo-dispatch (JIT register-type tracking)  **Status:** DONE
**Branch:** features/object-model-overhaul

## The gap (re-audited with fresh eyes)

`T BoxUnbox<T>(T v){ object o = v; return (T)o; }` where T is an **IL struct**
failed even in a Cecil-loaded JIT run (the "A JIT" reference) — so it is a
general engine gap, NOT a Cecil-free / T-identity regression (confirmed by the
neo-aot-generic-tidentity ship-log scope note, which parked this exact case).

Surfacing context: `BoxUnbox<NeoStep25CegVal>` was omitted from the T-identity
capstone wrappers precisely because it "fails a Cecil-loaded JIT run too". This
change closes that parked gap.

### NOT a deep rework — a SPECIFIC register-type-tracking gap (the 9-for-9 lesson holds)

The Box/Unbox **execution arms** (Step 13, `ILIntepreter.Neo.cs`) are CORRECT
for an IL value type:
- `Box` IL-VT branch (`ilType.IsValueType`): `Instantiate(false)` +
  `CopyFrameToIL` (copies bytes + refs). Correct.
- `Unbox`/`Unbox_Any` IL-VT branch: `CopyILToFrame` (reads bytes + refs back
  into the dest frame). Correct.
- `CopyFrameToIL` / `CopyILToFrame` helpers: correct.

The gap is in the **JIT** (`JITCompiler.TypeSpecializeNeoOpcodes`): it never
typed the **dest register of an Unbox/Unbox_Any of an IL value type** as an
in-frame VT.

## Root cause

`TypeSpecializeNeoOpcodes` walks the register IR and rewrites a heap `Ldfld_*` /
`Stfld_*` opcode to its `_Inline` (frame-direct) variant ONLY when the field's
operand register carries an in-frame IL value-type type
(`TryRewriteFieldAccessForInline` keys on
`operandType is ILType ot && ot.IsValueType`). The per-register static types
start from `BuildInitialRegisterTypes` (locals/params are typed), then each
opcode's case propagates/overwrites them. Three in-frame-VT "address" cases
already stamp the dest register's VT type: `Ldloca`/`Ldloca_S`, `Ldflda`, and
`Newobj` (IL-VT). **Unbox/Unbox_Any was a fourth such case that was MISSING.**

Chain of failure for `BoxUnbox<IL-VT>` (the body is inlined):

```
unbox.any r14, r13, <IL-VT>     ; r14 is the dest -- NOT typed (gap)
move r11, r14                    ; Move copies srcType (null) into r11
move r2, r11                     ; r2 = local `r`; Move OVERWRITES r2's
                                 ;   initial IL-VT local type with null
ldfld.i4 r11, r2, ...            ; r2's tracked type is now null ->
                                 ;   TryRewriteFieldAccessForInline returns
                                 ;   false -> stays the HEAP Ldfld_I4
```

The heap `Ldfld_I4` arm does `GetNeoILInstance(mStack, *(int*)(srcOffset))` — it
reads the dest's first 4 frame bytes (the int field `n`, e.g. 77) as an mStack
index → `mStack[77]` → `ArgumentOutOfRangeException` (OOB). (The local `r` even
reported `n=77` correct in the debugger dump, because the CopyILToFrame byte
copy was fine — only the subsequent *field access* mis-lowered.)

This is why the non-generic Step 13 IL-VT tests (`NeoTestIlVtOneRefBoxUnbox`,
`NeoTestIlVtManyRefsBoxUnbox`) are GREEN: there, `unbox.any r2, r1` writes
DIRECTLY into the typed local `r2` (no intervening `Move`), so `r2` keeps its
initial IL-VT local type and the following `ldfld.i4.inline` rewrites correctly.
The generic body, once inlined, routes the unbox result through a `Move` into
the local, which clobbers the type.

## The fix (1 file, Neo-gated)

`ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` — add an
`Unbox` / `Unbox_Any` case to `TypeSpecializeNeoOpcodes` (mirroring the existing
`Newobj` IL-VT dest-typing rule): when the type token (`op.Operand`) is an IL
value type (non-enum, non-primitive), stamp `op.Register1` (the dest) with that
ILType. Subsequent `Move`/`Ldfld`/`Stfld` then recognize the in-frame VT and
lower to the `_Inline` variant.

Box is left untouched (its dest is an mStack reference, not an in-frame VT).
Unbox of a reference/primitive type is left untyped.

The fix is entirely within `#if ENABLE_NEO_MODE` (the
`TypeSpecializeNeoOpcodes` method is Neo-gated, lines 776–1391), so it is
**Legacy-neutral** (plain `Debug` build does not compile it). Legacy `ExecuteR`
has no analogous gap (it does not use the register-type-tracking +
inline-vs-heap field-access lowering).

## Verification

- **Probe** (`TestCases/NeoStepBoxIlvtProbe.cs`): `NeoStepBoxIlvt_Val` (box an
  IL struct `{int n; string s;}` inside `BoxUnbox<T>`, unbox, assert `n==77` +
  `s=="ok"`, `s` non-null) — PASSES (returns 77). `NeoStepBoxIlvt_Int`
  (known-good reference) — PASSES (returns 4242).
- **Stash-toggle (load-bearing):** stash `JITCompiler.cs` -> `NeoStepBoxIlvt_Val`
  FAILS (`ArgumentOutOfRangeException`); pop -> PASSES.
- **NeoStep smoke: 291/291, 0 failed** (baseline 289 + the 2 added probe
  methods). No regression.
- **Legacy-neutral:** fix is inside the Neo gate.

## Scope notes / out of scope

- The non-generic IL-VT box/unbox (Step 13) was already correct; this fix only
  closes the generic/inlined case where a `Move` sits between the unbox dest and
  the field access.
- The `Constrained` ref-T callvirt gap (another parked engine gap from the
  T-identity work) is NOT in scope.
