# Planning Context — neo-async-movenext-fix (TRUE COMPLETION)

> SEED for the planner. Read THIS FIRST. TRUE-COMPLETION mandate: truly-async
> await must WORK end-to-end (not partial-ship, not scoped-deferral). Append
> durable findings after propose.

## What this change is (one line)

Fix the **MoveNext control-flow hang** so a truly-async await (an await whose
awaiter reports `IsCompleted == false`) completes correctly: the state machine
must reach `AwaitUnsafeOnCompleted` (register the continuation), suspend, and
resume via `MoveNext` when the awaited task completes -- running `GetResult` +
continuing the method. This is the **single biggest real Neo usability gap**
(async/await is core C#; truly-async currently HANGS).

## The precise diagnosis already on record (do NOT re-derive -- ADVANCE it)

The B1 child (`openspec/changes/archive/2026-07-08-neo-generic-redirect-resolution/`,
TEST-ONLY partial) EXONERATED the redirect (B1) and pinned the REAL blocker:
> "after `get_IsCompleted` returns false, MoveNext never reaches
> `AwaitUnsafeOnCompleted` (nor `GetResult`) and hangs in a loop / block-
> transition between them."

So the chain that WORKS: `Start -> MoveNext -> get_IsCompleted` (correctly
returns `false` for the deterministic incomplete Task). The chain that HANGS:
`get_IsCompleted == false -> ??? -> AwaitUnsafeOnCompleted` (NEVER reached).

**Your first task is an INSTRUCTION-LEVEL TRACE** of the async state machine's
`MoveNext` body AFTER the `get_IsCompleted` `brtrue` (the `IsCompleted==true`
short-circuit), to find the EXACT opcode/state-machine-state where execution
hangs. The dump is the arbiter. Suspects to confirm/refute from the trace:
- The `brtrue`/`brfalse` target after `IsCompleted` (does it branch to the
  AwaitUnsafeOnCompleted block, or to a wrong state / a loop back to MoveNext
  entry?).
- The `AwaitUnsafeOnCompleted` Call opcode's resolution (B1 confirmed the
  redirect resolves; but does the Call DISPATCH reach the registered handler, or
  does the hang precede the Call?).
- The state-machine `<>1__state` field transitions (does MoveNext re-enter on a
  state that skips the await block?).
- The `Box`/field-store of the awaiter (`<>u__1`) -- F-10 fixed the CLR-struct-
  field-of-IL layout; is the awaiter store/load correct here, or does a wrong
  awaiter field read cause a bad branch?

## Authoritative prior context (READ BEFORE PROPOSING)

1. `openspec/changes/archive/2026-07-08-neo-generic-redirect-resolution/{handoff/implementer-1.md, design.md, ship-log.md}`
   -- the B1 EXONERATION + the MoveNext blocker detail (the load-bearing prior
   diagnosis).
2. `openspec/changes/archive/2026-07-06-neo-step20-async-suspend/` -- the
   suspend-slice child (Phase-1 reachability unblockers SHIPPED: B3 Nop case, B2
   void-GetResult guard, Task.Delay redirect; the `HoistNeoILValueToHeap` helper
   + `ILAsyncContext<T>` skeleton that DE-RISK the suspend). Phase 2 STOPPED here.
3. `openspec/changes/archive/2026-07-06-neo-step20-async/` + `neo-clrstruct-field-of-il/`
   -- the sync slice + the F-10 awaiter-field fix (the awaiter `<>u__1` shape).
4. `TestCases/NeoStep20Test.cs` -- `NeoStep20_TC8_IncompleteAwaitHitsTaggedNIE`
  (`[Ignored]` hang reproducer; the deterministic TCS-backed incomplete Task) +
  TC9 (sync control) + TC10 (IsCompleted-false). TC8 is your reproducer + the
  un-ignore-green TARGET.
5. `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` -- the async
   redirects (`AwaitUnsafeOnCompleted_Neo` is a tagged NIE; the sync-slice
   redirects work).
6. `openspec/changes/neo-completion-portfolio/handoff/lead-2.md` -- portfolio
   context + the async Path-2 as the highest-value open item.

## The deterministic probe is the reproducer (NOT a racy Task.Delay)

`NeoStep20_TC8` uses a `TaskCompletionSource`-backed Task whose `SetResult` is
NEVER called -> `IsCompleted` deterministically `false` -> the await is FORCED
through `AwaitUnsafeOnCompleted` (no sync-completion race). TC8 currently HANGS
(`[Ignored]`; un-ignore -> the process times out). The SUCCESS criterion: TC8
un-ignored, GREEN, within a reasonable time (the await registers a continuation;
the test signals completion via the TCS or a timeout-then-check pattern --
DESIGN the test's completion signal during propose).

## Scope (TRUE COMPLETION -- but sequence the work)

The full truly-async path has 3 pieces; sequence them, each truly done:
1. **The MoveNext control-flow fix** (THIS child's core) -- make the state
   machine REACH `AwaitUnsafeOnCompleted` after `IsCompleted==false` (the hang
   fix). This alone may unblock the rest.
2. **The suspend machinery** -- `AwaitUnsafeOnCompleted_Neo` body: hoist the
   in-frame state machine to the heap (`HoistNeoILValueToHeap`, shipped in
   step20-async) + register the continuation (`ILAsyncContext<T>.MoveNext`,
   skeleton shipped) on the awaited task.
3. **The resume machinery** -- `ILAsyncContext<T>.MoveNext()` resumption:
   restore the hoisted SM to a fresh pooled interpreter, jump to the await
   state, run `ExecuteNeo`, complete the `ManualResetValueTaskSourceCore<T>` /
   SetResult.

If (1) reveals the hang is the ONLY bug and (2)+(3) are mostly wired (the
skeletons shipped), this child may achieve full truly-async. If (2)/(3) need
substantial new work, this child delivers (1) + as much of (2)/(3) as is
tractable, and the remainder sequences into a follow-up child -- BUT under the
TRUE-COMPLETION mandate, the follow-up MUST be driven next (not parked
indefinitely).

## Dump-gate + escalation (the binding discipline)

The async path has been a deep rabbit hole across 3 sessions. The discipline
that held: probe BEFORE designing; STOP only when a fix is genuinely
unfalsifiable. But under the TRUE-COMPLETION mandate, "STOP" now means "sequence
the remainder into the next child + drive it next," NOT "park indefinitely." If
the MoveNext hang resists a first fix attempt, apply the LEAD escalation ladder
(different strategy: re-trace at a different level; isolate the sub-bug; re-
scope) -- do NOT give up after one attempt. The instruction-level trace is the
key; without it, any fix is a guess.

## Build + test (CRITICAL -- always `-f net8.0`; build CLI with `Debug_Neo`,
NEVER TestCases with `Debug_Neo`)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep20   # async slice (12/0/1 baseline; TC8 ignored)
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep     # 218/0/1 regression
```
ALWAYS `-f net8.0`; CLI filter is a `Contains` substring (no `|`). A test taking
>10s usually = the async SM looping / a stuck await -- kill + investigate (that
IS the bug you're fixing). `Debug_Neo` prints huge JIT output -- normal. Truly-
async tests are TIME-SENSITIVE -- use the deterministic TCS probe, NOT real
delays.

## Spec authoring traps (from handoff)

- `specs/neo-async/spec.md` delta PURE ASCII (NOTE: the canonical neo-async spec
  has 57 PRE-EXISTING non-ASCII bytes -- em-dashes/arrows in other requirements'
  prose; your delta must be ASCII-only, don't touch the pre-existing). Start
  every requirement body with "... SHALL ..." on the FIRST hard-wrapped line.
- Capability: **neo-async**. The openspec validator false-fails neo-async (pre-
  existing); LEAD does manual archive merges.
- Legacy is the REFERENCE. Neo-only `#if ENABLE_NEO_MODE`. Legacy-neutral.

## Deliverables

`proposal.md`, `design.md` (the instruction-level trace FINDING + the root-cause
diagnosis + the fix design + the 3-piece sequencing decision), `specs/neo-async/spec.md`
(delta), `tasks.md`. The success criterion is TC8 un-ignored GREEN (truly-async
works).
