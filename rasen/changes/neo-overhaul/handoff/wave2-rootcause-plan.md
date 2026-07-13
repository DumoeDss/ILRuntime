# Wave-2 Root-Cause Clusters -- from the 2026-07-13 full-smoke grounding (189 failures)

> Source of truth: `fullsmoke-ground-2026-07-13.md` (a REAL full Neo run: 914 ran / 189 failed, no crash).
> This file clusters the 189 failures by SUSPECTED root cause (not by exception type) to drive Wave-2
> children. Each cluster = a candidate child. **Root causes here are SUSPECTED from the exception message
> pattern; each child MUST re-audit (reproducer + JIT dump) before fixing** -- do not trust the label blindly.
> User mandate: real bugs only (no fake tasks); verify each against the real run; fix and drive failures down.

## Headline finding (answers "are these all code bugs?")
**187 of 189 failures are real runtime bugs, NOT unimplemented-opcode TODOs.** Only 2 are NIE. The dominant
failure modes are `InvalidCastException` (56), `NullReferenceException` (45), `MissingMethodException` (23) --
i.e. the interpreter runs the opcodes but produces wrong casts / null derefs / unresolved VTable slots. NeoStep
filtered smoke is 380/0 green; ALL 189 failures are non-NeoStep TestCases (the broader suite the overhaul did
NOT regress-test against).

## Clusters (by suspected root cause, highest coverage first)

### C1 -- Delegate Adapter -> CLR Action/Func cast fails [InvalidCastException, ~32] HIGHEST VALUE
Tests: DelegateTest01/03/06/07/11/18/19/20/21/41/43/45, DelegateInnerTest.TestRun, CLRBindingTest07,
GenericExtensionMethod1Test1/2, GenericExtensionMethod2Test1-8, GenericStaticMethodTest1-8, GenericMethodTest14,
Test03.Test05. Message pattern: `Unable to cast MethodDelegateAdapter/FunctionDelegateAdapter\`N[...] to
System.Action\`1/Func\`2[...]`. Suspected root cause: a Neo MethodDelegateAdapter/FunctionDelegateAdapter
returned where a real CLR delegate is expected (a cast/conversion path -- maybe the delegate-invoke or the
adapter's implicit cast / Delegate op_Implicit is missing under Neo). **One fix could green ~32 tests.**

### C4 -- callvirt "this is null" + "cannot resolve VTable slot for GetType()" [NRE+MissingMethod, ~20]
NRE "Neo callvirt this is null": GenericMethodTest18, MyTest.UnitTest_Test1, ReflectionTest04/19,
SimpleTest.StaticTest, StaticTest.UnitTest_StaticTest03, UnitTest_10030. MissingMethod "cannot resolve VTable
slot for System.Type GetType()": DelegateTest36-40, EnumTest15, ReflectionTest03/10/11, TestMethodParametersInfo,
StaticTest01. Suspected: Neo callvirt's `this`/VTable-slot resolution for inherited CLR methods (GetType is
inherited from System.Object) and the `this=null` lazy-init class. Possibly shares a root with the C2 cast class.

### C2 -- ILTypeInstance -> CLR base/interface cast fails [InvalidCastException, ~14]
Tests: InheritanceTest01/02/03/04/06/14/16/21/22, TestAs03, TestIs.TestInterface, RefOutTest.UnitTest_OutTest,
StructTest6, GenericMethodTest11. Message: `Unable to cast ILTypeInstance to 'Adaptor'/ClassInheritanceTest/
TestClass4/IDisposable/IComparable`. Suspected: the CrossBindingAdaptor unwrap / CLR-base cast path under Neo
(an IL instance is not unwrapped to its CLR adaptor when cast to a CLR base/interface).

### C12 -- "Type must be a runtime Type" in Delegate.CreateDelegate [ArgumentException, 10]
Tests: DelegateTest25/28-35, EnumTest21. Message: `Type must be a runtime Type object. (Parameter 'type')`.
Suspected: a Neo ILType/ILRuntimeType is passed where System.Reflection wants a runtime Type (Delegate.CreateDelegate
/ Enum static). The ILRuntimeType -> runtime-Type bridge.

### C10 -- array/struct index out of range [ArgumentOutOfRange + IndexOOB, ~11]
Tests: ArrayTest05, CLRBindingTest08, StructTest8/11, Test03.TestUsingNested, TestValueTypeBinding.Test03,
UnitTest_10035/10036, ExpTest_10.UnitTest_Struct, UnitTest_10051. Suspected: a size/offset/index calculation
in array or VT-element access (ldelem/stelem or field-offset math).

### C11 -- TestValueTypeBinding misc (Exception/NRE, ~10)
Tests: UnitTest_10025/10034/10039/10042/10048/10050, Vector3.get_One, StructTest3/4/14, UnitTest_10034. A grab-bag
of VT-binding edges; needs per-test triage. May overlap C10/C1(UnitTest_10046 is a delegate cast).

### C9 -- Hotfix "is not bound!" [NotSupportedException, 10] -- LIKELY TEST SETUP, NOT ENGINE
Tests: HotfixBasicTestCases.Test02/03/07/08, HotfixTestGenericTestCases.Test01/02, HotfixTestInheritanceTestCases.Test02.
Message: `Void .ctor(Int32) is not bound!`, `System.String Format(...) is not bound!`, `get_One2() is not bound!`,
etc. Suspected: the HotfixAOT.patch / CLR binding registration does not cover these methods -- a TEST-HARNESS /
binding-config gap, NOT necessarily a Neo engine bug. **Re-audit FIRST**: confirm whether the engine should bind
these or the test setup is incomplete.

### C5 -- "No parameterless constructor for ILTypeInstance" [MissingMethod, 7]
Tests: GenericMethodTest4, JsonTest3/4/6/8/9. Message: `No parameterless constructor defined for type
'ILRuntime.Runtime.Intepreter.ILTypeInstance'`. Suspected: Activator.CreateInstance(typeof(ILType)) (child-22
redirect) edge, or JSON deserialization invoking the wrong ctor path. Shares root with the Activator surface.

### C16 -- generic "Object reference not set" NRE (non-callvirt-this) [NRE, ~25]
A residual NRE bucket after removing the C4 "callvirt this is null" ones: ArrayBindTest, ArrayTest09,
DelegateTest16/17/23, ExpTest_10 (UnitTest_10022/10023/1008/Struct2), GCTest.TestDicEnumerator, JsonTest2,
MyTest.Test, RefOutTest (several), ReflectionTest06/14, RegisterVMTest04, SimpleTest.EqualsTest,
StaticTest.05, StructTests, Test01.Generics/Generics2, Test05 (ForEach/ForEachTry/Return/StructDictionary/Out2),
UnitTest_10025/10034. Likely several distinct roots; needs per-sub-cluster triage.

### C8 -- async stateMachine null [ArgumentNullException, 4]
Tests: AsyncAwaitTest.TestRun/TestRun1/TestRun4, AsyncAwaitTest/TestClass.Show1. Message: `Value cannot be null.
(Parameter 'stateMachine')`. Suspected: the async state-machine bootstrap under Neo (Step 20 edge).

### C7 -- "Neo lowering could not find expected Push" [Exception, 3] JIT BUG
Tests: DelegateTest24, StructTest7, UnitTest_10027. Message from `Optimizer.Neo.cs:LowerNeoOffsets`. A JIT/optimizer
Push-deletion assumption violated (child-1/14 fixed Leave/EH; this is another shape). Real JIT correctness bug.

### C6 -- interface dispatch slot [MissingMethod, 3]
Tests: InheritanceTest_Interface/Interface2, TestGenericStruct. Message: `type X does not implement interface Y
(method slot 0)`. Interface-method slot resolution under Neo.

### C14 -- "Object does not match target type" [TargetException, 4]
Tests: InheritanceTest05/07/15/18. Reflection invoke / delegate target type mismatch.

### C13 -- "Cannot find Delegate Adapter" [KeyNotFound, 3]
Tests: DelegateExtTest03, DelegateTest14/22. DelegateManager adapter registration gap.

### C3 -- Enum cast/Equals/GetType [mix, ~8]
Tests: EnumTest11/15/20/21/22/30/32/33. Overlaps C4(GetType)/C12(runtime Type)/C2; enum-specific cast + Equals.

### Lone AV
UnitTest_10037 (AccessViolation) -- a real memory-safety bug; high severity even at count 1.

## Wave-2 child plan (priority order by coverage; each is a small-feature child, re-audit-first)
1. **neo-delegate-adapter-clr-cast** (C1, ~32) -- HIGHEST, one fix greens the most.
2. **neo-callvirt-this-null-vtable-gettype** (C4, ~20).
3. **neo-iltype-cast-clr-base-interface** (C2, ~14).
4. neo-type-must-be-runtime-type (C12, 10).
5. neo-vt-array-index-oob (C10, ~11).
6. neo-hotfix-not-bound-audit (C9, 10) -- AUDIT FIRST (test-setup vs engine).
7. neo-iltypeinstance-parameterless-ctor (C5, 7).
8. ... then C16 NRE sub-clusters, C8, C7, C6, C14, C13, C3, the AV.

## Verification discipline (per child)
- Each child MUST reproduce a representative failing test from THIS run (the test name is in
  `fullsmoke-ground-2026-07-13.md`), confirm it fails on HEAD, fix, then RE-RUN the full smoke and record the
  failure-count delta (expect the cluster's tests to flip green). No "looks fixed" -- the full-smoke number is the
  truth. Target: drive 189 -> lower, monotonically, child by child.
- A cluster that turns out to be test-setup (e.g. C9) is reported honestly as such, NOT forced as an engine fix.
