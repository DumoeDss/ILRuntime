# Review Report — neo-il-instance-clr-base-field

**Reviewer:** author != verifier gate (dispatched leaf reviewer, report-only)
**Change:** child 9 of the `neo-overhaul` portfolio
**Branch:** `features/object-model-overhaul`
**Mode:** dispatched (no auto-fix, no subagents, no commits)

## Scope

Two-file change:
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` — raw `Ldfld` (CLR ref-type owner `else`, ~3749) and raw `Stfld` (~3923): the `if (target is ILTypeInstance || target is CrossBindingAdaptorType)` sub-branch, which previously threw the Step-tagged NIE "IL-instance owner with a CLR-base field is deferred", now unwraps the owner to its `ILTypeInstance` and reads/writes the field on `il.CLRInstance` via the existing `NeoReadClrObjectField` / `NeoWriteClrObjectField` helpers.
- `TestCases/NeoStepIlClrBaseFieldTest.cs` (new) — `NeoStepIlClrBaseHolder : ClassInheritanceTest` + TC1 round-trip + TC2 read-default.

Diff stat (code): 34 changed lines in `ILIntepreter.Neo.cs` (one file). The other working-tree modifications (Dependencies `.pdb`, `planning-context.md`) are unrelated noise, not part of this change's intent.

## Verdict: APPROVE

The implementation is correct, minimal, Neo-gated, and faithfully realizes the proposal/design/spec. Every claim independently re-verified below. No Blocker, Major, or Minor findings. One Trivial observation recorded as accepted-known.

## Correctness verification (data path + unwrap + helper reuse)

**The data path is right.** A CLR-base field on an IL instance is NOT in `Primitives`/`ManagedObjects`; it lives on `ILTypeInstance.clrInstance` (`.CLRInstance`). Verified at source:
- `ILTypeInstance.cs:333` `public object CLRInstance { get {...} set {...} }`.
- `ILTypeInstance.cs:368` — for `FirstCLRBaseType is CrossBindingAdaptor`, `clrInstance = adaptor.CreateCLRInstance(appdomain, this)`; the wrapped Adaptor IS-A the CLR base, so the base's fields are real CLR fields on it.
- Legacy twin confirmed: `ILTypeInstance.cs:405/450` `clrType.GetFieldValue(index, clrInstance)` and `:496/533/661` `clrType.SetFieldValue(index, ref clrInstance, ...)` — the read-indexer / `AssignFromStack` CLR-inherited `else`. The Neo handler routes through the same `clrInstance` target, byte-identical in effect.

**The dual-owner unwrap is correct.** `ILTypeInstance il = target as ILTypeInstance ?? ((CrossBindingAdaptorType)target).ILInstance;` The outer `if` guarantees `target is ILTypeInstance || target is CrossBindingAdaptorType`. `CrossBindingAdaptorType` is a public interface (`CrossBindingAdaptor.cs:12`) exposing `ILTypeInstance ILInstance { get; }` (`:14`), so the `is` test and the cast are both valid. If the slot holds the IL instance directly, `as` returns it and `??` short-circuits; if it holds the Adaptor wrapper, `as` yields null and `??` takes `.ILInstance`. Both owner representations covered (spec scenario "Unwrap both owner representations" satisfied).

**The helper reuse is correct.** `NeoReadClrObjectField` / `NeoWriteClrObjectField` (`ILIntepreter.Neo.cs:6101` / `6113`) resolve `ct = appdomain.GetType(target.GetType()) as CLRType` and call `ct.GetFieldValue(fieldHash, tmp)` / `ct.SetFieldValue(fieldHash, ref tmp, value)`. For `il.CLRInstance` the runtime type is the Adaptor; `CLRType.GetField` (`CLRType.cs:529-539`), `GetFieldGetter` (`:505-515`), and `GetFieldSetter` (`:517-527`) all recurse via `BaseType`, so the field hash declared on the CLR base resolves from the Adaptor's CLRType up the base chain. Same reflection path the sibling CLR-ref-owner fall-through uses (`:3764` / `:3938`). No NIE on a non-CLR-resolvable target is reachable here: the field's declaring type is a CLR base, so `clrInstance` is always the Adaptor (the planner's "clrInstance == this is impossible for a CLR-base-field scenario" holds).

**Ldfld post-read marshalling preserved.** The restructured `if (ILTypeInstance||CBA) / else if (Array) / else` keeps the `if (fldVal is CrossBindingAdaptorType cba) fldVal = cba.ILInstance;` unwrap (`:3765`) and the dest marshalling — primitive `NeoWritePrimitiveToFrame`, VT `WriteNeoValueType`, ref `mStack.Add`+index (`:3767-3779`) — following all three branches, unchanged. The redundant inline CBA unwrap the design sketched is correctly omitted; the existing post-block unwrap already covers this branch.

**Stfld has no writeback (correct).** `il.CLRInstance` is a reference-type Adaptor (`ClassInheritanceTest` is a class), so `SetFieldValue(hash, ref tmp, value)` does not replace the instance — the `ref` is ILRuntime's defensive value-type convention, a no-op here. Legacy does not write `clrInstance` back either. No `ILTypeInstance` mutation needed (spec scenario "Write a CLR-base field ... without mutating the ILTypeInstance itself" satisfied). The source `value` was already marshalled by field category earlier in the handler (`:3877-3890`).

## Adversarial — Array deferral untouched

Confirmed by grep: the "IL-instance owner with a CLR-base field is deferred" NIE is now **0 occurrences** in `ILIntepreter.Neo.cs`. The separate `target is Array` deferred NIEs are all preserved verbatim:
- Ldfld ref-owner: `:3762` "array-element field read is deferred".
- Stfld VT-owner: `:3909-3910` `mStack[objIdx] is Array` guard + "array-element field write is deferred".
- Stfld ref-owner: `:3936` "array-element field write is deferred".

Only the two intended IL-instance-CLR-base NIEs were replaced. No collateral change to the array-deferral shape (out of scope).

## Gate re-runs (independent)

All builds use `-f net8.0`; CLI built `Debug_Neo --no-incremental`; TestCases built plain `Debug` (never `Debug_Neo`).

1. **Build CLI (Debug_Neo --no-incremental):** 0 errors. **Build TestCases (Debug):** 0 errors.
2. **NeoStep smoke** (`... true NeoStep`): `Ran 328 tests, 0 failed` (baseline 326 + 2 new probes). PASS.
3. **Probe filter** (`... true NeoStepIlClrBase`): `Ran 2 tests, 0 failed` (TC1 + TC2). PASS.
4. **Stash-toggle FAIL-on-HEAD:** stashed only `ILIntepreter.Neo.cs` (tagged NIE returned = 2, helper call = 0), rebuilt CLI, ran probe filter -> `Ran 2 tests, 2 failed` — TC1 faulted on the Stfld NIE (`ExecuteNeo ... line 3913`), TC2 on the Ldfld NIE (`... line 3750`), exactly the two replaced NIEs. Restored via `git stash pop` (helper call = 2, tagged NIE = 0), rebuilt, re-ran -> `Ran 2 tests, 0 failed`. Cycle clean; probes genuinely exercise the new path.
5. **Full-smoke NIE spot check:** ran unfiltered Neo smoke to file (crashed mid-stream at EXIT=139 — the known unrelated `GenericMethodTest` segfault/NRE; counts are pre-crash per the planner note). Grep of the captured log for "IL-instance owner with a CLR-base field is deferred" = **0 occurrences** (was ~5); `InheritanceTest`/`TestCls`/`ClassInheritanceTestAdaptor` ran extensively (1183 hits) before the crash, so the previously-failing inheritance paths no longer throw the tagged NIE.
6. **Legacy-neutral:** statically — `ILIntepreter.Neo.cs` line 1 is `#if ENABLE_NEO_MODE`; the entire file (including `ExecuteNeo` at `:1343` and both edited sites) compiles out of a plain `Debug` build, so Legacy is byte-identical to before. Empirically — plain-Debug CLI + `... true NeoStep`: `Ran 328 tests, 17 failed`; the 17 failures are all pre-existing NeoStep tests (NeoStep6/13/14/15/16/19/20/ClrStaticField) failing under Legacy for unrelated reasons (delegate-adapter missing, divide-by-zero asserts, NIE on Legacy-only paths). Neither new probe (`NeoStepIlClrBase_TC1/TC2`) is in the failure set — both ran and passed under Legacy too, consistent with Legacy having always handled this case via `AssignFromStack`/read-indexer. Count and set == baseline.

## Working-tree hygiene

My stash-toggle stash was popped and dropped (confirmed: `git stash list` shows only the pre-existing `child4-valuetask-blocked-partial` stash). The fix is present in the tree (2 `il.CLRInstance, fieldHash` helper calls). Reviewer-created temp logs removed. Tree left as found.

## Findings

| # | Severity | Finding |
|---|----------|---------|
| 1 | Trivial (accepted-known, non-blocking) | The helper form re-resolves the CLRType from `il.CLRInstance.GetType()` on every access rather than reusing the already-decoded declaring `ct` (the design's "Alternative" `ct.GetFieldValue(fieldHash, clrTarget)` form). This is the deliberate D2 choice for minimal diff and consistency with the sibling CLR-ref-owner branch (`:3764`/`:3938`), which also uses the helper. The helpers are `[AggressiveInlining]` and the path is not hot. No action required. |

No Blocker / Major / Minor findings. No new compiler warnings from the changed lines (all build warnings are pre-existing CS0219/CS0649/CS0414/CS1668 noise).

## Rationale

The data path (`CLRInstance`, not IL-field layout), the dual-owner unwrap (`??` over `ILTypeInstance` vs `CrossBindingAdaptorType`), and the helper reuse (base-chain walk in `CLRType.GetField/GetFieldGetter/GetFieldSetter`) are all verified at source and confirmed by the green stash-toggle + 328/0 NeoStep smoke + 0 tagged-NIE full smoke + 328/17 Legacy-neutral run. The change closes the stated Neo parity gap with the smallest possible diff, touches only Neo-gated code, and leaves the deferred Array shape intact. APPROVE.
