# Tasks — neo-vt-field-orchain-compare

> A focused Neo interpreter correctness follow-up. All tasks DONE.

- [x] 1. Isolate the bug to a minimal reproducer (NOT the array). Wrote
      `TestCases/NeoStepOrChainTest.cs` with plain-struct, multi-struct, and
      array-element `||`-chain shapes. Confirmed: plain-struct passes on HEAD;
      the array-element multi-cell form FAILs on HEAD; single compares pass.
- [x] 2. Dump the JIT for the `||` chain + find the divergence. Instrumented
      `op_Inequality` ENTER/redirect/CALLPOST + `Brtrue` in `ExecuteNeo`. Trace
      proved: the compare is correct (result=False, 4-byte bool written); the
      `brtrue` reads an 8-byte slot (`Operand2==8`) and sees stale high bytes
      (`offDstLong=0x0000000400000000` from the reused register's prior
      array-element use) → mis-fires.
- [x] 3. Root-cause the 8-byte read. `Optimizer.Neo.cs` sets
      `Brtrue`/`Brfalse` `op.Operand2 = localInfos[r1].Size`; every temp slot
      is sized to the method's MAX VT size (`maxSize >= 8`). A bool/int32
      producer writes only the low 4 bytes, leaving stale high bytes that the
      8-byte `brtrue` read picks up. Systematic latent bug (tests passed by
      luck — clean high bytes).
- [x] 4. Fix it (Neo-only, Legacy-neutral). `ILIntepreter.Neo.cs`
      `Brtrue`/`Brfalse` arms now read only the low int32 (the CIL truth value
      is always int32; Roslyn lowers long/float/object truthiness to a compare
      whose int32 result reaches the branch). Dropped the
      `Operand2 == 8 ? *(long*)` path. Fix is inside `#if ENABLE_NEO_MODE`;
      Legacy `ExecuteR` branch arms (typed-slot model) untouched.
- [x] 5. Verify. `NeoStepOrChain` 10/10 with the fix. `NeoStep` smoke
      **289/0/0** (279 baseline + 10). `NeoOptHardening` 24/0/0 (unchanged).
- [x] 6. Stash-toggle. `NeoStepOrChain` is 8/10 on HEAD (the two array
      multi-cell probes FAIL); 10/10 with the fix. The plain-struct + single-
      compare controls pass on both.
- [x] 7. Legacy-neutral confirm. Plain-`Debug` CLI builds 0 errors; fix is
      Neo-gated; Legacy `ExecuteR` untouched.
- [x] 8. Remove all temporary instrumentation (ENTER/redirect/CALLPOST/BRTRUE
      diagnostics + `System_String_Binding.cs` redirect debug line +
      `orchain-dbg.log`).
