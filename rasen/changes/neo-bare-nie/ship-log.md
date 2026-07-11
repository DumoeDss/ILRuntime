# Ship Log — neo-bare-nie (neo-overhaul child 5)

**Date:** 2026-07-11  **Delivery:** local commit + push. **Outcome:** SHIPPED. NeoStep **318/0**; full-smoke bare-default NIE ~38->6 (all 6 = deferred framework Enum.GetValues). Legacy-neutral.

## What shipped
- `AppDomain.cs` `GetPrimitiveSize` (Neo-gated @2230) — added `else if (fieldType.IsValueType && TypeForCLR != null && !(is ILType)) return Optimizer.GetNeoValueTypeManagedSize(fieldType.TypeForCLR);` (enum->underlying primitive, CLR struct->`Unsafe.SizeOf`). Tagged the residual throw `[neo-bare-nie]`. STRICTLY ADDITIVE (threw on all non-primitives before).
- `JITCompiler.cs` (3) + `AppDomain.cs` (2) — 5 message-only guard-tags (`[neo-bare-nie]`) on unfired bare-NIE `else` guards. No logic change.
- `TestCases/NeoStepBareNieTest.cs` — 2 probes (TC1 enum-arg method, TC2 CLR-struct stobj/ldobj copy).

## Root cause (planner, full-smoke stack dedup)
ALL ~38 bare-NIE printings (~19 throws x2) collapse to ONE site: `AppDomain.GetPrimitiveSize(IType)` lacked enum + CLR-VT branches. Enums reach it via the JIT call-param allocator `AllocateNeoCallParamSlot` (JIT-time); CLR structs via the ExecuteNeo `Stobj`/`Ldobj` arms (runtime). Both feeder paths fixed by the single additive branch. The 6th residual = the framework `System.Type.GetEnumValues()` NIE (out of scope, deferred).

## Evidence
- **Stash-toggle:** fix reverted -> 2/2 probes FAIL (bare NIE; TC1 at JIT-compile via AllocateNeoCallParamSlot, TC2 at runtime via Ldobj arm); restored -> PASS.
- **NeoStep smoke:** 318/0 (316 + 2).
- **Full smoke:** GetPrimitiveSize residuals ~16->0; total bare-default ~38->6 (all 6 = deferred framework Enum.GetValues via System_Enum_Binding:77).
- **Legacy-neutral:** GetPrimitiveSize Neo-gated; tags string-only. Plain Debug NeoStep 318 ran/17 failed == baseline.

## Review verdict
APPROVE-WITH-FINDINGS (reviewer != implementer). 0 Blocker/Major. SIZING-correct confirmed (GetNeoValueTypeManagedSize canonical, byte-consistent with all readers). F1 (ref-field CLR struct via stobj/ldobj copies with `refCount=0`, doesn't consult ValueTypeBinder unlike AllocateNeoCallParamSlot:1592 -> latent missed-GC-root; the refCount line is UNCHANGED by this PR, now reachable) -> follow-up child. F2 (TC1 asserts Length>0 not exact) accepted trivial.

## Deferred / surfaced
- **F1 follow-up:** ref-field CLR struct copied via Stobj/Ldobj has `refCount=0` (no ValueTypeBinder consult) -> latent missed-GC-root. Related to neo-clr-vt-reffields-binder.
- **Framework `System.Type.GetEnumValues()` NIE** (System_Enum_Binding:77): an IL-enum-System.Type-representation gap (sibling of the RuntimeHelpers.InitializeArray autogen-binding follow-up).
