## Why

Step 17 shipped the unified 8-byte Ref Slot model + `ldloca`/`ldflda`/`ldarga`/
`ldelema`/`stind_*`/`ldind_*` producers and consumers, the `ref`/`out` IL-parameter
call ABI, and the IL value-type array `ldelema` path -- but deliberately deferred
four sub-cases that were blocked on infrastructure that has now landed. The
highest-value deferred piece is **`constrained.`-on-value-type full dispatch**
(D-CONSTRAINED): `constrained.callvirt T.M` on a struct `this` (the C# lowering
of `v.ToString()` / `v.GetHashCode()` on a struct, and of any interface or
override dispatch on a struct) today throws a Step-17-tagged
`NotImplementedException` at `ILIntepreter.Neo.cs:~3139`. It was blocked on
three prerequisites -- (1) `ldarga` (now shipped in Step 17), (2) the VT `this`
in-frame address model (`[VT-THIS-ADDR]`, shipped), (3) the byref-`this`
direct-call machinery (`neo-step13-area4`, shipped) -- so it is now unblocked.
Completing it ALSO closes the area4 M2 obligation (`F-5 / NEO-CALLARG-BOXED-SRC`:
the boxed-source branch of `CopyNeoCallArguments` becomes reachable when a boxed
`this` flows from `constrained.callvirt`, and must be guarded or corrected, plus
the `CopyNeoCallThisBack` comment must be tightened). The CLR primitive-array
`ldelema` remainder (`D-LDELEMA`) is a small isolated NIE in the same `Ldelema`
arm that is cheap to close in the same cohort.

## What Changes

- **(a) `constrained.`-on-value-type full dispatch (D-CONSTRAINED) -- IN.**
  Replace the Step-17-tagged `Constrained` NIE
  (`ILIntepreter.Neo.cs:~3139`) with real box-once / direct-call dispatch for
  `constrained.callvirt T.M` where the constrained type `T` is a value type.
  Reuses VT-THIS-ADDR's in-frame VT address + area4's byref-`this` direct-call.
  Covers: a struct override that requires boxing (`v.ToString()` overriding
  `Object.ToString` -- the dispatched method sees a boxed `this`), a struct
  method that does NOT require boxing (direct call on the byref `this`), an IL
  value-type constrained call, and a CLR value-type constrained call.
- **(d) CLR primitive-array `ldelema` (D-LDELEMA remainder) -- IN.** Replace the
  `else` branch NIE in the `Ldelema` arm (`ILIntepreter.Neo.cs:~3053-3057`) with
  a Ref Slot `(arrayMStackIdx, elementByteOffset)` computed from the CLR array's
  element layout, consumable by `stind_*`/`ldind_*`. Direct indexing already
  covers the read/write path; this closes the address-of-element path for a CLR
  primitive array (`int[]`, `float[]`, etc.).
- **(M2 obligation) F-5 / NEO-CALLARG-BOXED-SRC -- IN (closes the area4 follow-up).**
  When the `constrained.callvirt` box-once path produces a boxed `this`, the
  boxed-source branch of `CopyNeoCallArguments` (`ILIntepreter.Neo.cs:~258-265`)
  becomes reachable. Add a correct copy (or a NIE guard if the box-once takes a
  different path) AND tighten the `CopyNeoCallThisBack` comment to state it
  covers mutating INSTANCE METHODS (not ctors -- the newobj path does not invoke
  it). Also fix the `T1` stale Constrained NIE text.
- **(b) `Stobj`/`Ldobj` ref-slot loop -- DEFERRED to a follow-up child
  `neo-step17-stobj-refloop`.** Today `Stobj`/`Ldobj` copy `primSize` bytes only
  (no ref-slot portion); a value type WITH reference fields copied through
  `stobj`/`ldobj` would lose its ref slots. This is a narrow, isolated extension
  (add the `TotalReferenceCount` ref-loop mirroring `Move_Vt`) that does NOT
  fall out of the constrained.-on-VT work and would, if bundled, mix two
  unrelated correctness surfaces into one diff (the explicit Step-13b/area4
  lesson).
- **(c) generic-byref / `fixed` / interface-on-VT-constrained -- DEFERRED to the
  same follow-up `neo-step17-stobj-refloop` (or a dedicated `neo-step17-misc`).**
  The closed-type byref ABI is the green target; `ref T`/`out T` with `T` a
  generic parameter, `fixed` unmanaged-pinning blocks, and the rare interface-on-
  VT-constrained sub-cases remain Step-17-tagged NIEs. None are unblocked by (a)
  and none are exercised by the smoke.

## Capabilities

### New Capabilities
<!-- None -- this change extends an existing capability. -->

### Modified Capabilities
- `neo-byref`: Flips the `constrained.` runtime arm requirement from PARTIAL /
  DEFERRED (Step-17-tagged NIE) to DELIVERED for the box-once + direct-call
  dispatch on a value-type constrained type; flips the `ldelema` CLR-primitive-
  array sub-case from NIE to DELIVERED; narrows the "Deferred byref sub-cases"
  requirement (Stobj/Ldobj ref-slot loop, generic-byref, fixed, interface-on-VT-
  constrained) to the residual set still throwing tagged NIE; records the
  `CopyNeoCallArguments` boxed-source obligation closure (F-5).

## Impact

- **`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`** -- the
  `Constrained` arm (replace NIE with dispatch); the `Ldelema` arm `else` branch
  (CLR primitive array); the `CopyNeoCallArguments` boxed-source branch
  (`~258-265`, correct copy / NIE guard); the `CopyNeoCallThisBack` comment.
- **`ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`** -- possibly a JIT
  fusion pass for `constrained.+callvirt` into a specialized callvirt opcode
  carrying the constrained type token (the architectural crux -- see design.md;
  the runtime Constrained arm consumes the trailing opcode today, but the
  preceding callvirt mis-reads the byref `this` BEFORE the Constrained arm runs,
  so the JIT likely needs to fuse or the callvirt needs to defer the `this` read
  on `Operand4 & 0x1`).
- **`ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs`** -- call-lowering
  / addrAlias gate changes IF the fusion approach is taken (constrained is
  already an escape consumer per the Step-17 gate; verify no regression).
- **`TestCases/NeoStep17Test.cs`** (extend) -- `NeoStep17_*` adversarial probes
  (struct `ToString()` boxed-once; struct `GetHashCode()`; a non-boxing struct
  method via constrained; an IL VT constrained; a CLR VT constrained; an
  override; CLR `int[]` ldelema -> stind/ldind). The `NeoStep` filter catches
  them. Do NOT create NeoStep19Test.cs.
- **Legacy (`ILIntepreter.Register.cs`) is the SEMANTIC REFERENCE** (the
  `Constrained` arm at `:3898` carries the box-once / enum / VT cases via
  `GetObjectAndResolveReference`); NOT modified.
