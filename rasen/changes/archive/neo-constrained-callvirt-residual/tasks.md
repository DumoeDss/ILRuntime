# Tasks -- neo-constrained-callvirt-residual (Wave-2 child)

Target: `RefOutTest.UnitTest_GenericsRefOut2` -- "constrained.callvirt not followed by
callvirt" (Step-17 D-CONSTRAINED defect). Full-smoke delta 35 -> lower.

## Phase 1 -- Re-audit (DONE)
- [x] Build CLI (`Debug_Neo --no-incremental`) + TestCases (`Debug`) with
      `UseSharedCompilation=false`. 0 errors each.
- [x] Confirm name-filter: `GenericsRefOut2` FAILS on Neo with
      `Step 17: Constrained not immediately followed by a callvirt` @ Neo.cs:6957.
- [x] Confirm `GenericsRefOut2` PASSES on Legacy (`Debug` + `useRegister=true`):
      `Ran 1 tests, 0 failded`.
- [x] Dump the JIT for `Read<T>(ref T dest)` (the failing caller). Final body:
      `10: push r0; 11: constrained TestGenrRef; 12: call r2, TestGenrRefBase.GetString()`.
      The trailing op is a plain `Call` (OpCodeREnum.Call), NOT a Callvirt.
- [x] Pin root cause: `GetString()` is a NON-VIRTUAL ILMethod on `TestGenrRefBase`
      (a class). C# emits `constrained. T; callvirt GetString` for a generic `T`
      (resolution depends on T's kind). The Neo JIT (JITCompiler.cs:2845-2850) lowers
      a callvirt on a non-virtual, non-abstract ILMethod whose declaring type is not
      an interface from `Callvirt` to `Call` (`op.Code = OpCodeREnum.Call`), with
      `hasConstrained=true` so `op.Operand4 = 1` and the Constrained op is reordered
      to precede the Call. The Neo `Constrained` arm (ILIntepreter.Neo.cs:6950-6958)
      accepts only `{Callvirt, Callvirt_IL, Callvirt_CLR, Callvirt_Interface,
      Call_Redirect}` as the trailing op and throws on a plain `Call`. Legacy
      (`ILIntepreter.Register.cs:3898`) does NOT inspect the trailing op -- it only
      prepares the receiver (box/value-type deref) and lets the next instruction's own
      arm (Call OR Callvirt) do the dispatch. So Legacy accepts a Call after constrained.
- [x] Confirm the trailing `Call` carries every field the Constrained arm reads from
      `cv = ip+1`: `Operand2` (method token, set by InitializeFunctionParam:3714),
      `Operand` (NeoCallParams index, set by Optimizer.Neo.cs:1459 -- the `case Call:`
      IS in the param-map case-list at :1216), `Register1`/`DstOffset`/`Operand3`
      (return slot, set at LowerR1). Identical to a Callvirt. => adding `Call` to the
      accepted-list is sufficient; the Constrained arm does its OWN dispatch
      (InvokeNeoClrMethod/InvokeNeoCallTarget on the boxed receiver) and skips the
      trailing op via `ip += 2`, so no Call-arm interaction occurs.
- [x] Confirm full-smoke baseline = `Ran 932 tests, 35 failded, 20 ignored, 7 todos`
      (exit 0, graceful).

## Phase 2 -- Implement + verify (DONE)
- [x] Add `OpCodeREnum.Call` to the Constrained arm's accepted-trailing-op list
      (ILIntepreter.Neo.cs:6966). Neo-gated by the enclosing `#if ENABLE_NEO_MODE`.
      +1 functional line + a documenting comment, 0 removed.
- [x] Name-filter verify: `GenericsRefOut2` PASS after fix (`Ran 1 tests, 0 failded`).
- [x] Stash-toggle (airtight): drop the `Call` term -> rebuild ->
      `GenericsRefOut2` FAIL (NIE returns, `Ran 1 tests, 1 failded`) -> restore ->
      rebuild -> PASS.
- [x] Full smoke (truth): delta `35 -> 34`. The single flip is `GenericsRefOut2`
      (the target); the remaining 34 failures are byte-identical to baseline (0
      regressions, verified by diffing the `Test name:` lists).
- [x] NeoStep broad-green: `398/0` (no regression).
- [x] Legacy-neutral: plain `Debug` build 0 errors; `GenericsRefOut2` PASS on Legacy
      (`Ran 1 tests, 0 failded`). Change is `#if ENABLE_NEO_MODE`-gated.

## Files
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`
  (`case OpCodeREnum.Constrained:` accepted-trailing-op list: +1 enum member).
- `rasen/changes/neo-constrained-callvirt-residual/design.md` (this change).
