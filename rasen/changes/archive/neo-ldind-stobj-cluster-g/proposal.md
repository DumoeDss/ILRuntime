# Proposal: neo-ldind-stobj-cluster-g

Wave-2 child of `neo-overhaul` (branch `features/object-model-overhaul`).
Addresses the fresh-60 cluster G surface: ldind / stobj / ldelema / ldlen
opcodes in `ExecuteNeo` that surfaced as failures in the full Neo smoke.

## Mandate (from LEAD)
- Ground: full Neo smoke = 48 failed (re-audited from the stale 60-grounding;
  wave-2 children C1..C7 + F-10 already shipped). Cluster G = the 6 tests
  hitting `ILIntepreter.Neo.cs` ldind/stobj/ldelema/ldlen arms.
- Success = full-smoke count dropping (48 -> lower), verified by re-running
  the full smoke. No "looks fixed".
- Fix the LARGEST sub-cluster + report the rest. Neo-gated -> Legacy-neutral.

## Re-audit (verified at 48 on HEAD fc2baa26)
The 6 cluster-G tests at 48, with current frames (line numbers shifted vs the
stale ground-60 doc):

| Test | Opcode arm | Neo.cs line (current) | Exception |
|---|---|---|---|
| RefOutTest.UnitTest_RefOutNull2 | stobj T | 6434 -> now in stobj-ref branch | NRE (store null into a ref/out param) |
| RefOutTest.UnitTest_GenericsRefOut | stobj T | 6449 | NRE (store new T into a static ref field) |
| RefOutTest.UnitTest_GenericsRefOut2 | stobj T | 6434 | NRE (store new T into a local ref param) |
| ExpTest_10.UnitTest_Struct | ldind.i4 | 6153 | IndexOOB (nested ldflda-on-byref, deferred) |
| ExpTest_10.UnitTest_Struct2 | ldind.i4 | 6152 | NRE (nested ldflda-on-byref, deferred) |
| ReflectionTest14 | ldlen | 5711 | NRE (upstream: GetFields() returns null) |
| RefOutTest.UnitTest_ArrayReferenceTest | ldelema | 6624/6706 | NRE (null IL-class array element as `out`) |

(UnitTest_RefOutNull2 is tracked in the stale ground-60 as cluster B, but
after the unbox.any fix it progressed to `stobj T` -- it is part of the stobj
sub-cluster now.)

## Sub-cluster grouping (at 48)
- **STOBJ reference-type (3 tests):** RefOutNull2, GenericsRefOut,
  GenericsRefOut2. `stobj T` where T is an IL **class** (TestGenrRef /
  TestClass222) and the dest byref points to a reference slot (a `ref`/`out`
  param or a static reference field). This is the LARGEST sub-cluster.
- **LDIND.I4 nested-ldflda (2 tests):** UnitTest_Struct, UnitTest_Struct2.
  The `a.Struct.value += N` pattern (`ldflda Struct; ldflda value; ldind.i4`).
  The inner `ldflda` operates on a byref-to-boxed-struct. Deferred by child-27
  as a sibling; needs a JIT marker or runtime byref-aware ldflda.
- **LDLEN upstream (1 test):** ReflectionTest14. `targetType.GetFields()`
  returns null under Neo -> `ldlen` NREs. The ldlen arm is CORRECT; the bug is
  upstream in IL reflection (GetFields returns null). Separate scope.
- **LDELEMA null-element (1 test):** UnitTest_ArrayReferenceTest.
  `dict.TryGetValue(k, out arr[i])` on a freshly-allocated IL-class array;
  `arr[i]` is null and the ILTypeInstance[] ldelema branch throws NRE.

## Target of THIS child (largest sub-cluster)
The **stobj reference-type** sub-cluster, plus its symmetric **ldobj
reference-type READ** counterpart (the same defect class: stobj/ldobj of a
reference-type T is a reference store/load through the dest/source byref, NOT
a value copy). The Neo stobj/ldobj arms only handled value-type T and fell
through to the ILTypeInstance-Primitives value-copy path for a reference T,
corrupting / NRE-ing.

## Why this is the right fix (principled, not a patch)
- `stobj`/`ldobj` of a reference-type T is semantically a reference store /
  load through the managed-pointer (byref) operand -- IDENTICAL byref
  semantics to `Stind_Ref` / `Ldind_Ref`, which already implement the full
  byref reference store/load dispatch.
- Legacy parity: `ExecuteR` Stobj dispatches on `objRef->ObjectType`
  (StackObjectReference -> reference store; StaticFieldReference -> static ref
  field set), and Ldobj StackObjectReference -> `CopyToRegister(dest,
  resolveReference)`. The Neo arms lacked the reference-type path entirely.
- The fix adds a `!t.IsValueType` branch at the top of each arm that mirrors
  the existing `Stind_Ref` / `Ldind_Ref` dispatch (frame-native /
  caller-owned-slot F-7B / CLR-array / CLR-object-field / IL-instance-ref-
  field). Value-type T is untouched (mutually exclusive discriminator).

## Out of scope (reported, not fixed here)
- ldind.i4 nested-ldflda (child-27 deferred sibling; 2 tests).
- ldlen / GetFields-returns-null reflection bug (upstream; 1 test).
- The deeper downstream gaps the stobj/ldobj fix UNBLOCKS but does not
  resolve: GenericsRefOut/GenericsRefOut2 progress past stobj/ldobj and hit
  (a) `constrained. T` not followed by a callvirt (JIT lowered a non-virtual
  callvirt to Call), and (b) a `NeoMarshalByrefFieldToSlot` NRE on a CLR-
  struct byref. These are separate defects.

## Also fixed in this child (bonus, same file)
- **ldelema null-element `out arr[i]` (UnitTest_ArrayReferenceTest, 1 test):**
  the ILTypeInstance[] ldelema branch threw NRE on a null element, breaking
  `dict.TryGetValue(k, out arr[i])` on a freshly-allocated array. Fixed by
  emitting the `(arrIdx, elementIdx)` array-element byref for a null element
  (so the write-back Array arm SetValues it); non-null elements keep the
  materialize-for-read path. Deterministic flip; 0 regression.
- The deeper downstream gaps the stobj/ldobj fix UNBLOCKS but does not
  resolve: GenericsRefOut/GenericsRefOut2 progress past stobj/ldobj and hit
  (a) `constrained. T` not followed by a callvirt (JIT lowered a non-virtual
  callvirt to Call), and (b) a `NeoMarshalByrefFieldToSlot` NRE on a CLR-
  struct byref. These are separate defects.

## Verify (truth = full-smoke number)
- NeoStep 0-failures (no regression; 398/0).
- Name-filter: RefOutNull2 + ArrayReferenceTest PASS after fix (deterministic).
- Full smoke: **48 -> 46** (RefOutNull2 + ArrayReferenceTest deterministic
  flips; 0 new regressions). RegisterVMTest04 is flaky/order-dependent (passes
  / fails with the SAME code across runs) -- NOT a deterministic effect.
- Legacy-neutral: change is 100% in `ILIntepreter.Neo.cs` (file-gated
  `#if ENABLE_NEO_MODE`); Legacy compiles none of it. Empirically: plain
  Debug + useRegister=true + NeoStep = 398 ran / 18 failed == documented
  pre-existing Legacy baseline.
