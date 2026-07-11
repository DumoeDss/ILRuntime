# Tasks -- implement-neo-step13b

Scope: **Area 5 core only** (unified CLRMethod param layout + remove the
caller-temp-slot fallback + ReadNeo*-by-width). Area 4, CLR ref/out, and
CLR-object stind/ldind are DEFERRED (see proposal.md).

All runtime changes are `#if ENABLE_NEO_MODE`, additive / replacing NIE throws
and a temporary fallback. Build: CLI `dotnet build ILRuntimeTestCLI/
ILRuntimeTestCLI.csproj -c Debug_Neo` -> 0 errors; TestCases `dotnet build
TestCases/TestCases.csproj -c Debug` (NEVER Debug_Neo). Smoke (regression --
EVERY CLR call uses this ABI):
`dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep`
-> all-green (was 81/81; new NeoStep13b cases add, ZERO existing regressions,
CLR-binding tests especially). `-f net8.0`. Test >10s = infinite loop -- kill.

## Phase 0 -- Baseline (no code change)

- [x] 0.1 Confirm `Debug_Neo` CLI builds 0 errors and the FULL NeoStep smoke is
      green (81/81) on the pre-change HEAD. Record the count.
- [x] 0.2 Confirm the K2 reproducer FAILS on this HEAD before any change: a
      CLR struct (host assembly, e.g. `TestFramework.TestVector3NoBinding`)
      passed by value to a CLR method throws
      `ArgumentOutOfRangeException` / `NotImplementedException`. (This is the
      before-state evidence for the K2/K2-FAM closure.)

## Phase 1 -- Unified callee param layout (D1)

- [x] 1.1 In `Optimizer.Neo.cs` CLRMethod call-lowering param loop (~`:1163-
      1186`), DELETE the `if (paramType.IsValueType && !IsPrimitive && !ILType
      && !IsEnum)` caller-temp-slot fallback branch; route ALL params through
      `AllocateNeoCallParamSlot` (including CLR structs).
- [x] 1.2 Build CLI `Debug_Neo` -> 0 errors.
- [x] 1.3 Run FULL NeoStep smoke. EXPECTED: no regression on primitives/enums/
      IL-VTs/refs (those already used the helper); CLR-struct-param cases that
      previously "worked by luck" via the fallback may change -- investigate
      each. If a smoke case regresses for a reason OTHER than the missing
      boxed-ref bridge (Phase 3), do NOT reintroduce the fallback -- fix the
      root cause. (DEVIATION recorded: the `IsValueType` branch of
      `AllocateNeoCallParamSlot` called `GetPrimitiveSize`, which only knows the
      primitive singletons and threw NIE for any CLR struct -- the callee layout
      was NEVER actually correct for CLR structs (Finding P was optimistic); the
      fallback masked it AND masked the async-prewarm crash. Fixed by sizing CLR
      structs via the new `GetNeoValueTypeManagedSize` (Unsafe.SizeOf<T>,
      non-throwing). Smoke: 81/81 unchanged after Phase 1.)

## Phase 2 -- Reflection fallback + helpers (D2, D4, D6)

- [x] 2.1 Add `ReadNeoValueType(Type clr, byte* frameBase, ref int curPrim,
      int sz)` and `WriteNeoValueType(object value, byte* dst, int sz)` next to
      the `ReadNeo*` family (`ILIntepreter.Neo.cs:26-127`). Pure-primitive
      structs via a typed read; mirror `NeoWritePrimitiveToFrame` (:3213).
      (IMPLEMENTED via cached DynamicMethod delegates calling
      `Unsafe.ReadUnaligned<T>`/`WriteUnaligned<T>` -- `byte*` cannot be a
      generic type arg, so custom `NeoVtReaderDelegate`/`NeoVtWriterDelegate`
      delegate types are used.)
- [x] 2.2 In `CLRMethod.Invoke(byte*)` (`CLRMethod.cs:332`), replace the
      `Step 13` NIE (`:362-365`) with: binder present -> binder read;
      pure-primitive -> `ReadNeoValueType`; struct-with-refs-no-binder ->
      Step-13b-tagged NIE.
- [x] 2.3 In `InvokeNeoClrMethod` (`ILIntepreter.Neo.cs:191-194`), replace the
      CLR-struct-return NIE with `WriteNeoValueType(res, retDstPtr, sz)`
      (+ ref slots via the binder when present).
- [x] 2.4 Build CLI `Debug_Neo` -> 0 errors. Run FULL NeoStep smoke. (84/84 --
      81 baseline + 3 new NeoStep13b cases, ZERO regression; the CLR-binding
      canary `ValidateNeoSmallPrimitiveArgs` still green.)

## Phase 3 -- K2-FAM boxed-ref-local -> flat-bytes-param bridge (D3)

DEFERRED per the design D3 safety valve (below) and task 3.3. Key implementer
finding: a CLR struct LOCAL obtained from a CLR method RETURN is stored as FLAT
BYTES (the D6 return path writes via `WriteNeoValueType`), NOT as a boxed-ref --
so passing such a local by value already works WITHOUT a bridge (verified by
`NeoStep13bClrStructByValueParamNoBinding`, which passes). The boxed-ref
representation only arises from the Box / Initobj paths. A clean D3 reproducer
(a boxed-ref CLR struct local passed by value, with a verifiable result) needs
`ldfld`/`stfld` on CLR struct fields, which is a DEFERRED concern -- so the
bridge has no clean test surface this step. Left as a documented deferred item
(silent wrong-result for that specific Box/Initobj-source shape, like the
value-type-`this` TODO), NOT a silent regression of a previously-green case.

- [ ] ~~3.1 Add the unbox bridge~~ -- DEFERRED (D3 safety valve; no clean test
      surface without CLR-struct field access).
- [ ] ~~3.2 Add a K2-FAM reproducer test~~ -- DEFERRED (blocked by deferred
      `ldfld`/`stfld` on CLR struct fields). The flat-bytes-source K2 case IS
      covered by `NeoStep13bClrStructByValueParamNoBinding`.
- [x] 3.3 Decision: split per safety valve -- Phases 1-2-4 land (close K2 for
      the flat-bytes-source case + return path); Phase 3 (boxed-ref-local /
      K2-FAM half) is DEFERRED. spec / design / proposal / planning-context
      updated to reflect the split.

## Phase 4 -- Autogen binding codegen consistency (D5)

- [x] 4.1 In `AppendArgumentCodeNeo` (`BindingGeneratorExtensions.cs:107`),
      replace the two CLR-struct TODO branches (`:123-127` binder, `:130-134`
      no-binder) with: pure-primitive -> `ReadNeoValueType` (binder AND no-
      binder, since the test structs are pure-primitive and the Neo-cursor
      binder ref-mapping API does not exist yet); struct-with-refs ->
      Step-13b-tagged NIE throw (binder-with-refs AND no-binder-with-refs).
      ByRef branch kept as a clearly-tagged DEFERRED TODO (CLR ref/out).
- [x] 4.2 In `GetReturnValueCodeNeo` (`:458-461`), replace the CLR-struct
      return TODO with `WriteNeoValueType(result_of_this_method, __retDst, sz)`
      (+ Step-13b NIE for struct-with-refs-no-binder).
- [x] 4.3 Build CLI `Debug_Neo` -> 0 errors. Run FULL NeoStep smoke (the
      CLR-binding tests are the canary for the generated path). (84/84; the
      `ValidateNeoSmallPrimitiveArgs` generated Neo redirect still green.)
      NOTE: the autogen CLR-struct codegen is verified by compile + the shared
      `ReadNeoValueType`/`WriteNeoValueType` (tested via the reflection path in
      Phase 2) + code inspection; no committed generated redirect exercises a
      CLR-struct param this step (regenerating the binding file is out of scope).

## Phase 5 -- Tests + final regression (TestCases/NeoStep13bTest.cs, ASCII)

- [x] 5.1 Add `TestCases/NeoStep13bTest.cs`: CLR struct by-value param
      (no-binder pure-primitive, `TestVector3NoBinding`) round-trip via host
      helpers; CLR struct return value round-trip; per-call param-region
      independence. The struct arg is obtained from a CLR method RETURN (host
      C# constructs it -- avoids the unimplemented `push` value-type-`this` ctor
      opcode); the result is checked by re-feeding to a CLR method returning a
      PRIMITIVE (avoids deferred `ldfld` on CLR struct fields). Added host
      helpers `SumTestVector3NoBindingFields` / `MakeTestVector3NoBinding` /
      `SumTestVector3Fields` to `TestCLRBinding` (TestClass3.cs).
- [x] 5.2 Build TestCases `Debug` + CLI `Debug_Neo`. Run FULL NeoStep smoke ->
      all-green (84/84 = 81/81 baseline + 3 new, ZERO regressions; CLR-binding
      tests unchanged).
- [x] 5.3 Update `design.md` / `tasks.md` with any deviation from this plan
      (the Phase 3 split decision + the `GetNeoValueTypeManagedSize` sizing
      helper deviation). Confirm DEFERRED items (Area 4, CLR ref/out, CLR
      stind/ldind field hash) still throw clearly-tagged NIEs.

## DEFERRED (explicitly out of scope this pass)

- [ ] ~~Area 4: `Unsafe.Unbox<T>` value-type instance `this` direct-call +
      eliminate `WriteBackInstance`~~ -- DEFERRED (the Neo wrapper does not
      emit `WriteBackInstance` today; the value-type `this` is a `// TODO` at
      `MethodBindingGenerator.cs:261`). Dedicated follow-up.
- [ ] ~~CLR-method `ref`/`out` (byref Ref Slot -> CLR `ref T`)~~ -- DEFERRED
      (separate typed-reference bridge into the frame; IL-method byref from
      Step 17 stays green).
- [ ] ~~CLR-object `stind`/`ldind`/`stobj`/`ldobj` via field hash~~ --
      DEFERRED (needs `Ldflda` field-hash stamping + dispatch to
      `CLRType.GetFieldValue`/`SetFieldValue`; independent plumbing).
