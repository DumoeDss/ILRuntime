# Tasks -- implement-neo-step13

Phased by in-scope area so the implementer can land + smoke-test phase by
phase. **Areas 4-5 are DEFERRED to a Step 13b follow-up** (see proposal.md
"Deferred" list + design.md Non-Goals); they appear here only as a clearly
marked, out-of-scope reminder so they are not accidentally pulled in.

Each phase ends with: rebuild CLI (`dotnet build
ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` -> 0 errors) + rebuild
TestCases (`dotnet build TestCases/TestCases.csproj -c Debug` -> 0 errors) +
full NeoStep smoke (`dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI
--no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll
HotfixAOT/Patched/HotfixAOT.patch true NeoStep` -> all green; was 41/41 after
Step 12b). `-f net8.0` mandatory. A test >10s = infinite loop, kill it.

---

## Phase 1 -- Area 1: IL value-type Box/Unbox coverage (verify, no behavior change)

Step 5 already implemented IL Box/Unbox via `CopyFrameToIL`/`CopyILToFrame`
(ILIntepreter.Neo.cs:1608-1664, 1856-1901). This phase only adds tests and
confirms edge cases; no runtime change expected.

- [x] 1.1 Read the IL Box arm (ILIntepreter.Neo.cs:1608-1664) and Unbox arm
      (1856-1901) and confirm: enum/primitive/VT-with-refs/ref-type-as-box are
      all handled; null-source Unbox throws NullReferenceException (1861-1865);
      CopyFrameToIL/CopyILToFrame (2124/2146) are the copy primitives.
- [x] 1.2 Add `TestCases/NeoStep13Test.cs` (ASCII) with an IL VT-with-ref
      box/unbox round-trip test (assert via the DivideByZero pattern or a
      value-sum compare, per NeoStep12bTest convention -- no throw-asserting
      tests; CLR newobj is Step 9).
- [x] 1.3 Add an IL enum + IL primitive box/unbox round-trip test.
- [x] 1.4 Build CLI + TestCases, run full NeoStep smoke. Expect 41/41 + the
      new IL cases green. If any IL box/unbox case reveals a bug, REPORT it
      (do not silently widen scope); only fix if trivially within Step 5's
      existing path.

## Phase 2 -- Area 2: CLR value-type Box/Unbox/Initobj (with + without binder)

This is the core of Step 13. The Neo path has zero `ValueTypeBinder` references
today; this phase wires the binder into the Neo Box/Unbox/Initobj arms for the
first time.

- [x] 2.1 Implement the CLR Box path. NOTE (deviation from design D2, see
      planning-context §8): the Neo frame stores a CLR value-type LOCAL as a
      BOXED OBJECT REFERENCE (4-byte mStack index, RefCount=1; per
      JITCompiler.AllocateLocalStackSpaces CLR-VT branch), NOT as flat bytes.
      So Box reads that mStack slot and produces an independent shallow copy
      via CLRType.PerformMemberwiseClone (snapshot semantics). CLR primitives
      (flat bytes) are boxed via NeoBoxReturnValue; CLR enums via
      NeoBoxPrimitiveByType + Enum.ToObject. No binder is required for the
      local path (mandatory only for the flat-bytes array/param representation
      deferred to Step 13b), so structs-with-refs-and-no-binder WORK for
      locals -- no Step-13b NIE here.
- [x] 2.2 Implement the CLR Unbox path: CLR primitives/enums unbox into the
      dest flat-bytes slot by value (NeoWritePrimitiveToFrame); CLR structs
      copy the boxed struct into an independent boxed instance for the dest
      local (PerformMemberwiseClone). Null-source -> NullReferenceException
      (handled in the shared arm above the branch).
- [x] 2.3 Implement the CLR Initobj path: CLR primitives/enums -> InitBlock 0
      of the sized slot; CLR structs -> CreateDefaultInstance() boxed into the
      dest mStack ref slot; CLR reference-type local -> null index (-1).
- [x] 2.4 SKIPPED (justified deviation). The design's Neo ValueTypeBinder
      helpers (BoxFromFrame/AssignToFrame/ZeroFrame on byte*+mStack) are NOT
      needed: CLR struct locals are boxed references, not flat bytes, so the
      binder has no frame byte/ref-slot mapping to perform for this pass. The
      binder becomes relevant only when area 5 (Step 13b) introduces the
      flat-bytes representation for CLR struct array elements / by-value
      params. Adding dead binder helpers now would be unused code; deferred to
      13b where they will be exercised. (Legacy binder methods untouched.)
- [x] 2.5 The WITH-binder path is implicitly handled: the boxed-reference
      representation does not branch on binder presence for local box/unbox
      (PerformMemberwiseClone works uniformly). The binder property is still
      consulted indirectly via CreateDefaultInstance / PerformMemberwiseClone.
      Verified with TestVector3 (which has a registered binder).
- [x] 2.6 Added tests: NeoTestClrStructNoBindingBoxRoundTrip (CLR struct
      without binder, TestVector3NoBinding) and NeoTestClrStructWithBinder-
      BoxRoundTrip (CLR struct WITH binder, TestVector3). Both assert box
      non-null + copy independence (ReferenceEquals false). Field-value
      round-trip is NOT asserted because Ldfld on CLR struct fields is a
      separate unimplemented concern (Step 6 NIE) outside Step 13's scope;
      this is documented in the test comments. The foreach(List<int>) no-alloc
      goal is deferred to Step 13b (requires CLR struct array elements / area
      5).
- [x] 2.7 Build CLI + TestCases, run full NeoStep smoke. Result: 49/49 green
      (was 41/41 after Step 12b; +8 new Step 13 cases, zero regressions). CLR
      binding tests unchanged -- the call ABI was NOT touched in this pass.

## Phase 3 -- Area 3: constrained. callvirt specialization on a value type

**STATUS: DEFERRED (blocked on prerequisites outside Step 13 scope).** See
planning-context.md section 8 (2026-07-04 implementer pass) for the full
rationale. Summary of the blocker: the end-to-end constrained callvirt on a
value type needs (1) `ldarga`/`ldarga_s` to load the address of a value-type
`this` parameter (UNIMPLEMENTED in ExecuteNeo, Step 6 NIE) -- the byref/address
model is owned by Step 17; and (2) a runtime arm for the `Constrained` opcode
itself, which today has NO ExecuteNeo handler and is re-appended AFTER the
callvirt in the instruction stream (JITCompiler.cs:1766-1772), so it cannot
inform the callvirt it prefixes. Making Area 3 work is therefore a substantial
JIT+interpreter change (a Constrained runtime arm that boxes-then-dispatches,
or JIT-time lowering that bakes the box into the callvirt) plus the ldarga
address model -- firmly Step 17 territory. No existing green NeoStep test
exercises constrained. today, so deferring incurs ZERO regression risk (the
41 prior + 8 new Step 13 box/unbox cases are all green). Area 3 will land with
Step 17 (byref) which provides the address model, or as a dedicated follow-up.
The design's D3 case analysis (box-once for inherited, direct-call for
declared) remains the blueprint; it was not deleted, only deferred.

- [ ] 3.1 (deferred) Read the JIT `Code.Callvirt` constrained handling
      (JITCompiler.cs:1700-1800, esp. 1709-1773) and the `Constrained` opcode
      translation (2168). Confirm where `hasConstrained` and the constrained
      type token are available. -- DONE during research (findings in §8); the
      Constrained op is re-appended after the callvirt (not before), which is
      the root structural blocker.
- [ ] 3.2 (deferred -- blocked on ldarga/Step 17 + Constrained runtime arm)
      Implement the value-type specialization.
- [ ] 3.3 (deferred) Confirm the `Callvirt_Interface` exclusion
      (Optimizer.Neo.cs:574-577, Step 11) still holds. -- Unchanged (no Area 3
      code landed, so the exclusion is byte-identical to Step 11).
- [ ] 3.4 (deferred -- blocked) Add constrained. tests.
- [ ] 3.5 (deferred) Build + smoke. -- The full NeoStep smoke IS green after
      areas 1-2 (49/49); Area 3 added no code, so nothing to smoke here.

## Phase 4 -- Finalization

- [x] 4.1 Re-run the full NeoStep smoke one more time; record the final green
      count. Result: **49/49 green** (was 41/41 after Step 12b; +8 new Step 13
      box/unbox cases, zero regressions). Area 3 deferred (no code added).
- [x] 4.2 Confirm CLI Debug_Neo builds with 0 errors and TestCases Debug
      builds with 0 errors. (Both confirmed, 0 errors.)
- [x] 4.3 Update planning-context.md section 8 with implementer findings
      (deviations from design D2, Area 3 deferral rationale, final smoke
      count). Done -- see the 2026-07-04 implementer entry below.
- [x] 4.4 Confirm no Legacy (`ExecuteR` / `USE_OLD_OBJ_MODEL`) path was
      touched; all new code is `#if ENABLE_NEO_MODE`. Confirmed: only
      ILIntepreter.Neo.cs (entirely under the file's outer `#if
      ENABLE_NEO_MODE`) was changed in the runtime; Legacy binder methods and
      ExecuteR are untouched.

---

## DEFERRED -- Step 13b (NOT in this pass; do not implement here)

These are recorded so they are not lost. They will be a SEPARATE change
(`implement-neo-step13b`) that MODIFIES the `neo-value-types` capability.

- [ ] (13b) Area 4: Binding codegen overhaul -- introduce `Unsafe.Unbox<T>`
      in-place + direct-call mode; eliminate the Legacy `WriteBackInstance` /
      `StackObject*` writeback from the Neo redirect path
      (`RedirectionNeo`/`*_Neo` generated in `Runtime/CLRBinding/`).
- [ ] (13b) Area 5: CLRMethod unified Neo param layout -- route CLR struct
      params through `AllocateNeoCallParamSlot` (Optimizer.Neo.cs:748),
      read via non-generic `ReadNeo*` helpers by actual slot width; REMOVE the
      caller-temp-slot fallback at Optimizer.Neo.cs:646-655. Cover CLR struct
      by-value params, return values, instance-method `this`, generic CLR
      struct params.
- [ ] (13b) Fix Step 12b K2 bug (VT-by-value CLR param) as a natural
      consequence of area 5 (see design.md D4). Add the deferred
      NeoTestVtPassedByValueToMethod-style test. Reference this design's D4
      as the K2 closure point in the 13b proposal.
