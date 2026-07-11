# Design — neo-step24-cli-roundtrip-fix

> A follow-up bug-fix change to `neo-step24-ilrt-neoc` (archived 2026-07-07).
> Branch: `features/object-model-overhaul`. Neo-gated. Additive correctness fix.
> Date: 2026-07-11.

## The regression

The Step-24 CLI-roundtrip self-check (`NeoStep24CliRoundtripCheck`, 5 cells)
regressed from **5/5 at ship (2026-07-07)** to **1/5 (4 cells failing)**. All 4
failures traced to ONE root cause: the probe type's empty static `.cctor()`
threw `IndexOutOfRangeException` while being force-compiled by the NeoCompiler
driver.

### Per-cell failure (before the fix)
- **Cell1 driver** — FAIL: driver recorded 1 skip (`TestCases.NeoStep24CliProbe..cctor()
  (IndexOutOfRangeException)`) -> `IsComplete == false`.
- **Cell2 header** — PASS (the only green cell).
- **Cell3 counts+split** — FAIL: `MethodDefs 6 vs ngen 7`. The driver compiled
  only 6 methods (the `.cctor` skip omitted it from the emitted model), but the
  self-check's partition *replication* unconditionally counted the `.cctor` in
  `ngen` (7) -- it does not force-compile, so it does not observe the skip.
- **Cell4 model1==model2** — FAIL: `IndexOutOfRangeException`. The replication's
  `ngen` (7 entries, includes the `.cctor`) was indexed positionally against
  `model1.MethodDefs` (6 entries, `.cctor` omitted) -> out-of-range at `ngen[6]`.
- **Cell5 fresh-body** — FAIL: `IndexOutOfRangeException`. Same `ngen` vs
  `model1.MethodDefs` length mismatch.

So 4 of the 4 failures are the SAME single upstream compile-time bug, not 4
independent Step-24-inherent gaps.

## Root cause (a REAL regression, NOT Step-24 partial-ness)

The crash stack:
```
IndexOutOfRangeException
  at Optimizer.LowerNeoOffsets(...) at Optimizer.Neo.cs:871
  at JITCompiler.RunNeoBackHalf(...) at JITCompiler.cs:715
  at JITCompiler.Compile(...)       at JITCompiler.cs:664
  at ILMethod.InitCodeBody(...)     at ILMethod.cs:916   (triggered by ilm.BodyRegister)
```

Line 871 (the `Ret` arm of the per-opcode lowering switch), introduced by commit
**`4e32dec6` "Neo ret-vt-with-ref-fields: value-type-with-ref-fields return"
(2026-07-09 23:45)** -- TWO DAYS AFTER the Step-24 ship:

```csharp
case OpCodeREnum.Ret:
    if (op.Register1 >= 0)
    {
        short retR1 = ResolveLiveAlias(op.Register1).Reg;
        op.Operand3 = localInfos[retR1].RefOffset;   // <-- UNGUARDED, line 871
        LowerR1(ref op, localInfos);
    }
    break;
```

For a VOID method's `ret`, JIT `Code.Ret` leaves `Register1` at its default **0**
(`hasReturn==false`). So `0 >= 0` is true -> the arm is entered -> `retR1`
resolves to 0 -> `localInfos[0]` on an EMPTY `localInfos` (a static void method
with no params/locals, like the probe's empty `.cctor()`) throws.

The commit's OWN author already hit this exact bug and fixed it -- but only in
`LowerR1` (the `DstOffset` stamp, lines 1473-1481), which documents verbatim:
*"A void method's `ret` ... reaches here with r1 pointing past the (possibly
empty) localInfos -- a phantom register ... Fixes the empty static .cctor
IndexOutOfRangeException (NeoStep24CliProbe..cctor)."* The companion `Operand3`
stamp (the ref-region base) one line above, in the SAME arm, was missed. So the
fix was incomplete: the guarded `LowerR1` is never reached because the
unguarded `localInfos[retR1].RefOffset` on the preceding line throws first.

### Why this only surfaced at Step-24 (and not in NeoStep smoke)
NeoStep smoke methods return real values (non-void), so their `Ret` has a
genuine return register that IS in `localInfos` -> never hit the phantom path.
The Step-24 probe's empty `.cctor()` is the first void/empty method the AOT
driver force-compiles end-to-end. (This also means the regression is broader
than Step-24: at RUNTIME the JIT runs the same path, so ANY void method's `Ret`
would crash on first invocation. The empty `.cctor` is simply the canary.)

### Why it is NOT a V4/V5-additive mismatch
The child-8 V5 (GenericParamNames) and child-12 V4 (local-var metadata) changes
are ADDITIVE serializer/reader table bumps. None of the 4 failing cells failed
on a comparator/version assertion: Cell2 (the header/version cell) PASSED, and
the Step-23 comparators (reused by Cell4) were never reached (the cells threw
before comparison). The V4/V5 work is exonerated.

## The fix (1-line-equivalent, additive, Neo-gated)

Guard the `Operand3` stamp with the SAME bounds check `LowerR1` already uses
(`Optimizer.Neo.cs`, `Ret` arm):

```csharp
op.Operand3 = (retR1 >= 0 && retR1 < localInfos.Length)
    ? localInfos[retR1].RefOffset : 0;
```

`0` is the `Operand3` default and is correct: the `ExecuteNeo` `Ret` arm
(`ILIntepreter.Neo.cs:3185`) reads `ip->Operand3` ONLY inside
`if (retDst != null && (returnPrimitiveSize > 0 || returnRefCount > 0))` -- a
void method has `returnPrimitiveSize==0 && returnRefCount==0`, so the body is
skipped and `Operand3` is never read. Leaving it 0 for the phantom-register
case is harmless and is the exact convention the commit author established in
`LowerR1`.

This is the minimal, surgical fix -- it mirrors an already-shipped, already-
documented guard in the same method, applied to the one site that was missed.

## Verification

- **NeoStep24CliRoundtrip: 5/5 cells PASS** (was 1/5). Cell1 compiles 7
  methods (the `.cctor` now compiles), 2 templates, 2 types.
- **NeoStep smoke: 289/289, 0 failed** (no regression; the 289 baseline holds).
- **NeoStep23Roundtrip: 15/15** (reuses the same JIT path; unaffected).
- **NeoStep25LoadExec: 28/28** (unaffected).
- **Legacy-neutral:** the fix is inside `Optimizer.Neo.cs` (whole file is
  `#if ENABLE_NEO_MODE`-gated); plain-`Debug` ILRuntime build = 0 errors.

## Scope (what this change does NOT do)

- Does NOT touch the Step-24 serializer/comparator/reader (they were correct).
- Does NOT address Step-24's inherent partial-ness (the full-`TestCases.dll`
  CLI compile, the 69 type-skips + 181 method-skips, the V1-BCL-only boundary) --
  that is the known broader Step-24/25 effort, out of scope here.
- Does NOT modify `NeoStep24CliRoundtripCheck` or the probe (the self-check and
  its partition replication were correct; the bug was purely in the JIT
  lowering the driver invoked).
