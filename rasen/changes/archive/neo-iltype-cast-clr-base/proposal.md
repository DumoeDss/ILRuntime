# Proposal: neo-iltype-cast-clr-base (Wave-2 child C2)

## Why
Wave-2 cluster C2 (~14 full-smoke failures): `Unable to cast object of type
'ILRuntime.Runtime.Intepreter.ILTypeInstance' to type '<CLR base / interface /
Adaptor>'`. An IL instance is passed to a CLR method (as `this` or a reference
param) whose declared type is a CLR base class / interface the IL type inherits
or implements, but Neo hands the autogen binding the RAW ILTypeInstance, which is
not IS-A the CLR type.

## Root cause (re-audited against a REAL run; the task's castclass/isinst framing
was DISPROVEN -- the failure is in the autogen binding's call-arg read, NOT in
castclass/isinst)
The autogen Neo CLR bindings read the `this` and reference params via a DIRECT
cast `(TargetType)ILIntepreter.ReadNeoReference(...)` and then invoke the CLR
method on it (e.g. `ILRuntimeTest_TestFramework_ClassInheritanceTest_Binding.
TestAbstract_0_Neo:100`). When the value is an ILTypeInstance that inherits /
implements the CLR type, the cast throws InvalidCastException.

The Legacy bindings instead call `typeof(T).CheckCLRTypes(ReadNeoReference(...))`
(`Extensions.cs:248-315`), which unwraps an ILTypeInstance to `ins.CLRInstance` --
the CrossBindingAdaptor wrapper that IS-A the CLR base / interface. The Neo
reflection fallback (`CLRMethod.Invoke:578`) projects only the `this`; the
autogen-redirect path projects NEITHER the `this` NOR reference params.

The generator sites: `MethodBindingGenerator.cs:302` (`this` else-branch) and
`BindingGeneratorExtensions.cs:250` (param else-branch) emit the bare direct cast
for non-delegate CLR-reference types (delegates already use CheckCLRTypes at
:298 / :246). The checked-in bindings are STATIC (no regeneration tool), so a
generator-only fix would not change them.

## What changes
ONE engine-level choke point: project ILTypeInstance -> CLRInstance for the `this`
and by-value CLR-reference params of every CLR call, inside `InvokeNeoClrMethod`
(the single dispatch shared by the autogen-redirect and the reflection-fallback
paths), BEFORE either reader runs. The helper walks the SAME callee-frame layout
the readers use (mirrors `CLRMethod.Invoke`'s proven curPrim walk + the autogen
`AppendArgumentCodeNeo` emission -- natural primitive sizes 1/2/4/8, enum=4,
CLR-VT=GetNeoValueTypeManagedSize, ref=4, newobj skips the 4-byte retRefBase).

The projected CLRInstance is stored in a FRESH mStack slot and the callee-frame
index rewritten, so the caller's slot is untouched (no corruption for by-value
params). Byref params and delegates are SKIPPED (write-back safety / the binding
unwraps delegates itself). This mirrors Legacy's per-arg CheckCLRTypes for ALL
current + future bindings without editing any checked-in binding file.

Files: `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (entirely
`#if ENABLE_NEO_MODE` -> Legacy-neutral by construction).

## Impact
Full Neo smoke: **140 -> 133 failed (7 eliminated)**. C2 tests flipped green:
InheritanceTest01/02/03/04/14, TestIs.TestInterface (6 C2) + InheritanceTest05
(C14 TargetException bonus). NeoStep 380/0 (no regression). Stash-toggle airtight.
The remaining C2-listed tests are DISTINCT downstream sub-bugs (see design.md
"Remaining sub-bugs") -- fixed the largest sub-cluster, reported the rest.
