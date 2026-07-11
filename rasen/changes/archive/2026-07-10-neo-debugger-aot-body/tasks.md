# Tasks - neo-debugger-aot-body (child 12)

> Status: DONE (all green; stash-toggle-verified; LEAD-verify pending).
> See `design.md` for the full design + scope boundary.

## 1. Probe + gap confirmation
- [x] 1.1 Read the shipped debugger children (`neo-debugger-neo-frame` + `neo-debugger-
        ilvt-local` ship-logs/designs): the Neo frame read inspects locals via
        `Definition.Body.Variables[i]` (type/name) + `CompiledFrame.LocalInfos[paramCnt+i]`
        (slot layout).
- [x] 1.2 Read `DebugService.GetLocalVariableInfo` / `GetThisInfo` Neo arms +
        `InitCodeBodyFromNeo` (the AOT deserialize). Confirm the `.neo`
        `NeoMethodDefRecord` carries `LocalInfos[]` but NO local type/name table;
        `InitCodeBodyFromNeo:996` sets `localVarCnt = LocalInfos.Length` (over-count
        bug).
- [x] 1.3 Construct a reproducer (TEMPORARY `NeoDebuggerAotBodyDiag`): compile the
        probe to a `.neo`, S1 Attach -> confirm AOT-body locals render correctly on
        S1 (Cecil present) but the S1 `localVarCnt` is over-counted (4 vs 2).
- [x] 1.4 Construct the S3-2 Cecil-free reproducer (`.neo` -> fresh AppDomain B):
        confirm `.LocalInfo = [<null>]` on HEAD (the whole inspection block NRE's
        via `GetThisInfo`'s `TypeDefinition.Fields` on a Cecil-free type).

## 2. The V4 `.neo` LocalVariables table
- [x] 2.1 `NeoAssembly.cs`: bump `NeoAssemblyFormat.Version` 3 -> 4 (additive V4
        comment; note the child-8 V4 conflict -> re-routed to V5).
- [x] 2.2 `NeoAssembly.cs`: add `NeoLocalVarRecord { int TypeRefIdx; string Name; }`
        + `NeoMethodDefRecord.LocalVariables` (NeoLocalVarRecord[]).
- [x] 2.3 `NeoAssemblyWriter.cs`: `BuildLocalVars` (read Cecil Body.Variables ->
        TypeRef idx + debug name) + `WriteNeoLocalVars` (count-prefixed pairs);
        wire into `BuildMethodDef` + `WriteMethodDef` (trailing, after EH).
- [x] 2.4 `NeoAssemblyReader.cs`: `ReadNeoLocalVars` (mirror); wire into
        `ReadMethodDef`.
- [x] 2.5 `ILMethod.cs`: fields `neoAotLocalTypes`/`neoAotLocalNames` +
        `HasNeoAotLocalMeta` + `GetNeoAotLocalType(i)`/`GetNeoAotLocalName(i)`.
- [x] 2.6 `ILMethod.InitCodeBodyFromNeo`: populate `neoAotLocalTypes/Names` via the
        `resolveCatchType` closure; FIX `localVarCnt` to the DECLARED-local count
        (`rec.LocalVariables.Length`, fallback `LocalInfos.Length - paramCnt -
        stackRegisterCount`).

## 3. Debugger read
- [x] 3.1 `DebugService.GetLocalVariableInfo` Neo arm: pick type/name source by
        `useAotMeta = m.HasNeoAotLocalMeta` (AOT body -> `.neo` table; JIT -> Cecil).
        Slot layout + value read (`ReadNeoLocalValue`) UNCHANGED.
- [x] 3.2 `DebugService.GetThisInfo` Neo arm: crash-guard for a Cecil-free shell
        (`Definition == null` -> placeholder string) so the ctor's single try/catch
        does not swallow `GetLocalVariableInfo`. AOT-body THIS-inspection is a
        follow-on.

## 4. Capstone gate
- [x] 4.1 `NeoDebuggerAotBodyCheck` (10 cells): compile+Attach (S1) -> 5 value cells
        (primitive/ref/long/IL-VT + adversarial mutation) + isNeoAotBody flag +
        body-mutation cell (mutate deserialized prim Ldc -> assert MUTATED in
        .LocalInfo) + S3-2 CF shell meta cell (HasNeoAotLocalMeta + type/name
        resolution).
- [x] 4.2 CLI hook (`Program.cs` mode `NeoDebuggerAotBody`).
- [x] 4.3 Stash-toggle: disable V4 serialization (`BuildLocalVars -> empty`) ->
        10/10 -> 3/10 (value cells + body-mutation + CF meta fail). Restore -> 10/10.

## 5. Verification (all green)
- [x] 5.1 `NeoDebuggerAotBody`: 10/10.
- [x] 5.2 `NeoDebuggerFrame`: 6/6 held (no regression).
- [x] 5.3 `NeoStep` smoke: 263/0/0 held.
- [x] 5.4 Cecil-free load checks held: `NeoStep25LoadExec` 28/28, `NeoStep25CecilFreeLoad`
        7/7, `NeoStep25CecilFreeMultiHotfix` 5/5.
- [x] 5.5 Legacy-neutral: plain `Debug` build of ILRuntime + CLI = 0 errors.
- [x] 5.6 Pre-existing failures confirmed NOT caused by this change (stash + HEAD
        re-run): `NeoStep24CliRoundtrip` 1/5 (lead-7 doc), `NeoStep25CecilFreeGeneric`
        G1/G2 (PARKED child-8 signal).

## 6. Cleanup
- [x] 6.1 Removed the temporary `NeoDebuggerAotBodyDiag` + its CLI hooks.
- [x] 6.2 `design.md` + this `tasks.md` written (PURE ASCII).

## Follow-ups (out of scope, sequenced)
- AOT-body THIS-inspection on a Cecil-free shell (walk a Cecil-free ILType's fields
  via `fieldTypes[]`/`fieldOffsets[]`/`fieldMapping` + the `.neo` FieldRef names).
- Cecil-free reference-local VALUE (a Cecil-free-EXECUTION gap -- the Stloc-ref
  mStack persistence; NOT a debugger gap).
- Cecil-free IL-VT-local type resolution (needs the nested IL value type emitted
  into the `.neo` -- closure completeness).
- Stale-autogen Neo binding regen sweep (cross-cutting; lead-7 finding 8).
