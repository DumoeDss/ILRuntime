## Context

Grounded in the branch state at propose time (HEAD after neo-il-exception-throw,
NeoStep smoke 108/108). All file:line refs are to that state. Scope is the
**{4b value-type-`this`, 4a `Unsafe.Unbox<T>` direct-call} cohort only**; 4c
(CLR-method ref/out) and 4d (CLR-object stind/ldind) are DEFERRED to a follow-up
child `neo-step13-area4-refandstind` (see proposal.md).

### The Neo CLR call ABI today (established by Step 9 + 13b)

A CLR method call from IL flows through two code paths that MUST agree byte-for-
byte on the callee param region:

1. **The reflection fallback** `CLRMethod.Invoke(byte*)` (`CLRMethod.cs:332`),
   invoked from `InvokeNeoClrMethod` (`ILIntepreter.Neo.cs:263`) when no
   `RedirectionNeo` autogen delegate is registered.
2. **The autogen redirect delegate** `*_Neo` (`MethodBindingGenerator.cs:249`),
   whose body is emitted by `GenerateMethodWraperCode_Neo` (the prologue reads
   `this` + each param; the epilogue writes the return).

Both walk a contiguous callee "param region" built by the optimizer
(`AllocateNeoCallParamSlot`, `Optimizer.Neo.cs:1319`) with a single `__curPrim`
cursor advanced by each slot's exact size. The `this` (slot 0 for `HasThis`)
precedes the params. Step 13b unified the **parameter** layout (CLR struct by-
value param + return via `ReadNeoValueType`/`WriteNeoValueType`); the **`this`
slot** was left as the 13b-era 4-byte mStack-index read for reference types and
a `// TODO: ValueType instance in Neo` for value types.

### The two defects this pass closes

**(4b) value-type-`this` direct-call -- the F-3 / NEO-BYREF-THIS defect class.**
The `HasThis` arm of `CLRMethod.Invoke` (`CLRMethod.cs:351-356`) reads `this`
unconditionally as a 4-byte mStack index:

```csharp
else if (HasThis)
{
    int thisIdx = *(int*)(targetBase + curPrim);
    instance = mStack[thisIdx];   // <-- 4-byte mStack index read
    curPrim += 4;
}
```

For a CLR struct instance method, the C# compiler does NOT emit `newobj` for the
`this` -- it lowers `local.method(...)` to `ldloca local; call method(...)`, so
`this` arrives as a **frame-native byref** = an 8-byte Ref Slot `(-1,
frameByteOff)` produced by `ldloca`. Reading the first 4 bytes as an mStack
index yields garbage / `ArgumentOutOfRangeException` (the F-3 reproducer). This
is a pre-existing gap (fails identically on `f673b9c9`, pre-F-MAJ-1). It is the
SAME defect class as `new ClrStruct(args)` (which lowers to `initobj; ldloca;
call ctor`) and callvirt-on-a-CLR-struct. The `GenerateMethodWraperCode_Neo`
prologue mirrors this with a `// TODO: ValueType instance in Neo`
(`MethodBindingGenerator.cs:258-262`): it declares
`instance_of_this_method = default(T)` but never reads it.

**(4a) `Unsafe.Unbox<T>` direct-call -- the boxed-CLR-VT instance method.** When
a CLR struct instance method is invoked on a BOXED struct (a struct that flowed
through `object`), the call ABI today boxes, calls on a copy, and -- for a
mutating method -- must write the mutated copy back. The Neo wrapper does NOT
emit `WriteBackInstance` (the 13b finding), so there is currently no write-back;
a mutating method silently loses its mutation. The 13b design named the
"`Unsafe.Unbox<T>` direct-call mode" as the Area 4 mechanism but did not build
it.

### Pre-requisite machinery (all shipped)

- **VT-THIS-ADDR** (just shipped): an in-frame IL value type `this` / dest is
  treated as an in-frame address for all field access. The frame-native Ref Slot
  `(-1, frameByteOff)` is the byref encoding.
- **Step 17 byref call-ABI + Ref Slot**: an 8-byte Ref Slot `(objectIndex,
  offset)`; `-1` = frame-native (absolute byte offset); `>=0` = mStack object
  (field offset). `ldloca` / `ldflda` produce Ref Slots; byref params are 8-
  byte Ref Slots.
- **Step 13b helpers**: `ReadNeoValueType(Type, byte*, ref curPrim, sz)` /
  `WriteNeoValueType(object, byte* dst, sz)` (cached `DynamicMethod` +
  `Unsafe.ReadUnaligned/WriteUnaligned`); `GetNeoValueTypeManagedSize(Type)`
  (single source of truth for a CLR struct's flat-byte size).

Legacy (`ILIntepreter.Register.cs` `ExecuteR` + the Legacy
`GenerateMethodWraperCode_Legacy` / `AppendArgumentCode` / `WriteBackInstance`
/ Legacy `CLRMethod.Invoke(StackObject*)`) is the SEMANTICS reference. The
Legacy `noUnbox` direct-call flag (`MethodBindingGenerator.cs:418-426`, used for
async `Start<TSM>`) and the Legacy value-type `this` WriteBackInstance
(`:738-762`) are the reference patterns. Legacy is NOT modified.

## Goals / Non-Goals

**Goals (IN this pass):**

- A CLR struct **instance** method called on an in-frame value-type `this`
  (lowered to `ldloca; call`) reads the struct correctly via the byref Ref Slot
  -- closes the F-3 / NEO-BYREF-THIS defect class for both the reflection
  fallback and the autogen wrapper.
- A CLR struct instance method called on a **boxed** struct (a struct that
  flowed through `object`) executes correctly, AND a mutating method's mutation
  propagates back to the boxed location (the 4a direct-call write-back).
- `GenerateMethodWraperCode_Neo` emits the value-type `this` read (replacing
  the `// TODO: ValueType instance in Neo`).
- `WriteBackInstance` is confirmed not emitted by the Neo wrapper (13b finding)
  and the value-type `this` write-back is documented as the flat-bytes write
  (4a).
- Full `NeoStep` smoke stays green (108/108 baseline); Legacy (plain `Debug`)
  stays at its baseline for any shared-engine edit (all changes Neo-only).

**Non-Goals (DEFERRED):**

- 4c CLR-method `ref`/`out` (typed-reference bridge) -- follow-up child.
- 4d CLR-object `stind`/`ldind` via field hash -- follow-up child.
- IL value-type `newobj` (shipped VT-THIS-ADDR).
- Delegate `newobj` (Step 19), async state machines (Step 20).
- A CLR struct instance method on a struct WITH reference fields and NO
  `ValueTypeBinder` (the 13b NIE guard stays -- pure-primitive structs and
  binder-registered structs work; structs with refs without a binder NIE).

## Decisions

### D1 -- Discriminate the `this` representation by the declaring type (4b core)

The `HasThis` arm of BOTH readers (`CLRMethod.Invoke` and the generated
`*Neo` prologue) decides how to read `this` based on the **declaring type**:

- **Reference type (or boxed value type used as a reference):** `this` is a
  4-byte mStack index (the existing `ReadNeoReference` / `mStack[thisIdx]`
  path). UNCHANGED.
- **Value type, called via byref (the in-frame direct-call, 4b):** `this` is an
  8-byte frame-native Ref Slot `(-1, frameByteOff)`. The reader advances
  `curPrim` by 8 (not 4), reads the struct's flat bytes via `ReadNeoValueType`
  at `frameBase + frameByteOff`, and boxes them into the runtime `instance`
  object. This mirrors the 13b by-value param read exactly (same helper, same
  size source).
- **Value type, called on a boxed struct (4a):** `this` is a 4-byte mStack index
  pointing at a BOXED struct. The reader reads the boxed object (`mStack[idx]`),
  the wrapper unboxes to a local `T`, calls the method on the local, and writes
  the local back to the boxed location for a mutating method (D3).

**The discriminator is `DeclearingType.IsValueType`** (the same test
`GenerateMethodWraperCode_Neo:258` already uses to pick the `default(T)`
prologue). The runtime needs one extra bit: is the byref `this` frame-native
(`objectIndex == -1`, the 4b case) or heap-boxed (`objectIndex >= 0`, the 4a
case)? That bit is the Ref Slot's first int -- already encoded by `ldloca` /
box.

**Why discriminate by declaring type (not by a per-call flag):** the 13b
`ReadNeoValueType`/`WriteNeoValueType` discriminator insight (M2/M3 finding:
the per-arm TYPE TOKEN determines the representation unconditionally, no per-
slot flag needed) applies here too. The `this` representation is determined by
the declaring type + whether the source is a byref or a box, both of which are
knowable at the call site from the Ref Slot's `objectIndex` half.

### D2 -- `CLRMethod.Invoke(byte*)` `HasThis` arm (reflection fallback, 4b)

Replace the unconditional 4-byte read at `CLRMethod.cs:351-356`:

```csharp
// CLRMethod.Invoke(byte*), HasThis arm.
if (DeclearingType is CLRType thisClr && thisClr.IsValueType
    && !thisClr.TypeForCLR.IsPrimitive && !thisClr.TypeForCLR.IsEnum)
{
    // Value-type `this`. The first int is the Ref Slot's objectIndex half.
    int objIdx = *(int*)(targetBase + curPrim);
    curPrim += 4;  // the objectIndex half
    if (objIdx == -1)
    {
        // 4b: frame-native byref. The second int is the absolute byte offset.
        int frameByteOff = *(int*)(targetBase + curPrim);
        curPrim += 4;  // the offset half (total this-slot width = 8)
        int sz = Optimizer.GetNeoValueTypeManagedSize(thisClr.TypeForCLR);
        // (binder / ref-field NIE guards mirror the 13b param read.)
        instance = ILIntepreter.ReadNeoValueType(thisClr.TypeForCLR,
            targetBase /*base is frameBase*/, ref frameByteOff /*read at off*/, sz);
        // NOTE: ReadNeoValueType advances its cursor; pass a temp = frameByteOff
        // so the actual frameByteOff is the READ site (it already is).
    }
    else
    {
        // 4a: boxed struct `this`. Read the boxed object, unbox to T below.
        // (The reflection path uses instance = mStack[objIdx]; the method
        // Invoke on a boxed struct boxes/unboxes internally; for a mutating
        // method the write-back is handled by the boxed-object identity -- the
        // box IS the object, so a reference-type call on it mutates in place.
        // For a value-type mutation on a box, the box is immutable post-unbox;
        // the autogen path (D3) owns the write-back. The reflection fallback
        // calls MethodInfo.Invoke on the boxed object, which is the CLR
        // semantics for a boxed-VT instance method -- no extra write-back.)
        instance = mStack[objIdx];
    }
}
else
{
    // Reference-type `this` -- the existing 4-byte mStack-index read.
    int thisIdx = *(int*)(targetBase + curPrim);
    instance = mStack[thisIdx];
    curPrim += 4;
}
```

**The cursor-advance subtlety (load-bearing):** the call-lowering
(`Optimizer.Neo.cs:1166-1186`) sizes the `this` slot via
`AllocateNeoCallParamSlot(DeclearingType)`. For a CLR struct `this`, that helper
sizes flat bytes (`Size = GetNeoValueTypeManagedSize`, the 13b behavior). So the
`this` slot's WIDTH in the param region is the struct's flat-byte size, NOT 4
and NOT 8. **This is the dump-confirm point at apply:** the byref Ref Slot
produced by `ldloca` is 8 bytes in the FRAME, but the param-region slot laid out
by `AllocateNeoCallParamSlot` may be the flat-bytes size OR the 8-byte Ref Slot
size, depending on whether the call-lowering treats the value-type `this` as a
byref (8 bytes) or as the in-frame value (flat bytes).

The apply-phase JIT dump of `local.SomeMethod()` decides which:

- **If the `this` slot is laid out as the 8-byte Ref Slot** (the byref shape):
  D2 reads the 8-byte Ref Slot as above; the cursor advances 8.
- **If the `this` slot is laid out as flat bytes** (the in-frame-value shape,
  matching how VT-THIS-ADDR seeds an IL VT ctor's slot-0): D2 reads the flat
  bytes directly via `ReadNeoValueType` at `curPrim`, advancing by the struct's
  flat-byte size.

The design permits BOTH; the apply phase picks the one matching the dump (the
VT-THIS-ADDR precedent: it seeded slot-0 as flat bytes for an IL VT ctor, so the
consistent rule for a CLR struct instance `this` is likely flat bytes too). The
`// 4b byref` vs `// 4b flat-bytes` reads differ only in WHERE the bytes are
(`frameBase + frameByteOff` for the byref deref, `targetBase + curPrim` for the
flat-bytes direct read) -- both use `ReadNeoValueType`. **Probe at apply.**

### D3 -- `GenerateMethodWraperCode_Neo` value-type `this` prologue (autogen, 4b + 4a)

Replace the `// TODO: ValueType instance in Neo` at
`MethodBindingGenerator.cs:258-262`. The generated prologue mirrors D1's
discriminator:

```csharp
// In GenerateMethodWraperCode_Neo, the !i.IsStatic branch.
if (type.IsValueType)
{
    // Read the Ref Slot's objectIndex half to discriminate byref vs box.
    // (The cursor has NOT yet advanced past the `this` slot.)
    sb.AppendLine($"            int __thisObjIdx = *(int*)(__frameBase + __curPrim);");
    sb.AppendLine($"            {typeClsName} instance_of_this_method = default({typeClsName});");
    sb.AppendLine($"            if (__thisObjIdx == -1) {{");
    // 4b: frame-native byref. Read flat bytes at the offset half.
    sb.AppendLine($"                int __thisSz = Optimizer.GetNeoValueTypeManagedSize(typeof({typeClsName}));");
    sb.AppendLine($"                int __thisOff = *(int*)(__frameBase + __curPrim + 4);");
    sb.AppendLine($"                instance_of_this_method = ({typeClsName})ILIntepreter.ReadNeoValueType(typeof({typeClsName}), __frameBase, ref __thisOff, __thisSz);");
    sb.AppendLine($"                __curPrim += 8;  // 8-byte Ref Slot");
    sb.AppendLine($"            }} else {{");
    // 4a: boxed struct `this`. Unbox the boxed object to a local T.
    sb.AppendLine($"                object __thisBox = __mStack[__thisObjIdx];");
    sb.AppendLine($"                instance_of_this_method = __thisBox != null ? ({typeClsName})__thisBox : default({typeClsName});");
    sb.AppendLine($"                __curPrim += 4;  // 4-byte mStack index");
    // 4a write-back stub: remember the box site so the epilogue can write back.
    sb.AppendLine($"                int __thisBoxIdx = __thisObjIdx;  // for D3 write-back");
    sb.AppendLine($"            }}");
}
else
{
    sb.AppendLine($"            {typeClsName} instance_of_this_method = ({typeClsName})ILIntepreter.ReadNeoReference(__frameBase, ref __curPrim, __mStack);");
}
```

(Dump-confirm at apply whether `__curPrim += 8` or `__curPrim += __thisSz` --
the byref-vs-flat-bytes question from D2. The generated code uses whichever the
JIT dump shows the call-lowering sized the `this` slot as.)

### D4 -- The 4a boxed-write-back (mutating method on a boxed struct)

For a CLR struct instance method invoked on a BOXED struct, the call semantics
require that a mutating method's changes propagate back to the box. The
generated epilogue (after the method call, before `GetReturnValueCodeNeo`) adds,
only for a value-type `this`:

```csharp
// Epilogue, value-type `this` mutating-method write-back.
// (Only when __thisObjIdx >= 0 -- the boxed case; the byref case mutates in
//  place through the frame address, no write-back.)
if (!i.IsStatic && type.IsValueType && !type.IsPrimitive)
{
    // The method may have mutated instance_of_this_method. Write it back to
    // the box: replace mStack[__thisBoxIdx] with a fresh box of the (possibly
    // mutated) value. This is the Neo analog of WriteBackInstance -- a flat-
    // bytes write into a NEW box (CLR boxes are immutable, so re-box).
    sb.AppendLine($"            if (__thisObjIdx >= 0) {{");
    sb.AppendLine($"                __mStack[__thisBoxIdx] = instance_of_this_method;  // re-box by value");
    sb.AppendLine($"            }}");
}
```

**Note: CLR boxed structs are immutable post-creation** (the CLR does not permit
mutating a boxed struct in place via its reference; `unbox` gives a ref to the
box's interior for `unbox.any`-style mutation, but a `call` on a boxed struct
unboxes to a copy). The exact semantics: `MethodInfo.Invoke(boxedStruct,
args)` on a CLR struct instance method operates on a COPY; the box is NOT
mutated (this is the CLR behavior, which the reflection fallback at D2 inherits
for free). The autogen `*Neo` wrapper, however, calls the method DIRECTLY on the
unboxed local `instance_of_this_method`, so for a mutating method the local's
mutation is NOT propagated unless D4 re-boxes. **D4 is the conservative choice**
(re-box so the mutation propagates); the alternative (match CLR `MethodInfo.Invoke`
semantics exactly -- drop the mutation) is also defensible. The apply phase
picks based on whether any green test relies on the mutation propagating (probe
at apply; the conservative D4 is the default).

**`WriteBackInstance` confirmation:** the Neo wrapper does NOT emit
`WriteBackInstance` (verified -- `GenerateMethodWraperCode_Neo` has no such
call). D4's re-box IS the Neo value-type-`this` write-back; the Legacy
StackObject `WriteBackInstance` (`CommonBindingGenerator.cs:131`) is the
Legacy-only mechanism. This confirms the 13b design note ("WriteBackInstance
elimination is a no-op for Neo") and adds the value-type-`this` write-back as
the flat-bytes re-box.

### D5 -- Edge cases / NIE guards

- **CLR struct `this` with reference fields and NO binder:** the 13b NIE guard
  applies (throw a clearly-tagged NIE directing the user to register a binder).
  The reflection fallback at D2 + the autogen prologue at D3 both reuse the 13b
  `NeoClrStructHasReferenceField` / `NeoBindingHasReferenceField` guards.
- **CLR struct `this` with a binder:** the binder maps ref fields. The D2/D3
  reads use `ReadNeoValueType` for the pure-primitive case; the binder case
  needs the binder's Neo-cursor API (which does NOT exist -- the 13b deferral).
  Stay NIE for the binder-with-ref-fields case (matches the 13b param read).
- **`constrained.` callvirt on a CLR struct:** the Step 17 `Constrained` runtime
  arm handles the IL-VT case; the CLR-struct case (a constrained callvirt that
  resolves to a CLR struct's override) is the 4b-by-callvirt shape -- probe at
  apply whether it flows through D2/D3 or needs the Step 17 arm (likely the
  former; the callvirt lowering produces the same byref `this`).
- **Default-struct `this` (zeroed):** a `default(T)` `this` flows through D3's
  `__thisBox != null ? ... : default` arm correctly.
- **Primitive-type `this`:** a primitive (e.g. `int`) instance method is rare
  and the primitive `this` path is unchanged (the 4b discriminator keys on
  `IsValueType && !IsPrimitive`).

## Risks / Trade-offs

- **[The `this`-slot width is dump-gated]** -> Mitigation: D2/D3 permit both the
  8-byte-Ref-Slot and the flat-bytes read; the apply phase JIT-dumps
  `local.Method()` to confirm which the call-lowering sized. If the dump shows
  neither (an unexpected third shape), STOP and pin the dump (the Q-STRUCT /
  Q-LONG / K1 "probe before fixing" discipline). The risk is MEDIUM because the
  call-lowering's value-type `this` handling is the one untested corner of the
  13b unified layout (13b tested by-value PARAMS, not the `this` slot).
- **[The D4 re-box choice could diverge from CLR `MethodInfo.Invoke`
  semantics]** -> Mitigation: D4 is conservative (propagate the mutation). If a
  green test relies on the CLR "boxed struct call drops the mutation" semantics,
  D4 would change behavior -- probe at apply with a boxed-struct-mutation test.
  The reflection fallback (D2) inherits the CLR semantics for free (it calls
  `MethodInfo.Invoke`); only the autogen path (D3/D4) is at risk.
- **[4b discriminator broadens the `HasThis` arm of BOTH readers -- shared by
  every CLR instance call]** -> Mitigation: the discriminator keys on
  `DeclearingType.IsValueType`, which is FALSE for every reference-type `this`,
  so the reference-type path is byte-identical (the existing 4-byte mStack-index
  read). The full `NeoStep` smoke (108/108, CLR-binding tests as the canary) is
  the gate. Adversarial probes (Step 17 B1 / F-MAJ-1 lesson) MANDATORY: a green
  smoke does NOT prove a codegen gate correct.
- **[Boxed-CLR-VT call (4a) shares the call-lowering with the byref case (4b)
  -- a mis-discrimination would corrupt the `this` slot]** -> Mitigation: the
  discriminator is the Ref Slot's `objectIndex` half (`-1` byref vs `>=0` box),
  already encoded correctly by `ldloca` / box. A boxed struct call flows through
  the existing reference-type call-lowering (the box is an mStack object), so
  the `this` slot IS the 4-byte mStack-index shape; a byref call flows through
  the byref param layout (8-byte Ref Slot). The two are distinguishable at the
  call site by whether the source is a `ldloca` (byref) or a box (ref). Probe at
  apply.
- **[Callvirt on a CLR struct (`local.VirtualMethod()`)]** -> Mitigation: a
  CLR struct's virtual method (e.g. `ToString`, `GetHashCode`) is either
  inherited from `System.Object` (box-once + call on the box -- the Step 17
  `Constrained` arm owns this) or overridden (a direct call on the byref `this`
  -- D2/D3). Probe at apply whether the callvirt lowering produces the same
  `this` shape as a direct `call`; if it diverges, the callvirt case is a
  follow-up (not a regression -- the callvirt-on-CLR-struct path is the F-3
  pre-existing gap).

## Migration Plan

No migration: all changes are behind `ENABLE_NEO_MODE` and additive (replacing
a `// TODO` and a 4-byte mis-read with implementations). Legacy CLR binding is
untouched. Rollback = revert the change directory; no data-format changes.

## Open Questions

- Does the call-lowering size a CLR struct instance `this` slot as the 8-byte
  Ref Slot (byref) or as flat bytes (in-frame value)? (Resolved during apply via
  a JIT dump of `local.Method()`; D2/D3 permit both.)
- Does any green NeoStep smoke case rely on the CLR "boxed struct call drops the
  mutation" semantics, which D4's re-box would change? (Probe at apply with a
  boxed-struct-mutation test; D4 defaults to conservative re-box.)
- Does `callvirt` on a CLR struct override produce the same `this` shape as a
  direct `call`? (Probe at apply; if divergent, the callvirt case is a follow-
  up, not a regression.)
- Is a separate `AppendThisCodeNeo` helper warranted, or is inlining the
  discriminator into `GenerateMethodWraperCode_Neo` cleaner? (Apply-phase
  decision; the minimal form inlines, mirroring how the reference-type `this`
  is inlined today.)

## Apply-Phase Resolution (2026-07-05)

**Dump-confirmed `this`-slot representation.** The C# compiler lowers
`local.VTInstanceMethod()` and `new VT(args)` to `ldloca; call`. The `this`
source register holds a frame-native byref = an 8-byte Ref Slot
`(-1, structFrameOffset)` produced by `ldloca`. The call-lowering
(`Optimizer.Neo.cs:1170-1171`) sizes the VT instance `this` DEST slot via
`AllocateNeoCallParamSlot(DeclearingType)` -> the `IsValueType` branch ->
flat bytes (`GetNeoValueTypeManagedSize`, e.g. 12 for TestVector3NoBinding).
So: SOURCE = 8-byte byref; DEST = struct's flat-byte width. The pre-fix copy
copied `dstInfo.Size` (12) bytes from the 8-byte byref temp -> 4 bytes of
garbage + the struct's tail (the F-3 defect).

**F-3 / NEO-BYREF-THIS: CLOSED.** `new TestVector3NoBinding(100f,200f,300f)`
end-to-end (the F-3 reproducer) and `v.LengthSquaredInt()` (the byref-`this`
instance method) both pass after the fix; both FAIL on HEAD with the F-3
`ArgumentOutOfRangeException` (stash-toggle confirmed). The 4b fix closes F-3
for the direct-`call` shape (the `initobj;ldloca;call ctor` and
`ldloca;call method` lowerings).

**Chosen discriminator / mechanism (DEVIATED from the design's literal D2/D3).**
The design assumed the readers dereference the byref. But BOTH readers lack the
real caller `frameBase`: the autogen delegate signature
`CLRRedirectionDelegateNeo(ILIntepreter, byte* frameBase, ...)` is invoked with
`targetBase` (the callee param region pointer) as `frameBase`, NOT the caller's
frame; the reflection `Invoke(byte*)` only receives `targetBase`. Threading the
real `frameBase` through the delegate signature would break the checked-in
static bindings (the delegate type is shared with all generated `*_Neo` methods).

The fix instead DEREFERENCES THE BYREF AT THE COPY SITE:
1. `NeoCallParamMap` gains a `PrimitiveByRefSrc` flag per prim slot
   (`JITCompiler.cs`).
2. The call-lowering marks the VT instance `this` slot's source as byref
   (`Optimizer.Neo.cs`).
3. `CopyNeoCallArguments` (`ILIntepreter.Neo.cs`) dereferences a byref source:
   reads the 8-byte Ref Slot at `frameBase + PrimitiveSrc[i]`, extracts the
   offset, and copies `PrimitiveSize[i]` bytes from `frameBase + offset` into
   the dest slot. So the callee param region's `this` slot holds the struct's
   FLAT BYTES.
4. Both readers (`CLRMethod.Invoke` HasThis arm + the autogen wrapper) read flat
   bytes via `ReadNeoValueType` exactly like a by-value VT param -- no
   `frameBase` needed, no byref/box discriminator needed.

**Ctor / mutating-method copy-back (the `ref this` semantics).** The reflection
reader boxes the struct off the param-region flat bytes, calls the method
(`cDef.Invoke` / `def.Invoke`) -- which mutates the BOX in place (verified: CLR
`ConstructorInfo.Invoke(box, args)` / `MethodInfo.Invoke(box, args)` mutate the
boxed struct in place, not a copy) -- then writes the mutated box's flat bytes
back to the param region's `this` slot (`WriteNeoValueType`). A new
`CopyNeoCallThisBack` post-call reverse copy (run in the Neo Call arm after
`InvokeNeoCallTarget`) propagates those bytes back to the caller's in-frame local
(the byref's target). This delivers CLR `ref this` struct semantics for the
reflection path: `new VT(args)` populates the local; `v.Reset()` zeroes it.

**D4 boxed-re-box (4a): NOT emitted in the autogen wrapper.** The dump-gate
confirmed NO direct-`call` path produces a boxed `this` (`objectIndex >= 0`); a
direct `call` always uses `ldloca` (frame-native byref, `objectIndex == -1`). A
boxed `this` is ONLY reachable via `constrained.callvirt`, which is the Step 17
`Constrained` arm (a NIE today). So the D4 boxed-re-box discriminator would be
dead code in the current scope. The 4a write-back IS implemented for the
reflection path via `CopyNeoCallThisBack` (which no-ops the boxed case, matching
CLR `MethodInfo.Invoke` "boxed-VT call drops the mutation" semantics). The
autogen 4a boxed-re-box lands with the Step 17 `Constrained` completion child.

**Risk-3 callvirt outcome: FOLLOW-UP (not a regression).** `v.ToString()` on a
struct override compiles to `constrained.callvirt`, which hits the Step 17
`Constrained` NIE -- NOT the 4b direct-call path. So callvirt-on-CLR-struct is a
Step 17 completion (D-CONSTRAINED) follow-up, not a 4b regression. 4b owns the
direct `call` lowering of a struct instance method (probes 5.1/5.3). A standalone
callvirt probe was REMOVED (it would either assert the Step 17 NIE -- Neo-specific,
fails on Legacy which handles constrained.callvirt -- or accept both outcomes,
too weak).

**Actual edit sites:**
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- added
  `PrimitiveByRefSrc` to `NeoCallParamMap`.
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` -- call-lowering:
  flag the VT instance `this` slot as byref-source in the copy map.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` --
  `CopyNeoCallArguments` derefs byref sources; new `CopyNeoCallThisBack` post-call
  reverse copy; Call arm invokes it after `InvokeNeoCallTarget`.
- `ILRuntime/CLR/Method/CLRMethod.cs` -- `Invoke(byte*)` HasThis arm: VT `this`
  reads flat bytes via `ReadNeoValueType` (+ NIE guards); post-invoke write-back
  of `instance` to the param region for ctor / mutating methods.
- `ILRuntime/Runtime/CLRBinding/MethodBindingGenerator.cs` --
  `GenerateMethodWraperCode_Neo`: VT `this` reads flat bytes via
  `ReadNeoValueType` (+ NIE guard); replaces the `// TODO: ValueType instance in Neo`.
- `ILRuntimeTestBase/TestFramework/TestVector3.cs` -- host helpers: instance
  methods `LengthSquaredInt` / `Reset` on `TestVector3NoBinding`; new
  `TestClrStructWithRef` (ref-field struct for the NIE probe).
- `TestCases/NeoStep13bTest.cs` -- 9 adversarial probes (5.1-5.3, 5.5-5.10).

**Verify results:** Neo smoke 117/117 (108 baseline + 9 probes); NeoOptHardening
16/16; Legacy NeoStep13_ probes 9/9 (Legacy-neutral; the 7 pre-existing Legacy
NeoStep failures reproduce with the fixes stashed). Stash-toggle: 6 of 9 probes
FAIL on HEAD with the F-3 `ArgumentOutOfRangeException`.
