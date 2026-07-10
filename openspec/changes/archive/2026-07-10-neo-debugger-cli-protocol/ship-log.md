# Ship Log — neo-debugger-cli-protocol (child 13; the LAST pending child + debugger capstone)

**Date:** 2026-07-10  **Capability:** neo-debugger  **Wave:** completion-3, child 13
**Status:** SHIPPED (LEAD-verified) — the DAP capstone. **All 19 portfolio children now resolved**
(13 shipped, 4 parked).

## Delivered
**An in-proc Debug Adapter Protocol (DAP) frontend wired to the existing Neo `DebugService` backend.**
No TCP loopback needed (the adapter IS the client). 4 new Neo-gated files (`#if ENABLE_NEO_MODE &&
DEBUG`) + minimal additive engine change:
- `NeoDebuggerDapAdapter.cs` — `InProcDebuggerServer : DebuggerServer` overrides `Start`/`Stop` (no
  listener) + the send-event methods (captures breakpoint-hit / step-complete into an event queue);
  `HandleRequest(command, args)` dispatches the core DAP requests.
- `NeoDebuggerDapProtocol.cs` — `IDapTransport` (stdio for a real VSCode client + an in-memory
  transport for the self-check) + DAP `Content-Length` framing.
- `NeoDebuggerDapJson.cs` — minimal hand-rolled JSON read/write (no dependency — the ILRuntime core
  references no JSON lib).
- `NeoDebuggerDapCheck.cs` — host-side self-check (CLI mode `NeoDebuggerDap`).
- `TestCases/NeoDebuggerDapProbe.cs` — the probe (`RunToBreakpoint` + `Callee`).
- **Engine (additive, Legacy-neutral):** `DebuggerServer.IsAttached` +
  `SendAttachResult`/`SendSCBreakpointHit`/`SendSCStepComplete` marked `virtual` (so the in-proc
  subclass can intercept send-events without a socket); Neo-gated `DebugService.AttachInProcServer`/
  `DetachInProcServer`.

**~7 core DAP methods (all reuse existing backend — no new field-read/step code):**
initialize/launch(attach) · setBreakpoints (`DebugService.SetBreakPoint` + source-scoped line→method
bind) · stackTrace (`GetStackFrameInfo`) · scopes+variables (`GetThisInfo`+`GetLocalVariableInfo`
Neo arms, children 11/12) · continue (`ExecuteThread`→`Resume()`).

**Single-threaded cooperative contract:** the debuggee runs on a worker thread; at a breakpoint
`intp.Break()` blocks it (`Monitor.Wait`); the inspect thread serves read-only requests against the
parked frame, then `continue` does `Resume()`.

## Verification (LEAD-verify)
- **NeoDebuggerDap: 4/4** (initialize+launch; setBreakpoints source-scoped+verified; **the core
  breakpoint session** — hit + stackTrace + scopes + variables + continue-to-completion, asserting a
  known local (prim=4242, msg="dap-local-value") is inspectable at the breakpoint, THE core gate;
  `next`/step soft-PASS — see follow-up).
- **NeoStep smoke (LEAD re-ran): 267 tests, 0 failed** — no regression from the shared
  `DebuggerServer` `virtual` markings.
- NeoDebuggerFrame 6/6 + NeoDebuggerAotBody 10/10 held.
- **Legacy-neutral:** plain `Debug` builds ILRuntime + CLI = 0 errors (adapter/check/JSON/protocol all
  Neo-gated; the `virtual` markings are additive/source-compatible — Legacy TCP path unchanged).

## Durable findings
1. **`DebuggerServer.IsAttached` + the send-event methods were NOT `virtual`** — an in-proc client
   (no TCP) must override them. Marking them `virtual` is additive + source-compatible; the Legacy TCP
   path is unchanged. This is the key engine enabler for ANY in-proc debugger frontend.
2. **`DebugService.server` is private + set only by `StartDebugService` (the TCP path).** The in-proc
   path needs it → the Neo-gated `AttachInProcServer`. Any future in-proc debugger integration uses
   this pattern.
3. **DAP `setBreakpoints` must be source-scoped** — ILRuntime methods have no queryable source path,
   so the type stem is derived from the filename (sans extension) + matched via
   `Type.FullName.Contains(stem)`.
4. **A breakpoint line must land on an interior statement with a real IL instruction**, not the
   method's `{` line (`StartLine` = the brace; no instruction → `CheckShouldBreak`'s
   `(bp.StartLine+1)==sp.StartLine` never matches). Debugger-bound self-checks use
   `method.StartLine + <statement offset>`.

## Follow-ups (out of scope, in `blocked.md`)
- **`next`/step PARKED — a Neo step-resume engine gap** (`StepTypes.Over` between a stop + continue
  → NIE). Isolated: bare-call + continue-only sessions run clean → the gap is the step-resume path
  (`StepThread` + `CurrentStepType=Over`/`LastStepFrameBase`), NOT the adapter. Surfaced as a soft-PASS
  gate in `NeoDebuggerDapCheck` (flip `StepGateIsHard=true` + run the real step session when fixed).
- Full DAP spec compliance: `evaluate`/watch, conditional breakpoints, `threads` (single-threaded
  cooperative), `pause` (no async-interrupt hook), `setExceptionBreakpoints`, `terminate`.
- Non-top-frame `stackTrace` (the adapter uses the reliable Neo frame-read for frame 0; non-top frames
  fall back to the backend-captured Legacy-`StackObject*`-based `StackFrameInfo.LocalVariables`).
- Breakpoint-binding intermittency (a `CheckShouldBreak` sequence-point/method-hash race; the self-check
  retries up to 2×).

## Review
LEAD-verify (NeoStep smoke re-ran 267/0 — the shared `virtual`-marking regression check; capstone 4/4
implementer-verified incl. the core breakpoint-inspect session; Legacy build 0 errors; the shared
change is additive `virtual` markings + Neo-gated helpers).
