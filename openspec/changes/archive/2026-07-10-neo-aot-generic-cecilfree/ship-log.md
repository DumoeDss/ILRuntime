# Ship Log — neo-aot-generic-cecilfree (child 8) — UNBLOCKED (the LAST parked child)

**Date:** 2026-07-10  **Capability:** neo-optimizer (AOT)  **Wave:** completion-3, child 8
**Status:** SHIPPED (LEAD-verified). The LAST parked child — **the entire 19-child portfolio is now
resolved** (all shipped, with minor sub-gap follow-ups noted).

## Delivered
**Cecil-free generic-method instances** (`T M<T>(T arg)` compiled to `.neo`, loaded Cecil-free,
ExecuteNeo). The prior "bounded 5-site JIT back-half rework" framing was **OVER-CAUTIOUS, not a
mis-attribution** (3rd re-audit after child 4 + child 17): the 5 (actually 6) sites were real blockers,
but each fix was a SMALL ADDITIVE Neo-gated patch (5–40 lines) — NOT a back-half rework, NOT an ABI
change, NOT a shared-cache risk. The Step-22 template machinery (`RunNeoBackHalf`/`CloneAndPatch`/
`TryInstantiate`) was UNCHANGED. A site the PARK note UNDER-estimated: the generic-def shell was NEVER
created at Cecil-free load (the `.neo` MethodDefs table only has non-generic methods [Step-24
partitioning]; the S2 bind loop must BUILD the shell).

**Minimal fix (7 engine files, all `#if ENABLE_NEO_MODE` + additive `.neo` V5):**
1. **`.neo` V5** — `GenericParamNames` + `ReturnTypeRefIdx` on `NeoTemplateRecord` (MethodRef omitted
   both; a generic-param return "T" must resolve for the return-slot sizing). Version 4→5, additive.
2. **`ILMethod`** — `neoShellGenericParamNames`; `GenericParameterCount` shell-aware;
   `FindGenericArgument` guard; `MakeGenericMethodShell` (Cecil-free, T-substituted params/return);
   `InitCodeBody` shell-instance guard -> routes through `TryInstantiate`.
3. **`JITCompiler`** — `BuildInitialRegisterTypes`/`AllocateLocalStackSpaces` read from
   `template.VariableTypes` + `method.Parameters` when `def == null` (the template already has both;
   flows through the existing `appdomain.GetType` -> `FindGenericArgument`). **NO ABI change.**
4. **`NeoAssemblyLoader`** — `MatchGenericDefinitionCecilFree`; `BuildAndRegisterGenericDefShell`
   (the under-estimated missing site); `ResolveVariableType` shell-aware (synthesizes a Cecil
   `GenericParameter` + a tiny `IGenericParameterProvider` owner; CLR bare-`TypeReference` fallback,
   no Cecil module needed).
5. **`GenericMethodTemplate.cs`** — UNCHANGED (re-audit proved the template path was NOT the blocker).

## Verification (LEAD-verify)
- **NeoStep25CecilFreeGeneric capstone: 12/12** (G1 template-bind + G2 fresh-instance template-mutation
  + 4 functional wrappers + compile + load + A-JIT reference). Added: 8, skipped: 0.
- **Stash-toggle (load-bearing):** stash the 7 engine files -> G1+G2 FAIL (10/12, the HEAD state); pop
  -> 12/12 PASS.
- **NeoStep smoke (LEAD re-ran): 278/0/0** (no regression). **NeoStep25 (LEAD re-ran): 11/0** (the
  `.neo` V5 bump is additive — Cecil-free load unaffected).
- NeoStep25LoadExec 28/28, NeoStep22SelfCheck 55/55, NeoStep23Roundtrip 15/15 — all held (no `.neo`
  V5 format regression).
- **Legacy-neutral:** plain `Debug` build 0 errors (all Neo-gated; V5 bump Legacy-compiled-out).

## Durable findings
1. **A "bounded N-site rework" framing can be OVER-CAUTIOUS** (3rd re-audit). The site COUNT was right,
   but each fix was a small additive patch — not a back-half rework. Re-audit the EFFORT estimate, not
   just the site list. (Step-22 template machinery was untouched.)
2. **The generic-def shell is NOT in the `.neo` MethodDefs table** (Step-24 partitions to non-generic) —
   the Cecil-free loader must BUILD the shell at S2 bind time (`BuildAndRegisterGenericDefShell`).
3. **`JITCompiler`'s back-half reads `template.VariableTypes` + `method.Parameters` cleanly when
   `def == null`** — no ABI change needed; the template already carries the generic info.

## Follow-ups (out of scope, documented)
- Generic **type** instances (`List<ILType>` field — a separate ILType field-layout surface).
- T-identity-token generic methods (`Box T`/`Ldobj T` — still trigger the S3 `hasIdentityToken`
  rejection; need a Cecil-free `GenericParamIdx`-keyed patch applier).

## Review
LEAD-verify (NeoStep + NeoStep25 re-ran 0-fail; capstone 12/12 + stash-toggle 10/12->12/12; Cecil-free
load held incl. .neo V5; Legacy build 0 errors; all Neo-gated).
