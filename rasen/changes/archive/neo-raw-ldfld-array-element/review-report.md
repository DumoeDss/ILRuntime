# Review Report: neo-raw-ldfld-array-element (child-24 of neo-overhaul)

Reviewer: independent (REVIEWER role). Code NOT written by this reviewer.
Branch: features/object-model-overhaul. Mode: Neo (ENABLE_NEO_MODE).

## Scope reviewed (working tree)
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- new const
  `NeoRawLdfldArrayElementByRefMarker = 0x1` (~:226) + stamp in the
  `case Code.Ldfld` CLRType else-branch when
  `ins.Previous != null && ins.Previous.OpCode.Code == Code.Ldelema` (~:3146),
  inside `#if ENABLE_NEO_MODE`.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- raw-Ldfld
  value-type-owner branch (~:3892): marker-gated array-element READ (decode
  (arrIdx, elementIdx), `Array.GetValue` + `f.GetValue`), existing flat-bytes
  `ReadNeoValueType` path moved under `else`.
- `TestCases/NeoStepRawLdfldArrayElementTest.cs` (3 TCs) + host helper
  `BuildNeoArrElemProbeArray` in
  `ILRuntimeTestBase/TestFramework/TestClass3.cs` (TestClass3.cs is an `M`
  working-tree change in THIS PR, despite a planning-context note calling it
  "child-19-added"; the sibling `NeoArrElemFieldSum` is child-19's).

## Verification performed (empirical)
- Build CLI `Debug_Neo --no-incremental`: 0 errors (270 warnings, all
  pre-existing noise -- the net3.0/console/MSVC-LIB noise).
- Build `TestCases` Debug: 0 errors.
- Full NeoStep smoke WITH fix: **Ran 368 tests, 0 failed, 0 ignored, 0 todos**
  (matches the implementer's claim exactly).
- Stash-toggle of the TWO engine files (probe + helper kept):
  - Fix STASHED (HEAD engine), rebuilt, ran `RawLdfldArrayElement` filter:
    **Ran 3 tests, 3 failed** -- all `System.DivideByZeroException`
    (the deliberate `1/0` on value mismatch; the read reinterprets the
    (arrIdx, elementIdx) byref as struct bytes). EXIT 127.
  - `git stash pop` restored both files (verified `M` again), rebuilt, re-ran:
    **Ran 3 tests, 0 failed**. EXIT 0.
  - Airtight: the fix is load-bearing; with it removed the 3 probes fault,
    with it restored they pass.
- Regression-family invocation counts inside the 368/0 smoke (all passed):
  NeoStepRawFld (child-4 raw Ldfld/Stfld CLR-owner) 2, NeoStepRawStfldArrElem
  (child-19 Stfld array WRITE) 2, NeoStepFloatSeeding (child-21 raw-Ldfld-CLR-
  struct seeding + float) 4, NeoStep12 12, NeoStep13 36, NeoStep17 54,
  NeoStepLdindStind (child-15) 2, NeoStepCeqNull (child-23) 7,
  NeoStepNewobjArgAlias (child-13) 4.

## Static analysis by dimension

### 1. JIT marker stamp reliability -- SOUND
- `ins.Previous == Code.Ldelema` is the IMMEDIATE CIL predecessor. The
  `Translate(block, Instruction ins, ...)` front-end walks the Mono.Cecil
  `Instruction` list in source order; the optimizer passes
  (`LowerNeoOffsets`, `TypeSpecializeNeoOpcodes`) run AFTER translation, so no
  reordering can have intervened at the stamp site. The `ins.Previous/Next`
  links are stable CIL order.
- Register coincidence verified against the actual JIT emission:
  - Ldelema (JITCompiler.cs:2875-2878): `Register1 = baseRegIdx-2` (dest),
    then `baseRegIdx--`. After decrement the byref lives at the NEW
    `baseRegIdx-1`.
  - Ldfld (JITCompiler.cs:3098-3099): `Register1 = Register2 = baseRegIdx-1`
    (R1==R2; owner == dest).
  - So ldelema-dest == ldfld-owner. The marker is stamped on exactly the ldfld
    whose owner is the ldelema byref.
- `readonly.` / `constrained.` are CIL PREFIXES (ECMA-335 III.1-2): they
  precede the instruction they modify (`readonly.` precedes `ldelema`;
  `constrained.` precedes `callvirt`). Neither can sit between `ldelema` and
  `ldfld`. Confirmed.
- Block-boundary false positive is impossible: in verifiable IL the eval stack
  is empty at branch targets, so a byref cannot be live across a block
  boundary. If `ldfld` were block-first, `ins.Previous` is the prior block's
  terminator (branch/leave/ret/throw -- never `ldelema`). And a `ldelema`
  pushing a byref that is the immediate CIL predecessor of `ldfld` IS, by stack
  discipline, that ldfld's owner. No false-positive shape exists.
- See Trivial T1 for one theoretical false NEGATIVE (`unaligned.` prefix).

### 2. Operand4 survival through the optimizer -- SOUND
- `LowerNeoOffsets` raw-Ldfld case (Optimizer.Neo.cs:945-971) touches only
  `op.Operand` (Ldfld_Ref only), `op.DstOffset`, `op.SrcOffset`. Operand4 is
  untouched; the comment states "field identity lives in OperandLong".
- Push-deletion remap (Optimizer.Neo.cs:1727-1729) fires only for
  `IsIntermediateBranching(op.Code)` opcodes; a raw `Ldfld` is not one, and
  even if it were, the guard `op.Operand4 > removedIndex` can never hold for
  0x1 (removedIndex is a body index >= 0).
- `TypeSpecializeNeoOpcodes` raw-Ldfld seeding (JITCompiler.cs:1110-1124, child-21)
  reads `OperandLong`, resolves the field, calls `SetRegisterType` -- it does
  NOT write `op.Operand4`. Runs after the stamp; leaves the marker intact.
- Comprehensive sweep of every `Operand4 =/|=/&=` site in both files: NONE
  apply to the raw `OpCodeREnum.Ldfld` except this PR's stamp (JIT:3146). The
  F-10 `fieldType.GetHashCode()` stamp (JIT:3123) is in the mutually-exclusive
  `if (type is ILType)` branch (an Ldfld is EITHER ILType-declaring -> a typed
  arm/F-10 -> OR CLRType-declaring -> raw + this marker; never both). The four
  Ldflda markers (JIT:3172/3182/3195; the 0x1/0x2/0x4/0x8 set on Ldflda) are a
  DIFFERENT opcode's Operand4 namespace. The jump-target remap (JIT:642) and
  the Ldtoken/call/elem Operand4 writes (Optimizer:580/881/936/1048/1121/...)
  are gated to other opcodes. The marker is collision-free and immutable on
  this opcode from stamp to runtime read.
- Empirical cross-check: the stash-toggle proves the marker reaches runtime
  (the array-read branch fires iff the fix is present).

### 3. Runtime array-element READ correctness -- SOUND
- Decode `arrIdx = *(int*)(ownerOff)`, `elementIdx = *(int*)(ownerOff+4)` is
  byte-identical to the ldelema CLR-array encoding (ILIntepreter.Neo.cs:5797-
  5798 writes raw `arrIdx`@+0, `elementIdx`@+4, NO flag bit) and to child-19's
  Stfld WRITE decode (:4091-4092 uses `off`@+4 as elementIdx). Symmetric.
- `cArr.GetValue(elementIdx)` returns a boxed copy of the struct element;
  `f.GetValue(boxedElem)` reads the field. Dest marshalling (:3960-3969) is
  SHARED with the flat-bytes path and correct by category: primitive ->
  `NeoWritePrimitiveToFrame`; VT -> `WriteNeoValueType` (sized by
  `GetNeoValueTypeManagedSize`); ref -> `mStack.Add` + index, -1 sentinel for
  null. Correct for int/float/VT/ref.
- NULL element: a value-type array element is never null (`GetValue` boxes the
  default struct for an uninitialized slot) -- no NRE. The ldelema arm
  (:5794) already rejects ref-type-element arrays, so the element is always a
  VT. A null REFERENCE FIELD inside the struct marshals to -1 correctly.
- Out-of-range elementIdx throws `IndexOutOfRangeException` at `GetValue` --
  the correct CLR semantics for `arr[i].field` with a bad index.
- NO write-back: the branch uses only `GetValue`/`GetValue` (read-only
  reflection); there is no `SetValue` and no `cArr.SetValue` (contrast child-19
  Stfld's box/mutate/unbox at :4111-4113). Confirmed a pure READ, no mutation.

### 4. No-overfire / marker exclusivity -- SOUND
- False positive (Previous==Ldelema but owner not an array-element byref):
  impossible by CIL stack discipline (dimension 1). None exists.
- False negative (array-element READ with Previous != Ldelema): the documented
  ref-local indirection (`ref var p = ref arr[i]; p.field` -> `ldelema; stloc
  p(ref); ldloc p; ldfld`) has `ins.Previous == ldloc`, so the marker does not
  fire -> the new `else` flat-bytes path runs. That is byte-identical to HEAD
  (HEAD always took flat-bytes) -- HEAD-faithful, NOT a new regression. The
  implementer documents this as a follow-up. Confirmed.

### 5. Regression surface -- CLEAN
- 368/0 with no regressions across the related families (counts above). The
  flat-bytes path (child-4) is now under the new `else` and still green
  (NeoStepRawFld 2/2); child-19's Stfld WRITE is untouched and green
  (NeoStepRawStfldArrElem 2/2); child-21's raw-Ldfld-CLR-struct seeding is
  untouched and green (NeoStepFloatSeeding 4/4, incl. the raw-Ldfld primitive
  float case); NeoStep12/13/17 VT surface (102 invocations) green.

### 6. TC3 arithmetic correction (1477 -> 7777) -- CONFIRMED CORRECT
- `BuildNeoArrElemProbeArray(7, 70, 700, 7000)` yields `arr[0] = {A=7, B=70}`,
  `arr[1] = {A=700, B=7000}`.
- TC3 reads `arr[0].A + arr[0].B + arr[1].A + arr[1].B = 7 + 70 + 700 + 7000
  = 7777`. The corrected `if (s != 7777)` is arithmetically correct; the
  original `1477` was a planner typo (7777 != 1477 would have made TC3
  unpassable on HEAD AND after the fix). Hand-checked and confirmed.
- All 3 PASS constants are genuinely reachable and discriminating:
  - TC1 = 4259 = 4242 + 17 (single-element round-trip; HEAD gives 3+1=4).
  - TC2 = 100 = 10 + 20 + 30 + 40 (two distinct element indices 0 and 5; a
    constant-0 or arrIdx-as-elementIdx decode lands the wrong element -> !=100).
  - TC3 = 7777 (four distinct escalating magnitudes; any wrong element/field
    decode yields a distinguishable wrong sum).

### 7. Read-back correctness (not just non-throwing) -- CONFIRMED
- The probes use the `1/0` fault discipline: PASS == exact sum match. So PASS
  means the host-written values round-trip EXACTLY (TC1 4242/17, TC2
  10/20/30/40 at indices 0/5, TC3 7/70/700/7000). These are the actual
  written+read values, not merely "did not throw". TC3 additionally isolates
  the READ (host-built array, no Stfld dependency), proving the Ldfld read
  alone is correct.

## Findings

### Blocker
(none)

### Major
(none)

### Minor
- **M1 (test-coverage, this-PR, non-blocking): float/VT/ref field categories
  on the array-element READ are not directly probed.** All 3 probes use INT
  fields (deliberate -- sidesteps the pre-existing `addi`-on-float and
  `conv.i4`-float-bit-reinterpret bugs, child-15/16). The array-element branch
  marshals via the SAME shared dest marshalling the flat-bytes path uses, and
  child-21 already exercised raw-Ldfld-CLR-struct primitive floats on the
  flat-bytes path -- so the float marshalling here is mechanically identical
  to a green path. Low risk. A follow-up float-field array-element probe would
  close the gap directly; not required for correctness of this fix.

- **M2 (benign behavior asymmetry, pre-existing-class, this-PR-adjacent): the
  array-element READ handles ref-field CLR structs via reflection WITHOUT
  NIE-ing**, whereas the flat-bytes `ReadNeoValueType` path NIEs ("CLR value
  type with reference fields and no ValueTypeBinder, Step 13b"). This is
  strictly MORE permissive (a ref-field struct read from an array element
  succeeds here) and is not a regression -- the flat-bytes NIE is unchanged.
  Not exercised by any probe (NeoArrElemIntProbe has only int fields). Worth a
  one-line design note so a future reader does not assume parity with the
  flat-bytes NIE.

### Trivial
- **T1 (this-PR, theoretical false-negative): `unaligned.` prefix.** ECMA-335
  permits `unaligned. N` to prefix `ldfld`, giving the CIL sequence
  `ldelema T; unaligned. N; ldfld field`. In that shape `ins.Previous` is the
  `unaligned.` instruction, not `ldelema`, so the marker would NOT fire ->
  flat-bytes path -> HEAD behavior (silent corruption for that shape). Roslyn
  never emits `unaligned.` for a managed CLR-struct array element field access
  (it is for `unsafe` misaligned pointer arithmetic), so this is purely
  theoretical. It is a HEAD-faithful coverage GAP (same class as the documented
  ref-local gap), not a regression. No action needed; noted for completeness.

- **T2 (doc-accuracy, this-PR):** the planning-context digest (neo-overhaul
  child-24 entry) labels `BuildNeoArrElemProbeArray` as "child-19-added", but
  git shows `TestClass3.cs` as a modified (`M`) working-tree file in THIS PR
  (child-19 added the sibling `NeoArrElemFieldSum` + the `NeoArrElemIntProbe`
  struct). Cosmetic; the helper ships in the right place regardless.

- **T3 (PRE-EXISTING, unrelated to this PR):** Optimizer.Neo.cs:1234 contains
  a bare expression-statement `op.Operand4 == 1;` -- a no-op comparison (looks
  like a typo for an assignment, but the enclosing logic appears intentional).
  It is in a Constrained/branching context, NOT the raw-Ldfld path, so it does
  not affect this change. Flagged only as a pre-existing curiosity for a future
  cleanup pass; out of scope here.

## Verdict: APPROVE-WITH-FINDINGS

0 Blocker, 0 Major. The fix is the architecturally consistent, PROVABLY-correct
choice for the canonical `arr[i].field` shape (JIT marker, mirroring the
established Neo pattern of resolving untyped-frame dataflow facts at JIT time);
it is symmetric with child-19's Stfld WRITE and introduces NO new silent-
corruption vector. The asymmetry with Stfld (marker here vs runtime check there)
is well-justified and correctly reasoned. Operand4 survival is verified both
statically (no pass touches it for the raw Ldfld) and empirically (stash-toggle).
The TC3 7777 correction is arithmetically right, and all 3 PASS constants are
reachable and discriminating. Read-back is exact (fault discipline), not merely
non-throwing. Regression surface is clean (368/0; child-4/15/19/21/23 + the
NeoStep12/13/17 VT surface all green). The Minors are coverage/doc notes; the
Trivials are theoretical/doc/pre-existing. Ship it.
