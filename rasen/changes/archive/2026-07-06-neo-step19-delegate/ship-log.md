# Ship Log — neo-step19-delegate (Step 19 Delegates)

**Date:** 2026-07-06. **Branch:** `features/object-model-overhaul`.
**Review:** round 0 CHANGES-REQUESTED (F1 Major — pool leak, F2 Minor, F3
Trivial); round 1 **APPROVED** (F1 fixed, F2 accepted-known, F3 reverted; 0
open Blocker/Major). **Working tree:** UNCOMMITTED (LEAD commits after ship).

---

## What shipped — Step 19 Delegates: full Neo delegate support

Neo mode (Steps 1-18) had **no delegate support** — `ldftn`/`ldvirtftn` hit the
catch-all `NotImplementedException` and the `Newobj` arm threw
`NotImplementedException("Neo Newobj delegate is not implemented")`. Step 19
closes the gap. Every delegate idiom — `Action a = Foo;`, `Func<,>`,
`list.ForEach(action)`, `+=`/`-=` multicast, virtual-method delegates,
closures over an IL `this` — now works under Neo.

### Delivered machinery

- **`ldftn` / `ldvirtftn` (ExecuteNeo arms).** Both resolve the method token to
  an `IMethod` and store it into a managed **ref slot** (mStack), writing the
  ref-slot index into the destination frame byte offset. An `IMethod` is a
  managed object → a Neo ref slot, NOT a raw pointer or in-frame value.
  `ldvirtftn` reads `this` from `Register2`'s ref slot and resolves the
  virtual-method override via the Step-10 VTable path
  (`Type.GetVirtualMethod`), so a delegate over a derived override dispatches
  to the override.
- **Delegate `newobj` (CLR-newobj branch).** `Action<>`/`Func<>` are
  **CLRTypes** (the first of the two dump-gated design corrections below), so
  delegate `newobj` routes through the **CLR-newobj branch** (mirrors Legacy
  `Register.cs:3539-3561`), not the IL-newobj path. It reads the bound `this`
  + `IMethod` from the operand registers, builds a `DelegateAdapter` via
  `DelegateManager.FindDelegateAdapter` (binding target + method; caching on
  the `ILTypeInstance` for an instance method or on the `ILMethod` for a
  static method, exactly as Legacy), and stores the adapter into the dest ref
  slot.
- **`DelegateAdapter.InvokeILMethod` Neo path (`NeoInvokeSub`).** The
  CLR→IL callback direction (e.g. `List.ForEach(action)` invoking the IL
  action from CLR). Builds a Neo `byte*` frame, writes the CLR args into the
  callee param region (**the inverse of `CopyNeoCallArguments`** — primitives
  by direct write, reference args by mStack index, CLR value-type args via
  `WriteNeoValueType`), calls `ExecuteNeo`, and reads the return back into a
  CLR object. Uses a **fresh pooled interpreter per invoke**
  (`RequestILIntepreter`), the second dump-gated design correction — see
  below.
- **Multicast next-chain.** The `next` field, `Combine`, and `Remove` are
  engine-agnostic and reused **unchanged** (DelegateManager). `NeoInvokeSub`
  walks the `next`-chain, invoking each adapter in order, discarding
  intermediate returns, returning the last delegate's result (Legacy
  `ILInvokeSub` multicast semantics).
- **`DelegateCombineNeo` / `DelegateRemoveNeo` Neo redirects** for `+=`/`-=`
  (lower to `System.Delegate.Combine`/`Remove`, a `Call_Redirect` routed
  through the new `case OpCodeREnum.Call_Redirect` arm). Params are read in
  **declaration order** (param 0 = dele1/source).

### Two design corrections (dump-gated, both confirmed at apply)

1. **`Action<>`/`Func<>` are CLRTypes, not ILTypes.** The proposal-text
   described an "IL delegate `Newobj` arm", but the actual delegate
   constructed types are CLR types, so delegate `newobj` goes through the
   **CLR-newobj branch** (the Step-18 `InvokeNeoClrMethod(isNewobj:true)`
   path), mirroring Legacy `Register.cs:3539-3561`. The IL-newobj arms shipped
   in Steps 8b/18 are untouched.
2. **Nested re-entry uses a FRESH pooled interpreter.** The design's premise
   was "push the delegate frame past the in-flight IL frame (advance `esp`)".
   The dump disproved this: nested delegate invocation (a delegate invoked
   from a CLR redirect body that itself runs inside `ExecuteNeo`) re-enters
   via a **fresh interpreter** from the pool (the Legacy
   `BeginInvoke`→`RequestILIntepreter` pattern). There is no in-flight-frame
   clobber risk — each callback gets its own engine stack. This is what made
   the F1 leak (below) a leak rather than a correctness bug.

### F1 review-fix (round 1, Major) — pool leak

`NeoInvokeSub` initially called `RequestILIntepreter` but **never
`FreeILIntepreter`** → pool starvation (one new `ILIntepreter` per callback).
Correctness held (each runs synchronously on its own engine stack), which is
why the round-0 smoke stayed green — but production delegate-heavy IL code
(the whole point of Step 19) would leak unbounded on the callback hot path.

**Fix:** wrap the body in `try { ... } finally { appdomain.FreeILIntepreter(intp); }`,
mirroring Legacy `using (var ctx = BeginInvoke())` → `InvocationContext.Dispose`
→ `domain.FreeILIntepreter`. The free runs after `ExecuteNeo` returns and the
result is read; the `next`-chain recursion does its own balanced request/free;
the `if (unhandled) throw` path throws out of the `try`, so `finally` fires on
the exception-escape path too.

**Verified (instrumented probe, since removed):**
- 1000-callback `list.ForEach` loop → **2 allocs / 999 pool hits**
  (pre-fix ~1000/0).
- Nested-nested ForEach (delegate whose body drives another `List.ForEach`)
  → interpreter freed at each nesting level.
- Exception path (IL target throws, caught by caller) → `finally` fires,
  exception propagates, interpreter freed.

### Files (23 source)

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` — `ldftn` /
  `ldvirtftn` arms; delegate newobj routing in `Call_Redirect`; the
  `Callvirt_IL` `IsDelegate` check; `ReadNeoDelegateInvokeArgs` /
  `WriteNeoDelegateInvokeReturn`; `NeoBoxReturnValue` made public.
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` —
  `ldftn`/`ldvirtftn` lowering + the `Call_Redirect` map entry.
- `ILRuntime/Runtime/Intepreter/DelegateAdapter.cs` — `NeoInvokeSub` (with
  the F1 try/finally + `FreeILIntepreter`); `WriteNeoCallSlot`;
  `NeoInvokePublic`; the 11 per-arity `InvokeILMethod` bodies wired
  `#if ENABLE_NEO_MODE` (Legacy `#else` byte-identical).
- `ILRuntime/Runtime/CLRRedirections.cs` — `DelegateCombineNeo` /
  `DelegateRemoveNeo`.
- `ILRuntime/Runtime/Enviorment/AppDomain.cs` —
  `RegisterCLRMethodRedirectionNeo` for Combine + Remove.
- `ILRuntime/Runtime/CLRBinding/MethodBindingGenerator.cs` /
  `BindingGeneratorExtensions.cs` /
  `ILRuntime/Runtime/CLRMethod.cs` — delegate
  `CheckCLRTypes(IsDelegate)` unwrap (param read codegen + reflection
  fallback).
- **15 checked-in autogen** `ILRuntimeTestBase/AutoGenerate/System_Action_*` /
  `System_Func_*` — `Invoke_*_Neo` this-read patched to
  `CheckCLRTypes(...,IsDelegate)`. Byte-identical to what the fixed codegen
  produces (`TypeFlags.IsDelegate == 0x8`), so the next regeneration will NOT
  revert the patches.
- `TestCases/NeoStep19Test.cs` (NEW) — 10 probes (`NeoStep19_*`).

## Verification

- **Neo:** `NeoStep19` 10/10; full `NeoStep` smoke **140/140, 0 failed**
  (130 + 10 `NeoStep19_*`).
- **Legacy (plain `Debug`):** builds clean (0 errors); `NeoStep19_*` **10/10
  green** (delegates are engine-agnostic at the test level). No new Legacy
  regression.
- **Autogen patch audit (round 0, Probe 1):** CLEAN — all 15 delegate bindings
  patched, none missed, byte-identical to the fixed codegen output (next regen
  won't revert). No non-delegate binding with a delegate param was missed
  either.
- **Round 0** found the F1 Major (pool leak — green-smoke-missed since
  correctness was fine); **round 1 APPROVED.**

## Accepted-known / deferred (recorded, not dropped)

- **F-7 / NEO-DELEGATE-REFOUT** — ref/out delegate params: byref-aware arg
  marshaling in `NeoInvoke` deferred (Step 19 probe 8 uses a plain `int` to
  exercise the IL-delegate construct + Invoke routing — the load-bearing
  assertion for that path). Routed to a byref follow-up child; recorded in
  `neo-deferred-items.md` (§2 + §3) and `planning-context.md`.
- **F2 / WriteNeoCallSlot** — CLR struct with reference fields in
  `WriteNeoCallSlot` (`RefCount > 0 && Size == 4` discriminator): pre-existing
  edge (same class as opt-harden-2 / area4 deferrals); no Step 19 probe
  exercises it. Accepted-known; recorded.
- **NEO-IL-VT-INSTANCE-COVERAGE** — probe 10's `Ldfld`-on-CLR-struct-param
  is the pre-existing Step 6 gap. Recorded; reuse the existing follow-up.

These are follow-ups; Step 19 itself is a roadmap step (no deferred-item to
"resolve" — the scoped follow-ups are recorded).

## Round-1 review outcome

**APPROVED.** F1 RESOLVED (try/finally on every exit path — happy, next-chain
recursion, exception-escape; free correctly placed after ExecuteNeo + result
read; pool-reclaim independently reproduced 2/999; Legacy byte-identical;
zero probe residue). Round-0 F2 (accepted-known) and F3 (reverted) unchanged.
The change is ready for archive.
