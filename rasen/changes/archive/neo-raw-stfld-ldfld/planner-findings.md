# Planner Findings — neo-raw-stfld-ldfld (instrumented full-Neo smoke, 2026-07-11)

## Confirmed escaping shape (the root cause)
Raw `Stfld`/`Ldfld` reach the ExecuteNeo Step-6 default because the **declaring type
(`type` returned by `AppDomain.GetFieldOffset`) is a CLRType, not an ILType**.

`JITCompiler.cs`, `case Code.Ldfld` (line ~2783) and `case Code.Stfld` (line ~2854),
Neo branch:
- `if (type is ILType)` -> rewrites `op.Code = GetLdfldCodeForType(fieldType)` /
  `GetStfldCodeForType(fieldType)` (a typed arm: `Ldfld_I4`/`Ldfld_Ref`/`Ldfld_Value`/
  `Stfld_*`). These ARE handled by ExecuteNeo + the offset-lowering pass.
- `else` (CLR declaring type, lines ~2812 / ~2882) -> **leaves `op.Code` as the raw
  `OpCodeREnum.Ldfld`/`Stfld`** and only stamps
  `op.OperandLong = ((long)type.GetHashCode() << 32) | (uint)offset.PrimitiveOffset`.

For a CLR type, `AppDomain.GetFieldOffset` returns
`PrimitiveOffset = type.GetFieldIndex(token)` (AppDomain.cs:2246) = the CLR FieldInfo hash.
So `OperandLong = (typeHash << 32) | fieldHash` — **identical to Legacy's raw
`Ldfld`/`Stfld` encoding** (`ILIntepreter.Register.cs:3122/3227`:
`type = AppDomain.GetType((int)(OperandLong >> 32))`, `fieldToken = (int)OperandLong`,
`CLRType.GetField(fieldToken)`).

## Why it reaches the default (TWO gaps, not one)
1. **ExecuteNeo has NO `case OpCodeREnum.Ldfld:` / `case Stfld:`** (only the typed
   `_I1.._Ref/_Value` arms + `ldfld.value`/`stfld.value`). Confirmed by grep.
2. **The offset-lowering pass has no case for raw `Ldfld`/`Stfld`** either
   (`Optimizer.Neo.cs` ~934 (Ldfld typed list) and ~960 (Stfld typed list) enumerate
   ONLY the typed arms). So at runtime the raw opcodes have valid `Register1`/`Register2`
   (set by the JIT) but **unset/zero `DstOffset`/`SrcOffset`**. The JIT sets
   `op.Register1/Register2` for these (Ldfld: R1=R2=baseRegIdx-1; Stfld: R1=baseRegIdx-2,
   R2=baseRegIdx-1), but `DstOffset`/`SrcOffset` are normally stamped by the lowering pass.

Legacy ExecuteR DOES handle raw `Stfld` (line 3093) and `Ldfld` (line 3197) — that is the
parity reference. (Legacy keeps DstOffset/SrcOffset-equivalent via register indices
directly; Neo needs the lowering pass.)

## Owner representation at runtime (instrumented dump, the load-bearing detail)
The owner register slot holds a **Neo object handle** whose shape varies (this mirrors the
Neo Ref-Slot / byref model used by `ldind`/`stind`/`ldobj`/`stobj` — Step 13 Area 4d):

- **Heap object:** `*(int*)(frameBase + ownerOff) = objIdx >= 0`, and `mStack[objIdx]` is
  the boxed object. Observed kinds:
  - CLR reference type: `TestCLRAttribute` (Name/String), `TestCLRBinding` (missingField/Int32),
    `TestVectorClass` (vector/TestVector3 field), `BindableProperty<long>` (OnChangeWithOldVal delegate).
  - **ILTypeInstance whose CLR BASE declares the field**: `TestCls..ctor` sets
    `ClassInheritanceTest.testVal`; `mStack[objIdx] = ILTypeInstance`. (IL type inheriting a
    CLR base class — the field resolves to the CLR base via GetFieldOffset.)
  - **Array element**: `UnitTest_10033/10047` owner = `mStack[4] = TestVector3[]` (a `stfld`
    on `arr[i]` where the array is in mStack).
- **Frame-native byref (CLR struct local/field-address):** `*(int*)(frameBase + ownerOff) = -1`
  and `*(int*)(frameBase + ownerOff + 4) = absoluteFrameByteOffset`. Observed for
  `TestVector3`(.X/.Y/.Z), `TestVector3NoBinding`(.x/.y), `TestVectorStruct`(.A),
  `TestStruct`(.value), `TestStructA`(.value). These are the deferred
  "Step 17/13b field access on a CLR object" cases.

This `(objIdx, offset)` pair is EXACTLY the Neo Ref-Slot format the implemented `Ldflda` arm
emits (`ILIntepreter.Neo.cs:1762-1788`) and the byref consumers resolve.

## Existing reuse points (Neo helpers the handler should build on)
- **Step 13 Area 4d** (`ILIntepreter.Neo.cs:5784-5820`): `NeoReadClrObjectField(appdomain,
  target, fieldHash)` -> `CLRType.GetFieldValue`; `NeoWriteClrObjectField(...)` ->
  `CLRType.SetFieldValue`; `NeoIsClrObject(mStack, objIdx)` discriminator. These cover the
  **heap-CLR-object** sub-case directly. (`Ldflda` arm comment @ 1799-1813 documents that
  for a CLR object the field-hash routes through these.)
- Neo value-type flat-bytes helpers: `ILIntepreter.ReadNeoValueType` /
  `WriteNeoValueType` / `Optimizer.GetNeoValueTypeManagedSize` (used by the F-10
  `Ldfld_Ref`/`Stfld_Ref` arms at 3660-3750).
- Legacy parity: `ILIntepreter.Register.cs` Stfld 3093-3196 (note the
  `ValueTypeObjectReference` split at 3097 + the CLR-object branch 3119-3190) and Ldfld
  3197-3253 (split at 3201 + CLR branch 3224-3243).

## Frequency (full smoke, pre-crash, this run)
27 raw-field hits captured (deduped to ~22 distinct method/field sites): Stfld dominates
(struct field writes in value-type-binding tests + TestCls..ctor x5). Ldfld is the
struct/class field reads. Matches the planning-context table (Stfld 36 / Ldfld 18
pre-crash).

## Baseline / regression
- NeoStep smoke AFTER removing all instrumentation: **314/0/0** (no regression). Build 0 errors.
- `git status`: ILIntepreter.Neo.cs / JITCompiler.cs / Optimizer.Neo.cs all CLEAN
  (instrumentation fully removed; working tree restored).
