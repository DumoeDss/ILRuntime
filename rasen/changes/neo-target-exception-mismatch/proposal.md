# proposal -- neo-target-exception-mismatch (Wave-2 C14)

## Mandated target
Cluster C14 from `wave2-rootcause-plan.md`: 4 tests failing with
`System.Reflection.TargetException: Object does not match target type`
(InheritanceTest05/07/15/18) at the 2026-07-13 grounding (189 failures).

## Re-audit finding (the headline)
**The TargetException (C14's defining symptom) is ALREADY RESOLVED -- by sibling
C2 (neo-iltype-cast-clr-base).** C2's `ProjectNeoClrCallRefArgs`
(ILIntepreter.Neo.cs:1114) projects an ILTypeInstance `this` / by-value CLR-ref
param to its `CLRInstance` adaptor before the reflection invoke, so the CLR
`MethodInfo.Invoke` direct cast no longer throws TargetException. Verified in the
current 104-baseline (`rasen/changes/neo-overhaul/handoff/fullsmoke-ground-2026-07-13.md`
is 189-failed / pre-C2; the post-C2..C5 logs `.tmp-c5-fullsmoke-after.log` show
the 104-baseline where NONE of the 4 tests throw TargetException anymore).

Current failure modes of the 4 C14 tests in the 104-baseline (each reproduces in
isolation; all PASS on Legacy plain-Debug + useRegister=true):
- InheritanceTest05: **PASSES** (C2 fixed it outright).
- InheritanceTest07: `0 != 11` -> a `ref int` write-back gap (C14-adjacent).
- InheritanceTest15: `Exception of type 'System.Exception'` (val != 1) -> the SAME ref write-back gap.
- InheritanceTest18: `Specified cast is not valid` -> a DISTINCT reverse-projection gap.

## Root cause pinned (Neo-vs-Legacy evidence) -- the ref write-back gap (07/15)
`obj.VMethod3(ref val)` on an IL class inheriting a CLR base lowers to
`callvirt.clr TestClass2::VMethod3(int32&)`. The Callvirt_CLR handler
(ILIntepreter.Neo.cs:3766) did:
  1. CopyNeoCallArguments (deref the byref into the callee param region `targetBase`)
  2. InvokeNeoClrMethod -> the autogen Neo redirect `VMethod3_1_Neo`
  3. **NO post-call reverse copy** (CopyNeoCallThisBack)

The IL `Call` path (ILIntepreter.Neo.cs:3367/3399) DOES call CopyNeoCallThisBack;
the Callvirt_CLR path was missing it. AND the committed `VMethod3_1_Neo` autogen
stub is STALE (same defect class as child-28): it reads `@arg` by value, mutates
the local, but never writes the mutated `@arg` back into `__frameBase`. The
current generator template (MethodBindingGenerator.cs:435-440 +
BindingGeneratorExtensions.cs `AppendNeoWriteBackCode`) DOES emit the write-back
epilogue -- the committed stub predates it.

So BOTH halves of the write-back were absent for Callvirt_CLR redirect calls:
- the stub never wrote the mutated ref into `targetBase`
- the handler never propagated `targetBase` -> caller frame

Result: `val` stayed 0 after `obj.VMethod3(ref val)` -> `0 != 11` / `val != 1`.

C2-adjacent verdict: C14 shares the Callvirt_CLR opcode with C2 but is a DISTINCT
site. C2 fixed the `this`/by-value-param PROJECTION (forward, IL->adaptor); C14
is the byref WRITE-BACK (a different mechanism C2 never touched).

## The fix (Neo-gated, Legacy-neutral by construction)
1. ILIntepreter.Neo.cs Callvirt_CLR (after InvokeNeoClrMethod, ~:3864): add the
   snapshot + CopyNeoCallThisBack write-back, mirroring the IL Call path's
   proven pattern (the call dest may alias a byref source register; the snapshot
   protects against the result clobbering the byref bytes before the write-back
   re-reads them). No-op when the method has no ref/out params
   (PrimitiveByRefSrc == null). F-7B rebasing is IL-method-only and excluded.
2. ILRuntimeTest_TestFramework_TestClass2_Binding.cs `VMethod3_1_Neo`: hand-port
   the stale stub to capture `__off_2` at read time and emit the Step-13-Area-4c
   write-back epilogue (`WriteNeoValueType(@arg, __frameBase + __off_2, sz)`),
   matching the current generator template (child-28 stale-stub pattern).

## Distinct follow-ups surfaced (NOT C14; reported honestly, not forced)
- **InheritanceTest07** progresses past the ref fix but then fails
  `Not supported opcode Addi_R4` thrown from `ILIntepreter.ExecuteR`
  (Register.cs:5325 -- a LEGACY executor). The adaptor invokes the IL override
  `TestCls5.AbMethod2` (`return arg1 + 1.2f`) through a Legacy ExecuteR path that
  cannot handle a Neo-only opcode. Pre-existing; masked by the earlier ref
  failure. Cluster: adaptor -> Legacy executor in Neo mode (deep).
- **InheritanceTest18** is the REVERSE of C2: `TestClass2.Alloc() as TestCls5`
  returns the CLR adaptor; casting it back to the IL type fails. Neo `unbox.any`
  on an IL reference type THROWS (ILIntepreter.Neo.cs:5159 / :5187) where Legacy
  KEEPS the adaptor (Register.cs:4147-4150) and unwraps it at field access
  (~15 `CrossBindingAdaptorType -> ILInstance` sites). A multi-site Neo gap
  (unbox.any + field-access adaptor-unwrap); distinct cluster.

## Verify (truth = full-smoke number)
- Full Neo smoke: **104 -> 103** (`.tmp-c14-postfix.log`: Ran 916, 103 failed).
  Failure-set diff vs `.tmp-c5-fullsmoke-after.log` (104): exactly 1 test
  flipped GREEN (InheritanceTest15); **0 regressions** (no new failures).
- Name-filter: InheritanceTest15 PASS after fix.
- Stash-toggle (both edits): IT15 FAILS on HEAD (`Exception of type
  'System.Exception'`) -> pop -> IT15 PASSES (airtight).
- NeoStep smoke: **382/0** (no regression).
- Legacy-neutral: IT15 PASS on Legacy; Legacy NeoStep 382/18 == documented
  pre-existing Legacy baseline (changes are 100% `#if ENABLE_NEO_MODE`-gated).

## Scope discipline
The mandated success criterion ("104 -> lower") is MET (104 -> 103, no
regressions). IT07 and IT18 are DISTINCT clusters with their own root causes
(adaptor->Legacy executor; reverse-projection unbox.any/field-access). Per the
"fix the tractable part + report" guidance, they are reported as surfaced
follow-ups rather than forced into this child.
