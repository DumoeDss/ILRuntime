## Why

Four small, independent unimplemented-opcode gaps in the Neo interpreter
(`ExecuteNeo`, `ILIntepreter.Neo.cs`) each surface in the full Neo smoke as a
`NotImplementedException`. Per the lead-11 re-scope frequency table they account
for **5 + 4 + 2 + 2 = 13 full-smoke hits combined**:

| hits | opcode / message | current outcome |
|------|------------------|-----------------|
| 5 | `Ldsflda` (load static field address) | Step-6 default (`Neo: opcode ... not yet implemented`) |
| 4 | `Conv_R_Un` (`conv.r.un`, unsigned int -> float) | Step-6 default |
| 2 | `Switch` (jump table) | Step-6 default |
| 2 | `Unbox` of a boxed IL enum | tagged NIE `Neo: unsupported CLR primitive for Unbox: ILEnumTypeInstance` |

None is a correctness landmine (each fails loud); together they are a clean batch
of mirror-Legacy ExecuteNeo arms. Steps 1-18 + children 1-6 cleared the larger
gaps; these are the remaining low-frequency opcode holes blocking the broader
Neo-overhaul functional-completion mandate (full autonomy, `--no-gate`).

## What Changes

Four independent `ExecuteNeo` arms, each mirroring the Legacy (`ExecuteR`,
`ILIntepreter.Register.cs`) semantics 1:1, all Neo-gated (`#if ENABLE_NEO_MODE`):

- **Conv_R_Un** (`ILIntepreter.Register.cs:1258` mirror): add the
  `case OpCodeREnum.Conv_R_Un:` arm to ExecuteNeo's conv cluster (beside
  `Conv_R4`/`Conv_R8` at `ILIntepreter.Neo.cs:2726`). Read the source interpreted
  UNSIGNED via the existing `ReadConvU4`/`ReadConvU8` helpers, widen to `double`,
  write `*(double*)(frameBase + ip->DstOffset)`. The opcode is ALREADY fully
  Neo-lowered (`Operand2` tag stamped at `JITCompiler.cs:975`; result type =
  `DoubleType` at `JITCompiler.cs:1466`; visited by `LowerNeoOffsets` at
  `Optimizer.Neo.cs:589`) -- only the dispatch arm is missing.

- **Switch** (`ILIntepreter.Register.cs:2754` mirror): add the
  `case OpCodeREnum.Switch:` arm. Read the integer index, look up
  `method.JumpTablesRegister[ip->Operand]`, bounds-check, and
  `ip = ptr + table[idx]; continue;` (out-of-range falls through, no jump). The
  jump table is already populated (`PrepareJumpTable`, `JITCompiler.cs:2174/2252`;
  table targets remapped by `LowerNeoOffsets` at `Optimizer.Neo.cs:1728`).

- **Unbox of a boxed IL enum** (Neo bug fix, Legacy mirror): the Neo Unbox arm's
  CLR-primitive branch (`ILIntepreter.Neo.cs:4390-4394`) calls
  `NeoWritePrimitiveToFrame(obj, ...)` without guarding `obj is ILEnumTypeInstance`,
  so unboxing a boxed IL enum to its underlying primitive throws the
  `:6339` NIE. Add the `!isEnumObj` guard Legacy uses (`Register.cs:4046`) and
  extract the enum's underlying value (mirror `Register.cs:4129` /
  `res.CopyToRegister(0, ...)`).

- **Ldsflda** (`ILIntepreter.Register.cs:3348` mirror): add the
  `case OpCodeREnum.Ldsflda:` arm. Produce a Neo byref (the 8-byte
  `(objIdx, off|flag)` pair) that addresses the static field. For an IL static
  field, materialize `ilType.StaticInstance` into `mStack` and emit
  `(mStackIdx, fieldPrimitiveOffset)` -- this reuses the EXISTING `Stind_*`/`Ldind_*`
  heap-IL arms (which already do `mStack[objIdx] is ILTypeInstance` ->
  `ins.Primitives[off]`/`ins.ManagedObjects[off]`, `ILIntepreter.Neo.cs:4863-4906` &
  `5143-5165`) with zero consumer changes. CLR-static `Ldsflda` is deferred with a
  tagged NIE unless diagnose-first shows a live hit (see design).

Each change gets one NeoStep probe that **FAULTs** today (uncaught NIE) and PASSES
with the fix (child-1/2/3/4/6 probe discipline: pass criterion = "ran without
throwing"; wrong-value probes do not fail).

## Capabilities

### New Capabilities
<!-- none -- all four are additions to existing capabilities -->

### Modified Capabilities
- `neo-optimizer`: ADDED requirement -- `Conv_R_Un` and `Switch` ExecuteNeo arms
  (the conv-cluster and branch ExecuteNeo arms + the `LowerNeoOffsets` opcode
  families are this capability's domain).
- `neo-type-checks`: ADDED requirement -- Unbox of a boxed IL enum (the
  Unbox/Isinst/Castclass Step-15 arms are this capability's domain).
- `neo-byref`: ADDED requirement -- `Ldsflda` static-field-address byref (the
  Ldflda/Stind/Ldind/byref Step-17 arms are this capability's domain).

## Impact

- **Code**: `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (4 new
  ExecuteNeo arms + the Unbox guard). Possibly `Optimizer.Neo.cs` only if
  diagnose-first finds `Switch`/`Ldsflda` need a `LowerNeoOffsets` case-list entry
  (expected NOT needed: both are already visited -- Switch at `:1728`, Ldsflda
  shares Ldsfld's path). No JIT change expected (all encodings confirmed present).
- **Tests**: one new `TestCases/NeoMiscOpTest.cs` with 4 NeoStep probes
  (`NeoStepMiscOp_ConvRUn`, `_Switch`, `_UnboxEnum`, `_Ldsflda`).
- **No API change, no dependency change, no Legacy behavior change** (all edits
  under `#if ENABLE_NEO_MODE` => Legacy-neutral by construction).
- **Baseline**: NeoStep smoke 320/0 (after child 6) -> 320+4/0 expected. Full Neo
  smoke (no filter, pre-crash): the 4 gap messages each drop to 0.
