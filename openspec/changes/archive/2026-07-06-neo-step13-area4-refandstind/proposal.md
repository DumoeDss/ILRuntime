## Why

The Step 13 Area 4 cohort (`neo-step13-area4`) shipped the value-type-`this`
direct-call (4b) and the `Unsafe.Unbox<T>` boxed direct-call (4a), but
deliberately scoped OUT two independent plumbing pieces (the explicit 13b
"don't bundle" lesson): **Area 4c** — a CLR method with `ref`/`out` parameters
(the typed-ref bridge), and **Area 4d** — `stind_*`/`ldind_*` on a byref to a
CLR OBJECT field (the field-hash path). Both are pre-existing gaps verified on
HEAD (`2ca6614f`):

- **4c CLR-method ref/out:** a CLR method taking `ref`/`out` args from IL is
  silently wrong today. The caller copy (`CopyNeoCallArguments`) lays the byref
  param into the callee region as an 8-byte Ref Slot `(objectIndex, offset)`
  (Step 17 sizing in `AllocateNeoCallParamSlot`), but neither reader handles
  it. The reflection fallback `CLRMethod.Invoke(byte*)` has NO `IsByRef` check
  in its param loop — it reads `t = pt.TypeForCLR` (strips the byref modifier)
  and reads the param's flat bytes, so `ref int` reads the Ref Slot's
  `objectIndex` half as the int value and never writes back. The autogen path
  (`AppendArgumentCodeNeo`) emits `default(...)` + a `// TODO: ByRef ... DEFERRED`
  comment for `pt.IsByRef` — also silent-wrong, no write-back. This is also the
  load-bearing gap behind **F-7 / NEO-DELEGATE-REFOUT** (a byref-typed delegate
  `Invoke` param — `Action<ref T>` — which routes the same param-region bytes
  through `WriteNeoCallSlot`).
- **4d CLR-object stind/ldind via field hash:** `ref clrObj.field` (a CLR
  object field address from `ldflda`) consumed by `stind_*`/`ldind_*` throws a
  Step-17/13b NIE today. The `Ldflda` arm's `else` branch produces
  `(objIdx, fieldPrimOff)` where `fieldPrimOff = field.PrimitiveOffset` — but a
  CLR object has no `Primitives[]` byte array, so the stind/ldind consumer's
  `else` branch calls `GetNeoILInstance`, which throws a clean
  `NotImplementedException` ("CLR field-hash plumbing lands in Step 13b").

Why now: both are reachable today (the Step-17 Ref Slot model + the 4b
deref-at-copy-site pattern are the prerequisites, and both shipped), so the
gaps are now first-class failures rather than dead branches. Closes the D-13B
Area 4 deferral and the F-7 obligation.

## What Changes

- **4c CLR-method `ref`/`out` typed-ref bridge.** Add byref-param handling to
  BOTH readers of the callee param region:
  - `CLRMethod.Invoke(byte*)` (`CLRMethod.cs:407-474`): for a `pt.IsByRef`
    param, read the 8-byte Ref Slot `(objectIndex, offset)` from the callee
    region, dereference it (frame-native `objectIndex == -1` -> read
    `frameBase + offset`; mStack-object `objectIndex >= 0` -> read the mStack
    object's field), pass the dereffed VALUE to the CLR method (boxed for a
    CLR struct `ref`), and after the call WRITE BACK the (possibly-mutated)
    value through the same Ref Slot (a `ref`/non-`out` arg).
  - `AppendArgumentCodeNeo` (`BindingGeneratorExtensions.cs:135-237`): replace
    the `pt.IsByRef` `default(...)` + TODO branch with codegen that reads the
    Ref Slot and dereferences it, and a new `AppendNeoWriteBackCode` (or
    inline epilogue) that writes `out`/`ref` results back through the Ref Slot
    after the call.
  - The `IsByRef` discriminator on the `IType` (or `ParameterType.IsByRef` on
    the CLR `ParameterInfo`) determines the representation unconditionally
    (the per-arm type-token insight from 13b / opt-harden-2; no per-slot flag).
- **4d CLR-object stind/ldind via field hash.** Add a CLR-object-field branch
  to the `stind_*`/`ldind_*` `else` arms (`ILIntepreter.Neo.cs:3142-3364`)
  and to `Stobj`/`Ldobj`. The byref operand's `(objIdx, off)` where
  `mStack[objIdx]` is neither an `ILTypeInstance` nor an `Array` is a CLR
  object; resolve `off` as a CLR FIELD HASH (stamped by `Ldflda` for a CLR
  field) and route to `GetFieldValue`/`SetFieldValue` (or a cached
  `FieldInfo`) on the mStack object. Requires the `Ldflda` arm to stamp the
  CLR-field identity (a `FieldInfo` handle or hash) rather than the
  IL-`field.PrimitiveOffset` for a CLR operand.
- **F-7 delegate ref/out (RELATED — share the bridge).** With 4c's byref
  marshaling primitives (deref + write-back of an 8-byte Ref Slot) factored,
  make `DelegateAdapter.NeoInvokeSub` / `WriteNeoCallSlot` byref-aware so a
  `ref`/`out` param on a delegate `Invoke` routes through the same primitives.
  In scope IF the diff stays reviewable; otherwise it splits to its own
  follow-up (see Scoping).
- **Tests:** adversarial probes in `TestCases/NeoStep13bTest.cs` (the `NeoStep`
  filter catches them). 4c — a CLR method with `ref int`, `out int`,
  `ref struct` (pure-primitive binder struct), `out T` (reference type);
  `ref int` mutation propagates to the caller's local. 4d — `ref clrObj.field`
  read + write via stind/ldind (the field-hash path); a CLR object with
  primitive + reference-type fields. Regression — the frame-native,
  ILTypeInstance, and CLR-array stind/ldind paths still work (the
  step17-completion + array-completion work); the 4b byref-`this` direct-call
  still works; the autogen/reflection split for non-byref CLR methods is
  byte-identical. Each FAIL-on-HEAD stash-toggle -> PASS-after.

## Capabilities

### New Capabilities
<!-- None — both 4c and 4d extend the existing neo-byref capability's Ref Slot
     / stind-ldind / ref-out-call-ABI requirements. -->

### Modified Capabilities
- `neo-byref`: adds the CLR-object-field `stind`/`ldind`/`Stobj`/`Ldobj`
  consumer path (4d) and the CLR-method `ref`/`out` typed-ref call-ABI bridge
  (4c) — both previously DEFERRED in this capability and in `neo-boxing`.

## Impact

- **`ILRuntime/CLR/Method/CLRMethod.cs`** — `Invoke(byte*)` param loop
  (`:407-474`): byref detection + deref read + post-call write-back. Neo-only
  path (the reflection `Invoke(byte*)` overload is Neo-specific; the Legacy
  `Invoke(intepreter, esp, mStack)` overload is byte-identical and untouched).
- **`ILRuntime/Runtime/CLRBinding/BindingGeneratorExtensions.cs`** —
  `AppendArgumentCodeNeo` (`:135-237`): replace the byref `default`+TODO arm
  with byref-aware codegen; new byref write-back epilogue helper. Neo-only
  (the Legacy `AppendArgumentCode` is the reference, untouched).
- **`ILRuntime/Runtime/CLRBinding/MethodBindingGenerator.cs`** —
  `GenerateMethodWraperCode_Neo` (`:249-310`): wire the byref write-back
  epilogue into the autogen wrapper after the CLR call. Neo-only.
- **`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`** — the
  `stind_*`/`ldind_*`/`Stobj`/`Ldobj` `else` arms (`:3142-3407`): add the
  CLR-object-field branch (resolve the field hash -> GetFieldValue/
  SetFieldValue). The `Ldflda` arm (`:827-885`) must stamp the CLR-field
  identity for a CLR operand (instead of the IL `field.PrimitiveOffset`). All
  Neo-only; Legacy `ExecuteR` is the reference.
- **`DelegateAdapter.cs`** (F-7, if in scope) — `NeoInvokeSub` /
  `WriteNeoCallSlot` byref-awareness. Neo-only.
- **`TestCases/NeoStep13bTest.cs`** (extend) — `NeoStep13_*` probes (4c) +
  `NeoStep17_*` probes (4d); the `NeoStep` filter catches them.
- **No capability spec moves beyond `neo-byref`**; a cross-reference note in
  `neo-boxing`'s deferral sentence is updated to point at this change (the
  sentence currently lists "the byref CLR crossing ... and CLR-object
  stind/ldind via field hash remain deferred" — this change closes both).
- **Regression risk: MEDIUM.** 4c touches the shared CLR-binding codegen (the
  `*Neo` variants only, but every Neo CLR method call flows through them); 4d
  is a runtime-arm addition gated on `mStack[objIdx]` not being an
  ILTypeInstance/Array (a discriminator branch, byte-identical for every
  existing path). Gate: full `NeoStep` smoke (161/161 baseline) + Legacy
  518/519 for any shared-engine edit. Adversarial probes MANDATORY (Step 17 B1
  / OPT-HARDEN K1 / F-MAJ-1 lessons: a green smoke does NOT prove a byref
  marshal correct).
