# Ship Log — neo-debugger-aot-body (child 12)

**Date:** 2026-07-10  **Capability:** neo-debugger  **Wave:** completion-3, child 12 (second debugger
child)  **Status:** SHIPPED (LEAD-verified).

## Delivered
**Debugger variable inspection for AOT-loaded methods** (a V4 `.neo` format change). lead-6's
"`registerSymbols` null on AOT" framing was imprecise — the debugger reads `Definition.Body.Variables`
+ `LocalInfos`, not `registerSymbols`. The real gap: the **S3-2 Cecil-free shell** (`def == null`) →
`Definition.Body.Variables[i]` NREs → `.LocalInfo` null. (S1 Attach keeps Cecil, already worked.)
**Fix = serialize local-var metadata into the `.neo` (Version 3 → 4):**
- **Serialize** (`NeoAssemblyWriter`): `BuildLocalVars` reads Cecil `Body.Variables` → TypeRef index +
  debug names; `WriteNeoLocalVars`; wired into `BuildMethodDef`/`WriteMethodDef`.
- **Deserialize** (`NeoAssemblyReader`): `ReadNeoLocalVars`; wired into `ReadMethodDef`.
- **Resolve** (`ILMethod.InitCodeBodyFromNeo`): populates new `neoAotLocalTypes`/`neoAotLocalNames`
  (via the `resolveCatchType` closure); `HasNeoAotLocalMeta`/`GetNeoAotLocalType`/`GetNeoAotLocalName`
  accessors; **fixes `localVarCnt`** to the declared-local count (`rec.LocalVariables.Length`) — was
  over-counted (`LocalInfos.Length` includes params + stack regs).
- **Debugger read** (`DebugService.GetLocalVariableInfo` Neo arm): selects the type/name source via
  `useAotMeta = m.HasNeoAotLocalMeta` (AOT body → `.neo` table; JIT → Cecil). Slot layout +
  `ReadNeoLocalValue` unchanged.
- **`GetThisInfo` crash-guard**: short-circuits on the Cecil-free shell (`Definition == null` →
  placeholder) so the `ILRuntimeException.ctor` try/catch doesn't swallow `GetLocalVariableInfo`.

## Verification (LEAD-verify)
- **NeoStep25 gate (LEAD re-ran): 11 tests, 0 failed** — the V4 `.neo` bump is additive; Cecil-free
  load unaffected (the key regression check).
- **NeoDebuggerAotBody capstone: 10/10** (5 value cells + isNeoAotBody + body-mutation + S3-2
  CF-meta + compile/Attach). Host-side CLI mode (not a TestCase filter).
- **Stash-toggle (load-bearing):** V4 serialization disabled → 10/10 → **3/10** (all 5 value cells +
  body-mutation + CF-meta fail: `li=[]` + `HasNeoAotLocalMeta=false`); restored → 10/10.
- **NeoDebuggerFrame 6/6 held** (JIT path unchanged). **NeoStep 263/0/0 held.** Cecil-free load checks
  held (LoadExec 28/28, CecilFreeLoad 7/7, MultiHotfix 5/5).
- **Legacy-neutral:** plain `Debug` builds ILRuntime + CLI = 0 errors (all Neo arms `#if
  ENABLE_NEO_MODE`; V4 version bump is Legacy-compiled-out).

## Durable findings
1. **S1 Attach keeps Cecil** — `InitCodeBodyFromNeo` sets `isNeoAotBody` but does NOT null `def`; only
   S3-2 `CreateFromNeoShell` has `def == null`. (So the debugger frame read worked for S1 AOT bodies
   all along.)
2. **`ILRuntimeException.ctor` wraps `GetThisInfo` + `GetLocalVariableInfo` in ONE try/catch**
   (`:34-45`) — an exception in `GetThisInfo` swallows the whole block so `GetLocalVariableInfo` never
   runs. Any future `GetLocalVariableInfo` fix whose path also throws in `GetThisInfo` MUST crash-guard
   `GetThisInfo` first.
3. **Cecil-free reference-local VALUE is a SEPARATE engine defect** (not debugger) — on S3-2, even with
   type/name resolved, `string msg` reads null (the reference-LOCAL mStack persistence on the CF path;
   string LITERAL resolution is fine). Cecil-free-EXECUTION follow-up.
4. **V4 is now owned by this child.** PARKED child-8 (`neo-aot-generic-cecilfree`) re-routed to V5.

## Follow-ups (out of scope)
- AOT-body THIS inspection on the Cecil-free shell (walk Cecil-free ILType fields via `fieldTypes[]`/
  `fieldOffsets[]`/`fieldMapping` + `.neo` FieldRef names).
- Cecil-free reference-local VALUE (Cecil-free-EXECUTION defect; Stloc-ref mStack persistence).
- Cecil-free IL-VT-local type resolution (needs nested types emitted to the `.neo`).

## Review
LEAD-verify (NeoStep25 gate re-ran 0-fail — the V4 .neo additive check; capstone implementer-verified
10/10 + stash-toggle 10→3→10; Legacy build 0 errors; Cecil-free load held).
