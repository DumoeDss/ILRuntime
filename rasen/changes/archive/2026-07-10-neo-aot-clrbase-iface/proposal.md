# Proposal: neo-aot-clrbase-iface

## Summary
Cecil-free AOT (`.neo`) load + execute of an IL type whose base or interface is
a CLR type that needs a `CrossBindingAdaptor` (the LOAD/EXECUTE side -- the
inverse of the archived COMPILE-side `neo-step25-clr-adaptor`). Today the
Cecil-free path resolves a CLR base/interface to a raw `CLRType` but never
installs the adaptor, so `FirstCLRBaseType`/`FirstCLRInterface` are wrong,
`TypeForCLR` resolves to the wrong CLR type, and the `ILTypeInstance.CLRInstance`
bridge is not built (an IL `: System.Exception` is not a real CLR Exception).

## Motivation
Closes lead-6 MEDIUM#6 / the deferred STEP-25 PARTIAL "CLR base/interface
resolution on the Cecil-free path (NEO-AOT-ADAPTOR-SKIP)" gap. A Cecil-free
load is the TRUE AOT scenario (a `.neo` into a fresh AppDomain with no Cecil).
Without this, an IL type inheriting a CLR base (the common `class MyException :
System.Exception` shape) mis-executes Cecil-free.

## Approach
- Engine fix (Neo-only, in `ILType.FinalizeFromNeoRecord`): after resolving the
  base/interface to a `CLRType`, look up `appdomain.CrossBindingAdaptors[clr]`
  and install the adaptor (mirroring the Cecil path's `InitializeBaseType` /
  `InitializeInterfaces`). Tweak `ResolveFirstCLRBase`/`ResolveFirstCLRInterface`
  so an adaptor base/interface is returned directly (it IS the first CLR type).
- Capstone (`NeoStep25ClrBaseIfaceCheck`, host-side DEBUG+Neo): compile
  `NeoClrProbe.ExceptionProbe : System.Exception` -> `.neo`, Cecil-free-load
  into a fresh AppDomain, assert the invariant (`FirstCLRBaseType is
  CrossBindingAdaptor`), the bridge (`CLRInstance is Exception`), and end-to-end
  exec (`Invoke(Tag) == 42` == A's JIT). Adversarial body-mutation cell.

## Scope boundary
This child is the LOAD/EXECUTE side. The COMPILE side
(`neo-step25-clr-adaptor`, archived) already emits a built-in-adaptor base
(`ExceptionProbe`) to the `.neo` and gracefully skips harness-adaptor types.
No `.neo` format change is needed (the adaptor resolves at LOAD time from the
AppDomain's `CrossBindingAdaptors` map). A harness-adaptor Cecil-free load is
out of scope (such types are skip-listed at compile time -> never reach a
Cecil-free load; a missing adaptor throws `TypeLoadException`, loud, mirroring
the Cecil path).
