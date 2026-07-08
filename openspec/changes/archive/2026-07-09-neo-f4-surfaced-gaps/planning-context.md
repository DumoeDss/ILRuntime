# Planning Context — neo-f4-surfaced-gaps (2 small F-4-surfaced gaps, TRUE COMPLETION)

> SEED. Two small, reproducible gaps surfaced by the F-4 / parametrized-Run work.
> Both are real bugs with known reproducers. Ship both; this is a small focused fix.

## Gap A -- op_Equality null-operand autogen-binding AoRE

The autogen Neo bindings for `Type.op_Equality` / `String.op_Equality` throw
`ArgumentOutOfRangeException` when one operand is the Neo null sentinel (`mStack[-1]`).
Surfaced by F-4 path #2 (the `e.GetType()` probe's `t != null` hit
`System_Type_Binding.op_Equality_1_Neo` indexing `mStack[-1]`). Root: the autogen
`op_Equality_Neo` reads both operands as mStack indices without a null-sentinel check;
a null operand indexes `mStack[-1]`.

**Fix surface (dump-gate):** the autogen `op_Equality_Neo` (in the generated
`*Binding` / the autogen-emitter) SHALL null-check each operand (the Neo null
sentinel) before indexing mStack; a null operand -> the equality result is
`null == null` / `null == x` per CLR semantics. Find the autogen emitter
(`Runtime/CLRBinding/` code generator) + the runtime read.

## Gap B -- `new MyEx(string)` ctor string-arg mis-route

`new MyEx("msg")` (an IL exception ctor taking a string) stores `this` into the
string field instead of the arg. Surfaced by F-4 path #4 (the indexer probe
worked around it via direct field assignment). Root: the Neo newobj-ctor arg
marshal (or the Stfld in the ctor) mis-routes the string arg.

**Fix surface (dump-gate):** trace `new MyEx("msg")` -> the ctor's `stfld msg`
-> confirm the string arg reaches the field (not `this`). Likely a newobj-arg
marshal or a stfld-this-vs-arg confusion in the Neo newobj path (Step 18 /
VT-THIS-ADDR territory). The F-4 #4 indexer fix reads the field correctly; the
WRITE (via the ctor) is the gap.

## Dump-gate (binding -- for each gap, probe on HEAD `bc9f1020`, cite file:line)

For A: find the autogen op_Equality emitter + the runtime read; confirm the
null-operand AoRE; design the null-check fix (Neo-only, Legacy-neutral).
For B: trace `new MyEx("msg")` ctor's stfld; confirm the mis-route; design the
fix (likely the newobj-arg marshal or the stfld source).

Both are SMALL focused fixes. Ship both; if one is unexpectedly LARGE, sequence it.

## Build + test (CRITICAL -- always `-f net8.0`)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep14   # add the 2 probes
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep     # 224/0/0 regression
```
ALWAYS `-f net8.0`. `Debug_Neo` prints huge JIT output -- normal.

## Spec authoring traps
- `specs/neo-exceptions/spec.md` (or neo-optimizer) delta PURE ASCII; SHALL-first. Legacy is the REFERENCE. Neo-only `#if ENABLE_NEO_MODE` (for B; A's autogen fix may be in the emitter which is Neo-gated). Legacy-neutral.

## Deliverables
`proposal.md`, `design.md` (per-gap dump-gate verdict + fix, file:line-cited),
`specs/<cap>/spec.md` (delta), `tasks.md`. Success = both probes PASS (FAIL-on-HEAD).
