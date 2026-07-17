# Review Report -- neo-raw-stfld-clr-object-vt-field

> Child 27 of the neo-overhaul portfolio. Independent verifier review
> (reviewer did NOT write this code). Branch `features/object-model-overhaul`.

## Verdict: APPROVE

The diagnosis is correct, the box/mutate/unbox is sound, the empirical
evidence is airtight, and no regression surfaced. No Blockers, no Majors.
Two Minor notes (one PRE-EXISTING, one forward-looking for the F-10 sibling)
and one Trivial (PRE-EXISTING comment) are recorded below; none block ship.

---

## What was verified (all 7 dimensions)

### 1. Diagnosis correctness -- THE CRUX -- CONFIRMED CORRECT

The implementer claims the `ldflda Struct(on CLR object)` runtime `else`
branch produces a byref `(objIdx_of_containing_CLR_object, structFieldHash)`
where the offset half is `FieldInfo.GetHashCode()` (NOT a byte offset), and
that the Area-4d helpers resolve that hash to the struct FieldInfo via the
containing CLRType's `fieldInfoCache`.

I traced the full chain end to end and it is self-consistent:

- **JIT stamping** (`AppDomain.GetFieldOffset`, AppDomain.cs:2262-2278): for a
  non-IL declaring type it returns `PrimitiveOffset = type.GetFieldIndex(token)`.
  `CLRType.GetFieldIndex(object token)` (CLRType.cs:661-679) returns
  `fieldMapping[f.Name]`, and `InitializeFields` (CLRType.cs:614) stores
  `fieldMapping[i.Name] = i.GetHashCode()` = the runtime `System.Reflection
  .FieldInfo.GetHashCode()`. So the JIT stamps `Operand2` of `ldflda` with the
  STRUCT field's FieldInfo hash on the owner type.
- **Runtime byref production** (ILIntepreter.Neo.cs:1941-1956, the `else`
  branch): `*(int*)(dst+0) = objIdx; *(int*)(dst+4) = fieldPrimOff;` where
  `fieldPrimOff = ip->Operand2` = the hash. The comment at :1945-1948 states
  this explicitly. CONFIRMED.
- **Hash resolution** (`NeoReadClrObjectField`, ILIntepreter.Neo.cs:6499-6508):
  re-resolves `ct = appdomain.GetType(target.GetType()) as CLRType` (the
  OWNER's CLRType) and calls `ct.GetFieldValue(fieldHash, target)`, which goes
  `GetField(hash)` (CLRType.cs:529-539) -> `Fields` (= `fieldInfoCache`,
  CLRType.cs:590/615, keyed by `FieldInfo.GetHashCode()`) -> `fieldInfo
  .GetValue(target)`. The hash is GENERATED (JIT) and RESOLVED (runtime) by
  the same function on the same FieldInfo on the same type -> guaranteed match.

**Is `NeoReadClrObjectField`/`NeoWriteClrObjectField` the correct accessor for
a STRUCT field?** YES. For a value-type field, `fieldInfo.GetValue(target)`
returns a BOXED copy of the whole struct, and `fieldInfo.SetValue(target,
value)` writes the whole struct back. That is exactly the read-the-whole-
struct / write-the-whole-struct pair the box/mutate/unbox needs. This is the
SAME mechanism the existing Area-4d callers use (ILIntepreter.Neo.cs:4026,
4031, 4216, 4232, 4250, 4266 -- proven by children 4/9).

**Could the hash collide?** Only theoretically, and this is PRE-EXISTING (see
Finding M1), not introduced here. `fieldInfoCache` is per-CLRType, and
`System.Reflection.RuntimeFieldInfo.GetHashCode()` is derived from the field's
runtime handle (distinct per field within a type in practice). No realistic
collision.

### 2. Box/mutate/unbox branch correctness -- SOUND

The three steps (ILIntepreter.Neo.cs:4222-4224):
```
object boxedStruct = NeoReadClrObjectField(AppDomain, target, off);  // read whole struct (a fresh box)
f.SetValue(boxedStruct, value);                                       // mutate the box in place
NeoWriteClrObjectField(AppDomain, target, off, boxedStruct);          // write the same box back
```
- **`f` is the LEAF field, not the struct field.** `f = ct.GetField(fieldHash)`
  where `ct` = the Stfld arm's `declType as CLRType` = the LEAF field's
  declaring type = the STRUCT (e.g. NeoClrObjVtFieldProbe), and `fieldHash` =
  the leaf field's hash (e.g. `a`). Correct.
- **`off` is the STRUCT field's hash.** `off = *(int*)(frameBase + ownerOff +
  4)` = the byref's +4 half = the `S` field's hash on the owner. Resolved
  against the OWNER's CLRType inside the helpers (the helpers re-derive `ct`
  from `target.GetType()`; no variable shadowing -- the helper's local `ct` is
  the owner type, the outer `ct` is the struct type; they are distinct and
  used for distinct purposes).
- **The SAME boxedStruct reference flows through all three steps.** Read
  returns a fresh box; `FieldInfo.SetValue` on a boxed value type mutates the
  box IN PLACE (established .NET reflection behavior; the child-19 precedent
  at :4188-4190 relies on the identical property); write-back stores that same
  mutated box. No copy is lost.
- **Write-back persists.** `NeoWriteClrObjectField` -> `ct.SetFieldValue(off,
  ref tmp, boxedStruct)` -> `fieldInfo.SetValue(target, boxedStruct)`
  (CLRType.cs:472-491). `target` is the owner CLASS on mStack, so the mutation
  is visible to subsequent reads (the TC2 host read-back = 666 empirically
  proves this). The `ref target` defensive is a no-op for a class target.

### 3. Runtime-detection safety (no marker) -- CORRECT

The implementer claims a VT-owner Stfld's owner is ALWAYS a byref, so runtime
content detection is unambiguous. The branch ordering confirms this:
```
if (objIdx == -1)                      // frame-local struct (box from frame bytes)
else if (objIdx >= 0 && mStack[objIdx] is Array cArr)   // child-19 array element
else if (objIdx >= 0)                  // THIS CHANGE: CLR object field owner
else   NIE                             // unrecognized shape
```
The three `objIdx >= 0` sub-shapes are discriminated by runtime CONTENT
(`is Array`, `is ILTypeInstance`/`CrossBindingAdaptorType` -> deferred NIE,
else CLR object), exactly mirroring child-19's array fix. The flat-bytes-vs-
byref ambiguity only exists for the READ side (raw Ldfld, which child-24 had
to mark); the WRITE side always goes through an address. Confirmed consistent
with the child-24/26 read/write asymmetry.

### 4. IL-instance deferred NIE -- HONEST deferral (F-10 sibling)

`target is ILTypeInstance || target is CrossBindingAdaptorType` -> tagged NIE
naming the F-10 ManagedObjects storage gap, the field, and the type
(:4220-4221). This is a correct, fail-loud deferral of a genuinely distinct
storage mechanism (an IL instance's CLR-struct field lives in ManagedObjects,
not as a CLR field on a CLR object). It is NOT a silent miss: the branch is
reached and explicitly throws. Good.

### 5. Field-preservation arithmetic -- CORRECT

- TC1: `o.S.a = 111` -> sum = 111+0+0 = 111. Correct.
- TC2: `o.S.a = 111; o.S.b = 222; o.S.c = 333` -> sum = 111+222+333 = 666.
  Correct. A fix that recreated the struct from default each write (instead of
  reading the current struct) would yield only the LAST write: a=0,b=0,c=333
  -> sum = 333, NOT 666. The `if (s != 666) { 1/0 }` assertion (probe idiom
  matching child-24/25/26) catches that bug class. The box/mutate/unbox reads
  the current struct before each mutate, so the multi-write progression
  (111,0,0) -> (111,222,0) -> (111,222,333) genuinely tests preservation.
  Arithmetic matches the child-24 lesson.

### 6. Regression surface -- CLEAN

- NeoStep smoke (with fix): **Ran 375 tests, 0 failed, 0 ignored, 0 todos.**
  Matches the claim (373 baseline + TC1 + TC2 = 375). The child-4/9/19/24/25/
  26 CLR-owner/array/byref families all stay green (they are in the NeoStep
  filter and contributed to the 373 baseline).
- Stash-toggle (airtight): stashing ONLY ILIntepreter.Neo.cs and rebuilding ->
  the 2 new probes FAULT with the exact HEAD NIE `Neo raw Stfld: unrecognized
  CLR value-type owner byref shape (objIdx=3). Field a on ...NeoClrObjVt
  FieldProbe` at ILIntepreter.Neo.cs:4193. Pop + rebuild -> 2/2 PASS. The fix
  is unambiguously what makes them pass.
- Legacy-neutral: the fix lives in `ILIntepreter.Neo.cs` (Neo-gated file). The
  implementer's tasks log records the Legacy register-mode run is unaffected.

### 7. UnitTest_Struct2 progression -- HONEST

`UnitTest_Struct2` (LightTester1.cs:100-116):
- Line 103 `obj.Struct.value = 111` is the raw Stfld THIS child fixes.
- Line 104 `obj.Struct.value += 111` lowers to `ldflda Struct; ldflda value;
  ldind.i4; add; stind.i4` -- the nested-ldflda `+=` path (out of scope).
- Line 105 `Console.WriteLine(obj.Struct.value)` is a raw Ldfld READ (the read
  sibling, out of scope).

With the fix in place, the test now fails with `NullReferenceException` at
`ILIntepreter.Neo.cs:line 5450`, which is inside `case OpCodeREnum.Ldind_I4`
(the READ-side indirect-int load, ILIntepreter.Neo.cs:5444-5453) -- a
completely different opcode handler than `Stfld` (:4128). This proves line 103
(the raw Stfld) now PASSES and the failure moved past it to the `+=`/read
sibling. The residual is genuinely the nested-ldflda / raw-Ldfld-read gap
(design out-of-scope items #1 and #2), NOT this child's raw Stfld. Progression
is honest.

---

## Findings

### M1 -- (Minor, PRE-EXISTING, not this PR) Hash-collision surface of the Area-4d dict
`fieldInfoCache` (CLRType.cs:590) is keyed by `FieldInfo.GetHashCode()`. Two
fields on the same type with colliding hashes would clobber in
`InitializeFields` (last-write-wins) and mis-resolve at runtime. This is the
shared foundation of ALL Area-4d callers (Step 13 + children 4/9/19/24/25/26)
and is NOT introduced by this PR. In practice `System.Reflection
.RuntimeFieldInfo.GetHashCode()` is derived from the field's runtime handle
and is unique per field within a type, so the risk is theoretical. No action
required for this child; noted only for completeness.

### M2 -- (Minor, FORWARD-LOOKING for the F-10 sibling implementer) `off` semantics differ for an IL owner
The new branch's `objIdx >= 0` shape implicitly assumes `off` is a FIELD HASH
(true for a CLR owner per ldflda :1945-1948). For an IL owner, the ldflda
`else` comment (:1950-1952) states `fieldPrimOff` is the field's Primitives
BYTE OFFSET, not a hash. The deferred IL-instance NIE guard (:4220-4221)
correctly protects this invariant TODAY, so the branch is correct as shipped.
When the F-10 sibling is later un-deferred, that implementer must NOT reuse
`NeoReadClrObjectField`/`NeoWriteClrObjectField` directly for the IL-owner
sub-case (they expect a hash) -- they need the F-10 ManagedObjects[refOff]
path. The NIE message already names this, so the handoff is clean. No action
for this child.

### T1 -- (Trivial, PRE-EXISTING) Stale cross-arm comment
The reference-type-owner `else` arm's comment at ~ILIntepreter.Neo.cs:4234
reads "an array-element owner (this change) does box/mutate/unbox via
Array.GetValue/SetValue." That "(this change)" refers to whichever PRIOR
child added the ref-type array-element case; it is NOT this child (this diff
touches only the VT-owner arm :4192-4225). Unchanged by this PR; noting only
to avoid confusion. No action required.

---

## Empirical evidence summary (re-run by this reviewer)

| Check | Result |
|---|---|
| Build CLI `Debug_Neo --no-incremental` | 0 errors |
| Build TestCases `Debug` | 0 errors |
| NeoStep smoke (with fix) | Ran 375, 0 failed, 0 ignored, 0 todos |
| 2 probes alone (with fix) | Ran 2, 0 failed |
| 2 probes alone (engine stashed = HEAD) | Ran 2, 2 failed -- exact NIE at :4193 (objIdx=3, Field a) |
| UnitTest_Struct2 (with fix) | fails at Ldind_I4 :5450 (NRE) -- past line 103, distinct path |

## Diagnosis-correctness verdict

The hash IS genuinely resolvable: the JIT stamps `Operand2` with
`FieldInfo.GetHashCode()` (via GetFieldOffset -> GetFieldIndex ->
fieldMapping) and the runtime resolves it through the same
`FieldInfo.GetHashCode()`-keyed `fieldInfoCache` on the containing owner
CLRType. Generate-side and resolve-side use the identical key on the identical
FieldInfo. The box/mutate/unbox is SOUND: `f` is the leaf field, `boxedStruct`
is the whole struct read via the hash-resolved struct FieldInfo, the SAME box
reference is mutated in place by `FieldInfo.SetValue` and written back by
`NeoWriteClrObjectField`, and field preservation is empirically proven (TC2 =
666, not 333).
