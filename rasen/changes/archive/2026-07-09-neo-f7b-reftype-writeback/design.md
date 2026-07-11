# Design - neo-f7b-reftype-writeback (F-7B TRUE COMPLETION)

## Context

F-7 (`archive/2026-07-09-neo-f7-delegate-byref`) shipped the same-frame
delegate-Invoke fast path for primitive `ref`/`out` params (`ref int` /
`out int` / multicast write-back is observable and green). The recorded
follow-up (F-7B, `neo-deferred-items.md` row "F-7B /
NEO-REF-TYPE-BYREF-WRITEBACK") is the reference-type half: when the callee
REASSIGNS the referent of a `ref <reference-type>` param (`s = s + "!"`), the
caller's variable ends up holding a DANGLING mStack index.

This change isolates that dangling-index mechanism empirically on HEAD
`12d9e809` and designs the mStack-lifetime promotion. It is a TRUE-COMPLETION
scope-aware pass: the goal is to isolate first, then decide SHIP vs SEQUENCE.
The verdict is SHIP (SMALL) -- the promotion is a targeted fix to the
delegate byref-writeback path, not a fundamental mStack-ownership redesign.

## The dump-gate verdict (reproduced on HEAD `12d9e809`)

A binding probe was added to `TestCases/NeoStep19Test.cs` (kept as the spec's
regression probe): `NeoStep19_ByRef_StringWriteBack` invokes a delegate whose
target `AppendBang(ref string s)` does `s = s + "!"` and returns the new
length. The caller asserts `r == 4 && s == "abc!"`.

Reproduction is 1/1 FAIL on HEAD, with the EXACT signature the deferred-items
row predicts:

```
System.ArgumentOutOfRangeException: Index was out of range. ... (Parameter 'index')
   at System.Collections.Generic.List`1.get_Item(Int32 index)
   at ILRuntime.Runtime.Generated.System_String_Binding.op_Inequality_3_Neo(...)
      in ...System_String_Binding.cs:line 348
   at ILRuntime.Runtime.Intepreter.ILIntepreter.InvokeNeoClrMethod(...)
      in ILIntepreter.Neo.cs:line 732
   at ILRuntime.Runtime.Intepreter.ILIntepreter.InvokeNeoCallTarget(...)
      in ILIntepreter.Neo.cs:line 635
   at ILRuntime.Runtime.Intepreter.ILIntepreter.ExecuteNeo(...)
      in ILIntepreter.Neo.cs:line 2265
```

The caller's `s == "abc!"` comparison compiles to `op_Inequality(s, "abc!")`,
whose generated binding derefs `mStack[<s's index>]`. After the delegate call,
`s`'s index is dangling, so the binding reads `mStack[<dead index>]` and
throws. The return value `r == 4` IS observed correctly (the single-reference
RETURN path is a separate, already-correct channel -- see "Why the return
survives" below).

### The callee JIT body (the smoking gun)

`AppendBang` lowers to:

```
0: ldind.ref r3, r0        ; read s through the byref
1: ldstr    r4, "!"
2: call     r3, r3, r4, System.String::Concat(string,string)  ; NEW object -> callee ref region
3: stind.ref r0, r3        ; write the new index back THROUGH the byref
4: ldind.ref r2, r0        ; read it back
5: callvirt.clr r1, r2, System.String::get_Length()
6: ret r1
```

Instruction 3 (`stind.ref`) is the write-back. The new `Concat` result lives
in the CALLEE's frame ref region. `stind.ref` writes that region's mStack
INDEX through the byref into the CALLER's frame cell.

## The ISOLATED root cause (the dangling mStack index mechanism)

The mechanism has three load-bearing facts, each file:line-cited.

### Fact 1: the relativization makes the callee's `stind`/`ldind` address the CALLER's frame cell

`NeoRunDelegateTargetOnThis` (`ILIntepreter.Neo.cs:668-723`) re-bases each
frame-native byref param's offset so that, inside the nested `ExecuteNeo`
(`InvokeNeoCallTarget` -> `ExecuteNeo` at `:630` / `:715`), the target's
`stind_*`/`ldind_*` -- which resolve `objectIndex == -1` against the TARGET's
`frameBase` (`ILIntepreter.Neo.cs:3926-3931` for `Stind_Ref`,
`:3950-3962` for `Ldind_Ref`) -- land back in the CALLER's frame. The rebase
at `:709`:

```
*(int*)(dTargetBase + slot.Offset + 4) = rebasedOrigOff - (int)frameDist;
```

where `frameDist = dTargetBase - callerFrameBase` (`:695`). Because
`frameBase` of the nested `ExecuteNeo` IS `dTargetBase` (`ExecuteNeo` sets
`frameBase = esp` at `:941`, and `esp` is the `dTargetBase` passed at `:715`),
resolving against the target frame at the rebased offset yields
`dTargetBase + (origOff - frameDist) = callerFrameBase + origOff` -- the
caller's cell. This is correct and is the F-7 primitive-byref mechanism.

### Fact 2: for a reference-typed referent, the written-back VALUE is a CALLEE-frame mStack index

`Stind_Ref` frame-native arm (`:3929-3931`):

```
if (objIdx == -1)
{
    *(int*)(frameBase + off) = vIdx;   // vIdx = source mStack index of the new object
}
```

`vIdx` is `*(int*)(frameBase + ip->SrcOffset)` (`:3928`) -- the mStack index
of the value being stored. For `s = s + "!"`, that value is the `Concat`
result, which the callee materialized into ITS OWN frame ref region (reserved
by the nested `ExecuteNeo` at `:960-962`: `frameRefBase = mStack.Count; ...
mStack.Add(null)`). So `vIdx >= calleeFrameRefBase`. The caller's cell at
`callerFrameBase + origOff` now holds `vIdx`, an index into the callee's ref
region.

### Fact 3: the callee's Ret pop deletes that region, leaving the caller index dangling

The nested `ExecuteNeo`'s normal-return path is the `Ret` arm
(`:2670-2721`). At `:2719`:

```
mStack.RemoveRange(frameRefBase, mStack.Count - frameRefBase);
```

This truncates the mStack back to the callee's `frameRefBase`, deleting every
slot at index `>= calleeFrameRefBase` -- including the `vIdx` the caller now
holds. (The exception-unwind path has the equivalent truncation at `:4700`,
but the binding reproduction is the normal-return case.) After the pop, the
caller's `s` slot contains an index that is either `>= mStack.Count` (OOB) or
points at a slot that has since been reused by a deeper call -- garbage.

### Why the normal reference RETURN survives (the existing promotion pattern)

A single-reference RETURN does NOT dangle because the `Ret` arm promotes the
object into the CALLER's return ref region BEFORE the pop (`:2689-2694`):

```
int retSrcIdx = *(int*)(frameBase + ip->DstOffset);
if (retSrcIdx >= 0) {
    mStack[retRefBase] = mStack[retSrcIdx];   // copy object to caller's ref slot
    *(int*)retDst = retRefBase;                // caller cell gets the CALLER ref index
}
```

`retRefBase` is the caller's return ref region (passed into `ExecuteNeo` at
`:911`, computed at the call site as `frameRefBase + ip->Operand3` -- e.g.
`ILIntepreter.Neo.cs:2239`, `:2517`). So a return value is re-homed into a
caller-owned mStack slot and the caller cell holds a caller-region index --
which survives the callee pop. F-7B is the byref-writeback channel MISSING
this promotion.

### Why primitive byref write-back is unaffected (F-7 is green)

For `ref int` / `out int`, the referent is a flat 4-byte value. `Stind_I4`
frame-native (`:3766-3775` area) writes the VALUE bytes directly into
`callerFrameBase + off`. There is no mStack index involved; the bytes ARE the
value. The pop does not touch the caller's frame cell. Hence F-7's primitive
write-back (`v == 15`, `v == 99`, multicast `v == 30`) is observable and
green. F-7B is specific to a reference-typed referent.

## Scope decision: SHIP (SMALL), not SEQUENCE

The mechanism is a SINGLE missing promotion in the delegate byref-writeback
channel. It is NOT a fundamental mStack-ownership redesign:

- The promotion pattern ALREADY EXISTS (the `Ret` arm single-reference return,
  `:2689-2694`). F-7B reuses the same shape for the byref channel.
- The fix site is localized: the delegate-Invoke branch (`:2541-2580`) and/or
  `NeoRunDelegateTargetOnThis` (`:668-723`), where the caller's `frameBase`,
  `frameRefBase`, `map`, and `mStack` are all in scope (`:2511-2517`).
- No change to the per-frame reservation/pop model (`:960-962`, `:2719`),
  which is the load-bearing Neo invariant for 200+ green tests. The promotion
  works WITHIN that model (re-home the object into a caller-owned slot before
  the callee pop).

The generalization to the DIRECT `Call_IL`/`CopyNeoCallThisBack` sibling path
(`:2265` + `:543-595`) is the SAME root cause (a frame-native-byref write-back
of a reference copies a callee-frame mStack index into the caller cell, which
`CopyNeoCallThisBack`'s `Unsafe.CopyBlock` at `:578` propagates verbatim).
That sibling is recorded as a SEQUENCING note (see "Open Questions") -- it is
not reachable by an inlinable target today (the Neo trivial inliner folds
small static targets, so a direct `ref string`-reassign call currently never
crosses a frame), which is why F-7 (the first real cross-frame byref) surfaced
only on the delegate path. F-7B ships the delegate path (the binding
reproduction) and sequences the direct path behind a probe.

## Goals / Non-Goals

**Goals:**
- A `ref <reference-type>` delegate param where the callee REASSIGNS the
  referent (`s = s + "!"`) -> the caller observes the NEW object (not a
  dangling index). The binding probe `NeoStep19_ByRef_StringWriteBack` (caller
  asserts `r == 4 && s == "abc!"`) MUST pass.
- The F-7 primitive-byref delegate cases (`ref int` / `out int` / multicast)
  stay byte-identical green (`NeoStep19_ByRef_Int` / `_Out` / `_Multicast`).
- The `ref string` MARSHAL + READ path (`NeoStep19_ByRef_String`,
  `ReadLength(ref s)` returns `s.Length`) stays green.
- Multicast with a `ref <reference-type>` param: each target sees the prior
  target's reassignment (last object wins), matching C# multicast-byref
  semantics and Legacy.

**Non-Goals:**
- The direct `Call_IL`/`CopyNeoCallThisBack` sibling path (a non-delegate
  cross-frame `ref <reference-type>` reassign). Recorded as a sequencing note;
  not reachable today (the inliner folds small direct targets). Sequenced
  behind a non-inlinable probe, NOT shipped in this change.
- The Step-17 `ldind_ref`/`stind_ref` heap-IL-ref-field deferral
  (`:3943-3944`, `:3998-3999`). Distinct gap, tracked under neo-byref / Step
  17. F-7B deals only with the mStack-object / frame-native referent.
- AOT (`ilrt_neoc`) wire-up. The JIT path is in scope; AOT follows the
  standard pattern if the smoke needs it (out of scope unless the AOT smoke
  regresses).

## Decisions

### D1: promote the written-back object into a CALLER-owned mStack slot

**Decision:** When a frame-native byref param of a delegate-Invoke target has
a REFERENCE-typed referent, the callee's write-back through that byref MUST
land the new object in a CALLER-owned mStack slot (a slot at index `< caller
frameRefBase`), and the caller's frame cell MUST end up holding that
caller-owned index. The promotion reuses the existing `Ret`-arm pattern
(`:2689-2694`): copy `mStack[vIdx]` into the caller-owned slot, then write the
caller-owned index into the caller cell.

**Rationale:** The dangling index is `>= calleeFrameRefBase` because the new
object is in the callee's region. Re-homing the object into a slot the callee
pop does NOT touch (`< caller frameRefBase`) makes the index stable, exactly
as the return-value channel already does.

**Two implementation sites, EITHER suffices (the apply stage picks one + a
probe; both are SMALL):**

#### D1a (recommended): convert the frame-native byref to an mStack-slot byref in `NeoRunDelegateTargetOnThis`

`NeoRunDelegateTargetOnThis` runs in the CALLER's `ExecuteNeo` context (it is
invoked at `ILIntepreter.Neo.cs:2544`, before the nested `ExecuteNeo`), so the
caller's `frameRefBase` and `mStack` are in scope. For each frame-native byref
param whose referent is a reference type, instead of (or in addition to) the
offset rebase at `:709`, rewrite the byref in `targetBase` from the
frame-native form `(objectIndex == -1, callerOffset)` to an mStack-slot form
`(objectIndex == <caller ref slot>, <write-the-object-here sentinel>)`:

- The caller ref slot is the source local's OWN ref slot (the local `s`
  already has a ref slot in the caller's frame ref region; the caller's index
  slot and ref slot both describe `s`). The byref must be wired so the
  callee's `Stind_Ref` mStack-object arm (`:3933-3945`) -- or a small new
  sub-arm keyed on the sentinel -- writes the object into
  `mStack[callerRefSlot]` AND updates the caller's index slot to
  `callerRefSlot`.
- The callee's `Ldind_Ref` mStack-object arm (`:3967-3981`) reads
  `mStack[callerRefSlot]` for the READ path.

The sentinel/encoding MUST be disjoint from the existing mStack-object offset
semantics (field hash for CLR objects, Primitives byte offset for ILTypeInstance).
A high-bit discriminator on the `offset` half (mirrors the F-10
`NeoF10ByrefOffsetFlag = 0x40000000` pattern at
`ILIntepreter.Neo.cs` Stfld_Ref/Ldfld_Ref) is the established encoding idiom
for "this mStack-object byref is a special shape."

This makes BOTH the read and the write of the reference byref go through
mStack-absolute slots that survive the callee pop, and removes the
relativization for reference byrefs entirely (the rebase at `:709` stays for
primitive/value-type byrefs).

#### D1b (alternative): promote at the `Stind_Ref` frame-native arm via a cross-frame indicator

Teach `Stind_Ref` (`:3929-3931`) to detect that a frame-native write resolves
OUTSIDE the current frame (the target offset, after resolution, is `< frameBase`
or `>= frameBase + frameSize`) and, for a reference value, copy the object into
a caller-owned slot before writing the index. This requires the callee to know
a caller-owned slot to promote into (passed in via a new `ExecuteNeo` param, or
recovered from a frame-boundary registry). MORE invasive (new param threading
through `ExecuteNeo`/`InvokeNeoCallTarget`) and REJECTED in favor of D1a unless
D1a's encoding proves intractable at apply time.

### D2: gate the promotion on the referent being a reference type

**Decision:** The promotion applies ONLY to byref params whose referent is a
reference type (`!IsPrimitive && !IsValueType`, i.e. the `ref string` /
`ref <class>` shape). Primitive byrefs (`ref int` etc.) keep the F-7
relativization byte-write path UNCHANGED. Value-type byrefs (`ref struct`) are
out of scope for F-7B (a `ref struct` reassign copies flat bytes that may
span the index + ref sub-slots; recorded as a sequencing note -- not reachable
by current tests).

**Rationale:** Only a reference referent produces an mStack index that
dangles. Gating narrowly preserves the green F-7 primitive path byte-for-byte
(the `NeoStep19_ByRef_*` regression guard) and avoids touching the value-type
byref path (Step 17 territory).

### D3: multicast -- the caller-owned slot accumulates across the chain

**Decision:** For a multicast delegate with a `ref <reference-type>` param,
the caller-owned slot from D1 is shared across the chain: each target's
write-back lands in the SAME caller slot (last write wins), and each
subsequent target reads the prior target's object from that slot -- matching
C# multicast-byref semantics and the F-7 multicast primitive behavior (D3 of
the F-7 design). The relativization-undo at `:719-720` is replaced by the
mStack-slot rewrite's natural idempotency (the rewrite targets a stable
caller slot, not a frame-distance-dependent offset).

**Rationale:** Uniformity with the F-7 multicast design; the caller-owned slot
is the single source of truth.

### D4: keep the primitive-byref delegate path and the non-IL-target fallback UNCHANGED

**Decision:** The F-7 primitive-byref fast path (relativization +
direct-write) and the `ReadNeoDelegateInvokeArgs` + `NeoInvokePublic`
non-IL-target fallback (F-7 D5) are NOT modified. The promotion is taken ONLY
for an IL delegate target with a reference-typed byref param.

**Rationale:** Conservative. The primitive path is the green F-7 hot path; the
fallback is rarely hit. A later cleanup can unify if the mStack-slot byref
form proves uniform.

## Risks / Trade-offs

- **[Risk] D1a's sentinel encoding collides with an existing mStack-object
  offset (field hash / Primitives offset).** -> Mitigation: a high-bit
  discriminator (bit 30, the F-10 idiom) is disjoint from real field hashes
  (process-global counter values, well below 2^30) and from Primitives byte
  offsets (frame sizes are tiny). The apply stage MUST add an adversarial
  probe that passes BOTH a `ref string` (the promoted shape) and a
  `ref <CLR-object>.refField` (the existing mStack-object shape) through the
  same delegate-Invoke path to prove the dispatch is disjoint.
- **[Risk] The caller-owned slot is the source local's ref slot -- but a
  byref may alias a TEMP (not a named local), e.g. the C# compiler's
  addrAlias folding.** -> Mitigation: the byref source for a delegate-Invoke
  callvirt is the caller's `ldloca`/`ldarga` product (the JIT `NeoCallParamMap`
  records `PrimitiveByRefSrc` + the source slot). The promotion must recover
  the caller ref slot from the SAME source metadata the F-7 rebase uses
  (`map.PrimitiveSrc[i]` + the caller frame layout). If the source is a temp
  with no ref slot, fall back to allocating a fresh caller-frame ref slot
  (the caller's `frameRefBase + <a reserved slot>`); the apply probe MUST
  cover a temp-source byref.
- **[Risk] The promotion changes the green `NeoStep19_ByRef_String` (READ
  path) behavior.** -> Mitigation: D1a routes BOTH read and write through the
  caller-owned slot, so `ReadLength(ref s)` (read) still resolves to the same
  object. The READ-path probe stays green by construction; it is an explicit
  regression guard.
- **[Trade-off] Two reference-byref write-back encodings now coexist for a
  transition (the D1a mStack-slot form vs. the old frame-native form).** ->
  Acceptable: D1a REPLACES the frame-native form for reference byrefs
  entirely (no transition state); primitive/value-type byrefs keep
  frame-native. No dual path.
- **[Risk] D1b (the alternative) would thread a new param through
  `ExecuteNeo`.** -> Avoided by choosing D1a; documented as the fallback only.

## Open Questions

- **Direct `Call_IL`/`CopyNeoCallThisBack` sibling:** a non-delegate
  cross-frame `ref <reference-type>` reassign has the SAME dangling-index
  mechanism (`CopyNeoCallThisBack` `:578` `Unsafe.CopyBlock` propagates the
  callee index verbatim). It is NOT reachable today because the Neo trivial
  inliner (`JITCompiler.cs:2931-2975`) folds small static targets, so a
  direct `ref string`-reassign call never crosses a frame (verified by
  reasoning from the inliner conditions -- a method with an exception handler
  or above the inline size threshold is the only way to force it). F-7B ships
  the delegate path (the binding reproduction). The direct path is SEQUENCED:
  a follow-up adds a non-inlinable `ref string` direct-call probe and applies
  the analogous promotion in `CopyNeoCallThisBack` (copy the OBJECT, not the
  index, into the caller's ref slot). NOT blocking F-7B.
- **Value-type byref reassign (`ref struct` where the struct has reference
  fields):** the flat-bytes write-back may span the index + ref sub-slots.
  Out of scope (Step 17 territory); recorded as a sequencing note. Not
  reachable by current tests.

## Implementation Notes

- The probe `NeoStep19_ByRef_StringWriteBack` + target `AppendBang` are
  ALREADY added to `TestCases/NeoStep19Test.cs` (this planner's reproduction).
  The apply stage keeps them as the binding regression probe and adds the
  multicast-with-ref-string + temp-source-byref adversarial probes.
- The deferred-items F-7B row (`neo-deferred-items.md:92`) cites the pop at
  `ILIntepreter.Neo.cs:4703`; the ACTUAL binding pop for the normal-return
  case is the `Ret` arm at `:2719` (`:4700` is the exception-unwind path).
  Both truncate mStack to the nested frame's `frameRefBase`; the design cites
  `:2719` as the binding site and notes `:4700` as the unwind equivalent.
- Legacy is the REFERENCE: Legacy's `StackObject[]` evaluation stack +
  separately-managed `ManagedStack` does NOT frame-truncate the mStack on
  return the way Neo does, so a reference byref write-back carries the object
  stably. F-7B brings Neo to parity WITHIN Neo's per-frame-reservation model
  (no model change -- just the promotion).
