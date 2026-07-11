# Design — neo-aot-delegate-exe-parity

**Date:** 2026-07-11  **Capability:** neo-optimizer (AOT)  **Status:** DONE

## The re-audited root cause (a SINGLE bug, not "general")

Child 16's ship-log framed this as a "pre-existing GENERAL AOT-vs-JIT body
discrepancy for delegate/callback shapes (non-byref controls ALSO fail)."
The 6-for-6 lesson held: the "general" framing hid ONE specific bug.

**The bug:** `NeoAssemblyLoader.Attach` did NOT re-register the .neo's
APPROACH-1 token bindings into the EXECUTION AppDomain's `mapMethod` /
`mapTypeToken`. A .neo is compiled in a SEPARATE compile AppDomain (the
normal case — `ilrt_neoc` builds a fresh AppDomain, and the host-side
`NeoCompiler.Compile(testCasesDllPath, ...)` does too), so every method/type
token hash baked into the bodies comes from the COMPILE AppDomain. Those
hashes are ABSENT from the execution AppDomain's maps. At `ExecuteNeo`, every
`Ldftn` / `Call` / `Callvirt_IL` / `Callvirt_CLR` / `Newobj` / `Ldvirtftn`
operand resolved via `AppDomain.GetMethod(token)` → **null** → the call site
was silently skipped (`if (targetMethod == null) { ip++; continue; }`).

This is why delegate/callback shapes failed on AOT-exec but passed on JIT:
the delegate `Invoke` callvirt (and the `Newobj` that builds the delegate,
and the `Ldftn` that loads the target) resolved to null and was skipped,
leaving the dest register stale → the downstream `if (result != expected)`
assertion fired its deliberate `1/0` (DivideByZero), or a CLR-field NIE for
the `List.ForEach` shape. The JIT path worked because the JIT registers the
tokens into the SAME AppDomain that executes.

The Cecil-free `LoadNeoAssembly` path was NOT affected — it already calls
`AppDomain.ReRegisterTokenBindings(model)` (the S3-2 APPROACH-1 pass). Only
the S1 `NeoAssemblyLoader.Attach` path (same-AppDomain assumed) skipped it.
The byref-wireup probe (child 16) compiles in a fresh AppDomain + attaches to
the session AppDomain — a CROSS-AppDomain attach — which is exactly what
surfaced it.

**Probe-first confirmation (the diagnostic, now removed):**
- The AOT body of `Doubler` (`int Doubler(int x){return x*3;}`) was
  byte-identical to the JIT body (same `Muli dst=4 op=3`, same frame layout,
  same `NeoCallParams`). `CompileFresh` (what gets serialized) matched the
  `InitCodeBody` JIT body exactly. So the body was NEVER the divergence.
- A token-probe on the AOT `NeoStep19_PlainIntParam` body showed all three
  token operands (`Ldftn Doubler`, `Newobj delegate..ctor`, `Callvirt_IL
  Invoke`) resolved to NULL in the session AppDomain.
- The model carried 1774 method-token bindings + 3582 type-token bindings,
  but **0/1774** resolved in the session `mapMethod` pre-Attach.

## The fix (3 parts, all Neo-gated / additive)

1. **`NeoAssemblyLoader.Attach` re-registers token bindings.** Call
   `appdomain.ReRegisterTokenBindings(model)` at the very start of `Attach`,
   before the MethodDef bind loop. (`AppDomain.ReRegisterTokenBindings` was
   promoted `void` → `internal` so `Attach` can reach it; the Cecil-free
   `LoadNeoAssembly` caller is unchanged.) This makes Attach robust to a
   .neo compiled in a different AppDomain — the normal production case.

2. **`AppDomain.ResolveMethodRefByName` handles IL constructors.** A
   `.ctor` / `.cctor` binding (delegate `.ctor`, IL type `.ctor`) was NOT in
   the `methods` dictionary (`ILType.InitializeMethods` files constructors
   into a separate `constructors` list), so `GetMethod(".ctor", ...)` missed.
   Added an IL-arm ctor search over `GetConstructors()` by param count.

3. **`AppDomain.ResolveMethodRefByName` handles CLR declaring types.** The
   prior `!(it is ILType) return null` guard dropped EVERY CLR method / ctor
   binding — a host-CLR helper (`TestCLRBinding.InvokeRefCallback`) and a CLR
   delegate `.ctor` (`Clr2IlRefIntDelegate..ctor`, a nested CLR type). Added:
   (a) a `GetType(fullName)` fallback for a nested CLR type not in
   `LoadedTypes`; (b) a CLR arm that resolves the method by name+paramCount
   via `CLRType.GetMethod`, and the `.ctor` via a new
   `CLRType.GetConstructorByParamCount(int)` (constructors are stored
   separately from methods on CLRType too).

## Why this is ONE bug, not a multi-shape rework

All 6 failing cells (2 non-byref controls + 4 byref shapes) shared the SAME
root cause: a token operand in the AOT body resolved to null because the
compile-AppDomain hash was never re-registered in the execution AppDomain.
The byref-specific machinery (`NeoInvokeByRef`, `NeoRunDelegateTargetOnThis`,
the byref convertor) was correct all along — the byref shapes failed only
because their ENTRY methods' `Newobj`/`Call` tokens (CLR delegate ctor +
`InvokeRefCallback`) didn't resolve, so the entry threw before the byref
machinery was even reached. One fix (token re-registration + the two
`ResolveMethodRefByName` gaps) closed all 6.

## Per-shape status (all now fixed)

| Shape | Pre-fix | Post-fix |
|---|---|---|
| `NeoStep19_PlainIntParam` (IL delegate invoke, non-byref) | DivideByZero | PASS |
| `NeoStep19_ClrCallback` (List.ForEach CLR→IL, non-byref) | NIE / DivideByZero | PASS |
| `NeoStep19_Clr2Il_ByRef` (CLR→IL byref delegate) | DivideByZero | PASS |
| `NeoStep19_Clr2Il_Out` (CLR→IL out delegate) | DivideByZero | PASS |
| `NeoStep19_Clr2Il_ByRefLong` (CLR→IL ref long) | DivideByZero | PASS |
| `NeoStep19_Clr2Il_Multicast` (CLR→IL multicast byref) | NRE | PASS |

## Verification

- **Byref-wireup probe:** 7/13 → **13/13** (all 6 previously-failing cells
  pass; verdict flipped from "GENERAL gap" to "round-trip clean").
- **NeoStep smoke: 289/289, 0 fail** (baseline held; no regression).
- **NeoStep25 filter: 11/11** (AOT checks).
- **Cecil-free AOT checks unchanged:** `NeoStep25LoadExec` 28/28,
  `NeoStep25CecilFreeMultiHotfix` 5/5 (the Cecil-free path already called
  `ReRegisterTokenBindings`; the `ResolveMethodRefByName` enhancements are
  strictly additive there).
- **Legacy-neutral:** plain `Debug` build of ILRuntime = 0 errors (all new
  code is `#if ENABLE_NEO_MODE`-gated or a plain additive public method on
  CLRType).

## Files changed

- `ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs` — `Attach` calls
  `appdomain.ReRegisterTokenBindings(model)` at entry (Neo-gated file).
- `ILRuntime/Runtime/Enviorment/AppDomain.cs` — `ReRegisterTokenBindings`
  promoted `void` → `internal`; `ResolveMethodRefByName` extended (IL ctor
  search + CLR arm + GetType fallback) (Neo-gated region).
- `ILRuntime/CLR/TypeSystem/CLRType.cs` — added
  `GetConstructorByParamCount(int)` (additive public method, no gating).

## Out of scope / notes

- The fix assumes the .neo carries APPROACH-1 token bindings (`model.
  MethodTokenBindings` / `TypeTokenBindings`). A .neo compiled by an older
  `ilrt_neoc` without bindings would still fail — but the current Step-24
  compiler always emits them, so this is not a practical concern.
- The `Attach` docstring's "Same-AppDomain" assumption is now relaxed to
  "Same-AppDomain OR a .neo with token bindings"; the comment is updated
  in-place.
