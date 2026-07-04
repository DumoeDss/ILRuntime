# Design -- implement-neo-step13b (CLR binding codegen + CLRMethod unified param layout)

Grounded in the branch state at propose time (HEAD after Step 17,
`21b68d92`). All file:line refs are to that state. Scope is the **core Area 5
ABI fix only**; Area 4, CLR ref/out, and CLR-object stind/ldind are DEFERRED
(see proposal.md In/Deferred list).

## Context

Neo passes CLR method arguments in a contiguous callee "param region" built by
the optimizer (`AllocateNeoCallParamSlot`, `Optimizer.Neo.cs:1319`). Two
readers consume that region, and they MUST agree with the layout byte-for-byte:

1. **The reflection fallback** `CLRMethod.Invoke(byte*)` (`CLRMethod.cs:332`),
   invoked from `InvokeNeoClrMethod` (`ILIntepreter.Neo.cs:158-167`) when no
   `RedirectionNeo` autogen delegate is registered.
2. **The autogen redirect delegate** `*_Neo` (`MethodBindingGenerator.cs:249`,
   `ConstructorBindingGenerator.cs:55`), whose body is emitted by
   `AppendArgumentCodeNeo` / `GetReturnValueCodeNeo`
   (`BindingGeneratorExtensions.cs:107` / `:440`).

Both readers walk the region with a single `__curPrim` cursor advanced by each
param's exact size (no per-param alignment) -- this is the contract
`AllocateNeoCallParamSlot` documents (`Optimizer.Neo.cs:1322-1329`).

**What already works:** primitives, enums, IL value types (sized via
`TotalPrimitiveSize`), and reference types. The `IsValueType` branch of
`AllocateNeoCallParamSlot` (`:1357-1361`) ALREADY sizes a CLR struct via
`GetPrimitiveSize`. So the *callee layout* for a CLR struct is already correct.

**What is broken (K2/K2-FAM):**
- The call-lowering has a **caller-temp-slot fallback** (`Optimizer.Neo.cs:1172-
  1181`): for a CLR struct param it overrides the callee layout with the
  *caller source register's* `Size`/`RefCount` (`srcInfo`), so the param-copy
  (`CopyNeoCallArguments`, `ILIntepreter.Neo.cs:130`) copies bytes whose
  shape does not match what the reader expects. The fallback exists "so
  unsupported CLR structs (e.g. TaskAwaiter) do not fail during JIT prewarm".
- `CLRMethod.Invoke(byte*)` throws `Step 13` NIE for any CLR struct param
  (`CLRMethod.cs:362-365`).
- `GetReturnValueCodeNeo` (`BindingGeneratorExtensions.cs:460`) and the
  reflection return path (`ILIntepreter.Neo.cs:191-194`) throw for a CLR
  struct return.

A CLR struct is stored **flat bytes** in the param region (it is an argument
or return slot -- NOT a boxed-ref local; that representation is for locals
per Step 13 Finding G). So the param/return path is a width-sized memcpy,
identical in shape to an IL value-type param. K2's "reads a primitive field
VALUE as an mStack index" is exactly the fallback miscopying bytes.

## Goals / Non-Goals

**Goals (IN this pass):**
- CLR struct by-value parameter: caller copies the struct's primitive bytes
  (+ ref slots if the struct has a binder) into the callee region; both
  readers (`Invoke(byte*)` and `AppendArgumentCodeNeo`) materialize it.
- CLR struct return value: the callee writes the struct's primitive bytes
  into the dest frame slot; both return paths read it.
- Remove the caller-temp-slot fallback.
- Close K2 / K2-FAM.

**Non-Goals (DEFERRED):**
- Area 4 `Unsafe.Unbox<T>` direct-call + eliminate `WriteBackInstance`
  (proposal: DEFERRED).
- CLR-method `ref`/`out` (byref Ref Slot -> CLR `ref T` crossing).
- CLR-object `stind`/`ldind` via field hash (Step 17 M1 deferral).
- IL value-type `newobj` / CLR struct `newobj` (Step 18).
- async state machines (`TaskAwaiter.GetResult()` by value) -- these exercise
  this pass's param/return path but async itself is Step 20.

## Decisions

### D1 -- Unified callee param layout: remove the fallback

`AllocateNeoCallParamSlot` (`Optimizer.Neo.cs:1319`) already produces the
correct callee slot for a CLR struct (the `else if (type.IsValueType)` branch,
`:1357-1361`, sizes by `GetPrimitiveSize`). The fix is to STOP special-casing
CLR struct params in the call-lowering and let them flow through the same
`AllocateNeoCallParamSlot` call as every other param type.

```csharp
// Optimizer.Neo.cs, in the CLRMethod call-lowering param loop (~:1163-1186).
// BEFORE: a special `if (paramType.IsValueType && !IsPrimitive && !ILType &&
//         !IsEnum)` branch copied srcInfo.Size/RefCount (the fallback).
// AFTER:  delete that branch; all params go through AllocateNeoCallParamSlot.
for (int p = 0; p < pCnt; p++)
{
    int dstIndex = (op.Code == OpCodeREnum.Newobj) ? p + 1 : p;
    CLR.TypeSystem.IType paramType =
        (targetMethod.HasThis && op.Code != OpCodeREnum.Newobj && p == 0)
            ? targetMethod.DeclearingType
            : clrMethod.Parameters[p - ((targetMethod.HasThis && op.Code != OpCodeREnum.Newobj) ? 1 : 0)];
    paramInfos[dstIndex] = AllocateNeoCallParamSlot(paramType, ref curPrim, ref curRef, domain);
}
```

The existing param-copy loop (`:1197-1215`) then copies `dstInfo.Size` bytes
+ `dstInfo.RefCount` ref slots from the source register. Because the source
register for a CLR-struct local is a **boxed-ref** (Step 13 Finding G: a CLR
struct local is a 4-byte mStack index, `RefCount=1`), the source shape and the
new callee shape DISAGREE for structs-with-no-binder -- see D3 for the
boxed-ref-to-flat-bytes bridge. For a struct *argument* that is itself a
param-temp or an IL-field flat-bytes slot, the shapes already agree (both
flat bytes), and K2 is fixed directly.

**Why this is safe for non-struct calls:** primitives/enums/IL-VTs/ref-types
already go through `AllocateNeoCallParamSlot` (the fallback only fired for CLR
structs). Removing the fallback changes ONLY the CLR-struct branch; every
other param's layout is byte-identical. The full NeoStep smoke (CLR-binding
tests especially) is the gate.

### D2 -- `CLRMethod.Invoke(byte*)`: read CLR struct params by width

The reflection fallback (`CLRMethod.cs:332-388`) replaces the
`Step 13` NIE (`:362-365`) with a flat-bytes read sized by
`AppDomain.GetPrimitiveSize(clrType)`, plus ref-slot materialization when a
binder is present:

```csharp
// CLRMethod.Invoke(byte*), replacing the NIE at :362-365.
if (pt is CLRType clrType && clrType.IsValueType
    && !clrType.TypeForCLR.IsPrimitive && !clrType.TypeForCLR.IsEnum)
{
    int sz = AppDomain.GetPrimitiveSize(clrType);
    var binder = clrType.ValueTypeBinder;
    if (binder != null)
    {
        // Struct with a binder: hand the binder the frame cursor + ref base.
        // binder.AssignFromFrameNeo(targetBase + curPrim, ...) -> boxed struct.
        // (Reuse the Step 13 binder boxed-ref shape; the binder maps ref
        //  fields.) curPrim += sz; curRef += binder.ReferenceCountFor(...).
        param[i] = binder.ReadNeo(targetBase, ref curPrim, mStack, ref curRef);
    }
    else
    {
        // Pure-primitive CLR struct (no ref fields without a binder): read the
        // flat bytes into a managed T via a typed read, then box by value.
        // (Same shape as the Step 13 no-binder box path.)
        param[i] = ILIntepreter.ReadNeoValueType(clrType.TypeForCLR,
                                                 targetBase, ref curPrim, sz);
    }
    continue;
}
```

`ReadNeoValueType` (new, see D4) reads `sz` bytes at the cursor and boxes them
into the runtime type. For pure-primitive structs this is a single
`Unsafe.ReadUnaligned<T>`-shaped read into a boxed `T`. A struct WITH ref
fields and no binder cannot be read (no way to map ref slots without the
binder) -> throw a clear Step-13b-tagged NIE directing the user to register a
binder (this is the only remaining NIE in the param read).

The existing primitive branch (`:373-387`) and the ILType/reference branch
(`:367-372`) are unchanged.

### D3 -- The boxed-ref-local -> flat-bytes-param bridge (K2-FAM closure)

A CLR struct LOCAL is a boxed-ref (4-byte mStack index, Step 13 Finding G).
A CLR struct PARAM slot is flat bytes (`AllocateNeoCallParamSlot` sizes by
`GetPrimitiveSize`). When a boxed-ref local is passed by value to a CLR
method, the param-copy must UNBOX the local into the flat-bytes callee slot,
not memcpy 4 bytes (which is K2: it copies the mStack index into the param
region, and the reader then reads that index as primitive bytes).

This is the K2-FAM case. The bridge is the existing Step 13 unbox machinery
(`NeoWritePrimitiveToFrame` for primitives; `PerformMemberwiseClone` +
write-back for structs), applied at the call site. Concretely: when the
call-lowering's param-copy detects that the source register is a boxed-ref
CLR-struct local (`srcInfo` is a 4-byte ref slot whose declared param type is a
CLR value type) but the callee slot is flat bytes, it emits an **unbox** copy
rather than a raw `CopyBlock`. The implementer determines the cleanest hook
(either a special-case in `CopyNeoCallArguments` keyed on the
`NeoCallParamMap` entry, or an explicit unbox lowering stamped by the
optimizer for that param). The Step 13 unbox helpers are reused -- no new
copy primitive.

This bridge is the one genuinely new mechanism in the pass; it is the K2-FAM
closure. A reproducer test (CLR struct local -> by-value param -> field
round-trip) is mandatory.

### D4 -- `ReadNeoValueType` / `WriteNeoValueType` helpers

New non-generic-by-type helpers next to the existing `ReadNeo*` family
(`ILIntepreter.Neo.cs:26-127`), mirroring the Step 16 element-read pattern:

```csharp
// Read sz bytes at frameBase+curPrim into a boxed object of the given CLR
// type. curPrim += sz. For pure-primitive structs this is a typed read; for
// structs with a binder the caller passes the binder instead (D2).
internal static unsafe object ReadNeoValueType(Type clr, byte* frameBase,
                                               ref int curPrim, int sz);

// Write a boxed CLR struct's primitive bytes into frameBase+dst (sz bytes).
// The inverse, used by the return-value path.
internal static unsafe void WriteNeoValueType(object value, byte* dst, int sz);
```

These wrap `Unsafe.ReadUnaligned`/`WriteUnaligned` over a stackalloc-sized T
local (or `Marshal.AllocHGlobal` for large structs -- the implementer picks;
the struct sizes in the test suite are small). They are the by-type
generalization of the existing `NeoWritePrimitiveToFrame`
(`ILIntepreter.Neo.cs:3213`).

### D5 -- Autogen binding codegen: same read/write model

`AppendArgumentCodeNeo` (`BindingGeneratorExtensions.cs:107`) currently emits a
`// TODO: CLR value type reflection fallback: Step 13` (the binder case,
`:123-127`) or a `// TODO: ByRef or unsupported ValueType` (the no-binder
case, `:130-134`). Replace with:

- **Binder present:** `ILRuntime.Runtime.Generated.CLRBindings.s_<T>_Binder`
  is consulted (mirror the Legacy `AppendArgumentCode` binder path, `:173-198`,
  but on the Neo cursor: the binder reads from `__frameBase + __curPrim`).
- **No binder, pure-primitive struct:** emit
  `ReadNeoValueType(typeof(T), __frameBase, ref __curPrim, sz)`.
- **No binder, struct with ref fields:** emit a Step-13b-tagged NIE throw
  (matches D2).

`GetReturnValueCodeNeo` (`BindingGeneratorExtensions.cs:458-461`) gets the
inverse: the `else if (type.IsValueType)` branch writes the result via
`WriteNeoValueType(result_of_this_method, __retDst, sz)` (and ref slots via the
binder when present), replacing the `Step 13` TODO.

**The generated + reflection paths share `ReadNeoValueType`/`WriteNeoValueType`
+ the binder helpers**, so D2 and D5 stay byte-consistent with the D1 layout by
construction.

### D6 -- Return-value reflection path

`InvokeNeoClrMethod` (`ILIntepreter.Neo.cs:191-194`) throws for a CLR struct
return. Replace with `WriteNeoValueType(res, retDstPtr, sz)` (+ ref slots via
the binder). `retDstPtr` is the caller's dest frame slot, already sized for
the value type by `AllocateLocalStackSpaces` -- so the write is a flat-bytes
copy into the caller's flat-bytes dest.

## Edge cases / Non-Goals

- **CLR struct WITH ref fields and no binder:** throw a Step-13b-tagged NIE in
  both readers (D2/D5). Pure-primitive structs and binder-registered structs
  work. This matches Legacy (which also requires a binder for structs with
  refs to avoid descriptor allocation).
- **Generic CLR struct params (`T` where `T:struct`):** the layout helper uses
  `GetPrimitiveSize(type)` which resolves the instantiated generic; rare in
  the smoke. If a generic-struct-param smoke case surfaces a layout mismatch,
  it is reported (not silently widened); the fallback-to-`srcInfo` is NOT
  reintroduced.
- **Value-type instance `this` (Area 4):** DEFERRED. The generated wrapper's
  value-type `this` is still a `// TODO` (`MethodBindingGenerator.cs:261`).
  A CLR struct *instance* method call (not static, `this` is a struct) is not
  exercised by the in-scope param/return work; it lands with Area 4.
- **`WriteBackInstance`:** the Neo wrapper does NOT emit it (verified:
  `GenerateMethodWraperCode_Neo` has no `WriteBackInstance` call; only the
  Legacy `GenerateMethodWraperCode_Legacy` does). Area 4's "eliminate
  WriteBackInstance" is therefore a no-op for the Neo path today; the real
  Area 4 work is the value-type-`this` direct-call, which is DEFERRED.
- **Byref CLR params:** DEFERRED (separate typed-reference bridge).

## Risks / Trade-offs

- **[Removing the fallback perturbs every CLR struct call]** -> Mitigation:
  the fallback ONLY fired for CLR struct params; primitives/enums/IL-VTs/refs
  are byte-identical. The full NeoStep smoke (was 81/81; CLR-binding tests are
  the canary) is the gate. If a struct-call smoke case regresses and the root
  cause is not the boxed-ref bridge (D3), the fallback is NOT reintroduced --
  the underlying layout bug is fixed.
- **[K2-FAM bridge (D3) is the one new mechanism]** -> Mitigation: it reuses
  the Step 13 unbox helpers and is gated to the boxed-ref-local -> flat-bytes-
  param case only; a dedicated reproducer test is mandatory. If the bridge
  proves too large/risky, the implementer MAY land D1/D2/D5 (which close K2
  for the flat-bytes-source case) and DEFER only the boxed-ref-local bridge
  (D3, the K2-FAM half) -- the proposal's In/Deferred list is updated to
  reflect the split.
- **[Autogen + reflection readers must stay byte-consistent]** -> Mitigation:
  both share `ReadNeoValueType`/`WriteNeoValueType` + the binder helpers (D5),
  so consistency is by construction, not by careful duplication.

## Migration Plan

No migration: all changes are behind `ENABLE_NEO_MODE` and additive (replacing
NIE throws / a temporary fallback with implementations). Legacy CLR binding is
untouched. Rollback = revert the change directory; no data-format changes.

## Implementation deviations (apply pass -- 2026-07-04)

The apply pass followed D1/D2/D4/D5/D6. D3 (the K2-FAM boxed-ref-local bridge)
was DEFERRED per its own safety valve. Two concrete deviations from the letter
of the design, both with rationale:

1. **CLR-struct sizing: `GetPrimitiveSize` -> new `GetNeoValueTypeManagedSize`.**
   Finding P (the callee layout "already sizes a CLR struct via
   `GetPrimitiveSize`") was OPTIMISTIC: `AppDomain.GetPrimitiveSize`
   (`AppDomain.cs:1880-1934`) only knows the primitive ILType singletons and
   throws NIE for ANY non-primitive value type. So the `IsValueType` branch of
   `AllocateNeoCallParamSlot` was never actually correct for CLR structs -- the
   caller-temp-slot fallback masked it AND masked an async-state-machine
   prewarm crash (`AsyncTaskMethodBuilder` param during `AppDomain.Prewarm`).
   Fix: a new `Optimizer.GetNeoValueTypeManagedSize(Type)` (public, cached per
   Type via the generic `Unsafe.SizeOf<T>()` instantiated by reflection) sizes
   a CLR struct's managed byte size and NEVER throws, so prewarm of a method
   with an unsupported CLR struct param no longer crashes. The reader/writer
   (`ReadNeoValueType`/`WriteNeoValueType`) use the SAME size, so the layout
   and the readers stay byte-consistent by construction (single size source).

2. **`ReadNeoValueType`/`WriteNeoValueType` via DynamicMethod, not `stackalloc`
   + `Unsafe.ReadUnaligned<T>`.** `byte*` cannot be a generic type argument
   (CS0306), so `Func<byte*,object>`/`Action<byte*,object>` are illegal. The
   helpers use custom `NeoVtReaderDelegate`/`NeoVtWriterDelegate` delegate types
   (pointer params are allowed on custom delegates) wrapping a cached
   `System.Reflection.Emit.DynamicMethod` per Type (IL: `ldarg`; call
   `Unsafe.ReadUnaligned<T>(void*)` / `WriteUnaligned<T>(void*,T)`; box/unbox;
   ret). The hot path is a delegate invoke with no reflection. Visibility of
   `GetNeoValueTypeManagedSize` / `ReadNeoValueType` / `WriteNeoValueType` is
   `public` (not `internal`) because the autogen binding code is compiled into
   the HOST assembly and must call them.

3. **Phase 3 (D3) DEFERRED.** A CLR struct LOCAL obtained from a CLR method
   RETURN is flat bytes (D6 writes via `WriteNeoValueType`), NOT a boxed-ref,
   so passing it by value already works without a bridge (verified by
   `NeoStep13bClrStructByValueParamNoBinding`). The boxed-ref representation
   only arises from Box/Initobj. A clean D3 reproducer needs `ldfld`/`stfld` on
   CLR struct fields (DEFERRED), so the bridge has no clean test surface this
   step. Left as a documented deferred item (silent wrong-result for the
   Box/Initobj-source shape, like the value-type-`this` TODO), NOT a regression
   of a previously-green case.

4. **Test design.** The design's K2 scenario ("the callee receives the struct's
   fields unchanged -- the sum of its fields equals...") cannot be expressed
   with IL-side field reads (`ldfld` on CLR struct fields is deferred) NOR with
   `new T(x,y,z)` (emits the unimplemented `push` value-type-`this` ctor
   opcode). The apply pass adds HOST helpers to `TestCLRBinding`
   (`TestClass3.cs`): `MakeTestVector3NoBinding` (returns a struct; exercises
   D6), `SumTestVector3NoBindingFields` (takes a struct by value, returns a
   primitive; exercises D2 + the flat-bytes-source K2 case). The IL test
   obtains the struct from the RETURN (no ctor push) and checks the result via
   the primitive-returning re-feed (no `ldfld`). These go through the
   reflection fallback (no autogen redirect for them), exercising D2/D6.

DEFERRED items confirmed still clearly-tagged-NIE / silent-TODO (unchanged from
the design): Area 4 value-type `this` (`MethodBindingGenerator.cs:261` TODO);
CLR-method ref/out (the autogen ByRef branch emits a Step-13b-tagged TODO);
CLR-object stind/ldind via field hash (Step-17-tagged NIE). Legacy CLR binding
+ Legacy codegen UNTOUCHED (all new code behind `#if ENABLE_NEO_MODE`).
