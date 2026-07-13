# design -- neo-async-statemachine-null (Wave-2 child C8)

## The defect class (recurring, child-2 lineage)
Autogen Neo CLR-binding stubs are often broken `default(...)` / TODO stubs
(`// TODO: ValueType instance in Neo`, or stubs that dispatch through the CLR
`IAsyncStateMachine` adaptor interface). The robust fix is to register a
hand-written Neo redirect on `RedirectMapNeo` in the AppDomain ctor, which runs
BEFORE the test-harness `CLRBindings.Initialize`. The redirect map is
first-registered-wins, and `CLRMethod.TryGetRedirection` tries
`GetGenericMethodDefinition()` FIRST -- so registering ONE open generic
definition preempts every autogen per-instantiation closed-generic stub
(child-2 ldtoken, child-6 InitializeArray, child-22 Activator.CreateInstance).
This child closes the same gap for the non-generic async task/value-task
builder `Start`.

## Why the non-generic `Start` was the only missing redirect
`CLRRedirectionsAsyncNeo.Register` registers `Start` for:
- `AsyncTaskMethodBuilder<T>` -- via `RegisterTaskBuilderT` (open generic def,
  line ~1422).
- `AsyncValueTaskMethodBuilder<T>` -- via `RegisterValueTaskBuilderT` (open
  generic def, line ~1440).
- `AsyncVoidMethodBuilder` -- open generic def inline (line ~1345).

But the NON-GENERIC blocks for `AsyncTaskMethodBuilder` and
`AsyncValueTaskMethodBuilder` registered only `Create`/`get_Task`/
`SetException`/`SetResult` + `RegisterAwaiters` (which does AwaitUnsafeOn/
AwaitOn/SetStateMachine). `Start` was omitted in BOTH. So `async Task` and
`async ValueTask` (non-generic) methods -- whose builder is the non-generic
struct -- fell through `Start` to the autogen `Start_1_Neo` stub.

## The autogen stub's failure mode (confirmed by stack trace)
`Start_1_Neo` (`...AsyncTaskMethodBuilder_Bi.cs:201-209`):
```csharp
AsyncTaskMethodBuilder instance_of_this_method = default(...); // TODO: VT in Neo
IAsyncStateMachineAdaptor @stateMachine =
    (IAsyncStateMachineAdaptor)ReadNeoReference(__frameBase, ref __curPrim, __mStack);
instance_of_this_method.Start<IAsyncStateMachineAdaptor>(ref @stateMachine);
```
`ReadNeoReference` returns the heap `ILTypeInstance` SM; the cast to the CLR
`IAsyncStateMachineAdaptor` interface yields `null` (an ILTypeInstance is not a
CLR adaptor). The framework `AsyncMethodBuilderCore.Start<IAsyncStateMachine
Adaptor>(ref null)` then throws `ArgumentNullException('stateMachine')`.

This is Neo-specific: the `#else Start_1` Legacy stub resolves the SM via
`RetriveObject` + `CheckCLRTypes` as a real adaptor wrapper, so Legacy works.

## The fix (additive, 2 blocks, single file)
Mirror the `AsyncVoidMethodBuilder.Start` registration inline in each
non-generic builder block:
```csharp
MethodInfo startOpen = t.GetMethod("Start", flag);
if (startOpen != null && startOpen.IsGenericMethodDefinition)
    app.RegisterCLRMethodRedirectionNeo(startOpen, AsyncTaskMethodBuilder_Start_Neo);
```
(and the `AsyncValueTaskMethodBuilder_Start_Neo` variant for the value-task
builder). The wrappers already exist and delegate to
`AsyncTaskMethodBuilder_T_Start_Neo`, the proven generic-builder Start.

### Why the generic-builder Start redirect is correct for the non-generic builder
`AsyncTaskMethodBuilder_T_Start_Neo` (`CLRRedirections.AsyncNeo.cs:246`):
- skips slot 0 (builder `this`, 8 bytes: `curPrim += 8`),
- reads the SM byref `(objIdx, off)` from slot 1,
- recovers the heap `ILTypeInstance` from `mStack[objIdx]`,
- drives `MoveNext` via a fresh pooled interpreter.

This is generic-agnostic (it never reads `method.DeclearingType`'s generic
args). The CIL signature is identical for both builders:
`instance void Builder::Start<TSM>(!!TSM& stateMachine)` -- `this` byref + one
byref param -- so the Neo engine lays out the param region identically.

### The 8-byte builder-this skip is correct for all 3 builder sizes
`Start` treats the builder `this` as an 8-byte byref Ref Slot (`curPrim += 8`),
NOT as the struct's flat managed bytes (unlike `SetResult`, which uses
`BuilderThisManagedSize` because the engine dereferences the builder byref and
copies the struct's flat bytes for SetResult's param layout). A byref is ALWAYS
8 bytes, so `+= 8` is correct regardless of the builder's flat-bytes size:
- `AsyncTaskMethodBuilder` (non-generic): 8-byte struct (single `Task` field).
- `AsyncTaskMethodBuilder<T>` (generic): 8-byte struct. (TC8 green.)
- `AsyncValueTaskMethodBuilder<T>` / non-generic: 16-byte struct -- but the
  `Start` byref skip is still 8 (the byref, not the struct bytes).

So no per-builder sizing is needed for `Start` (the existing redirect is
correct as-is); only the registration was missing.

## Re-audit notes (honest)
- `[NEO-CLRSTRUCT-FIELD-OF-IL]`: the `neo-async` spec marks the non-generic
  `Task` sync scenario "BLOCKED on [NEO-CLRSTRUCT-FIELD-OF-IL]". After this
  fix, `AsyncAwaitTest.TestRun/TestRun1/TestRun4/TestClass.Show1` all PASS
  (sync `Task.CompletedTask`, suspend `Task.Delay`, and `Task.Run<int>` paths).
  So either that blocker was resolved by an earlier Wave-1/2 child, or it did
  not apply to these specific shapes. This child does NOT touch the
  CLR-struct-field-of-IL surface; it only registers the missing `Start`
  redirect. The scenario is now reachable; updating its BLOCKED marker is in
  the spec delta.
- `TestRun`/`TestRun1` await `Task.Run<int>(lambda)`. `Task.Run` is NOT
  Neo-redirected, yet these tests PASS -- the reflection fallback handles the
  `Task.Run<int>(ILDelegate)` call for these shapes (the IL lambda is a Step-19
  delegate adapter that round-trips). Out of scope to dig further; they pass.

## Stash-toggle (before/after evidence)
- HEAD (no fix): name-filter `AsyncAwaitTest` -> `Ran 13 tests, 4 failded`,
  all 4 with the `ArgumentNullException('stateMachine')` stack above.
- After fix: name-filter `AsyncAwaitTest` -> `Ran 13 tests, 0 failded`.
- Full smoke: `114 -> 110` (the 4 C8 names absent from the 110-failure list;
  no `AsyncAwait*` and no `NeoStep` test in the failures -> 0 regressions).
- NeoStep: `382 / 0` (baseline holds).

## Capability
`neo-async` (the Step-20 async state-machine bootstrap / builder redirects).
Delta: the existing "Neo async method builder redirection" requirement already
names the non-generic builders; this child adds a pinning scenario that the
non-generic `Start<TSM>` SHALL be Neo-redirected (not left to the autogen
stub), and un-blocks the "Sync-completing async method returning Task"
scenario.
