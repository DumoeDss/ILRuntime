# Tasks — neo-ilvt-boxing-roundtrip

**Status:** DONE  **Date:** 2026-07-11

## 1. Reproducer (probe) — DONE
- Added `TestCases/NeoStepBoxIlvtProbe.cs`:
  - `struct NeoStepBoxIlvtVal { int n; string s; }`
  - `T BoxUnbox<T>(T v){ object o = v; return (T)o; }` (the generic body)
  - `NeoStepBoxIlvt_Int` (BoxUnbox<int> = known-good reference) + `NeoStepBoxIlvt_Val`
    (BoxUnbox<NeoStepBoxIlvtVal>, asserts n==77 + s=="ok", s non-null).
- On HEAD (pre-fix): `NeoStepBoxIlvt_Val` threw `ArgumentOutOfRangeException` at
  `ILIntepreter.Neo.cs:3460` (heap `Ldfld_I4` reading the int field as an mStack
  index). `NeoStepBoxIlvt_Int` passed.

## 2. Root cause — DONE
- Gap is in the JIT (`JITCompiler.TypeSpecializeNeoOpcodes`), NOT the Box/Unbox
  execution arms (which are correct for IL VTs).
- The dest register of an `Unbox`/`Unbox_Any` of an IL value type was never
  typed as an in-frame VT. The inlined `BoxUnbox<IL-VT>` body routes the unbox
  result through a `Move` into the result local; that `Move` clobbers the
  local's initial IL-VT type with null. The subsequent `ldfld` then does NOT
  rewrite to the `_Inline` variant → stays the heap `Ldfld_I4` → reads the int
  field's bytes as an mStack index → OOB.
- The non-generic Step 13 IL-VT tests are green because there the unbox writes
  DIRECTLY into the typed local (no intervening Move), so the type survives.

## 3. Fix — DONE (Neo-gated, 1 file)
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`: added an
  `Unbox`/`Unbox_Any` case to `TypeSpecializeNeoOpcodes` that stamps the dest
  register (`op.Register1`) with the ILType when `op.Operand` resolves to an IL
  value type (non-enum, non-primitive). Mirrors the existing `Newobj` IL-VT
  dest-typing rule (the fourth in-frame-VT address case).
- Box left untouched (dest is a reference). Unbox of ref/primitive left untyped.
- Entirely within `#if ENABLE_NEO_MODE` (the method is Neo-gated) → Legacy-neutral.

## 4. Verify — DONE
- Probe: both methods PASS (`NeoStepBoxIlvt_Val` returns 77).
- Stash-toggle: stash JITCompiler.cs → probe FAILS (ArgumentOutOfRange); pop → PASSES.
- NeoStep smoke: 291/291, 0 failed (baseline 289 + 2 added). No regression.
- Legacy-neutral confirmed (Neo-gated).

## 5. Deep-rework fallback — N/A
- The 9-for-9 lesson held: it was a specific single-case register-type-tracking
  gap, not a deep Box/Unbox rework. No parking needed.
