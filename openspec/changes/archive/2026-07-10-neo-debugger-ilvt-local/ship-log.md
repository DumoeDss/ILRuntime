# Ship Log — neo-debugger-ilvt-local (child 11)

**Date:** 2026-07-10  **Capability:** neo-debugger  **Wave:** completion-3, child 11 (first debugger
child)  **Status:** SHIPPED (LEAD-verified). Extends the shipped `neo-debugger-neo-frame` frame read.

## Delivered
**Reconstruct an IL-value-type LOCAL's fields in the Neo debugger frame inspection.** Previously an
IL-VT local rendered as a placeholder (`<IL value-type local: reconstruction deferred>`); now its
fields are walked + shown. Confirmed on HEAD via a probe (`VtLocal { int X; string S; }` set then
unhandled-throw → `GetLocalVariableInfo`).

**Mechanism (field-walk):** an in-frame IL-VT local spans the SAME split storage a heap
`ILTypeInstance` uses, only the region bases are frame-relative: primitive sub-region at
`frameBase + slot.Offset` (`Size = ilType.TotalPrimitiveSize`), reference sub-region at
`mStack[frameRefBase + slot.RefOffset]` (`RefCount = ilType.TotalReferenceCount`). Field F at ILType
index `i`: primitive at `frameBase + slot.Offset + off.PrimitiveOffset`; reference at
`mStack[frameRefBase + slot.RefOffset + off.ReferenceOffset]`, where `off = ilType.GetFieldOffset(i)`
— the SAME offsets the F-4 indexer + `Move_Vt` (`ILIntepreter.Neo.cs:1332`) use.
- `ReadNeoIlVtLocalFields`: walks `[0, TotalFieldCount)`, per-field dispatch mirroring the F-4
  indexer (primitive → `ReadNeoFramePrimitive`; ref/enum/CLR-struct → the mStack slot; nested IL-VT
  field → recurse ONE level, deeper → placeholder + note). Per-field try/catch (a bad field renders
  `<unreadable fN>`, does not abort the struct).

## Verification (LEAD-verify)
- **NeoDebuggerFrame gate: 6/6** (4 original held + 2 new: `ProbeVtLocal` asserts X=4242 +
  S="vt-field-A"; `ProbeVtLocalMutate` ADVERSARIAL asserts the mutated X=8888 is read live, not
  stale 1111).
- **Stash-toggle (load-bearing):** placeholder active → cells 4+5 FAIL (4/6); fix restored → 6/6.
- **NeoStep smoke: 253/0/0** (no regression).
- **Legacy-neutral:** plain `Debug` build 0 errors (all Neo arms `#if ENABLE_NEO_MODE`).

## Durable findings
1. **Two mStack-index conventions coexist in `ReadNeoLocalValue`:** a top-level reference LOCAL's
   slot word stores an ABSOLUTE mStack index (`mStack[idx]`); an IL-VT local's reference FIELDS are
   contiguous from the VT's FRAME-RELATIVE ref base (`mStack[frameRefBase + slot.RefOffset +
   off.ReferenceOffset]`). Not a contradiction — the top-level slot word is an absolute index written
   by the executor; VT ref fields are contiguous from a frame-relative base (allocator + Move_Vt).
2. **The frame-local path is STRICTLY RICHER than the heap IL-VT-field read** — the F-4 indexer
   THROWS a tagged NIE for an IL-VT FIELD (`ILTypeInstance.cs:437`), but the frame-local path can
   RECURSE (it has the field offsets + frame bases). A future child could lift this recursion into
   the F-4 indexer to close the heap IL-VT-field gap (same shape, only bases differ).

## Follow-ups (out of scope, sequenced)
- Heap IL-VT-FIELD reconstruction in the F-4 indexer (lift this recursion).
- AOT-body variable inspection (`neo-debugger-aot-body` — `registerSymbols` null on AOT).
- CLI debugger-protocol capstone (`neo-debugger-cli-protocol`).

## Review
LEAD-verify (Neo-gated debugger read path; implementer evidence trusted — stash-toggle 4/6→6/6 +
adversarial mutation probe; NeoStep 253/0/0; Legacy build 0 errors).
