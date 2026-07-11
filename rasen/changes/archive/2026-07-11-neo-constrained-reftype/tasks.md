# Tasks — neo-constrained-reftype

**Status:** DONE (2026-07-11) -- Gap A applied after Gap B (commit a465f3f0) landed.
Re-producer `CompareIt<string>` passes end-to-end. NeoStep 301/301, NeoStep17 54/54,
stash-toggle + Legacy-neutral all green. NOT committed (LEAD commits). See design.md
"Gap A RESOLVED".

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

## 5. NEXT WORKER -- DONE (Gap B landed in a465f3f0, then Gap A re-applied 2026-07-11)
- [x] **Gap B (blocker, higher leverage): DONE (2026-07-11, commit a465f3f0).** Root
      cause was NOT param sizing -- it was the Neo `Box` opcode mishandling a CLR
      REFERENCE type. See design.md "Gap B RESOLVED".
- [x] **Gap A (re-apply): DONE (2026-07-11).** Applied the READY branch from
      design.md "The fix that works (Gap A)" (the `!IsValueType && thisObjIdx < 0`
      ref-type `this` branch reading the receiver's mStack index via the frame-
      native byref). Eliminated the Step-17 NIE.
- [x] **Found + fixed a SECOND bug in the same path (the F3 ref-region copy loop).**
      Gap A alone left the reproducer with a WRONG result (`"abc".CompareTo("abc")`
      -> 1). Root cause: the box-once path's F3 "accepted-known" ref-region copy
      loop (8bafaaef) was BOTH broken (read `cmap.RefSrc[i]` -- a RefOffset -- as
      a frame byte offset -> garbage) AND destructive (wrote
      `mStack[frameRefBase + RefDst[i]]`, which for the `y` param is the caller's
      OWN object slot -> nulled the very object the callee's primitive index points
      at). Fix: removed the loop entirely; the box-once path now copies only the
      primitive bytes for non-`this` args (exactly like `CopyNeoCallArguments` /
      `Callvirt_CLR`). The IL-VT-with-ref-fields `this` is owned by the direct-call
      path's `constrainedSlot0Seed`, so nothing is lost. See design.md "Gap A
      RESOLVED".
- [x] Reproducer wrappers added to `NeoStep17Test.cs` (3: `ConstrainedRefTypeString`
      / `...StringEqual` / `...GetHashCode`) + 2 helpers (`Step17CompareIt<T>` /
      `Step17HashCodeOf<T>`). The TT-call probe (`x.GetHashCode()` on a generic T)
      is re-enabled and green.
- [x] Verify: NeoStep17 gate held -- **54/54** (51 baseline + 3 new; value-type
      constrained cohorts {a,d,M2,b,K6,K7} green); NeoStep smoke **301/301**
      (298 baseline + 3 new); stash-toggle (HEAD -> 3/3 NIE, fix -> 3/3 PASS);
      Legacy-neutral (plain `Debug`, 0 errors). NOT committed (LEAD commits).
