# neo-recluster-4 -- ship log

Wave-2 child of `neo-overhaul` (branch `features/object-model-overhaul`).
PLANNER+IMPLEMENTER, BACKGROUND run. Neo = `ExecuteNeo` under `ENABLE_NEO_MODE`.

## Outcome
- Full Neo smoke: **4 -> 3** (StructTest6 flipped green). Truth = fresh full
  smoke (no filter), `Ran 948 tests, 3 failded, 20 ignored, 7 todos` (exit 127
  = known graceful Dict-NRE crash; summary emitted).
- NeoStep: **414/0** (no regression; the load-bearing gate for a
  CopyNeoCallArguments / CopyNeoCallThisBack change).
- Legacy-neutral: plain `Debug` build = 0 errors (all changes `#if
  ENABLE_NEO_MODE`-gated; ILIntepreter.Neo.cs / Optimizer.Neo.cs file-gated;
  NeoCallParamMap fields under the struct's `#if ENABLE_NEO_MODE`).
- NOT committed (LEAD commits).

## Fresh grounding (the CURRENT 4 on entry -- re-clustered)
| test | shape | verdict |
|------|-------|---------|
| StructTest6 | byref out-STRUCT ref-region write-back (0-prim+2-ref IL struct local as `out ILTypeInstance`) | FIXED this child |
| StructTest12 | `Activator.CreateInstance<T>` generic-param mis-resolution (T -> ILTypeInstance) + heap-not-struct return | REMAINS (deep, 2 coupled bugs) |
| UnitTest_10051 | constrained-callvirt property read on a nested struct field (`.x.RawValue` over inlined struct getter) | REMAINS (deep, multi-system) |
| MyTest.Test | boxed-CLR-struct enumerator interface dispatch, this-alias across loop iters (Step-19) | REMAINS (deep) |

Full table + pinned roots: `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-04.md`.

## Batch + pinned root (StructTest6)
`Dictionary<string, StructTest>` resolves to `Dictionary<string, ILTypeInstance>`
(IL struct as CLR generic arg -> boxed, BY DESIGN). So `TryGetValue(string, out
ILTypeInstance)` is a BYREF REFERENCE-typed param whose caller-side source is
the StructTest LOCAL (0 prim + 2 ref). The Area-4c frame-native CopyBlock
forward/write-back only touches the 4-byte ref-slot index -- never the struct's
REF region. The byref sibling of the FIXED neo-il-struct-box-call-boundary child
(which handled the BY-VALUE struct->ref param). Reuse that child's BOX/UNBOX
pattern + liveAliasMap (byref temp -> struct local) for a focused fix.

## Fix (files + lines, all Neo-gated -> Legacy-neutral)
1. `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- NeoCallParamMap:
   +`PrimitiveByRefBoxIlType` (ILType[]) / `PrimitiveByRefBoxSrcRefOff` (ushort[]).
2. `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs`:
   - Initobj seeding in the curVtTypes tracker (so a struct local zero-init'd
     via `initobj rLocal, StructType` keeps its type for the detection).
   - byref-box detection in the param-map build (byref ref-typed param +
     liveAliasMap trace to an IL-VT struct local) -> sets the box fields.
   - lazy-carry assignment of the new map arrays.
3. `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`:
   - CopyNeoCallArguments: FORWARD box for the byref-box case (Instantiate +
     CopyFrameToIL -> fresh mStack index).
   - CopyNeoCallThisBack: +`frameRefBase` param (5 call sites updated); WRITE-BACK
     unbox for the byref-box case (CopyILToFrame of the result ILTypeInstance ->
     caller struct prim + ref regions) instead of the 4-byte CopyBlock.

## Verify
- StructTest6 targeted: 1 ran / 0 failed (PASS).
- Full smoke: 4 -> 3 (StructTest6 gone; 3 survivors = strict subset).
- NeoStep: 414/0.
- Legacy build (plain Debug): 0 errors.

## Remaining (reported honestly)
3 deep singletons (StructTest12, UnitTest_10051, MyTest.Test) -- each a distinct
root, re-confirmed this child. See fullsmoke-ground-04.md for the pinned roots
and candidate future children.
