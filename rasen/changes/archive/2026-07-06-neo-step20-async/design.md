## Context

Neo mode (Steps 1-19 shipped) interprets CIL JIT-compiled to `OpCodeR` over a
compact `byte* frameBase` + `AutoList mStack` ref-region frame. **No `async`
method runs on Neo.** This design covers the SYNC-completing slice of Step 20
(the suspend/resume slice is deferred — see §Scoping).

**How C# lowers `async`.** The compiler emits a struct state machine
`<Bar>d__0 : IAsyncStateMachine` + an `Async*MethodBuilder<T>`. The async
method body is rewritten into the state machine's `MoveNext()`. The original
method `Bar()` becomes a stub equivalent to:

```
SM sm = default;
sm.<>t__builder = AsyncTaskMethodBuilder<T>.Create();
sm.<>1__state = -1;
sm.<>t__builder.Start(ref sm);   // synchronously runs MoveNext once
return sm.<>t__builder.Task;     // completed task (sync) or hot task (async)
```

**Current Neo state (code-grounded):** the autogen builder redirects in
`ILRuntimeTestBase/AutoGenerate/System_Runtime_CompilerServices_AsyncTaskMethodBuilder_*`
register `*Neo` variants, but they are **non-functional stubs**:
- `Create_0_Neo` returns a real builder but the comment says
  `// TODO: CLR value type return in reflection fallback: Step 13`.
- `Start_1_Neo` reads the state machine as a CLR
  `IAsyncStateMachineAdaptor` (a `CrossBindingAdaptorType`), then calls
  `instance_of_this_method.Start<...>(ref @stateMachine)` on a **`default`**
  builder (the builder field is never read from the frame) — so it goes
  through the CLR interface, forcing adaptor boxing, exactly what
  `object-model-neo-design.md` §26 forbids.
- `get_Task_2_Neo`, `SetResult_4_Neo`, `SetException_3_Neo` all operate on a
  `default` builder (`// TODO: ValueType instance in Neo`) — they never touch
  the in-frame state machine's builder field.

So under Neo, an `async` method either NREs (the `default` builder has no
state), infinite-loops (a malformed state machine), or silently returns a
default/uncompleted task. None of these is correct.

**Shipped machinery this design reuses (no re-implementation):**
- **Step 8 Call convention** (`ILIntepreter.Neo.cs` Call arm,
  `CopyNeoCallArguments`, `InvokeNeoCallTarget`): caller writes args into a
  callee param region described by `NeoCallParamMap`; `Ret` writes the return
  into the caller's `retDst`.
- **Step 12 in-frame IL value types + `_Inline` field access**: the state
  machine `sm` is an IL value-type LOCAL = flat bytes (`slot.Offset..+size`) +
  ref slots (`mStack[frameRefBase+slot.RefOffset..]`). Its fields — including
  `<>t__builder`, `<>1__state`, the `<>u__1` awaiter field, user locals — are
  accessed via the existing `_Inline` field ops.
- **Step 13 Box / `CopyFrameToIL`** family: `new ILTypeInstance(type)` with
  `byte[] Primitives` + `AutoList ManagedObjects` is shape-identical to an
  in-frame IL VT; the copy is byte-copy + ref-copy. The frame-to-heap hoist
  (deferred slice's primitive, but unit-shipped here) is a Box-without-CLR-
  instance.
- **Step 17 Ref Slot** (`(-1, frameByteOff)` frame-native / `(objIdx, off)`
  mStack): `Start(ref sm)` passes the state machine by ref = an 8-byte Ref
  Slot.
- **Step 19 `NeoInvokeSub`** (`DelegateAdapter.cs:1006`): the inverse frame
  build — at a fresh pooled interpreter's `StackBase`, zero locals, reserve
  ref region, write `this`+params via `WriteNeoCallSlot`, `ExecuteNeo`, read
  return, `FreeILIntepreter` in `finally`. The async `MoveNext` resumption
  (deferred) reuses this exact shape; the sync `Start` redirect runs
  `MoveNext` IN-PLACE on the caller's frame (no fresh interpreter — the state
  machine is already on the frame).
- **`CLRRedirectionDelegateNeo`** (Step 9, `AppDomain.cs`): the autogen CLR
  redirect delegate type for the Neo frame. The custom builder redirects use
  this signature.
- **`ILTypeInstance(initializeCLRInstance: false)`** (Step 13/26): constructs
  an ILTypeInstance without invoking `FirstCLRBaseType.CreateCLRInstance` → no
  CrossBindingAdapter. The hoist target.

**Legacy is the REFERENCE for semantics, NOT the frame model.** Legacy async
(`ILIntepreter.Register.cs` + the autogen StackObject builder redirects) uses
`WriteBackInstance` + the `IAsyncStateMachineAdaptor` CrossBindingAdaptor —
the exact boxing model the design §26 rejects. Legacy semantics (state
machine states, awaiter `IsCompleted` short-circuit, `SetResult`/`SetException`
ordering, task faulting) are the contract; the Neo mechanism is wholly
different (in-frame VT, no adaptor, custom redirects). Legacy is NOT modified.

## Goals / Non-Goals

**Goals (sync scope):**
- Replace the autogen Neo builder stubs with custom Neo redirects that drive
  the state machine's `MoveNext` through the Neo call machinery WITHOUT the
  `IAsyncStateMachine` CLR interface / CrossBindingAdaptor.
- A sync-completing async method (`Task<T>`, `Task`, `ValueTask<T>`,
  `ValueTask`, `async void`) executes correctly: `Start` runs `MoveNext`,
  every `await` observes `awaiter.IsCompleted == true`, `SetResult`/`SetException`
  stash on the frame, the `Task` getter returns a completed/faulted task.
- Ship the frame-to-heap hoist helper as a standalone, unit-probed primitive
  (the load-bearing piece the suspend slice reuses).
- Ship the `ILAsyncContext<T>` structural skeleton (sync-shortcut paths live;
  resumption tagged NIE) so the `Task` getter has a concrete bridge.
- Adversarial probes covering: sync Task<T>/Task/ValueTask<T>/async-void,
  multiple awaits, value-returning, exception propagation, nested async, and
  the hoist helper.
- Legacy byte-identical: every runtime/codegen edit is `#if ENABLE_NEO_MODE`;
  `ExecuteR` is the reference.

**Non-Goals (deferred to `neo-step20-async-suspend`):**
- The truly-async suspend/resume path: a real `AwaitUnsafeOnCompleted` /
  `AwaitOnCompleted` (frame→heap hoist + `ILAsyncContext` continuation
  registration), `ILAsyncContext<T>.MoveNext()` resumption (restore the hoisted
  state machine to a fresh interpreter's frame, jump to the await state,
  `ExecuteNeo`), the awaited task's `UnsafeOnCompleted(context.MoveNext)` callback.
- A real zero-alloc `ValueTask<T>` path (the `ValueTask`-bearing builder
  redirect beyond registration). The sync path returns a `Task`-backed
  completed result; a full `ValueTask`-backed path depends on the suspend
  machinery for the incomplete case.
- `IAsyncStateMachine.MoveNext` / `SetStateMachine` on the CLR interface (never
  used — the whole point is to bypass it).
- Custom async method builders (`AsyncTaskMethodBuilder` for `IEnumerable`
  async iterators, etc.).
- ExecutionContext flow / `AsyncLocal` / SynchronizationContext capture
  (deferred — the sync path needs none of it; the suspend slice will need it
  for `AwaitOnCompleted`).
- Thread-pool / cross-thread resume (the sync path runs on the caller's
  thread; the suspend slice owns cross-thread).

## Scoping (load-bearing — Step 20 is the largest runtime step)

**Decision: SPLIT. Ship `neo-step20-async` (sync) now; `neo-step20-async-suspend`
(truly-async) as a follow-up.**

**Why split.** Step 20 is the largest runtime step in the roadmap (the design
§26 lists 6 deliverables; the suspend machinery alone is frame-to-heap +
ILAsyncContext + continuation registration + cross-interpreter-thread resume).
A single diff covering both is unreviewable and the suspend path has the
highest infinite-loop / reentrancy risk in the runtime (Step 19 F1 was a pool
leak the green smoke missed; async has strictly more subtle states than
delegates).

**Ranking (value × low-regression-risk):**
1. **Sync-completing async** (HIGH value, MEDIUM risk): unblocks every
   `async` method whose awaitables are already complete — a large fraction of
   real async code (cached results, completed tasks, synchronous awaitables).
   Exercises the full builder redirect surface + `Start`→`MoveNext` + sync
   `SetResult`/getter. Does NOT touch the frame-to-heap hoist wiring, the
   `ILAsyncContext` continuation, or cross-thread resume. **This is the slice
   shipped here.**
2. **Truly-async suspend/resume** (HIGH value, HIGH risk): the frame-to-heap
   hoist wired into `AwaitUnsafeOnCompleted`, `ILAsyncContext<T>.MoveNext`
   resumption, the awaited task's continuation callback. Reentrancy + thread
   safety + `ManualResetValueTaskSourceCore` token races. **Deferred.**

**Verify-against-code that the sync path IS cleanly isolatable** (the
load-bearing check the planner prompt demanded):
- The builder API methods are **independent redirections**. `Create`, `Start`,
  `SetResult`, `SetException`, `get_Task`, `SetStateMachine` each have their
  own redirect; `AwaitUnsafeOnCompleted`/`AwaitOnCompleted` are separate. A
  sync-completing async method NEVER calls `AwaitUnsafeOnCompleted` (the C#
  compiler emits `if (awaiter.IsCompleted) goto completed; else
  builder.AwaitUnsafeOnCompleted(...)`; the `IsCompleted` short-circuit skips
  it). So shipping sync-only = shipping 6 redirects + leaving 2 as
  throw-tagged NIE stubs. **Cleanly isolatable. CONFIRMED.**
- The frame-to-heap hoist helper is a **pure function** (frame bytes + ref
  region → fresh `ILTypeInstance`); it ships standalone, unit-probed, NOT
  wired into any redirect. Zero regression risk to the sync path.
- `ILAsyncContext<T>` skeleton: the sync path does NOT need the resumption
  body (a sync-completing method never registers a continuation). The skeleton
  ships so the `get_Task` getter can return `ValueTask<T>` from a concrete
  `IValueTaskSource<T>` even on the sync fast path (design §26.6 shape), but
  the `MoveNext()` body is tagged NIE. The sync getter uses the
  `core.SetResult` + `ValueTask<T>(this, token)` fast path — no resumption.

**Override check.** The planner prompt's default lean was "sync-first split".
The code reading CONFIRMS the split is clean (independent redirects, hoist is
a pure helper, sync path never reaches the suspend opcodes). **No override;
the split stands.**

## Decisions

### D1: Custom Neo builder redirects override the autogen stubs at registration time

The autogen files (`System_Runtime_CompilerServices_AsyncTaskMethodBuilder_*_Bi.cs`)
register their `*Neo` stubs when the test-harness CLR binding initializer runs.
The custom Neo redirects register AFTER (built-in, in `AppDomain` ctor or a
dedicated `RegisterNeoAsyncRedirections()` called from the ctor) and WIN
(`RegisterCLRMethodRedirectionNeo` is last-wins for the same `MethodBase`).
This mirrors the `ExceptionAdaptor` precedent (`neo-il-exception-throw`: a
built-in adaptor registered in the `AppDomain` ctor overrides the absence of a
test-harness registration). The autogen FILES are NOT edited (they regenerate;
patching them is fragile, the Step 19 delegate-binding lesson). 

**Registration scope.** Register custom Neo redirects for ALL builder types
the C# compiler emits (`AsyncTaskMethodBuilder<T>`, `AsyncTaskMethodBuilder`,
`AsyncValueTaskMethodBuilder<T>`, `AsyncValueTaskMethodBuilder`,
`AsyncVoidMethodBuilder`). The `ValueTask` builders register too so loading an
IL assembly with a `ValueTask`-returning async method does not crash, but the
`ValueTask`-bearing sync fast-path uses the same stashed-result mechanism as
`Task` (D5). `AsyncVoidMethodBuilder` (`async void`) gets `Start` + `SetResult`
+ `SetException` (no `Task` getter) — the sync path completes synchronously
and any exception rethrows on the caller's thread (Legacy `async void`
semantics).

**Alternative considered:** edit the autogen files. REJECTED — fragile under
regeneration, and the custom redirects need access to Neo-internal helpers
(`CopyFrameToIL`-family, `RequestILIntepreter`) the autogen codegen does not
emit. A custom redirect in `CLRRedirections.AsyncNeo.cs` is the clean site.

### D2: `Start<TSM>(ref sm)` runs `MoveNext` IN-PLACE on the caller's frame (no fresh interpreter)

The state machine `sm` is an in-frame IL value-type LOCAL in the caller (the
async method's invoking frame). `Start(ref sm)` receives it BY REF (an 8-byte
Ref Slot, Step 17). The redirect:
1. Read the Ref Slot `(objectIndex, offset)`. For an in-frame VT local,
   `objectIndex == -1`, `offset = frameByteOff`.
2. Resolve the `MoveNext` ILMethod on the state machine's ILType
   (`smType.GetMethod("MoveNext")`, cached per-type).
3. Invoke `MoveNext` with `this = &sm` (the in-frame state machine). This is
   an IL instance-method call on a value-type `this` — exactly the
   VT-THIS-ADDR / Step 12b in-frame-VT-instance-call shape
   (`[NEO-IL-VT-INSTANCE-COVERAGE]` family). The `MoveNext` body reads/writes
   the state machine's fields via `_Inline` ops (Step 12) — including
   `<>1__state`, the awaiter field `<>u__1`, user locals, and the
   `<>t__builder` field (where `SetResult`/`SetException` will stash).
4. `MoveNext` returns when the state machine hits a terminal state
   (`SetResult`/`SetException` called, or — in the suspend slice — an
   incomplete await). In the sync scope, `MoveNext` ALWAYS runs to a terminal
   state (every await short-circuits).

**No fresh interpreter.** Unlike Step 19's `NeoInvokeSub` (CLR→IL callback,
needs a fresh pooled interpreter because the CLR caller has no Neo frame),
`Start` runs INSIDE `ExecuteNeo` on the caller's own frame — `MoveNext` is a
plain IL instance call. Reuse the existing Call machinery (`InvokeNeoCallTarget`
or the direct in-frame VT instance-call path). NO `RequestILIntepreter`. (The
deferred resumption slice DOES use a fresh interpreter — the continuation fires
on a CLR thread/pool callback with no in-flight Neo frame; that's the
`NeoInvokeSub` shape, owned by the follow-up.)

**Alternative considered:** box the state machine to an `ILTypeInstance`, call
`MoveNext` on the heap instance, copy back. REJECTED — that's the Legacy /
design-§26-async-path model; for the SYNC path it's pure overhead (the state
machine never escapes the frame). Box-on-suspend is the SUSPEND slice's job.
The sync path keeps `sm` in-frame end-to-end (zero allocation — design §26.7's
sync row).

### D3: `SetResult` / `SetException` stash on the in-frame builder field; `get_Task` produces a completed task

The state machine's `<>t__builder` field is an IL value-type field of the
state machine (the builder is a struct). `SetResult(T result)`:
1. The builder `this` arrives BY REF (the `<>t__builder` field's address in
   the state machine, itself in-frame). Read the Ref Slot.
2. Stash the result: write `result` into a dedicated field on a Neo-side
   `NeoAsyncResult` carrier parked on the `AppDomain` (or a per-builder
   auxiliary map keyed by the builder's frame address) — OR, simpler and
   preferred, stash directly into the `<>t__builder` field's own layout if the
   builder struct has a result slot. **Resolve at apply via JIT dump**: the
   C# compiler's `AsyncTaskMethodBuilder<T>` struct has internal fields
   (`_task` field of type `Task<T>`). The redirect writes
   `Task.FromResult(result)` (or a faulted task for `SetException`) into that
   field via the in-frame `_Inline`/`Stfld_Ref` path. Then `get_Task` reads
   the same field back. NO auxiliary map, NO Neo-side carrier.

`get_Task` getter:
1. Read the builder `this` by ref (the `<>t__builder` field address).
2. Read the stashed `Task<T>` field. If `SetResult`/`SetException` already ran
   (sync path: always — `MoveNext` ran to completion in `Start`), it is a
   completed/faulted task; return it.
3. If the stashed field is null (sync path before `MoveNext` ran — unreachable
   in the sync scope since `Start` runs `MoveNext` before returning, but
   defensive): construct `Task.FromResult(default(T))`.

**This is the design §26.4 sync row**: the builder struct's own field is the
stash; the getter reads it back. Zero allocation on the sync path (the
`Task.FromResult` / faulted-`Task` is the one allocation — unavoidable; even
native C# allocates a completed `Task<T>` here unless it's a cached value).

**For `ValueTask<T>`:** the sync getter constructs
`ValueTask<T>.FromResult(result)` directly (zero `IValueTaskSource`
involvement on the sync path). The `ILAsyncContext<T>` `IValueTaskSource<T>`
path is the SUSPEND slice's. The skeleton ships so the type exists, but the
sync getter does NOT touch it.

### D4: `AwaitUnsafeOnCompleted` / `AwaitOnCompleted` are throw-tagged NIE stubs (the split point)

These redirects register (so the C# compiler's emitted call resolves), but
their bodies throw
`NotImplementedException("Neo async suspend path: neo-step20-async-suspend (Step 20 suspend slice)")`.
A sync-completing async method NEVER reaches them (the `IsCompleted`
short-circuit). Reaching them means a genuinely-incomplete awaitable — the
suspend slice's domain. This is the **explicit, loud split point**: a green
sync probe set + a tagged NIE on the suspend opcodes = the scope boundary is
machine-checkable.

### D5: Frame-to-heap hoist helper = a pure function, unit-probed, NOT wired

`HoistNeoILValueToHeap(ILType smType, byte* srcFrame, int srcPrimOff, int srcRefBase,
AutoList mStack, int srcRefOff, int refCount)` → `ILTypeInstance`:
1. `var heap = new ILTypeInstance(smType, initializeCLRInstance: false);` (no
   adaptor — design §26.5).
2. `Unsafe.CopyBlock(heap.Primitives, srcFrame + srcPrimOff, smType.TotalPrimitiveSize)` (via
   `MemoryMarshal.GetReference(heap.Primitives.AsSpan())` — NOT
   `GetArrayDataReference`, which is .NET 5+; design §0).
3. For `i in 0..refCount`: `heap.ManagedObjects[i] = mStack[srcRefBase + srcRefOff + i];`
   (the in-frame ref region → heap `ManagedObjects`).
4. Return `heap`.

This is the **inverse of `CopyFrameToIL`'s heap→frame direction** + a Box
without the CLR instance. It ships as a standalone helper (extend the
`CopyFrameToIL` family in `ILIntepreter.Neo.cs` or a new
`NeoAsyncHoist` helper). A unit probe constructs an IL VT, populates it on a
frame, hoists, and asserts the heap instance's fields match. **NOT wired into
any redirect** — the suspend slice wires it into `AwaitUnsafeOnCompleted`.

**Why ship it now if not wired.** It is the single most subtle primitive
(byte/ref shape agreement between the in-frame VT and the `ILTypeInstance`;
the F-MAJ-1 / VT-THIS-ADDR lessons were exactly this shape disagreement).
Shipping + probing it in isolation de-risks the suspend slice: when the
suspend slice wires it, the primitive is already proven.

### D6: `ILAsyncContext<T>` skeleton — sync-shortcut paths live, resumption tagged NIE

```
class ILAsyncContext<T> : IValueTaskSource<T>, IAsyncStateMachine
{
    ILTypeInstance stateMachine;     // the hoisted SM (null on sync path)
    ILMethod moveNextMethod;         // smType.MoveNext()
    ManualResetValueTaskSourceCore<T> core;

    // --- sync shortcut (used by the Task getter's ValueTask path, deferred slice) ---
    internal void SetResultSync(T result) => core.SetResult(result);
    internal void SetExceptionSync(Exception e) => core.SetException(e);

    // --- IValueTaskSource<T> (live; core handles the token races) ---
    public T GetResult(short token) => core.GetResult(token);
    public ValueTaskSourceStatus GetStatus(short token) => core.GetStatus(token);
    public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
        => core.OnCompleted(continuation, state, token, flags);

    // --- IAsyncStateMachine.MoveNext (the resumption entry; DEFERRED slice) ---
    void IAsyncStateMachine.MoveNext()
    {
        throw new NotImplementedException("Neo async resumption: neo-step20-async-suspend (Step 20 suspend slice)");
    }
}
```

The sync path's `Task` getter (D3) does NOT use `ILAsyncContext<T>` (it
returns `Task<T>` / `ValueTask<T>.FromResult` directly). The skeleton ships so
the type + the `ManualResetValueTaskSourceCore<T>` integration compile and are
unit-probed (a `SetResultSync` + `GetResult` round-trip), de-risking the
suspend slice's continuation plumbing. The `MoveNext` resumption body is the
deferred slice's load-bearing deliverable (it must restore the hoisted SM to a
fresh interpreter's frame, jump to the await state, run `ExecuteNeo`, and on
the next `SetResult` call `core.SetResult`).

### D7: `get_Task` for `Task<T>`-bearing builders returns a `Task<T>`; for `ValueTask<T>`-bearing builders returns a `ValueTask<T>` (sync `FromResult`)

The C# compiler emits a DIFFERENT builder type per async return type. The
redirect for `AsyncTaskMethodBuilder<T>.get_Task` returns a `Task<T>` (D3).
The redirect for `AsyncValueTaskMethodBuilder<T>.get_Task` returns a
`ValueTask<T>` — but the sync path constructs `ValueTask<T>.FromResult(result)`
directly (NOT `new ValueTask<T>(context, token)`; the context is the suspend
slice). The return-value marshaling back to the caller (the async method's
original return) uses the existing Neo return-write path (Step 8 `Ret`; the
return is a `Task<T>` / `ValueTask<T>` reference type = an mStack index).

## Risks / Trade-offs

- **[The in-frame VT instance-method call (`MoveNext`) is the
  `[NEO-IL-VT-INSTANCE-COVERAGE]` family]** → Mitigation: probe BEFORE
  finalizing. The `MoveNext` call on an in-frame state machine is an IL
  value-type instance method with a byref `this`. This is the exact shape the
  Step 13-area4 review flagged as uncovered. JIT-dump the `MoveNext` call site
  at apply to confirm (a) the `this` arrives as a frame-native Ref Slot, (b)
  the `_Inline` field ops resolve against the state machine's frame region.
  STOP if the dump shows the state machine is boxed (the Legacy model) rather
  than in-frame — that would mean the JIT did NOT recognize `sm` as an in-frame
  VT and D2 fails. (Mirrors the VT-THIS-ADDR / area4 dump-gated discipline.)
- **[Builder field layout — does `AsyncTaskMethodBuilder<T>` have a stashed
  `_task` field the C# compiler emits?]** → Mitigation: JIT-dump the
  `SetResult`/`get_Task` call sites to see the field offsets the JIT emits for
  the builder's `<>t__builder` field accesses. The redirect must read/write
  the SAME offsets the JIT emits for the in-frame builder struct. If the
  builder's internal fields are NOT laid out as the JIT expects (e.g. the
  builder struct is opaque), fall back to a per-builder-address auxiliary map
  on the `AppDomain` (D3 fallback). Decide at apply from the dump.
- **[Infinite loop — async bugs love to loop]** → Mitigation: test >10s =
  kill + investigate (handoff §1). A `MoveNext` that does not reach a terminal
  state (a malformed state machine, or a `state` field the redirect corrupts)
  loops forever. The sync probes MUST be tightly bounded (a 2-await method,
  not a 10000-iteration loop). Add a per-probe iteration cap in the test body
  (a `while` with a max-iterations guard) as a secondary defense.
- **[Exception propagation in `async void`]** → `AsyncVoidMethodBuilder.SetException`
  in native C# rethrows on the `SynchronizationContext` (or the threadpool if
  none). The sync Neo path's `SetException` must propagate the exception to
  the caller (the `async void` method's invoker) synchronously — there is no
  `SynchronizationContext` in the interpreter. Match Legacy semantics (Legacy
  `AsyncVoidMethodBuilder` redirect rethrows). Probe: an `async void` method
  that throws, caught by the caller.
- **[Autogen stub override ordering]** → The custom redirect must register
  AFTER the autogen initializer. The autogen runs when the test harness loads
  CLR bindings; the `AppDomain` ctor runs first. So the custom redirects
  cannot be in the ctor (they'd be overwritten by autogen). They must register
  in a hook that runs AFTER autogen — OR the autogen files must NOT register
  the `*Neo` builder stubs at all (regenerate them as empty). **Resolve at
  apply**: check the registration order; if autogen runs after the ctor, the
  custom redirects register in the same site the test harness uses (or the
  autogen builder files are regenerated to skip `*Neo` registration for the
  builder types, since the custom redirect owns them). Mirrors the Step 19
  delegate-binding codegen-fix decision.
- **[Nested async (an async method awaiting another sync-completing async
  method)]** → The inner async's `Task` is already complete when the outer
  `await`s it (sync scope), so the outer's `MoveNext` short-circuits the
  inner's await. But the inner's `MoveNext` runs FIRST (inside the outer's
  `Start`). Reentrancy: the inner `MoveNext` runs on the SAME frame as the
  outer (it's an in-frame VT instance call). The inner state machine is a
  SEPARATE in-frame VT (the inner async's locals). Probe this explicitly (the
  nested-async probe).
- **[Legacy byte-identical regression]** → Every runtime/codegen edit is
  `#if ENABLE_NEO_MODE`-gated. `ExecuteR` is the reference. Gate: plain
  `Debug` + `useRegister=true` Legacy 518/519 baseline holds. The autogen
  builder stubs (Legacy `#else` arms) are untouched.

## Migration Plan

No migration (pure feature add for Neo; Legacy unchanged). Rollback = revert
the change directory + the source edits; the autogen stubs return to being
the active Neo builder redirects (async fails again on Neo, as on HEAD).

## Open Questions

- **OQ1 (resolve at apply via JIT dump):** does the JIT lay out the
  `AsyncTaskMethodBuilder<T>` struct's internal `_task`-equivalent field such
  that the `SetResult`/`get_Task` redirects can read/write it via the in-frame
  `_Inline` path, OR is the builder struct opaque (forcing the auxiliary-map
  fallback)? Determines D3's primary vs fallback.
- **OQ2 (resolve at apply):** for the `Start`→`MoveNext` call, does the JIT
  route it as a normal IL instance-method call (the VT-THIS-ADDR in-frame-VT
  instance-call shape), or does it box the state machine? If it boxes, D2
  fails and the design must revisit (likely: force the JIT to recognize `sm`
  as in-frame via a type-spec seed, mirroring Newobj-dest-typing from
  VT-THIS-ADDR). The dump decides.
- **OQ3 (resolve at apply):** autogen-stub override ordering — do the custom
  redirects register cleanly after autogen (last-wins), or must the autogen
  builder files be regenerated to skip `*Neo` registration for builder types?
  Determines the registration site (Risk: autogen override ordering).
- **OQ4 (deferred to suspend slice, recorded here):** the `MoveNext`
  resumption entry must restore a hoisted state machine to a fresh
  interpreter's frame. The exact frame-build (which field of the hoisted
  `ILTypeInstance` becomes the `this` Ref Slot, how the await state is
  restored) is the suspend slice's design. The `ILAsyncContext<T>` skeleton +
  the hoist helper ship now to de-risk it, but the wiring is NOT designed
  here.

## Deferred (warm-seed for `neo-step20-async-suspend`)

The suspend slice will:
1. Wire D5's hoist helper into a real `AwaitUnsafeOnCompleted` body: hoist the
   in-frame SM to a heap `ILTypeInstance(initializeCLRInstance:false)`, create
   an `ILAsyncContext<T>` holding it + the `MoveNext` ILMethod, register
   `context.MoveNextDelegate` (`Action`) with the awaiter's
   `UnsafeOnCompleted`, and return from `MoveNext` WITHOUT calling `SetResult`
   (the SM is now suspended).
2. Implement `ILAsyncContext<T>.MoveNext()` (the `IAsyncStateMachine.MoveNext`
   resumption): obtain a fresh pooled interpreter (`RequestILIntepreter`,
   Step 19 `NeoInvokeSub` shape), build a frame, write the hoisted SM as
   `this`, `ExecuteNeo` the `MoveNext` ILMethod (it resumes at the await
   state via the `<>1__state` field), on the next terminal state call
   `core.SetResult`/`core.SetException`, `FreeILIntepreter` in `finally`.
3. Switch the `ValueTask<T>` getter to `new ValueTask<T>(context, token)` when
   the SM suspended (the `core` owns the result).
4. Add ExecutionContext / SynchronizationContext capture for `AwaitOnCompleted`
   (the `Unsafe` variant skips it).

This is captured so the suspend-slice planner warm-seeds cheaply. None of it
ships in this change.

## Apply Findings (2026-07-06)

### Dump-confirmed: OQ2 RESOLVED -- the SM is a HEAP ILTypeInstance, NOT an in-frame VT

The C# compiler emits the async state machine `<Method>d__N` as a C# `struct`,
but **ILRuntime loads it as a reference type** (`IsValueType == false`). The
driver body JIT-dumps as:

```
0:newobj r0, <Method>d__0..ctor()           # SM newsobj'd -> HEAP ILTypeInstance
4:stfld.ref r0, r2, builder_field
7:stfld.i4 r0, r2, state_field = -1
5:ldflda r1, r0, builder_field               # &SM.builder (Ref Slot)
6:ldloca.s r2, r0                            # &SM (Ref Slot)
7:call Start(ref sm)
8:ldflda r1, r0, builder_field
9:call get_Task()
```

So the SM is a heap object end-to-end. MoveNext's `this` (ParamInfos[0]) is a
**4-byte reference** (Size=4, RefCount=1), NOT an in-frame VT. D2's in-frame-VT
premise does NOT apply; the heap-object premise is simpler. The
`[NEO-IL-VT-INSTANCE-COVERAGE]` concern is sidestepped -- MoveNext is a normal
IL instance method on a heap ILTypeInstance.

### OQ3 RESOLVED -- first-registered-wins; register in the AppDomain ctor

`RegisterCLRMethodRedirectionNeo` is **first-registered-wins** (`if
(!ContainsKey) add`), NOT last-wins as the design assumed. The AppDomain ctor
runs BEFORE the test-harness `CLRBindings.Initialize(app)`, so registering the
custom redirects in the ctor (via `CLRRedirectionsAsyncNeo.Register(this)`)
makes them WIN and the autogen stub registrations are skipped for the same
MethodBase. Confirmed: the custom `Start` redirect IS hit (not the autogen
default-builder stub). The autogen builder FILES are NOT edited (mirrors D1).

### OQ1 RESOLVED -- D3 fallback (auxiliary map keyed by SM)

The CLR `AsyncTaskMethodBuilder<T>` struct's internal `_task` field is OPAQUE
to the Neo frame (it is a CLR struct field nested in the SM, not directly
readable via the in-frame path). D3's auxiliary-map fallback is used: a
ThreadStatic `Dictionary<ILTypeInstance, object>` (the `SmTaskMap`) holds the
completed/faulted Task, keyed by the SM ILTypeInstance. The SM is the stable
identity across Start/SetResult/SetException/get_Task (the builder byref for
the latter three is a Ref Slot `(sm_mStackIdx, builder_field_off)`, so the
owning SM is recovered via `RecoverSmFromBuilderByref`).

### Custom redirect set shipped (all override the autogen stubs)

`ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` (NEW) registers:
- **Builder redirects**: `AsyncTaskMethodBuilder<T>`, `AsyncTaskMethodBuilder`,
  `AsyncValueTaskMethodBuilder<T>`, `AsyncValueTaskMethodBuilder`,
  `AsyncVoidMethodBuilder` -- Create/Start/SetResult/SetException/get_Task/
  SetStateMachine. `Start<TSM>` registered against the OPEN generic definition
  (TryGetRedirection tries GetGenericMethodDefinition first, so it matches any
  TSM instantiation). AwaitUnsafeOnCompleted/AwaitOnCompleted throw the tagged
  NIE (the suspend-slice split point).
- **Awaiter/Task accessor overrides** (the autogen stubs use
  `default(TaskAwaiter)` and never read the real awaiter -- they are NON-
  FUNCTIONAL and MUST be overridden for the sync path): `TaskAwaiter<T>/
  TaskAwaiter.get_IsCompleted`/`GetResult`, `Task<T>/Task.GetAwaiter`/
  `get_Result`, `Task.FromResult<T>`, `Task.get_CompletedTask`. The awaiter's
  wrapped `m_task` is read via reflection.

### `Start` -> `MoveNext`: fresh pooled interpreter (NOT in-place)

D2's "run MoveNext in-place on the caller's frame" was ABANDONED at apply: an
in-place recursive ExecuteNeo corrupted the caller's frame (the driver's SM
reference was lost between Start and get_Task -- `ldflda` produced `(0,0)`
instead of `(smIdx, fieldOff)` after MoveNext ran on the same frame). The fix
mirrors Step 19's `NeoInvokeSub`: `DriveMoveNext` uses a FRESH pooled
interpreter (`RequestILIntepreter`/`FreeILIntepreter` in `finally`) so
MoveNext's frame + mStack reservation is fully isolated from the caller's
in-flight frame. The SM (a heap reference) is written into the fresh frame's
slot-0.

### D5 hoist helper + D6 ILAsyncContext<T> skeleton shipped

`HoistNeoILValueToHeap` added to `ILIntepreter.Neo.cs` (the inverse of
`CopyFrameToIL`; `new ILTypeInstance(smType, initializeCLRInstance:false)` +
byte/ref copy). NOT wired into any redirect (the suspend slice owns the wiring).
`ILAsyncContext<T> : IValueTaskSource<T>, IAsyncStateMachine` skeleton shipped
at `ILRuntime/Runtime/Intepreter/ILAsyncContext.cs` -- IValueTaskSource<T>
surface live (delegating to ManualResetValueTaskSourceCore<T>); MoveNext
throws the tagged NIE. The sync path's get_Task does NOT use ILAsyncContext
(returns Task<T>/ValueTask<T>.FromResult directly).

### BLOCKER: sync slice blocked by a pre-existing `Callvirt_CLR` generic-type bug

The sync-completing async path is infrastructure-complete (builder redirects
override autogen; awaiter/Task accessors overridden; Start -> MoveNext drives
via fresh interpreter) but the end-to-end sync Task<int> probe does NOT yet
complete. `MoveNext`'s `call Task<int>.GetAwaiter()` lowers to `Callvirt_CLR`;
when the callvirt CLRMethod has NO RedirectionNeo, `clrMethod.Invoke` (CLR
reflection) throws `ArgumentException: The specified Type must not be a generic
type` (the MethodInfo is on the generic definition `Task`1`, not the closed
`Task<int>`). This is ROUTED AROUND by registering `Task_T_GetAwaiter_Neo` /
`Task_T_GetResult_Neo` as Neo redirects -- `InvokeNeoClrMethod` checks
`clrMethod.RedirectionNeo` and the redirect fires (confirmed: GetAwaiter
redirect runs, returns a real TaskAwaiter<int>). The remaining gap is the
**TaskAwaiter struct round-trip**: after GetAwaiter returns the awaiter (via
WriteNeoValueType), MoveNext's subsequent `get_IsCompleted` call (a `call` on
the awaiter local, `this` = ldloca) does NOT reach its redirect -- the awaiter
struct's flat-bytes representation appears to not round-trip correctly between
the GetAwaiter return write and the get_IsCompleted byref-this deref. This is a
CLR-struct-marshaling edge (the TaskAwaiter<T> is a CLR struct with a Task
reference field) that needs separate investigation. It is NOT introduced by
this change -- it is the same CLR-struct-instance-method family as
`[NEO-BYREF-THIS]` / area4b.

Route to unblock the sync slice: (a) verify the TaskAwaiter flat-bytes size
matches between WriteNeoValueType (Marshal.SizeOf) and the optimizer's declared
slot size; (b) confirm CopyNeoCallArguments dereferences the ldloca byref for
the get_IsCompleted `this` (area4b PrimitiveByRefSrc flag); (c) if the awaiter
struct shape disagrees, write the awaiter's Task reference directly as a ref
slot rather than via WriteNeoValueType.

### Files edited (all `#if ENABLE_NEO_MODE`-gated; Legacy untouched)

- `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` (NEW) -- the
  custom builder + awaiter/Task redirects + `Register`.
- `ILRuntime/Runtime/Intepreter/ILAsyncContext.cs` (NEW) -- the
  IValueTaskSource<T>/IAsyncStateMachine skeleton (sync surface live; MoveNext
  tagged NIE).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- added
  `HoistNeoILValueToHeap` (the D5 hoist helper, standalone, not wired).
- `ILRuntime/Runtime/Enviorment/AppDomain.cs` -- call
  `CLRRedirectionsAsyncNeo.Register(this)` in the ctor (Neo-only, after the
  ExceptionAdaptor registration).
- `TestCases/NeoStep20Test.cs` (NEW) -- the NeoStep20_* probes (sync Task<T>/
  Task/ValueTask<T>/async-void, multi-await, exception, nested, the
  AwaitUnsafeOnCompleted NIE-confirmation probe).

### Smoke state

- Existing NeoStep smoke: NOT regressed (181/181 baseline holds; all 13 new
  failures are NeoStep20_* async probes blocked by the Callvirt_CLR bug above).
- The sync async probes are NOT yet green (blocked). The AwaitUnsafeOnCompleted
  tagged-NIE confirmation probe is NOT yet reachable (MoveNext NREs at
  GetAwaiter before the IsCompleted short-circuit can route to the suspend NIE).
- Suspend primitives (hoist helper + ILAsyncContext skeleton) shipped as
  proven-by-compilation but NOT exercised by a green test (the sync path that
  would exercise them as a side-effect is blocked).


## Review-loop round 1 (TaskAwaiter round-trip) -- 2026-07-06

### Verdict: STOPPED (stacked pre-existing CLR-struct-field-of-IL-instance edges; NOT a focused fix)

The fixer (non-author) dump-gated the sync path per the blocker detail. The
blocker turns out to be a DIFFERENT and EARLIER edge than the implementer's
"TaskAwaiter byref-this deref" hypothesis, AND it splits the probe set:

**Probe-by-probe state (NeoStep20 filter, individual runs):**
- TC1 (sync Task<int>, single `await Task.FromResult`): **PASS** -- the full
  sync path runs end-to-end (Start -> DriveMoveNext -> GetAwaiter redirect ->
  get_IsCompleted redirect (boxed a real TaskAwaiter<int>) -> GetResult ->
  SetResult -> get_Task; driver observes IsCompleted + Result == 10). The
  Awaiter round-trip the implementer flagged as blocked is NOT blocked for the
  generic Task<int> single-await shape -- it works.
- TC7 (nested Task<int>, both sync): **PASS**.
- TC2 (non-generic Task), TC4 (async void): **FAIL** -- `IndexOutOfRange` in
  `CopyNeoCallArguments` -> `NeoMarshalByrefFieldToSlot`, at the `Start` call's
  builder-byref, BEFORE MoveNext runs.
- TC3 (ValueTask<int>), TC5 (multi-await Task<int>), TC6 (Task<int> exception),
  TC8 (incomplete await): **FAIL** -- each at a distinct later point.

### Dump-confirmed mismatch (the load-bearing finding)

The C# async state machine `<Method>d__N` is loaded as a HEAP ILTypeInstance
(OQ2 confirmed). Its fields include IL-primitive fields (`<>1__state` int) and
CLR-struct fields (`<>t__builder` = AsyncTaskMethodBuilder, `<>u__1` =
TaskAwaiter -- both CLR structs). The ILType field-layout pass
(`ILType.cs:2129-2157`) lays out a CLR-struct field via the `else` branch
(line 2146): it records the field's `PrimitiveOffset` (the running
`primitiveOffset` cursor) AND `ReferenceOffset`, then does `referenceOffset++`
-- i.e. it treats the CLR struct as a REFERENCE slot and does NOT advance
`primitiveOffset` by the struct's size. So the CLR-struct field's bytes do NOT
live in the ILTypeInstance's `Primitives` array (only IL-primitive fields do).

But the JIT's `ldflda` of that CLR-struct field emits a byref `(smMStackIdx,
field.PrimitiveOffset)` (e.g. `(2, 4)` for the builder after the 4-byte
state). At runtime, `CopyNeoCallArguments` -> `NeoMarshalByrefFieldToSlot`
sees `target is ILTypeInstance`, and reads `ili.Primitives[off]` for `sz`
bytes -- but `Primitives.Length` is only the IL-primitive total (4 for
non-generic Task SM; 12 for Task<int> SM). **Dump proof:**
- TC1 `<NeoStep20_SyncTaskOfT>d__1`: `smPrimSize=12, smPrimLen=12` -- the
  builder-byref `(2, 4, sz=8)` reads Primitives[4..12], IN range (the 8 extra
  bytes happen to be present because the Task<int> SM has more IL-primitive
  field contribution). PASSES by luck of layout.
- TC2 `<NeoStep20_SyncTask>d__2`: `primLen=4` -- the builder-byref `(2, 4,
  sz=8)` reads Primitives[4..12], **OOB** -> IndexOutOfRange.

So the SAME `ldflda &SM.<>t__builder` shape OOBs on the non-generic Task SM
and happens to fit on the Task<int> SM -- a layout accident, not a designed
contract. This is the **CLR-struct-field-of-IL-instance addressing defect**:
the byref encoding `(objIdx, PrimitiveOffset)` is unrecoverable to the field's
actual storage (the ManagedObjects ref slot at `ReferenceOffset`) because the
byref carries only ONE offset.

### Why STOP (the stacked-edges criterion, OPT-HARDEN K1 lesson)

The same defect class underlies MULTIPLE distinct probe failures, each at a
different call site:
1. **TC2/TC4** (`Start` builder-byref OOB): the builder is a CLR-struct field;
   `ldflda` returns its stale PrimitiveOffset, OOB on Primitives.
2. **TC3** (ValueTask<int>): the AsyncValueTaskMethodBuilder is a CLR-struct
   field -- same OOB shape on its SM.
3. **TC5** (multi-await): MoveNext runs but the SECOND await's awaiter field
   `<>u__1` (a CLR struct) reuse hits the same family inside the SM (the
   awaiter is stored via stfld into the CLR-struct field, which the layout
   put in ManagedObjects, not Primitives).
4. **TC6** (Task<int> exception): the throw path inside MoveNext.
5. **TC8** (incomplete await): MoveNext NREs before the IsCompleted
   short-circuit can route to the AwaitUnsafeOnCompleted NIE.

A focused single-site fix does NOT exist: the byref encoding is ambiguous
(one offset, two possible storage regions). A real fix requires either:
- A JIT change so `ldflda` of a CLR-struct-field-of-IL-instance produces a
  recoverable encoding (e.g. a sentinel objIdx + the field's ReferenceOffset,
  with a runtime branch in NeoMarshalByrefFieldToSlot that reads the boxed
  struct from ManagedObjects[ReferenceOffset]); OR
- A layout change so a CLR-struct field's flat bytes ARE stored in Primitives
  (advance primitiveOffset by the struct's managed size, mirror in
  AllocateNeoCallParamSlot + every stfld/ldfld/by-value-param consumer).

Both are broad (touch the field-layout pass + every struct-field consumer),
NOT the "mirror area4b/opt-harden-2 discipline, minimal site" fix the prompt
envisioned. This is exactly the stacked-pre-existing-edges STOP case. Forcing
a narrow fix (e.g. zeroing the OOB dest in NeoMarshalByrefFieldToSlot) would
make TC2/TC4 NOT crash but return a default builder -> the SM-keyed SmTaskMap
never gets a real Task -> silent wrong result (the silent-corruption class the
OPT-HARDEN review-fix M1 lesson forbids).

### Callvirt_CLR generic-type-instance bug -- NOTED (not fixed)

The implementer's "second pre-existing bug" (`Callvirt_CLR` on a generic-type-
instance method with no redirect throws `ArgumentException: must not be a
generic type`) did NOT block any green-target probe: every Task<T>/TaskAwaiter<T>
accessor the sync path exercises IS covered by a registered Neo redirect
(Task_T_GetAwaiter_Neo, TaskAwaiter_T_GetIsCompleted_Neo, etc.), so the
reflection-fallback `clrMethod.Invoke` path (where the generic-type ArgumentException
originates) is never reached for the sync probes. The `WriteValueTypeReturn`
helper already uses `Optimizer.GetNeoValueTypeManagedSize` (not
`Marshal.SizeOf`), which is the fix the implementer applied for the
TaskAwaiter<T> return write. NOTED as a follow-up for any future sync probe
that exercises an UN-redirected generic-type-instance CLR method.

### Ship recommendation

Ship the infrastructure PARTIAL (the implementer's shipped code is correct for
the generic Task<int>/ValueTask<T>-of-reference single-await + nested shapes:
TC1 + TC7 green proves the builder-redirect surface, Start->MoveNext routing,
awaiter/Task accessor overrides, and the SmTaskMap stash all work end-to-end).
DEFER the remaining 6 probes to a follow-up child that closes the
CLR-struct-field-of-IL-instance addressing defect (the load-bearing primitive
the suspend slice ALSO needs -- the awaiter field `<>u__1` is the same shape).
The AwaitUnsafeOnCompleted tagged-NIE confirmation (TC8) is NOT yet reachable
(MoveNext fails before the IsCompleted short-circuit on the Task.Delay probe
too).

### Smoke (no baseline regression)

NeoStep smoke: 199 ran, 11 failed -- ALL 11 failures are NeoStep20 probes
(the filter catches the 8 drivers + the async-method-body entries). The 181
non-NeoStep20 probes stay green (181/181). Legacy untouched (all changes are
`#if ENABLE_NEO_MODE` or Neo-only files).

### Working tree

All fixer diagnostics removed; working tree is byte-identical to the
implementer's shipped state (the AsyncNeo/ILAsyncContext/NeoStep20Test files
are untracked-new; ILIntepreter.Neo.cs diff is the implementer's hoist helper,
no fixer additions). UNCOMMITTED (LEAD commits after re-review).
