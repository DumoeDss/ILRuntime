# Tasks — neo-brtrue-on-reference

> Build/test: ALWAYS `-f net8.0`; CLI = `Debug_Neo`; NEVER build `TestCases`
> with `Debug_Neo` (use plain `Debug`). All edits `#if ENABLE_NEO_MODE`-gated.
> Baseline NeoStep smoke after child 10: **330/0**.
>
> ## IMPLEMENTED (2026-07-12) — all 7 groups done. NeoStep smoke **332/0**
> (330 + 2 probes), Tr2/Tr5 PASS, Legacy-neutral (17-failure baseline holds).
>
> F4 (Brtrue_Ref/Brfalse_Ref null-sentinel): appended the 2 enum members;
> TypeSpecializeNeoOpcodes seeds the Ldsfeld dest type (IL StaticFieldTypes +
> CLR FieldInfo.FieldType) and rewrites Brtrue/Brfalse -> _Ref when
> IsNeoReferenceSlot(cond); LowerNeoOffsets + 5 Optimizer.Utils.cs opcode-lists
> extended; ExecuteNeo deref arm `idx = *(int*)DstOffset; truthy = idx >= 0 &&
> mStack[idx] != null`. CRITICAL follow-on: the F4 specialization mis-fired on
> a bool/int CALL result in a register reused from a reference load (stale ref
> type not cleared) and regressed NeoStepOrChain_FirstCompareTrue +
> NeoStep16_TC10 (2 div-by-zero). Fixed by extending the Call/Callvirt case to
> clear a stale reference type when the call returns a non-reference (the
> load-bearing regression repair -- WITHOUT it F4 regresses 2 existing tests).
>
> F3 (IL-static Stsfld/Ldsfeld raw DstOffset): both IL-static arms now resolve
> the operand byte offset via `localInfos[ip->DstOffset].Offset` (mirrored the
> CLR-static arms; stale comment trimmed).
>
> Coupling verdict REFINED (empirically re-verified): the coupling is
> ASYMMETRIC, not symmetric as the planner's D5 implied. F4-ALONE does NOT break
> Tr2/Tr5 (both pass -- on HEAD the F3 bug leaves the cache-check register at
> its stale 0 from a prior ldc.i4.0, plain Brtrue would read 0=falsey, and F4's
> deref reads idx=0 -> mStack[0] which happens to be null -> falsey -> init
> runs). The REAL coupling is the OTHER direction: F3-ALONE (correct Ldsfeld
> offset + plain Brtrue) reads the real non-zero cache index N as truthy ->
> skips init -> null delegate -> NRE (this is what child-3 observed and left
> the F3 gap for). So F3 is the TRIGGER that exposes the brtrue gap; F4 is the
> FIX; F4+F3 is the only correct state. Both ship together.
>
> Probes (TestCases/NeoStepBrtrueRefTest.cs): TC1 delegate-cache + TC2 non-null
> local coalesce. Both PASS on FIXED (Brtrue_Ref fires correctly). NOTE: the
> Neo frame is zero-initialized, so the delegate-cache check register reads 0 on
> HEAD too -> these guards pass on both HEAD and FIXED (F3 bug is inert for a
> fresh register). The load-bearing fault evidence is therefore NOT a
> probe-NRE-on-HEAD but (a) the F4-induced 2-test regression without the
> Call-case clear, and (b) the ceq-form TestStaticFieldInstance NRE on HEAD.
>
> OPEN (out of scope, design D3): the confirmed-active failures
> TestStaticFieldInstance / RegisterVMTest04 use the CEQ form
> (`ldsfld;ldnull;ceq;brfalse`), NOT the direct `ldsfld;brtrue`. ceq compares
> the raw mStack index N against ldnull's -1 sentinel as integers -> never equal
> for an IL-static null -> "instance != null" -> init skipped -> NRE. This is a
> DISTINCT gap (ceq null-sentinel) that this child does NOT fix (the direct-form
> Brtrue_Ref fix is correct but does not touch ceq). Candidate follow-up child.
> Also open: a runtime IL-static int/ref field write+read ROUND-TRIP returns
> garbage on BOTH HEAD and FIXED (a separate field-storage gap, not the F3
> operand-offset fix -- F3 is correct, mirrors the working CLR arm, but the
> round-trip has a deeper issue).

## 1. Diagnose-first (confirm the F3/F4 coupling before coding)

- [ ] 1.1 Confirm the HEAD NeoStep smoke baseline is green:
  `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental`
  then
  `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep`
  → expect 330/0. Record the exact count.
- [ ] 1.2 Add a quick lazy-init probe (`NeoStep` in the name) to `TestCases/`
  that FAULTs on HEAD: an IL-static reference field with
  `if (field == null) { field = new SomeILType(); }` then dereferences `field`.
  Build `TestCases` with plain `Debug`, run the smoke filtered to the probe →
  confirm it NREs (init skipped). This proves F4 is live before any fix.
- [ ] 1.3 (Optional but recommended) Temporarily apply F4 ALONE (tasks 2-4
  without task 5) and run the smoke filtered to `NeoStep20_Tr2` / `NeoStep20_Tr5`
  + the probe → confirm they BREAK (OOB crash or NRE). This empirically validates
  the D5 coupling verdict before committing to the combined fix. Revert before
  proceeding.

## 2. JIT — type-specialize the branch condition

- [ ] 2.1 Append `Brtrue_Ref` and `Brfalse_Ref` to
  `ILRuntime/Runtime/Intepreter/OpCodes/OpCodeREnum.cs` (after the existing
  `Brtrue`/`Brfalse` cluster or at the end — enum is implicit-numbered, order
  does not shift existing values).
- [ ] 2.2 In `TypeSpecializeNeoOpcodes` (`JITCompiler.cs:777`), add a
  `case OpCodeREnum.Ldsfld:` that seeds `registerTypes[op.Register1]` from the
  static field type: declaring type via
  `appdomain.GetType((int)(op.OperandLong >> 32))`; `ILType` →
  `ilt.StaticFieldTypes[(int)op.OperandLong]`; `CLRType` → resolve the field's
  `FieldType` to an `IType` (reuse the domain's CLR→IType resolution already used
  in the JIT). Guard against out-of-range `sIdx` / null `StaticFieldTypes`.
- [ ] 2.3 In the same switch, add `case Brtrue/Brtrue_S/Brfalse/Brfalse_S:`:
  if `IsNeoReferenceSlot(GetRegisterType(registerTypes, op.Register1))` →
  `op.Code = (Brtrue or Brtrue_S) ? Brtrue_Ref : Brfalse_Ref`. (Mutate `op` in
  the `body` list in place, as the other rewrites do.)
- [ ] 2.4 Verify (diagnose-first) whether the heap `Ldfld_Ref` dest and a
  reference-returning `Call` need seeding too — check the `Move`/`Call` seeding
  (`JITCompiler.cs:1180`/`:1207`) and whether any smoke pattern hits
  `ldfld ref; brtrue` or `call; brtrue` on null. Seed only if needed; the
  `ldsfld`-fed patterns are the confirmed path.

## 3. LowerNeoOffsets — lower the new variants

- [ ] 3.1 In `Optimizer.Neo.cs` `LowerNeoOffsets` Brtrue case-list (`:697-708`),
  add `Brtrue_Ref` / `Brfalse_Ref` alongside `Brtrue`/`Brtrue_S`/`Brfalse`/
  `Brfalse_S` so they get the same R1 lowering
  (`op.Operand2 = localInfos[r1].Size; op.DstOffset = localInfos[r1].Offset`).
- [ ] 3.2 Confirm no compaction/roundtrip pass strips the new opcodes (the
  Step-23 roundtrip check enumerates opcodes; if it has a whitelist, extend it).

## 4. ExecuteNeo — runtime null-sentinel arms

- [ ] 4.1 In `ILIntepreter.Neo.cs`, add `case OpCodeREnum.Brtrue_Ref:` /
  `Brfalse_Ref:` arms next to the existing Brtrue/Brfalse arms (`:2047`/`:2069`):
  `int idx = *(int*)(frameBase + ip->DstOffset);`
  `bool truthy = idx >= 0 && mStack[idx] != null;`
  `Brtrue_Ref` → branch when `truthy`; `Brfalse_Ref` → branch when `!truthy`.
  No bounds guard (Legacy parity; valid after task 5). Include a comment citing
  Legacy `Register.cs:2053` and the three null encodings.

## 5. F3 — lower IL-static Stsfld/Ldsfld operand offset (SAME child)

- [ ] 5.1 In the IL-static `Stsfld` arm (`Neo.cs:4119`, the `srcSlot` read at
  `:4135`), resolve the operand register byte offset via `localInfos` exactly as
  the CLR-static arm does (`:4192-4194`): replace `frameBase + ip->DstOffset`
  with `frameBase + (localInfos[ip->DstOffset].Offset)`. Update/trim the now-
  stale comment at `:4187-4191`.
- [ ] 5.2 In the IL-static `Ldsfld` arm (`Neo.cs:4267`, the `dstSlot` write at
  `:4277`), apply the same `localInfos[ip->DstOffset].Offset` resolution
  (mirror the CLR-static arm at `:4331-4332`).
- [ ] 5.3 Re-run the lazy-init probe + `NeoStep20_Tr2`/`Tr5` → the `ldsfld`
  now reads the real static value at the correct offset; with task 4 the branch
  tests it correctly. No OOB.

## 6. Probes (must FAULT on HEAD, PASS after)

- [ ] 6.1 Finalize TC1 (lazy-init): `TestCases/NeoStep*Test.cs` — an IL-static
  reference field, `if (field == null) { field = new SomeILType(...); }`, then
  assert `field` is usable (call a method / read a field on it). NREs on HEAD.
  Embed `NeoStep` in the method name.
- [ ] 6.2 TC2 (delegate-cache): cache a `Func`/`Action` in an IL-static field
  via the `if (cached == null)` pattern; invoke twice; assert non-null (and
  optionally same instance). Embed `NeoStep` in the method name.
- [ ] 6.3 Build `TestCases` with plain `Debug`
  (`dotnet build TestCases/TestCases.csproj -c Debug`).

## 7. Verify (the unmasking-risk gate)

- [ ] 7.1 Build CLI: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental`.
- [ ] 7.2 Full NeoStep smoke (no filter change): expect **330 → 330 + N probes**
  (N = new probes), 0 failures.
- [ ] 7.3 Explicitly re-verify the unmasking canary:
  run filtered to `NeoStep20_Tr2` and `NeoStep20_Tr5` → both PASS.
- [ ] 7.4 Confirm the confirmed-active failures are resolved: run filtered to
  the `TestStaticFieldInstance` / `RegisterVMTest04` names (if they are in the
  `NeoStep` filter set; otherwise note their status) → PASS.
- [ ] 7.5 Legacy-neutral: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug` then
  `dotnet run -c Debug -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep`
  → same failure set as the pre-change Legacy baseline (the new probes MUST pass
  under Legacy too); no regression.
- [ ] 7.6 (Optional) Full Neo smoke (drop the `NeoStep` filter) to confirm no
  new brtrue-related NREs surface pre-crash.
