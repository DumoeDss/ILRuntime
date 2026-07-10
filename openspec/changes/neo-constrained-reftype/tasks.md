# Tasks — neo-constrained-reftype

**Status:** HANDOFF (Gap A ready to re-apply; Gap B is the blocker and must land first)

## 0. Reproducer + confirm the throw on HEAD -- DONE
- [x] Added `Step17CompareIt<T>(T x, T y) where T: IComparable<T> { return
      x.CompareTo(y); }` + the `ConstrainedRefTypeString` / `...StringEqual`
      wrappers to `NeoStep17Test.cs` (the JIT path, NOT Cecil-free -- isolates
      the engine gap from the MethodToken T-identity work).
- [x] Confirmed HEAD throw: `Step 17: constrained.callvirt on a null or
      unsupported constrained type is not handled (box-once no-op / ref-type
      this)` for both ref-type-T wrappers.

## 1. Root-cause the Constrained ref-type `this` (Gap A) -- DONE
- [x] Found the throw site: `ILIntepreter.Neo.cs:~5333` (the `boxedReceiver ==
      null` residual NIE in the box-once `else` branch).
- [x] Confirmed (via diagnostics) the missing branch: for a ref-type T
      (`System.String`, a CLRType, NOT a value type), none of `thisObjIdx >= 0`
      / `ILType IsValueType` / `primitive` / `CLR IsValueType` fire, so
      `boxedReceiver` stays null.
- [x] Measured the data flow: the JIT emits the constrained `this` as a managed
      pointer (`ldarga.s`), so slot-0 source is an 8-byte frame-native byref
      `[-1, byteOff]`; the receiver object's mStack index is at
      `*(int*)(frameBase + thisByteOff)`.

## 2. FIX Gap A (the Constrained ref-type `this` branch) -- DONE (not committed)
- [x] Implemented the narrow branch (see design.md "The fix that works (Gap A)"):
      when `!constrainedType.IsValueType && thisObjIdx < 0`, `boxedReceiver =
      mStack[*(int*)(frameBase + thisByteOff)]`. Verified `boxedReceiver`
      resolves to the correct string object (the NIE is eliminated).

## 3. Discovered the blocker (Gap B) -- DONE (the reason this is a HANDOFF)
- [x] After Gap A, the reproducer fails with a WRONG RESULT (divide-by-zero),
      not the NIE. The `this` is correct but the `y` param arrives null.
- [x] Proved Gap B is NOT Constrained-specific via 7 probes (echo / echo-second
      PASS; object-call / string-call / TT-call / non-constrained-cast /
      constrained all FAIL with NRE). Generic ref-param passing into ANY call is
      broken; the echo (return the param) works.
- [x] Strong evidence for the root: a generic ref-type param T=string is sized
      as an 8-byte frame slot (measured via localInfos), so the call-arm
      primitive copy reads the wrong 4 bytes (the live object is in the slot's
      ref region, not its first 4 primitive bytes). Fix surface:
      `Optimizer.Neo.cs` (NeoCallParamMap builder + AllocateNeoCallParamSlot) /
      `JITCompiler.cs` (AllocateLocalStackSpaces).

## 4. SCOPE decision: PARK (revert) -- DONE
- [x] Reverted both source files to HEAD (Gap A alone makes the engine silently
      wrong for ref-type constrained callvirt -- strictly worse than the clean
      Step-17 NIE). Neo engine back to its clean TODO state.
- [x] Re-verified after revert: NeoStep smoke 293/0; NeoStep17 gate 0 failed;
      Legacy-neutral (plain Debug) 0 errors.

## 5. NEXT WORKER -- OPEN (do Gap B first, then re-apply Gap A)
- [x] **Gap B (blocker, higher leverage): DONE (2026-07-11).** Root cause was
      NOT param sizing -- it was the Neo `Box` opcode mishandling a CLR
      REFERENCE type. See design.md "Gap B RESOLVED". A generic param T flowing
      into an `object`/base-class param emits `box !!T`; specialized to T=string
      this is `box System.String`, an ECMA-identity that the Neo Box handler had
      no arm for (it ran the CLR-struct path `ReadNeoValueType(typeof(string))`,
      reinterpreting the mStack index as a struct -> NRE/OOB). Fix = 1 new
      `else if (!clrBoxType.TypeForCLR.IsValueType)` arm in
      `ILIntepreter.Neo.cs` OpCodeREnum.Box (reads the mStack index, passes the
      object as-is; mirrors Legacy Register.cs:3880-3883). 5 probes added in
      `TestCases/NeoStepGapBProbe.cs` (all PASS; the 3 call-arm probes FAILED on
      HEAD). NeoStep 298/298; Legacy-neutral; stash-toggle verified; value-type
      generic `Echo<int>` unaffected. NOT committed (LEAD commits).
- [ ] **Gap A (re-apply, ready):** once Gap B lands, re-apply the Constrained
      ref-type `this` branch (design.md "The fix that works (Gap A)"). Then the
      `ConstrainedRefTypeString` / `...StringEqual` wrappers pass.
- [ ] Add the reproducer wrappers to `NeoStep17Test.cs` (source currently at
      HEAD; the wrappers + probe battery were validated during the audit and are
      documented in design.md).
- [ ] Verify: NeoStep17 gate held (value-type constrained cohorts green); NeoStep
      smoke green (293 baseline + the new wrappers); stash-toggle; Legacy-
      neutral.
