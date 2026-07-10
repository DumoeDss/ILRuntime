# Design - neo-debugger-aot-body (child 12)

> Scope: Neo debugger LOCAL-variable inspection for an AOT-loaded body (a `.neo`
> deserialized + bound via `NeoAssemblyLoader.Attach`). Extends the shipped
> `neo-debugger-neo-frame` (JIT-path frame read) + `neo-debugger-ilvt-local`
> (IL-VT local reconstruction) to the AOT-body path. Probed-by-reasoning +
> a constructed reproducer on HEAD `e7e4e5a8`, then fixed + stash-toggle-verified.
> PURE ASCII; SHALL-first. Legacy is the REFERENCE; every shipped edit is Neo-only
> (`#if ENABLE_NEO_MODE`).

## 0. The two AOT-body paths (established by reading the code)

An AOT-loaded ILMethod (`isNeoAotBody == true`) is created by ONE of two paths:

- **S1 same-AppDomain Attach** (`NeoAssemblyLoader.Attach`): operates on an
  EXISTING Cecil-built ILMethod. `InitCodeBodyFromNeo` overwrites `CompiledFrame`
  field-by-field + sets `isNeoAotBody`, BUT `def` (the Cecil MethodDefinition)
  stays NON-null. So `m.Definition.Body.Variables` is STILL accessible. This is
  the "load a `.neo` into the same app that compiled it" scenario.

- **S3-2 Cecil-free load** (`AppDomain.LoadNeoAssembly` into a FRESH AppDomain):
  methods are `CreateFromNeoShell` shells with `def == null`. The Cecil-reading
  surfaces (Name/HasThis/etc.) return recorded shell data; `Definition` returns
  null. `m.Definition.Body.Variables[i]` NRE's.

## 1. The gap (confirmed on HEAD by a constructed reproducer)

The shipped Neo debugger frame read (`DebugService.GetLocalVariableInfo` Neo arm,
`:355-381`) reads each local's TYPE+NAME from Cecil:
```
var lv = m.Definition.Body.Variables[i];   // :359 -- Cecil VariableDefinition
IType lt = ResolveLocalType(m, lv, domain);
...
m.Definition.DebugInformation.TryGetName(lv, out vName);  // :366 -- Cecil debug info
```

**S1 path (HEAD):** Cecil present -> the read WORKED for every local shape
(primitive / reference / long / IL-VT), confirmed by a reproducer that compiled
the probe to a `.neo`, Attach'ed it, drove each throw-probe, and dumped
`.LocalInfo`. ALL values rendered correctly. The ONLY defect on the S1 path was a
LATENT `localVarCnt` over-count: `InitCodeBodyFromNeo:996` set `localVarCnt =
LocalInfos.Length` (which holds `paramCnt + varCnt + stackRegisterCount`), NOT
the declared-local count. This made the debugger loop over-iterate past
`Definition.Body.Variables.Count` -- the extra iterations threw
`ArgumentOutOfRangeException` and were swallowed by the per-iteration `catch`
(`:375`), so it was HARMLESS on HEAD but a latent bug (and it meant
`m.LocalVariableCount` was wrong for any AOT-body consumer).

**S3-2 Cecil-free shell path (HEAD):** `m.Definition == null` ->
`m.Definition.Body.Variables[i]` NRE's. The NRE is thrown on the FIRST iteration
inside the loop's `try`, swallowed by the per-iteration `catch` -> the loop
renders NOTHING -> `GetLocalVariableInfo` returns an EMPTY string. AND because
`ILRuntimeException.ctor` (`ILRuntimeException.cs:34-45`) wraps
`GetThisInfo` + `GetLocalVariableInfo` in ONE `try/catch`, and `GetThisInfo`
(NEo arm, `:233 nInstance.Type.TypeDefinition.Fields`) ALSO NRE's on a
Cecil-free type (`TypeDefinition == null`), the ctor's outer catch swallows the
WHOLE block -> `.LocalInfo` stays NULL. Reproducer confirmed: HEAD CF path
returns `.LocalInfo = [<null>]`.

So the CORE gap is: **on a Cecil-free AOT shell, the debugger has no source for
the local type/name** (the slot LAYOUT is already Cecil-independent in
`LocalInfos`). The `.neo` `NeoMethodDefRecord` carried `LocalInfos[]` (offsets/
sizes) but NO local type/name table.

## 2. The fix (V4 `.neo` format -- serialize + deserialize + resolve + read)

### 2.1 The `.neo` V4 LocalVariables table (additive)

`NeoAssemblyFormat.Version` 3 -> 4. A new per-method table,
`NeoMethodDefRecord.LocalVariables` (`NeoLocalVarRecord[]`, one entry per
DECLARED local -- `varCnt`, the SAME count as Cecil `Body.Variables.Count`):
```
internal struct NeoLocalVarRecord { public int TypeRefIdx; public string Name; }
```
- `TypeRefIdx` -> the TypeRef table (the SAME table catch/return types use),
  resolved to a runtime `IType` at load. -1 = unavailable (renders as
  `<unknown local type>`, mirroring the JIT-path null-type guard).
- `Name` -- the Cecil debug name (or `"v" + index`), the SAME fallback the
  JIT-path debugger uses (`DebugService.cs:366-367`).
- EMPTY array for a method with no locals (never null on the wire; length is
  always exactly `varCnt`).

The slot LAYOUT stays in `LocalInfos[paramCnt + i]` (Cecil-independent). So a
local's FULL inspection = `LocalVariables[i]` (type+name) +
`LocalInfos[paramCnt + i]` (offsets/sizes) -- the SAME pair the JIT path uses
(`Definition.Body.Variables[i]` + `LocalInfos[paramCnt + i]`).

**V4 bump rationale + conflict note:** the PARKED child 8
(`neo-aot-generic-cecilfree`) originally planned the V4 bump for a
GenericParamNames table. This child claims V4; child 8 is re-routed to
`neo-aot-generic-cecilfree-backhalf` and will use V5. Recorded in the V4
comment + this design.

### 2.2 Serialize (`NeoAssemblyWriter`)

- `BuildLocalVars(method, b, module)`: reads `method.Definition.Body.Variables`
  (Cecil-present at serialize -- BuildMethodDef runs in the COMPILING AppDomain),
  indexes each local's `VariableType` via `b.IndexTypeRef` (the SAME helper
  catch/return/template types use), resolves the debug name via
  `DebugInformation.TryGetName`. Populated in `BuildMethodDef`.
- `WriteNeoLocalVars(bw, lvs)`: count-prefixed `(TypeRefIdx, Name)` pairs.
  Added to `WriteMethodDef` AFTER `ExceptionHandlers` (trailing, additive).

### 2.3 Deserialize (`NeoAssemblyReader`)

- `ReadNeoLocalVars(br)`: the mirror. Added to `ReadMethodDef` AFTER
  `ExceptionHandlers`. Empty array when count == 0.

### 2.4 Resolve into ILMethod (`InitCodeBodyFromNeo`)

- New ILMethod fields `neoAotLocalTypes` (`IType[]`) + `neoAotLocalNames`
  (`string[]`), populated by `InitCodeBodyFromNeo` via the SAME `resolveCatchType`
  closure (TypeRef idx -> runtime IType) the catch-type resolver uses. A miss ->
  null (rendered as `<unknown local type>` upstream).
- `HasNeoAotLocalMeta` (true iff `neoAotLocalTypes != null`) +
  `GetNeoAotLocalType(i)` / `GetNeoAotLocalName(i)` accessors (bounds-safe).
- **Bug fix:** `localVarCnt` is now the DECLARED-local count. The authoritative
  source is `rec.LocalVariables.Length` (V4). Fallback (a pre-V4 record -- rejected
  by the Version guard anyway): `LocalInfos.Length - paramCount -
  stackRegisterCount`. This corrects the latent over-count.

### 2.5 Debugger read (`GetLocalVariableInfo` Neo arm)

The loop now picks the type/name source by `useAotMeta = m.HasNeoAotLocalMeta`:
- AOT body (S1 Attach OR S3-2 shell): type/name from the `.neo` table
  (`GetNeoAotLocalType` / `GetNeoAotLocalName`).
- JIT path (no `.neo`): type/name from Cecil (`Definition.Body.Variables`).

The slot layout + value read (`ReadNeoLocalValue`) is UNCHANGED -- it already
reads `LocalInfos[paramCnt + i]` (Cecil-independent) for both paths. So the
per-shape value dispatch (primitive / reference / CLR-struct / IL-VT) is shared.

### 2.6 `GetThisInfo` Neo arm crash-guard (scope boundary)

The ctor calls `GetThisInfo` BEFORE `GetLocalVariableInfo`
(`ILRuntimeException.cs:36-40`). On a Cecil-free shell, `GetThisInfo`'s
`:233 nInstance.Type.TypeDefinition.Fields` NRE's -> the ctor's outer catch
swallows the WHOLE block -> `GetLocalVariableInfo` is NEVER reached, even after
the metadata fix. So a crash-guard is a PREREQUISITE for the core
(local-variable inspection) to be observable on the CF path:
```
if (m is ILMethod ilmThis && ilmThis.Definition == null)
    return "<this: Cecil-free AOT body (inspection deferred)>";
```
This short-circuits THIS-inspection on a Cecil-free shell to a placeholder
(AOT-body THIS-inspection is a follow-on child -- out of scope here; the local-
variable inspection is the core). It does NOT change the JIT path or the S1
Cecil-present path (both have a Definition).

## 3. The capstone gate (`NeoDebuggerAotBodyCheck`, 10 cells)

Host-side DEBUG+Neo self-check (CLI mode `NeoDebuggerAotBody`). Compiles the
`NeoDebuggerFrameProbe` to a `.neo`, Attach'es it (S1 path -> AOT bodies), then
drives each throw-probe + asserts `.LocalInfo` carries the CORRECT live values:
- 5 value cells (primitive / reference / long / IL-VT + adversarial mutation) --
  the SAME shapes the JIT-path `NeoDebuggerFrameCheck` asserts, now read off the
  AOT body.
- `isNeoAotBody` flag cell.
- **body-mutation cell (load-bearing):** mutate the deserialized ProbeThrow
  body's `prim` Ldc_I4 (12345, the SOLE assignment -- no later overwrite) to
  70707 before Attach -> assert `.LocalInfo` shows 70707, NOT 12345. PROVES the
  inspector reads the genuine AOT body's frame bytes (a JIT-fallback read would
  surface 12345).
- **S3-2 Cecil-free shell meta cell (the V4 pipeline gate):** compile +
  Cecil-free-load into a FRESH AppDomain (NO Cecil module) -> verify the shell
  methods HAVE the deserialized local meta (`HasNeoAotLocalMeta` +
  `GetNeoAotLocalName("prim"/"msg")` + `GetNeoAotLocalType` resolves to
  Int32/String). This is the serialize -> deserialize -> resolve pipeline on the
  genuine Cecil-free path. (The reference-local VALUE on the CF path is a
  SEPARATE Cecil-free-execution gap -- see Durable finding 3.)

**Stash-toggle (load-bearing):** disabling the V4 serialization
(`BuildLocalVars -> empty array`) drops the gate 10/10 -> 3/10 (all 5 value
cells, the body-mutation cell, AND the CF meta cell fail: `li=[]` +
`HasNeoAotLocalMeta=false`). Restoring -> 10/10. Binds the V4 table to the
behavior.

## 4. Scope boundary

- **AOT-body LOCAL-variable inspection (type + name + value):** the core. SHIPPED.
  Works on BOTH the S1 path (full value fidelity for every shape) AND the S3-2
  shell path (type/name/primitive-value fidelity; reference-local VALUE is
  blocked by a separate engine gap -- finding 3).
- **AOT-body THIS-inspection on a Cecil-free shell:** SEQUENCE. The crash-guard
  (`:220-230`) makes it a non-throwing placeholder so the core is reachable.
  Full reconstruction (walk a Cecil-free ILType's fields via its installed
  `fieldTypes[]`/`fieldOffsets[]`/`fieldMapping` + the `.neo` FieldRef names) is
  a follow-on child.
- **Cecil-free reference-local VALUE:** SEQUENCE (a Cecil-free-execution gap,
  NOT a debugger gap -- finding 3).
- **Cecil-free IL-VT local type resolution:** SEQUENCE. A nested IL value type
  (e.g. the probe's `VtLocal`) must be IN the `.neo` (closure completeness) for
  its TypeRef to resolve in the CF domain; it currently renders
  `<unknown local type>` on the CF path. On the S1 path it resolves + the field-
  walk works.

## 5. Verification plan (all green)

- `NeoDebuggerAotBody`: 10/10 (stash-toggle 10/10 -> 3/10 -> 10/10).
- `NeoDebuggerFrame`: 6/6 held (no regression to the JIT-path gate).
- `NeoStep` smoke: 263/0/0 held (no regression).
- Cecil-free load checks held: `NeoStep25LoadExec` 28/28,
  `NeoStep25CecilFreeLoad` 7/7, `NeoStep25CecilFreeMultiHotfix` 5/5 (the V4
  format change is additive + does not break Cecil-free load).
- Legacy-neutral: plain `Debug` build of ILRuntime + CLI = 0 errors (all Neo
  arms under `#if ENABLE_NEO_MODE`; the V4 format + Version bump compile out).
- Pre-existing failures NOT caused by this change (confirmed by stash + re-run on
  HEAD): `NeoStep24CliRoundtrip` 1/5 (documented in lead-7 handoff),
  `NeoStep25CecilFreeGeneric` G1/G2 (the PARKED child-8 forward signal).

## 6. Durable findings (carry forward)

1. **The S1 same-AppDomain Attach path keeps Cecil.** `InitCodeBodyFromNeo`
   overwrites `CompiledFrame` + sets `isNeoAotBody`, but `def` (Cecil
   MethodDefinition) stays NON-null. So `m.Definition.Body.Variables` is
   accessible on S1. Only the S3-2 `CreateFromNeoShell` path has `def == null`.
   The lead-6 "`registerSymbols` null on AOT" framing was imprecise:
   `registerSymbols` is NOT what the debugger reads (it reads
   `Definition.Body.Variables` + `LocalInfos`); the real gap was the Cecil-free
   shell's null `Definition`.
2. **`ILRuntimeException.ctor` wraps `GetThisInfo` + `GetLocalVariableInfo` in
   ONE try/catch** (`:34-45`). A crash in `GetThisInfo` (called FIRST) swallows
   the whole block, so `GetLocalVariableInfo` is never reached. ANY future fix
   to `GetLocalVariableInfo` on a path where `GetThisInfo` also throws MUST
   crash-guard `GetThisInfo` first (or the fix is unobservable). The ctor's
   ordering is a hidden prerequisite.
3. **Cecil-free reference-local VALUE is a SEPARATE engine gap, NOT a debugger
   gap.** On the S3-2 path, a reference local (`string msg`) reads `null` even
   though its TYPE/NAME resolve correctly + primitive locals read correctly. The
   string LITERAL resolves (a field assignment `FRef = "hi"` works in the CF
   domain -- proven by `NeoStep25CecilFreeLoad`'s `Compute()`). The gap is
   specifically the reference-LOCAL slot's mStack persistence on the CF path
   (likely a Stloc-ref token/mStack-index convention diff vs the Cecil path).
   This is a Cecil-free-EXECUTION follow-up, not a debugger-meta follow-up.
4. **`localVarCnt` must be the DECLARED-local count, NOT `LocalInfos.Length`.**
   `LocalInfos` holds `[this] + params + locals + stackRegisters`; using its
   Length as the local count over-reports. The JIT path sets it from
   `def.Body.Variables.Count` (`ILMethod.cs:456,805`); the AOT path must match
   (now from `rec.LocalVariables.Length`).
5. **A nested IL value type local needs CLOSURE COMPLETENESS to resolve on the
   CF path.** `TestCases.VtLocal` (the probe's IL struct) is not in the `.neo`
   (the compile closure does not pull it as a standalone TypeDef), so its TypeRef
   does not resolve in the CF domain -> `<unknown local type>`. A future CF
   IL-VT-local cell needs the nested type emitted into the `.neo`.
6. **V4 is claimed by this child.** The PARKED `neo-aot-generic-cecilfree`
   (child 8) is re-routed to `neo-aot-generic-cecilfree-backhalf` + V5. Any
   `.neo`-format consumer that recorded a V3 assumption must handle V4 (the
   Version guard rejects a mismatched `.neo` -- all in-tree readers use the
   shared `NeoAssemblyFormat.Version`).
