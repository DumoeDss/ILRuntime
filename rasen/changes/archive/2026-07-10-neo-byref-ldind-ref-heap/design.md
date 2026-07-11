# Design: neo-byref-ldind-ref-heap

> Child 15 of the ILRuntime Neo completion portfolio (lead-6 MEDIUM#8).
> Scope: the Step-17 `ldind_ref` / `stind_ref` heap-IL-reference-field read/write
> through a byref produced by `ldflda &heapInstance.<refField>`.

## 1. The gap (lead-6 handoff, `ILIntepreter.Neo.cs:~4310`)

A heap `ILTypeInstance` lays out a reference-typed field (string / IL-class /
object) as a slot in `ManagedObjects[ReferenceOffset]` — NOT in the `Primitives`
byte array. The Step-17 `ldflda` HEAP arm (the runtime `else` branch at
`ILIntepreter.Neo.cs:1449-1464`) stamped the produced byref with
`(objIdx, field.PrimitiveOffset)` — the field's PRIMITIVE byte offset. For a
reference field the PrimitiveOffset is meaningless (the value lives in
ManagedObjects), so a downstream `ldind_ref` (read the reference through the
byref) or `stind_ref` (write it) had no way to recover the reference.

Both `Ldind_Ref` (`:4310`) and `Stind_Ref` (`:4240`) carried an explicit
`throw new NotImplementedException("Step 17: ldind_ref/stind_ref on a heap IL ref
field is deferred (ref-field Ref Slot encoding)")`. The Stind_Ref comment even
documented the intended design ("off is the field's reference offset, stamped by
Ldflda via the Operand3 marker -- not yet wired, so NIE for the heap-ref
sub-case this step").

The IN-FRAME case (a byref to a frame-native ref slot, `objIdx == -1`) was
already handled. The GAP was purely the heap-IL-ref-field sub-case.

## 2. Reproducer (confirmed FAIL-on-HEAD -> PASS-after, stash-toggle proven)

A heap IL class `class C { public string S; }`, obtain a byref to `c.S` via a
`ref` param helper (`ldflda c.S` + the byref crosses an inlined frame), then
`ldind_ref` the byref. On HEAD this NIEs; after the fix it reads the field's
reference correctly. Probes in `TestCases/NeoStep17Test.cs`:

- `NeoStep17_LdindRefHeap_Read` — `ldflda c.S; ldind.ref` (read). FAIL-on-HEAD
  (NIE) -> PASS.
- `NeoStep17_LdindRefHeap_WriteRead` — adversarial: `stind.ref` write `c.S` via
  byref, then `ldind.ref` read back; assert BOTH the direct read (c.S) and the
  byref read observe the write. FAIL-on-HEAD (NIE) -> PASS.
- `NeoStep17_LdindRefHeap_NonZeroRefOffset` — `class { int n; string S; string
  S2; }`: TWO ref fields so S2 has a NON-zero ReferenceOffset (1). Reads BOTH
  ref fields via ldind_ref in one body; asserts n (primitive) untouched + each
  ref field reads its own value. Exercises the offset masking at refOff=0 AND
  refOff=1. FAIL-on-HEAD (NIE) -> PASS.
- `NeoStep17_LdindRefHeap_Control_FrameLocal` — control: in-frame ldind_ref (a
  byref to a frame local holding a reference). PASSES on HEAD AND after.

## 3. The fix

### 3.1 JIT `Ldflda` stamp (the field-type discriminator)

`JITCompiler.cs` `case Code.Ldflda:` already captures
`offset.PrimitiveOffset` (Operand2) and `offset.ReferenceOffset` (Operand3). It
already stamps the F-10 CLR-struct-field marker (`NeoLdfldaClrStructFieldMarker`
= Operand4 bit 0x2) for a CLR-struct field of IL (mutually exclusive with F-6's
in-frame-VT marker 0x1).

ADD a third Operand4 bit, `NeoLdfldaHeapIlRefFieldMarker = 0x4`, stamped when
the declaring type is an ILType AND the field is a non-value, non-primitive type
(a reference field). Mutually exclusive with F-6 (in-frame VT) and F-10 (CLR
value type) — a reference field is neither.

The type-spec pass `case OpCodeREnum.Ldflda:` (the F-10-R1 gate,
`JITCompiler.cs:~1045`) clears BOTH the F-10 marker AND this new marker when the
operand is an in-frame VT source (an IL struct `struct S { public string s; }`
with `ldflda this.s` routes through the F-6 frame-native branch — the ref field
sits in the frame ref region at a frame-relative offset, NOT in
ManagedObjects). The boxed-IL-VT-with-ref-field case keeps the marker (operand
is a heap object).

### 3.2 Runtime `Ldflda` arm — produce the byref

`ILIntepreter.Neo.cs` `case OpCodeREnum.Ldflda:` (`:~1383`): a new branch,
checked FIRST (before the F-10 `clrStructFieldMarker` branch), keyed on
`heapIlRefFieldMarker && objIdx >= 0`. Produces a byref
`(objIdx, ip->Operand3)` — the field's ReferenceOffset, **UNFLAGGED**.

(See 3.4 for why the byref carries NO offset-flag.)

### 3.3 Runtime `Ldind_Ref` / `Stind_Ref` — content-based dispatch

`case OpCodeREnum.Ldind_Ref` / `Stind_Ref` (`:~4218` / `:~4272`): ADD a new arm
that routes the heap-IL-ref-field byref to `ManagedObjects[off]`. The arm is
keyed on `mStack[objIdx] is ILTypeInstance` (content-based), placed AFTER:
1. `objIdx == -1` (frame-native),
2. the F-7B `(off & NeoF10ByrefOffsetFlag) != 0` caller-owned-slot flag (bit 30),
3. `mStack[objIdx] is Array` (CLR array, ldelema),
4. `NeoIsClrObject` (4d CLR-object field, field-hash path).

For Stind_Ref: `refIns.ManagedObjects[off] = vIdx >= 0 ? mStack[vIdx] : null`.
For Ldind_Ref: read `refIns.ManagedObjects[off]`, materialize into the dest ref
slot (`dstIdx = frameRefBase + ip->Operand3`).

The OLD NIE else-fallbacks are kept (tagged "unsupported byref shape") as the
defensive guard for genuinely-unsupported shapes.

### 3.4 Why content-based dispatch (NOT a bit-flag) -- the load-bearing finding

The first implementation stamped a second offset-half bit-flag
(`NeoHeapIlRefFieldByrefFlag = 0x80000000`, bit 31) on the byref, mirroring the
F-10 bit-30 idiom, and dispatched on `(off & 0x80000000) != 0`. This introduced
an INTERMITTENT REGRESSION in the PRE-EXISTING `NeoStep17_ClrObjectRefFieldReadWrite`
probe (4d.3, a CLR object's reference field via field-hash): it flaked ~30% of
NeoStep17 runs (stable on HEAD 10/10).

ROOT CAUSE: a CLR OBJECT field byref (the 4d path) carries `(clrObjIdx,
fieldHash)` where `fieldHash` is the runtime CLR FieldInfo hash
(`type.GetFieldIndex(token)` -> a `GetHashCode`-derived value). These hashes are
NON-DETERMINISTIC across runs (process-global-counter-based, same family as the
Cecil TypeReference/MethodReference identity hashes documented in S3-2). On runs
where the hash happened to set bit 31, the bit-31 flag check fired on a CLR
object -> routed to `GetNeoILInstance` -> the "field/element access on a CLR
object via the IL-instance path" NIE.

(The F-10 bit-30 flag has the SAME theoretical exposure, but its byrefs are only
produced for IL-declared CLR-struct fields -- the JIT stamps bit 0x2 at ldflda
time, so the offset is a controlled small ReferenceOffset, not a raw field-hash.
The 4d CLR-object path produces a raw field-hash offset, which is what collides.)

THE FIX: dispatch on the mStack CONTENT (`mStack[objIdx] is ILTypeInstance`),
not on a bit in the offset half. This is unambiguous for ALL offset values:
- heap-IL-ref-field byref: `mStack[objIdx]` IS an ILTypeInstance (the holder).
- CLR-object field byref: `mStack[objIdx]` is a CLR object (NeoIsClrObject
  catches it in arm 4, before the ILTypeInstance arm).
- CLR array byref: `mStack[objIdx]` is an Array (arm 3).
- F-7B caller-owned-slot byref: sets the bit-30 flag (arm 2) AND its mStack slot
  holds the referent (a string/CLR ref). An F-7B-promoted IL-CLASS referent
  (`ref SomeIlClass` via delegate-Invoke) WOULD have `mStack[objIdx] is
  ILTypeInstance` -- but it is caught by arm 2 (bit-30 flag) FIRST, so it never
  reaches the ILTypeInstance arm. (No probe exercises `ref IlClass` via
  delegate-Invoke; the arm-2-first ordering is the correctness guarantee.)

Content-based dispatch removed the `NeoHeapIlRefFieldByrefFlag` constant
entirely; the byref carries the raw ReferenceOffset. NeoStep17 stable 12/12
after the pivot (was 7/10 with the bit-flag).

## 4. Scope boundary

IN SCOPE: `ldind_ref` / `stind_ref` on a byref produced by `ldflda` of a
REFERENCE-TYPED field of a HEAP IL class (single-level; refOff 0 and non-zero).

OUT OF SCOPE (not a C#-compiler-emitted shape for ref load/store, or separate):
- `ldobj` / `stobj` (value-type-sized copy) on a heap IL ref field -- a whole
  reference read/written through an IL-instance byref goes through
  ldind_ref/stind_ref, not the value-type-sized ldobj/stobj. (The Ldobj/Stobj
  arms' F-10 bit-30 CLR-struct path is unchanged; a ref field is not value-typed
  so it never hits ldobj/stobj.)
- A byref to a heap CLR-STRUCT field (handled by F-10, unchanged).
- A byref to a NESTED field (ldflda chain) -- not exercised; the content-based
  arm would route correctly for a single-level nested ref field (the byref's
  objIdx is the holder instance), but a multi-level chain is untested.
- A byref PARAMETER crossing a frame that is NOT inlined -- the inliner folded
  the probe helpers (`LdindRefHeapRead`/`StindRefHeapWrite`) inline, so the
  byref stayed in one frame. A cross-frame IL-byref to a heap ref field would
  flow through `CopyNeoCallArguments` (the 4c path copies it raw as an
  mStack-object byref, `objIdx != -1` -> no rebase) and the callee's ldind/stind
  would dispatch content-based correctly (the objIdx is an absolute mStack index
  to the holder instance, valid for the call's lifetime). Not independently
  probed but structurally sound.

## 5. Verification

- NeoStep smoke: **263/0/0** (259 baseline + 4 probes), stable across 5+ runs.
- NeoStep17 gate: **51/0/0** (47 baseline + 4 probes), stable across 12+ runs
  (NO flake after the content-based-dispatch pivot).
- Stash-toggle: the 3 engine-dependent probes FAIL-on-HEAD (NIE) -> PASS-after;
  the control (frame-local) PASSES both.
- Legacy-neutral: `dotnet build ILRuntime/ILRuntime.csproj -c Debug` = 0 errors
  (all edits `#if ENABLE_NEO_MODE`).
- NeoOptHardening / NeoStep19 (delegate-byref, the F-7B/F-7B-SIB callers of the
  shared Stind_Ref/Ldind_Ref arms) unaffected: the full NeoStep smoke includes
  them and stays green.
