# Tasks — neo-ceq-null-sentinel

> Build/test: ALWAYS `-f net8.0`; CLI = `Debug_Neo --no-incremental`; NEVER build
> `TestCases` with `Debug_Neo` (use plain `Debug`). All runtime/JIT/optimizer
> edits `#if ENABLE_NEO_MODE`-gated. Baseline NeoStep smoke after child 11:
> **332/0**.
>
> Defect: `Ceq` (`ILIntepreter.Neo.cs:1968`), `Beq` (`:2137`), `Bne_Un`
> (`:2144`) compare raw operand `int32`s. For a reference operand (an mStack
> index; null = non-zero index or `-1`), this mis-compares the index integers.
> The ceq form of `x == null` / `x == y` (`ldsfld ref; ldnull; ceq; brfalse`)
> therefore yields the wrong result -> lazy-init skipped -> NRE. Actively
> failing: `TestStaticFieldInstance`, `RegisterVMTest04`.
>
> Mechanism (confirmed in JIT): `InferPrimTag` (`JITCompiler.cs:1548`) returns
> the fallback `I4` for references (line 1581); `GetTypedCompareOpcode`/
> `GetTypedBranchOpcode` (`:1678`/`:1717`) `return code` unchanged for `I4`
> (line 1714) -> a reference-typed `Ceq`/`Beq`/`Bne_Un` stays the plain opcode
> -> reaches the raw-int runtime arm. Fix = JIT type-specialize to
> `Ceq_Ref`/`Beq_Ref`/`Bne_Un_Ref` when an operand is a reference slot (mirror
> child-11 `Brtrue`->`Brtrue_Ref`), with a runtime resolve-and-compare arm
> (Legacy `Ceq`/`Beq`/`Bne_Un` parity).

## 1. Diagnose-first (confirm the ceq gap is live on HEAD)

- [x] 1.1 Confirm the HEAD NeoStep smoke baseline is green:
  `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental`
  then
  `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep`
  -> expect **332/0**. Record the exact count.
- [x] 1.2 Add the TC1 ceq-form lazy-init probe (task 6) and CONFIRM it FAULTs on
  HEAD (NRE: init skipped, field stays null). If it does NOT fault, inspect the
  JIT `OUTPUT_JIT_RESULT` dump for the probe's method: confirm Roslyn emitted
  `Ceq` (not a direct `brtrue`). If Roslyn emitted the direct form, restructure
  the probe to force ceq materialization (assign the comparison to a `bool`
  local FIRST: `bool b = (field == null); if (b) { ... }`, or compare two
  references `x == y`). Re-confirm the fault. This proves the gap is live.
- [x] 1.3 (Optional) Confirm `TestStaticFieldInstance` / `RegisterVMTest04`
  still NRE on HEAD via a targeted full-smoke run filtered to those names (they
  are NOT in the `NeoStep` set). Note their pre/post status for the ship log.

## 2. JIT — type-specialize the reference equality compares

- [x] 2.1 Append `Ceq_Ref`, `Beq_Ref`, `Bne_Un_Ref` to
  `ILRuntime/Runtime/Intepreter/OpCodes/OpCodeREnum.cs` (after the existing
  `Ceq`/`Beq`/`Bne_Un` cluster or at the end -- enum is implicit-numbered,
  order does not shift existing values).
- [x] 2.2 In `TypeSpecializeNeoOpcodes` (`JITCompiler.cs`), in the
  `case OpCodeREnum.Ceq: case Cgt: case Cgt_Un: case Clt: case Clt_Un:` block
  (`:902-908`), AFTER the existing
  `op.Code = GetTypedCompareOpcode(...)` + `SetRegisterType(registerTypes,
  op.Register1, appdomain.IntType)`, add the reference upgrade: if
  `op.Code == OpCodeREnum.Ceq` AND
  (`IsNeoReferenceSlot(GetRegisterType(registerTypes, op.Register2))` OR
  `IsNeoReferenceSlot(GetRegisterType(registerTypes, op.Register3))`) ->
  `op.Code = OpCodeREnum.Ceq_Ref`. (Only `Ceq`; do NOT touch `Cgt`/`Clt`
  reference ordering -- invalid CIL; `Cgt_Un` already handled.)
- [x] 2.3 In the conditional-branch case (`:910-930`, the
  `Beq`/`Bne_Un`/`Blt`/.../`Bge_Un` block), AFTER the existing
  `op.Code = GetTypedBranchOpcode(NormalizeBranchOpcode(op.Code), ...)`, add
  the reference upgrade: if (`op.Code == Beq` || `op.Code == Bne_Un`) AND
  (`IsNeoReferenceSlot(GetRegisterType(registerTypes, op.Register1))` OR
  `IsNeoReferenceSlot(GetRegisterType(registerTypes, op.Register2))`) ->
  `op.Code = (op.Code == Beq) ? Beq_Ref : Bne_Un_Ref`. (Do NOT touch
  `Blt`/`Bgt`/... reference ordering -- invalid CIL.)
- [x] 2.4 VERIFY (diagnose-first) no additional reference-producer seeding is
  needed beyond the existing `Ldnull` (`:843-845` -> `ObjectType`) and child-11
  `Ldsfeld` (`:1243-1258`). Run the smoke after task 4; if a `ceq` on a
  different ref producer (e.g. heap `Ldfld_Ref`, ref-returning `Call`) stays
  plain and a smoke pattern regresses, seed that producer too. The
  `ldsfld`/`ldnull`-fed patterns are the confirmed path.

## 3. LowerNeoOffsets + Optimizer.Utils — lower/classify the new variants

- [x] 3.1 In `Optimizer.Neo.cs` `LowerNeoOffsets`, add `Ceq_Ref` to the R1R2R3
  case-list (`:459-503`, the block ending `LowerR1R2R3(ref op, localInfos);` at
  `:502` -- same shape as `Ceq`).
- [x] 3.2 Add `Beq_Ref` and `Bne_Un_Ref` to the R1R2 case-list (`:654-695`,
  the block ending `LowerR1R2(ref op, localInfos);` at `:694` -- same shape as
  `Beq`/`Bne_Un`).
- [x] 3.3 In `Optimizer.Utils.cs`, audit every categorization list that
  currently contains `Ceq`/`Beq`/`Bne_Un` and add the corresponding `_Ref`
  variant where the list's semantics apply to a reference equality compare
  (child-11 added `Brtrue_Ref`/`Brfalse_Ref` to 5 such lists at lines ~354,
  594, 868, 1285, 1530 -- mirror that audit). Do NOT extend
  `SupportIntemediateValue` (`:17`) -- reference operands are never foldable
  immediates (leave `_Ref` to `default: return false`). Do NOT add to the
  typed->base normalize function (`:1594`/`:1601`/`:1602`).
- [x] 3.4 Confirm no compaction/roundtrip pass strips the new opcodes (the
  Step-23 roundtrip check enumerates opcodes; if it has a whitelist, extend it
  -- mirror what child-11 did for `Brtrue_Ref`).

## 4. ExecuteNeo — runtime reference-equality arms

- [x] 4.1 In `ILIntepreter.Neo.cs`, add `case OpCodeREnum.Ceq_Ref:` next to the
  `Ceq` arm (`:1968`):
  `int cra = *(int*)(frameBase + ip->SrcOffset); int crb = *(int*)(frameBase + ip->OperandOffset);`
  `object rra = cra >= 0 ? mStack[cra] : null; object rrb = crb >= 0 ? mStack[crb] : null;`
  `*(int*)(frameBase + ip->DstOffset) = rra == rrb ? 1 : 0;`
  `break;`  (a = `SrcOffset`/Register2, b = `OperandOffset`/Register3, dest = `DstOffset`/Register1)
  Include a comment citing Legacy `Ceq` (`Register.cs:4557-4603`) and the three
  null encodings.
- [x] 4.2 Add `case OpCodeREnum.Beq_Ref:` next to the `Beq` arm (`:2137`):
  `int bra = *(int*)(frameBase + ip->DstOffset); int brb = *(int*)(frameBase + ip->SrcOffset);`
  `object bra2 = bra >= 0 ? mStack[bra] : null; object brb2 = brb >= 0 ? mStack[brb] : null;`
  `if (bra2 == brb2) { ip = ptr + ip->Operand; continue; }`
  `break;`
- [x] 4.3 Add `case OpCodeREnum.Bne_Un_Ref:` next to the `Bne_Un` arm (`:2144`):
  same resolve, branch when `bra2 != brb2`.

## 5. Probes (must FAULT on HEAD, PASS after)

- [x] 5.1 TC1 (ceq-form lazy-init -- the confirmed-live failure shape):
  `TestCases/NeoStepCeqNullSentinelTest.cs`. An IL-static reference field,
  initially null, with `bool b = (field == null); if (b) { field = new T(...); }`
  then dereference `field` (call a method / read a field). NREs on HEAD (ceq
  mis-compares -> init skipped -> field null). Embed `NeoStep` in the class
  name. CONFIRM via stash-toggle + JIT dump that this emits `Ceq` and NREs on
  HEAD (task 1.2). If Roslyn folds to direct `brtrue`, restructure per task 1.2.
- [x] 5.2 TC2 (two-reference identity via ceq): two reference locals/fields,
  `bool same = (a == b);` where both are the SAME non-null instance -> assert
  `same` is true; and two DISTINCT instances -> assert `same` is false. Forces
  `ceq` on two references (Roslyn can't fold to a branch -- result is used as a
  value). Without the fix the raw-index compare gives the wrong identity answer
  -> deliberate `1/0` div-by-zero on mismatch. Embed `NeoStep`.
- [x] 5.3 (Optional) TC3 (beq/bne.un reference form): `if (a == b) { ... }` /
  `if (a != b) { ... }` on two references if Roslyn emits `beq`/`bne.un` (verify
  via JIT dump); otherwise skip (the arms ship for completeness regardless).
- [x] 5.4 Build `TestCases` with plain `Debug`
  (`dotnet build TestCases/TestCases.csproj -c Debug`).

## 6. Verify (the regression gate)

- [x] 6.1 Build CLI: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental`.
- [x] 6.2 Full NeoStep smoke (no filter change): expect **332 -> 332 + N probes**
  (N = new probes that pass), 0 failures. CRITICAL: the child-11 load-bearing
  repair (Call-case stale-ref clear) MUST NOT regress -- if any previously-green
  NeoStep test now fails (especially `NeoStepOrChain_*`, `NeoStep16_TC10`,
  `NeoStep20_Tr2`/`Tr5` -- the child-11 canaries), a stale-reference type is
  mis-firing the ceq specialization; investigate the producer's type seeding.
- [x] 6.3 Explicitly re-verify the confirmed-active failures are resolved: run
  filtered to `TestStaticFieldInstance` and `RegisterVMTest04` (they are outside
  the `NeoStep` filter set) -> both PASS (no NRE). Record before/after.
- [x] 6.4 Legacy-neutral:
  `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug` then
  `dotnet run -c Debug -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep`
  -> same failure set as the pre-change Legacy baseline (the new probes MUST
  pass under Legacy too); no regression.
- [x] 6.5 (Optional) Full Neo smoke (drop the `NeoStep` filter) to confirm no
  new ceq/beq/bne.un-related NREs surface pre-crash.
