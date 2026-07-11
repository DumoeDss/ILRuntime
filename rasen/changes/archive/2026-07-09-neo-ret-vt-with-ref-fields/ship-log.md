# Ship Log — neo-ret-vt-with-ref-fields

**Date:** 2026-07-09  **Capability:** neo-value-types  **Wave:** completion-3
**Status:** SHIPPED  **Pipeline:** small-feature (LEAD-driven, Tier A, author!=verifier)

## Delivered
Closed HIGH#1 engine gap (lead-6 audit): the `Ret` opcode (`ILIntepreter.Neo.cs:~2940`) threw
`NotImplementedException("Neo return with value-type reference fields requires Step 12/13 return
layout support.")` for ANY IL method returning a struct WITH reference fields. Now a `Move_Vt`-
style return copy: `Unsafe.CopyBlock(retDst, frameBase+ip->DstOffset, returnPrimitiveSize)` + a
ref-slot loop copying `returnRefCount` slots from the callee return ref region to the caller's
`retRefBase`. The existing single-reference (class) return path and the pure-primitive path are
unchanged.

**Key finding:** the return value's callee-side ref offset was NOT on `CompiledFrame` (only
`ReturnPrimitiveSize`/`ReturnRefCount` are recorded — `AllocateLocalStackSpaces` uses throwaway
local cursors). The per-register `RefOffset` lives in `localInfos[retReg].RefOffset`. Fix: stamp
`op.Operand3 = localInfos[retR1].RefOffset` in the `Ret` case of `LowerNeoOffsets`
(`Optimizer.Neo.cs`), mirroring `Initobj`/`Move_Vt`. Runtime reads `mStack[frameRefBase +
ip->Operand3 + i]`. The stamp runs BEFORE `LowerR1` converts the index to a byte offset
(verified — the load-bearing subtlety).

## Verification
- NeoStep smoke: 241/0/0 -> **248/0/0** (+7 probes). No regression.
  New probes (`NeoStepRetVtTest`): OneRef, ManyRefs, ReadAfterReturn, ShallowCopyIndep,
  NestedVtWithRef (outer-primitive only — `Ldfld_Value` NIE scoped out), PurePrimitiveControl,
  RefOnly (ref-only struct, primSize 0).
- Stash-toggle (load-bearing): stash the 2 engine files -> 5/6 ref-field probes FAIL with the
  exact NIE; PurePrimitiveControl correctly passes on HEAD (returnRefCount==0 path). pop ->
  7/7 PASS.
- Legacy-neutral: plain `Debug` builds 0 errors (both files `#if ENABLE_NEO_MODE`-gated); 7 new
  probes PASS on Legacy; ~10 pre-existing Legacy NeoStep failures unchanged.

## Review
APPROVE-WITH-FINDINGS (non-author reviewer). 0 Blocker, 0 Major, 1 Minor (kept the added
`NeoStepRetVt_RefOnly` ref-only-struct edge probe), 3 Trivial (doc/scoping, no action). Subtle
points verified GOOD: LowerNeoOffsets `Operand3` stamp runs BEFORE `LowerR1` (faithful `Initobj`
mirror); `Operand3` spare for `Ret` (no collision); ref-region contiguity (shares Move_Vt's
invariant); ref-only-struct (primSize 0) edge PASSes; caller-side copy-prop-eliminates-post-call-
move verified (call dest IS the typed local).

## Files
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (+48/-8): Ret else-branch
  Move_Vt-style value-type-with-ref-fields copy.
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` (+17): `LowerNeoOffsets` Ret case
  stamps `Operand3 = localInfos[retR1].RefOffset`.
- `TestCases/NeoStepRetVtTest.cs` (new, 7 adversarial probes).

## Follow-ups
- **(child 4 `neo-async-valuetask-asyncvoid`)** The async `get_Task` redirect for `ValueTask<T>`
  must now use the value-type return path (available after this change) instead of
  `WriteReferenceReturn`. This child unblocks child 4's ValueTask half.
- **(pre-existing, out of scope)** `Ldfld_Value` (whole-nested-struct field load) Step-6/12b NIE
  blocks nested-field reads off returned structs -> future Step-12b follow-up.
- **(pre-existing)** `object.ReferenceEquals` Neo CLR-static-call gap.
