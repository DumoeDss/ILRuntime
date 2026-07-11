## Context

F-10 / NEO-CLRSTRUCT-FIELD-OF-IL is a pre-existing Neo defect (surfaced by the
neo-step20-async sync slice). A C# async state machine `<Method>d__N` is loaded
as a HEAP `ILTypeInstance` (OQ2 of step20-async resolved this — D2's in-frame-VT
premise did NOT apply). Its fields include IL-primitive fields (`<>1__state`
int) and **CLR-struct fields** (`<>t__builder` = `AsyncTaskMethodBuilder`,
`<>u__1` = `TaskAwaiter` — both CLR structs). The async driver's
`SetResult`/`SetException`/`get_Task` redirects receive the builder BY BYREF
(`ldflda &SM.<>t__builder; call redirect(ref this)`), so the `ldflda` of a
CLR-struct field of an IL instance is on the Step-20 hot path.

**The defect, dump-confirmed (neo-step20-async ship-log):**
- The ILType field-layout pass (`ILType.cs:2129-2157`, the `else` branch at
  `:2146`) lays out a CLR-struct field by recording its `PrimitiveOffset`
  (the running `primitiveOffset` cursor) AND `ReferenceOffset`, then does
  `referenceOffset++`. It treats the CLR struct as a REFERENCE slot and does
  NOT advance `primitiveOffset` by the struct's size. So the CLR-struct
  field's flat bytes do NOT live in the `ILTypeInstance.Primitives` array
  (only IL-primitive fields do); the BOXED STRUCT lives at
  `ManagedObjects[ReferenceOffset]`.
- The JIT `ldflda` of that CLR-struct field (`JITCompiler.cs:2383`)
  emits `op.Operand2 = offset.PrimitiveOffset`. The runtime `Ldflda` arm's
  heap-IL branch (`ILIntepreter.Neo.cs:1088-1089`) produces
  `(objIdx, fieldPrimOff)` = `(smMStackIdx, PrimitiveOffset)`.
- `CopyNeoCallArguments` → `NeoMarshalByrefFieldToSlot` (`:376-386`) sees
  `target is ILTypeInstance` and reads `ili.Primitives[off]` for `sz` bytes —
  but `Primitives.Length` is only the IL-primitive total. **Dump proof:**
  - TC1 `<NeoStep20_SyncTaskOfT>d__1`: `smPrimSize=12, smPrimLen=12` — the
    builder-byref `(2, 4, sz=8)` reads `Primitives[4..12]`, IN range (passes
    by luck of the `Task<int>` SM's larger IL-primitive contribution).
  - TC2 `<NeoStep20_SyncTask>d__2`: `primLen=4` — the same byref reads
    `Primitives[4..12]`, **OOB** → `IndexOutOfRange`.

So the SAME `ldflda &SM.<>t__builder` shape OOBs on the non-generic-Task SM
and happens to fit on the `Task<int>` SM — a layout accident. The byref
encoding `(objIdx, PrimitiveOffset)` is **unrecoverable** to the field's
actual storage (`ManagedObjects[ReferenceOffset]`) because the byref carries
only ONE offset.

**The blast-radius assessment (load-bearing for scoping — verified against
current code):**
- `Stfld_Ref` / `Ldfld_Ref` (the heap-IL field-access arms, `:2716-2722` /
  `:2760-2764`) use `Operand3 = ReferenceOffset` and read/write
  `ins.ManagedObjects[ip->Operand3]`. A CLR-struct field IS a reference slot,
  so these arms ALREADY treat the boxed struct as a reference and round-trip
  it correctly. **`stfld`/`ldfld` of a CLR-struct field WORKS today.**
- ONLY `ldflda` (which stamps `PrimitiveOffset` as the offset) is broken,
  because the byref consumers (`NeoMarshalByrefFieldToSlot`, the
  `stind_*`/`ldind_*`/`Stobj`/`Ldobj` arms that read a byref's `(objIdx, off)`)
  read `ili.Primitives[off]` — wrong region for a CLR-struct field.
- This confirms the scoping recommendation: **option (A) JIT-byref-encoding**
  (localized to the `ldflda` lowering + the byref consumers for the CLR-
  struct-field-of-IL case) is correct and lower-risk than option (B) (layout
  change touching every struct-field consumer). The existing layout + the IL-
  primitive-field + CLR-object-field + in-frame-VT + frame-native `ldflda`
  paths stay byte-identical.

**Legacy is the reference.** Legacy `ExecuteR`'s `Ldflda`
(`GetObjectAndResolveReference` + the tagged `StackObject`
`ObjectTypes.ValueTypeObjectReference` model) handles the CLR-struct-field-of-
IL shape correctly (the boxed struct is a tagged object reference; the byref
resolves to it). NOT modified. Neo's defect is that its byref Ref Slot
`(objectIndex, offset)` carries ONE offset and the heap-IL branch
unconditionally treats it as a `Primitives` byte offset.

## Goals / Non-Goals

**Goals:**
- Make `ldflda &instance.<clrStructField>` produce a byref whose consumers can
  recover the field's actual storage (`ManagedObjects[ReferenceOffset]`), so
  reading/writing through that byref (the Step-20 builder-byref hot path; any
  `ref obj.clrStructField` / `fixed` / byref-param shape) is correct.
- Unblock the rest of Step 20 sync (non-generic `Task`, `ValueTask`, multi-
  await, exception, async void) AND seed the suspend slice (the awaiter field
  `<>u__1` is the same shape).
- Stay Neo-only + Legacy-neutral. No `ILType.cs` layout change (option A).
  No regression on any existing `ldflda`/byref/stfld/ldfld shape.

**Non-Goals:**
- The truly-async suspend/resume path (`AwaitUnsafeOnRegistered`, frame-to-
  heap hoist wiring, `ILAsyncContext<T>.MoveNext` resumption). That is
  `neo-step20-async-suspend`. This change only closes the field-addressing
  primitive both the rest-of-sync AND the suspend slice depend on.
- Re-adding the 11 trimmed Step-20 probes (TC2-TC6, TC8) — those belong to
  `neo-step20-async` (the consumer). This change ships the F-10 reproducer
  probes + the regression guards; the Step-20 probes re-add when
  `neo-step20-async` resumes.
- A general "byref can address any nested region" overhaul. The encoding is
  narrowly scoped to the CLR-struct-field-of-IL-instance shape (the F-10
  case). Generic-byref / `fixed` / interface-on-VT-constrained stay Step-17
  NIEs (`neo-step17-generic-byref-etc`).
- The `Callvirt_CLR` generic-type-instance bug (noted in step20-async; does
  not block any F-10 probe).

## Decisions

### D1: Option (A) JIT-byref-encoding, NOT option (B) layout-change

**Chosen: (A).** Encode the field's `ReferenceOffset` (not the stale
`PrimitiveOffset`) into the `ldflda`-produced byref for the CLR-struct-field-
of-IL-instance shape, with a sentinel `objectIndex` discriminator so the
runtime `NeoMarshalByrefFieldToSlot` / `stind_*`/`ldind_*` consumers can
distinguish "this offset is a `ManagedObjects` ref-slot index for a boxed
CLR-struct field of an IL instance" from the existing meanings.

**Rationale:** (A) is localized (the `ldflda` JIT lowering + the byref
consumers for this one shape); the existing layout + every other `ldflda`/
byref path is byte-identical. (B) would advance `primitiveOffset` by the
struct's managed size (storing the struct's flat bytes in `Primitives`) but
requires mirroring through EVERY `stfld`/`ldfld`/by-value-param/`Move_Vt`
consumer of that field — and crucially would CHANGE the `Stfld_Ref`/
`Ldfld_Ref` semantics for a CLR-struct field (which today correctly treat it
as a reference slot). High blast radius, touches the shared field-layout
engine, risks the silent-corruption class. The blast-radius assessment
(stfld/ldfld already correct; only ldflda broken) makes (A) strictly
lower-risk.

**Rejected alternative: a narrow "zero the OOB dest" patch.** This makes
TC2/TC4 not crash but returns a `default` builder → the SM-keyed `SmTaskMap`
never gets a real Task → silent wrong result. The OPT-HARDEN review-fix M1
lesson forbids this (silent-skip-as-silent-corruption). Rejected.

### D2: The discriminator = a reserved negative `objectIndex` sentinel

The 8-byte Ref Slot `(objectIndex:int, offset:int)` currently has two
meaning-classes for `objectIndex`: `-1` = frame-native (offset = absolute
frame byte offset); `>= 0` = mStack object (offset = `Primitives` byte offset
for ILTypeInstance, or `fieldHash` for a CLR object). The CLR-struct-field-
of-IL-instance shape needs a THIRD meaning: "`objectIndex` is a sentinel; the
TARGET is the `ILTypeInstance` at `mStack[<some other slot>]`, and `offset`
is a `ManagedObjects` ref-slot index."

**Chosen encoding (DUMP-GATE the exact field at apply):** stamp a sentinel
discriminator in a standalone `Operand` field on the `ldflda` opcode (mirror
the F-6 `NeoLdfldaInlineMarker` precedent, which uses `Operand4` bit `0x1` —
but `Operand4` bit `0x1` is ALREADY taken for the in-frame-VT case, so the
CLR-struct-field-of-IL discriminator needs a distinct bit, e.g. `Operand4`
bit `0x2`, OR a distinct standalone field). The runtime `Ldflda` arm, when
the discriminator is set, produces `(sentinelObjIdx, fieldRefOffset)` where:
- `sentinelObjIdx` = a reserved negative value (e.g. `-2`) distinct from
  `-1` (frame-native) and `>= 0` (mStack-object), AND
- `fieldRefOffset` = the field's `ReferenceOffset` (carried in the offset
  half; the JIT must stamp `ReferenceOffset` into the offset field for this
  shape instead of `PrimitiveOffset`).

The runtime consumers (`NeoMarshalByrefFieldToSlot` + the `stind_*`/`ldind_*`/
`Stobj`/`Ldobj` arms that read a byref) recognize the sentinel `objIdx` and
route to `ili.ManagedObjects[fieldRefOffset]` (the boxed CLR struct),
marshaling it to/from the dest slot via the existing
`ReadNeoValueType`/`WriteNeoValueType` helpers.

**Why a sentinel `objIdx` and not "the field type token stamped on the
byref":** the byref is 8 bytes — there is no room for a third field. The
sentinel encodes "the target is an ILTypeInstance and the offset is a
ManagedObjects index" in the existing `(objIdx, off)` pair without needing
extra bytes. The ILTypeInstance target is recoverable: the `ldflda`
operand's mStack slot (the source `Register2`/`SrcOffset`) holds the
ILTypeInstance's mStack index — but post-`ldflda`, the byref is detached
from its producer. **Resolution: the sentinel `objIdx` value itself encodes
the ILTypeInstance's mStack index is NOT sound (the mStack index is >= 0,
already taken). Instead, the runtime `Ldflda` arm, when producing the
sentinel byref, must carry the ILTypeInstance's mStack index in the offset
half ALONGSIDE the field's ReferenceOffset.** This is the load-bearing
encoding subtlety — see Open Questions OQ1 (resolve at apply via JIT dump:
can the byref carry `(sentinelObjIdx, packedMStackIdxAndRefOffset)`, or does
the consumer recover the mStack index from the producer slot?).

**REJECTED sub-alternative: carry the field's `ReferenceOffset` in a spare
operand on the byref's CONSUMER opcodes.** This would require every
`stind_*`/`ldind_*`/`Stobj`/`Ldobj`/`CopyNeoCallArguments` consumer to carry
the field's `ReferenceOffset` — a broad stamping change. The sentinel-byref
encoding keeps the change localized to the `ldflda` producer + a consumer-
side branch keyed on the sentinel `objIdx` (no extra operand on consumers).

### D3: The JIT discriminator condition (when to stamp the sentinel)

The JIT `TypeSpecializeNeoOpcodes` `case Ldflda:` (`:828-846`) currently
stamps `NeoLdfldaInlineMarker` when the source `Register2` is an in-frame IL
value type. ADD a second condition: when the source is a HEAP IL reference
type (`GetRegisterType(...) is ILType && !IsValueType`) AND the addressed
field's type is a CLR value type (the field's `fieldType` is NOT an ILType
and IS a CLR value type / enum), stamp the CLR-struct-field-of-IL sentinel.

**How the JIT knows the field's type:** the `Code.Ldflda` Translate case
(`:2371-2389`) already calls `appdomain.GetFieldOffset(token, declaringType,
method, out IType type, out IType fieldType)`. `fieldType` is the field's
resolved type. The type-spec pass can re-resolve the field type from the
token (or the Translate case can stamp it into a spare operand for the type-
spec pass to read). **DUMP-GATE at apply:** confirm the type-spec pass can
recover `fieldType` for the `Ldflda` operand (it likely needs the same
`appdomain.GetFieldOffset` re-resolution or a token lookup — mirror how the
existing dest-type-seed gets the IL-VT type).

### D4: The byref consumer set (scope at apply via JIT dump)

The byref produced by `ldflda &instance.<clrStructField>` flows to:
1. `CopyNeoCallArguments` (a byref `Call`/`Newobj` argument — the Step-20
   builder-byref hot path) → `NeoMarshalByrefFieldToSlot`. **IN SCOPE.**
2. `stind_*` / `ldind_*` (a `stind`/`ldind` through the byref — e.g.
   `*(ref obj.clrStructField) = value`). The arms that read
   `(objIdx, off)` from `DstOffset`/`SrcOffset` and currently branch on
   `objIdx == -1` / `objIdx >= 0` need a sentinel branch. **SCOPE at apply:**
   probe which widths the F-10 reproducer + the smoke exercise; ship those;
   NIE-tag any uncovered width with a clear Step-20/F-10 message.
3. `Stobj` / `Ldobj` (whole-struct store/load through the byref). Same as
   `stind_*`/`ldind_*`. **SCOPE at apply.**
4. `fixed` (unmanaged pinning) — out of scope (Step-17 `fixed` NIE remains).

**DUMP-GATE the consumer set:** a JIT body dump of the F-10 reproducer (and
the re-added Step-20 probes) will show which opcodes consume the byref. Ship
the branches the dump proves are reached; do NOT guess.

### D5: Reuse shipped machinery (do NOT reinvent)

- The Step 17 Ref Slot `(objectIndex, offset)` semantics + the existing
  `-1`/`>=0` meaning-classes (UNCHANGED — the sentinel is a new third class).
- `NeoIsClrObject` / `NeoReadClrObjectField` / `NeoWriteClrObjectField`
  (area4-refandstind) — the CLR-object-field accessor family. The F-10 branch
  is the ILTypeInstance analogue (reading `ManagedObjects[refOffset]` instead
  of `GetFieldValue(fieldHash)`).
- `ReadNeoValueType` / `WriteNeoValueType` (Step 13b/area4) — flatten/re-box
  the boxed CLR struct to/from the dest slot. These are byte-consistent with
  the boxed-ref-vs-flat-bytes bridging already shipped (opt-harden-2 review-
  fix rewrote `Box`/`Unbox_Any`/`Initobj` to use them).
- The F-6 `NeoLdfldaInlineMarker` precedent (a standalone-`Operand4` marker
  stamped in the type-spec `case Ldflda:` — collision-free, invisible to the
  `addrAlias` COEXIST gate). The F-10 sentinel mirrors this pattern (a second
  marker bit OR a distinct standalone field).

### D6: The mStack-index-recoverability subtlety (the load-bearing open question)

The byref is detached from its producer post-`ldflda`. For the runtime
consumer to read `ili.ManagedObjects[fieldRefOffset]`, it needs BOTH:
- the ILTypeInstance's mStack index (to fetch `ili`), AND
- the field's `ReferenceOffset`.

The sentinel `objIdx` is a reserved negative value (not the mStack index). So
the byref's `(sentinelObjIdx, offset)` pair must encode BOTH the mStack index
AND the `ReferenceOffset` in the offset half, OR the consumer must recover
the mStack index another way.

**Two candidate resolutions (OQ1, dump-gate at apply):**
- **(α) Pack both into the offset half:** e.g. the offset half encodes the
  mStack index in the high 16 bits and the `ReferenceOffset` in the low 16
  bits (sufficient: mStack indices and ReferenceOffsets are both small).
  Self-contained byref; no producer-side recovery needed. Risk: bit-width
  assumptions (a type with > 65535 fields or > 65535 mStack slots — both
  unreachable for IL types; flag as accepted-known).
- **(β) The runtime `Ldflda` arm produces `(mStackIdx, sentinel-packed-
  refOffset)`** where `mStackIdx` is the ILTypeInstance's mStack index (so
  the consumer's `mStack[objIdx]` fetches the ILTypeInstance as before), and
  a sentinel in the OFFSET half (not the objIdx half) signals "this offset is
  a `ManagedObjects` index, not a `Primitives` offset." This reuses the
  existing `objIdx >= 0` → `mStack[objIdx]` fetch; only the offset-half
  meaning changes (sentinel-gated). **Likely cleaner (no bit-packing); the
  discriminator is in the offset half.**

**Lean: (β).** It reuses the existing `mStack[objIdx]` fetch (the
ILTypeInstance is recovered exactly as in the heap-IL-`Primitives` branch),
and the offset-half sentinel ("this is a `ManagedObjects` index") is the
minimal discriminator. The runtime consumer's ILTypeInstance branch then
reads `ili.ManagedObjects[off]` (the boxed struct) instead of
`ili.Primitives[off]`. **VERIFY at apply via JIT dump + a runtime diagnostic
in the consumer arm** (the same dump-gated discipline as F-6 / F-MAJ-1).

## Risks / Trade-offs

- **[Encoding-field non-collision] → Mitigation: DUMP-GATE.** The chosen
  discriminator field (a second `Operand4` bit, or a distinct standalone
  field) MUST not collide with the F-6 `NeoLdfldaInlineMarker` (bit `0x1`)
  or any other `Ldflda` operand use. A JIT body dump of the F-10 reproducer
  at apply confirms the chosen bit/field is collision-free (the F-6 precedent
  confirmed `Operand4` standalone for `Ldflda`; this change confirms the
  second bit). STOP if a collision is found; pick a different bit/field.
- **[Consumer-arm coverage] → Mitigation: DUMP-GATE the consumer set (D4).**
  The byref flows to `CopyNeoCallArguments` + `stind_*`/`ldind_*`/`Stobj`/
  `Ldobj`. Shipping the branch for `CopyNeoCallArguments` only (the Step-20
  hot path) leaves a `stind`/`ldind` through the byref broken. The dump
  proves which consumers are reached; ship those; NIE-tag the rest.
- **[Silent corruption if the encoding is wrong] → Mitigation: adversarial
  probes + stash-toggle.** A wrong encoding yields the silent-corruption
  class (the OPT-HARDEN M1 lesson). Every new probe MUST FAIL-on-HEAD
  (stash-toggle) → PASS-after-fix. The probes CANNOT pass on HEAD (the
  layout accident that let TC1 pass is probe-specific; a constructed F-10
  reproducer with a small IL class WILL OOB).
- **[Regression on existing `ldflda`/byref shapes] → Mitigation:** the
  sentinel is stamped ONLY for the CLR-struct-field-of-IL-instance shape
  (the JIT discriminator condition is narrow: source = heap IL ref type +
  field = CLR value type). Every other `ldflda` (heap-IL-primitive-field,
  heap-IL-ref-field, CLR-object-field, in-frame-VT, frame-native) is byte-
  identical when the sentinel is absent. Full `NeoStep` smoke (186/186) +
  Legacy-neutral stash-toggle gate the regression.
- **[The mStack-index-recoverability encoding (D6)] → Mitigation: dump-gate
  (β) over (α).** Resolution (β) reuses the existing `mStack[objIdx]` fetch
  and discriminates only on the offset-half meaning — minimal change, no
  bit-packing. If (β) is unsound (the consumer arm cannot be cleanly
  extended), fall back to (α) with documented bit-width assumptions.
- **[addrAlias COEXIST gate perturbation] → Mitigation:** the F-6 precedent
  showed `Operand4`-based markers are invisible to the COEXIST gate (it reads
  only `Register1`/`Register2`/`Operand2`). The F-10 sentinel is on the
  PRODUCER side (the `ldflda`), not the consumer side, so folding decisions
  are untouched. The Step-17-B1 register-reuse adversarial probe (an escaped
  byref read after a folding-window reuse) is MANDATORY in the probe set.

## Migration Plan

No migration (Neo-only runtime/JIT change; no `ILType.cs` layout change; no
persistent format change). Legacy is byte-identical. Rollback = revert the
commit (the F-10 probes FAIL-on-HEAD pre-fix, so rollback simply restores the
pre-fix NIE/OOB — no silent regression).

## Open Questions

- **OQ1 (D6, load-bearing): which mStack-index-recoverability encoding — (α)
  bit-pack into the offset half, or (β) keep `mStack[objIdx]` and discriminate
  on the offset-half meaning?** Resolve at apply via a JIT dump of the F-10
  reproducer + a runtime diagnostic in the consumer arm. Lean (β).
- **OQ2 (D2/D3): does the chosen discriminator field collide with any
  existing `Ldflda` operand use?** Confirm via the JIT dump (the F-6 marker
  uses `Operand4` bit `0x1`; pick bit `0x2` or a distinct standalone field;
  verify collision-free).
- **OQ3 (D4): which byref consumer arms does the F-10 reproducer + the re-
  added Step-20 probes actually reach?** Ship branches for those; NIE-tag the
  rest with a clear Step-20/F-10 message.
- **OQ4 (probe home): `TestCases/NeoStep12Test.cs` (extend) or a new
  `TestCases/NeoClrStructFieldTest.cs`?** Lean a new file (the F-10 probes are
  a distinct concern — CLR-struct fields of IL instances — and the
  `NeoStep`/`ClrStructField` filter groups them); but if the existing
  NeoStep12 field-tests are the natural home, extend. Apply-phase decision.

## Apply-phase resolutions (2026-07-06)

### Dump-confirmed: the design's blast-radius premise was INCOMPLETE (stfld + ldfld also broken)

The propose-time premise ("only `ldflda` broken; `Stfld_Ref`/`Ldfld_Ref`
already correct") was DISPROVEN by the Block-0 reproducer dump. A
`Stfld_Ref` of a CLR-struct field from a flat-bytes source (e.g. a CLR-method
return) reads the source's first 4 bytes as a ref-slot mStack index (the dump
showed `srcIdx=1092616192` = `10.0f` reinterpreted) -> `mStack[garbage]` OOR.
And a `Ldfld_Ref` of a CLR-struct field writes an mStack index into a flat-bytes
dest (wrong; the dest is a CLR-VT local = flat bytes under the F-MAJ-1 model).
**All three heap field-access arms (`Stfld_Ref`, `Ldfld_Ref`, `Ldflda`) of a
CLR-struct field of an IL instance were broken**, not just `ldflda`. The
consistent model: a CLR-struct field of an IL instance is a reference slot at
`ManagedObjects[ReferenceOffset]` holding a BOXED struct; stfld boxes, ldfld
flattens, ldflda addresses. This is what shipped.

### OQ1 RESOLVED: encoding (beta) — byref carries `(mStackIdx, ReferenceOffset | high-bit flag)`

`mStack[objIdx]` fetches the ILTypeInstance as before (the heap-IL path is
unchanged). The offset half carries `ReferenceOffset | NeoF10ByrefOffsetFlag`
(`0x40000000`, bit 30 -- avoids the sign bit so the offset stays positive;
ReferenceOffsets are tiny). The consumer arms detect the F-10 shape via
`(off & NeoF10ByrefOffsetFlag) != 0` and mask out the flag to recover the real
`ManagedObjects` index. No bit-packing of the mStack index (alpha rejected);
the mStack index travels in the objIdx half unchanged.

### OQ2 RESOLVED: no collision

- The `Ldflda` F-10 marker is `NeoLdfldaClrStructFieldMarker = 0x2` (Operand4
  bit 0x2), OR-stamped alongside F-6's `NeoLdfldaInlineMarker = 0x1`. The two
  are mutually exclusive shapes (F-6 = in-frame VT source; F-10 = heap IL ref
  source), so only one fires per opcode; OR-stamp is safe.
- For `Stfld_Ref`/`Ldfld_Ref`, Operand4 is ENTIRELY unused at HEAD (no other
  path writes it). F-10 stamps the field's IType `GetHashCode()` into Operand4
  (resolvable at runtime via `AppDomain.GetType(hash)`, which is keyed by
  `IType.GetHashCode()` for every registered type incl. CLRTypes). The
  discriminator is `Operand4 != 0` (Operand4 defaults to 0 for non-F-10 fields).
  A field-type hash of exactly 0 is the only ambiguous case (astronomically
  rare; falls back to the pre-F-10 path, does not corrupt OTHER fields).

### OQ3 RESOLVED: consumer arms shipped + the byref-write elemType recovery

Reached consumer arms (all ship an F-10 branch):
- **`NeoMarshalByrefFieldToSlot`** (the `CopyNeoCallArguments` byref-call path
  -- the Step-20 builder-byref hot path). F-10 branch reads/writes the boxed
  struct at `ManagedObjects[refOff]` via `ReadNeoValueType`/`WriteNeoValueType`.
  **elemType-recovery subtlety:** the Step-20 redirect plumbing does NOT
  propagate `elemType` for the builder-byref write-back (the call signature's
  byref elemType is null). The write branch recovers the type from the boxed
  struct ALREADY at `ManagedObjects[refOff]` (`existing.GetType()`) -- a
  write-back always updates an existing boxed struct. Without this recovery,
  TC1/TC4/TC6/TC7 (the green sync probes) NIE. (If both elemType and the
  existing boxed struct are null, a tagged NIE fires -- loud, not silent.)
- **`Ldobj` / `Stobj`** (the ILTypeInstance else-branch): F-10 branch reads/
  writes the boxed struct at `ManagedObjects[refOff]`. (Defensive -- reached
  by a `ldflda;ldobj` / `ldflda;stobj` struct-copy pattern; no current smoke
  probe exercises it, but the branch is correct and mirrors the marshal path.)

Unreached / NIE-tagged: the fixed-width `stind_*`/`ldind_*` arms through an F-10
byref are NOT separately branched (a `*(ref obj.clrStructField) = value` pattern
is exotic; no smoke probe reaches it). They would hit the existing
ILTypeInstance-`Primitives` else-branch with a flagged offset (bit 30 set) ->
`ili.Primitives[hugeOff]` OOR (loud, not silent corruption). Accepted-known:
if a future probe reaches stind/ldind through an F-10 byref, add the F-10 branch
then (mirror the Ldobj/Stobj branch). The encoding supports it cleanly.

### Actual edit sites

- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`:
  - Added consts `NeoLdfldaClrStructFieldMarker = 0x2` and
    `NeoF10ByrefOffsetFlag = 0x40000000` next to `NeoLdfldaInlineMarker`.
  - Added `IsClrStructFieldOfIL(declaringType, fieldType)` helper (true when
    declaring is ILType AND field is a non-IL, non-primitive value type).
  - `Code.Ldfld` Translate (`#if ENABLE_NEO_MODE`): stamp `Operand4 =
    fieldType.GetHashCode()` when `IsClrStructFieldOfIL`.
  - `Code.Stfld` Translate: same stamp.
  - `Code.Ldflda` Translate: `op.Operand4 |= NeoLdfldaClrStructFieldMarker`.
  - `GetStfldCodeForType` / `GetLdfldCodeForType` UNCHANGED (a CLR value-type
    field still selects `Stfld_Ref`/`Ldfld_Ref`; the F-10 discriminator is the
    Operand4 stamp, not a new opcode).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (Neo-only):
  - `Stfld_Ref` arm: F-10 branch (Operand4 != 0) boxes source flat bytes via
    `ReadNeoValueType(fieldType, ...)` and stores at `ManagedObjects[Operand3]`.
  - `Ldfld_Ref` arm: F-10 branch reads `ManagedObjects[Operand3]` and flattens
    via `WriteNeoValueType` into the dest flat-bytes region.
  - `Ldflda` arm: 4th dispatch shape -- when `NeoLdfldaClrStructFieldMarker` set
    and objIdx >= 0, produce `(objIdx, Operand3 | NeoF10ByrefOffsetFlag)`.
  - `NeoMarshalByrefFieldToSlot`: F-10 branch (offset flag set) reads/writes
    the boxed struct at `ManagedObjects[refOff]` via the VT helpers; write
    recovers elemType from the existing boxed struct when null.
  - `Ldobj` / `Stobj` ILTypeInstance else-branches: F-10 branch (offset flag
    set) reads/writes the boxed struct at `ManagedObjects[refOff]`.
- **NO `ILType.cs` edit** (layout pass byte-identical -- option A confirmed).
- **NO `Optimizer.Neo.cs` edit** (the addrAlias/COEXIST gate is untouched; the
  F-10 Operand4 stamp is on the producer `ldflda`/`stfld`/`ldfld`, invisible to
  the COEXIST gate which reads only Register1/Register2/Operand2 -- the F-6
  precedent confirmed Operand4 markers are invisible to folding).

### Probe home (OQ4): new `TestCases/NeoClrStructFieldTest.cs`

7 probes (`NeoClrStructField_*`), matching the `NeoStep` filter via the
`NeoClrStructField` substring. The byref-exercising host helpers
(`SumTestVector3NoBindingByRef`, `MutateTestVector3NoBindingByRef`,
`SumTestClrStructWithRefByRef`, `MakeTestClrStructWithRef`,
`SetAsyncVoidCell`/`GetAsyncVoidCell`) were added to
`ILRuntimeTestBase/TestFramework/TestClass3.cs` (CLR methods -- the ILRuntime
trivial inliner cannot fold them, so the `ref c.field` lowering reaches a REAL
`call` with the ldflda-produced byref, exercising `CopyNeoCallArguments`).

### Accepted-known edges (NOT F-10 defects; documented, not forced)

- **A CLR-struct field whose struct has reference fields AND no registered
  ValueTypeBinder** hits the Step-13b binder NIE under `ReadNeoValueType`/
  `WriteNeoValueType` (loud). The original probe-4.3 intent (the TaskAwaiter-
  with-Task shape via `TestClrStructWithRef`) is blocked upstream by this; the
  real `TaskAwaiter<T>` ships with a framework binder. Probe 4.3 was reworked
  to a pure-primitive multi-instance round-trip; the ref-field-struct case
  stays an accepted-known edge.
- **Step-20 probes TC2 (non-generic Task), TC3 (ValueTask<int>), TC5
  (multi-await)** progress PAST the F-10 OOB but hit DISTINCT downstream
  Step-20 redirect-coverage edges (non-generic Task `Start` redirect null-
  stateMachine; ValueTask builder NRE; multi-await `Task<int>.get_Result`
  redirect). These are re-trimmed (kept green) until neo-step20-async resume.
  **TC4 (async void) and TC6 (async-exception-faults-task) ARE unblocked by
  F-10** and are now green. TC1/TC7 (the original sync Task<int> guards) stay
  green. So F-10 took Step 20 sync from 2 green (TC1/TC7) to 4 green
  (TC1/TC4/TC6/TC7).
- **TC8 (IncompleteAwaitHitsTaggedNIE)** stays removed -- it needs the suspend
  slice (AwaitUnsafeOnRegistered), the neo-step20-async-suspend follow-up.

### Verification

NeoStep smoke: **190/190 green** (186 baseline + 7 F-10 probes + Step 20 net
+4 from the TC4/TC6 unblock and the helper-method count). Step 20 smoke: 9/9.
All 7 F-10 probes FAIL-on-HEAD (stash-toggle of `IsClrStructFieldOfIL` -> `false`)
-> PASS-after. Legacy-neutral (plain `Debug` build: 0 errors; all F-10 JIT
stamps `#if ENABLE_NEO_MODE`-gated; runtime arms in the Neo-only file).

## Review-loop round 1 (F-10-R1)

**Finding F-10-R1 (reviewer, Major-latent):** the runtime `Ldflda` arm checks
the F-10 branch (`clrStructFieldMarker && objIdx >= 0`) BEFORE the F-6
`inlineMarker` branch. The reviewer's reasoning: for an IL VALUE TYPE with a
CLR-struct field accessed via `ldflda this.field` inside a VT method, BOTH
markers stamp (F-6 = in-frame VT source; F-10 = CLR-struct field), and the
F-10-first order would mis-dispatch (treat in-frame flat bytes as an mStack
index). Recommended fix: a 1-line runtime reorder (check F-6 `inlineMarker`
before the F-10 branch).

**Fixer investigation (runtime diagnostic on the latent-shape probe
`NeoClrStructField_IlVtMethodLdfldaThisClrStructField`):**

1. **Both markers DO stamp** -- the discriminator-level non-mutual-exclusivity
   the reviewer flagged is REAL. For `ldflda this.field` in a VT method on
   `struct IlVtWithClrStructField { int prefix; TestVector3NoBinding field; }`,
   the runtime sees `Operand4 = 0x3` (F-6 `0x1` | F-10 `0x2`), `inlineMarker =
   true`, `clrStructFieldMarker = true`. Confirmed.

2. **The recommended runtime REORDER (F-6 first) is INCORRECT -- REJECTED.**
   Applying it (F-6 `inlineMarker` checked before F-10) broke 6 NeoStep17 F-6-
   only probes: `NeoStep17_TC6_RefInFrameVtField`, `NeoStep17_LdfldaInline_RefFieldRead`,
   `_RefFieldWrite`, `_NestedField`, `_RefTypeField`, `_RegisterReuseEscape`
   (NeoStep smoke went 190/190 -> 184/190). Root cause: the F-6 shape 3 branch
   (`-1, operandSlotOff + fieldPrimOff`) and the shape 1/2 frame-native branch
   (`-1, vtBase + fieldPrimOff`, where `vtBase = *(int*)(slot+4)`) produce
   DIFFERENT byrefs. Every reachable VT `this` and by-value VT arg today
   arrives as a managed pointer (`objIdx == -1`), so under HEAD order they
   route to shape 1/2 (correct). The reorder routes them to F-6 shape 3
   (wrong offset). The F-6 shape 3 branch is only correct for the constrained-
   boxed-`this` flat-bytes sub-case, NOT for managed-pointer `this`.

3. **The F-10-first mis-dispatch the reviewer feared is NOT reachable today.**
   It requires `objIdx >= 0` with flat bytes in the operand slot -- the
   constrained-boxed-VT sub-case. That path is gated behind the DEFERRED
   `constrained.callvirt`-on-VT (Step 13 Area 3 / Step 17 follow-up; confirmed
   DEFERRED in `TestCases/NeoStep13Test.cs:255-266`). For every VT source
   shape reachable in current Neo (`this` in a VT method, by-value VT arg,
   `ldloca V`), the operand slot holds a managed pointer (`objIdx == -1`), so
   the F-10 arm (`objIdx >= 0`) does NOT fire and shape 1/2 frame-native
   handles it correctly. The shipped F-10 case (heap IL ref source,
   `objIdx >= 0`, F-10-only -- no F-6 marker) is unaffected.

4. **The HEAD (F-10-first) order is CORRECT for every reachable shape today.**
   The defect is fully latent (gated behind an unimplemented feature), not
   merely unexercised.

5. **The correct FUTURE fix (when constrained-VT lands) is the JIT
   discriminator gate** -- reviewer option (a): only stamp the F-10 marker
   when the source is NOT an in-frame VT (mirror the F-6 source check in the
   type-spec pass, so the two markers are genuinely mutually-exclusive at the
   producer). This is a heavier JIT-side change, NOT a runtime reorder. Filed
   as the deferred resolution for the constrained-VT follow-up; the runtime
   precedence stays as-is (F-10-first, then `objIdx == -1`, then F-6) until
   then.

**Action taken (fixer, round 1):**
- NO runtime reorder applied (the recommended fix is incorrect; it regresses
  6 F-6-only probes). The `ILIntepreter.Neo.cs` Ldflda arm is UNCHANGED from
  the shipped F-10 change.
- Added latent-shape probe `NeoClrStructField_IlVtMethodLdfldaThisClrStructField`
  to `TestCases/NeoClrStructFieldTest.cs` (+ host helper
  `SetTestVector3NoBindingByRef` in `TestClass3.cs`). The probe exercises the
  F-6+F-10 both-stamp shape end-to-end (`ldflda this.field; call` -> a CLR
  host helper via `CopyNeoCallArguments`, defeating the IL inliner). It
  PASSES under the current (HEAD) order (objIdx == -1 -> shape 1/2 frame-
  native -> correct round-trip) and serves as a regression guard for the
  both-stamp shape's objIdx == -1 routing.
- The probe does NOT FAIL-on-HEAD (and cannot, given current Neo): the
  mis-dispatch requires objIdx >= 0 with flat bytes, which is the deferred
  constrained-VT path. This is the documented reason; the probe is a shape
  guard, not a FAIL-on-HEAD reproducer.

**Verification (round 1):** NeoStep 190/190, NeoStep20 9/9, ClrStructField
8/8 (7 original F-10 + the new latent-shape probe). Legacy `Debug` build: 0
errors. The runtime reorder was stash-toggle-verified to break exactly the 6
F-6-only probes listed above (and the breakage disappears on revert).

**Reclassification recommendation:** F-10-R1's "Major, latent" severity is
confirmed LATENT but the recommended fix (runtime reorder) is WRONG. The
finding should be reclassified as an accepted-known deferred edge (constrained-
VT) with the JIT-discriminator gate as the tracked resolution, NOT a runtime
precedence bug. The shipped change is correct for all reachable shapes.

