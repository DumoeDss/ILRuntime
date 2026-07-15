# neo-il-struct-box-call-boundary -- design

Wave-2 child of neo-overhaul. Pinned 2-for-1 root: StructTest11 +
TestStructDictionary. RE-AUDIT CONFIRMED the root AND refined the
discriminator (the handoff's "srcInfo indicates a flat-bytes IL-struct"
framing was IMPRECISE -- see D2).

## Root (pinned, Neo-vs-Legacy)
An IL value-type struct is NOT boxed to ILTypeInstance when passed as a
reference-typed param at a Callvirt_CLR / Call / Call_Redirect boundary.

The canonical trigger is a CLR generic collection over an IL struct:
`List<Anim>.Add(new Anim(...))` resolves (under ILRuntime) to
`List<ILTypeInstance>::Add(ILTypeInstance)`. The C# compiler emits NO CIL
`box` (at source level the param is the value type T, passed by value),
but ILRuntime resolves T to ILTypeInstance (a reference) -- so the call
boundary MUST box the flat-bytes struct into a fresh ILTypeInstance.

Legacy boxes implicitly via `StackObject.ToObject` at the marshalling hop.
Neo's `CopyNeoCallArguments` raw-`CopyBlock`s the struct bytes into the
dest 4-byte ref slot. The autogen binding (`Add_0_Neo`) then reads the
slot via `ReadNeoReference` (expects an mStack index) -> reads the struct's
first primitive-field bytes as an index -> garbage -> OOB (StructTest11:
`ArgumentOutOfRangeException @ List.get_Item`) or null-downstream NRE
(TestStructDictionary: the Add stored garbage -> get_Item returned null ->
`item.id` ldfld.i4 heap-arm `GetNeoILInstance` NRE). TestStructDictionary's
NRE is a DOWNSTREAM symptom of the SAME Add-boxing bug (the "2-for-1").

## D2 -- the discriminator (re-audit refinement, the crux)
The handoff prescribed: "set the flag when srcInfo indicates a flat-bytes
IL-struct AND paramType is ILTypeInstance/reference". RE-AUDIT found BOTH
halves need refinement:

1. **srcInfo shape is AMBIGUOUS.** Anim = {string name; float duration;}
   has TotalPrimitiveSize=4 / TotalReferenceCount=1 under the Neo object
   model -- IDENTICAL to a genuine reference slot (Size=4, RefCount=1).
   So `StackSlotInfo` (Offset/RefOffset/Size/RefCount) CANNOT distinguish
   an IL-struct source from a reference source. A pure shape discriminator
   would either over-fire (box a genuine reference -> CopyFrameToIL
   reinterprets an mStack index as struct bytes -> corruption) or
   under-fire (Anim). UNSOUND. The source TYPE is required.

2. **frame.NeoRegisterTypes is last-write-wins (DISPROVEN for reused
   temps).** The TypeSpecialize pass IS position-correct DURING its walk,
   but the array it returns holds the FINAL write per register (single
   forward pass, no phi-merge -- the child-11/16/21 lineage's known
   imprecision). StructTest11 reuses r5: `newobj r5 = Anim` at body 7
   seeds registerTypes[r5]=Anim, but `ldc.i4.s r5,12` at body 13
   overwrites it to Int32. So reading frame.NeoRegisterTypes[r5] at the
   call (body 8) yields Int32 (stale), and the boxing discriminator
   NEVER fires. (Confirmed by diagnostic: registerTypes[r5]=System.Int32
   at the Add call, even though the Newobj case DID seed Anim mid-pass.)

**The fix = a position-correct tracker maintained INSIDE the LowerNeoOffsets
main loop** (which already walks the body in order). `curVtTypes` is:
- seeded from frame.NeoRegisterTypes (so declared locals/params carry
  their stable declared type -- handles TestStructDictionary's `def` local
  passed directly as the call arg);
- updated per-instruction as the loop walks forward: Newobj/Unbox of an
  IL-VT seed the dest; Move/Move_Vt/Ldloc*/Ldarg* propagate Register2's
  type; any other dest-defining op clears (a non-IL-VT producer, or a
  producer kind this conservative tracker does not model);
- read at each call site, so it reflects the most-recent writer BEFORE the
  call (position-correct for the straight-line top-of-stack arg shape).

At StructTest11's call (body 8), curVtTypes[r5] = Anim (last writer before
body 8 = the body-7 newobj), NOT the final Int32. CORRECT.

This is deliberately CONSERVATIVE: a struct produced by Ldfld/Ldelem/Call
(a producer kind the tracker clears instead of seeding) is MISSED (no
box), never a false positive. The target tests need only Newobj (StructTest11)
and the local seed (TestStructDictionary), both covered. A struct-field or
struct-call-result source would need the tracker extended (future child).

paramType (reference: !IsValueType && !IsPrimitive && !IsByRef) is kept as
a SECONDARY gate for safety, but for a CLR callee an IL-VT source implies
an ILTypeInstance param (CLR methods cannot take an IL struct by value), so
the source-type check is the load-bearing discriminator.

## The 3-site fix (all Neo-gated -> Legacy-neutral)
1. **JITCompiler.cs -- NeoCallParamMap flag + frame type thread.**
   - `NeoCallParamMap.PrimitiveBoxIlType` (ILType[], per-prim-slot, null=no
     box) + `PrimitiveBoxSrcRefOff` (ushort[], the struct's caller-frame ref
     offset). Sizes come from the ILType; the prim source offset is the
     existing PrimitiveSrc[i].
   - `CompiledFrame.NeoRegisterTypes` (IType[]): TypeSpecializeNeoOpcodes
     now RETURNS registerTypes; RunNeoBackHalf stores it. Read by
     LowerNeoOffsets to seed curVtTypes.

2. **Optimizer.Neo.cs -- map-build discriminator + curVtTypes tracker.**
   - Captures `paramTypes[]` (per-dst-slot resolved param type) in the
     CLRMethod paramInfos build.
   - curVtTypes seeded + maintained per-iteration (Newobj/Unbox seed,
     Move/Ldloc*/Ldarg* propagate, else clear).
   - Discriminator: !dstByRef && paramType is reference && curVtTypes[srcReg]
     is IL-VT (not enum/primitive) -> set PrimitiveBoxIlType[i].
   - Arrays only carried when at least one slot boxes (common no-box call
     pays no per-call cost -- CopyNeoCallArguments null-checks the field).

3. **ILIntepreter.Neo.cs -- CopyNeoCallArguments boxing.**
   - Gains a `frameRefBase` param (all 11 call sites updated).
   - In the prim loop, when PrimitiveBoxIlType[i] != null: box the struct
     (`ilType.Instantiate(false)` + `CopyFrameToIL` -- the existing Box arm
     + TryNeoIlVtElementArrayCall Set box primitive), mark Boxed, park on
     mStack, write the mStack index into the dest 4-byte ref slot. The ref
     region (RefDst) is never read by autogen bindings (they read only the
     sequential 4-byte prim indices via ReadNeoReference), so writing the
     index alone suffices.

## Soundness / regression surface
CopyNeoCallArguments is on EVERY CLR-call hot path. The flag fires ONLY
for IL-struct-to-reference-param (curVtTypes says IL-VT + paramType is
reference + !dstByRef). A genuine reference source (IL class / CLR
string/object) has a non-IL-VT curVtTypes -> no box -> raw copy stands.
A byref param / CLR-VT `this` (dstByRef) is excluded. NeoStep 404/0
(no regression) is the load-bearing gate and is GREEN.

## Verify (truth = full-smoke number)
- StructTest11 + TestStructDictionary PASS (stash-toggle airtight: HEAD
  fails both -> fix passes both).
- FULL SMOKE: 15 -> 13 (the 2-for-1; both target tests flipped; the 13 are
  a strict subset, no new failures). NeoStep 404/0.
- Legacy-neutral: plain Debug build 0 errors (all changes Neo-gated; the
  CopyNeoCallArguments signature change is in the file-gated Neo file).
