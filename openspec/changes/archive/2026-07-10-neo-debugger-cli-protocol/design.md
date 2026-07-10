# Design — neo-debugger-cli-protocol (child 13, the DAP capstone)

**Capability:** neo-debugger. **Wave:** completion-3, child 13. **Status:** implementer.
**Depends on:** neo-debugger-neo-frame / ilvt-local (11) / aot-body (12) — the shipped Neo
`DebugService` backend (breakpoints, single-step, frame/variable inspection).

## Problem
The Neo debugger BACKEND exists in `DebugService`:
- breakpoints (`SetBreakPoint` / `CheckShouldBreak` / `DoBreak`, DebugService.cs:662/815/906),
- single-step (`StepThread` Over/Into/Out, :759; `ExecuteThread` :746),
- frame read (`GetStackFrameInfo` :1092) + variable inspection (`GetThisInfo` :203 Neo arm,
  `GetLocalVariableInfo` :332 Neo arm — children 11/12),
- a wire protocol (`DebuggerServer`, TCP/UDP) the existing VS2022 VSIX frontend speaks.

The GAP: no DAP (Debug Adapter Protocol) surface — an external debugger client (VSCode, over
DAP/stdio) cannot drive a Neo session. lead-6/MEDIUM#7: "CLI debugger-protocol capstone
(VSCode DAP frontend + ~6 protocol/frontend methods)."

## Scope (MINIMAL viable DAP adapter)
The ~6 core DAP requests, wired to the existing backend (no new field-read / step code):

| DAP request | Backend reused | Notes |
|---|---|---|
| `initialize` + `launch`/`attach` | `DebugService` ctor + an in-proc attach | start a Neo session: load the assembly, mark `IsAttached` so `CheckShouldBreak` fires |
| `setBreakpoints` | `DebugService.SetBreakPoint` (:662) + the bind resolution `DebuggerServer.TryBindBreakpoint` (:401) reuses | set a breakpoint at a method/IL-offset (line-based, matching the backend's sequence-point machinery) |
| `stackTrace` | `DebugService.GetStackFrameInfo` (:1092) — the Neo frame chain | return the call stack at a breakpoint |
| `scopes` + `variables` | `DebugService.GetThisInfo` (:203) + `GetLocalVariableInfo` (:332) Neo arms (children 11/12) | return locals/this at a frame |
| `continue` / `next` (step) | `DebugService.ExecuteThread` (:746) / `StepThread` (:759) | resume/step the Neo execution |

OUT of scope (follow-ups, noted): `evaluate`/watch, conditional breakpoints (the backend
CONDITION machinery exists but the DAP expression bridge is not wired), `threads` (single
thread only — the interpreter is single-threaded cooperative, ILIntepreter.Break docs :56),
`pause` (no async interrupt hook), `setExceptionBreakpoints`, `terminate`.

## The component — `NeoDebuggerDapAdapter` (in-proc DAP frontend)

**File:** `ILRuntime/Runtime/Debugger/NeoDebuggerDapAdapter.cs` (`#if ENABLE_NEO_MODE && DEBUG`).
**Companion:** `NeoDebuggerDapProtocol.cs` (the DAP JSON-RPC message + the stdio transport).

### Why in-proc, not a TCP loopback
`DebuggerServer` binds a real `TcpListener` + UDP broadcast (DebuggerServer.cs:68). Binding a
port just to echo to ourselves in one process is brittle + slow (port-in-use, firewall, the
Windows nested-process spawn caveat from lead-7 finding #6). The adapter instead attaches to
`DebugService` DIRECTLY and is the "client" — it captures the server→client events
(breakpoint-hit / step-complete) and issues the client→server requests (bind-breakpoint /
execute / step), both WITHOUT a socket.

### The single-threaded-cooperative contract (the crux)
ILRuntime is single-threaded cooperative (ILIntepreter.Break docs :56-71; `Run` builds the
frame on the CURRENT pooled interpreter). `intp.Break()` BLOCKS the interpreter thread
(`Monitor.Wait`, ILIntepreter.cs:84, or the `mainthreadLock` sleep-loop on the main thread
:72-83); `Resume()` pulses it (:90). So the breakpoint loop is:
1. The interpreter runs on a WORKER thread (`AppDomain.BeginInvoke` / `Thread`).
2. `CheckShouldBreak` fires a breakpoint → `DoBreak` → `SendSCBreakpointHit` → `intp.Break()`
   blocks the worker.
3. The adapter (on its own thread) receives the breakpoint-hit event, serves the DAP requests
   (stackTrace / scopes / variables — all READ-ONLY reads of the blocked frame, which is safe:
   the interpreter is parked), then on `continue`/`next` calls `ExecuteThread`/`StepThread`
   which `Resume()`s the worker.

This is EXACTLY the existing `DebuggerServer` model — the adapter just replaces the TCP
transport with an in-proc event queue + method calls.

### Engine touch (minimal, additive, Neo-gated) — make `DebuggerServer` in-proc-subclassable
The `DebuggerServer` send-event methods (`SendSCBreakpointHit` :534, `SendSCStepComplete` :544,
`SendSCAttachResult`→`SendAttachResult` :346, plus `Start`/`Stop` already `virtual`) are NOT
`virtual` — an in-proc subclass cannot intercept them. **Fix:** mark them `virtual`. This is
additive (a `virtual` method is source-compatible for all existing callers) + the adapter
subclass overrides them to capture events instead of hitting a socket. `Start`/`Stop` are
overridden to NOT bind a listener (the adapter IS the client). `IsAttached` is driven true on
attach so `CheckShouldBreak` (:817) proceeds. This is the ONLY engine file touched; all Neo-
gated via the adapter (the `virtual` marks are neutral under Legacy but only exercised under
Neo via the adapter).

### DAP wire format (real DAP, over stdio)
DAP frames: `Content-Length: N\r\n\r\n<JSON>`. The adapter reads/writes stdio, parses the
`Request`/`Response`/`Event` envelopes, dispatches `command` to the ~6 handlers. For the
self-check (below), the transport is an IN-MEMORY pipe (the adapter's JSON-RPC layer is split
from the byte transport so a check can drive the SAME handlers without a real stdio fork —
mirrors the host-side self-check shape of NeoDebuggerFrameCheck / NeoDebuggerAotBodyCheck).

## Verification — `NeoDebuggerDapCheck` (host-side self-check, CLI mode "NeoDebuggerDap")
Drives a REAL Neo breakpoint session through the adapter (in-mem transport): launch →
setBreakpoints on a probe method → run (worker) → hit the breakpoint → stackTrace →
scopes → variables (assert a known local is inspectable at the breakpoint) → next (step) →
continue (resume to completion). Asserts each DAP request round-trips + a variable's value
is present at the breakpoint (the binding gate: the adapter is load-bearing — on a stub it
fails). Mirrors the other NeoDebugger*Check shapes (static `Run(AppDomain)` → Pass/Fail tally).

A small dedicated probe `NeoDebuggerDapProbe` (TestCases/) carries a method with a KNOWN
local value set before a no-op call site (so a line-breakpoint can land + the local is live).

## SCOPE BOUNDARY / PARK fallback
- **UPDATE (child-13 follow-up, neo-debugger-step-resume):** the `next`/step PARK was
  RESOLVED. The re-audit (fresh eyes) traced the step-resume NIE to a SPECIFIC site — the
  Legacy `StackObject*`-based `AddStackFrameInfoVariables` reading the Neo compact `byte*`
  frame in the step-complete `DoBreak` frame capture (the `ToObject` `default` NIE). NOT a
  deep step-engine gap. Closed by the Neo-gated `AddStackFrameInfoVariablesNeo` (a byte*
  slot read). `NeoDebuggerDapCheck` Cell 4 is now a HARD gate. Full record in `blocked.md`.
- If the breakpoint-hit loop is too big for one child → ship the scaffold + the working core
  methods (initialize/launch/setBreakpoints/stackTrace/scopes/variables) + PARK continue/next
  (the resume/step) in `blocked.md`.
- If `stackTrace`'s `GetStackFrameInfo` (Legacy `StackObject*` path, :1016-1089) is unreliable
  under Neo → PARK `stackTrace` + ship scopes/variables (which reuse the RELIABLE Neo
  `GetThisInfo`/`GetLocalVariableInfo` arms from children 11/12, NOT the `StackObject*` path).
- Full DAP spec compliance (eval/watch/conditional-bp/threads) is OUT of scope — follow-ups.
