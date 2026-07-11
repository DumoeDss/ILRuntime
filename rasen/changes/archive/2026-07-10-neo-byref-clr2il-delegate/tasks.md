# Tasks - neo-byref-clr2il-delegate (child 14)

## Status: DONE (all green)

## Reproducer (probe-first) -- DONE
- [x] Construct a CLR->IL delegate-byref reproducer: custom delegate type
      `Clr2IlRefIntDelegate(ref int x)` + `Clr2IlOutIntDelegate(out int x)` +
      CLR host helpers `InvokeRefCallback`/`InvokeOutCallback`
      (`TestFramework.TestCLRBinding`, TestClass3.cs).
- [x] IL probes `NeoStep19_Clr2Il_ByRef` / `_Out` (IL methods
      `Clr2IlBumpRef`/`Clr2IlSetOut`).
- [x] Confirm the gap on HEAD `48af2123`: `KeyNotFoundException: Cannot find
      Delegate Adapter for: ...Clr2IlSetOut(System.Int32& x)` at
      `DummyDelegateAdapter.get_NativeDelegateType` <- `GetConvertor` <-
      `CheckCLRTypes` (the two-layer BIND + CONVERT gap).

## Fix -- DONE
- [x] D1: `DelegateManager.RegisterDelegateByRefConvertor<T>
      (Func<IDelegateAdapter, Delegate>)` + `clrByRefDelegates` dict +
      `HasByRefConvertor(type)`. `ConvertToDelegate` prefers the byref
      converter (DelegateManager.cs).
- [x] D2: `DelegateAdapter.GetConvertor` consults `HasByRefConvertor` BEFORE
      `NativeDelegateType`; `ConvertToDelegate` checks `clrByRefDelegates`
      BEFORE the Dummy throw (unblocks the byref-registered path).
- [x] D3: `DelegateAdapter.NeoInvokeByRef(object[])` (on `IDelegateAdapter`
      under `#if ENABLE_NEO_MODE`) -> `NeoInvokeSub(args, marshalByRef: true)`:
      scratch-cell staging (self-referencing byref `(objIdx==-1, off=scratch)`)
      + post-run write-back to `args[]`. Helpers `NeoByrefElemSize`,
      `WriteNeoByrefScratchValue`, `ReadNeoByrefScratchValue`.
- [x] D4: multicast passes `marshalByRef` through the next-chain.
- [x] Converters in `helper.cs` (Neo-gated) for the 4 delegate types.

## Verify -- DONE
- [x] NeoStep19 gate: 23/0/0 (was 19; +4 probes: _ByRef, _Out, _ByRefLong,
      _Multicast).
- [x] Full NeoStep smoke: 267/0/0 (was 263; +4). No regression.
- [x] Stash-toggle: on HEAD (engine fixes reverted), the probe FAILS at
      `GetConvertor`/`Cannot find Delegate Adapter`; with the fix, 4/4 PASS.
- [x] Legacy-neutral: ILRuntime + TestBase plain-`Debug` builds 0 errors (Neo-
      gated code under `#if ENABLE_NEO_MODE`; `RegisterDelegateByRefConvertor`/
      `HasByRefConvertor` are pure-additive public API, never called in Legacy).

## Adversarial probes -- DONE
- [x] `out int` (write-only): `NeoStep19_Clr2Il_Out` (->77).
- [x] `ref int` (read+write): `NeoStep19_Clr2Il_ByRef` (5->15).
- [x] `ref long` (8-byte element / scratch sizing): `_ByRefLong` (10L->1010L).
- [x] multicast ref-int (each target sees prior mutation): `_Multicast`
      (+10, *2; 5->30).

## Scope boundary -- recorded (NOT shipped)
- A REFERENCE-typed referent (`ref string`/`ref <class>` reassign) needs an
  mStack-slot stage (the F-7B promotion analog). Sequencing note; the primitive/
  value core is shipped (the common `ref int`/`out int`/`ref long` shape).
- AOT wire-up of the converter registration: out of scope (JIT path only).
