# Design -- neo-step17-generic-byref-etc

Per-sub-item dump-gate outcomes drive this design. 3 of 4 sub-items are no-ops
or out-of-scope; the single engine change is the F-10-R1 JIT-discriminator gate.

## Sub-item verdicts (Debug_Neo, HEAD `0aafdb34`, NeoStep 198/198)

| # | Sub-item | Probe outcome on HEAD | Verdict |
|---|----------|-----------------------|---------|
| 1 | generic-byref (`ref T`/`out T`, T generic) | 3/3 PASS (Swap int / IL-ref / IL-VT) | **NO-OP** -- TEST-ONLY guards |
| 2 | `fixed` unmanaged-pinning | FAIL `Conv_U not yet implemented` (Step 6) | **DEFER** -- out of byref scope |
| 3 | interface-on-VT-constrained beyond common | 2/2 PASS (IL-VT direct-call; CLR-VT box) | **NO-OP** -- TEST-ONLY guards |
| 4 | F-10-R1 JIT-discriminator gate | FAIL `NullReferenceException` at NeoMarshalByrefFieldToSlot:466 | **REAL GAP** -- JIT gate |

---

## 1. generic-byref -- NO-OP (type-agnostic byref model)

### Why it already works

A Neo byref is an 8-byte Ref Slot `(objectIndex:int, offset:int)`. The call ABI
copies it as a single 8-byte primitive entry regardless of the element type:
`AllocateSlotForType` sizes a `IsByRef` slot to 8 bytes / `RefCount == 0` on
BOTH the caller and callee sides, and `CopyNeoCallArguments` emits one 8-byte
`CopyBlock`. The generic-parameter type token is a CALL-SITE concern (the JIT
resolves `T` via generic substitution when emitting the method body), NOT a
byref-marshal concern. Inside `Swap<T>`, `T tmp = a; a = b; b = tmp;` lowers to
the correct `Move` (T=int/ref) or `Move_Vt` (T=IL-VT) from the resolved `T` --
the byref slots themselves carry no type.

The 4c typed-ref bridge (area4) keys the deref on the parameter's `IsByRef`
type token, which is true for both `ref int` and `ref T` (a generic-param `T`
marked `IsByRef` at the call site). No generic-param-specific discriminator is
needed.

### Fix site

NONE. The 3 probe guards graduate to keepers (`NeoStep17_GenericByRef_SwapInt` /
`_SwapIlRef` / `_SwapIlVt`), renamed off the `_Probe_` prefix.

---

## 2. `fixed` -- DEFER (blocked by `Conv_U`, out of byref scope)

### Dump evidence

`fixed (int* p = arr) { sum = p[0]+p[1]+p[2]; }` (array populated element-by-
element to avoid the initializer) FAILS on HEAD:
```
Neo: opcode Conv_U not yet implemented (Step 6)
```
The C# compiler lowers `fixed` to a pinned local + `conv.u` (`Conv_U`,
convert the pinned array ref to a native `int*`) + raw pointer indexing. `Conv_U`
/ `Conv_I` are unimplemented Step-6 opcodes; they are the pointer-conversion
primitives for the raw-pointer model, which is fundamentally different from the
Neo VM's managed Ref Slot.

(When the array uses an inline initializer `new int[]{...}`, Roslyn lowers it via
`RuntimeHelpers.InitializeArray` -> `ldtoken`, so the probe hits
`Ldtoken not yet implemented` first. Both `Ldtoken` and `Conv_U` are outside the
byref capability.)

### The address works; the `fixed` statement does not

TC14 `NeoStep17_ClrPrimitiveArrayLdelema_StindLdind` exercises the array-element
address via `ref arr[i]` (the C# `ref` form, which lowers to `ldelema` + the
byref-param ABI) and is GREEN. So `ldelema` + `stind`/`ldind` on a CLR primitive
array already work. The gap is specifically the `fixed` statement's pointer
conversion + raw-pointer dereference, which needs `Conv_U`/`Conv_I` (and
optionally a pinned-local model).

### Fix site

NONE (this change). Defer to a future pointer/`Conv_U` step. Record as an
accepted-known limitation in the ship log + the deferred-items doc. Do NOT
implement `Conv_U` here (out of `neo-byref` scope; a guessed pointer model risks
silent corruption).

---

## 3. interface-on-VT-constrained -- NO-OP (the {a,d,M2,b} cohorts cover it)

### Why it already works

The Constrained runtime arm (`ILIntepreter.Neo.cs:4074`) dispatches
`constrained.callvirt T.M` on a value type `T` via two paths:

- **Direct-call** (`actualMethod is ILMethod && thisObjIdx < 0 &&
  constrainedType is ILType && IsValueType`, line 4187): copies the struct's flat
  primitive bytes into callee slot-0; for an IL-VT interface impl whose override
  is an ILMethod, this is the path. An IL struct implementing `IFace { int M(); }`
  dispatched via a generic `T v where T:IFace { v.M(); }` resolves
  `GetVirtualMethod` to the ILMethod -> direct-call. PROBE PASSES.
- **Box-once** (the `else` branch, line 4237): boxes the struct (CLR-VT via
  `ReadNeoValueType`; IL-VT-inherited-CLRMethod via `Instantiate(false)` +
  `CopyFrameToIL`) and dispatches via `InvokeNeoClrMethod`/`InvokeNeoCallTarget`
  on the boxed receiver. A CLR struct implementing `IComparable<int>` dispatched
  via a generic `T v where T:IComparable<int>` boxes and calls `CompareTo` via
  reflection. PROBE PASSES.

The "beyond the common shape" the seed hypothesized (box + interface-map
dispatch) is already the box-once path's responsibility, and it works. The F1
round-1 fix (`neo-step17-completion`) handled the IL-VT-inherited-CLRMethod sub-
case; the (b) cohort (`neo-step17-stobj-refloop`) handled the IL-VT-with-ref-
fields sub-case. No reachable interface-on-VT-constrained shape still NIEs.

### Fix site

NONE. The 2 probe guards graduate to keepers (`NeoStep17_InterfaceOnIlVtConstrained`
/ `_InterfaceOnClrVtConstrained`).

---

## 4. F-10-R1 JIT-discriminator gate -- the engine change

### The defect (dump-confirmed)

An IL value type with a CLR-struct field:
```csharp
public struct ProbeIlVtWithClrField : IProbeSetAndSum {
    public int prefix;                  // IL-primitive (non-empty flat region)
    public TestVector3NoBinding field;  // CLR-struct field (F-10 marker)
    public int SetAndSumViaLdflda(float x, float y, float z) {
        TestCLRBinding.SetTestVector3NoBindingByRef(ref this.field, x, y, z);
        return TestCLRBinding.SumTestVector3NoBindingByRef(ref this.field);
    }
}
static int ProbeConstrainedCallSetAndSum<T>(T v, ...) where T : struct, IProbeSetAndSum
    => v.SetAndSumViaLdflda(...);   // constrained.callvirt T.SetAndSumViaLdflda
```

Trace on HEAD (FAILS, NRE at `NeoMarshalByrefFieldToSlot:466`):

1. `constrained.callvirt` resolves `actualMethod` to the ILMethod override ->
   direct-call path (line 4187). Slot-0 is seeded with the struct's FLAT
   PRIMITIVE bytes: just `prefix` (4 bytes). The CLR-struct field `field` is a
   reference slot (referenceOffset++, NO primitiveOffset advance -- the F-10
   layout), so it contributes ZERO flat bytes; its boxed value lives in the
   ref region (seeded by the constrained-slot-0-ref-seed hook).
2. Inside the body, `ldflda this.field`:
   - `operandSlotOff` = slot-0's frame byte offset.
   - `objIdx = *(frameBase + operandSlotOff + 0)` = `prefix` value (e.g. 7).
   - JIT stamped BOTH markers: F-6 (`0x1`, source is in-frame VT) AND F-10
     (`0x2`, field is a CLR-struct field of an ILType) -> `Operand4 = 0x3`.
     (`IsClrStructFieldOfIL` returns true for an IL value-type declaring type
     too -- it checks `declaringType is ILType`, not `!IsValueType`.)
   - Runtime Ldflda arm (ILIntepreter.Neo.cs:1118): the FIRST check is
     `clrStructFieldMarker && objIdx >= 0` -> `true && 7 >= 0` -> TRUE ->
     produces `(objIdx=7, ReferenceOffset | NeoF10ByrefOffsetFlag)`.
3. The byref `(7, refOff|flag)` flows to `SumTestVector3NoBindingByRef` via
   `CopyNeoCallArguments` -> `NeoMarshalByrefFieldToSlot(objIdx=7, off=refOff|flag)`.
4. `target = mStack[7]` (line 378) -- but the struct was NOT boxed (direct-call
   path), so mStack[7] is NOT the ILTypeInstance. It is null or an unrelated
   slot. `target is ILTypeInstance` -> false; `target is Array` -> false; falls
   through to the CLR-object branch -> `NeoReadClrObjectField(appdomain, null,
   off)` -> **NRE** at line 466.

### The fix -- JIT-discriminator gate (NOT a runtime reorder)

The F-6 and F-10 markers are OR-stamped by two separate JIT sites:
- F-6 (`0x1`): `TypeSpecializeNeoOpcodes case Ldflda:` (JITCompiler.cs:877),
  when the SOURCE OPERAND register is an in-frame IL value type.
- F-10 (`0x2`): the main-JIT `case Code.Ldflda:` body emission
  (JITCompiler.cs:2434), when `IsClrStructFieldOfIL(type, fieldType)` (the
  field's declaring type is an ILType, the field is a CLR value type).

The two conditions are ORTHOGONAL: an IL VALUE type can be the declaring type
of a CLR-struct field, and its source operand can be in-frame. So both stamp,
producing the ambiguous `Operand4 = 0x3`. The runtime's F-10-first check then
mis-fires when slot-0 holds flat bytes (the constrained-direct-call shape).

**The fix: make the two markers mutually-exclusive at the PRODUCER.** In
`TypeSpecializeNeoOpcodes case Ldflda:` (the F-6 stamping site, JITCompiler.cs:862-
880), when the F-6 condition fires (`srcType is ILType srcIl && srcIl.IsValueType
&& !srcIl.IsEnum`), CLEAR any F-10 marker the main-JIT body emission set:

```csharp
case OpCodeREnum.Ldflda:
{
    IType srcType = GetRegisterType(registerTypes, op.Register2);
    if (srcType is ILType srcIl && srcIl.IsValueType && !srcIl.IsEnum)
    {
        SetRegisterType(registerTypes, op.Register1, srcType);
        op.Operand4 |= NeoLdfldaInlineMarker;
        // F-10-R1: the F-6 (in-frame-VT) and F-10 (CLR-struct-field-of-IL)
        // markers are NOT mutually-exclusive at the main-JIT body emission
        // (IsClrStructFieldOfIL returns true for an IL value-type declaring
        // type too). An in-frame-VT source MUST route through the F-6 runtime
        // branch (shape 1/2/3), NEVER the F-10 heap-ManagedObjects branch.
        // Clear F-10 here (the type-spec pass runs AFTER body emission, so
        // the body's F-10 stamp is already on Operand4). This makes the both-
        // stamp shape (Operand4 = 0x3) impossible -- the runtime Ldflda arm's
        // F-10-first check can no longer mis-fire on flat bytes.
        op.Operand4 &= ~NeoLdfldaClrStructFieldMarker;
    }
}
break;
```

### Why the type-spec-pass gate (not `!type.IsValueType`)

A naive gate at the F-10 stamping site (JITCompiler.cs:2433)
`if (IsClrStructFieldOfIL(type, fieldType) && !type.IsValueType)` was applied
during the propose-phase dump-gate and DID make the probe pass. But it is
SUBTLY WRONG for the **boxed-IL-VT-with-CLR-struct-field** case: a boxed IL
value type (operand is a heap mStack object from a `box` opcode) whose CLR-struct
field is accessed via `ldflda` has `type.IsValueType == true` (the declaring
type is a value type), so `!type.IsValueType` would suppress F-10 -- but the
operand is a HEAP boxed object that correctly needs the F-10 path
(`ManagedObjects[ReferenceOffset]`). The declaring-type gate would silently
break that shape.

The **type-spec-pass gate** keys on the OPERAND's value-category (in-frame VT
vs heap/boxed), which is exactly the F-6 condition. It clears F-10 ONLY when
the operand is genuinely in-frame (F-6 stamped):
- in-frame IL VT source -> F-6 stamped -> F-10 cleared -> F-6 shape 1/2/3 fires.
- heap IL class source -> F-6 NOT stamped -> F-10 stays -> F-10 path fires (the
  existing 8 NeoClrStructField probes -- unchanged).
- boxed IL VT source -> F-6 NOT stamped (operand is a heap mStack object, not an
  in-frame VT) -> F-10 stays -> F-10 path fires (correct for the boxed-VT case).

So the type-spec-pass gate is correct for ALL three operand shapes.

### Dump-gate proof (the concept is validated)

During propose, the `!type.IsValueType` form of the gate (which suppresses F-10
for the same in-frame-VT probe shape) was applied temporarily and the probe
PASSED. This validates that suppressing F-10 for the in-frame-VT case routes
the runtime to F-6 shape-3 `(-1, operandSlotOff + fieldPrimOff)`, which produces
a CORRECT byref for this shape (the constrained-slot-0-ref-seed + the F-6 byref +
the byref consumer converge correctly -- the probe returns 60, the right sum).
The implementer applies the type-spec-pass gate (the principled form) and re-
verifies with the same probe.

### Runtime arm -- NO CHANGE

The runtime Ldflda arm (ILIntepreter.Neo.cs:1112-1170) is UNCHANGED. The F-10-
first check order stays as shipped:
```
if (clrStructFieldMarker && objIdx >= 0) { ... F-10 branch ... }
else if (objIdx == -1) { ... shape 1/2 frame-native ... }
else if (inlineMarker) { ... shape 3 flat-bytes ... }
else { ... heap/CLR ... }
```
With the JIT gate, `clrStructFieldMarker` is never true together with
`inlineMarker`, so the F-10-first check is safe: for an in-frame-VT operand,
`clrStructFieldMarker` is false -> the F-10 branch never fires -> routes to
shape 1/2/3. The reviewer's recommended runtime F-6-before-F-10 reorder is NOT
applied (it was DISPROVEN: it broke 6 NeoStep17 F-6-only probes, 190->184,
because shape 3 vs shape 1/2 produce different byrefs for the `objIdx == -1`
case that every reachable VT `this`/arg uses today).

### Shared-vs-Neo gating + Legacy neutrality

The change is entirely within `TypeSpecializeNeoOpcodes` (JITCompiler.cs), which
is `#if ENABLE_NEO_MODE` (file/run-gated). The Legacy JIT (`ILIntepreter.Register.cs`
`ExecuteR` + the Legacy JIT path) is the semantic reference and is NOT modified.
The shared passes (FCP/BCP/copy-prop/RegisterCleanup) are untouched. The CLR
binding generator is untouched. The runtime arms are untouched. Legacy-neutral
BY CONSTRUCTION; the apply phase confirms with the Step 17 Legacy filter.

### Stfld_Ref / Ldfld_Ref consistency (apply-phase verification)

The F-10 hash is also stamped on `Stfld_Ref`/`Ldfld_Ref` (JITCompiler.cs:2406,
2459) for the heap box/unbox path (`Operand4 = fieldType.GetHashCode()`). For an
in-frame-VT operand, the type-spec pass rewrites `Stfld`/`Ldfld` to the `_Inline`
variant (`Stfld_*_Inline`/`Ldfld_*_Inline`), whose runtime arm does NOT consult
`Operand4` -- so the F-10 hash is never consumed for the in-frame-VT case. No
probe exercises an in-frame-VT `Stfld_Ref`/`Ldfld_Ref` that reads the F-10 hash
today. The apply phase SHALL verify via JIT dump that no such path exists; if
one is found, the same type-spec-pass gate is extended to clear the F-10 hash on
the in-frame-VT Stfld/Ldfld rewrite (but this is not expected -- the `_Inline`
rewrite is the type-spec pass's own decision, so it already controls those
opcodes).

---

## Summary of artifacts shipped

- **Engine (Neo-only):** 1 JIT pass edit (`TypeSpecializeNeoOpcodes case Ldflda:`
  clears F-10 when F-6 stamped, ~2 lines).
- **Tests (TestCases/NeoStep17Test.cs):** 6 keepers total:
  - 3 generic-byref guards (PASS on HEAD -- TEST-ONLY).
  - 2 interface-on-VT-constrained guards (PASS on HEAD -- TEST-ONLY).
  - 1 F-10-R1 adversarial keeper (FAIL-on-HEAD -> PASS-after).
  - The 2 `fixed` probes are RETAINED as documentation of the deferred gap
    (they stay FAIL-on-HEAD via `Conv_U`; marked `[JsonIgnore]`-style or
    excluded from the smoke filter -- the apply phase decides: either keep them
    as explicit "expected-fail" documentation or drop them. Preferred: DROP from
    the keeper set and record the `fixed`/`Conv_U` blocker in the ship log +
    deferred-items doc, so the smoke stays green).
- **Docs:** `neo-deferred-items.md` D-CONSTRAINED (c) row updated ((c) generic-
  byref + interface-on-VT CLOSED as no-ops; `fixed` rerouted to a `Conv_U`
  step); F-10-R1 §3 entry -> RESOLVED.

## Verification plan (apply phase)

1. Apply the type-spec-pass gate. Rebuild CLI (`Debug_Neo`).
2. Run the F-10-R1 probe: FAIL-on-HEAD -> PASS-after (load-bearing).
3. Run NeoClrStructField (8/8 must stay green -- heap-IL F-10 path unchanged).
4. Run full NeoStep smoke: 198 baseline + 5 new PASSing keepers = 203 green
   (the 2 `fixed` probes dropped or marked expected-fail).
5. Run NeoStep20 9/9 + NeoOptHard 24/24 (F-10 is load-bearing for Step 20).
6. Run the Step 17 Legacy filter (Legacy-neutral confirmation).
7. Stash-toggle: revert the gate -> F-10-R1 probe NREs again; the 6 F-6-only
   probes are unaffected (the gate touches only F-10, not F-6).
