# Design -- neo-constrained-callvirt-residual (Wave-2 child)

## Symptom
`RefOutTest.UnitTest_GenericsRefOut2` fails on Neo with
`Step 17: Constrained not immediately followed by a callvirt (unexpected JIT shape)`
@ `ILIntepreter.Neo.cs:6957`. PASSes on Legacy.

## Root cause (Neo-vs-Legacy)
The test calls `dest.GetString()` inside `Read<T>(ref T dest) where T : TestGenrRefBase`,
with `T = TestGenrRef` (a reference-type IL class). For a generic type parameter `T`,
the C# compiler ALWAYS emits `constrained. T; callvirt M` (the CLR must resolve the
call shape based on whether T is a value type or reference type), even when `M` is
non-virtual.

`TestGenrRefBase.GetString()` is a NON-VIRTUAL ILMethod on a non-interface IL class.
The Neo JIT lowers a `callvirt` on such a method from `OpCodeREnum.Callvirt` to
`OpCodeREnum.Call` (`JITCompiler.cs:2845-2850`):
```
if (code.Code == Code.Callvirt && m is ILMethod)
{
    ILMethod ilm = (ILMethod)m;
    if (!ilm.Definition.IsAbstract && !ilm.Definition.IsVirtual && !ilm.DeclearingType.IsInterface)
        op.Code = OpCodeREnum.Call;
}
```
This lowering fires BEFORE the Neo `InitializeCallvirtDispatch` specialization (which is
itself gated `!hasConstrained` at :2852), and it is INDEPENDENT of `hasConstrained`.
The Constrained+Call pair is then reordered so the `Constrained` op precedes the `Call`
(`JITCompiler.cs:2884-2894`), with the method token copied onto the Constrained's
`Operand2` and `op.Operand4 = 1` (the constrained flag) on the Call. So the final JIT
body for `Read<T>` is:
```
10: push r0
11: constrained TestCases.RefOutTest/TestGenrRef
12: call   r2, TestCases.RefOutTest/TestGenrRefBase.GetString()
```

The Neo `Constrained` runtime arm (`ILIntepreter.Neo.cs:6948-6958`) reads the trailing
op at `cv = ip + 1` and REJECTS anything that is not one of
`{Callvirt, Callvirt_IL, Callvirt_CLR, Callvirt_Interface, Call_Redirect}`. A plain
`Call` throws. This is the defect.

Legacy does NOT have this problem: the Legacy `Constrained` arm
(`ILIntepreter.Register.cs:3898-4031`) does NOT inspect the trailing op at all. It only
PREPARES the receiver (boxes a value type, or uses the object as-is for a reference
type -- `insIdx = objRef->Value` at :3937) in place on the eval stack, then `break`s.
The NEXT instruction's own arm (Call OR Callvirt) performs the actual dispatch on the
already-prepared receiver. So Legacy accepts a `Call` after `constrained.` for free.

The Neo arm took a different design: the `Constrained` arm OWNS the dispatch (it
box-once's the receiver and calls `InvokeNeoClrMethod`/`InvokeNeoCallTarget` itself),
then skips the trailing op with `ip += 2`. This is correct and necessary for the value-
type box-once semantics, but the accepted-trailing-op guard was written too narrowly --
it omitted the `Call` case that the JIT lowering produces for a non-virtual method on a
reference-type constrained T.

## The fix
Add `OpCodeREnum.Call` to the accepted-trailing-op list at `ILIntepreter.Neo.cs:6950`.
One line, 0 removed. Neo-gated by the enclosing `#if ENABLE_NEO_MODE`.

### Why this is sufficient (no other change needed)
The Constrained arm reads everything it needs from `cv = ip + 1`:
- `cv->Operand2` -- method token. Set by `InitializeFunctionParam` (`JITCompiler.cs:3714`)
  identically for Call and Callvirt (same case `Code.Call:/Callvirt:`).
- `cv->Operand` -- NeoCallParams index. Set by the LowerNeoOffsets call-param case
  (`Optimizer.Neo.cs:1459`), whose case-list (`:1216-1222`) INCLUDES `Call`. The
  `hasConstrained` discriminator (`:1243-1246`) keys on `op.Operand4 == 1`, which the
  JIT stamps for BOTH Call and Callvirt (`JITCompiler.cs:2886`). So a constrained `Call`
  already gets a correct NeoCallParamMap.
- `cv->Register1` / `cv->DstOffset` / `cv->Operand3` -- return-slot info. Set by
  `LowerR1` (`Optimizer.Neo.cs:1477`) identically for Call and Callvirt.

The arm then performs its OWN dispatch on the resolved receiver:
- For a reference-type T (our case: `TestGenrRef`), the box-once path's Gap A branch
  (`:7098-7103`) fires: `!constrainedType.IsValueType && thisObjIdx < 0` -> it
  dereferences the receiver object (`mStack[recvIdx]`) and uses it as-is (NO box),
  mirroring Legacy's `insIdx = objRef->Value`. The resolved `actualMethod`
  (`constrainedType.GetVirtualMethod(targetMethod)` -> the ILMethod) is then dispatched
  via `InvokeNeoCallTarget` (`:7228`). This is exactly the T=string path the arm was
  already written for; `TestGenrRef` is the same shape (a reference ILType).
- The trailing `Call` is skipped by `ip += 2` (`:7238`), so the Call arm never runs --
  no interaction with the Call-arm's own dispatch.

### Why the Call lowering is CORRECT and must be honored
ECMA III.3.19: `constrained. T` on a reference type is equivalent to a plain callvirt
on the pointer (no box). When `M` is non-virtual, the resolved call is a non-virtual
call on the object. The JIT's `callvirt -> Call` lowering for a non-virtual ILMethod
faithfully encodes this, and the Constrained arm's box-once path's reference-type
branch (`!IsValueType`) already implements the "no box, use the object" semantics.
The ONLY missing piece was the dispatch-guard list.

## Scope / blast radius
The Constrained arm fires ONLY when a `Constrained` op is emitted, which happens ONLY
for a `constrained.` CIL prefix. Adding `Call` to the accepted list changes behavior
solely for the previously-NIE'd shape (`constrained. <ref-type T>; call <non-virtual
ILMethod>`); every other Constrained sub-case (value-type box-once, IL-VT direct-call,
enum, inherited Object method) is reached via the SAME arm body unchanged. The arm
already throws a locatable NIE for every UNsupported sub-case, so no silent corruption
is introduced.

The fix mirrors Legacy (which accepts Call after constrained) -> Neo/Legacy parity for
this shape.

## Verification
- Name-filter: `GenericsRefOut2` FAIL-on-HEAD (NIE) -> PASS-after.
- Stash-toggle: drop the `Call` case -> NIE returns -> restore -> PASS.
- Full smoke: `35 -> N` (lower; +-1 crash-order noise).
- NeoStep broad-green (~398/0): no regression (the Constrained arm is exercised by the
  existing Step-17 / byref probes).
- Legacy-neutral: change is `#if ENABLE_NEO_MODE`; Legacy compiles none of it.
