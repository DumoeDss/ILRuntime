# Design: neo-float-vtreturn-opaddition

## Phase 1 diagnosis (empirical, the crux)

### Probes (TestCases/NeoStepFloatVtReturnDiagTest.cs, run under Neo, JIT-dumped)
Each computes a HOST-side int sum (CLR-side float arithmetic -- sidesteps the unrelated
conv.i4-float-bit-reinterpret bug) and asserts via `TestCLRBinding.NeoAssertEq`, which
THROWS with the ACTUAL value in the message so the run reveals the real sum.

- TC1 (ctor alone): `var v = new TestVector3(1f,2f,3f); Sum(v, One)`.
  Expected 9 = (1+2+3)+(1+1+1). HEAD: **actual=3** -> v=(0,0,0). SYMPTOM 1 CONFIRMED.
- TC2 (op_Addition return alone, NO ctor): `var c = TestVector3.One + TestVector3.One;
  Sum(c, One)`. Expected 9 = (2+2+2)+3. HEAD: **actual=3** -> c=(0,0,0). SYMPTOM 2 CONFIRMED.
  (One is host-precomputed (1,1,1); op_Addition's body uses the PARAMETERLESS newobj + field
  writes, so the float ctor is NOT involved -- this isolates the VT-return path.)
- TC3 (array += compound): HEAD crashes at JIT time -- `Optimizer.Neo.cs:1268`
  "Neo lowering could not find expected Push instructions for Call/Newobj" during
  LowerNeoOffsets. A SEPARATE JIT-lowering bug (the `new TestVector3[1]; arr[0] = new ...;
  arr[0] += new ...` compound), NOT a value bug. Out of scope here.

### JIT dump evidence (the pin)
TC1 final JIT:
```
0:initobj r0, TestVector3            # v = default
1:ldloca.s r2, r0
2:ldc.r4 r3,1 ; 3:ldc.r4 r4,2 ; 4:ldc.r4 r5,3
5:push r2
6:call.redirect -, r3, r4, r5, TestVector3::.ctor(Single,Single,Single)(m)  # AUTOGEN redirect
7:ldsfld r3, ...                     # One
8:call r1, r0, r3, SumTestVector3Fields(...)
...
Local dump: TestVector3 v = (0,0,0), Int32 s = 3
```
TC2 final JIT:
```
3:call.redirect r0, r2, r3, TestVector3::op_Addition(...)(mr)   # AUTOGEN redirect
```
BOTH route to `call.redirect` (the autogen `*_Neo` stub), and BOTH yield zero. The
reflection-fallback `call` (SumTestVector3Fields, NeoAssertEq) WORKS (the assert fired with
the right value).

### Root cause (stale generated stubs)
`ILRuntimeTest_TestFramework_TestVector3_Binding.cs` (generated, registered because
TestVector3 has a registered ValueTypeBinder) contains STALE `*_Neo` stubs from BEFORE the
Step 13b generator fix:

`op_Addition_2_Neo`:
```csharp
TestVector3 @a = default(TestVector3);   // BROKEN: VT arg NEVER read from frame
// TODO: CLR value type reflection fallback: Step 13
TestVector3 @b = default(TestVector3);   // BROKEN
// TODO: CLR value type reflection fallback: Step 13
var result_of_this_method = @a + @b;     // = (0,0,0)
// TODO: CLR value type return in reflection fallback: Step 13   // return NEVER written
```
`Ctor_0_Neo`:
```csharp
__curPrim += 4;                          // skip retRefBase
float x = ReadNeoFloat(...); y = ...; z = ...;   // floats read correctly
TestVector3 result_of_this_method = new TestVector3(x,y,z);  // constructed correctly
// TODO: CLR value type return in reflection fallback: Step 13   // DISCARDED, never written
```

The runtime reflection path is CORRECT (`InvokeNeoClrMethod`: VT param read
ILIntepreter.Neo.cs:342-345, VT return write :1129-1144). The GENERATOR is CORRECT
(`BindingGeneratorExtensions.cs`: VT param -> ReadNeoValueType :177-178, VT return ->
WriteNeoValueType :603). ONLY the generated test-binding files are stale.

### Why "ctor yields zero" looked suspicious but IS real
`TestVector3.One` is `public static TestVector3 One = new TestVector3(1,1,1)` -- a STATIC
FIELD initialized in the HOST. IL reads it via `ldsfld`; the precomputed bytes cross
unchanged. It NEVER invokes the broken ctor/op_Addition autogen stubs. So children 3/8/15/
16/21 (which use `TestVector3.One` + field reads, NOT `new TestVector3(float,float,float)`)
PASS. The bug is context-specific to the autogen-redirect (`call.redirect`) path: IL that
CALLS the TestVector3 ctor or operators. Suspicion reconciled.

## Eliminated hypotheses
- (a) "float-VT-RETURN marshalling gap in the runtime" -- DISPROVEN. The runtime reflection
  path marshals VT returns correctly (WriteNeoValueType; proven by the working helpers). The
  gap is the autogen STUB not CALLING that path, not a runtime marshalling bug.
- (b) "same unseeded-producer class as child-21 but for a VT-return" -- DISPROVEN. The
  result is ZERO (unread/unwritten), not bit-corrupted typed-arithmetic garbage. child-21's
  class produces WRONG float values via integer ops on IEEE bits; this produces DEFAULT zero
  because the args were never read. Different mechanism entirely. (Also the ctor result is
  never consumed in arithmetic in TC1, so registerTypes seeding is irrelevant.)
- (c) "something in the ldobj/stobj float path" -- DISPROVEN for TC1/TC2 (no array element
  involved). TC3's failure is a JIT Push-lowering crash, not the ldobj/stobj runtime arms
  (child-26 fixed those; the int-struct sidestep proved them correct).
- (d) "genuinely the ctor (float-arg marshalling)" -- PARTIALLY but not as framed. The ctor's
  FLOAT ARGS are read fine (ReadNeoFloat works); the bug is the ctor RESULT is discarded
  (unwritten return), so it's a VT-RETURN bug, not a float-arg bug.

## The fix (Phase 2)
Two parts:

### Part A -- public size facade + generator emission fix (the enabler)
The current generator's Step-13b emission referenced the INTERNAL `Optimizer` class
(`ILRuntime.Runtime.Intepreter.RegisterVM.Optimizer.GetNeoValueTypeManagedSize`) in 5
CONSUMER-emission sites (BindingGeneratorExtensions.cs:177/210/284/603 +
MethodBindingGenerator.cs:283). `Optimizer` is internal (default access), and ILRuntime
grants NO InternalsVisibleTo to the consumer (ILRuntimeTestBase) -- so this emission NEVER
COMPILED in the consumer. (No existing generated file references it -- the Step-13b regen
never happened.) This is a latent generator bug.

Fix:
1. Added a public facade `ILIntepreter.GetNeoValueTypeManagedSize(Type t)` (ILIntepreter.Neo.cs,
   right after WriteNeoValueType) that delegates to `Optimizer.GetNeoValueTypeManagedSize`.
   ILIntepreter is already the public Neo-VT-marshalling facade (ReadNeoValueType/
   WriteNeoValueType/ReadNeoFloat/ReadNeoReference are all public on it), and ILIntepreter.Neo.cs
   is `#if ENABLE_NEO_MODE` file-gated -> Legacy-neutral by construction.
2. Repointed the 5 generator emission sites to `ILIntepreter.GetNeoValueTypeManagedSize` (so a
   future regen produces COMPILABLE consumer code).

### Part B -- hand-port the stale TestVector3 `*_Neo` static/ctor stubs
Mirror the (now-compilable) generator emission in the framed-gap file:
- VT param read: `int __sz_N = ILIntepreter.GetNeoValueTypeManagedSize(typeof(TestVector3));
  TestVector3 @a = (TestVector3)ILIntepreter.ReadNeoValueType(typeof(TestVector3), __frameBase, ref __curPrim, __sz_N);`
- VT return write: `if (__retDst != null) { int __retSz = ILIntepreter.GetNeoValueTypeManagedSize(typeof(TestVector3)); ILIntepreter.WriteNeoValueType(result_of_this_method, __retDst, __retSz); }`

Stubs hand-ported (all `#if ENABLE_NEO_MODE`):
- `get_One2_0_Neo`: + VT return write.
- `op_Multiply_1_Neo`: VT param a read (replaces default; REALIGNS the cursor so the following
  float b read is at the right offset) + VT return write.
- `op_Addition_2_Neo`: VT params a,b read + VT return write.
- `Ctor_0_Neo`: + VT return write. (SEE the ctor follow-up note below -- the regular-call
  return write is correct, but a CLR struct newobj is lowered differently and the write no-ops;
  Ctor_0_Neo's write is still correct for the non-newobj value-type-init path and future use.)

### Why hand-port instead of regenerate
Regeneration is driven by the TestMainForm WinForms GUI (TestMainForm.cs:252-261: load DLL into
an AppDomain, ILRuntimeHelper.Init, GenerateBindingCode(domain, path)). It is not cleanly
drivable headlessly, and a full regen produces a large diff across many stale files
(TestStructB/Fixed64/JInt/...) with its own regression surface. The generator is now correct
(and compilable), so hand-porting the framed-gap type (TestVector3) to match the generator
output is the focused, low-risk, verifiable fix. A future regen produces equivalent code for
these stubs (no conflict).

## SYMPTOM 1 (struct newobj ctor) -- NOT FIXED; distinct gap, follow-up
`new TestVector3(1f,2f,3f)` still yields (0,0,0) after Part B. Root cause: the Neo JIT+optimizer
LOWERS a CLR struct newobj to `initobj r0; ldloca r6,r0; push r6(this byref); call.redirect
.ctor(...)` with dest=`-` (Register1<0). The ctor is treated as VOID (constructors return
void); the struct is meant to be mutated IN PLACE through the `this` byref. The runtime
Call_Redirect arm (ILIntepreter.Neo.cs:3079) computes `crRetDstPtr = ip->Register1 >= 0 ? ... :
null` -> null for this dest-`-` form. So the autogen `Ctor_0_Neo`'s WriteNeoValueType-to-
`__retDst` (which Part B added, mirroring the generator) NO-OPS on null -> the dest stays zero.

This is a genuine optimizer/redirect CONTRACT mismatch: the optimizer communicates the struct
dest via the `this` byref (retDst=null), but the generator emits a write-to-retDst. It is NOT
the regular-call VT-return path (symptom 2) and requires either (a) the optimizer to preserve a
dest register for struct newobj so retDst is set, or (b) the redirect to write the constructed
struct THROUGH the `this` byref (reading/decoding the byref from the param region -- its exact
layout for a struct-newobj call.redirect needs empirical confirmation). Both are more involved
than this child's clean regular-call fix; deferred to a follow-up. The diagnosis above (TC1
empirical + JIT dump + the runtime null-retDst path) pins it for the next worker.

Reconciliation with the framed claim: symptom 2 (op_Addition VT-return) -- the literal
"op_Addition float VT-return doesn't write back" -- IS fixed and verified. Symptom 1 (the
"ctor yields zero" half) is a SEPARATE convention and remains open.

## Out of scope (documented)
- SYMPTOM 1 struct newobj ctor (above).
- Instance-method stubs `Normalize_4_Neo` / `Test_3_Neo`: `// TODO: ValueType instance in Neo`
  is a DISTINCT Area-4b/4a shape (VT `this` read + lossy re-box write-back; Test has out-params).
  Not the framed gap; left as-is (already broken on HEAD, no regression).
- The diagnostic TC3's JIT Push-lowering crash ("could not find expected Push for Call/Newobj"
  in LowerNeoOffsets for the `arr[0] = new VT(...); arr[0] += new VT(...)` compound): separate
  JIT bug. Candidate follow-up. (The permanent TC3 uses a host-built array + `+= One`, avoiding
  both the ctor and the stelem/ldelem.any gaps.)
- Full regen of all stale AutoGenerate files: the broader follow-up.

## Verification (DONE)
- Permanent probes (TestCases/NeoStepFloatVtReturnTest.cs): TC1 op_Addition (One+One->Sum 9),
  TC2 op_Multiply (One*5f->Sum 18), TC3 child-26 shape `arr[0] += One` (host-built array->Sum 6).
  Each FAULTS on HEAD (stale stub -> zero -> DivideByZero) and PASSES after.
- Stash-toggle (revert ONLY TestVector3_Binding.cs to HEAD's stale stubs, keep the facade +
  generator fixes): 3/3 FAIL (DivideByZero) -> restore -> 3/3 PASS. Airtight.
- NeoStep smoke: **378/0** (375 baseline + 3 probes), EXIT=0, no regressions.
- Legacy-neutral: plain Debug + useRegister=true + NeoStep = 378 ran / 18 failed (the documented
  pre-existing Legacy set); the 3 new probes PASS under Legacy (not in the 18). Structural too:
  all changes are `#if ENABLE_NEO_MODE` (the facade is in the file-gated ILIntepreter.Neo.cs;
  the binding edits are in Neo blocks; the generator changes only affect future regen).
- child-25 build-server-cache gotcha honored: killed dotnet build-server + Used
  `-p:UseSharedCompilation=false` after every TestClass3.cs / ILRuntimeTestBase touch.
