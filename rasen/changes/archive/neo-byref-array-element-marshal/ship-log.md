# Ship Log — neo-byref-array-element-marshal (child 25)

**Change:** `ref arr[i]` byref-param marshalling — add an `Array` branch to `NeoMarshalByrefFieldToSlot` so a
byref to a CLR array element (passed to a CLR method) materializes forward AND writes back post-call.
**Capability:** `neo-byref` (ADDED requirement).
**Pipeline:** small-feature (triage-re-audit -> propose+apply -> verify -> review -> ship).
**Date:** 2026-07-13. **Branch:** `features/object-model-overhaul`. **Base HEAD:** `5b980cb8`.

## Triage (this child was the HIGHEST-value of triage batch-2)
A `ref arr[i]` byref passed to a CLR method (`TestClass3.setBit(ref byteArr[idx], ...)`) hit the `Step 13
Area-4c: a CLR-array-element byref param is not handled` NIE. `NeoMarshalByrefFieldToSlot` (`ILIntepreter.Neo.cs:~508`)
handled `NeoIsClrObject` but not a byref whose target is an array element. This is the most pervasive real-
world byref pattern of the residual set.

## What shipped (Neo-only, single engine file + helpers + probe, Legacy-neutral)
- **`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`**: replaced the NIE-throwing `target is Array`
  stub in `NeoMarshalByrefFieldToSlot` with a real `Array` branch (~:508). Forward read = `arr.GetValue(off)` +
  `WriteNeoValueType` by category (~:563); write-back = `ReadNeoValueType`/mStack-index + `arr.SetValue(value, off)`
  (~:556). `elemType` recovered from `array.GetType().GetElementType()` when null. NO JIT/optimizer/object-model/
  binding change — the call map's `PrimitiveByRefElemType`/`PrimitiveSize`/`PrimitiveByRefWriteBack` plumbing
  already existed; the helper's slot marshalling was the only gap.
- **`ILRuntimeTestBase/TestFramework/TestClass3.cs`**: 4 host helpers in the `TestCLRBinding` class (~:226-258).
- **`TestCases/NeoStepByrefArrayElementTest.cs`** (new): 3 TCs (byte, int multi-index, VT twice-call).

## Why (the both-directions insight)
`NeoMarshalByrefFieldToSlot` is the SHARED marshal invoked for BOTH the forward call-arg deref
(`CopyNeoCallArguments:424`, `isWrite:false`) AND the post-call write-back (`CopyNeoCallThisBack:730`,
`isWrite:true`). So ONE `Array` branch fixes the pervasive `ref arr[i]` CLR-call pattern in both directions.

## Verification
- **NeoStep smoke: 371/0** (368 baseline + 3 probes), no regressions. Canary `CLRBindingTest01` PASSES (was NIE).
- **Stash-toggle (airtight):** stash `ILIntepreter.Neo.cs` (probe + helpers kept) → 3/3 FAULT (exact Area-4c NIE
  on the forward deref); pop → 371/0.
- **Write-back persistence (empirically proven, not just non-throwing):** TC1 (`ref byteArr[1]` 20→25, read back
  25), TC2 (`ref arr[0]`+`ref arr[3]` both persist, 101+104=205), TC3 (VT `ref vectorArr[0]` twice-call: ret2=6777
  = the call-1 mutated sum — proves the struct mutation persisted into the array element). TC3's twice-call
  pattern isolates the byref marshal from the still-broken `ldobj`-array-element read (R1-Shape-B, next child).
- **Legacy-neutral:** plain `Debug`+`useRegister=true`+NeoStep = 371 ran/18 failed (pre-existing Legacy set; the 3
  probes PASS under Legacy). 100% `#if ENABLE_NEO_MODE`.

## Review
**APPROVE** (reviewer != implementer; cleanest review this portfolio — 0 Blocker/Major). All 7 dimensions confirmed:
both-directions correctness, byref decode (`off`=element index, ldelema producer at `:5874-5875`), elemType recovery,
branch exclusivity (only Array targets), write-back persistence, regression surface (no `ref`/`out` regressions),
build-server-cache sanity (helpers genuinely in the live DLL metadata, confirmed 3 ways).
- 1 Minor doc-only: `tasks.md` TC3 narrative lags the implemented 7777/6777 values.
- 2 Trivial: a `:5797` comment-ref should be `:5874`; the primitive/enum + struct arms are logically redundant
  (mirror the sibling CLR-object-field branch).
- Pre-existing (not this PR): R1-Shape-B `ldobj`-array-element read (`:5704`) still NIEs.

## Delivery
**Mode:** local commit (portfolio per-child delivery; push per parent directive). Committed with the
portfolio-run.json + planning-context.md record updates. No PR.

## Durable findings (for future planning)
1. **`NeoMarshalByrefFieldToSlot` is the SHARED byref marshal for BOTH directions** (`CopyNeoCallArguments` forward
   + `CopyNeoCallThisBack` write-back). One branch fixes a byref pattern in both. The `target is Array` decode
   uses `off` = element index (the `ldelema` convention).
2. **BUILD-SERVER CACHE GOTCHA (cost ~1h):** the Roslyn VBCSCompiler shared server caches a STALE compilation of
   `TestClass3.cs`; `rm -rf bin/obj` does NOT invalidate it (a 3s "rebuild" is the tell; a real clean build is
   ~10s). `strings` is misleading (method names live in the embedded PDB, not the MethodDef table — use
   `System.Reflection.Metadata TypeDefinition.GetMethods()`). FIX: kill all `dotnet` build-server processes +
   `-p:UseSharedCompilation=false` on every build after touching `TestClass3.cs`/`ILRuntimeTestBase`.
3. **`TestClass3.cs` is a multi-class FILE:** `TestClass3` spans only lines 12-39 (closes after `setBit`); the
   array-element host helpers (child-19/24/25) live in `TestCLRBinding` (line 110+). A probe must reference
   `TestCLRBinding.X`. Grep the class boundary before adding host helpers.
