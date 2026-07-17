# neo-typed-opcode-reach-executer -- design

Wave-2 child of `neo-overhaul`. Target: the ~6-test cluster failing with
`Not supported opcode {Muli_R4/Addi_R4/Ldfld_I4/Ldfld_U4/Add_I8}` (Neo typed
opcodes) because a Neo-JIT'd IL method (its body carries Neo typed opcodes)
reaches the LEGACY `ExecuteR` (`ILIntepreter.Register.cs:5325`), whose `default`
arm throws -- Legacy ExecuteR does not recognize Neo typed opcodes.

## The route (pinned by a Register.cs:5325 stack-trace dump)

A `StackTrace` dump injected at the ExecuteR `default` arm (temporarily) captured
the FULL .NET stack for two representative cluster tests. BOTH converge on ONE
site:

`ILRuntime.Runtime.Enviorment.InvocationContext.Invoke()` (`InvocationContext.cs`,
old line 458) calls `intp.ExecuteR(method, esp, out unhandledException)`.

- Reflection route (ReflectionTest09): `ReflectionTest10()` runs in ExecuteNeo ->
  `MethodInfo.Invoke` (callvirt.clr) -> autogen `System_Reflection_PropertyInfo_
  Binding.GetValue_3_Neo` -> `PropertyInfo.GetValue` -> `ILRuntimePropertyInfo.
  GetValue` -> `using (var ctx = appdomain.BeginInvoke(getter)) { ctx.PushObject(
  obj); ctx.Invoke(); return ctx.ReadObject(...); }` -> **InvocationContext.Invoke
  -> ExecuteR:5325** (the getter body has `Ldfld_U4`).
- Cross-binding route (InheritanceTest07): the CLR base calls the IL override ->
  autogen Adaptor -> `CrossBindingFunctionInfo.Invoke(instance, arg)` ->
  `using (var ctx = domain.BeginInvoke(method)) { ctx.PushObject(instance); ctx.
  PushParameter(...); ctx.Invoke(); ... }` -> **InvocationContext.Invoke ->
  ExecuteR:5325** (the override body has `Addi_R4`).

Root cause: `InvocationContext.Invoke` hardcoded the Legacy executor. Under Neo
the IL method body is JIT'd to Neo typed opcodes; ExecuteR's switch has no case
for them -> `NotSupportedException("Not supported opcode " + code)` at
`Register.cs:5325`. Neo-specific (Legacy passes these tests: under Legacy the body
has no typed opcodes, so ExecuteR runs it fine).

`ILRuntimeMethodInfo.Invoke` is NOT affected: it calls `appdomain.Invoke` ->
`Run` -> `ExecuteNeo` (already Neo-aware). The gap is exclusively the
InvocationContext callers (PropertyInfo get/set, CrossBindingMethodInfo x11,
DelegateAdapter x11, ILTypeInstance helpers).

## The fix (Neo-gated HYBRID, Legacy-neutral)

`InvocationContext.Invoke` gains an `#if ENABLE_NEO_MODE` branch:
- `CanInvokeNeo()` -- a guard that admits ONLY the "simple" arg shape the Neo
  re-entry path marshals faithfully: instance/static methods (NOT ctors), whose
  pushed arg slots are all primitives or plain references (NO
  `StackObjectReference` = byref, NO `ValueTypeObjectReference` = binder value
  type). The arg slots live in the LAST `paramCnt` StackObjects before `esp`
  (mirrors ExecuteR's `r = LocalVarPointer - ParameterCount`, `r--` for HasThis).
- `InvokeNeo()` -- read those args back as objects via `StackObject.ToObject`,
  re-enter through `intp.Run` (the proven CLR->IL re-entry point used by
  `ILRuntimeMethodInfo.Invoke`: Run builds the Neo frame at StackBase, marshals
  via `DelegateAdapter.WriteNeoCallSlot`, runs ExecuteNeo, returns the boxed
  result with type discrimination), then `PushObject` the result at `ebp` and set
  `esp` so the typed readers (`ReadInteger`/`ReadObject`/..., which dereference
  `esp`) keep working unchanged.
- Else: fall through to the existing Legacy arm (`ExecuteR`/`Execute`). The
  ctor / byref / binder-value-type cases run on Legacy EXACTLY as before this
  change -> no regression.

### Why a hybrid (not a full Neo port)

A first attempt routed ALL InvocationContext invokes through `Run`. It fixed the
4 simple cluster tests but REGRESSED 8 Hotfix tests (+4 net: 69 -> 73). Re-audit
(`$$$INVNEO$$$` arg-dump diagnostic) pinned THREE faithful-marshalling gaps that
`Run` cannot handle:

1. **Ctors** -- `HotfixClass___Extra..ctor(HotfixClass, Int32)`: `method.HasThis`
   is FALSE for a ctor (the instance is param 0). Ctors are invoked by the newobj
   arm's direct `ExecuteNeo(ilCtor, ...)` with a pre-built frame; `Run` is the
   wrong tool (its `CompiledFrame.ParamInfos`/HasThis handling does not match the
   ctor convention) -> NRE at `Run:179` (`WriteNeoCallSlot`).
2. **Byref** (`ref`/`out` params, e.g. `VMethod3(ref int)`): the CrossBinding
   push convention is `PushInteger(value); PushObject(this); PushReference(0);`
   -- the value-storage slot sits BEFORE [this, byref]. Re-marshalling through
   `Run`/objects loses byref semantics; the callee's write-back never reaches the
   storage slot the caller reads via `ReadInteger(0)`. Also misaligns args
   (`Convert.ToInt64(ILTypeInstance)` -> InvalidCastException).
3. **Binder value-type args** (`ValueTypeObjectReference`): `PushValueType` writes
   raw VT bytes; the object round-trip is lossy.

The `CanInvokeNeo()` guard rejects exactly these three shapes; they fall back to
ExecuteR (pre-existing behavior). This makes the change regression-free while
flipping the 4 simple typed-opcode tests green.

## Verify (truth = full-smoke count, REAL run; same TestCases.dll + patch)

- Baseline (pristine HEAD, no fix): **69 failed**, 6 with "Not supported opcode"
  (InheritanceTest07/16, JsonTest1, ReflectionTest09/11/23).
- After fix: **65 failed** (69 -> 65). "Not supported opcode" cluster: **0
  remaining**. FIXED: JsonTest1, ReflectionTest09, ReflectionTest11,
  ReflectionTest23. **ZERO regressions** (failure-set diff baseline vs post: no
  test moved pass->fail; the 8 Hotfix tests the v1 attempt regressed are back to
  passing via the ExecuteR fallback).
- NeoStep smoke: **394/0** (unchanged vs the documented baseline; no regression).
- Stash-toggle (causality): stash `InvocationContext.cs` -> ReflectionTest09
  FAILS (`Not supported opcode Ldfld_U4`); pop -> PASS. Airtight.
- Legacy-neutral: the entire change is `#if ENABLE_NEO_MODE`-gated; Legacy
  compiles none of it (the Legacy `Execute`/`ExecuteR` arms are byte-identical).

## Out of scope (follow-ups)

The remaining 2 cluster tests (InheritanceTest07, InheritanceTest16) still fail
-- they were failing on HEAD and are NOT regressions. The route IS fixed: their
`AbMethod2` bodies now execute in ExecuteNeo (no more "Not supported opcode").
The residual is a DOWNSTREAM Neo float bug, NOT an InvocationContext issue:

- **InheritanceTest07 / InheritanceTest16 -- the `arg1 + 1.2f` float bug.**
  `TestCls5.AbMethod2(int arg1) { return arg1 + 1.2f; }` (and the
  InheritanceTest16SubCls `Muli_R4` variant) now RUN in Neo but produce a garbage
  denormal (`3E-45 != 12.1f`). This is a DOWNSTREAM Neo float-arithmetic issue in
  the `conv.r4 arg1; ldc.r4 1.2; addi.r4` (or `muli.r4`) path, NOT the route and
  NOT InvocationContext arg-marshalling: (a) `Conv_R4`/`Conv_R8` ARE seeded in
  `TypeSpecializeNeoOpcodes` (JITCompiler.cs:1156-1165) and the `ReadConvR4`
  runtime helper (Neo.cs:7387) does a correct numeric int->float cast; (b) my
  InvokeNeo marshals primitive args correctly -- `ReflectionTest11`'s
  `get_Item(int, long)` (int+long arithmetic, returns long) PASSES with the right
  value via the same path. The residual is specific to the int->float conv +
  float-literal addi/muli shape and needs its own re-audit (likely the
  `addi.r4`/`muli.r4` immediate-operand runtime arm or a JIT tag/stamp issue on
  this exact shape) -- a sibling JIT child, separate from this route fix.

The three hybrid-deferred shapes (the reason `CanInvokeNeo` rejects them) are
LATENT (the ExecuteR fallback handles them at pre-existing behavior; none
currently block a cluster test):

- **neo-invocationctx-byref**: marshal `StackObjectReference` (byref) args with
  Neo byref semantics + post-call write-back into the caller's storage slot (the
  `ReadInteger(0)` readback contract). Note: `InheritanceTest07`'s byref
  `VMethod3(ref int)` now runs on the ExecuteR fallback (not blocking); the test's
  current failure is the float bug above.
- **neo-invocationctx-ctor**: route ctor invokes through the newobj-style direct
  `ExecuteNeo(ilCtor, ...)` frame build (NOT `Run`), or teach `Run` the ctor
  HasThis=false convention. The Hotfix `HotfixClass___Extra..ctor` path currently
  uses the ExecuteR fallback (passes unless its body has typed opcodes).
- **neo-invocationctx-valuetype**: faithful binder value-type (`ValueTypeObject
  Reference`) marshalling.

## Files

- `ILRuntime/Runtime/Enviorment/InvocationContext.cs` (+~80, all inside
  `#if ENABLE_NEO_MODE`): the `Invoke` Neo branch, `CanInvokeNeo`, `InvokeNeo`.
  No runtime/JIT/object-model/binding change elsewhere.
