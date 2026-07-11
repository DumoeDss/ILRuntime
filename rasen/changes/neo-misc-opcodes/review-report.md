# Review Report: neo-misc-opcodes (child 7 of neo-overhaul)

**Reviewer:** author != verifier (independent re-verification, dispatched report-only)
**Branch:** features/object-model-overhaul
**Scope:** 4 ExecuteNeo arms + 1 Unbox guard in `ILIntepreter.Neo.cs` + 4 probes in
`TestCases/NeoMiscOpTest.cs`. All edits `#if ENABLE_NEO_MODE` (Neo-gated).
**Verdict:** **APPROVE-WITH-FINDINGS** — all 4 opcodes correct, all gates pass, stash-toggle
symmetric, full-smoke NIE drops confirmed, Legacy-neutral. Two Minor findings recorded (a
documented Conv precision divergence and one untested path); neither blocks shipping.

---

## Scope check

**Intent (proposal):** mirror 4 small Legacy ExecuteR arms (Conv_R_Un, Switch, Unbox-of-enum,
Ldsflda) as ExecuteNeo arms, one FAULTING probe each, zero Legacy/JIT change.
**Delivered:** exactly that — 4 arms + the enum Unbox guard + 4 probes. No Optimizer/JIT/CopyNeo
edits (confirmed not needed; tasks 5.3 skipped honestly). No scope creep. One minor scope note:
the Ldsflda reference-typed-static path is implemented but not exercised by a probe (Finding F2).

---

## Per-opcode correctness verdicts

### 1. Conv_R_Un — CORRECT (arm at ILIntepreter.Neo.cs ~2758, beside Conv_R8)

Dispatches on `(NeoPrimitiveTypeTag)ip->Operand2`: I8/U8 -> `(double)ReadConvU8`; R4 ->
`(double)*(float*)`; R8 -> `*(double*)`; else (I4/U4, and the rare I1/U1/I2/U2 that Legacy widens
to Integer first) -> `(double)ReadConvU4`. Writes `*(double*)(frameBase + ip->DstOffset)`.

- **Width dispatch is necessary and correct.** `ReadConvU4` (line 6174) casts I8/U8 via
  `(uint)*(long/ulong*)` — i.e. truncates 64-bit sources. Without the I8/U8 branch a `ulong`
  source would be silently truncated. Verified against `ReadConvU4`/`ReadConvU8` bodies.
- **Unsigned semantics correct.** ReadConvU4/U8 return `uint`/`ulong`; e.g. 0xFFFFFFFF -> 4294967295.0,
  not -1.0. Matches Legacy `Register.cs:1273-1274` (`(uint)reg1->Value`) and the spec delta scenario.
- **Conv_R_Un IS lowered** (`Optimizer.Neo.cs:589`, grouped with Conv_R4/R8) -> `ip->DstOffset` is a
  byte offset, used directly. Correct.
- **Conv float32-vs-double (the flagged semantic difference) — acceptable, see Finding F1.** Legacy
  (`Register.cs:1288-1291`) writes a **float32** intermediate for 32-bit (Integer/Float) sources;
  Neo always writes **double** (8-byte dest, forced by `GetConvResultType` = DoubleType). Writing a
  4-byte float to an 8-byte slot would leave the high dword stale and corrupt the following
  `conv.r8` read — so the double write is *forced* by the Neo register model, not optional. The
  divergence is documented in design D1, spec-compliant (ECMA-335 III.3.27 F-type permits extended
  precision; Neo is the more accurate one), and the probe is deliberately neutral
  (`0x80000000` = 2^31 is float32-exact, so Legacy and Neo agree on the probe value).

### 2. Switch — CORRECT (arm at ~2078, after Brfalse)

Reads the index via the `localInfos`-resolved offset, looks up `method.JumpTablesRegister[ip->Operand]`,
in-range (`swVal >= 0 && swVal < swTable.Length`) -> `ip = ptr + swTable[swVal]; continue;` else
falls through (`break`). Byte-for-byte Legacy (`Register.cs:2754-2764`).

- **Index-field resolution correct.** Verified Switch is NOT in the `LowerNeoOffsets` lowering
  case-list (it hits the offset-lowering `default:` -> `handled=false` at `Optimizer.Neo.cs:1465`),
  so `ip->DstOffset` is the raw `Register1` index. The defensive
  `(idx < localInfos.Length) ? localInfos[idx].Offset : idx` resolves it to the byte offset.
  `DstOffset`/`Register1` aliasing confirmed (`OpCode.cs:46-47`, both FieldOffset 4).
- **No `continue`-leak / no missing fall-through.** Out-of-range path exits the `if` then `break`s
  (no jump), matching Legacy. Probe exercises a distinct value per case (0/1/2) + out-of-range (99),
  so a wrong-case jump would fail the equality, not pass by luck.

### 3. Unbox-of-enum — CORRECT (guard at ~4494, inside the `clrUnboxType.IsPrimitive` branch)

Added `if (obj is ILEnumTypeInstance enumUnboxObj)` before `NeoWritePrimitiveToFrame`: extracts the
underlying value via `GetPrimitiveSize(enumIlType.FieldTypes[0])` + `Unsafe.CopyBlock` from
`enumUnboxObj.Primitives` into `frameBase + ip->DstOffset`. Else falls through to the original
CLR-primitive write.

- **Mirrors Legacy** (`Register.cs:4046` `!isEnumObj` guard + `:4129` `res.CopyToRegister(0,...)`).
- **Mirrors the existing ILType-enum Unbox arm** (`Neo.cs:4454-4461`: same
  `GetPrimitiveSize(ilType.FieldTypes[0])` + `CopyBlock` from `ins.Primitives`). The new guard adds
  an extra `!= null` defensive check on `Primitives` (slightly more defensive than the ILType-enum
  arm; harmless).
- **Value storage confirmed.** `ILEnumTypeInstance.Primitives` is authoritative (consistent with the
  Box arm at `:3594-3610` and the ILType-enum Unbox arm). For `(int)(object)E.B` the target type is
  the CLR `int` (so the ILType branch is skipped and the new guard in the CLR branch serves it);
  `enumUnboxObj.Type.FieldTypes[0]` is the underlying `int`, size 4, copies `E.B == 2`. Correct.

### 4. Ldsflda — CORRECT, zero-consumer-change CONFIRMED (arm at ~4197, before Ldsfld)

Decodes `typeHash=(int)((ulong)ip->OperandLong>>32)`, `fieldHash=(int)ip->OperandLong` (decode is
byte-for-byte consistent with the working Ldsfld arm at `:4249-4256`); for an IL type materializes
`ilt.StaticInstance` into `mStack` and emits byref `(mStack.Count-1, PrimitiveOffset|ReferenceOffset)`.
Does NOT stamp `Operand3` (correct — it aliases OperandLong's high dword).

- **Dest resolution correct.** Ldsflda is NOT in LowerNeoOffsets (hits the `default:` -> not lowered),
  so `ip->DstOffset` is the raw Register1 index, resolved via `localInfos`. This is the *correct*
  pattern — and deliberately unlike the existing Ldsfld IL-static arm, which uses `ip->DstOffset`
  directly and has a **pre-existing** dest-resolution gap (acknowledged in tasks 5.4; the probe
  reads back via `ldind`, not `ldsfld`, to stay on the byref path).
- **Zero-consumer-change CONFIRMED (the blast-radius site).** The byref `(objIdx, off)` is decoded
  identically by the lowered Stind/Ldind arms: `objIdx=*(int*)(DstOffset+0)`,
  `off=*(int*)(DstOffset+4)`, then content-based dispatch `mStack[objIdx] is ILTypeInstance` ->
  `ins.Primitives[off]` (Stind_I1..I8 / Ldind_I1..R8, `:4970-5132`) and `refIns.ManagedObjects[off]`
  (Stind_Ref/Ldind_Ref, `:5180-5198`). `GetNeoILInstance` (`:6052`) does `mStack[objIdx] as
  ILTypeInstance`; `ILTypeStaticInstance : ILTypeInstance`, so the materialized StaticInstance is
  recognized with no new code. The probe (`SetRef(ref S_Sf,7)` -> `ldsflda; stind.i4`,
  `GetRef(ref S_Sf)` -> `ldsflda; ldind.i4`) round-trips 7 == 7. CopyNeoCallArguments ->
  NeoMarshalByrefFieldToSlot is the same content-based path (per task 1.4), so `ref`/`out` static
  args resolve too.
- **CLR-static deferred honestly.** No heap object -> tagged NIE "Neo Ldsflda: CLR static field
  address deferred (follow-up)" (distinct from the Step-6 default). The 2 full-smoke hits of this
  shape remain as measurable deferrals — acceptable, mirrors child-4 discipline.

---

## Findings by severity

### F1 — Minor (documented, accepted-known): Conv_R_Un Legacy/Neo precision divergence
For `(double)(uint)x` / `(double)(ulong)x` where the magnitude exceeds float32 precision
(`> 2^24` / `> 2^53`), Legacy produces the float32-rounded value (its 32-bit intermediate) while Neo
produces the exact double. Example: `(double)(uint)0xFFFFFFFF` -> Legacy `4294967296.0`, Neo
`4294967295.0` (Neo is the true value). This is **not a bug** — Neo is more accurate and ECMA-335
permits it, and the double dest is forced by the 8-byte register slot (a float32 write would corrupt
the subsequent `conv.r8`). It is a behavioral difference from Legacy worth recording. No smoke/NeoStep
test asserts the Legacy-rounded value (Conv_R_Un NIE dropped to 0 with no value-divergence failure
surfaced). The probe is intentionally neutral. No action required; recorded for awareness.

### F2 — Minor (coverage gap): Ldsflda reference-typed IL-static-field path untested
The arm emits `ReferenceOffset` for a reference-typed static and the Stind_Ref/Ldind_Ref
`ManagedObjects[off]` path logically serves it (content-based dispatch, same path as the tested
`ldflda` heap-ref consumer). But the probe exercises only a primitive `int` static
(`PrimitiveOffset` path). A reference-typed static (e.g. `static string S_Ref;` via a `ref string`
helper, or `Interlocked.Exchange(ref S_Ref, ...)`) is not covered. Low risk (shared dispatch path),
but a follow-up probe would close the gap. Recommend the LEAD consider a one-probe follow-up.

No Blocker or Major findings. No Standards-axis violations (Neo-gated additive arms; no
concurrency/persistence/API surface; the `Unsafe.CopyBlock`/`MemoryMarshal` usage mirrors the
adjacent existing arms). Spec axis: all 3 spec deltas (neo-optimizer Conv_R_Un+Switch,
neo-type-checks Unbox-enum, neo-byref Ldsflda IL + CLR-deferral) are implemented as written, with
the two specified deferrals tagged honestly.

---

## Gate evidence (independent re-run)

- **Build CLI** `dotnet build ILRuntimeTestCLI -c Debug_Neo --no-incremental`: **0 errors** (270
  warnings, all pre-existing CS0219/CS1668 noise).
- **Build TestCases** `dotnet build TestCases -c Debug`: **0 errors**.
- **NeoStep smoke** (`... true NeoStep`): **Ran 324 tests, 0 failed** (320 baseline + 4 new probes).
  Matches the expected 324/0.
- **NeoMiscOp 4-probe isolation** (`... true NeoStepMiscOp`, arms present): **Ran 4, 0 failed**.
- **Stash-toggle (the author!=verifier core):** stashed ONLY `ILIntepreter.Neo.cs` (kept the
  untracked probe file + already-built TestCases.dll), rebuilt CLI `--no-incremental`, ran the 4
  probes -> **Ran 4, 4 failed**, each with the exact expected HEAD message:
  - Conv_R_Un: `Neo: opcode Conv_R_Un not yet implemented (Step 6)`
  - Switch: `Neo: opcode Switch not yet implemented (Step 6)`
  - UnboxEnum: `Neo: unsupported CLR primitive for Unbox: ILRuntime.Runtime.Intepreter.ILEnumTypeInstance`
    (stack: `NeoWritePrimitiveToFrame` :6339 <- `ExecuteNeo` :4394, the unguarded branch)
  - Ldsflda: `Neo: opcode Ldsflda not yet implemented (Step 6)` (default case :5811)
  Restored via `git stash pop`, rebuilt, re-ran -> **Ran 4, 0 failed**. Symmetric and reproducible.
- **Full-smoke per-opcode NIE** (`... true`, no filter; run segfaulted late at EXIT=139 — the known
  Step-19+ full-Neo-smoke crash, unrelated to this change; NIEs captured pre-crash across ~127k
  lines): each opcode NIE dropped to **0** — Conv_R_Un (was 4), Switch (was 2), Ldsflda-Step6 (was
  5), "unsupported CLR primitive for Unbox: ILEnumTypeInstance" (was 2). The only remaining
  Ldsflda-related messages are the **2** intentionally-deferred `Neo Ldsflda: CLR static field
  address deferred (follow-up)` tagged NIEs. Matches the implementer's claim exactly.
- **Legacy-neutral:** built CLI plain `Debug` (ENABLE_NEO_MODE off -> the 4 arms are `#if`-excluded),
  ran the 4 probes `... true NeoStepMiscOp` -> **Ran 4, 0 failed**. The 4 probes pass identically
  under Legacy (Legacy implements all 4 opcodes), so they add 0 failures to the Legacy baseline.
  Architecturally Legacy-neutral by construction (whole change is `#if ENABLE_NEO_MODE`).

---

## Notes / observations (non-blocking)

- The pre-existing Ldsflda-neighbor **Ldsfld IL-static dest-resolution gap** (Ldsfld uses
  `ip->DstOffset` directly despite not being lowered) is real but explicitly out of scope for this
  change; the Ldsflda arm was written correctly (localInfos resolution) and the probe sidesteps
  Ldsfld. Flagging as out-of-scope context for the LEAD, not a finding against this change.
- Working tree left as found: `ILIntepreter.Neo.cs` modified (4 arms + guard), `NeoMiscOpTest.cs`
  untracked, my temp logs removed, my toggle stash popped. (A pre-existing unrelated stash
  `child4-valuetask-blocked-partial` was already present and left untouched.)

---

## Verdict: APPROVE-WITH-FINDINGS

All 4 opcodes are correct (verified against Legacy + the Neo consumer/helper arms), all gates pass
(builds 0-error, NeoStep 324/0, 4-probe isolation 4/0), the stash-toggle is symmetric (4/4 distinct
NIEs on HEAD -> 4/0 restored), the full-smoke per-opcode NIE drops are confirmed (each -> 0; only
the 2 honest CLR-static Ldsflda deferrals remain), and the change is Legacy-neutral by construction
and by 4-probe Legacy re-run. The two Minor findings (documented Conv precision divergence F1;
untested reference-static Ldsflda path F2) are recorded as accepted-known / coverage-recommendation
and do not block shipping. The LEAD may optionally add the F2 follow-up probe; otherwise this is
ready to ship.
