## 1. JIT-dump reconnaissance (do FIRST -- resolves OQ1/OQ2)

- [x] 1.1 Build the CLI (`dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`) + TestCases (`dotnet build TestCases/TestCases.csproj -c Debug`); confirm 0 errors and NeoStep smoke 130/130 baseline green.
- [x] 1.2 Dump the JIT output for `Action a = StaticFoo;` / `Func<int,int> f = Bar;`. CONFIRMED: (a) `ldftn` lowers with `Operand2` = method token, `Register1` = dest (no JIT change needed; the optimizer needed a new ldftn/ldvirtftn lowering case to stamp the dest ref slot + DstOffset); (b) `ldvirtftn` lowers with `Register1` = dest, `Register2` = `this` source; (c) the delegate `.ctor` `Newobj` -- for the COMMON case (`Action<>`/`Func<>`) the declaring type is a CLRType (NOT an ILType), so it routes through the CLR newobj branch. OQ1 RESOLVED: the JIT routes the ctor's `(target, fnptr)` args through `CopyNeoCallArguments` -> `targetBase` as a 2-entry NeoCallParamMap (map[0]=target size 4, map[1]=fnptr size 8; the 8-byte fnptr slot's first 4 bytes hold the IMethod's mStack index).
- [x] 1.3 DUMP-confirmed the delegate ctor param-region layout: `map.PrimitiveDst.len=2 sizes=4,8`; map[0]=target (object, 4-byte mStack index), map[1]=fnptr (IntPtr, 8-byte slot whose first 4 bytes = IMethod mStack index). The map does NOT include a `this` slot.
- [x] 1.4 Removed the recon probe; findings recorded in design.md / planning-context.md.

## 2. `ldftn` / `ldvirtftn` opcode arms in ExecuteNeo

- [x] 2.1 Added `case OpCodeREnum.Ldftn:` in `ILIntepreter.Neo.cs`: `m = AppDomain.GetMethod(ip->Operand2)`; dest ref slot = `frameRefBase + ip->Operand`; `mStack[dstRef] = m`; write index to `ip->DstOffset`. Added the matching optimizer lowering (`Optimizer.Neo.cs`): stamp `op.Operand = localInfos[Register1].RefOffset` + `LowerR1` (and for ldvirtftn lower R2 -> SrcOffset).
- [x] 2.2 Added `case OpCodeREnum.Ldvirtftn:`: read `this` from `ip->SrcOffset` (mStack index); resolve via `((ILTypeInstance)thisObj).Type.GetVirtualMethod(...)` (ILMethod target) / `Type.GetVirtualMethod` (CLR target) / `BaseType.GetVirtualMethod` (CrossBindingAdaptorType); store the resolved IMethod into the dest ref slot.
- [x] 2.3 Build clean; the catch-all NIE for ldftn/ldvirtftn is gone.

## 3. Delegate `Newobj` arm in ExecuteNeo

- [x] 3.1 DEVIATION from the design (key finding): the common `Action<>`/`Func<>` case is a CLRType, handled in the CLR newobj branch (`if (targetMethod.DeclearingType is CLRType)`), NOT the `ilNewobjType.IsDelegate` branch. Added a `clrDeclType.IsDelegate` sub-branch in the CLR newobj path: read target (map[0]) + IMethod (map[1]) from `targetBase`, build the adapter via `DelegateManager.FindDelegateAdapter(clrDeclType, ...)`, store into the dest ref slot. ALSO implemented the IL-defined delegate newobj in the `ilNewobjType.IsDelegate` branch (same map layout; uses the IL `FindDelegateAdapter` overload) -- needed for IL-defined delegate types.
- [x] 3.2 Build; TC1-TC10 construct successfully.

## 4. `InvokeILMethod` Neo calling convention (the CLR -> IL callback)

- [x] 4.1 Added a `NeoInvokeSub(object[] args)` helper in `DelegateAdapter.cs` under `#if ENABLE_NEO_MODE`: requests a FRESH interpreter (Legacy BeginInvoke semantics -- Risk 3 dissolved: a callback from inside ExecuteNeo runs on its OWN engine stack, no in-flight frame to clobber), builds a Neo `byte*` frame at StackBase, writes `this`(slot 0) + each param via `WriteNeoCallSlot`, calls `ExecuteNeo`, reads the return, restores mStack.
- [x] 4.2 Wired ALL 11 per-arity `FunctionDelegateAdapter<...>`/`MethodDelegateAdapter<...>` `InvokeILMethod` bodies to call `NeoInvoke(new object[]{...})` under `#if ENABLE_NEO_MODE` (Legacy `StackObject` path byte-identical under `#else`).
- [x] 4.3 Multicast `next`-chain walk reused: `NeoInvokeSub` walks `next` discarding intermediate returns (mirrors Legacy `ILInvokeSub:965-974`).
- [x] 4.4 Built `--no-incremental`; TC1-TC10 green.

## 4b. Supporting Neo arms landed as part of Step 19 (multicast + IL-delegate-Invoke + delegate unwrap)

- [x] 4b.1 `Call_Redirect` Neo arm (`ILIntepreter.Neo.cs`) + optimizer case (`Optimizer.Neo.cs`): the C# `+=`/`-=` multicast lowering compiles to `System.Delegate.Combine`/`Remove` (a `Call_Redirect`). Added the runtime arm (routes through `InvokeNeoClrMethod`) + the optimizer Call-case entry so the NeoCallParamMap is built.
- [x] 4b.2 Neo redirects `DelegateCombineNeo` / `DelegateRemoveNeo` (`CLRRedirections.cs`) + registration (`AppDomain.cs`): the StackObject Legacy redirects are not Neo-aware; added Neo-signature variants that read the two Delegate params (IDelegateAdapter unwrap), apply the multicast, and write the result. PARAM READ ORDER = source/declaration order (param 0 = dele1/source), NOT stack order.
- [x] 4b.3 IL-delegate-Invoke callvirt routing (`Callvirt_IL` arm): an IL-defined delegate's `Invoke` (`del(args)`) routes to `adapter.NeoInvokePublic(args)` (mirrors Legacy `IsDelegateInvoke -> IDelegateAdapter.ILInvoke`). Added `ReadNeoDelegateInvokeArgs` + `WriteNeoDelegateInvokeReturn` helpers.
- [x] 4b.4 Delegate-typed `this`/param unwrap in autogen codegen + reflection fallback: the autogen Neo binding for a delegate `Invoke` cast the `this` directly (`(Func<...>)ReadNeoReference`) -- but the object is an `IDelegateAdapter`. Added `CheckCLRTypes(TypeFlags.IsDelegate)` unwrap in `MethodBindingGenerator.cs` (this-read) + `BindingGeneratorExtensions.cs` (param-read) + `CLRMethod.Invoke` (reflection fallback). Patched the 15 checked-in delegate binding files' `Invoke_*_Neo` this-reads to match.

## 5. Adversarial probe suite (TestCases/NeoStep19Test.cs)

- [x] 5.1 `NeoStep19_StaticAction` -- static `Func<int,int>` construct + invoke.
- [x] 5.2 `NeoStep19_StaticFunction` -- `Func<int,int> f = Bar; int r = f(7);` with return.
- [x] 5.3 `NeoStep19_InstanceMethod` -- instance-method delegate (reads `this` state).
- [x] 5.4 `NeoStep19_VirtualMethod` -- virtual-method delegate (ldvirtftn): Derived override dispatched via a Base-typed variable.
- [x] 5.5 `NeoStep19_MulticastCombine` -- `a += Baz; a();` (Call_Redirect -> DelegateCombineNeo).
- [x] 5.6 `NeoStep19_MulticastRemove` -- `a -= Foo;` (DelegateRemoveNeo).
- [x] 5.7 `NeoStep19_ClrCallback` -- `List<int>.ForEach(action)` (the CLR -> IL `InvokeILMethod` callback).
- [x] 5.8 `NeoStep19_RefOutParam` -- IL-defined delegate construct + Invoke callvirt (the IL-delegate-Invoke routing). NOTE: a delegate with ref/out params requires byref-aware arg marshaling in NeoInvoke (deferred -- see Follow-ups); this probe uses a plain int param + return to exercise the IL-delegate construct + Invoke path.
- [x] 5.9 `NeoStep19_ClosureOverThis` -- instance-method delegate closing over an IL instance's `this` (re-invoked after mutating the field).
- [x] 5.10 `NeoStep19_ValueTypeParam` -- `Func<TestVector3,float>` round-trips a registered CLR struct param through `WriteNeoValueType` (R4 risk). The target returns a constant (struct field access via generic `Ldfld` is a separate Step 6 gap, [NEO-IL-VT-INSTANCE-COVERAGE]).

## 6. Verification + regression gate

- [x] 6.1 Full `NeoStep` smoke: **140/140 green** (130 baseline + 10 new `NeoStep19_*`).
- [x] 6.2 Legacy-neutral: plain `Debug` CLI builds clean; `NeoStep` filter = 140 ran, 7 failed -- the 7 are the PRE-EXISTING Legacy failures (NeoStep13 ClrStruct x2, NeoStep14 TC1/TC5/TC8, NeoStep15 TC6, NeoStep6 NeoNaNR8); ALL 10 `NeoStep19_*` pass on Legacy (delegates are engine-agnostic). No new Legacy regression.
- [x] 6.3 Stash-toggle: the 10 probes are NEW; on HEAD (pre-change) `ldftn`/`ldvirtftn`/delegate-newobj all NIE -> the probes could not pass. The construct path is load-bearing.
- [x] 6.4 No test takes >10s (10 NeoStep19 tests run in ~1s; full NeoStep smoke completes promptly).
- [x] 6.5 No host-type addition in this change (no CLI `--no-incremental` host-DLL gotcha trigger).
