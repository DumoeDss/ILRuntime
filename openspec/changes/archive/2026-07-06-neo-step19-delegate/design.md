## Context

Neo mode (Steps 1-18 shipped) interprets CIL JIT-compiled to `OpCodeR` over a
compact `byte* frameBase` + `AutoList mStack` ref-region frame. Delegates have
NO support in `ExecuteNeo`:

- `ldftn` / `ldvirtftn` are absent from the giant switch (they hit the catch-all
  `NotImplementedException`).
- The `Newobj` arm throws `NotImplementedException("Neo Newobj delegate is not
  implemented")` for an IL delegate type.
- `DelegateAdapter.InvokeILMethod` (CLR -> IL callback) is `StackObject`-wired
  (Legacy). It builds a `StackObject*` evaluation stack, pushes `this` + params,
  calls `ExecuteR`, reads the return. Under Neo this is the wrong calling
  convention entirely.

The machinery to land delegates already ships:

- **Step 8 Call convention** (`ILIntepreter.Neo.cs:1660-1690` Call arm): the
  caller writes args into a callee param region (`CopyNeoCallArguments`), calls
  `InvokeNeoCallTarget` -> `ExecuteNeo`, the callee's `Ret` writes the return
  into the caller's `retDst`. The param-region layout is described by
  `NeoCallParamMap` (PrimitiveSrc/Dst/Size, RefSrc/Dst/Count) stamped by the
  optimizer (`Optimizer.Neo.cs:AllocateNeoCallParamSlot`).
- **Step 9 `CLRRedirectionDelegateNeo`** (`AppDomain.cs:32`): the autogen CLR
  redirect delegate type for the Neo frame. IL -> CLR calls route through
  `InvokeNeoClrMethod` -> `redirectNeo(this, targetBase, mStack, ...)`.
- **Step 10 VTable** (`ILType.neoVTable` + `GetVirtualMethod`): `ldvirtftn`
  resolves the override exactly as `Callvirt_IL` does.
- **Step 13b/area4 `ReadNeoValueType`/`WriteNeoValueType`**
  (`ILIntepreter.Neo.cs:209/225`): value-type param/return byte <-> boxed-object
  bridges, reused by the delegate param/return path.
- **`DelegateManager.FindDelegateAdapter`** (`DelegateManager.cs:232/273`):
  builds + caches a `DelegateAdapter` for `(ILTypeInstance, ILMethod)` (instance
  method) or `(null, ILMethod)` (static method). Engine-agnostic.
- **The Neo entry shim** (`ILIntepreter.cs:87-141` `Run`): builds a minimal
  `byte*` frame for a no-arg static method call and invokes `ExecuteNeo`. The
  `InvokeILMethod` Neo path generalizes this shape (instance `this` + params +
  return).

The Legacy reference is authoritative for SEMANTICS (the dispatch / adapter /
multicast logic), NOT for the frame model. Key Legacy arms:

- `ILIntepreter.Register.cs:4524-4553` -- `ldftn` (`domain.GetMethod`,
  `AssignToRegister`) and `ldvirtftn` (reads `this`, `Type.GetVirtualMethod`).
- `ILIntepreter.Register.cs:3359-3408` -- delegate `Newobj` (reads `this` =
  Register2, IMethod = Register3, builds adapter via DelegateManager, pushes it).
- `DelegateAdapter.cs:925-1022` -- `BeginInvoke` / `ILInvoke` / `ILInvokeSub` /
  `ClearStack` (the `StackObject` CLR -> IL bridge + the `next`-chain multicast).

## Goals / Non-Goals

**Goals:**

- Implement `ldftn` / `ldvirtftn` in `ExecuteNeo` so a "function pointer"
  (`IMethod`, + bound `this` for ldvirtftn) is materialized into a Neo ref slot.
- Implement the delegate `Newobj` arm so `Action a = Foo;`, `Func<...> f = M;`,
  instance-method delegates, and virtual-method delegates construct a working
  `DelegateAdapter`.
- Adapt `DelegateAdapter.InvokeILMethod` to the Neo calling convention so a
  delegate invocation routes into `ExecuteNeo` (the CLR -> IL callback path:
  `list.ForEach(ilAction)`, event invocation, etc.).
- Keep multicast (`+=` / `-=` / `next`-chain) working -- it is engine-agnostic
  once the single-invoke path is correct.
- Legacy byte-identical: every change to `DelegateAdapter.cs` /
  `ILIntepreter.cs` is `#if ENABLE_NEO_MODE`-gated; `ExecuteR` is the reference
  and is NOT modified.

**Non-Goals:**

- `Delegate.DynamicInvoke` (runtime-late-bound invocation). NIE-tagged, follow-up.
- Open-instance delegates constructed from static methods (rare; follow-up).
- Generic delegate types beyond the `Action<>`/`Func<>` arities
  `DelegateManager` already registers (`<= 4` params + the 5-param
  `NET_4_6 || NET_STANDARD_2_0` variant). Unregistered arities throw the
  `DelegateAdapter.ThrowAdapterNotFound` helper message (existing behavior).
- Cross-binding-adaptor field/method reads off a caught IL exception
  (`[NEO-IL-EX-FIELDACCESS]` follow-up, independent mechanisms).
- Step 20 async/await (which CONSUMES delegates for continuations). Delegates
  are a prerequisite, not the implementation, of async.

## Decisions

### D1: IMethod representation in the Neo frame = a ref slot (CLR object)

An `IMethod` (ILMethod / CLRMethod) is a CLR heap object, so under the Neo
object model it lives in `mStack` with a 4-byte index in the frame byte region
-- exactly like any reference-type local/temp (Step 7). `ldftn` and `ldvirtftn`
both produce an `IMethod`, so:

- `ldftn`: `m = AppDomain.GetMethod(ip->Operand2)`; store `m` into the dest ref
  slot, write the index to the dest byte offset.
- `ldvirtftn`: read `this` from `Register2`'s mStack slot, resolve
  `m = ((ILTypeInstance)this).Type.GetVirtualMethod(targetIlMethod)`, store the
  resolved `m` into the dest ref slot + index write.

This mirrors Legacy `AssignToRegister(ref info, ip->Register1, m)` semantics,
just in the byte/ref-slot model. The JIT already lowers `ldftn`/`ldvirtftn`
with the right operands (`InitializeFunctionParam` resolves the token into
`Operand2`; `Register1` = dest, `Register2` = `this` source for ldvirtftn --
`JITCompiler.cs:2377-2396`). No JIT change.

**Alternative considered:** carry the `IMethod` as a raw pointer / token in the
byte region (treat it as a non-managed value). REJECTED -- the adapter ctor
(Newobj) reads it back as a managed object, and the multicast `next`-chain /
`GetConvertor` path treats it as a managed ref. A ref slot is the only
representation consistent with the rest of the object model.

### D2: Delegate `Newobj` reads `this` (Register2) + IMethod (Register3) from ref slots, builds the adapter via DelegateManager

Mirror the Legacy arm (`ILIntepreter.Register.cs:3359-3408`) precisely:

1. `targetMethod = AppDomain.GetMethod(ip->Operand2)` (the delegate `.ctor`).
2. `ilNewobjType = targetMethod.DeclearingType as ILType`; gate `IsDelegate`.
3. Read `this`: the `this` arg is at `Register2` (the JIT stamps the ctor args
   into Register2/Register3/Register4 per `:1859-1875`). For a delegate ctor
   the args are `(object target, IntPtr fnptr)` -- but ILRuntime's convention
   (Legacy) is that `Register2` = the bound instance (mStack slot) and
   `Register3` = the `IMethod` (mStack slot). Read both as ref-slot indices.
4. `ins = (this == null marker) ? null : mStack[thisIdx]`.
5. `mi = (IMethod)mStack[methodIdx]`.
6. Build the adapter:
   - instance method (`ins != null`): cache on the `ILTypeInstance`
     (`GetDelegateAdapter`/`SetDelegateAdapter`), else
     `DelegateManager.FindDelegateAdapter((ILTypeInstance)ins, ilMethod, invokeMethod)`.
   - static method (`ins == null`): cache on the `ILMethod.DelegateAdapter`,
     else `DelegateManager.FindDelegateAdapter(null, ilMethod, invokeMethod)`.
7. Store the adapter into the dest ref slot (`newobjDstIdx`) + write the index
   to the dest byte offset (same shape as the IL ref-type newobj
   `:1813-1823`).

**Note on the call-args copy:** the delegate `.ctor` takes NO IL-side params
that need `CopyNeoCallArguments` -- the `this` + `IMethod` are read directly
from the caller's registers (the JIT does not push them through the param
region for a delegate ctor; verify via JIT dump at apply, the area4 discipline).
Legacy does the same (it reads `r + ip->Register2` / `Register3` directly, not
via the param-push loop). If the dump shows the JIT DID route them through the
param region, fall back to reading from `targetBase` -- the design does not
force either; the dump decides (mirrors the VT-THIS-ADDR / area4 dump-gated
discipline).

### D3: `InvokeILMethod` Neo convention = build a Neo frame, write CLR args into the param region, ExecuteNeo, read return

This is the inverse of Step 8/9 IL -> CLR. The Neo `InvokeILMethod`
(`DelegateAdapter.cs`, under `#if ENABLE_NEO_MODE`) replaces the
`StackObject`-based `ILInvokeSub` + `ClearStack` for the single-invoke path:

1. **Frame build** (mirrors the `Run` Neo entry shim
   `ILIntepreter.cs:97-111`): allocate `byte* frameBase` from the engine's
   stack (`stack.StackBase` + `esp`), sized to `method.CompiledFrame.TotalStructSize`;
   allocate the frame ref region (`mStack.Count += TotalRefSize`); allocate the
   return slot (`retDst` + `retRefBase`).
2. **Write `this` + params** into the callee param region. For an instance
   method, slot 0 = the bound `instance` (write into `frameRefBase + 0`, set
   `frameBase + thisByteOff = 0`). For each CLR arg, write by type:
   - primitive: `*(T*)(frameBase + paramOff) = (T)arg` (per the param's
     `Size`/type).
   - reference (object/string/ILTypeInstance): `mStack[frameRefBase + refOff] = arg`;
     `*(int*)(frameBase + paramOff) = refOff`.
   - CLR value type: `WriteNeoValueType(arg, frameBase + paramOff, sz)`
     (the area4 helper, inverse of the IL -> CLR read).
   The byte/ref offsets come from the callee `CompiledFrame`'s param layout
     (`localInfos[0..paramCnt]`), exactly as `AllocateNeoCallParamSlot` sizes
     them -- so the layout the optimizer produces for IL -> IL Call is the same
     layout the adapter writes here.
3. **`ExecuteNeo(method, frameBase, retDst, retRefBase, out unhandled)`**.
4. **Read the return**: if non-void, read `retDst` by return type (primitive
   `*(T*)retDst`; reference `mStack[retRefBase]`; value type
   `ReadNeoValueType(retType, retDst, ref off, sz)` -> boxed object).
5. **Tear down**: `mStack.Count = frameRefBase` (O(1) truncate); restore `esp`.

**Multicast** (the `next`-chain): UNCHANGED. The `next` field, `Combine`, and
`Remove` are engine-agnostic (they manipulate `IDelegateAdapter` objects, not
the frame). The `next != null` branch in `ILInvokeSub` walks the chain; for the
Neo path, `NeoInvokeILMethod` walks the same chain, discarding intermediate
returns and returning the last (mirrors Legacy `ILInvokeSub:965-974`).

**Alternative considered:** route the CLR -> IL callback through the EXISTING
`Run` / `appdomain.Invoke` re-entry. REJECTED for two reasons: (a) `Run`'s Neo
shim handles only no-arg static methods (`ILIntepreter.cs:94-120`, the Step 6
limitation); (b) it allocates a fresh frame from `StackBase` each call (no
`this`, no param region write), which cannot carry the bound instance. A
dedicated `NeoInvokeILMethod` in `DelegateAdapter` is the minimal generalization.
The shim's frame-build SHAPE is reused (the design does not invent a new frame
layout).

### D4: Reuse DelegateManager / the `next`-chain / caching verbatim

`DelegateManager.FindDelegateAdapter`, the per-`ILTypeInstance` adapter cache
(`GetDelegateAdapter`/`SetDelegateAdapter`), the per-static-`ILMethod` cache
(`ilMethod.DelegateAdapter`), and the `next`-chain multicast are all
engine-agnostic. They are NOT modified. Only the single-invoke path
(`InvokeILMethod` -> `ILInvokeSub` -> `ExecuteR`) gains a Neo branch
(`-> ExecuteNeo`).

### D5: `CLRRedirectionDelegateNeo` (Step 9) is the IL -> CLR direction; Step 19 is the inverse

Step 9 laid `CLRRedirectionDelegateNeo` for IL -> CLR autogen redirects (the
`MethodBindingGenerator` `*Neo` variants). Step 19 is the INVERSE direction
(CLR -> IL, the delegate callback) and lives in `DelegateAdapter`, not in the
autogen codegen. The two are complementary; no Step 9 codegen change for Step
19's scope. (A CLR delegate type whose `Invoke` is itself autogen-redirected is
out of scope -- that is the cross-binding-adaptor follow-up.)

## Risks / Trade-offs

- **[The `InvokeILMethod` Neo frame build is the one untested corner]** ->
  Mitigation: probe BEFORE finalizing the frame shape. At apply, JIT-dump a
  delegate callback target (`list.ForEach(action)` -> the IL action's frame)
  to confirm (a) the param-region layout the adapter writes matches what
  `ExecuteNeo` reads, and (b) the `this` slot for an instance-method delegate
  is at `frameRefBase + 0` (not a byte offset). This is the area4 / opt-harden-2
  dump-gated discipline. STOP and re-design if the dump disagrees with D3.
- **[Delegate `.ctor` arg routing: registers vs param region]** -> Mitigation:
  the JIT may route the `.ctor`'s `(target, fnptr)` args through the param
  region (`CopyNeoCallArguments`) OR leave them in `Register2`/`Register3`.
  Legacy reads them from registers directly. The Neo `Newobj` arm must match
  whatever the JIT produced. JIT-dump the delegate `.ctor` call site at apply
  to confirm which; read accordingly. (Mirrors the VT-THIS-ADDR copy-back
  ambiguity, resolved by dump.)
- **[Multicast return-value semantics]** -> Legacy discards intermediate
  returns and returns the LAST delegate's result
  (`DelegateAdapter.cs:965-974`). The Neo path MUST match. Low risk (the
  `next`-chain walk is the same logic), but probe `+=` over delegates with
  different return values to confirm.
- **[Value-type param / return through `InvokeILMethod`]** -> A delegate with a
  CLR struct param or return (e.g. `Func<Vector3, int>`) must round-trip
  through `WriteNeoValueType` (write) + `ReadNeoValueType` (read). The area4
  helpers are the proven path, but a delegate callback is a NEW caller. Probe
  it explicitly (TC8 ref/out + a struct param probe).
- **[CLR -> IL re-entry thread/state]** -> `ExecuteNeo` mutates the engine's
  `esp` / `mStack.Count`. A delegate invoked from a CLR redirect delegate body
  (which itself runs inside `ExecuteNeo`) is a nested re-entry. The frame-build
  must push onto the SAME engine stack (not a fresh one) so `esp` restores
  correctly. This mirrors how Legacy `ILInvoke` nests inside `ExecuteR`; the
  risk is low (the shim already does this for `Run`), but probe a delegate
  invoked from inside an IL method that itself was called via a delegate (the
  TC7 `List.ForEach` inside an IL method covers the common shape).
- **[Legacy byte-identical regression]** -> `DelegateAdapter.cs` /
  `ILIntepreter.cs` changes are `#if ENABLE_NEO_MODE`-gated. The Legacy
  `Run`/`ExecuteR`/`ILInvokeSub` paths are untouched. Gate: plain `Debug` +
  `useRegister=true` Legacy 518/519 baseline holds after the change.

## Open Questions

- **OQ1 (resolve at apply via JIT dump):** does the JIT route the delegate
  `.ctor`'s `(target, fnptr)` args through the param region
  (`CopyNeoCallArguments` writes them to `targetBase`) or leave them in
  `Register2`/`Register3` for the runtime to read directly? Determines whether
  the `Newobj` delegate arm reads from `targetBase` or from
  `frameBase + Register2/Register3 byte offsets`. (Legacy reads registers.)
- **OQ2 (resolve at apply):** for `ldvirtftn`, is the `this` source slot
  (`Register2`) a ref slot (mStack index) for ALL cases, or can it be a
  boxed-struct / value-type `this`? For Step 19 scope (delegate over an IL
  reference-type instance method, or a static method) it is always a ref slot;
  a delegate over a CLR struct instance method is the area4 byref-`this` shape
  and may need the `PrimitiveByRefSrc` deref -- probe if a struct-delegate case
  is added, otherwise defer (out of scope).
- **OQ3 (resolve at apply):** the `InvokeILMethod` Neo frame -- can it reuse
  the engine's `stack.StackBase` + `esp` directly (as `Run` does), or does it
  need its own allocation to avoid clobbering the in-flight IL frame? `Run`
  reuses `StackBase` safely because it is the OUTERMOST entry (no in-flight IL
  frame below it). A delegate callback from INSIDE `ExecuteNeo` has an in-flight
  frame, so the adapter MUST push onto `esp` (advance past the current frame),
  not reset to `StackBase`. Confirm the engine stack has enough headroom; if
  not, this surfaces a stack-overflow class of bug under deep delegate nesting
  (probe TC7 nested).

## Apply findings (2026-07-05) -- dump-confirmed frame build + routing

### The common `Action<>`/`Func<>` delegate is a CLRType, NOT an ILType (D2 DEVIATION)

The design's D2 assumed the delegate newobj declaring type is an ILType (the
`ilNewobjType.IsDelegate` branch). DUMP-DISPROVEN: `Func<int,int>` /
`Action<int>` etc. resolve to CLRType (CLR delegate types bound via the test
helper). The common delegate newobj routes through the CLR newobj branch
(`if (targetMethod.DeclearingType is CLRType)`), NOT the IL-delegate branch.
The CLR-delegate sub-branch reads `(target, IMethod)` and builds the adapter
via `DelegateManager.FindDelegateAdapter(CLRType, ILTypeInstance, ILMethod)`.
The IL-delegate branch is ALSO implemented (rare IL-defined delegate types)
via the IL `FindDelegateAdapter` overload. The design's literal D2 site was
wrong; the actual edit is in the CLR newobj branch (mirrors Legacy
`ILIntepreter.Register.cs:3539-3561`, NOT `:3359-3408`).

### OQ1 RESOLVED: ctor args route through CopyNeoCallArguments -> targetBase

DUMP (`NeoStep19_StaticAction`): the delegate ctor's NeoCallParamMap has 2
entries with sizes `[4, 8]`. map[0] = target (object, 4-byte mStack index),
map[1] = fnptr (IntPtr, 8-byte slot whose first 4 bytes hold the IMethod's
mStack index). The map does NOT include a `this` slot. The runtime reads
`*(int*)(targetBase + map.PrimitiveDst[0])` for the target index and
`*(int*)(targetBase + map.PrimitiveDst[1])` for the IMethod index. (Legacy
reads registers directly; Neo reads targetBase because the optimizer builds
the map for the CLR delegate ctor.)

### Risk 3 DISSOLVED: each delegate invoke runs on a FRESH interpreter

The design's OQ3 / Risk 3 premise (a delegate callback from inside ExecuteNeo
re-enters the SAME interpreter and must push past the in-flight frame) is
WRONG. Legacy `BeginInvoke` -> `appdomain.RequestILIntepreter()` returns a
FRESH (or pooled-free) interpreter for each invoke. So a delegate invoked
from inside an IL method (e.g. `List.ForEach(action)` called from IL) runs on
its OWN engine stack -- there is no in-flight frame to clobber. The Neo
`NeoInvokeSub` mirrors this: request a fresh interpreter, build the frame at
its `StackBase`, restore `mStack.Count` on teardown. NO `esp`-past-in-flight-
frame logic needed.

### Additional Step 19 edit sites (beyond the design's 3)

The design's 3 changes were insufficient -- C# delegate idioms compile to
opcodes / CLR redirects that Neo did not implement:

1. **`Call_Redirect` Neo arm + optimizer case**: C# `a += b` / `a -= b`
   compiles to `System.Delegate.Combine` / `Remove` (a `Call_Redirect`).
   Added the runtime arm (`ILIntepreter.Neo.cs`, routes through
   `InvokeNeoClrMethod`) + the optimizer Call-case entry (`Optimizer.Neo.cs`)
   so the NeoCallParamMap is built.
2. **Neo `DelegateCombineNeo` / `DelegateRemoveNeo` redirects** (`CLRRedirections.cs`
   + registration in `AppDomain.cs`): the Legacy StackObject `DelegateCombine`/
   `DelegateRemove` redirects are not Neo-aware. Added Neo-signature variants
   that read the two Delegate params (IDelegateAdapter unwrap) in SOURCE/
   declaration order (param 0 = dele1/source -- NOT stack order), apply the
   multicast, and write the result.
3. **IL-delegate-Invoke callvirt routing** (`Callvirt_IL` arm): an IL-defined
   delegate's `del(args)` compiles to `callvirt.il Invoke`. Added a
   `targetMethod.IsDelegateInvoke` branch that routes to
   `adapter.NeoInvokePublic(args)` (mirrors Legacy `IsDelegateInvoke ->
   IDelegateAdapter.ILInvoke`), with `ReadNeoDelegateInvokeArgs` /
   `WriteNeoDelegateInvokeReturn` helpers.
4. **Delegate-typed `this`/param unwrap (autogen codegen + reflection
   fallback)**: the autogen Neo binding for `Func.Invoke` cast the `this`
   directly to `Func<...>` -- but the object is an `IDelegateAdapter`. Added
   `CheckCLRTypes(TypeFlags.IsDelegate)` unwrap in `MethodBindingGenerator.cs`
   (this-read for delegate types), `BindingGeneratorExtensions.cs`
   (`AppendArgumentCodeNeo` param-read for delegate types), and `CLRMethod.Invoke`
   (reflection fallback). Patched the 15 checked-in delegate binding files'
   `Invoke_*_Neo` this-reads to match (the codegen fix only takes effect on
   regeneration).

### Defer-via-follow-up (out of Step 19 scope, pre-existing gaps surfaced)

- **Ref/out params through an IL-delegate-Invoke**: `NeoInvokeSub`'s `object[]`
  arg model loses byref semantics (an `out int` would need to flow as a raw
  8-byte Ref Slot and the callee's write propagated back). TC8 was scoped to a
  plain-param IL-delegate Invoke (the routing is the load-bearing assertion);
  ref/out-through-delegate is a follow-up (byref-aware arg marshaling).
- **`Ldfld` (generic) on a CLR struct param** (TC10): struct field access via
  non-inline `Ldfld` is a Step 6 gap (`[NEO-IL-VT-INSTANCE-COVERAGE]`). TC10's
  target returns a constant to isolate the test to the struct-param marshaling
  round-trip (the R4 risk).

## Review-loop round 1 (F1 fix)

**Finding F1 (Major, resource leak).** `NeoInvokeSub` called
`appdomain.RequestILIntepreter()` per delegate invocation (mirroring Legacy
`BeginInvoke`) but never called `appdomain.FreeILIntepreter(intp)`. Legacy
returns the interpreter via `using (var ctx = BeginInvoke())` ->
`InvocationContext.Dispose` -> `domain.FreeILIntepreter`
(`InvocationContext.cs:594-603`). Without the free, every delegate callback
allocated a NEW `ILIntepreter`; the free-pool stayed empty so no reuse ever
occurred -- a steady memory + GC-pressure leak proportional to the callback
count on the delegate hot path (the whole point of Step 19). Correctness was
unaffected (each callback runs synchronously on its own engine stack), which is
why the smoke stayed green; production delegate-heavy IL code would leak.

**Fix.** Wrapped the `NeoInvokeSub` body (after `RequestILIntepreter()`) in
`try { ... } finally { appdomain.FreeILIntepreter(intp); }`, mirroring the
Legacy `using (BeginInvoke())` lifecycle exactly. The free runs AFTER
`ExecuteNeo` returns AND the return value is read (the `result` read + the
`next`-chain recursion both complete inside the `try` before `finally` fires).
`FreeILIntepreter` clears `Stack.ManagedStack` / `Stack.Frames`, so the
explicit `mStack.RemoveRange(mStackBase, ...)` teardown is now redundant but
kept (harmless, documents intent). The `next`-chain recursion
(`result = n.NeoInvokeSub(args)`) does its own balanced request/free pair, so
multicast nesting is safe. The `if (unhandled) throw` path throws out of the
`try`, so the `finally` fires on the exception-escape path too -- the
interpreter is freed and the exception propagates to the CLR caller.

**Verification (instrumented probe, since removed).** Added temporary counters
to `RequestILIntepreter` (pool-dequeue hits vs new-alloc) + a public pool-count
accessor, ran three probes:

- **F1-Loop** (`list.ForEach(action)` over 1000 elements): pre-fix would be
  ~1000 allocs / 0 hits; **post-fix: 2 allocs, 999 pool hits** -- the pool
  reclaims and reuses. Definitive proof F1 is fixed.
- **F1-Nested2** (delegate whose body calls `List.ForEach` on another delegate
  -- 2-level nesting): 1 alloc, 12 hits, pool stays bounded (net +1) -- the
  interpreter is freed at EACH nesting level, no per-level leak.
- **F1-Exception** (delegate whose IL target throws, caught by the caller):
  0 allocs, 2 hits, Pass -- the `finally` fires on the throw path and the
  exception propagates correctly.

All instrumentation (the `NeoF1Probe` counters/accessor in `AppDomain.cs`, the
probe output in `Program.cs`, and the three temp test methods + helper classes
in `NeoStep19Test.cs`) was removed before completion; the only shipped change
is the `try`/`finally` + `FreeILIntepreter` in `DelegateAdapter.NeoInvokeSub`.

Smoke after the fix: NeoStep19 **10/10**, full NeoStep **140/140**, Legacy
(plain `Debug`) NeoStep19 **13/13** (10 original + 3 probes while
instrumentation was live), Legacy CLI builds clean. Happy-path results are
byte-identical (only the lifecycle changed).

**F2 (Minor, pre-existing edge):** `WriteNeoCallSlot`'s CLR-struct-with-ref-field
param discriminator (`RefCount > 0 && Size == 4`) is a pre-existing gap (same
class as opt-harden-2 / area4 deferrals). No Step 19 probe exercises it.
Accepted-known; left as-is (consistent with prior deferrals).

**F3 (Trivial):** restored the trailing space on the Legacy line
`ctx.SetInvoked(esp); ` in the 0-arg `FunctionDelegateAdapter.InvokeILMethod`
to eliminate the cosmetic whitespace churn in the byte-identical Legacy region.


