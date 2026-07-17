# Review Report: neo-raw-ldfld-stfld-clr-struct-seeding (child 21)

**Reviewer:** independent (reviewer-1). **Author != verifier.**
**Branch:** `features/object-model-overhaul`. **Change dir:**
`rasen/changes/neo-raw-ldfld-stfld-clr-struct-seeding/`.
**Reviewed diff:** uncommitted working-tree change to
`ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` (+ the new untracked
`TestCases/NeoStepFloatSeedingProbe.cs`).

## Scope reviewed

Three additive sites under `#if ENABLE_NEO_MODE` in
`TypeSpecializeNeoOpcodes` (+ one helper), all in `JITCompiler.cs`:

1. New `case OpCodeREnum.Ldfld:` (~:1089) -- resolves the raw-Ldfld CLR owner
   + field and seeds `registerTypes[op.Register1]` with the field's primitive
   type.
2. Call trailing primitive-ReturnType seed (~:1311-1329) -- after the existing
   stale-VT / stale-ref if/else-if chain.
3. `NeoClrPrimitiveTypeToIType` helper (~:1581).

## Verification performed (independent re-runs)

- Built `ILRuntimeTestCLI` (`-c Debug_Neo --no-incremental`) and `TestCases`
  (`-c Debug`): 0 errors each.
- **With fix, full NeoStep smoke:** `Ran 358 tests, 0 failded, 0 ignored, 0
  todos`. No regressions.
- **FAULT-on-HEAD (stash-toggle of JITCompiler.cs only, rebuilt CLI, ran the 4
  probes):** `Ran 4 tests, 4 failded` -- all 4 throw
  `DivideByZeroException` at the deliberate `1/0` guards
  (`NeoStepFloatSeedingProbe.cs:65/79/98/113`). Confirms the probes are
  observably-wrong (FAULT), not merely wrong-value.
- **JIT Final Results with fix** confirm the typed specialization now fires:
  - TC1: `call r7, GetOneF()` -> `addi.r4` (was plain `addi` on HEAD).
  - TC2: `call r7, GetOneD()` -> `addi.r8`.
  - TC3: `ldfld r7, r0, 0x...` (RAW ldfld, CLR owner) -> `muli.r4` -> `addi.r4`.
  - TC4: two raw `ldfld`s -> `add.r4` (binary form).
- **Additive check:** `git diff --numstat` = `71 0` (71 added, 0 removed).
- **Hash-decode parity:** the new raw-Ldfld seeding case decodes
  `typeHash = (int)((ulong)op.OperandLong >> 32)` / `fieldHash = (int)op.OperandLong`
  -- byte-identical to the ExecuteNeo runtime handler
  (`ILIntepreter.Neo.cs:3878-3886`) and uses the same `AppDomain.GetType` +
  `CLRType.GetField(fieldHash)` path.
- **No conflicting case:** the only other `case OpCodeREnum.Ldfld:` in the file
  (:367) is in a different method (`GatherValueTypes`, a VT-gathering pass for
  temp-slot sizing); it cannot conflict with the seeding switch.

## Findings

### Dimension 1 -- Call arm seeding correctness: SOUND

The trailing seed sits inside the existing `if (op.Register1 >= 0)` block,
after the stale-VT / stale-ref `if/else if` chain, and is gated by
`rtr != null && rtr.IsPrimitive` after nulling ByRef returns. Verified each
sub-claim:

- (a) void / null ReturnType: `cmr == null` -> `rtr = null` -> skip. A non-null
  `VoidType` has `IsPrimitive == false` -> skip. Dest stays unseeded.
- (b) ByRef return: `rtrIl.IsByRef` -> nulled -> skip.
- (c) by-value IL-VT return NOT clobbered: an IL-VT return has
  `IsPrimitive == false` (ILType.IsPrimitive is hard-coded `false`,
  `ILType.cs:1935`) -> seed skipped. The stale-VT branch's kept in-frame-VT type
  is preserved. (See pre-existing note P1 below for a fresh-temp IL-VT-return
  gap that is NOT introduced here.)
- (d) I4 return: seeds `IntType`. `InferPrimTag(IntType)` = I4 = the prior
  `null -> I4` default, so specialization is identical. `IntType` is a CLRType
  (not ILType), so it cannot be mistaken for an in-frame VT by any
  `is ILType`-keyed discriminator.

The trailing seed is strictly more correct than HEAD for primitive returns: on
HEAD a primitive-returning call to a dest reused from a *different* primitive
left the STALE type in place (no clear in the ELSE branch); now it is
re-seeded to the correct type. No test relied on the stale behavior (358/0).

### Dimension 2 -- raw Ldfld arm correctness: SOUND

- Only a raw `Ldfld` (CLR declaring type) reaches the case; the typed
  `Ldfld_R4/R8/I8` arms keep their own cases. Confirmed by the runtime handler,
  which throws NIE if a raw `Ldfld`'s declaring type is not a CLRType
  (`ILIntepreter.Neo.cs:3882-3883`) -- i.e. raw `Ldfld` is CLRType-only by
  construction.
- `declType is CLRType ct` pattern: null / ILType owners safely skip (no throw
  at JIT time -- correct: a seeding miss must not NIE; the runtime will catch a
  genuine error).
- `ct.GetField(fieldHash)` null -> skip.
- Seeds ONLY primitives via the helper (float/double/long live; int/other
  primitive -> IntType == prior default; non-primitive -> null, no seed).

### Dimension 3 -- registerTypes reuse / phi-merge: PRE-EXISTING, accepted

See P2. The seed lives within the same single-pass dataflow envelope as every
other seed (`Ldc_*`, `Ldfld_R4`, `Ldind_R4`, ...). Straight-line
produce-then-arith is reliable; cross-block reuse is the known imprecision. Not
introduced or broadened here.

### Dimension 4 -- `NeoClrPrimitiveTypeToIType`: CORRECT and complete

Mapping is correct: `float -> FloatType`, `double -> DoubleType`,
`long`/`ulong -> LongType`, other-primitive -> IntType, non-primitive -> null.
`null` is the right non-seed sentinel (dest stays at the I4 default).

### Dimension 5 -- Additive safety: CONFIRMED

`71 0` (added / removed). No existing case label or clear-logic was modified.
The new `case Ldfld:` is a fresh label; the Call seed is appended inside the
existing guard. `SetRegisterType` is itself bounds-guarded.

### Dimension 6 -- Probe strength: STRONG

All 4 FAULT on HEAD (DivideByZero, independently confirmed via stash-toggle)
and PASS after (358/0). Assertions run through the host helper
`TestCLRBinding.SumTestVector3Fields` (CLR-side float arithmetic), correctly
sidestepping the out-of-scope `conv.i4`-float-bit-reinterpret bug. The two
`NeoStepGetOne*` helpers are genuinely non-inlined (try/catch ->
`hasExceptionHandler` -> `canInline=false`): the Final Results show the
materialized `call` op, not an inlined body.

### Dimension 7 -- Regression surface: ADEQUATE

The change is narrowly scoped (seeds primitives only, for two producer
families). 358/0 across EH / byref / VT / box / arithmetic / float. One
uncovered shape: CLR-callee float return -- see Minor M2.

---

## Findings by severity

### Blocker
(none)

### Major
(none)

### Minor

- **M1 -- `appdomain.GetMethod(op.Operand2)` not hoisted (deviates from design
  D2).** The design explicitly recommended hoisting the resolution (the
  pre-existing code already called it up to 2x). The implementer added a THIRD
  call (`cmr`) rather than reusing the `cm` / `cm2` already resolved in the
  stale-VT / stale-ref branches. Because those branches are `if / else if`
  (mutually exclusive) and the trailing seed runs unconditionally, `GetMethod`
  is now invoked up to twice per Call op. This is JIT-time (once per method,
  cached) so the perf cost is negligible, and it is purely a redundant-token-
  lookup (no correctness impact -- `GetMethod` is deterministic/idempotent).
  Still, it is a spec deviation. Recommend hoisting `cmr` to the top of the
  `if (op.Register1 >= 0)` block and reusing it in all three places.

- **M2 -- Open Question O1 (CLR-callee primitive return) is unresolved.** The
  design flagged that `appdomain.GetMethod(op.Operand2).ReturnType` for a
  `Callvirt_CLR` / `Call_Redirect` callee returning `float`/`double`/`long`
  should be spot-checked, and recommended adding a probe. The IL-callee path
  (the load-bearing one) is confirmed by the re-audit probes. The CLR-callee
  path is NOT covered by any probe. Worst case: if resolution returns null or a
  non-primitive IType for such a callee, the dest stays unseeded -- which is
  byte-identical to HEAD (**no regression**, the bug merely persists for that
  shape). This is a coverage gap, not a defect. Recommend a follow-up probe
  (e.g. a CLR binding / redirect method returning `float`, used in `addi`)
  either to confirm it works or to extend the resolution (e.g. via
  `CLRMethod.ReturnType`).

### Trivial

- **T1 -- `ulong` mapped to `LongType` (I8), not `ULongType` (U8).**
  `NeoClrPrimitiveTypeToIType` maps `ulong` to `appdomain.LongType`. For
  `add`/`sub`/`mul` the I8 and U8 results are bit-identical, so this is
  correct for the common arithmetic; only `div`/`rem`/signed-comparison
  semantics would differ. This is a deliberate scoping decision (the design
  scopes the live fix to float/double/long) and, critically, it is a STRICT
  IMPROVEMENT over HEAD: before the change a `ulong` CLR field loaded via raw
  `Ldfld` was `null -> I4` (totally broken width -- 8-byte value treated as
  4-byte); after, it is `I8` (correct width). Not a regression. Optional: map
  `ulong` to `ULongType` and `uint`-style fields to their unsigned equivalents
  for full precision if desired.

- **T2 -- hash-decode cast form differs from a sibling pass (cosmetic).** The
  new seeding case uses `(int)((ulong)op.OperandLong >> 32)` (matching the
  runtime handler), while `GatherValueTypes` (:371) uses
  `(int)(code.OperandLong >> 32)`. For the raw-Ldfld encoding the `(ulong)`
  cast is the more correct form (avoids sign-extension when the high bit of
  `typeHash` is set). No defect; just noting the two passes differ and the new
  code chose the better form.

- **T3 -- typo in CLI runner output (`0 failded`).** Pre-existing in the CLI
  harness, not introduced here. Noted only because it appears in the smoke
  summary line.

---

## Pre-existing items (NOT introduced by this PR -- noted, not blocking)

- **P1 -- by-value IL-VT call return to a FRESH temp is not seeded.** When a
  call returns a by-value IL value type and the dest is a fresh temp (`cur ==
  null`), neither the stale-VT branch (requires `cur is ILType` VT) nor the
  trailing seed (`IsPrimitive == false` for an IL-VT) types the dest. So a
  subsequent `ldfld`/`stfld` on that dest would not be recognized as in-frame
  by `TryRewriteFieldAccessForInline`. This is the pre-existing behavior (the
  old ELSE branch did nothing either) and the trailing seed does not change it.
  Out of scope for this change; flagged for a future VT-return producer step.

- **P2 -- `registerTypes` is a single linear pass with no phi-merge (child 11 /
  child 16 gotcha).** A primitive seed on a dest that is later reused across a
  join for a different type could in principle mis-type the later use. This is
  the SAME reliability envelope every existing seed relies on; the straight-
  line produce-then-arith shape (the failing patterns) is same-block and
  reliable. The change does not broaden the dataflow. Mitigated by the 358/0
  smoke + the 4 dedicated probes.

---

## Verdict

**APPROVE-WITH-FINDINGS.**

The change is correct, minimal, purely additive (71/0), empirically proven
(4 probes FAULT on HEAD with DivideByZero; 358/0 with the fix), and safe:
seeding is restricted to primitive types (CLRType, not ILType, so no in-frame-VT
discriminator can be perturbed), the hash decode is byte-identical to the
proven runtime handler, and all gate paths (void / null / ByRef / VT / I4)
behave as the design specifies. There are no Blocker or Major issues. The two
Minor findings (M1: hoist `GetMethod`; M2: add a CLR-callee probe for O1) are
non-blocking improvements/coverage items; the Trivial items are observations.
The pre-existing items P1/P2 are out of scope and not introduced here.

Recommended before merge: none required. Recommended as follow-up: M1 (hoist)
and M2 (CLR-callee probe), at the implementer's discretion.
