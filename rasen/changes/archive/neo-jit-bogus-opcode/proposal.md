## Why

The Neo interpreter's dispatch loop reads `OpCodeREnum code = ip->Code;` at
`ILIntepreter.Neo.cs:1410` and throws `"Neo: opcode {0} not yet implemented (Step 6)"`
in its `default:` arm (line 5357) when the value is unhandled. On the full Neo smoke
(no `NeoStep` filter) the runtime prints **`opcode 2359324`** (~8 consistent pre-crash
sites). `2359324 = 0x23F70C` is **not** a named `OpCodeREnum` member -- the enum is
all-implicit `0..328` with zero explicit-value members -- so the value is **garbage**,
not a missing-feature opcode. Legacy `ExecuteR` is green on the same test DLL, so the
defect is **Neo-only** (it lives in the Neo JIT-lowering pipeline that Legacy never
runs). This is a **correctness** bug, not a TODO: a garbage `Code` could silently alias
a real opcode and corrupt execution, and it is the first blocker for the broader Neo
opcode/correctness overhaul (it must be fixed before the missing-opcode children are
even visible).

## What Changes

- **Root-cause the garbage `Code`** via an instrumented `default:` case that logs the
  declaring method, the body index `(int)(ip - ptr)`, the raw `Code`, the full 24-byte
  `OpCodeR` field dump, and `body.Length` (to distinguish an in-range corrupt slot from
  an out-of-bounds `ip` overrun). Capture the minimal triggering method(s).
- **Fix the Neo lowering defect** that produces the bad value. The investigation points
  at the Neo-only post-translation passes (`TypeSpecializeNeoOpcodes`,
  `Optimizer.LowerNeoOffsets` in `Optimizer.Neo.cs`): `LowerNeoOffsets` is the sole pass
  that **mutates body length** -- it deletes synthetic `Push` instructions (in-place
  shift + `Array.Resize`, lines ~1204-1215) and re-maps branch / switch targets via
  `FixBranchTargetsAfterRemove` (line ~1662). A mis-remap there lands `ip` at a wrong
  body index or past the body end, and the **un-bounded** `ExecuteNeo` loop
  (`while(!returned)` with an unconditional `ip++`, no bounds check) then reads garbage
  silently.
- **Add a dispatch safety guard** so an out-of-range `Code` (or an `ip` past `body.Length`)
  becomes a **loud, locatable** throw carrying the method + body index, instead of a
  misleading "not yet implemented" message or silent aliasing. This is defense-in-depth
  that stays useful as a regression tripwire after the root-cause fix.
- **Regression probe** in `TestCases/` exercising the trigger (the `Push`-deletion path
  fires only for calls / `newobj` with more than 3 register parameters, so the probe
  targets multi-argument calls followed by control flow) whose result depends on the
  opcode stream executing correctly.

All changes are gated `#if ENABLE_NEO_MODE` (the lowering passes and `ExecuteNeo` are
already Neo-only), so Legacy `ExecuteR` is byte-identical -- Legacy-neutral by
construction.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-optimizer`: Adds a new requirement that the Neo-lowered body
  (`frame.NeoExecuteBody`, the `OpCodeR[]` `ExecuteNeo` runs) is **dispatch-safe**: every
  `Code` field is a named `OpCodeREnum` member, no branch / switch / `Leave` target lands
  outside the body, and the body is terminable. The `LowerNeoOffsets` `Push`-deletion +
  `FixBranchTargetsAfterRemove` length-mutation is the load-bearing site. The requirement
  also records the `ExecuteNeo` dispatch-loop bounds guard as the defense-in-depth
  tripwire that turns any future regression into a precise failure.

## Impact

- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` -- primary suspect:
  `LowerNeoOffsets` `Push`-deletion block (~1204-1215) and `FixBranchTargetsAfterRemove`
  (~1662); the actual root-cause fix lands here (or in `JITCompiler.cs`'s
  `TypeSpecializeNeoOpcodes` / branch-target resolution if the reproducer redirects).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- `default:` arm
  instrumentation is replaced by a permanent dispatch guard (bounds + named-range check)
  at the loop head (~1410) / `default:` (~5357).
- `TestCases/` -- new `NeoStep*Test.cs` regression probe(s).
- No public API changes. No dependency changes. Legacy (`ExecuteR`) untouched.
- Build/test surface: `Debug_Neo` CLI build + `NeoStep` smoke (baseline **301/0/0**);
  reproducer step additionally drops the `NeoStep` filter to run the full (crash-prone)
  smoke and capture the trigger before the crash.
