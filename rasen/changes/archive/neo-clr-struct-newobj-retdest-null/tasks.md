# Tasks: neo-clr-struct-newobj-retdest-null

## Phase 1 -- RE-AUDIT (VERIFY) [DONE]
- [x] Build CLI (Debug_Neo) + TestCases (Debug). (build-server shutdown + UseSharedCompilation=false after touching ILRuntimeTestBase.)
- [x] Confirm `new TestVector3(f,f,f)` into a local yields (0,0,0) under Neo; PASS on Legacy. (probe TC1/TC2 FAULT on HEAD; Legacy PASS.)
- [x] Pin the JIT lowering: Roslyn `call .ctor` shape `initobj; ldloca; <args>; push(this byref); call.redirect ctor` with dest `-` (Register1=-1), crIsNewObj=false. (JIT dump captured.)
- [x] Pin the root cause with byte-dump evidence: the autogen `Ctor_0_Neo` `!isNewObj` branch is a TODO (reads the zero `this` slot as args), AND the `Call_Redirect` arm never calls `CopyNeoCallThisBack` (retDst=null so the stub write no-ops; the byref `this` is never propagated back).

## Phase 2 -- implement [DONE]
- [x] Hand-port `Ctor_0_Neo` (`ILRuntimeTest_TestFramework_TestVector3_Binding.cs`): `!isNewObj` skips the `this` struct (`__curPrim += __thisSz`), reads args, writes the constructed struct to the `this` slot (`__frameBase`).
- [x] Runtime `Call_Redirect` arm (`ILIntepreter.Neo.cs`): mirror the `Call` arm -- snapshot write-back-flagged byref sources, invoke the redirect, then `CopyNeoCallThisBack` (no-op when `PrimitiveByRefSrc == null`).
- [x] Generator template (`ConstructorBindingGenerator.cs` `GenerateConstructorWraperCode_Neo`): declare `__thisSz` for value types; `!isNewObj` does `__curPrim += __thisSz`; return-write splits `isNewObj` (write `__retDst`) vs `!isNewObj` (write `__frameBase`). Reference-type ctors unchanged.

## Phase 2 -- verify [DONE]
- [x] Name-filter: `NeoStepClrStructNewobj_TC1_DirectCtor` (into-local), `TC2_TwoNewobj`, `TC3_NewobjAsArg` (as-value control) -- 3/3 PASS after fix.
- [x] Stash-toggle (airtight): stash engine + stub -> TC1/TC2 FAULT (DivideByZero, structs read (0,0,0)), TC3 PASS (control); pop -> 3/3 PASS.
- [x] NeoStep broad smoke: 385/0 (382 baseline + 3 new probes; 0 regression). The Call_Redirect change is broad -- VT/newobj steps (NeoStep12/13/8 family) all green.
- [x] Legacy-neutral: plain `Debug` + useRegister=true -> 3/3 probes PASS (Legacy `Ctor_0` + `ExecuteR` byte-unchanged; all changes Neo-gated).
- [x] Full Neo smoke: **101 -> 101 (delta 0)**. DelegateTest24 (the Wave-2 named test) has a SECOND, separate blocker -- `callvirt.clr` struct-arg marshalling to `List<TestVector3>.Add` delivers a zero struct to the host (host-side diagnostic confirmed). That is out of scope (sibling of child-26 `stelem.any`/`ldelem.any`); needs its own child. The struct-newobj-retdest-null contract itself is fixed (3 probes + stash-toggle).

## Files
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (Call_Redirect arm: snapshot + CopyNeoCallThisBack).
- `ILRuntimeTestBase/AutoGenerate/ILRuntimeTest_TestFramework_TestVector3_Binding.cs` (hand-port Ctor_0_Neo).
- `ILRuntime/Runtime/CLRBinding/ConstructorBindingGenerator.cs` (generator template: value-type ctor `!isNewObj` path).
- `TestCases/NeoStepClrStructNewobjTest.cs` (new probes TC1/TC2/TC3).
