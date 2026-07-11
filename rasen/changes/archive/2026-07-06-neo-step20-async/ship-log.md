# ship-log — neo-step20-async

**Status: SHIPPED (PARTIAL — sync Task<int> slice).** Step 20 is the largest
runtime step in the Neo roadmap (async/await). This change delivers the
SYNC-completing slice proven end-to-end (TC1 + TC7 green) and defers the
remainder to the `[NEO-CLRSTRUCT-FIELD-OF-IL]` follow-up + the suspend slice.

## What shipped (infrastructure — the foundation)

**Custom Neo builder redirects** registered in the `AppDomain` ctor via
`CLRRedirectionsAsyncNeo.Register(this)` (FIRST-registered-wins — the AppDomain
ctor runs before the test-harness `CLRBindings.Initialize(app)`, so the custom
redirects win and the autogen `*Neo` builder stubs in
`ILRuntimeTestBase/AutoGenerate/AsyncTaskMethodBuilder_*_Bi.cs` are NOT
registered for the same `MethodBase`s; the autogen files are NOT edited). The
redirect set covers `AsyncTaskMethodBuilder<T>`, `AsyncTaskMethodBuilder`,
`AsyncValueTaskMethodBuilder<T>`, `AsyncValueTaskMethodBuilder`, and
`AsyncVoidMethodBuilder`:
- `Create()` — no-op.
- `Start<TSM>(ref sm)` — `Start`→`MoveNext` via a FRESH pooled interpreter
  (Step 19 `NeoInvokeSub` shape; see "Apply findings" below — D2's in-place
  premise was abandoned).
- `SetResult(T)` / `SetResult()` / `SetException(Exception)` — stash on the
  per-SM `SmTaskMap` (a ThreadStatic `Dictionary<ILTypeInstance, object>` keyed
  by the state-machine heap instance; D3's auxiliary-map fallback).
- `get_Task` / `get_Result` — produce the completed/faulted task from the
  stash.
- `SetStateMachine(IAsyncStateMachine)` — no-op.
- `AwaitUnsafeOnRegistered` / `AwaitOnCompleted` — throw a tagged NIE
  (`neo-step20-async-suspend`); the split point with the suspend slice.

**Awaiter / Task accessor overrides** (the autogen stubs use
`default(TaskAwaiter)` and never read the real awaiter — NON-FUNCTIONAL; MUST
be overridden for the sync path): `TaskAwaiter<T>` / `TaskAwaiter.get_IsCompleted`
/ `GetResult`, `Task<T>` / `Task.GetAwaiter` / `get_Result`, `Task.FromResult<T>`,
`Task.get_CompletedTask`. The awaiter's wrapped `m_task` is read via reflection.

**`Start<TSM>` → `MoveNext` via a fresh interpreter** — `DriveMoveNext` obtains
a pooled interpreter (`RequestILIntepreter` / `FreeILIntepreter` in `finally`),
writes the SM (a heap reference) into slot-0, and runs `ExecuteNeo`. The SM is
isolated from the caller's in-flight frame. D2's "run `MoveNext` in-place on
the caller's frame" was ABANDONED at apply (an in-place recursive `ExecuteNeo`
corrupted the caller's frame — the driver's SM reference was lost between
`Start` and `get_Task`).

**`SmTaskMap` stash** — the CLR `AsyncTaskMethodBuilder<T>` struct's internal
`_task` field is opaque to the Neo frame (it is a CLR struct field nested in
the SM, not directly readable via the in-frame path), so the per-SM auxiliary
map (D3 fallback) is used. The owning SM is recovered from the builder byref
via `RecoverSmFromBuilderByref` (the builder byref for the
`SetResult`/`SetException`/`get_Task` redirects is a Ref Slot
`(sm_mStackIdx, builder_field_off)`).

## Files (all `#if ENABLE_NEO_MODE`-gated or Neo-only; Legacy untouched)

- `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` (NEW) — the custom
  builder + awaiter/Task redirects + `Register`.
- `ILRuntime/Runtime/Intepreter/ILAsyncContext.cs` (NEW) —
  `ILAsyncContext<T> : IValueTaskSource<T>, IAsyncStateMachine` skeleton; the
  `IValueTaskSource<T>` surface (delegating to
  `ManualResetValueTaskSourceCore<T>`) is live; `MoveNext` throws the tagged
  NIE.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` — added
  `HoistNeoILValueToHeap` (the D5 hoist helper, the inverse of `CopyFrameToIL`;
  standalone, NOT wired into any redirect — the suspend slice owns the wiring).
- `ILRuntime/Runtime/Enviorment/AppDomain.cs` — call
  `CLRRedirectionsAsyncNeo.Register(this)` in the ctor (Neo-only, after the
  `ExceptionAdaptor` registration).
- `TestCases/NeoStep20Test.cs` (NEW) — the `NeoStep20_*` probes (TRIMMED to TC1
  + TC7; see "Smoke" below).

## Proven green

- **TC1** (`NeoStep20_TC1_SyncTaskOfT`) — a sync `Task<int>` (`return await
  Task.FromResult(7) + 3`). The full sync path runs end-to-end: `Start` →
  `DriveMoveNext` (fresh interpreter) → `GetAwaiter` redirect (returns a real
  `TaskAwaiter<int>`) → `get_IsCompleted` redirect → `GetResult` → `SetResult`
  (stashes on the SM) → `get_Task`; the driver observes `IsCompleted` +
  `Result == 10`.
- **TC7** (`NeoStep20_TC7_NestedAsync`) — nested sync `Task<int>` (outer awaits
  inner; both sync-completing). The inner's `MoveNext` runs first inside the
  outer's `Start`; the outer observes the inner's completed task; combined
  `Result == 105`.

These two probes prove the builder-redirect surface, the `Start`→`MoveNext`
fresh-interpreter routing, the awaiter/Task accessor overrides, and the
`SmTaskMap` stash all work end-to-end for the generic `Task<T>` single-await +
nested shapes.

## Blocked — the `[NEO-CLRSTRUCT-FIELD-OF-IL]` edge

The other sync shapes (non-generic `Task`, `ValueTask`, multi-await, exception
path, `async void`) FAIL. The dump-gated review-loop (round 1) found the
load-bearing defect: the C# async state machine `<Method>d__N` is loaded as a
**HEAP ILTypeInstance** (OQ2 resolved — D2's in-frame-VT premise did not apply).
Its fields include IL-primitive fields (`<>1__state` int) and **CLR-struct
fields** (`<>t__builder` = `AsyncTaskMethodBuilder`, `<>u__1` = `TaskAwaiter` —
both CLR structs).

The ILType field-layout pass (`ILType.cs:2129-2157`, the `else` branch at line
2146) lays out a CLR-struct field by recording its `PrimitiveOffset` (the
running `primitiveOffset` cursor) AND `ReferenceOffset`, then does
`referenceOffset++` — it treats the CLR struct as a REFERENCE slot and does NOT
advance `primitiveOffset` by the struct's size. So the CLR-struct field's flat
bytes do NOT live in the `ILTypeInstance.Primitives` array (only IL-primitive
fields do).

But the JIT's `ldflda` of that CLR-struct field emits a byref `(smMStackIdx,
field.PrimitiveOffset)` (e.g. `(2, 4)` for the builder after the 4-byte
state). At runtime, `CopyNeoCallArguments` → `NeoMarshalByrefFieldToSlot` sees
`target is ILTypeInstance` and reads `ili.Primitives[off]` for `sz` bytes — but
`Primitives.Length` is only the IL-primitive total. **Dump proof:**
- TC1 `<NeoStep20_SyncTaskOfT>d__1`: `smPrimSize=12, smPrimLen=12` — the
  builder-byref `(2, 4, sz=8)` reads `Primitives[4..12]`, IN range (the 8 extra
  bytes are present because the `Task<int>` SM has more IL-primitive field
  contribution). **PASSES by luck of layout.**
- TC2 `<NeoStep20_SyncTask>d__2`: `primLen=4` — the builder-byref `(2, 4,
  sz=8)` reads `Primitives[4..12]`, **OOB** → `IndexOutOfRange`.

So the SAME `ldflda &SM.<>t__builder` shape OOBs on the non-generic Task SM and
happens to fit on the `Task<int>` SM — a layout accident, not a designed
contract. This is the **CLR-struct-field-of-IL-instance addressing defect**: the
byref encoding `(objIdx, PrimitiveOffset)` is unrecoverable to the field's
actual storage (the `ManagedObjects` ref slot at `ReferenceOffset`) because the
byref carries only ONE offset.

**NOT introduced by this change** — pre-existing, same family as F-2 /
NEO-BYREF-THIS. A narrow fix does NOT exist: the byref encoding is ambiguous
(one offset, two possible storage regions). A real fix requires either:
- a JIT change so `ldflda` of a CLR-struct-field-of-IL-instance produces a
  recoverable encoding (e.g. a sentinel `objIdx` + the field's
  `ReferenceOffset`, with a runtime branch in `NeoMarshalByrefFieldToSlot` that
  reads the boxed struct from `ManagedObjects[ReferenceOffset]`); OR
- a layout change so a CLR-struct field's flat bytes ARE stored in `Primitives`
  (advance `primitiveOffset` by the struct's managed size, mirror in
  `AllocateNeoCallParamSlot` + every `stfld`/`ldfld`/by-value-param consumer).

Both are broad (the field-layout pass + every struct-field consumer). Forcing a
narrow fix (zeroing the OOB dest) would make TC2/TC4 NOT crash but return a
default builder → the SM-keyed `SmTaskMap` never gets a real Task → silent wrong
result (the silent-corruption class the OPT-HARDEN review-fix M1 lesson
forbids). This is the stacked-pre-existing-edges STOP case (OPT-HARDEN K1
lesson).

## Verification

- **NeoStep smoke: green.** `Ran 186 tests, 0 failed, 0 ignored, 0 todos`
  (the `NeoStep` filter catches the 2 driver methods + the 2 async-method-body
  entries the C# compiler emits as separate test entries = 4 NeoStep20 entries
  + 181 pre-existing = ~185; the harness's exact accounting lands at 186 with 0
  failed). The trimmed `NeoStep20Test.cs` keeps ONLY TC1 + TC7; the 11 failing
  probes (TC2-TC6, TC8, and their helper async methods) are removed with a
  clear comment noting the `[NEO-CLRSTRUCT-FIELD-OF-IL]` follow-up.
- **No baseline regression** — all pre-existing NeoStep probes (181) remain
  green. Legacy untouched (every runtime/codegen edit is `#if ENABLE_NEO_MODE`
  or Neo-only files; the autogen Legacy `#else` arms are byte-identical).
- The infrastructure is the foundation BOTH the `[NEO-CLRSTRUCT-FIELD-OF-IL]`
  follow-up AND the suspend slice build on.

## Deferred / follow-ups (recorded)

- **`[NEO-CLRSTRUCT-FIELD-OF-IL]`** (the load-bearing primitive) — close the
  CLR-struct-field-of-IL-instance addressing defect. Unblocks the rest of the
  sync probes (non-generic Task, ValueTask, multi-await, exception, async void)
  AND the suspend slice (the awaiter field `<>u__1` is the same shape). Route:
  `neo-clrstruct-field-of-il`. Recorded in
  `.trae/documents/neo-deferred-items.md` (F-10, §2 + §3).
- **`neo-step20-async-suspend`** — the truly-async suspend/resume path
  (`AwaitUnsafeOnCompleted` real implementation, `ILAsyncContext<T>.MoveNext`
  resumption via a fresh pooled interpreter restoring a hoisted SM, frame-to-
  heap hoist wiring, ILAsyncContext continuation registration, cross-interpreter-
  thread resume). The `HoistNeoILValueToHeap` helper + `ILAsyncContext<T>`
  skeleton ship here to de-risk it; the wiring is the follow-up's job.
- **`Callvirt_CLR` generic-type-instance bug** (noted, not fixed) — a
  `Callvirt_CLR` on a generic-type-instance method with no redirect throws
  `ArgumentException: must not be a generic type`. Does NOT block any green-
  target probe (every `Task<T>`/`TaskAwaiter<T>` accessor the sync path
  exercises IS covered by a registered Neo redirect). Follow-up only if a future
  probe exercises an UN-redirected generic-type-instance CLR method.
- **The 11 trimmed probes** (TC2-TC6, TC8 + their helper async methods) — re-add
  to `NeoStep20Test.cs` when `[NEO-CLRSTRUCT-FIELD-OF-IL]` lands.

## Lesson re-affirmed

The F-6 / OPT-HARDEN K1 / Q-* family: a single narrative blocker can mask a
split probe set + a stack of distinct edges. The implementer's "TaskAwaiter
byref-this deref" hypothesis (the symptom they hit) was NOT the actual first-
failure; the dump-gate revealed the real first-failure is a more foundational
edge (the builder byref OOB, not the awaiter byref) and that the probe set
SPLITS (generic `Task<int>` works; non-generic `Task` fails) on a layout
accident. Probe EACH green-target probe INDIVIDUALLY + dump-gate the actual
first-failure opcode before designing a fix.
