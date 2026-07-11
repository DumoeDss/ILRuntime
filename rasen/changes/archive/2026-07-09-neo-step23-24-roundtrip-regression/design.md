# Design - neo-step23-24-roundtrip-regression

> Minimal, Neo-gated, Legacy-neutral fixes for the two regressions. One fix
> per root cause. Dump-gate discipline: root cause established BEFORE the fix
> (see proposal.md + ship-log.md for the dumps).

## Fix #1 -- the InvalidCastException (NeoAssemblyWriter.BuildMethodDef)

### Site
`ILRuntime/Runtime/NeoAOT/NeoAssemblyWriter.cs`, `BuildMethodDef`, the
`ReturnTypeRefIdx` assignment (introduced by Step 25 S3-2, commit `f32f816e`).

### The bug
```csharp
rec.ReturnTypeRefIdx = retType != null
    ? b.IndexTypeRef(retType is ILType irt ? irt.TypeReference
        : module.ImportReference(((CLRType)retType).TypeForCLR), retType)
    : -1;
```
The false arm assumes every non-`ILType` `IType` is a `CLRType`. An OPEN
generic method's return (`GenericProbe<T>` -> `T`) is an
`ILGenericParameterType` (a third IType: not ILType, not CLRType, but it
carries a Cecil `TypeReference`). `(CLRType)retType` throws
`InvalidCastException`.

### The fix
Extract the return-type -> Cecil-`TypeReference` resolution into a helper that
handles all three IType kinds explicitly:
```csharp
static TypeReference ResolveReturnTypeCecilRef(IType retType, ModuleDefinition module)
{
    if (retType is ILType ilt) return ilt.TypeReference;
    if (retType is ILGenericParameterType gpt) return gpt.TypeReference;   // <-- the fix
    return module.ImportReference(((CLRType)retType).TypeForCLR);
}
```
and call it from `BuildMethodDef`. `IndexTypeRef`'s existing Kind byte logic
(`itype is CLRType ? Clr : Il`) already classifies `ILGenericParameterType`
as `Il` (it is not `CLRType`), which is correct for the `.neo` TypeRef table.

### Why minimal / correct
- `ILGenericParameterType.TypeReference` is exactly the Cecil
  `GenericParameter` token `TypeReferencePatchInfo.Create` already handles
  (it has a dedicated `IsGenericParameter` arm), so the roundtrip stays
  faithful.
- This is the SAME resolution path the HybridPatch
  `TypeReferencePatchInfo.Create(IType, ...)` already uses (it special-cases
  `ILGenericParameterType` -> `gpt.TypeReference`), so the writer now matches
  the established convention.
- The reader is unchanged (it just reads the int). No `NeoAssemblyReader`
  change.

## Fix #2 -- the IndexOutOfRangeException (Optimizer.Neo.LowerR1)

### Site
`ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs`, `LowerR1` (called
from `LowerNeoOffsets`'s `case Ret`, `Ldstr`, `Ldftn`, `Ldvirtftn`).

### The bug
`LowerR1` indexed `localInfos[r1].Offset` with no bounds check. For the empty
`.cctor` (void, no params, no locals, no stack temps), `localInfos.Length == 0`
but the `ret` op's `Register1 == 0` (a phantom: the JIT `Code.Ret` case only
sets `Register1` when `hasReturn`, so it stays at its default 0 for void
methods). `case Ret: if (op.Register1 >= 0) LowerR1(...)` then OOBs.

### The fix
Make `LowerR1` defensively treat an out-of-`localInfos`-range index as offset
0 -- the SAME defensive pattern the comparison/lowering sites elsewhere in
`LowerNeoOffsets` already use (`srcReg >= 0 && srcReg < localInfos.Length ?
localInfos[srcReg].X : 0`):
```csharp
int off1 = (r1 >= 0 && r1 < localInfos.Length) ? localInfos[r1].Offset : 0;
op.DstOffset = (ushort)off1;
```

### Why this is safe at runtime
`ExecuteNeo`'s `case Ret` (`ILIntepreter.Neo.cs`) reads `ip->DstOffset` ONLY
when `retDst != null && (returnPrimitiveSize > 0 || returnRefCount > 0)` --
i.e., only when there IS a return value. A void `.cctor` has
`returnPrimitiveSize == 0` and `returnRefCount == 0`, so the phantom
`DstOffset` is NEVER read. Setting it to 0 is harmless.

### Why the optimizer (not the JIT)
The JIT `Code.Ret` root cause (leaving `Register1 = 0` for void methods) is in
`JITCompiler.cs`, which is SHARED between Legacy and Neo. The optimizer
`LowerNeoOffsets`/`LowerR1` is Neo-only (`Optimizer.Neo.cs` is file-gated
`#if ENABLE_NEO_MODE`). Fixing at the optimizer keeps the change
Legacy-neutral automatically. The JIT root cause is noted as a forward-looking
observation (a future tightening could set `Register1 = -1` for void `Ret`),
but is not required here and would touch shared code.

## Fix #3 -- the stale test replication (NeoStep24CliRoundtripCheck)

### Site
`ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep24CliRoundtripCheck.cs`, the
replication loop that builds the EXPECTED partition (Cell3 counts + Cell5
fresh-body positional check).

### The bug (test-only, exposed by Fix #2)
The replication loop built `tm = GetMethods() + GetConstructors()` but did NOT
append the static `.cctor`. The driver (`NeoCompiler.CompileCore`) DOES append
it (via `GetStaticConstroctor()`) since Step 25 S3-4. While the `.cctor`
compile THREW (pre-Fix-#2), the driver skipped it, so `MethodDefs` (6) matched
`ngen` (6) and Cells 3/5 passed by accident. Once Fix #2 makes the `.cctor`
compile cleanly, the driver emits it (7 methods) but `ngen` is still 6 ->
`Cell3 counts+split: MethodDefs 7 vs ngen 6` and a positional
`Cell5 fresh-body: body mismatch for NestedMethod` (the cctor shifted every
later method's index). The test's expected partition was simply stale w.r.t.
the S3-4 driver change.

### The fix
Mirror the driver in the replication loop: append
`GetStaticConstroctor() as ILMethod` to `tm`. This is a TEST-ONLY change (the
file is `#if ENABLE_NEO_MODE && DEBUG`); it makes the test's expected
partition match the actual driver behavior.

## Non-changes (deliberately NOT done)

- The shared JIT `Code.Ret` emission. Touched only as a root-cause
  observation.
- Any `NeoAssemblyReader` change (the reader is symmetric already).
- Functional `.cctor` execution / deserialize->ExecuteNeo (Step-25 scope).
- Broadening `LowerR1R2`/`LowerR1R2R3` or the inline `localInfos[op.Register1]`
  sites at Box/Unbox/Ldstr/Ldftn. Those opcodes carry real local-slot
  registers (a Ldstr dest is a real local ref slot), so they cannot reach here
  with a phantom index; only void-`Ret` is the phantom case. Minimal scope.
