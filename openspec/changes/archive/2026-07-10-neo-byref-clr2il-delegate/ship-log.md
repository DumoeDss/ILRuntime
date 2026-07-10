# Ship Log — neo-byref-clr2il-delegate (child 14)

**Date:** 2026-07-10  **Capability:** neo-byref  **Wave:** completion-3, child 14
**Status:** SHIPPED (LEAD-verified). The REVERSE of F-7 (CLR->IL delegate byref).

## Delivered
**CLR->IL delegate callback with a byref param.** A custom `ref int`/`out int` delegate type, invoked
by a CLR host helper into an IL method. **Root cause = a 2-LAYER gap:**
- **Layer 1 (binding):** `FindDelegateAdapter` (`DelegateManager.cs:304`) matches by param-count with
  BY-VALUE args. `Action<>`/`Func<>` can't carry `ref`/`out` → the `out int` param never matches →
  `DummyDelegateAdapter`.
- **Layer 2 (conversion):** `GetConvertor` (`DelegateAdapter.cs:1561`) accesses `NativeDelegateType`
  BEFORE consulting any convertor → throws on `DummyDelegateAdapter`.

**Fix (3 coordinated changes, Neo-gated, additive):**
1. `DelegateManager.RegisterDelegateByRefConvertor<T>(Func<IDelegateAdapter,Delegate>)` — a new
   convertor that takes the **adapter** (not its by-value `Delegate`), so it can route a byref.
   `HasByRefConvertor(type)` guards it so `GetConvertor`/`ConvertToDelegate` consult it BEFORE the
   `DummyDelegateAdapter` throws (the layer-2 bypass).
2. `DelegateAdapter.NeoInvokeByRef(object[])` (`#if ENABLE_NEO_MODE`) → `NeoInvokeSub(args,
   marshalByRef:true)`: for each byref param, reserve a **self-referential scratch cell** (beyond
   `TotalStructSize`, sized to the element width), stash the CLR value there, set the param's 8-byte
   Ref Slot to `(objIdx==-1, off=scratchOff)` so the callee's `ldind`/`stind` read/write the scratch,
   then after `ExecuteNeo` returns **write the mutated value back** into `args[]`. Multicast passes
   `marshalByRef` (re-reads `args[]` per target → multicast `ref` semantics).
3. The convertor in `helper.cs` + the delegate types/host helpers in `TestCLRBinding`.

## Verification (LEAD-verify)
- **NeoStep19 gate (LEAD re-ran): 23 tests, 0 failed** (was 19; +4: `Clr2Il_ByRef`, `_Out`,
  `_ByRefLong`, `_Multicast`).
- **NeoStep smoke: 267/0/0** (263 + 4). No regression.
- **Stash-toggle:** on the HEAD engine the probes fail at `GetConvertor` ("Cannot find Delegate
  Adapter"); with the fix, 4/4 PASS.
- **Legacy-neutral:** ILRuntime + TestBase plain `Debug` builds 0 errors (Neo code `#if
  ENABLE_NEO_MODE`; `RegisterDelegateByRefConvertor`/`HasByRefConvertor` are additive public API never
  called in Legacy).

## Durable findings
1. **CLR→IL delegate byref is a 2-LAYER gap (binding + conversion), not one.** Both `FindDelegateAdapter`'s
   by-value param matching AND `GetConvertor`'s pre-convertor `NativeDelegateType` access must be
   bypassed for a byref-registered type. A delegate-TYPE-keyed convertor is the path; param-count
   matching is a dead end (`Action<>`/`Func<>` can't carry `ref`/`out`).
2. **For `NeoInvokeSub`'s ISOLATED frame (no IL caller frame), a byref must be SELF-REFERENTIAL** —
   unlike F-7's relativization (redirect offsets back to the caller frame), here there is no caller
   frame to relativize to; the byref points at a scratch cell WITHIN the callee's own frame, read back
   by `NeoInvokeSub` before teardown.
3. **`NeoInvokeSub` frame growth (beyond `TotalStructSize`) is safe for a self-referential byref** —
   the callee never addresses the scratch except through the byref; the return slot is at the grown
   `newEsp`.

## Follow-ups (out of scope)
- Reference-type ref-body (`ref string` / `ref <class>` reassignment) needs an mStack-slot phase (F-7B
   promotion analogy) — sequenced; the primitive/value-type core is delivered.
- AOT (`ilrt_neoc`) convertor-registration integration — standard pattern out of the box.
- The F-7 / NEO-DELEGATE-REFOUT row in `neo-deferred-items.md` is the ORIGINAL CLR→IL direction —
  child 14 closes it; LEAD can mark that row resolved.

## Review
LEAD-verify (NeoStep19 gate re-ran 23/0; implementer NeoStep 267/0/0 + stash-toggle; Legacy build 0
errors; Neo-gated + additive public API).
