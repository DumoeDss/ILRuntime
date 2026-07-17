# neo-stfld-ref-generics -- ship log

Wave-2 child of `neo-overhaul` (branch `features/object-model-overhaul`).
Target: `Test01.UnitTest_Generics` + `Test01.UnitTest_Generics2` (stfld.ref NRE
on the self-referential generic singleton `SingletonTest : Singleton<SingletonTest>`).

## Result
- **Full-smoke delta: 27 -> 24** (3 tests fixed -- EXCEEDS the 2-test target).
  - `Test01.UnitTest_Generics` -- FIXED (target)
  - `Test01.UnitTest_Generics2` -- FIXED (target)
  - `ReflectionTest06` -- FIXED (bonus; Cluster B in ground-28, same root)
- NeoStep regression: **401 / 0 failures** (no regression).
- Legacy-neutral: plain `Debug` build = 0 errors (fix is `#if ENABLE_NEO_MODE`-gated;
  `Ceq_Ref` is a Neo-only opcode). Legacy was always passing these tests.
- Fresh full smoke: `Ran 935 tests, 24 failed, 20 ignored, 7 todos`.

## Pinned root (the "Inst owner resolves null" was a SYMPTOM, not the root)
The reported site -- `stfld.ref` NRE in `SingletonTest.set_Test` because the owner
(`this`, the value returned by `Inst`) was null -- is the symptom. The owner was null
because `Singleton<T>.get_Inst`'s lazy initializer `if (_inst == null) { _inst = new T(); }`
NEVER ran: `_inst` was read back null on every call.

### Why the initializer was skipped -- INLINED Ceq_Ref + mis-typed Brfalse
1. `get_Inst` is small, so the Neo inliner splices it into each caller
   (`UnitTest_Generics`, `UnitTest_Generics2`). The inliner copies the callee's
   POST-`TypeSpecializeNeoOpcodes` `BodyRegister` -- which has the **already-
   specialized `Ceq_Ref`** baked in (NOT a plain `Ceq`).
2. `TypeSpecializeNeoOpcodes` then re-runs on the caller's combined body. The
   plain-`Ceq` case (JITCompiler.cs:1088) seeds its dest register to `IntType`
   (the real 0/1 result of a compare). But there was **NO case for `Ceq_Ref`** --
   so the inlined `Ceq_Ref`'s dest register kept a STALE reference type from the
   preceding `Ldsfeld _inst` + `Box SingletonTest`.
3. The `Brtrue/Brfalse -> _Ref` rewrite (JITCompiler.cs:1623) keys on the
   condition register's tracked type. Seeing the stale reference type, it
   mis-classified the ceq's int32 result as a reference and emitted `Brfalse_Ref`.
4. At runtime, `Ceq_Ref` correctly computed `1` (null == null -> true) and wrote
   it to the dest slot. But `Brfalse_Ref` read that `1` as an **mStack index**:
   `mStack[1]` happened to be null, so `!(idx>=0 && mStack[idx]!=null)` -> true
   -> it BRANCHED (treated "true" as falsey) -> skipped the `new T()` body ->
   `_inst` stayed null -> caller's `.Test = "bar"` threw NRE.

Diagnostic evidence (removed): `Ceq_Ref res=1 DstOff=4` followed by
`Brfalse_Ref DstOff=4 idx=1 mStack[idx]=null -> BRANCH`. The standalone (non-
inlined) `get_Inst` compiled correctly (brfalse dest typed `Int32`, isRef=False);
only the inlined body mis-typed it.

### Why ReflectionTest06 also fixed (bonus)
Same inlined-`Ceq_Ref` pattern in a different test (a reflection `SetValue` path
that gated on a null check). The fix is general: any inlined body carrying an
already-specialized `Ceq_Ref` was vulnerable.

## Fix
**File:** `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`
**Site:** `TypeSpecializeNeoOpcodes`, immediately before the plain-`Ceq` case
(formerly line 1088).

Added a `case OpCodeREnum.Ceq_Ref:` that seeds the dest register to `IntType`
(mirroring line 1094 for freshly-specialized `Ceq`). The opcode itself is NOT
re-touched (`Ceq_Ref` is terminal). This ensures an inlined `Ceq_Ref`'s result is
correctly typed, so the following `Brfalse`/`Brtrue` stays plain (reads the int32
0/1, not an mStack index).

The fix is purely additive, Neo-gated, and corrects a type-tracking gap for an
already-specialized opcode re-entering the specialize pass via inlining.

## Verification
- `Test01.UnitTest_Generics` + `Generics2` (name filter): `Ran 4 tests, 0 failed`.
- Full Neo smoke (no filter): `Ran 935 tests, 24 failed` (was 27). The 24 are a
  strict subset of the prior 27 (diff = the 2 targets + ReflectionTest06; zero
  new failures).
- NeoStep: `Ran 401 tests, 0 failed`.
- Legacy: plain `Debug` build 0 errors (Neo-gated fix).
