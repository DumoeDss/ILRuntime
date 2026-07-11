# Design: neo-aot-clrbase-iface (Cecil-free CLR base/interface adaptor)

> Technical design for the LOAD/EXECUTE side of a Cecil-free AOT (`.neo`) type
> whose base or interface is a CLR type that needs a `CrossBindingAdaptor`.
> Grounded in the shipped Cecil-free machinery (S3-2 / S3-4) on HEAD. Legacy is
> the REFERENCE, not a target. This child = lead-6 MEDIUM#6.

## Context

The Cecil-free load path (`LoadNeoAssembly` / `ILType.CreateFromNeoRecord` /
`FinalizeFromNeoRecord`) shipped in S3-2 (2026-07-08-neo-step25-s3-cecil-free-load)
+ S3-4 (.cctor seeding). It handles an IL type whose base/interface is:
  - another IL type in the same `.neo` (resolves by name in `mapType`), OR
  - a BCL primitive / `System.Object` (resolves via the CLR fallback).

The DEFERRED gap (per the S3-2 design D-GATE 4 + the deferred-items STEP-25
PARTIAL row): a Cecil-free IL type whose base or interface is a CLR type that
needs a `CrossBindingAdaptor` (e.g. `class X : System.Exception`, where
`ExceptionAdaptor` is the bridge). The COMPILE side of this
(`neo-step25-clr-adaptor`, archived) made the standalone CLI robust to such
types (a built-in-adaptor base COMPILES; a harness-adaptor base is a graceful
TYPE-level skip). This child is the inverse -- the LOAD/EXECUTE side: a REAL IL
type with a CLR base/interface is loaded Cecil-free + executed, and the adaptor
must be installed on the Cecil-free ILType so CLR-base dispatch + the CLR
instance bridge work.

## The dump-gate verdict (binding -- reproduced by code-reading on HEAD)

### D-GATE 1. How the Cecil path installs the adaptor (the reference)

`ILType.InitializeBaseType` (ILType.cs:1916-2018) + `InitializeInterfaces`
(:1885-1915) are the Cecil-path base/interface resolvers. For a CLR base that
is NOT `System.Object`/`Enum`/`ValueType`/`MulticastDelegate`, they:
  1. resolve the base to a `CLRType` via `appdomain.GetType(CecilTypeRef,...)`;
  2. look up `appdomain.CrossBindingAdaptors[baseType.TypeForCLR]` (a
     `Dictionary<Type, CrossBindingAdaptor>`, AppDomain.cs:467);
  3. REPLACE `baseType` with the `CrossBindingAdaptor` instance (:1999 / :1905);
  4. set `firstCLRBaseType` = the adaptor (the `while (curBase is ILType)` walk
     at :2011-2016 lands on the adaptor for an IL type with a CLR base).

`InitializeInterfaces` does the analog for a CLR interface: replaces the
interface entry with the adaptor + sets `firstCLRInterface` (:1900-1907).

Two consumers depend on `FirstCLRBaseType`/`FirstCLRInterface` being the
adaptor:
  - **`ILType.TypeForCLR` (ILType.cs:1747-1755):** returns
    `((CrossBindingAdaptor)FirstCLRBaseType).RuntimeType.TypeForCLR` (the
    Adapter's CLR type, e.g. `ExceptionAdaptor.Adapter`). WITHOUT the adaptor
    it falls through to the `else` branches (the raw CLR type / reflection type)
    -- wrong CLR type for any CLR-side interop.
  - **`ILTypeInstance` ctor (ILTypeInstance.cs:364-382):**
    `if (type.FirstCLRBaseType is CrossBindingAdaptor) clrInstance =
    adaptor.CreateCLRInstance(...)`. WITHOUT the adaptor, `clrInstance = this`
    (no CLR bridge) -- the instance is NOT a CLR Exception, so the Throw
    opcode's unwrap (ILIntepreter.Neo.cs:5131 `ex = ili.CLRInstance as
    Exception`) yields null, and any inherited CLR-base member dispatch via
    `clrInstance` is broken.

### D-GATE 2. The Cecil-free path does NOT install the adaptor (the gap)

`ILType.FinalizeFromNeoRecord` (ILType.cs:1463-1535) is the Cecil-free pass-2
base/interface resolver. It:
  1. resolves `baseType` via `ResolveTypeRefToIType` (by name -> a `CLRType`
     for a CLR base), (:1471);
  2. resolves `firstCLRBaseType` via `ResolveFirstCLRBase(baseType)`
     (:1493) -- which returns the raw `CLRType` DIRECTLY (the `if (bt is
     CLRType) return bt;` at :1545), NOT the adaptor;
  3. interfaces analogously via `ResolveFirstCLRInterface` (:1494, :1560-1572).

**There is NO `CrossBindingAdaptors` lookup anywhere on the Cecil-free path.**
So a Cecil-free `class X : System.Exception` has:
  - `baseType` = raw `CLRType(System.Exception)` (should be `ExceptionAdaptor`);
  - `firstCLRBaseType` = raw `CLRType` (should be `ExceptionAdaptor`);
  - `TypeForCLR` = the wrong CLR type (the `else` branch);
  - `ILTypeInstance.clrInstance` = `this` (no CLR bridge).

### D-GATE 3. The repro (what fails on HEAD for a Cecil-free CLR-base type)

A Cecil-free load of `ExceptionProbe : System.Exception` (NeoClrProbe, already
compiled+emitted by the COMPILE side, neo-step25-clr-adaptor) into a fresh
AppDomain B, then `Instantiate` + `Invoke` a method:
  - **Instantiate:** the `ILTypeInstance` ctor's `FirstCLRBaseType is
    CrossBindingAdaptor` check is FALSE (raw CLRType) -> `clrInstance = this`.
    No exception here yet (the ctor does not throw for a non-adaptor base).
  - **A method that calls an inherited CLR-base member** (e.g.
    `this.ToString()` resolved to `System.Object.ToString`, or a field set via
    the Exception adaptor): the dispatch routes through `clrInstance`, which is
    `this` (an ILTypeInstance, not a CLR Exception). Depending on the exact
    member, this either NRE's, mis-dispatches, or yields a wrong type.
  - **`TypeForCLR`:** returns the wrong CLR type -> any CLR-side interop that
    keys on `TypeForCLR` (the delegate adapter, the CLR ctor invoke) is broken.

The decisive, stable assertion: `type.FirstCLRBaseType is CrossBindingAdaptor`
is FALSE on HEAD for a Cecil-free CLR-base type. After the fix it is TRUE. This
is the stash-toggle-able, load-bearing invariant.

### D-GATE 4. Scope: SMALL

SMALL. The adaptor lookup is a 3-line addition to `FinalizeFromNeoRecord`
mirroring the Cecil path's `CrossBindingAdaptors.TryGetValue`. The
`ExceptionAdaptor` is registered by the AppDomain ctor
(AppDomain.cs:275) for ANY AppDomain (including a fresh Cecil-free B) -- so a
built-in-adaptor base resolves with no extra registration. The probe
(`ExceptionProbe : System.Exception`) already exists in NeoClrProbe + already
compiles+emits (the COMPILE side shipped this). The capstone mirrors the S3-2
capstone discipline (compile in A, Cecil-free-load into fresh B, invoke,
assert == A's JIT; adversarial body-mutation cell).

## Decisions

### D1. Install the adaptor in FinalizeFromNeoRecord (the fix)

`FinalizeFromNeoRecord` (ILType.cs:1463), after resolving `baseType` to a
`CLRType` (and analogously each `interfaces[i]`), adds a Neo-only block
mirroring `InitializeBaseType`/`InitializeInterfaces`:

```
// baseType: if it resolved to a CLRType needing an adaptor, install the
// adaptor (mirrors InitializeBaseType :1996-2007). System.Object / Enum /
// ValueType / MulticastDelegate are NOT adaptor-requiring (the Cecil path
// nulls them at :1985-1993) -> left as-is.
if (baseType is CLRType clrBase)
{
    var clr = clrBase.TypeForCLR;
    if (clr != typeof(object) && clr != typeof(System.Enum) && clr != typeof(Enum)
        && clr != typeof(ValueType) && clr != typeof(MulticastDelegate))
    {
        if (appdomain.CrossBindingAdaptors.TryGetValue(clr, out var adaptor))
            baseType = adaptor;
        else
            throw new TypeLoadException("Cannot find Adaptor for:" + clr);
    }
}
// interfaces[i]: same lookup (mirrors InitializeInterfaces :1900-1910). A CLR
// interface needing an adaptor is replaced with the adaptor; firstCLRInterface
// set. (Only one CLR interface is valid -- the Cecil path's constraint.)
```

Then `ResolveFirstCLRBase` / `ResolveFirstCLRInterface` are called AFTER the
adaptor installation, so `firstCLRBaseType` / `firstCLRInterface` land on the
adaptor (the `while (curBase is ILType)` walk + the `if (it is CLRType) return
it` now returns the adaptor, which IS the CLRType-subclass via
`CrossBindingAdaptor : ILType`... -- see D2).

### D2. CrossBindingAdaptor is an ILType subclass -- ResolveFirstCLRBase needs a tweak

`CrossBindingAdaptor` extends `ILType` (it is NOT a `CLRType`). So
`ResolveFirstCLRBase`'s `if (bt is CLRType) return bt;` does NOT catch an
adaptor base; its `if (bt is ILType ilt)` branch recurses into the adaptor's
OWN `baseType`/`firstCLRBaseType` (which for `ExceptionAdaptor` is the Cecil-
loaded CLR `System.Exception` -- NOT what we want). D2 special-cases: if the
resolved `baseType` (post-adaptor-install) `is CrossBindingAdaptor`, return it
directly (it IS the first CLR base type, by construction). Same for an adaptor
interface. This mirrors the Cecil path's `firstCLRBaseType = curBase` landing
on the adaptor (the `while (curBase is ILType)` walks past IL bases and stops
at the non-IL adaptor).

Concretely: `ResolveFirstCLRBase` gains `if (baseType is CrossBindingAdaptor
cba) return cba;` as the FIRST check. `ResolveFirstCLRInterface` analogously
checks each interface `is CrossBindingAdaptor`.

### D3. The capstone probe (ExceptionProbe : System.Exception)

`NeoClrProbe.ExceptionProbe` (already in the repo, already compiles+emits to a
`.neo` per the COMPILE-side child). It declares `public int Tag() => 42;`.
For this capstone we need a method that (a) round-trips under Cecil-free exec
== A's JIT, and (b) EXERCISES the CLR-base bridge. A trivial `Tag()` only
proves the ILType loads + the method body runs -- it does NOT prove the adaptor
is installed (the body never touches the CLR base). D3 EXTENDS the probe with a
method that touches the CLR base:
  - `public string GetMessage()` that reads an inherited CLR-base member. The
    cleanest CLR-base-touching member on `System.Exception` that does not
    require ctor args is `Message` (the protected field / the virtual getter).
    BUT an IL override of `Message` is complex; instead D3 uses the simplest
    stable CLR-bridge exercise: instantiate (the `ILTypeInstance` ctor's
    `clrInstance` bridge) + assert `type.FirstCLRBaseType is
    CrossBindingAdaptor` + assert the instance's `CLRInstance is Exception`
    (the bridge produced a real CLR Exception). This is the DIRECT, load-
    bearing assertion of D-GATE 3's invariant. PLUS a method call so the
    capstone proves end-to-end exec (Tag() == 42).

The capstone thus has TWO load-bearing assertions:
  - (a) the invariant: `FirstCLRBaseType is CrossBindingAdaptor` (FALSE on
    HEAD, TRUE after the fix) -- the stash-toggle-able core;
  - (b) end-to-end: instantiate + invoke Tag() == 42 under Cecil-free exec
    (proves the type loads + a method runs, the S3-2 capstone discipline).

### D4. The adversarial body-mutation cell (mandatory)

Mirror the S3-2 M1 cell: deserialize `modelA` into `model2`, MUTATE `Tag`'s
`Ldc_I4 42 -> 555` in `model2`'s body BEFORE `LoadNeoAssembly`, Cecil-free-load
into fresh B2, invoke Tag() -> assert 555. A Cecil-fallback (re-reading the
Cecil body) yields 42 -> FAIL. Proves ExecuteNeo runs the genuine `.neo` body
on the Cecil-free CLR-base type.

### D5. Gating: Neo-only, Legacy-neutral, additive

- The `FinalizeFromNeoRecord` adaptor-install block + the
  `ResolveFirstCLRBase`/`ResolveFirstCLRInterface` tweak are `#if
  ENABLE_NEO_MODE` (the whole factory is Neo-only; `FinalizeFromNeoRecord` is
  only called on the Cecil-free path, never on the Cecil ctor path).
- The capstone check (`NeoStep25ClrBaseIfaceCheck`) + the probe extension are
  `#if ENABLE_NEO_MODE && DEBUG` / in NeoClrProbe (test-only).
- The Cecil ctor + `InitializeBaseType` / `InitializeInterfaces` + ALL lazy
  inits are UNCHANGED. `ExecuteNeo`, the optimizer, the JIT, Step-22/23/24,
  the standalone CLI, Legacy `ExecuteR` UNCHANGED.
- The COMPILE side (neo-step25-clr-adaptor) already emits `ExceptionProbe` to
  the `.neo`; no `.neo` format change is needed for the LOAD side (the adaptor
  is resolved at LOAD time from the AppDomain's `CrossBindingAdaptors` map, not
  serialized).

## Risks / Trade-offs

- **[The adaptor is not registered in the fresh Cecil-free AppDomain B]** ->
  the AppDomain ctor registers `ExceptionAdaptor` + `AttributeAdapter` for ANY
  AppDomain (AppDomain.cs:268-275), so a built-in-adaptor base resolves. A
  HARNESS-adaptor base (TestClass2) would NOT resolve in B (no harness init) ->
  the `TypeLoadException` (D1) is the correct, loud failure (mirrors the Cecil
  path). The capstone uses `ExceptionProbe` (built-in adaptor) precisely to
  avoid this; a harness-adaptor Cecil-free load is out of scope (the COMPILE
  side already skip-lists such types -> they never reach a Cecil-free load).
  Mitigation: capstone uses the built-in adaptor; the TLE is the documented
  behavior for a harness adaptor.
- **[ResolveFirstCLRBase tweak breaks the IL-base capstone]** -> LOW. The tweak
  adds a `is CrossBindingAdaptor` short-circuit FIRST; the existing IL-base
  walk is unchanged for a non-adaptor base. The S3-2 capstone (IL base) + S3-4
  capstone regression prove no break. Mitigation: NeoStep25CecilFreeLoad +
  NeoStep25LoadExec regression.
- **[The CLRInstance bridge is not exercised by a trivial method]** -> D3's
  invariant assertion (`FirstCLRBaseType is CrossBindingAdaptor` + `CLRInstance
  is Exception`) is the DIRECT bridge proof (the ILTypeInstance ctor uses
  `FirstCLRBaseType` to build `CLRInstance`). A green invariant + a green
  Tag() invocation cover both the bridge + end-to-end exec. Mitigation: both
  assertions mandatory.

## Migration Plan

None. Additive + Neo-only. Rollback = revert the `FinalizeFromNeoRecord`
adaptor-install block + the `ResolveFirstCLRBase`/`ResolveFirstCLRInterface`
tweak + the capstone check + the probe extension. No shared Cecil-path code
depends on the Cecil-free factory.
