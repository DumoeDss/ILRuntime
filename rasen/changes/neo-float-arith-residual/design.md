# neo-float-arith-residual -- design

Wave-2 child of `neo-overhaul`. Target: `InheritanceTest07` / `InheritanceTest16`
fail under Neo with a garbage denormal (`3E-45`) for `arg1 + 1.2f` and
`arg1 * 12333f`. After the `neo-typed-opcode-reach-executer` fix these methods
RUN in Neo (the route through `InvocationContext.Invoke -> ExecuteR` was
replaced by `InvokeNeo -> Run -> ExecuteNeo`), but their float RETURN was
silently corrupted. The surfacing note (see
`rasen/changes/neo-typed-opcode-reach-executer/design.md`) framed this as "a
DISTINCT float-producer/arithmetic bug, NOT the InvocationContext marshalling."
That framing was DISPROVEN by the JIT-dump + runtime-instrument re-audit below:
the arithmetic is correct end-to-end; the corruption is in the RETURN
read-back at the `InvocationContext` boundary.

## The bug, pinned by instrumentation (NOT a JIT/arith issue)

The surfacing note's hypotheses ("the addi.r4/muli.r4 immediate-operand runtime
arm", "a JIT tag/stamp issue", "some OTHER float producer unseeded") are ALL
WRONG. The re-audit proved the body arithmetic is correct.

`TestCls5.AbMethod2(int arg1) { return arg1 + 1.2f; }` JIT-folds to:
```
0: conv.r4 r3, r1      ; (float)arg1  [arg1 = int param, r1]
1: addi.r4 r2, r3, 1.2 ; r2 = r3 + 1.2f  (ELDC fold of ldc.r4 1.2; add)
2: br.s 3
3: ret r2
```
(`arg1 * 12333f` in InheritanceTest16 folds to `muli.r4` -- identical shape.)

Temporary diagnostics in the runtime arms captured GROUND TRUTH (arg1 = 11):
```
Conv_R4   DstOff=12 SrcOff=4 tag=0(I4) srcInt=11            res=11        ; r3 = 11.0f  -- CORRECT
Addi_R4   DstOff=8  SrcOff=12 srcFloat=11 operandFloat=1.2   res=12.2      ; r2 = 12.2f  -- CORRECT
Ret       DstOff=8  retPrimSize=4 srcFloat=12.2 retDstFloat=12.2           ; retDst = 12.2f -- CORRECT
Run-read  retIsVal retSize=4 retDstFloat=12.2 retTypeCLR=System.Single result=12.2 ; Run returns 12.2f -- CORRECT
```
So `conv.r4`, `addi.r4`, `ret`, and `Run`'s `NeoBoxReturnValue` are ALL correct:
the boxed `12.2f` leaves `intp.Run(method, instance, args)` faithfully. The
typed specialization (`Addi_R4`/`Muli_R4`) fires (child-16/21 seeding holds for
this shape -- `conv.r4` dest is `FloatType`, the immediate `OperandFloat`
survives `LowerNeoOffsets` because `LowerR1R2` touches only offsets 4/6).

The corruption happens ONE step further, in `InvocationContext.InvokeNeo`:
```csharp
object result = intp.Run(method, instance, args);          // 12.2f (boxed)
StackObject* newEsp = ILIntepreter.PushObject(ebp, mStack, result, true);  // <-- BUG
esp = newEsp - 1;
```

`PushObject(..., isBox:true)` stores EVERY value (including primitives) as
`ObjectType = Object` + `Value = mStack.Count` (the mStack index of the boxed
value). The cross-binding caller reads the return via
`CrossBindingFunctionInfo<int,float>.Invoke -> ctx.ReadResult<float>()`, which
dispatches to `ReadFloat()`:
```csharp
public float ReadFloat() { return *(float*)&esp->Value; }   // reads esp->Value INLINE
```
`ReadFloat` reinterprets `esp->Value` (the mStack INDEX, e.g. `3`) as a float
--> a garbage denormal. `3E-45` == the IEEE bits of the int `3` (the mStack
index). Airtight.

## Why the Legacy arm does not hit this

The Legacy `ExecuteR` arm does NOT re-enter through `Run`; it runs the method
body directly and the body's `ret` writes the return INLINE onto the
StackObject stack as a typed slot (`ObjectType = Float`, `Value = float bits`).
`ReadFloat` then reads `*(float*)&Value` = the float. The `InvokeNeo` path
introduced `PushObject(isBox:true)`, which violates the inline-primitive
contract that the typed readers (`ReadFloat`/`ReadInteger`/`ReadLong`/
`ReadDouble`) depend on.

## Why ReflectionTest09/11/23 (the reflection route) did NOT hit this

The two re-entry routes through `InvocationContext.Invoke` read the return with
DIFFERENT readers, and only one is vulnerable:

- **Reflection route** (`ILRuntimePropertyInfo.GetValue` / `ILRuntimeMethodInfo
  .Invoke` callers): reads via `ctx.ReadObject<T>()` -> `StackObject.ToObject(
  esp, ...)`, which DEREFERENCES the slot: for an `Object` slot it returns
  `mStack[Value]` (the boxed value). So `isBox:true` (Object slot + mStack idx)
  is faithfully recovered by `ReadObject`. ReflectionTest09/11/23 (the
  `neo-typed-opcode-reach-executer` reflection fixes) PASS for ANY return type
  under `isBox:true`. The surfacing note's "ReflectionTest11 proven sound"
  observation is real but was mis-attributed to "arg marshalling" -- it is sound
  because the reflection route reads via `ToObject`, not because the path is
  free of this defect.
- **Cross-binding route** (`CrossBindingFunctionInfo`/`CrossBindingMethodInfo`
  for inheritance overrides): reads via `ctx.ReadResult<TResult>()`, which
  dispatches to the TYPED readers (`ReadFloat`/`ReadInteger`/`ReadLong`/
  `ReadDouble`) per the return type. These read `*(T*)&esp->Value` INLINE and
  are broken by `isBox:true`. InheritanceTest07/16 (`ReadFloat`) hit it.

The fix (`isBox:false`) is correct for BOTH routes: primitives go inline (typed
readers work) and `ToObject` of an inline primitive slot returns the boxed
primitive (so `ReadObject` still works -- `StackObject.ToObject` handles
`Float`/`Long`/`Integer` slots). References/VTs are pushed as Object slots
identically to `isBox:true`. So the reflection route stays green and the
cross-binding route is fixed.

## The fix (Neo-gated, 1 line + comment, Legacy-neutral)

In `InvocationContext.InvokeNeo`, change `PushObject(ebp, mStack, result, true)`
to `PushObject(ebp, mStack, result, false)`.

`isBox:false` routes primitives through `UnboxObject`, which writes them INLINE
(`float`: `ObjectType = Float; *(float*)&Value = (float)obj` -- `ILIntepreter.cs:
6193-6197`; `long`: `ObjectType = Long; *(long*)&Value = obj`), matching the
Legacy arm exactly so the typed readers work. References and value types are
pushed as `Object` slots IDENTICALLY to `isBox=true` (`PushObject`'s `!isBox`
branch only diverges for primitives/enums), so the F-12 reference-return
contract and value-type returns are byte-identical to before. Enum returns are
pushed as `Integer` slots (more precise than `Object`; read correctly by
`ReadInteger`). Null reference returns go through `PushNull` (returns `esp+1`,
preserving the `esp = newEsp - 1` invariant). The entire change is inside
`#if ENABLE_NEO_MODE` -> Legacy compiles none of it (Legacy-neutral by
construction).

## Why this is NOT the framed "unseeded float producer" class

child-16/21/23 closed the unseeded-primitive-PRODUCER class (the body-side
typed-arithmetic specialization). This bug is a CONSUMER-side read-back defect
at the CLR<->IL InvocationContext boundary -- a completely different layer
(`InvocationContext.cs`, not `JITCompiler.cs`/`ILIntepreter.Neo.cs`). The
diagnostics definitively show the body produces 12.2f and `Run` returns 12.2f;
the loss is solely in the `PushObject(isBox=true)` step. No seeding, JIT, or
runtime arithmetic change is needed or correct here.

## Verify (truth = full-smoke count, REAL run; same TestCases.dll + patch)

- Baseline (pristine HEAD `5358b89c`, no fix): **65 failed** (the
  `neo-typed-opcode-reach-executer` end state). InheritanceTest07/16 fail with
  `3E-45` garbage.
- After fix: **65 -> N** (recorded in tasks.md; InheritanceTest07 + InheritanceTest16
  flip to PASS). Stash-toggle: stash `InvocationContext.cs` ONLY ->
  InheritanceTest07 FAILS (`3E-45 != 12.1f`); pop -> PASS. Airtight.
- NeoStep smoke: 0 failures (no regression).
- Legacy-neutral: plain `Debug` + `useRegister=true` is byte-identical (the
  change is `#if ENABLE_NEO_MODE`-gated).

## Out of scope

The three hybrid-deferred shapes (`neo-invocationctx-byref` / `-ctor` /
`-valuetype`) remain on the ExecuteR fallback (pre-existing behavior; none
block a test). The broader full-smoke NIE/opcode surface is the parent
`neo-overhaul`, not this child.

## Files

- `ILRuntime/Runtime/Enviorment/InvocationContext.cs` (+15 / -1, all inside
  `#if ENABLE_NEO_MODE` in `InvokeNeo`): `isBox:true` -> `isBox:false` + the
  explanatory comment. No other runtime/JIT/object-model/binding change.
