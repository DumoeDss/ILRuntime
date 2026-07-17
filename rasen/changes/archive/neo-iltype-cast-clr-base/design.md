# Design: neo-iltype-cast-clr-base (Wave-2 child C2)

## Re-audit (the task's castclass/isinst framing was DISPROVEN)
The task suspected castclass/isinst/unbox miss the ILTypeInstance->adaptor
unwrap. Verified against a REAL run: Neo castclass (`ILIntepreter.Neo.cs:~5140`)
and isinst (:~5112) BOTH keep the raw `obj` when `CanAssignTo` returns true --
byte-identical to Legacy (`ILIntepreter.Register.cs:4385`/`:4435`), which ALSO
keeps the raw obj. So castclass/isinst are NOT the bug.

The real stack (InheritanceTest01):
```
InvalidCastException: Unable to cast ILTypeInstance to ClassInheritanceTest
  at ILRuntimeTest_TestFramework_ClassInheritanceTest_Binding.TestAbstract_0_Neo (...:100)
  at ILIntepreter.InvokeNeoClrMethod (...:1098)
  at ILIntepreter.ExecuteNeo (...:3642)   [Callvirt_CLR case]
```
Line 100: `ClassInheritanceTest instance = (ClassInheritanceTest)ReadNeoReference(...)`
-- a DIRECT cast in the AUTOGEN binding. Legacy's binding (line 111) uses
`typeof(ClassInheritanceTest).CheckCLRTypes(StackObject.ToObject(...))`, whose
ILTypeInstance branch (`Extensions.cs:297-313`) returns `ins.CLRInstance` (the
CrossBindingAdaptor wrapper that IS-A the CLR base).

## The fix (engine-level choke point, mirrors Legacy's per-arg CheckCLRTypes)
Add `ProjectNeoClrCallRefArgs(clrMethod, isNewobj, targetBase, mStack)` at the
TOP of `InvokeNeoClrMethod` (before `redirectNeo` / `clrMethod.Invoke`). It walks
the callee-frame param region with the SAME layout the readers use:

1. newobj -> `curPrim += 4` (skip retRefBase; mirrors autogen `Ctor_*_Neo` +
   `CLRMethod.Invoke:364`).
2. HasThis -> CLR-VT declaring type: `curPrim += GetNeoValueTypeManagedSize`;
   else (ref this) project slot at curPrim, `curPrim += 4`.
3. for each param (de-byref `pt = ptRaw.IsByRef ? ptRaw.ElementType : ptRaw`):
   - CLR VT (non-prim, non-enum) -> `curPrim += GetNeoValueTypeManagedSize`.
   - ILType -> `curPrim += 4` (reader wants an ILTypeInstance; do NOT project).
   - CLR reference type (class/interface/object) -> project by-value slot, skip
     byref (write-back safety) and delegate (binding unwraps via its own
     CheckCLRTypes(IsDelegate)); `curPrim += 4`.
   - primitive/enum -> `curPrim += NeoClrPrimitiveSlotSize(t)` (1/2/4/8, enum=4).

`ProjectNeoClrRefSlot(targetBase, slotOff, mStack, targetType, isThis)` reads the
4-byte index, and if `mStack[idx] is ILTypeInstance ili && !(ili is
ILEnumTypeInstance)`:
- isThis=true: project whenever `ili.CLRInstance != null && != obj` (NO
  assignability guard -- mirrors Legacy CheckCLRTypes which projects
  unconditionally for an ILTypeInstance; LOAD-BEARING for Object base-calls,
  see Gotcha 1).
- isThis=false (param): project only when `!targetType.IsInstanceOfType(obj)`
  (the raw ILTypeInstance does not already satisfy the param type; protects
  ILTypeInstance-typed / object-typed params, see Gotcha 2).

The projected CLRInstance goes into a FRESH `mStack.Add` slot; the callee-frame
index is rewritten. The caller's slot is untouched -> no corruption for by-value
params. A pure-CLR call (no ILTypeInstance in any ref slot) is a read-only walk.

## Gotcha 1 (CRITICAL): the System.Object base-call StackOverflow recursion
The first full smoke after the naive fix CRASHED (StackOverflow) in
InheritanceTest07. Mechanism: an IL override `TestCls5.ToString()` whose body is
`return base.ToString()` lowers to `call System.Object::ToString` ->
`System_Object_Binding.ToString_0_Neo` reads the `this` and calls
`instance.ToString()` (VIRTUAL dispatch). With the raw ILTypeInstance as `this`,
that re-enters `ILTypeInstance.ToString()` (host, `ILTypeInstance.cs:1055`) which
`AppDomain.Invoke`s the IL override -> `base.ToString()` -> ... infinite recursion.

Fix: for the `this`, project unconditionally (isThis=true, no assignability
guard) so the Object binding receives the adaptor and calls
`adaptor.ToString()` (the CLR base) instead of virtually re-dispatching on the
ILTypeInstance. This mirrors Legacy's CheckCLRTypes (which projects an
ILTypeInstance `this` to CLRInstance even for `typeof(object)`). Safe because a
VIRTUAL ToString/Equals/GetHashCode on an IL object routes to callvirt.il / the
IL override (ResolveNeoGenericCallvirtTarget: `thisObj is ILTypeInstance` ->
ResolveNeoCallvirtILTarget), NOT the Object binding -- so projecting the Object
binding's `this` only affects base/explicit calls. (Confirmed: the run no longer
crashes; InheritanceTest07 fails cleanly on its separate C14 TargetException.)

## Gotcha 2 (NeoStep regression): ILTypeInstance-typed params
The naive "project every ILTypeInstance" version regressed
`NeoStep14_ILEx_GapB_NewobjStringArg`: an exception ctor whose param is typed
`ILTypeInstance` had its arg converted to `ExceptionAdaptor+Adapter`, and the
reflection fallback's `def.Invoke` threw "cannot convert Adapter to
ILTypeInstance". Fix: for a PARAM, project only when the raw ILTypeInstance does
NOT already satisfy the param type (`!targetType.IsInstanceOfType(obj)`). This
keeps ILTypeInstance-typed / object-typed params as-is and still projects
CLR-base/interface params (which the raw ILTypeInstance never satisfies).

## Remaining sub-bugs (distinct roots; NOT fixed here -- reported honestly)
These C2-listed tests STILL FAIL after the fix, with DIFFERENT downstream errors
(they got past the original cast):
- InheritanceTest16 -> "Not supported opcode Muli_R4" (a JIT typed-arithmetic
  specialization gap, the child-16/21 float-mul class -- separate).
- InheritanceTest21 / InheritanceTest22 -> NRE downstream.
- InheritanceTest06 -> a different castclass/`Specified cast` path.
- TestAs03 / RefOutTest.UnitTest_OutTest -> "cast to 'Adaptor'" (the
  CrossBindingAdaptor nested Adaptor type -- a different cast path).
- StructTest6 -> "cast String to ILTypeInstance" (the REVERSE direction).
- GenericMethodTest11 -> constrained.callvirt-to-CLR-interface edge case (the
  boxed-receiver path at `ILIntepreter.Neo.cs:~6534` writes the `this` index to
  `cmap.PrimitiveDst[0]`; the projection walks offset 0 -- needs a separate look
  at whether the constrained cmap lays out the `this` at offset 0).

Each is its own child. This child fixed the largest sub-cluster (the autogen
binding direct-cast on this/ref-params: 6 C2 + 1 C14 = 7 failures).

## Verification (truth = full-smoke number)
- Full Neo smoke: **140 -> 133 failed** (Ran 914). C2 flipped green:
  InheritanceTest01/02/03/04/14, TestIs.TestInterface; plus InheritanceTest05.
- NeoStep: **380/0** (no regression).
- Stash-toggle (pathspec stash of ILIntepreter.Neo.cs only): stashed -> rebuild
  -> InheritanceTest01 FAILS (InvalidCastException); pop -> rebuild -> PASS.
  Airtight.
- Legacy-neutral: plain `Debug` CLI builds 0 errors (the changed file is entirely
  `#if ENABLE_NEO_MODE`); InheritanceTest01 PASSES under plain Debug +
  useRegister=true.

## Generator follow-up (out of scope, noted)
The generator's `else`-branches (`MethodBindingGenerator.cs:302`,
`BindingGeneratorExtensions.cs:250`) still emit the bare direct cast for
non-delegate CLR-reference types. The engine-level projection makes this
harmless for all current bindings (and any future regen), but a future regen
SHOULD emit CheckCLRTypes there to mirror Legacy exactly (self-contained correct
bindings). Left as a follow-up; not required for this fix.
