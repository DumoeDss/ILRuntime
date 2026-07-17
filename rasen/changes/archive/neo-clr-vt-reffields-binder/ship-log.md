# Ship Log — neo-clr-vt-reffields-binder (neo-overhaul child 6)

**Date:** 2026-07-11  **Delivery:** local commit + push. **Outcome:** SHIPPED. NeoStep **320/0**; NeoStepLdtoken 5/0; full-smoke Step-13b NIE ~18->0. Legacy-neutral. RESOLVES the child-2 RuntimeHelpers.InitializeArray follow-up.

## What shipped
- `CLRRedirections.cs` — new `CLRRedirections.InitializeArrayNeo` redirect: reads param 0 (Array) + param 1 (byte[]) via `ReadNeoReference` (+4 each); `GCHandle.Alloc(Pinned)` + `Marshal.Copy` into the array (try/finally Free — safer than Legacy); void return. Modeled on `DelegateCombineNeo`.
- `AppDomain.cs` — `RegisterCLRMethodRedirectionNeo(mi, InitializeArrayNeo)` registration (`#if ENABLE_NEO_MODE`, alongside the Legacy line).
- `ILIntepreter.Neo.cs` — ldtoken field-path remediation: source the `.size N` blob from Cecil `FieldDefinition.InitialValue` (push as a Neo reference, first arm before the VT arm). Bonus: NeoStepLdtoken TC5 now passes via correct contents (its catch-NIE is dead).
- `TestCases/NeoClrVtReffieldsBinderTest.cs` — TC1 int[32], TC2 double[32] (element-type-agnostic).

## Root cause (planner; implementer DISPROVED the blob-path assumption)
The 18 Step-13b NIEs were ALL `RuntimeFieldHandle` from C# array initializers (`RuntimeHelpers.InitializeArray`). It had only a Legacy redirect; Neo consults `RedirectMapNeo` exclusively -> fell through to reflection -> the NIE. The planner assumed the blob byte[] was in the static instance `ManagedObjects`; the implementer DISPROVED this (a `.size N` blob struct has zero IL fields -> TPS=0/TRC=0 -> ManagedObjects null -> the blob is reachable ONLY via Cecil `FieldDefinition.InitialValue`). The ldtoken field path now sources it from Cecil. The binder was the wrong tool (Legacy-StackObject-based, useless to the Neo byte* cursor).

## Evidence
- **Stash-toggle:** redirect registration stashed -> TC1/TC2 FAIL (exact `CLR value type with reference fields ... RuntimeFieldHandle` NIE at CLRMethod.cs:501); restored -> 2/0 PASS.
- **NeoStep smoke:** 320/0 (318 + 2). **NeoStepLdtoken:** 5/0 (TC5 upgraded, TC1-TC4 unregressed — the remediation is field-path Operand==0 only; typeof probes Operand==1 insulated).
- **Full smoke:** Step-13b "CLR value type with reference fields" ~18->0 (every RuntimeFieldHandle hit is now `call.redirect InitializeArray`). (Run segfaults exit 139 on a pre-existing AccessViolationException-throwing EH test; unrelated.)
- **Legacy-neutral:** all changes Neo-gated; plain Debug NeoStep 320 ran/17 failed == baseline.

## Review verdict
APPROVE (reviewer != implementer). 0 Blocker/Major/Minor. Redirect-correct + ldtoken-blast-radius-clean both confirmed. T1 (duplicated comment) trivial; I1/I2 informational.

## Resolved / deferred
- **RESOLVES** the child-2 surfaced follow-up `neo-runtimehelpers-initializearray-neo` (this child IS that fix).
- F1 (child-5 Stobj/Ldobj refCount=0) NOT subsumed (different site) — stays a follow-up.
