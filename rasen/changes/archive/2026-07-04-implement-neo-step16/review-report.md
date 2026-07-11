# Review Report — implement-neo-step16 (Neo Step 16: array element access)

**Reviewer:** independent verify-stage reviewer (author != verifier).
**Date:** 2026-07-04.
**HEAD:** `cf4a0331` (Step 15). All Step 16 work is uncommitted working-tree changes.

## Executive verdict: **CLEAN** (with 3 informational notes)

The implementation is correct, well-scoped, and matches the spec + design. No
Blockers, no Majors. The 3 pre-existing quirks are genuinely pre-existing (verified
by code-path analysis + the TC7 control test). Smoke is 72/72. Legacy is untouched.

**Severity counts:** Blocker 0 / Major 0 / Minor 0 / Informational 3.

---

## Scope check

- **Diff = exactly the 3 intended files** (working tree vs HEAD `cf4a0331`):
  `Optimizer.Neo.cs` (+85), `ILIntepreter.Neo.cs` (+299), `TestCases/NeoStep16Test.cs`
  (new, 7 tests). (Plus pre-existing `.pdb`/`.gitignore` noise unrelated to this step.)
- **Legacy `ILIntepreter.Register.cs`: 0-line diff.** Confirmed untouched. ✓
- **All new runtime code behind `#if ENABLE_NEO_MODE`** (both files wrapped from line 1). ✓
- **Build:** CLI `Debug_Neo` = 0 errors; TestCases `Debug` = 0 errors. ✓
- **Smoke:** FULL NeoStep = **72/72, 0 failed** (baseline 65 + 7 new NeoStep16 = TC1
  int[]/TC2 long[]/TC3 float+double[]/TC4 IL ref-type[]/TC5 IL value-type[]/TC6
  Ldlen/TC7 newobj-arg control). No existing case regressed. ✓

**Scope verdict:** CLEAN — built exactly what was specified, nothing more.

---

## Priority scrutiny results

### (a) Operand non-clobbering — **VERIFIED SAFE** (not a finding)

The critical subtlety holds. `OpCodeR` is `[StructLayout(Explicit)]`
(`OpCode.cs:35-71`). Field alias map:
- offset 8: `Register3` | `Register4`(@10) | `Operand`(@8,int) | `OperandOffset` | `OperandFloat`
- offset 16: `Operand3` (independent)
- offset 20: `Operand4` (independent)

LowerNeoOffsets array arms touch ONLY `DstOffset`(@4), `SrcOffset`(@6), `Operand3`(@16),
`Operand4`(@20). None of these alias `Operand`(@8).

JIT stamping verified directly in `JITCompiler.cs`:
- `Newarr` (@2061-2065): stamps R1, R2, **and `Operand`** (element-type token).
- `Ldelem_*` group (@1943-1959): stamps ONLY R1/R2/R3. `Operand` NOT stamped.
- `Stelem_*` group (@2071-2084): stamps ONLY R1/R2/R3. `Operand` NOT stamped.

Therefore lowering Ldelem/Stelem cannot clobber a token (there is none to clobber), and
Newarr's `Operand` is preserved (the Newarr lowering arm never writes `Operand`). The
design note's correction ("Ldelem_Any/Stelem_Any do NOT carry a token") is accurate. The
element type for Any variants is recovered at runtime from the array's CLR type / the
fetched element's `.Type`, which is sufficient and matches Legacy. **No silent
wrong-element-type risk.**

### (b) Newarr per-kind correctness — **VERIFIED CORRECT**

- IL ref-type: `new ILTypeInstance[count]`, null slots (C# default). ✓
- IL value-type: `new ILTypeInstance[count]` + `for (i=0; i<count; i++) ilArr[i] = ilType.Instantiate(true)`.
  **Pre-instantiation covers ALL slots** (full loop, not slot 0 only). `Instantiate(true)`
  signature confirmed at `ILType.cs:2265` (`bool callDefaultConstructor`). No NRE on first
  VT stelem/ldelem of any slot. Matches Legacy @4904-4910. ✓
- Non-IL: `CLRType.CreateArrayInstance(n)` / `Array.CreateInstance(TypeForCLR, n)` +
  `AppDomain.GetType(arr.GetType())` registration (as Legacy does). ✓
- Array ref stored at `frameRefBase + ip->Operand3`, mStack index written to
  `frameBase + ip->DstOffset`. Null element type → dest slot = -1. ✓
- **In-place read-before-write is safe:** JIT Newarr sets R1==R2 (`baseRegIdx-1`), so
  DstOffset==SrcOffset; the arm reads `count` from SrcOffset BEFORE writing the array
  mStack index to DstOffset. No corruption.

### (c) Ldelem/Stelem per-kind correctness — **VERIFIED CORRECT (vs Legacy)**

Cross-checked every arm against Legacy `ILIntepreter.Register.cs` (@4916-5304):
- Primitive I1/U1/I2/U2 bool/sbyte/byte/char/ushort disambiguation order matches Legacy
  exactly (Ldelem_I1: bool[]→sbyte[]; Ldelem_U1: byte[]→bool[]; Ldelem_I2: short[]→char[];
  Ldelem_U2: ushort[]→char[]; Stelem_I1: byte[]→bool[]→sbyte[]; Stelem_I2:
  short[]→ushort[]→char[]). Value read width (`byte`/`short`) then cast preserves bit
  pattern for negatives. ✓
- IL ref-type → mStack-resident object element (Ldelem_Ref/Any ref path: store object on
  `frameRefBase + Operand3`, write index). ✓
- IL value-type → `CopyILToFrame` (load) / `CopyFrameToIL` (store). Helper signatures
  (`ILIntepreter.Neo.cs:2755`, `:2777`) and call-site argument order verified correct.
  Both copy primitive bytes (`Unsafe.CopyBlock`) AND ref slots (mStack-region loop). ✓
- CLR `object[]` → `Array.GetValue` (load) / `SetValue` (store). Null handled (vIdx < 0). ✓
- `Ldelem_Any`/`Stelem_Any` VT-vs-ref decision uses runtime `elemIns.Type.IsValueType &&
  !elemIns.Boxed` — robust, no token dependency, matches Legacy dispatch. ✓

### (d) Adjudication of the 3 pre-existing quirks — **ALL GENUINELY PRE-EXISTING**

Verified each is in code untouched by this diff (not a Step 16 gap):

1. **Newobj dest/arg register aliasing** — **PRE-EXISTING (Step 10/11).** TC7
   (`new NeoStep16Item(5)`, no array) is GREEN, proving newobj-with-arg works in
   isolation. The quirk manifests only when `new T(intArg)` immediately FOLLOWS a
   `newarr` (register-reuse collision in the Call/Newobj Push-scanning lowering).
   Diff adds 0 lines to newobj/call/Push machinery (verified by grep). Worked around
   in TC4 via default-ctor + field-set. Out of Step 16 scope; flag for Step 10/11
   newobj/call hardening. **Severity: informational (pre-existing).**

2. **Struct-local + field-mutation + element-read optimizer temp-renumber** —
   **PRE-EXISTING (BCP/copy-prop).** Diff adds 0 lines to BCP/copy-prop/conv/compare
   (verified by grep). The basic struct store/load round-trips both primitive AND
   reference fields correctly (TC5 GREEN). The quirk is in optimizer temp renumbering,
   exposed only by the struct-mutation-then-read pattern; not exercised by TC5.
   **Severity: informational (pre-existing).**

3. **`long` default-zero compare (conv.i8)** — **PRE-EXISTING (conv/compare).** Same
   grep confirmation (0 diff lines to conv/compare). The `long` store/load itself
   round-trips correctly (TC2 GREEN). The quirk is in conv.i8 + long-compare
   evaluation, not the array path. **Severity: informational (pre-existing).**

None block Step 16. All are correctly worked around in the tests and flagged for the
relevant future steps.

### (e) Stelem_I lowered-but-interpreter-NIE — **ACCEPTABLE (informational)**

`Stelem_I` (native-int store) is added to the LowerNeoOffsets switch (correct 3-register
encoding for safety) but has NO interpreter arm — at runtime it falls through to the
generic Step-tagged NIE at `ILIntepreter.Neo.cs:2482` (`"Neo: opcode {0} not yet
implemented (Step 6)"`). This is a **loud, Step-tagged failure**, not silent corruption.

Plausibility: C# emits `Stelem_I` for `IntPtr[]`/`UIntPtr[]`/pointer-array element stores
on 64-bit. `IntPtr[]` is technically a CLR primitive array (in-scope kind), so a future
test exercising native-int arrays would hit this NIE. It is rare in typical C# output and
explicitly scoped out by the spec/design (§9 "non-goals"). **Verdict: acceptable as
documented Minor-gap; loud NIE is the correct safety posture.** Flag for a future step if
`IntPtr[]` tests are added. Not a blocker.

### (f) Regression + scope confirmation

- Full NeoStep smoke re-run: **72/72, 0 failed.** ✓
- `Ldelema`: still a Step-tagged NIE (deferred to Step 17 per spec — verified not
  implemented; the `case OpCodeREnum.Ldelema:` is in the JIT binary-op group @1943 but
  has NO Neo interpreter arm, so it NIEs at runtime as specified). ✓
- Generic-token `Code.Ldelem`/`Code.Stelem`/`Code.Ldelem_I`/`Code.Ldelem_U8`: NOT in JIT
  enumeration (verified) → NIE at JIT time, out of scope. ✓
- Multi-dim arrays: not exercised (rank-1 only, per scope). ✓
- Legacy `ILIntepreter.Register.cs`: 0-line diff. ✓
- Bounds: relies on CLR typed indexer `IndexOutOfRangeException` (OOB); null array →
  `NullReferenceException` via explicit `srcIdx < 0` guards + null-cast/`.Length` NRE.
  Matches Legacy semantics. ✓
- All new code behind `#if ENABLE_NEO_MODE`. ✓

---

## Findings summary

| # | Severity | Location | What |
|---|----------|----------|------|
| 1 | Informational | Optimizer.Neo.cs (Stelem_I arm) + ILIntepreter.Neo.cs:2482 | `Stelem_I` lowered but interpreter NIEs (native-int array store). Loud, Step-tagged, scoped out. Documented gap for `IntPtr[]` if ever tested. |
| 2 | Informational | (pre-existing) Optimizer.Neo.cs Call/Newobj Push-scanning | Newobj dest/arg aliasing when `new T(int)` follows `newarr`. Pre-existing Step 10/11; exposed by TC4, worked around. |
| 3 | Informational | (pre-existing) BCP/copy-prop + conv/compare | Struct-mutation-read temp-renumber quirk; long conv.i8 compare quirk. Pre-existing; basic paths green (TC2/TC5). |

No Blockers. No Majors. No Minors. The three items above are all informational
(documented gap + pre-existing issues correctly scoped out).

## Recommendation

**SHIP.** Step 16 is correct, matches spec/design, regression-clean (72/72), Legacy
untouched, fully Neo-gated. The 3 pre-existing quirks are properly adjudicated as
out-of-scope and worked around in tests; Stelem_I's loud NIE is the right safety posture
for an explicitly-deferred native-int path.
