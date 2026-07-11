## Why

Neo `Brtrue`/`Brfalse` (`ILIntepreter.Neo.cs:2063`/`:2071`) test the branch
condition as a low `int32` (`*(int*)(frameBase + ip->DstOffset) != 0`). Under
the Neo object model a reference value is stored as an **mStack index** in the
slot's primitive bytes, and a **null** reference is encoded as a non-zero mStack
index (IL-static `Ldsfld`, `Neo.cs:4302-4304`) or `-1` (CLR-static `Ldsfld`,
`Neo.cs:4350`; `Ldnull`, `Neo.cs:1526`). The `!= 0` test therefore reads a null
reference as **truthy**. This breaks the canonical C# lazy-init / delegate-cache
pattern `if (x == null) { x = new ...; }` (Roslyn lowers `x == null` on a
reference to a *direct* `ldsfld x; brtrue skipInit` with **no** `ceq`): a null
`x` reads truthy, the initializer is skipped, `x` stays null, and the program
NREs downstream. This is **actively failing** today (`TestStaticFieldInstance`,
`RegisterVMTest04`, the `NeoStep20_Tr2/Tr5` delegate-cache pattern) — a live
correctness gap, not a missing feature.

## What Changes

- **Type-specialize the branch condition.** Add `Brtrue_Ref` / `Brfalse_Ref`
  `OpCodeREnum` variants (appended; the enum is implicit-numbered so no values
  shift). The Neo frame is untyped (no per-slot `ObjectType` tag like Legacy),
  so the reference-vs-int distinction is made at **JIT time** in
  `TypeSpecializeNeoOpcodes`: when the condition register's tracked type is a
  reference slot (`IsNeoReferenceSlot` — non-primitive and non-value-type),
  rewrite `Brtrue`/`Brfalse` → `Brtrue_Ref`/`Brfalse_Ref`. `ceq`-normalized
  conditions stay on plain `Brtrue` (`ceq` seeds its dest as `IntType`, so the
  truth value is a real 0/1 `int32`) — only *direct* reference branch conditions
  (Roslyn's null-test optimization) become `_Ref`.
- **Seed the reference producers.** `Ldsfld` (the dominant feeder for the
  failing patterns) does **not** currently seed the per-register type map; add
  `Ldsfld` dest-type seeding (and verify/seed the heap `Ldfld_Ref` and
  reference-returning `Call` paths) so the `_Ref` rewrite fires for the
  `ldsfld ref; brtrue` chain.
- **Runtime null-sentinel test.** The `Brtrue_Ref`/`Brfalse_Ref` `ExecuteNeo`
  arms test the **referenced object's** nullness —
  `int idx = *(int*)(frameBase + ip->DstOffset); truthy = idx >= 0 && mStack[idx] != null`
  (mirrors Legacy `mStack[reg1->Value] != null`, `ILIntepreter.Register.cs:2053`)
  — instead of the raw index int. Handles all three null encodings (-1 and
  null-at-index) uniformly.
- **Fix F3 in the same child (IL-static `Stsfld`/`Ldsfld` raw `DstOffset`).**
  These arms are NOT lowered by `LowerNeoOffsets`, so `ip->DstOffset` is still a
  raw register index that the IL-static arms misread as a byte offset
  (`Neo.cs:4135`/`:4277`); the CLR-static arms already resolve it via
  `localInfos` (`:4192`/`:4331`). F3 and this brtrue gap (F4) are **coupled**
  through the `ldsfld(IL-static ref); brtrue` delegate-cache pattern: fixing F4
  alone feeds the new deref arm garbage from the un-lowered `Ldsfld` (mStack OOB
  risk), and fixing F3 alone unmasks F4 (child-3 established this breaks
  `NeoStep20_Tr2/Tr5`). Both must land together, F4 first then F3.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-optimizer`: add a correctness requirement pinning the branch-condition
  null-sentinel — a reference-typed `Brtrue`/`Brfalse` condition MUST test the
  referenced object's nullness (`mStack[idx] != null`), never the raw mStack-index
  `int32`. This is a type-specialization invariant of `TypeSpecializeNeoOpcodes` +
  `LowerNeoOffsets` (the capability that already owns the conv/compare
  type-specialization and the `Stsfld`/`Ldsfld` arms, per the child-3 precedent).

## Impact

- `ILRuntime/Runtime/Intepreter/OpCodes/OpCodeREnum.cs` — append
  `Brtrue_Ref`, `Brfalse_Ref`.
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`
  (`TypeSpecializeNeoOpcodes`) — seed `Ldsfld` (and verify `Ldfld_Ref` / `Call`)
  dest types; add the `Brtrue`/`Brfalse` → `_Ref` rewrite.
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` (`LowerNeoOffsets`)
  — add `Brtrue_Ref`/`Brfalse_Ref` to the Brtrue R1-lowering case-list.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` — add the
  `Brtrue_Ref`/`Brfalse_Ref` `ExecuteNeo` arms; fix the IL-static `Stsfld`/
  `Ldsfld` `DstOffset` resolution (F3) to mirror the CLR-static arms.
- `TestCases/` — new `NeoStep*` probes (lazy-init `if(x==null)`, delegate cache).
- All edits are `#if ENABLE_NEO_MODE`-gated → **Legacy-neutral by construction**.
  Legacy (`ExecuteR`) already handles this via `reg1->ObjectType`. No public API
  change.
