# Tasks — neo-step24-cli-roundtrip-fix

> Status: COMPLETE (implementer; LEAD to commit). All green.

## 1. Root-cause the 4 failing NeoStep24CliRoundtrip cells
- [x] Build the standalone `ilrt_neoc` CLI + CLI + TestCases (Debug_Neo / Debug).
- [x] Run `NeoStep24CliRoundtrip` -> identify the 4 failing cells + exact failure.
      - Cell1 driver: skip `..cctor() (IndexOutOfRangeException)`.
      - Cell3 counts+split: `MethodDefs 6 vs ngen 7`.
      - Cell4 model1==model2: `IndexOutOfRangeException` (positional ngen vs model).
      - Cell5 fresh-body: `IndexOutOfRangeException` (same length mismatch).
      - (Cell2 header: PASS.)
- [x] Confirm all 4 share ONE upstream cause (the `.cctor` compile skip).
- [x] Capture full stack via a temporary skip-trace print in `NeoCompiler.CompileCore`
      -> `Optimizer.LowerNeoOffsets` line 871. Temp print reverted after capture.

## 2. Determine regression vs inherent-partial-ness
- [x] Trace line 871 to commit `4e32dec6` (ret-vt-with-ref-fields, 2026-07-09).
- [x] Confirm causality: Step-24 shipped 2026-07-07 (5/5) BEFORE `4e32dec6`; the
      commit introduced the unguarded `localInfos[retR1].RefOffset` access.
- [x] Confirm the commit author already fixed the SAME bug in `LowerR1` (the
      companion DstOffset stamp) but missed the Operand3 stamp one line above.
- [x] Exonerate V4/V5 additive work: Cell2 (header/version) PASSED; cells threw
      before any comparator ran. NOT a V4/V5 mismatch.

## 3. Fix (Neo-gated, additive)
- [x] Guard the `Operand3` stamp in the `Ret` arm of `Optimizer.LowerNeoOffsets`
      (`Optimizer.Neo.cs:871`) with `retR1 >= 0 && retR1 < localInfos.Length`,
      falling back to `0` (the Operand3 default). Mirrors the existing guard in
      `LowerR1`.
- [x] Confirm `ExecuteNeo`'s `Ret` arm reads `Operand3` only when there IS a
      return value (`ILIntepreter.Neo.cs:3185`) -> the void/phantom case never
      reads it -> default 0 is safe.

## 4. Verify
- [x] `NeoStep24CliRoundtrip`: 5/5 cells PASS (Cell1: 7 methods, 2 templates, 2 types).
- [x] `NeoStep` smoke: 289/289, 0 failed (no regression).
- [x] `NeoStep23Roundtrip`: 15/15 (reuses the same JIT path).
- [x] `NeoStep25LoadExec`: 28/28.
- [x] Legacy-neutral: plain-`Debug` ILRuntime build = 0 errors (fix is inside
      a `#if ENABLE_NEO_MODE`-gated file).

## 5. Document
- [x] `design.md` written (root cause, per-cell analysis, the fix, verification).
- [ ] LEAD: review + commit (the implementer does NOT commit).
