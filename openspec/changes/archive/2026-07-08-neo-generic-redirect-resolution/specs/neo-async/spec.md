## MODIFIED Requirements

### Requirement: Neo async AwaitUnsafeOnCompleted is a tagged deferral (synchronous scope boundary)

The Neo `AwaitUnsafeOnCompleted<TA,TSM>` and `AwaitOnCompleted<TA,TSM>` builder redirections SHALL be REGISTERED on the Neo redirect map (`RedirectMapNeo`) so a closed-generic call site resolving to them dispatches to the registered handler and does not crash; the handlers SHALL throw a `NotImplementedException` tagged `neo-step20-async-suspend` if reached at runtime. Reaching these redirections indicates a genuinely-incomplete awaitable (an await whose awaiter reports `IsCompleted == false`), which is the DEFERRED suspend/resumption scope.

The closed-generic `AwaitUnsafeOnCompleted<TA,TSM>` (TWO generic arguments) SHALL resolve on `RedirectMapNeo` via the SAME redirect-lookup path as the one-generic-argument `Start<TSM>`: the lookup is arity-agnostic (it branches on `IsGenericMethod && !IsGenericMethodDefinition`, trying `GetGenericMethodDefinition()` first then the closed definition) and SHALL NOT require arity-specific handling for the 2-argument case. The reachability of this tagged deferral SHALL be proven by a DETERMINISTIC probe: an await whose awaiter is backed by a `TaskCompletionSource`-style awaitable whose completion is NOT signaled before the assertion (so `IsCompleted` is deterministically `false`), NOT by a racy `Task.Delay` probe that may sync-complete.

This is the explicit, machine-checkable scope boundary: the synchronous scope is correct and complete for sync-completing async; the suspend scope (the body BEHIND this tagged NIE) is a follow-up.

#### Scenario: Incomplete awaiter reaches the tagged deferral (deterministic probe)

- **WHEN** an async method awaits an awaitable whose `IsCompleted` is deterministically `false` (a `TaskCompletionSource`-backed `Task` whose `SetResult` has NOT been called before the assertion, so the await is forced through `AwaitUnsafeOnCompleted` with no sync-completion race), triggering the closed-generic `AwaitUnsafeOnCompleted<TA,TSM>` call
- **THEN** the Neo redirect for the 2-generic-argument call SHALL resolve on `RedirectMapNeo` (same arity-agnostic lookup path as `Start<TSM>`) and SHALL throw a `NotImplementedException` whose message references `neo-step20-async-suspend`, and SHALL NOT infinite-loop, silently hang, or produce a wrong result
- (PROVEN by a deterministic `TaskCompletionSource`-style regression guard in `TestCases/NeoStep20Test.cs`, the successor to the removed TC8.)

#### Scenario: Sync-completing await never reaches AwaitUnsafeOnCompleted

- **WHEN** an async method awaits an awaitable whose `IsCompleted` is `true`
- **THEN** the C# compiler's lowered `IsCompleted` short-circuit SHALL skip the `AwaitUnsafeOnCompleted` call entirely, and the synchronous scope SHALL complete without invoking the tagged deferral
- (Control for the deterministic probe: proves the probe is specific to the incomplete-await path and does not NIE on a sync-completing await.)
