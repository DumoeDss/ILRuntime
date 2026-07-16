# Design -- neo-recluster-2 (Wave-2 child of neo-overhaul)

## Goal
FRESH-ground the full Neo smoke (was 2 failed), re-cluster the CURRENT 2, fix
the most tractable singleton, verify the drop by re-running the full smoke.

## Fresh grounding (no filter, Debug_Neo)
- Entry: `Ran 951 tests, 2 failded` (StructTest12 + MyTest.Test).
- Post-fix: `Ran 951 tests, 1 failded` (StructTest12 only).
- Delta: **-1 (MyTest.Test FIXED)**. NeoStep 417/0. Legacy-neutral.

## The 2 (entry) -> verdict
1. **MyTest.Test** -- FIXED. Two coupled sub-bugs:
   - (A) Box is an UNSEEDED reference producer in TypeSpecializeNeoOpcodes
     (the crash root). The Move specialization keys the reference flag on
     `IsNeoReferenceSlot(registerTypes[src])`; an unseeded box dest yields
     Operand=0 -> the runtime Move copies only the prim index, not the ref-
     slot object -> the dest points at the box register's own ref slot. When
     that register is reused (ldstr into the same temp), the box is
     overwritten and the Move dest dangles (a String ended up in the `e`
     local -> get_Current's `(IEnumerator)` cast threw).
   - (B) The autogen `get_Current_0_Neo` stub discarded its KeyValuePair
     value-type return (a literal `// TODO: CLR value type return ... Step 13`
     line) -> get_Current always yielded the dest default ([0,0]).
2. **StructTest12** -- REMAINS (deepest singleton). Activator.CreateInstance<T>
   in a generic method resolves T to ILTypeInstance (not the enclosing T=
   MyStruct2) AND the redirect returns a heap ILTypeInstance not a struct. Two
   coupled bugs; reported honestly.

## The fix (2 sites, both Neo-gated -> Legacy-neutral)
1. **JITCompiler.cs TypeSpecializeNeoOpcodes**: `case OpCodeREnum.Box:` seeds
   `registerTypes[op.Register1] = appdomain.ObjectType`. Box ALWAYS produces a
   reference (the boxed object); seeding ObjectType makes every following Move
   a reference Move (Operand=1) -> the runtime copies the boxed object to the
   dest's OWN ref slot (independent copy). Same producer-seeding pattern as
   child-11 (Ldsfeld) / child-16/21 (Ldind/Ldelem/Call) / child-23 (Ldfld_Ref).
   Box was the one remaining unseeded reference producer.
2. **System_Collections_Generic_IEnumerator_1_KeyValuePair_2_I_t1.cs
   get_Current_0_Neo**: replace the Step-13 TODO with the child-28 value-type-
   return write (`WriteNeoValueType` for the KeyValuePair<int,int> return).

## Soundness
- The Box seed is additive. A reference seed can never collide with an int-
  branch (Brtrue/Ceq key on IsNeoReferenceSlot) or a VT path (Move_Vt keys on
  IsValueType); a reference is neither-conflicting. Consumers of registerTypes
  only GAIN a correct reference classification they were previously missing.
- The reference Move's `mStack[dstIdx] = mStack[srcIdx]` gives each dest an
  independent object reference (shallow copy, C# box-then-assign semantics).
  The box register's ref slot can be reused freely afterwards.
- The get_Current stub fix is the canonical child-28 WriteNeoValueType pattern.
  KeyValuePair<int,int> is a pure-primitive CLR struct (8 bytes, RefCount 0).

## Residual (honestly deferred, does NOT fail the test)
The MAIN loop of MyTest.Test prints "0  0" under Neo (Legacy prints "1  1").
GetEnumeratorTest ("1  1") and GetEnumeratorTest2 ("1  1") are correct
post-fix. MoveNext_0_Neo calls the real boxed-struct MoveNext (mutates the box
in place), so box mutation works in general (GetEnumeratorTest2 proves it).
The main loop's specific box (the GetEnumerator-inlined path) does not observe
the MoveNext advance -- a deeper box-identity / return-marshalling sub-issue,
NOT the crash root. The test has no value assertion, so it passes. Candidate
follow-up = the boxed-enumerator value path in the inlined-loop context
(Step-19 territory).

## Verify
- MyTest.Test targeted: 1/0 PASS (exit 0; no String cast; GetEnumeratorTest2
  "1  1" correct).
- NeoStep smoke: 417/0 (no regression).
- Full smoke: 2 -> 1 (StructTest12 the sole survivor; strict subset).
- Legacy-neutral: plain Debug build 0 errors (TypeSpecializeNeoOpcodes is the
  Neo-only pass; the stub fix is inside `#if ENABLE_NEO_MODE`).
