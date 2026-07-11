## Why

Neo `Ceq` (`ILIntepreter.Neo.cs:1968`) compares its two operands as raw
`int32`s — `*(int*)(frameBase + ip->SrcOffset) == *(int*)(frameBase + ip->OperandOffset)`.
`Beq` (`:2137`) and `Bne_Un` (`:2144`) do the same on `DstOffset`/`SrcOffset`. This is
correct for primitive `int`/`long`/`float` compares, but **wrong for a reference operand**:
under the Neo object model a reference is stored as an **mStack index** in the slot's
primitive bytes, and a **null** reference is encoded as a non-zero mStack index (IL-static
`Ldsfld` does `mStack.Add(null)` then stores the index, `Neo.cs:4302-4304`) or the `-1`
sentinel (CLR-static `Ldsfld` `:4350`; `Ldnull` `:1526`). So `Ceq`/`Beq`/`Bne_Un` compare
two index/sentinel **integers**, never the referenced objects' nullness/identity.

This breaks the **ceq form** of the C# reference-equality / lazy-init pattern. Roslyn
lowers `x == null` on a reference to `ldsfld x; ldnull; ceq; brfalse.s skipInit` when the
comparison result is materialized (a `bool` local/field/return, or two-reference equality)
rather than a direct `brtrue`. With the bug, for a null `x` the IL-static index `N` is
compared to `ldnull`'s `-1`: `N != -1` -> `ceq` yields `0` -> `x == null` is **FALSE** ->
the `if (x == null) { x = init; }` body is skipped -> `x` stays null -> downstream NRE.
This is **actively failing** today (`TestStaticFieldInstance`, `RegisterVMTest04`) and is
the distinct sibling of child-11's direct-`brtrue` gap — child-11 fixed `Brtrue_Ref`/
`Brfalse_Ref` but explicitly left this ceq gap open (its `tasks.md` OPEN item, and its
`design.md` D3 only handles the ceq-form by noting "ceq seeds IntType -> stays plain
Brtrue", which is correct for the *following* branch but does NOT fix the ceq compare
itself).

The mechanism is confirmed in the JIT: `InferPrimTag` (`JITCompiler.cs:1548`) returns the
**fallback `NeoPrimitiveTypeTag.I4`** for any reference type (no `clr ==` check matches a
reference `TypeForCLR`, line 1581). `GetTypedCompareOpcode`/`GetTypedBranchOpcode`
(`:1678`/`:1717`) only special-case `I8`/`U8`/`R4`/`R8` and `return code` unchanged for
`I4` (line 1714). So a reference-typed `Ceq`/`Beq`/`Bne_Un` stays the plain opcode and
reaches the raw-int runtime arm. (`Cgt_Un` already has a Step-15 runtime null-sentinel
patch at `Neo.cs:1974-1999` for the `ldnull; cgt.un` "not null" idiom; `Ceq`/`Beq`/`Bne_Un`
have no equivalent.)

## What Changes

- **Type-specialize the reference equality compares.** Add `Ceq_Ref`, `Beq_Ref`,
  `Bne_Un_Ref` `OpCodeREnum` variants (appended; the enum is implicit-numbered so no
  values shift). The Neo frame is untyped (no per-slot `ObjectType` tag like Legacy), so
  the reference-vs-int distinction is made at **JIT time** in `TypeSpecializeNeoOpcodes`,
  mirroring child-11's `Brtrue`/`Brfalse` -> `Brtrue_Ref`/`Brfalse_Ref` rewrite (and the
  established `Ldfld`->`Ldfld_Ref` idiom): when either operand register's tracked type is a
  reference slot (`IsNeoReferenceSlot` -- non-primitive and non-value-type), rewrite
  `Ceq`/`Beq`/`Bne_Un` -> `Ceq_Ref`/`Beq_Ref`/`Bne_Un_Ref`. Primitive compares
  (`InferPrimTag` in {I4,I8,U8,R4,R8}) stay on the plain/typed opcodes. `Ldnull` already
  seeds its dest as `ObjectType` (`JITCompiler.cs:843-845`) and child-11 already seeds
  `Ldsfld` (`:1243-1258`), so the dominant `ldsfld ref; ldnull; ceq` chain is fed.
- **Runtime reference-equality arms.** The `Ceq_Ref`/`Beq_Ref`/`Bne_Un_Ref` `ExecuteNeo`
  arms resolve each operand to its referenced object (or null) and compare by C#
  reference equality, mirroring Legacy (`ILIntepreter.Register.cs` `Ceq` `:4557-4603`,
  `Beq` `:2090-2136`, `Bne_Un` `:2172-2220`):
  `object R(int v) => v >= 0 ? mStack[v] : null;` (the `-1` sentinel -> null; a valid
  index -> `mStack[v]`, which is itself null for an IL-static null). `Ceq_Ref` writes
  `R(a) == R(b) ? 1 : 0` to dest; `Beq_Ref` branches when `R(a) == R(b)`; `Bne_Un_Ref`
  branches when `R(a) != R(b)`. This uniformly covers all three null encodings (-1
  sentinel and null-at-valid-index) and reference identity, byte-for-byte Legacy parity.
- **`LowerNeoOffsets` + opcode-list bookkeeping.** Add `Ceq_Ref` to the R1R2R3 case-list
  (`Optimizer.Neo.cs:459-503`, same as `Ceq`), and `Beq_Ref`/`Bne_Un_Ref` to the R1R2
  case-list (`:654-695`, same as `Beq`/`Bne_Un`). Audit the `Optimizer.Utils.cs`
  categorization lists and add the `_Ref` variants where the list's semantics apply to a
  reference equality compare (child-11 added `Brtrue_Ref`/`Brfalse_Ref` to 5 such lists;
  the same audit applies). `SupportIntemediateValue` (`Optimizer.Utils.cs:17`) is
  deliberately NOT extended -- a reference operand is never a foldable immediate.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-optimizer`: add a correctness requirement pinning the ceq/beq/bne.un
  null-sentinel -- a reference-typed `Ceq`/`Beq`/`Bne_Un` MUST compare the referenced
  objects' identity/nullness (`R(a) == R(b)`), never the raw mStack-index `int32`s. This
  is the sibling of child-11's branch-condition null-sentinel requirement; together they
  close the null-comparison gap (direct-branch form + ceq form). It is a type-specialization
  invariant of `TypeSpecializeNeoOpcodes` + `LowerNeoOffsets` (the capability that owns the
  compare/branch type-specialization, per the child-11 precedent).

## Impact

- `ILRuntime/Runtime/Intepreter/OpCodes/OpCodeREnum.cs` -- append `Ceq_Ref`, `Beq_Ref`,
  `Bne_Un_Ref`.
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`
  (`TypeSpecializeNeoOpcodes`) -- in the `Ceq`/`Cgt`/`Cgt_Un`/`Clt`/`Clt_Un` case
  (`:902-908`), after the typed-opcode rewrite, upgrade a reference-operand `Ceq` to
  `Ceq_Ref`; in the conditional-branch case (`:910-930`), upgrade a reference-operand
  `Beq`/`Bne_Un` to `Beq_Ref`/`Bne_Un_Ref`. No new dest-type seeding (the producers
  `Ldnull`/`Ldsfeld` are already seeded by existing code / child-11).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` (`LowerNeoOffsets`) -- add
  `Ceq_Ref` to the R1R2R3 case-list; add `Beq_Ref`/`Bne_Un_Ref` to the R1R2 case-list.
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Utils.cs` -- audit + extend the
  categorization lists that include `Ceq`/`Beq`/`Bne_Un` (mirror child-11's 5-list
  extension for `Brtrue_Ref`/`Brfalse_Ref`).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- add the
  `Ceq_Ref`/`Beq_Ref`/`Bne_Un_Ref` `ExecuteNeo` arms next to the existing
  `Ceq`/`Beq`/`Bne_Un` arms (`:1968`/`:2137`/`:2144`).
- `TestCases/` -- new `NeoStep*` probes (the ceq-form lazy-init `if (x == null)`; a
  two-reference identity compare `x == y`).
- All edits are `#if ENABLE_NEO_MODE`-gated -> **Legacy-neutral by construction**. Legacy
  (`ExecuteR`) already handles reference compares via `reg->ObjectType` (`Object` ->
  `mStack[v1] == mStack[v2]`). No public API change.
