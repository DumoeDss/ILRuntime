## 1. Offset-lowering pass entry (shared code, do first)

- [x] 1.1 In `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs`, add `case OpCodeREnum.Ldfld:` to the Ldfld typed-arm offset-lowering case-list (~line 934; the block that sets `DstOffset = localInfos[R1].Offset; SrcOffset = localInfos[R2].Offset`). Do NOT add an `Operand`/`Operand4` assignment (the raw opcode carries field identity in `OperandLong`, which the block must not touch).
- [x] 1.2 Add `case OpCodeREnum.Stfld:` to the Stfld typed-arm case-list (~line 960; same `DstOffset = localInfos[R1].Offset; SrcOffset = localInfos[R2].Offset` block).
- [x] 1.3 Build `ILRuntimeTestCLI` (`Debug_Neo --no-incremental`) and run the NeoStep smoke; confirm 314/0/0 still holds (no regression to the typed arms from the lowering edit). The raw opcodes will still NIE at runtime (handler not added yet) but must not affect NeoStep tests.

## 2. NeoStep regression probes (write first, must FAULT before the fix)

- [x] 2.1 Add a `NeoStep` probe in `TestCases/` that reads and writes a field declared on a CLR **reference** type (e.g. a public int/string field on a CLR class), asserting the round-tripped value. Confirm it FAULTS with the Step-6 NIE on the current build (probe must FAULT to fail, per child-1/child-2 discipline).
- [x] 2.2 Add a `NeoStep` probe that reads and writes a primitive field of a CLR **value-type** local struct (e.g. `TestVector3`-style `x`/`y`/`z`), asserting the round-tripped value. Confirm it FAULTS before the fix.
- [x] 2.3 (Optional) Add a probe for a CLR-struct-typed field or an inherited CLR-base field if infra already exists; otherwise log as a follow-up.

## 3. ExecuteNeo runtime handler

- [x] 3.1 In `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`, add `case OpCodeREnum.Ldfld:` (near the typed Ldfld arms ~3636) and `case OpCodeREnum.Stfld:` (near the typed Stfld arms ~3702), placed BEFORE the `default` arm.
- [x] 3.2 Decode field identity: `int typeHash = (int)((ulong)ip->OperandLong >> 32); int fieldHash = (int)ip->OperandLong; var type = AppDomain.GetType(typeHash);` (a `CLRType`). Resolve `FieldInfo` via `type.GetField(fieldHash)` (null-guard → tagged NIE).
- [x] 3.3 CLR reference-type owner: owner slot first int = `mStack` index. Ldfld → `NeoReadClrObjectField(AppDomain, mStack[idx], fieldHash)`; write the result to the dest slot (reuse the primitive/ref/VT-to-frame helpers used by the static-field arms, discriminating on `FieldInfo.FieldType`). Stfld → read the source value, `NeoWriteClrObjectField(AppDomain, mStack[idx], fieldHash, value)`.
- [x] 3.4 CLR value-type owner — Ldfld (inline flat bytes): the owner register slot holds the struct's flat bytes; read the primitive at `ownerOff + FieldInfo.GetFieldOffset()` (typed read sized to `FieldInfo.FieldType`) into the dest slot.
- [x] 3.5 CLR value-type owner — Stfld (frame-native byref): owner slot is a byref `(objIdx=-1, structBaseOff)`; write the source primitive at `frameBase + structBaseOff + FieldInfo.GetFieldOffset()` (typed write sized to the field type).
- [x] 3.6 Handle (or defer with a tagged NIE) the remaining owner shapes from the dump: IL-instance-with-CLR-base-field (`mStack[idx] is ILTypeInstance`) and CLR value-type array element (`mStack[idx] is Array`). If deferred, the tagged NIE must be distinct from the Step-6 default so progress is measurable.
- [x] 3.7 Add a fail-loud tagged-NIE guard for any owner shape the handler cannot resolve (mirror the Stobj arm's Step-17 deferrals), so silent corruption cannot occur.

## 4. Verify

- [x] 4.1 Build CLI (`Debug_Neo --no-incremental`) + TestCases (`Debug`); run the NeoStep smoke with the `NeoStep` filter — confirm 314+/0/0 (the new probes pass, no regressions).
- [x] 4.2 Run the FULL Neo smoke (no filter) and confirm the raw `Stfld`/`Ldfld` Step-6 hits are gone (re-run the planner's instrumentation greppable, or just confirm the previously-failing tests now advance past the field access). Counts are pre-crash only (known NRE crash is unrelated).
- [x] 4.3 Confirm Legacy-neutral: `git diff` shows all edits under `#if ENABLE_NEO_MODE` / Neo-only files; no `ExecuteR`/Legacy-path change.

## 5. Ship

- [x] 5.1 Update `rasen/changes/neo-overhaul/planning-context.md` durable-findings section with the confirmed escaping shape + fix site.
- [ ] 5.2 Commit (`git status` first to avoid partial commit; trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`), push with `git config lfs.useslockfiles false`. — DEFERRED to shipper (implementer must NOT commit).
