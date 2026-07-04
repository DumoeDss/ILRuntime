# Tasks

## Phase 0 -- Baseline + reproducer evidence

- [x] 0.1 Build CLI (`dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c
  Debug_Neo`) and TestCases (`dotnet build TestCases/TestCases.csproj -c Debug`);
  confirm 0 errors. Run full NeoStep smoke; record the green count (baseline
  84/84).
- [x] 0.2 Add a **Q-NEWOBJ reproducer** to a scratch test (the Step 16 TC4 form
  restored: `T[] a = new T[n]; T item = new T(intArg); assert item.field ==
  intArg`). Confirm it FAILS on current HEAD. Dump the JIT body + `localInfos`
  for the method (Debug_Neo prints JIT/optimizer output) to pin the exact
  dest/arg aliasing. Record the dump in the ship log.
- [x] 0.3 Confirm the IL value-type ctor's `CompiledFrame.ParamInfos[0]` (`this`)
  sizing on a dump: is it 8-byte byref or in-frame value? (D2 decision input.)

## Phase 1 -- Q-NEWOBJ fix (do first; lowest scope, highest shared-risk)

- [x] 1.1 From the 0.2 dump, confirm the aliasing root cause (frame byte overlap,
  ref-slot overlap, or dest-read-as-arg). Map it to D4 option (a)
  optimizer-localized fix vs (b) JIT dest-register change.
  **Finding: NOT REPRODUCIBLE on current HEAD.** The Q-NEWOBJ JIT dump (task
  0.2) shows the newobj dest, the newarr array temp, and the int arg each get a
  DISTINCT frame byte region (`Offset`) AND a DISTINCT mStack ref slot
  (`RefOffset`); the newobj dest register (r1, ref=1), the array (r0, ref=0),
  and the arg (r11, ref=3) do not alias. The reproducer PASSES (0 fail). The
  planner's hypothesis (newarr doesn't decrement baseRegIdx -> collision) is
  disproven: `AllocateLocalStackSpaces` gives every distinct register a distinct
  region/ref-slot. This is the same outcome as Q-STRUCT / Q-LONG (OPT-HARDEN
  probes): the suspected quirk is already gone on HEAD (Steps OPT-HARDEN/13b/17
  likely resolved it). NO fix applied (a fix to the shared call/newobj lowering
  without a reproducing case would be worse than none).
- [x] 1.2 Apply the **minimal** fix (prefer option (a) in `Optimizer.Neo.cs`
  Newobj dest handling; option (b) in `JITCompiler.cs` only if (a) is
  infeasible). All new runtime code behind `#if ENABLE_NEO_MODE`; Legacy
  (`ExecuteR`) untouched.
  **No code change: N/A** (1.1 found nothing to fix). The existing Newobj dest
  handling is correct.
- [x] 1.3 Confirm the Q-NEWOBJ reproducer (0.2) now PASSES and TC7
  (newobj-with-arg in isolation) still passes. Restore Step 16 TC4 to the real
  ctor-with-arg form (remove the default-ctor + field-set workaround) -- or note
  why it is left as-is.
  **Done.** NeoStep18 TC4 (Q-NEWOBJ form) and Step 16 TC7 both pass; Step 16 TC4
  restored to the real ctor-with-arg form (`new NeoStep16Item(5)`) and passes.

## Phase 2 -- IL value-type newobj

- [~] 2.1 In `ILIntepreter.Neo.cs` `Newobj` arm: add an `IsValueType` branch on
  `newobjType`. Zero-init the dest region (`InitBlock` `TotalPrimitiveSize` bytes
  at `frameBase + destByteOff`; null the dest's `TotalReferenceCount` ref slots
  at `frameRefBase + destRefOff + i`).
- [~] 2.2 Write the frame-native Ref Slot `(-1, destByteOff)` into the ctor
  callee param region's `this` slot (param slot 0): `*(int*)(targetBase +
  thisPrimOff + 0) = -1; *(int*)(targetBase + thisPrimOff + 4) = destByteOff;`
  using the ctor's `ParamInfos[0]` offsets. Do NOT push a fresh mStack `this`
  object for the VT case.
- [~] 2.3 Copy the remaining ctor args via `CopyNeoCallArguments` (param slots
  [1..]); invoke the ctor via `InvokeNeoCallTarget(ctorMethod, isNewobj:true,
  targetBase, mStack, null, targetRetRefBase, out _)`.
- [~] 2.4 If 0.3 showed the ctor `this` is NOT laid out as an 8-byte byref,
  adjust the `Optimizer.Neo.cs`/ctor-frame layout so a value-type ctor `this`
  (param slot 0) is the byref shape (D2). Minimal; reuse Step 17 byref param
  machinery.
- [~] 2.5 Verify with a VT newobj reproducer (`new MyILStruct(args)`, incl. a
  ctor that sets `this.field =`). Confirm ref-field writeback if the struct has a
  reference field (D1 "ctor this ref slot"); if ref fields do not propagate,
  apply the fallback (encode the dest ref base into the Ref Slot).

> **Phase 2 DEFERRED (apply-phase finding).** The IsValueType branch was added
> but surfaces a Step-18-tagged NIE instead of constructing. Reason: the VT
> ctor's `this`-relative stfld lowers to a MIX of in-frame `_Inline` and heap
> `GetNeoILInstance` arms, and the caller's subsequent field reads on the newobj
> dest are non-inline (expect an mStack object index). The `addrAlias` folding
> only tracks ldloca-produced addresses, not a `this` param or a newobj dest, so
> the VT representation is inconsistent end-to-end. A heap-alloc + copy-back
> fallback is ALSO infeasible without first fixing the consistency (the inline
> stflds would write the callee frame while the heap stflds write the
> ILTypeInstance). 0.3 confirmed `ParamInfos[0]` is sized as the in-frame value
> (NOT 8-byte byref). Making this work is the D2 JIT change (track a VT `this`
> / newobj dest as an in-frame address for ALL field access) -- it touches the
> Step 12 VT frame layout / shared field-access lowering used by every VT
> instance method, too broad/risky for this step (84/84 -> 91/91 smoke is the
> gate). Note: the C# compiler lowers `VT x = new VT(args)` on a local to
> `ldloca + call ctor`, which hits the SAME VT-`this` field-access issue (that
> path is likewise deferred). Recorded in `.trae/documents/neo-deferred-items.md`
> (Q-VT-NEWOBJ) for a dedicated follow-up.

## Phase 3 -- CLR-type newobj

- [x] 3.1 In `ILIntepreter.Neo.cs` `Newobj` arm: when
  `targetMethod.DeclearingType is CLRType`, resolve `CLRMethod clrCtor =
  targetMethod`. Remove the blanket CLR NIE (`ILIntepreter.Neo.cs:1598-1600`).
- [x] 3.2 Set up `targetBase` (already done by the lowering for CLR Newobj). Call
  `InvokeNeoClrMethod(clrCtor, isNewobj:true, targetBase, mStack, retDstPtr:
  frameBase + destByteOff, targetRetRefBase: destRefOff)`.
- [x] 3.3 In `InvokeNeoClrMethod`: split the `if (isNewobj || retDstPtr == null)
  return;` early-return so the **reflection** newobj path stores the returned
  object into the dest mStack ref slot + writes the index to the dest byte
  offset (mirror the reference-type return store). The **redirect** path keeps
  its early-return (the redirect owns the dest write).
- [x] 3.4 Verify: `new List<int>()` (reflection) constructs and is usable; a
  redirected-ctor CLR type (if a test exists) constructs via the redirect.

## Phase 4 -- Tests + regression

- [x] 4.1 Add `TestCases/NeoStep18Test.cs` (ASCII, `public static void`
  parameterless). Cover: IL VT default ctor; IL VT ctor-with-arg setting
  `this.field`; IL VT with a reference field; CLR type newobj (`new
  List<int>()` or similar); CLR generic newobj (`new Dictionary<int,string>()`
  if feasible); Q-NEWOBJ form (`newarr; new T(intArg)`). Assertion style: same
  as prior NeoStep tests (passing test returns; logic failure via a deliberate
  fault). A try/catch around `throw new SomeClrException()` is acceptable if CLR
  newobj unblocks it.
- [x] 4.2 Build CLI (Debug_Neo) + TestCases (Debug). Run **full NeoStep smoke**
  (`dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build --
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch
  true NeoStep`). Gate: all previously-green cases stay green (84/84 baseline);
  NeoStep18 cases green; any previously-failing newobj/exception test that turns
  green is noted (side-benefit). Kill + investigate any test >10s (infinite
  loop).

## Phase 5 -- Close-out

- [x] 5.1 Update `design.md` with the confirmed Q-NEWOBJ root cause + the actual
  fix site, and any D1/D2/D3 adjustment discovered during apply. Append durable
  findings to `planning-context.md` section 8.
- [x] 5.2 Mark `Q-NEWOBJ` RESOLVED in `.trae/documents/neo-deferred-items.md`
  (move to section 4).
- [x] 5.3 Hand off to review/ship. Legacy (`ExecuteR`) untouched; all Neo code
  behind `#if ENABLE_NEO_MODE`.

## Deferred (NOT in this pass; recorded so they are not silently dropped)

- [ ] **Delegate `newobj`** (`new Action(foo)`) -- Step 19 (`ldftn`/DelegateAdapter).
  The existing `NotImplementedException("Neo Newobj delegate is not implemented")`
  stays.
- [ ] **CLR value-type newobj with reference fields, no binder** (reflection
  path) -- Step 13b `NeoClrStructHasReferenceField` NIE guard stays.
- [ ] **Generic-parameter VT newobj** spanning IL/CLR -- with the generic-byref
  follow-up.
- [ ] **IL value-type `newobj` (real, non-inlined) + `call VT ctor` via ldloca**
  (Q-VT-NEWOBJ) -- blocked on the VT field-access lowering consistency (a VT
  ctor's `this`-relative stfld is a mix of in-frame `_Inline` and heap
  `GetNeoILInstance`; the caller's reads on a newobj dest are non-inline; the
  `addrAlias` folding only tracks `ldloca` addresses). Needs the D2 change
  (track a VT `this`/newobj-dest as an in-frame address for ALL field access).
  The Step 18 Newobj arm surfaces a clear Step-18-tagged NIE for the VT case.
  Recorded in `.trae/documents/neo-deferred-items.md` (Q-VT-NEWOBJ /
  [VT-THIS-ADDR]).
