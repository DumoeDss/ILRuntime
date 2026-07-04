# Ship Log -- implement-neo-step17 (Neo Step 17: byref / Ref-Slot)

- **Change:** `implement-neo-step17`
- **One-line:** Unified 8-byte Ref Slot + byref model for the Neo register VM
  (`ldloca`/`ldflda`/`ldarga`/`ldelema` producers, `stind`/`ldind`/`stobj`/
  `ldobj` consumers, IL `ref`/`out` call ABI, `addrAlias`-COEXIST gate) plus
  Step-17-tagged NIEs for the deferred sub-cases.

## Verdict: CLEAN (after review-loop round 1 fixed Blocker B1)

## Verification evidence

- **Builds, 0 errors:**
  - `ILRuntimeTestCLI` Debug_Neo -- 0 errors.
  - `TestCases` Debug -- 0 errors.
  - Legacy plain-`Debug` builds clean (Neo-only change; Legacy untouched).
- **FULL NeoStep smoke:** 81 ran / 0 failed. +9 NeoStep17 (incl. TC8 the B1
  regression case + TC9 the Stind_Ref test). Steps 12-16 and TC1-7 preserved,
  0 regression.
- **B1 regression (TC8):** FAIL-on-HEAD / PASS-after the fix (the addrAlias
  under-gate was a real silent-corruption defect; TC8 proves it).
- **Non-author re-review of B1 fix:** 3 adversarial probes pass; no new hole
  confirmed.

## Review summary

- **Blocker B1 (fixed, review-loop round 1):** addrAlias under-gate silent
  corruption on register reuse. Root cause was deeper than the reviewer's
  initial direction: a static addrAlias with last-write-wins semantics plus
  branch / comparison operands wrongly resolved through the alias map. Fixed
  via a per-instruction `liveAliasMap` snapshot, an escape-gate, and a
  branch/Initobj split. See the postmortem note below.
- **M1:** CLR-object `stind`/`ldind` (field-hash) tagged NIE (deferred to
  Step 13b).
- **M2:** TC9 Stind_Ref test added.
- **M3:** `stobj`/`ldobj` spec narrowed -- ref-slot copy loop deferred;
  primitive-field value types are the green target this step.
- **M4:** `constrained.` requirement reconciled -- PARTIAL/DEFERRED this step
  (loud tagged NIE; full VT dispatch needs callvirt-byref-`this`, -> Step 13b).

## Delivered scope

- 8-byte Ref Slot (`objectIndex`, `offset`): frame-native (`-1`, abs offset) and
  mStack-object (`mStackIdx`, byte offset) forms; byref-typed slots sized 8
  bytes / RefCount 0 on both the frame allocator and the call-param layout
  helper.
- Address producers: `ldloca`/`ldloca_s`, `ldarga`/`ldarga_s`, real `ldflda`
  arm (in-frame-VT vs heap-IL discriminated), `ldelema` (IL VT array element
  address -- resolves D-LDELEMA).
- Indirect consumers: `stind_*`, `ldind_*`, `stobj`, `ldobj` (frame-native +
  `ILTypeInstance` pinned; `stobj`/`ldobj` copy `TotalPrimitiveSize` bytes;
  ref-slot loop deferred -- primitives only this step).
- IL `ref`/`out` call ABI: `IsByRef` branch, one 8-byte primitive-copy entry,
  no ref entry; mutations propagate to the caller.
- `addrAlias` COEXIST gate: keep folding for pure
  `ldloca;ldflda;stfld/ldfld` patterns; remove a dest from folding when any
  consumer is a byref-escape opcode (`stind_*`/`ldind_*`/`stobj`/`ldobj`/
  `ldelema`/byref `Call`/`Newobj`/`Push` arg/`constrained.` box path).
- K1 ldloca-kill (FCP) extended to `ldflda`/`ldarga`/`ldelema` as escapes.
- `constrained.` runtime arm present as a loud Step-tagged NIE.

## Deferred (accepted-known)

- `constrained.`-on-VT full dispatch (callvirt byref-`this`) -> Step 13b.
- CLR-object `stind`/`ldind` field-hash -> Step 13b.
- CLR-method `ref`/`out` params -> Step 13b (closes K2 / K2-FAM).
- `stobj`/`ldobj` ref-slot copy loop -> future.
- generic-byref / `fixed` / explicit-interface-byref /
  interface-on-VT-constrained -> future.

## Postmortem note -- the addrAlias COEXIST gate (Blocker B1)

Step 17 added a genuine byref path, which made the existing `addrAlias`
folding (Step 12-16's in-frame-VT fast path) conditionally unsound: a dest
folded to a compile-time offset must NOT also be realized as a Ref Slot that
escapes. The first cut gated folding only on the dest's own consumers and
used a single static alias map, which silently corrupted state when (a) a
register was reused across a folding window, or (b) a branch / comparison
operand was resolved through the alias map. The fix is three-fold:

1. **Per-instruction `liveAliasMap` snapshot** -- alias resolution is no
   longer last-write-wins across the whole body; each instruction sees the
   aliases live at its point.
2. **Escape-gate** -- any consumer that escapes the folding window (a
   byref-escape opcode) removes the dest from folding so its non-foldable
   consumers read the real Ref Slot, while foldable consumers keep their own
   folded offsets (they never referenced the dest at runtime).
3. **Branch / Initobj split** -- branch and comparison operands, and
   `Initobj`, are no longer resolved through the alias map; they read the
   slot directly.

The COEXIST contract the gate enforces: a given `ldloca`/`ldflda` dest is
either folded (compile-time offset, runtime arm dead) or realized as a real
Ref Slot, never both. TC8 is the regression that pins this.

## Files changed (this change's source scope)

- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs`
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.FCP.cs`
- `TestCases/NeoStep17Test.cs`

## Git note

Source is uncommitted (LEAD stages commit/push; this worker ships + archives
only). The LEAD commit scope is EXACTLY: the five source files above, the
`openspec/` artifacts for this change (incl. this ship-log and the archive
move), and the deferred-items doc (`.trae/documents/neo-deferred-items.md`).
The LEAD commit does NOT include the `.pdb` / `.gitignore` repository churn
visible in `git status` (pre-existing, unrelated).
