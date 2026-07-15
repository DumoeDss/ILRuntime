# Design: neo-step19-delegate-enumerator-group

## Decision: the 4 tests have DISTINCT roots (no shared multi-flip root)

The ground-13 handoff's hypothesis ("4 tests all touch Step-19 delegate/enumerator
dispatch -- possibly a shared root") is DISPROVEN by per-test re-audit. Each fails
at a different Neo.cs path:

| test | site | root | class |
|------|------|------|-------|
| DelegateTest42 | DelegateTest.cs:662 `del.Target != cls2` | `Delegate.get_Target` has NO Neo redirect twin -> reflection returns wrong object for an IDelegateAdapter | missing-Neo-redirect (child-2/6/22 lineage) |
| UnitTest_10046 | TestValueTypeBinding.cs:470 `a.X != 2` (delegate body) | delegate-INVOKE struct-arg marshalling (CLR delegate wrapping IL static method, by-value struct arg) | Step-19 invoke arg-marshal |
| UnitTest_10051 | TestValueTypeBinding.cs:619 `list[0].V2.x.RawValue != 999` | constrained-callvirt PROPERTY read on a NESTED struct field (`.x.RawValue`) | nested-struct-field property read (F-10-pinned) |
| MyTest.Test | Test01.cs:618 InvalidCast `String -> IEnumerator` at `get_Current_0_Neo:48` | enumerator `this` register corrupted to a String on a later loop iteration | register/frame aliasing in loop |

This child fixes ONLY DelegateTest42 (the tractable one) and reports the rest.

## The DelegateTest42 fix (the implemented change)

**Root (JIT-dump-confirmed):** the JIT emits `callvirt.clr r6, r6, System.Delegate::get_Target()` (a raw Callvirt_CLR, NOT Call_Redirect). That happens because `AppDomain.cs:354` registers the Legacy redirect on `redirectMap` only -- there is no `RedirectMapNeo` entry. The Neo call path decides redirect-vs-reflection at JIT time (Callvirt_CLR -> no runtime RedirectMapNeo lookup; the runtime arm at ILIntepreter.Neo.cs:4139 goes straight to `ResolveNeoCallvirtCLRTarget` -> `InvokeNeoClrMethod` -> reflection). The reflection fallback does not return `((IDelegateAdapter)dele).Instance` for an IL delegate (held as an IDelegateAdapter, an ILTypeInstance subclass, NOT a System.Delegate), so `del.Target != boundInstance` -> the test's `throw`.

Why v2/v3 (CLR delegates) pass but v4 (IL delegate) fails: v2/v3 use REAL CLR Delegates
(`Delegate.CreateDelegate` via the CLR-delegate newobj path, ILIntepreter.Neo.cs:3843),
so reflection `((Delegate)dele).Target` works. v4 uses an IL delegate type -> the IL-delegate
newobj path (ILIntepreter.Neo.cs:3861) stores an IDelegateAdapter -> reflection mis-reads.

**Fix (mirror Legacy `DelegateGetTarget` exactly, Neo-gated):**
1. `CLRRedirections.DelegateGetTargetNeo` -- signature `void(ILIntepreter, byte* frameBase,
   AutoList, CLRMethod, bool, byte* retDst, int retRefBase)` (the standard Neo redirect shape).
   Reads the single `this` ref via `ReadNeoReference(frameBase, ref curPrim=0, mStack)`; null ->
   NRE; `is IDelegateAdapter da` -> `da.Instance`; else `((Delegate)dele).Target`. Result via
   `WriteNeoObjectResult` (null-aware: emits the `-1` sentinel for a static delegate's null
   Target, so the caller's `brfalse.ref` / `ceq.ref` null-test reads null correctly -- this is
   why WriteNeoDelegateResult is WRONG here: it writes a valid index even for null).
2. `AppDomain.cs` ctor: `RegisterCLRMethodRedirectionNeo(mi, CLRRedirections.DelegateGetTargetNeo)`
   under `#if ENABLE_NEO_MODE`, immediately after the Legacy registration.

Once registered on RedirectMapNeo, the JIT emits `call.redirect` for get_Target and the
redirect runs instead of the reflection fallback -- restoring Legacy parity.

## Soundness

- Byte-for-byte Legacy semantics (`DelegateGetTarget` at CLRRedirections.cs:1271): same
  IDelegateAdapter-vs-Delegate split, same `.Instance` / `.Target` returns.
- Null-aware write matches the Neo null convention (ReadNeoReference `idx >= 0 ?
  mStack[idx] : null`); a static delegate's null Target round-trips as null.
- Real CLR Delegate delegates (v2/v3 and any CLR-side `del.Target`) take the `else`
  branch -- identical to reflection, no behavior change.
- Neo-gated -> Legacy-neutral by construction (verified: plain Debug build, 0 errors).

## Why NOT fix #2/#3/#4 here

- #2 (UnitTest_10046): the delegate-INVOKE struct-arg path is a DISTINCT Step-19 surface
  (invoke arg marshalling, not Target read). Distinct JIT/runtime path; a focused JIT-dump
  triage child. Risky to bundle.
- #3 (UnitTest_10051): already pinned by the F-10 child as a nested-struct-field
  constrained-callvirt property read (`.x.RawValue`) -- unrelated to delegates.
- #4 (MyTest.Test): register/frame aliasing (enumerator `this` clobbered by a String temp
  in the loop) -- a liveness/aliasing bug, not delegate/enumerator dispatch.

## Surfaced follow-up (not in the 13)

`Delegate.op_Equality` / `op_Inequality` (AppDomain.cs:235-242) ALSO register only a Legacy
redirect, no Neo twin. Latent (no failing test in the current smoke exercises `==`/`!=` on an
IL delegate via the raw callvirt path; DelegateTest43's `!= null` is handled elsewhere). A
future child can add the Neo twins identically if a test surfaces.
