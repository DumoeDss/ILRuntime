# neo-stfld-value-ldfld-value — tasks

## 1. Probe (reproducer) — DONE
- [x] Add `NeoStep12bFieldInnerPrim` / `NeoStep12bFieldOuterPrim` (pure-primitive
      nested VT, refCount 0) + `NeoStep12bFieldInnerWithRef` /
      `NeoStep12bFieldOuterWithRef` (Inner WITH a ref field — load-bearing) to
      `TestCases/NeoStep12bTest.cs`.
- [x] Add `NeoStep12b_StfldLdfldValue_Prim` (`o.inner = src; Inner i = o.inner;
      assert i.a==1 && i.b==2 && o.tag==5`) +
      `NeoStep12b_StfldLdfldValue_WithRef` (assert `i.a==11 && i.s=="hello"
      && o.tag==7`).
- [x] Confirm FAIL-on-HEAD: `Neo: opcode Stfld_Value not yet implemented (Step 6)`.

## 2. Root-cause — DONE
- [x] `ExecuteNeo` had NO `case Stfld_Value`/`Ldfld_Value` → default NIE
      (`ILIntepreter.Neo.cs:5234`).
- [x] JIT already emitted the opcodes + field offsets but NOT the field type.
- [x] `LowerNeoOffsets` never lowered R1/R2 for these opcodes (silent 0/0
      offsets; `WarnUnhandledNeoLoweringOpcode` is a no-op).

## 3. Fix (Neo-gated, Legacy-neutral) — DONE
- [x] **JIT** (`JITCompiler.cs` `case Code.Ldfld`/`case Code.Stfld`,
      `#if ENABLE_NEO_MODE`): stamp `op.Operand4 = fieldType.GetHashCode()` when
      `op.Code == Ldfld_Value`/`Stfld_Value`.
- [x] **Optimizer** (`Optimizer.Neo.cs` `LowerNeoOffsets`): add
      `case Stfld_Value` (Stfld group) + `case Ldfld_Value` (Ldfld group) so
      `LowerR1R2` runs.
- [x] **Runtime** (`ILIntepreter.Neo.cs` `ExecuteNeo`): `case Stfld_Value` +
      `case Ldfld_Value` after `Stfld_Ref`. Mirror `Move_Vt`: byte CopyBlock
      for the primitive region (at `Primitives[field.PrimitiveOffset]`) + an
      mStack-to-mStack ref-slot loop (at
      `ManagedObjects[field.ReferenceOffset]`); recover the in-frame VT's
      ref-run base via the `localInfos` scan (R2); scan-miss → tagged NIE.

## 4. Verify — DONE
- [x] Both probes FAIL-on-HEAD → PASS-after.
- [x] NeoStep12b gate: 6/6.
- [x] Full NeoStep smoke: 293/0/0 (291 baseline + 2 probes).
- [x] Stash-toggle PROVEN (stash 3 engine files, keep test → FAIL; pop → PASS).
- [x] Legacy-neutral: `dotnet build ILRuntime/ILRuntime.csproj -c Debug` → 0
      errors.

## 5. Docs — DONE
- [x] `design.md` + `tasks.md` in
      `openspec/changes/neo-stfld-value-ldfld-value/`.
- [ ] Update `.trae/documents/neo-deferred-items.md` (move the Step 12b
      `Stfld_Value`/`Ldfld_Value` deferred item to Resolved) — LEAD/archivist
      on archive.
