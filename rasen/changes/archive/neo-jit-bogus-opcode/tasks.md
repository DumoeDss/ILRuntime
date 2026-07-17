## 1. Reproduce the garbage opcode (instrumented full smoke)

- [x] 1.1 In `ILIntepreter.Neo.cs` `ExecuteNeo`, temporarily instrument the loop head
      (just after `OpCodeREnum code = ip->Code;`, ~line 1410) and/or the `default:` arm
      (~line 5357) under `#if DEBUG && !DISABLE_ILRUNTIME_DEBUG` to log: declaring method
      display name, body index `(int)(ip - ptr)`, `body.Length`, raw `(int)code`, and the
      full `OpCodeR` field dump (Register1/2/3/4, Operand, OperandLong, Operand2/3/4).
- [x] 1.2 Build the CLI: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental`.
- [x] 1.3 Run the FULL Neo smoke (drop the `NeoStep` filter):
      `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true`
      and capture the instrumented output to a work-dir log (the run NRE-crashes
      mid-stream; capture is pre-crash).
- [x] 1.4 From the log, isolate the minimal triggering method(s) + body index + raw-byte
      dump. Record whether each hit is an **overrun** (`index >= body.Length`) or an
      **in-range garbage slot** (index valid, `Code` out of named range).
      FINDING: every hit is an OVERRUN -- `AsyncAwaitTest/<TestRun2|3>d__N.MoveNext()`
      idx=64/64 (ip == body.Length). rawCode 24/0/2359324 are adjacent-heap bytes read
      past the end (the 2359324 garbage), NOT in-range slots.

## 2. Diagnose the root cause (pick the fix site)

- [x] 2.1 If 1.4 shows an **overrun**: inspect the suspect body's last instruction and the
      branch/switch/Leave targets of the triggering method. Confirm whether it is
      un-terminated (H2) or a target past the end (H1).
      FINDING: body tail `[57]Call/Op=12 [58]Leave_S/Op=64 [59..62]... [63]Ret`. Body ends
      in Ret (H2 un-terminated REFUTED). A Leave-overshoot probe fired:
      `[NEO-LEAVE-OVR] leaveOp=64/64 leaveFromIdx=27` and `=58` -- Leave_S.Operand==64 ==
      body.Length, the post-try/method-exit target NOT decremented after Push-deletion.
- [x] 2.2 If 1.4 shows an **in-range garbage slot**: dump the JIT output for the method
      (`OUTPUT_JIT_RESULT` is already on in `Debug_Neo`) and find which
      `LowerNeoOffsets`/`TypeSpecializeNeoOpcodes` rewrite produced the bad slot, and
      whether a branch mis-target landed `ip` there (H1) or the slot itself is corrupt (H3).
      FINDING: not an in-range slot (overrun). H3 (corrupt in-range slot) REFUTED: the bad
      index is past body.Length; in-range slots always hold JIT-written named opcodes
      (`new OpCodeR()` zeroes to Nop, every Translate arm sets Code) so 2359324 cannot be a
      real in-range Code.
- [x] 2.3 Record the confirmed root cause + the exact file/function/line of the defect in
      the change's work notes. If the dump REFUTES H1/H2/H3 (spec DEFERRED scenario),
      stop -- pin the dump artifacts and do NOT ship a guessed fix.
      CONFIRMED ROOT CAUSE = H1: `Optimizer.Neo.cs FixBranchTargetsAfterRemove` (~1662)
      remaps IsBranching (Operand), IsIntermediateBranching (Operand4), and Switch jump
      tables, but NOT Leave/Leave_S (EH target in Operand, consumed by ExecuteNeo as
      `ip = ptr + ip->Operand` at ~5032 and recorded into finallyEndAddress at ~5027).
      `LowerNeoOffsets` deletes synthetic Push instructions for Call/Newobj with >3
      register params; the deletion shifts indices below the Leave target, which is left
      pointing past the shortened body end -> ip overrun -> garbage Code.

## 3. Fix the root-cause defect (site per diagnosis)

- [x] 3.1 Implement the scoped fix in the confirmed site: most likely
      `Optimizer.Neo.cs` (`FixBranchTargetsAfterRemove` ~1662 and/or the `Push`-deletion
      block ~1204-1215 -- e.g. a target category the helper misses, an off-by-one on the
      deletion index, or an un-remapped `Leave`/`Leave_S` / `Switch` entry). If 2.x
      redirects, fix `JITCompiler.cs` (`TypeSpecializeNeoOpcodes` or branch-target
      resolution) instead.
      FIX: added `bool isLeave = op.Code==Leave || op.Code==Leave_S;` and OR'd it into the
      Operand-remap arm (`if (IsBranching(op.Code) || isLeave) if (op.Operand > removedIndex) op.Operand--;`).
- [x] 3.2 Ensure the fix is gated `#if ENABLE_NEO_MODE` (or lives in already-Neo-only code)
      so Legacy is byte-identical. Do NOT touch Legacy (`ExecuteR`) code paths.
      CONFIRMED: the fix is inside `Optimizer.Neo.cs`, which is `#if ENABLE_NEO_MODE`
      end-to-end; `FixBranchTargetsAfterRemove` has no non-Neo caller.
- [x] 3.3 Rebuild (`Debug_Neo --no-incremental` CLI + `Debug` TestCases) and re-run the
      instrumented full smoke (task 1.3). Confirm the previously-captured garbage-opcode
      hits are gone; iterate 3.1-3.3 if new hits surface until only known named-missing-opcode
      NIEs remain.
      RESULT: re-run instrumented full smoke -> ZERO [NEO-BADOP]/[NEO-LEAVE-OVR] hits. The
      only remaining default-arm entries are `Neo: opcode Ldtoken not yet implemented (Step 6)`
      (named opcode, sibling child neo-ldtoken). No numeric/garbage opcode values remain.

## 4. Add the permanent ExecuteNeo dispatch guard

- [x] 4.1 In `ILIntepreter.Neo.cs` `ExecuteNeo`, add a **bounds check** at the loop head
      (after computing the body index, before the switch): if `(int)(ip - ptr) >= body.Length`,
      throw a precise `NotImplementedException`/`InvalidOperationException` naming the method,
      the offending index, and `body.Length`. Gate `#if ENABLE_NEO_MODE` (the function is
      already Neo-only).
      DONE: `"Neo: ip ran past body end in <method> at index <i>/<len> (...)"` ships in all
      Neo builds (NOT DEBUG-gated).
- [x] 4.2 In the `default:` arm (line ~5357), when `code` is outside the named
      `OpCodeREnum` range, throw a message naming the method, body index, raw `Code`, and
      the operand/register field dump (replacing the generic "Step 6" message for the
      out-of-range case; keep the existing message for in-range-but-unimplemented codes).
      DONE: out-of-range Code throws `"Neo: corrupt opcode <raw> at <method>:<i>/<len> <fields>"`;
      named-but-unimplemented falls through to the existing Step-6 message. Range bound uses
      a cached `NeoOpCodeCount = Enum.GetValues(typeof(OpCodeREnum)).Length` (self-maintains).
- [x] 4.3 Remove the temporary instrumentation from task 1.1 (keep only the permanent
      guard from 4.1/4.2). The guard MUST ship in all Neo builds (NOT DEBUG-only).
      DONE: temp [NEO-BADOP]/[NEO-BODYTAIL]/[NEO-LEAVE-OVR] instrumentation removed (grep
      verifies no markers remain).

## 5. Regression probe

- [x] 5.1 Add a `TestCases/NeoStep*Test.cs` probe exercising the discovered trigger shape:
      a method that makes a call (or `newobj`) with MORE than 3 register parameters
      (exercising the `Push`-deletion path) followed by branch/switch/try control flow,
      whose correct result depends on the opcode stream executing faithfully. Mirror the
      existing `NeoStep*Test.cs` naming + `[ILRuntimeTest]` conventions.
      DONE: `TestCases/NeoStepBogusLeaveTest.cs` (5 TCs: VOID4/VOID6/RET5/RET6/ASN6).
- [x] 5.2 Stash the fix (task 3.1), build, run the new probe, and confirm it FAILS on HEAD
      without the fix (wrong result or the loud guard throw). Restore the fix and confirm
      it PASSES.
      DONE: with the Leave remap stashed (guard kept), `NeoStepBogusLeave_TC_RET5_Return5ArgRef`
      throws `Neo: ip ran past body end ... at index 12/11` (1 test failed). With the fix
      restored it returns 5 (all 5 probe TCs pass). NOTE: the in-range mis-target is
      layout-dependent (post-try code shifts where the un-remapped Leave lands), so TC_RET5
      (whose Leave target lands exactly at the post-deletion body end) is the deterministic
      detector; the others exercise the path and pass with the fix.

## 6. Verify (NeoStep smoke + Legacy-neutral)

- [x] 6.1 Run the NeoStep smoke:
      `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep`
      -- confirm it stays **301/301** (ZERO regressions).
      RESULT: 306 invoked (301 baseline + 5 new probe TCs), **0 tests failed**.
- [x] 6.2 Stash-toggle Legacy proof: build plain `Debug` + run with `useRegister=true` and
      the `NeoStep` filter, confirm the SAME pre-existing Legacy failure set with and
      without this change (Legacy-neutral).
      RESULT: plain `Debug` + useRegister=true + NeoStep filter -> 16 pre-existing Legacy
      failures (NeoStep tests ExecuteR cannot handle; none are the new probe -- all 5 probe
      TCs pass under Legacy). All runtime changes are in fully `#if ENABLE_NEO_MODE` files
      (`Optimizer.Neo.cs`, `ILIntepreter.Neo.cs`), so plain Debug compiles them out ->
      Legacy binary byte-identical to HEAD -> Legacy-neutral by construction.
- [x] 6.3 `git status` to confirm only the intended Neo-only source files + the new probe
      are modified (no accidental TestCases-under-`Debug_Neo` build, no stray debug
      prints). Clean up the temporary instrumentation log from the work dir.
      RESULT: modified `ILIntepreter.Neo.cs` + `Optimizer.Neo.cs`; new `TestCases/NeoStepBogusLeaveTest.cs`.
      No stray debug prints (grep-clean). Temp repro/probe logs removed from the change dir.
