# Design — neo-clr-vt-reffields-binder

## 1. The throw site and the guard
- `ILRuntime/CLR/Method/CLRMethod.cs:499-502` — the Neo reflection-fallback
  `Invoke(byte*)`. For a CLR-struct param with `ValueTypeBinder == null`:
  ```csharp
  else if (NeoClrStructHasReferenceField(t))
      throw new NotImplementedException("CLR value type with reference fields and no ValueTypeBinder (Step 13b): register a binder. Type: " + t.FullName);
  ```
  `NeoClrStructHasReferenceField` (`CLRMethod.cs:642-664`) returns true for
  `RuntimeFieldHandle` (it wraps the reference-typed `RuntimeFieldInfoInternal` on net8.0).
- Twin autogen stub: `BindingGeneratorExtensions.cs:203-207` (same message; not reached
  for InitializeArray because it is redirected, not autogen-bound).

## 2. The ValueTypeBinder mechanism (and why it cannot help here)
- Registration: `AppDomain.RegisterValueTypeBinder(Type, ValueTypeBinder)`
  (`AppDomain.cs:1228`) stores into `valueTypeBinders`, calls
  `binder.RegisterCLRRedirection`, and sets `binder.CLRType`. `CLRType.ValueTypeBinder`
  reads it back. The harness registers binders for user structs in
  `ILRuntimeTestBase/Adapters/helper.cs:32-40` (TestVector3, Fixed64, etc.).
- Layout effect (`Optimizer.Neo.cs:1577-1599`, `AllocateNeoCallParamSlot`, CLR-struct arm):
  `slot.Size = GetNeoValueTypeManagedSize(TypeForCLR)` always; if a binder exists,
  `slot.RefCount = managedCount` (the binder's managed-ref count) and `refOffset` advances.
  If NO binder, `RefCount = 0` — the whole struct is treated as pure flat bytes, which is
  only valid when the struct has NO managed refs.
- The binder class hierarchy (`ValueTypeBinder.cs`) is built on the Legacy `StackObject`
  model (`CopyValueTypeToStack`/`AssignFromStack` read/write `StackObject*`). The Neo
  flat-bytes reader (`ReadNeoValueType`) consumes a `byte*` cursor, not `StackObject*`, so
  a Legacy binder cannot drive it. `BindingGeneratorExtensions.cs:167-169` documents this:
  *"a binder struct WITH reference fields would need the binder's ref-mapping on the Neo
  cursor (a Neo-cursor binder API does not exist yet)"*. => The binder route is closed for
  Neo ref-field structs in general, and meaningless for an opaque token like
  `RuntimeFieldHandle` regardless.

## 3. Why these 18 hits lack a binder (and why that is correct)
The 18 hits are all `System.RuntimeFieldHandle`, all from C# array initializers. A binder
is the WRONG tool: (a) it is a Legacy-StackObject API useless to the Neo cursor, and
(b) `RuntimeFieldHandle` is a system token with no field-level decomposition a binder
could express. They "lack a binder" because no binder should exist for them. The right
fix is to avoid the marshal entirely via a Neo redirect for the one intrinsic that
produces them.

## 4. Root cause: InitializeArray has no Neo redirect
- Legacy redirect: `CLRRedirections.InitializeArray` (`CLRRedirections.cs:232-380`),
  registered in the `AppDomain` ctor at `AppDomain.cs:149-150` via
  `RegisterCLRMethodRedirection` (writes the Legacy `redirectMap`). Its body:
  `data = StackObject.ToObject(param, ...) as byte[]`; `array = ...`; pin + `Marshal.Copy`.
- Neo dispatch consults ONLY `RedirectMapNeo` (`CLRMethod.cs:145-155` `RedirectionNeo` ->
  `TryGetRedirection(appdomain.RedirectMapNeo, ...)`). `InvokeNeoClrMethod`
  (`ILIntepreter.Neo.cs:986-997`): if `clrMethod.RedirectionNeo != null`, call it and
  return; ELSE fall to `clrMethod.Invoke(targetBase, mStack, isNewObj)` (the reflection
  fallback where the Step-13b NIE lives). No Neo entry => fallback => NIE.

## 5. The data-representation problem (load-bearing)
`CopyNeoCallArguments` (`ILIntepreter.Neo.cs:425-428`) copies each param by the CALLEE's
declared size (`map.PrimitiveSize[i]`, built from the callee signature via
`AllocateNeoCallParamSlot`). Param 1 of InitializeArray is `RuntimeFieldHandle` ->
`GetNeoValueTypeManagedSize(RuntimeFieldHandle)` (~8 managed bytes). The actual initializer
blob is N bytes (e.g. 128 for TC5's 32 ints). So the blob is TRUNCATED if it is pushed as
flat bytes.

The data source exists: `ILTypeInstance.cs:71-86` (Neo path) replays Cecil
`f.InitialValue` for static fields with non-empty initial data:
```csharp
if (f.InitialValue != null && f.InitialValue.Length > 0) {
    var offset = type.GetStaticFieldOffset(idxStatic);
    if (managedObjs != null)
        managedObjs[offset.ReferenceOffset] = f.InitialValue;   // the byte[] blob
}
```
So the `<PrivateImplementationDetails>` blob field's value is a `byte[]` sitting in the
static instance `ManagedObjects`. The fix is to surface THAT `byte[]` as a reference
through the `ldtoken` -> InitializeArray param 1, so the Neo redirect reads it with
`ReadNeoReference` (mirroring the Legacy redirect's `data = param[1] as byte[]`).

### Neo ldtoken field path today (`ILIntepreter.Neo.cs:1575-1617`)
Routes on `ft` (the field's declared type):
- `ft.IsPrimitive` -> read primitive bytes.
- `ft.IsValueType && ft is ILType vtil` -> copy `vtil.TotalPrimitiveSize` flat bytes +
  `vtil.TotalReferenceCount` refs.
- else (reference field) -> materialise a ref slot (`mStack.Add(rv)`; `*(int*)dstSlot = idx`).

The blob field's declared type is a compiler-generated `.size N` value type. The apply
worker must determine (empirically, via TC5) which arm it takes and whether the `byte[]`
currently reaches param 1. Two concrete remediation sub-options:
- **(preferred)** In the `ldtoken` field arm, detect the RVA-blob case — the static value
  at `ManagedObjects[off.ReferenceOffset]` is a `byte[]` — and push it as a reference
  (`mStack.Add(blob); *(int*)dstSlot = idx`). Param 1 then carries a 4-byte mStack index
  to the `byte[]`; the redirect reads it via `ReadNeoReference`. (The 8-byte RuntimeFieldHandle
  slot copies the 4-byte index + 4 don't-care bytes; the redirect reads only the index.)
- **(fallback)** If the blob struct's computed `TotalPrimitiveSize` already holds the raw
  bytes in `Primitives`, have the redirect read `PrimitiveSize`-many bytes directly from
  `targetBase + off1` and `Marshal.Copy` those. Verify byte-for-byte against TC5.

Either way the pass criterion is observable: TC5's `arr[i] == 100+i` for all 32 elements.

## 6. The fix
1. `CLRRedirections.InitializeArrayNeo` (Neo-gated), modeling `DelegateCombineNeo`
   (`CLRRedirections.cs:513`) for the param-read pattern:
   ```csharp
   int curPrim = 0;
   object array = ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);      // param 0
   object data  = ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);      // param 1 (byte[])
   // curPrim now past the RuntimeFieldHandle slot (its managed size); reading param 1
   // as a REFERENCE consumes the 4-byte mStack index the ldtoken pushed (see section 5).
   if (data is byte[] bytes && array is Array arr && bytes.Length > 0) {
       var h = System.Runtime.InteropServices.GCHandle.Alloc(arr, GCHandleType.Pinned);
       var dst = System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(arr, 0);
       System.Runtime.InteropServices.Marshal.Copy(bytes, 0, dst, bytes.Length);
       h.Free();
   }
   // void method -> no retDst write.
   ```
   NOTE the cursor discipline: param 0 is a 4-byte ref; param 1 is laid out as an
   ~`RuntimeFieldHandle`-sized slot. `ReadNeoReference` reads only the leading 4-byte
   index, so the two reads stay correct as long as the ldtoken pushed the `byte[]` index
   into the FIRST 4 bytes of param 1's slot (little-endian int). The apply worker confirms
   the cursor does not need an explicit skip (the ref-read is size-4; if the layout placed
   the index elsewhere, adjust by reading at the slot's fixed offset).
2. Register on `RedirectMapNeo` in the `AppDomain` ctor (`#if ENABLE_NEO_MODE`):
   ```csharp
   var miIA = typeof(System.Runtime.CompilerServices.RuntimeHelpers).GetMethod("InitializeArray");
   RegisterCLRMethodRedirectionNeo(miIA, CLRRedirections.InitializeArrayNeo);
   ```
   (`GetMethod("InitializeArray")` returns the 2-param overload; reuse the same lookup as
   the Legacy line 149.)
3. ldtoken field-path remediation (section 5) — push the `byte[]` blob as a reference.
4. Probe (section 7).

## 7. Probes
- NEW `TestCases/NeoClrVtReffieldsBinderTest.cs`:
  - `NeoClrVtReffieldsBinder_TC1_ArrayInitializer` — a `new int[]{ ... }` with >= 32
    distinct ints (128+ bytes, forces the InitializeArray + ldtoken `<field>` form, per
    the TC5 comment) and NO try/catch. FAULT criterion: without the redirect the uncaught
    Step-13b NIE propagates => test FAILs. PASS criterion (after fix): the array is
    populated correctly; assert `arr[i] == expected` for all i (a wrong copy -> a visible
    assertion failure, not a silent pass). This makes the probe a real correctness check,
    not a ran-without-throwing check (child-1/child-2 probe discipline).
- EXISTING `NeoStepLdtoken_TC5_ArrayInitializerFieldPath` — already forward-compatible
  (its comment at `NeoStepLdtokenTest.cs:108-146` says so): after this change the
  `catch (NotImplementedException)` is dead and the `arr[i] == 100+i` loop runs. If the
  redirect is wrong, TC5 FAILs with DivideByZeroZero (the `z/d` trap) — so TC5 also guards
  correctness.

## 8. Verify
- Stash-toggle: with the change reverted, `NeoClrVtReffieldsBinder_TC1` FAILs (uncaught
  Step-13b NIE); with it applied, TC1 + TC5 PASS with correct contents.
- NeoStep smoke: baseline **318/0** (post-child-5); after, 318+N/N additional passes, 0
  new failures.
- Full Neo smoke (pre-crash): the 18 `RuntimeFieldHandle` Step-13b occurrences -> 0
  (grep `CLR value type with reference fields` in the captured log).
- Legacy-neutral: plain `Debug` + `useRegister=true` + `NeoStep` stays at the baseline
  ran/failed set (the new probes run under Legacy too — Legacy already has the
  InitializeArray redirect, so they should pass; confirm).

## 9. Risks / open questions for the apply worker
- The exact computed layout (`TotalPrimitiveSize` / `TotalReferenceCount`) of the
  compiler-generated `.size N` blob struct under ILRuntime — determines which ldtoken arm
  fires today and whether the `byte[]` already reaches param 1 or needs the remediation in
  section 5. Resolve by instrumenting TC5 (dump param 1's first 8 bytes + the mStack entry
  it indexes) before choosing sub-option (preferred) vs (fallback).
- Whether Legacy actually populates the array correctly or zero-fills (`data = ... as byte[]`
  returning null => early `return ret`, `CLRRedirections.cs:243`). If Legacy zero-fills,
  matching it is not enough — the Neo fix must read the real RVA blob (section 5's
  `byte[]` source guarantees this). Confirm TC5's contents assertion under Legacy to
  calibrate expectations.
- `ReadNeoReference` cursor behaviour across an ~`RuntimeFieldHandle`-wide slot — verify
  no off-by-N between param 0 and param 1 reads.
