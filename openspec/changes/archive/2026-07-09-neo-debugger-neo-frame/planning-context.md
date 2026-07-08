# Planning Context — neo-debugger-neo-frame (TRUE COMPLETION)

> SEED. Neo debugger variable inspection: `DebugService` reads the Neo frame for
> `GetThisInfo` / `GetLocalVariableInfo` (currently return "not supported yet").
> Scope-AWARE: dump-gate whether var inspection is tractable or needs deep
> protocol work; ship the tractable slice, sequence the rest.

## What this change is

Under `ENABLE_NEO_MODE`, `DebugService`'s variable-inspection paths
(`GetThisInfo` at `:203-261`, `GetLocalVariableInfo` at `:263-300`) return
"Neo ... not supported yet" (`:207-209`, `:268-271`). They're built around the
Legacy `StackObject*` + `BasePointer` frame; the Neo frame is `byte* frameBase` +
`AutoList mStack` + `CompiledFrame.LocalInfos`. This child reads the Neo frame for
variable inspection. (The stacktrace instruction dump IS already Neo-adapted at
`:143-148` -- NOT deferred.)

## The dump-gate (binding -- the dump decides the scope)

On HEAD `50d0e7e2`:
1. What do `GetThisInfo` / `GetLocalVariableInfo` need (the `StackObject*` read +
   the field/type info)? Cite file:line. How does the Legacy path recover a
   variable's value + type from the frame?
2. The Neo frame: `byte* frameBase` + `AutoList mStack` (the ref region) +
   `CompiledFrame.LocalInfos` (`StackSlotInfo{Offset, RefOffset, Size, RefCount}`
   per slot). Can a Neo variable's value + type be recovered the SAME way the F-4
   indexer / the Step-13b `ReadNeoValueType` recover a Neo slot's value? (The F-4
   `ILTypeInstance` indexer fix shipped the per-field-TypeForCLR dispatch --
   mirror it for frame locals.)
3. **Scope-aware:** is var inspection SMALL (mirror the F-4 indexer / ReadNeoValueType
   dispatch against `frameBase + LocalInfos[i]` for the GetThisInfo/GetLocalVariableInfo
   methods) or LARGE (deep debugger-protocol work)? Ship the tractable slice (e.g.
   primitive + reference locals; defer CLR-struct / IL-VT locals if they need more).

## Authoritative prior context

1. `openspec/changes/archive/2026-07-08-neo-f4-reflection-on-neo/` -- the F-4
  `ILTypeInstance` indexer fix (the per-field-TypeForCLR dispatch: primitive -> Primitives;
   reference -> ManagedObjects; CLR-struct -> boxed; IL-VT -> tagged NIE). MIRROR this
   for frame-local inspection.
2. `openspec/changes/archive/2026-07-08-neo-step26-perf-validation/` -- Step 26 deferred
   the debugger (the D sub-surface); this is the follow-on.
3. `ILRuntime/Runtime/Debugger/DebugService.cs` (GetThisInfo/GetLocalVariableInfo +
   the Neo "not supported yet" guards + the already-Neo stacktrace dump `:143-148`) +
   the Neo frame model (`CompiledFrame.LocalInfos` + `byte* frameBase` + `AutoList mStack`).
4. `.trae/documents/neo-deferred-items.md` -- the neo-debugger-neo-frame row.

## Scope (TRUE COMPLETION -- ship the tractable slice; sequence the rest)

Ship Neo variable inspection for the common local shapes (primitive + reference +
CLR-struct-boxed locals) -- mirror the F-4 indexer / ReadNeoValueType dispatch
against the frame. Sequence IL-VT locals (need reconstruction) if they need more.
Capstone: a debug session that inspects a Neo frame's locals (primitive + reference)
-> the correct values, not "not supported yet". Adversarial: mutate a local's value
-> the inspection reflects it.

## Build + test (CRITICAL -- always `-f net8.0`)

The debugger is exercised via the debugger protocol (the CLI's debugger mode, or a
host-side debug self-check). Probe how the existing debugger tests run (is there a
NeoStep debugger filter? or a host-side DebugService self-check?). If no harness,
construct a host-side self-check mirroring the NeoStep25*Check pattern.

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep     # 226/0/0 regression
```
ALWAYS `-f net8.0`. `Debug_Neo` prints huge JIT output -- normal.

## Spec authoring traps
- `specs/neo-debugger/spec.md` (or neo-optimizer) delta PURE ASCII; SHALL-first. Legacy is the REFERENCE. Neo-only `#if ENABLE_NEO_MODE`. Legacy-neutral.

## Deliverables
`proposal.md`, `design.md` (the dump-gate verdict: var-inspection tractable scope +
the frame-local dispatch design, file:line-cited), `specs/<cap>/spec.md` (delta),
`tasks.md`. Success = a Neo frame's primitive+reference locals inspect correctly.
