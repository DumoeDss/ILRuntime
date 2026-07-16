# Full Neo Smoke Grounding -- 2026-07-16 FRESH re-cluster (2 -> 1; MyTest.Test FIXED)

## STATUS: FRESH grounding (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch).
Full smoke is **1 failed** after this child (was 2 on entry).
`Ran 951 tests, 1 failded, 20 ignored, 7 todos` (the survivor is StructTest12).
This child FRESH-ran the smoke, re-clustered the CURRENT 2, deep-diagnosed BOTH,
PINNED the root of each, fixed the most tractable singleton (MyTest.Test -- an
unseeded-REFERENCE-PRODUCER defect in the Box opcode + a stale autogen
value-type-return stub), and confirmed the drop by re-running the full smoke.
NeoStep **417/0** (no regression). Legacy-neutral (plain Debug build = 0 errors;
all changes are Neo-gated / file-gated / in `#if ENABLE_NEO_MODE` blocks).

Build this child: CLI `Debug_Neo --no-incremental -p:UseSharedCompilation=false`
(0 errors), TestCases `Debug -p:UseSharedCompilation=false` (0 errors). After
touching the autogen binding (ILRuntimeTestBase) you MUST kill the build-server
+ rebuild the CLI (not just TestCases) -- the `--no-build` run loads the CLI's
own stale ILRuntimeTestBase.dll (child-25/29 gotcha).

## Fresh ground (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch)
- PRE-this-child (entry):           `Ran 951 tests, 2 failded, 20 ignored, 7 todos`.
- POST-fix (this child):            `Ran 951 tests, 1 failded, 20 ignored, 7 todos`.
- Delta: **-1 (MyTest.Test flipped green)**. The 1 survivor (StructTest12) is a
  STRICT SUBSET of the entry 2 (no new failures, no regressions).
- NeoStep: 417/0 (no regression throughout).

## The CURRENT 2 (entry) -> pinned root + verdict (1 FIXED this child, 1 remains)

### MyTest.Test -- FIXED this child (Box = unseeded reference producer + stale VT-return stub)
- **Symptom (Test01.cs:618):** `InvalidCastException: String -> IEnumerator<
  KVP<int,int>>` at autogen `get_Current_0_Neo` (line 48 of
  System_Collections_Generic_IEnumerator_1_KeyValuePair_2_I_t1.cs). A boxed
  Dictionary.Enumerator stored in the `e` local read back as a String on a
  later loop iteration. Console output before the fix: GetEnumeratorTest
  printed "1  1" (struct enumerator, no box); GetEnumeratorTest2 printed
  "0  0" (WRONG -- should be "1  1"); the main loop crashed on the 2nd
  get_Current of iteration 1.
- **ROOT PINNED (JIT-dump + code-dive, TWO coupled sub-bugs):**
  (A) **Box is an UNSEEDED reference producer** (the load-bearing crash root).
      The Neo type-specialization pass `TypeSpecializeNeoOpcodes`
      (JITCompiler.cs:979) seeds `registerTypes[dest]` for every reference
      producer EXCEPT Box: Ldnull/Ldstr/Ldsfelda (child-11), Ldfld_Ref
      (child-23), Ldind/Ldelem (child-16/21), Call-primitive-return (child-21)
      are all seeded; Box is MISSING. The `Move` specialization
      (JITCompiler.cs:1121-1125) keys the reference flag SOLELY on
      `IsNeoReferenceSlot(GetRegisterType(registerTypes, op.Register2))`:
      `op.Operand = IsNeoReferenceSlot(srcType) ? 1 : 0`. With the box dest
      unseeded, `srcType` is null -> `Operand = 0` -> the runtime Move arm
      (ILIntepreter.Neo.cs:2220) takes the NON-reference path: it CopyBlock's
      the prim bytes (the mStack index) but SKIPS the
      `mStack[dstIdx] = mStack[srcIdx]` ref-slot copy. So `move rDst, rBox`
      leaves rDst's prim pointing at the BOX register's OWN ref slot -- rDst
      does NOT get an independent copy of the boxed object. For MyTest.Test
      the lowering is `box r6,enum; move r5,r6; move r1,r5` (GetEnumerator is
      INLINED into Test()): r1 ended up pointing at r6's ref slot. A later
      `ldstr r6,"  "` (r6 is a reused temp) overwrote that ref slot with the
      "  " String -> the next get_Current read r1 -> mStack[r1's index] =
      "  " -> the `(IEnumerator)` cast threw. Same unseeded-reference-producer
      defect class as child-11 (brtrue on static-ref) / child-23 (brfalse on
      instance-ref via Ldfld_Ref); Box was the remaining hole.
  (B) **The autogen `get_Current_0_Neo` stub DISCARDED its value-type return**
      (the "0  0" wrong-value root). The stub read `this`, computed
      `instance_of_this_method.Current` (a KeyValuePair<int,int> struct), then
      hit a literal `// TODO: CLR value type return in reflection fallback:
      Step 13` and returned WITHOUT writing retDst. So get_Current always
      yielded the dest's prior default ([0,0]) -- GetEnumeratorTest2's "0  0".
      Same stale-autogen-stub defect class as child-28 (TestVector3 operators)
      and the 18+ bindings carrying the same Step-13 TODO.
- **THE FIX (2 sites, both Neo-gated -> Legacy-neutral):**
  (1) JITCompiler.cs TypeSpecializeNeoOpcodes: added `case OpCodeREnum.Box:`
      that seeds `registerTypes[op.Register1] = appdomain.ObjectType` (placed
      right after the Ldstr case). This makes every Move of a box dest a
      reference Move (Operand=1) -> the runtime copies the boxed object to the
      dest's OWN ref slot (independent copy) -> the box register's ref slot can
      be reused freely without dangling the Move dest. Matches the runtime Box
      arm's dest contract (writes the mStack index to prim + the boxed object
      to frameRefBase+dstRefOffset). Box ALWAYS produces a reference (the boxed
      object), so seeding ObjectType is unconditionally correct.
  (2) System_Collections_Generic_IEnumerator_1_KeyValuePair_2_I_t1.cs
      get_Current_0_Neo: replaced the TODO with the child-28 value-type-return
      write pattern -- `if (__retDst != null) { int __retSz = ILIntepreter.
      GetNeoValueTypeManagedSize(typeof(KVP<int,int>)); ILIntepreter.
      WriteNeoValueType(result_of_this_method, __retDst, __retSz); }`.
      KeyValuePair<int,int> is a pure-primitive CLR struct (8 bytes, RefCount
      0) so WriteNeoValueType's flat-bytes write is correct.
- **Soundness:** the Box seed is additive (a reference seed can never collide
  with an int-branch or VT path -- consumers key on IsNeoReferenceSlot/
  IsValueType, and a reference is neither-conflicting). The ref-slot-copy in
  the reference Move gives each dest an independent object, which is the
  correct C# box-then-assign语义. The get_Current stub fix is the canonical
  child-28 pattern (the generator's value-type-return emission is a separate
  broader follow-up; this hand-ports the one stub the failing test reaches).
- **Verify:** MyTest.Test PASS (targeted 1/0, exit 0; no more String cast).
  GetEnumeratorTest2 now prints "1  1" (was "0  0"). Full smoke 2 -> 1.
  NeoStep 417/0 (no regression). Legacy-neutral (plain Debug 0 errors).
  Before/after established by the FRESH grounding run (MyTest.Test failing)
  vs the post-fix full smoke (MyTest.Test gone).
- **RESIDUAL (honestly reported, does NOT fail the test):** the MAIN loop of
  MyTest.Test prints "0  0" under Neo (Legacy prints "1  1"). GetEnumeratorTest
  ("1  1") and GetEnumeratorTest2 ("1  1") are correct post-fix; only the
  third loop (the one that crashed pre-fix) reads a wrong value. MoveNext_0_Neo
  calls the real boxed-struct MoveNext (mutates the box in place), so box
  mutation works in general (GetEnumeratorTest2 proves it). The main loop's
  specific box does not observe the MoveNext advance -- a deeper box-identity
  / return-marshalling sub-issue, NOT the crash root (the crash is fixed; the
  test passes because it has no value assertion). Candidate follow-up child =
  the boxed-enumerator value path in the GetEnumerator-inlined loop context.
  This is the Step-19 boxed-enumerator territory the prior handoff flagged.

### StructTest12 -- REMAINS (Activator.CreateInstance<T> generic-param mis-resolution)
- **Structs.cs:396 (via StructTest12Sub<T> where T:struct,ITestStruct).**
  `T ins = new T() { i = 10 };` where T = MyStruct2. Dump: `ins.i = 1`
  (expected 10) -> the `if (ins.i != 10) throw` fires.
- **ROOT PINNED via JIT dump (confirmed still present, TWO coupled bugs):**
  Roslyn lowers `new T()` (generic struct T, NO `new()` constraint) to
  `call Activator.CreateInstance<T>()`. The JIT dump for StructTest12Sub shows
  `2:call.redirect r1, System.Activator::ILTypeInstance CreateInstance[
  ILTypeInstance]()` -- the generic param T of the Activator call resolves to
  **ILTypeInstance**, while `initobj r1, MyStruct2` in the SAME method
  correctly resolves T to MyStruct2. Inconsistent generic-param resolution:
  `initobj T` resolves T to the concrete struct; the Activator CALL's generic
  arg T resolves to ILTypeInstance (the catch-all IL reference type -- an IL
  struct used as a CLR generic arg is mapped to ILTypeInstance BY DESIGN, the
  same mapping StructTest6's handoff documented).
  - Bug 1: child-22's CreateInstanceNeo redirect does NOT fix this -- the
    generic arg is mis-resolved to ILTypeInstance BEFORE the redirect body
    runs (CreateInstanceNeo reads `method.GenericArguments[0] = ILTypeInstance`
    -> `ilt.Instantiate()` -> a HEAP ILTypeInstance, not a struct).
  - Bug 2: even with correct resolution, the redirect returns a HEAP
    ILTypeInstance (a reference) but the caller assigns the result to a STRUCT
    local (`ins`). The Activator result's mStack index overwrites `ins`'s
    bytes; `ins.i` then reads the index as the field value -> "1".
  The subsequent `constrained MyStruct2; callvirt set_i(10)` boxes the
  ILTypeInstance-index bytes as MyStruct2 and mutates the BOX (discarded), so
  the write does not land either.
- **DEPTH (JIT generic-param resolution under a generic-method context):**
  AppDomain.GetMethod marks the Activator call's T ContainsGenericParameter ->
  the fallback resolves T to ILTypeInstance. A fix needs BOTH (a) JIT
  generic-param propagation into Activator.CreateInstance<T> inside a generic
  method (resolve T to the enclosing method's concrete T, like initobj does)
  AND (b) a struct-returning CreateInstance path (WriteNeoValueType instead of
  WriteNeoObjectResult when T is a struct). Two coupled bugs; the deepest
  singleton in the wave. Candidate future child = JIT generic-method-arg
  substitution for CLR-method generic params + a struct-returning Activator
  redirect arm.

## The fix this child (NOT committed, LEAD commits)
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` (+`case OpCodeREnum.
  Box:` in TypeSpecializeNeoOpcodes seeding switch, seeds registerTypes[dest] =
  appdomain.ObjectType; ~25 added / 0 removed incl. the explanatory comment).
- `ILRuntimeTestBase/AutoGenerate/System_Collections_Generic_IEnumerator_1_
  KeyValuePair_2_I_t1.cs` (get_Current_0_Neo: TODO -> WriteNeoValueType for the
  KeyValuePair<int,int> return; 1 line replaced).
- `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-02.md` (THIS file).

## Remaining 1 (reported honestly -- a distinct deep singleton, re-confirmed)
- **StructTest12** -- JIT generic-param resolution for `Activator.CreateInstance<T>`
  in a generic method (T resolves to ILTypeInstance instead of the enclosing T)
  + the redirect returns a heap ILTypeInstance not a struct (two coupled bugs).

## Lesson
The 12-for-12 / wave-2 "re-audit a verdict + find the unseeded-producer" discipline
held AGAIN. MyTest.Test was filed in ground-04 as "Step-19 boxed-enumerator this-
marshalling / this-register aliasing across loop iterations" -- a DEEP, Step-19-
flavoured framing. But the CRASH root was NARROWER and structural: Box was the one
remaining reference producer that TypeSpecializeNeoOpcodes did NOT seed, so every
`move` of a boxed value was a non-reference move that left the dest pointing at the
box register's own ref slot. The String was not a Step-19 dispatch artefact -- it
was a reused temp's `ldstr` overwriting the box register's ref slot that the
unseeded Move had failed to copy. The same producer-seeding pattern that closed
the float-arithmetic class (child-16/21) and the brtrue/ceq null-comparison class
(child-11/23) closes the box-Move class here. LESSON: when a reference-typed value
"dangles" or "becomes another type" across a few instructions, FIRST audit whether
its PRODUCER seeds registerTypes -- an unseeded reference producer makes every
consumer Move a shallow prim-only copy, and the dangling is a register-reuse
artifact, not a dispatch bug. (Residual: the main-loop "0  0" value is a separate
box-identity sub-issue, honestly deferred.)
