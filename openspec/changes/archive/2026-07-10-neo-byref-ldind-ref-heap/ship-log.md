# Ship Log — neo-byref-ldind-ref-heap (child 15)

**Date:** 2026-07-10  **Capability:** neo-byref  **Wave:** completion-3, child 15
**Status:** SHIPPED (LEAD-verified). Out-of-order (LEAD tackled the fast byref opcode before the deep
debugger children 12-13).

## Delivered
**The Step-17 `ldind_ref`/`stind_ref` heap-IL-reference-field read.** Previously the byref to a heap
IL ref field explicitly NIE'd ("deferred — ref-field Ref Slot encoding"). Root cause: the `ldflda`
HEAP branch stamped the byref `(objIdx, field.PrimitiveOffset)`, but a heap IL reference-typed field
lives in `ManagedObjects[ReferenceOffset]`, not `Primitives` → the offset was unrecoverable to the
reference. **Fix (3 sites, `#if ENABLE_NEO_MODE`):**
1. JIT marker `NeoLdfldaHeapIlRefFieldMarker = 0x4` (Operand4 bit) in `case Ldflda:` when the source
   is a heap IL instance + the field is reference-typed.
2. Runtime `Ldflda` branch produces `(objIdx, ReferenceOffset)` (no flag).
3. Runtime `Ldind_Ref`/`Stind_Ref` branch — **CONTENT-based dispatch** (`mStack[objIdx] is
   ILTypeInstance`) → routes to `ManagedObjects[off]`.

**Key design correction:** the first impl used a bit-31 offset flag, which caused ~30% intermittent
regression in `NeoStep17_ClrObjectRefFieldReadWrite` (4d.3) — CLR `FieldInfo` hashes (4d offsets) are
NON-deterministic (process-global counter) and can set bit 31. Switched to content-based dispatch,
unambiguous for all offset values.

## Verification (LEAD-verify)
- **NeoStep 263/0/0** (259 + 4 probes), stable over 5+ runs.
- **NeoStep17 51/0/0** (47 + 4), stable over 17+ runs (no intermittency after the content-based fix).
- Probes: `NeoStep17_LdindRefHeap_{Read,WriteRead,NonZeroRefOffset}` (FAIL-on-HEAD NIE → PASS-after)
  + `_Control_FrameLocal` (PASS on both).
- Stash-toggle: 3 engine-dependent probes NIE on HEAD → PASS after; control passes on both.
- **Legacy-neutral:** plain `Debug` build 0 errors; the marker constant is unconditional (a harmless
  `const int`, only read by the Neo runtime); NeoStep19 (F-7B delegate-byref, shared branch) 19/0/0 +
  NeoOptHardening 24/24 unaffected (the new branch is ordered AFTER the F-7B bit-30 flag).

## Durable finding (cross-cutting)
**NEVER use a bit-flag on a byref's OFFSET half** — CLR `FieldInfo` hashes (4d offsets) are
non-deterministic (process-global counter, same family as the S3-2 Cecil identity hashes) and can set
high bits. F-10's bit-30 flag only survives because F-10 stamps CONTROLLED small ReferenceOffsets at
`ldflda` time; raw 4d CLR-object-field-hash offsets collide. Any new ManagedObjects-routing byref
shape should dispatch on mStack CONTENT (`mStack[objIdx] is ILTypeInstance` / `is Array` /
`NeoIsClrObject`), not an offset bit-flag.

## Review
LEAD-verify (marker constant unconditional-harmless; implementer confirmed usage Neo-gated + Legacy
build 0 errors + shared NeoStep19 byref branch unaffected; stash-toggle 3→PASS; NeoStep 263/0/0).
