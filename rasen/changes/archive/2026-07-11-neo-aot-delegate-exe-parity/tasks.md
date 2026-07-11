# Tasks — neo-aot-delegate-exe-parity

**Status:** DONE (all shipped; LEAD commits)

## 1. Re-audit (probe-first) — DONE
- [x] Read child-16 ship-log + `NeoStep25ByrefWireupCheck` + the AOT-exec
  path (`NeoAssemblyLoader.Attach`, `isNeoAotBody`, `ExecuteNeo`) + the
  delegate machinery (`NeoInvokeSub`, `NeoRunDelegateTargetOnThis`).
- [x] Construct a minimal reproducer isolating the simplest failing non-byref
  shape (`NeoStep19_PlainIntParam` → `Doubler`) from the full-TestCases noise.
- [x] Confirm the discrepancy on HEAD (JIT PASS, AOT FAIL = DivideByZero).

## 2. Root-cause — DONE (a SINGLE bug)
- [x] Dump the AOT body of `Doubler` vs the JIT body → **byte-identical**
  (same `Muli dst=4 op=3`, same frame, same `NeoCallParams`). Body was NEVER
  the divergence.
- [x] Dump `CompileFresh` (what gets serialized) vs `InitCodeBody` (JIT exec)
  → **identical**. Serialization raw-byte round-trip is lossless.
- [x] Token-probe the AOT `NeoStep19_PlainIntParam` body → all three token
  operands (`Ldftn`/`Newobj`/`Callvirt_IL`) resolve to **NULL** in the session
  AppDomain.
- [x] Confirm the model carries 1774 method + 3582 type bindings, **0/1774**
  resolve in the session `mapMethod` pre-Attach.
- [x] **Root cause:** `NeoAssemblyLoader.Attach` did NOT call
  `AppDomain.ReRegisterTokenBindings`; tokens baked in the COMPILE AppDomain
  never got re-registered in the EXECUTION AppDomain → every call-site
  operand resolved null → silently skipped → wrong result. The Cecil-free
  `LoadNeoAssembly` path was unaffected (it already called it).

## 3. Fix (Neo-gated / additive) — DONE
- [x] `NeoAssemblyLoader.Attach`: call `appdomain.ReRegisterTokenBindings(model)`
  at entry, before the MethodDef bind loop.
- [x] `AppDomain.ReRegisterTokenBindings`: promote `void` → `internal` so
  `Attach` can reach it (the `LoadNeoAssembly` caller is unchanged).
- [x] `AppDomain.ResolveMethodRefByName`: add IL `.ctor`/`.cctor` search over
  `GetConstructors()` by param count (constructors are not in the `methods`
  dictionary).
- [x] `AppDomain.ResolveMethodRefByName`: add a CLR declaring-type arm (CLR
  method via `CLRType.GetMethod`; CLR `.ctor` via a new
  `CLRType.GetConstructorByParamCount`), + a `GetType(fullName)` fallback for
  a nested CLR type not in `LoadedTypes`.

## 4. Verify — DONE
- [x] Byref-wireup probe: 7/13 → **13/13** (all 6 previously-failing cells
  pass; verdict "round-trip clean").
- [x] NeoStep smoke: **289/289, 0 fail** (baseline held).
- [x] NeoStep25 filter: 11/11.
- [x] Cecil-free AOT checks unchanged: `NeoStep25LoadExec` 28/28,
  `NeoStep25CecilFreeMultiHotfix` 5/5.
- [x] Legacy-neutral: plain `Debug` ILRuntime build = 0 errors.

## 5. Cleanup — DONE
- [x] Remove the temporary diagnostic (`NeoDelegateParityDiag.cs` + its CLI
  special-mode wiring).
- [x] Create `design.md` + `tasks.md` (this change).
