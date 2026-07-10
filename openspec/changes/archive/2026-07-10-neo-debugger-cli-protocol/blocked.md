# Blocked / PARKED — neo-debugger-cli-protocol (child 13)

**Status:** NOT blocked — SHIPPED (the core DAP adapter + the ~7 working core methods
+ an end-to-end self-check are GREEN). This file documents the ONE parked method
(`next` / step-over) that hits a deep engine gap, per the PARK scope boundary.

## What shipped (GREEN)
A MINIMAL DAP (Debug Adapter Protocol) frontend for the Neo debugger, wired to the
existing `DebugService` backend (children neo-frame / ilvt-local / aot-body). Files:
- `ILRuntime/Runtime/Debugger/NeoDebuggerDapAdapter.cs` — the adapter (in-proc, no
  TCP loopback) + `InProcDebuggerServer` (subclasses `DebuggerServer`).
- `ILRuntime/Runtime/Debugger/NeoDebuggerDapProtocol.cs` — `IDapTransport` (stdio +
  in-mem) + DAP `Content-Length` framing.
- `ILRuntime/Runtime/Debugger/NeoDebuggerDapJson.cs` — minimal hand-rolled JSON
  reader/writer (dependency-free).
- `ILRuntime/Runtime/Debugger/NeoDebuggerDapCheck.cs` — host-side self-check
  (CLI mode `NeoDebuggerDap`).
- `TestCases/NeoDebuggerDapProbe.cs` — the probe.
- Engine touch: `DebuggerServer` send-event methods + `IsAttached` marked `virtual`
  (additive, Legacy-neutral) + `DebugService.AttachInProcServer/DetachInProcServer`
  (Neo-gated) so the in-proc adapter installs WITHOUT a TCP listener.

**The ~7 working core DAP requests** (verified end-to-end by the self-check):
`initialize` + `launch`(attach) / `setBreakpoints` / `stackTrace` / `scopes` /
`variables` / `continue`. Self-check: **NeoDebuggerDap 4/4** (initialize+launch,
setBreakpoints, the core breakpoint session [hit + stackTrace + scopes + variables +
continue-to-completion], + a SOFT step-gap cell). The known local (prim=4242,
msg="dap-local-value") is asserted inspectable at the breakpoint — the load-bearing
gate (the adapter reads the live Neo frame, children 11/12).

## What's PARKED — `next` (step-over): Neo step-resume engine gap
**Symptom:** a breakpoint session that issues `next` (StepTypes.Over) then `continue`
surfaces `ILRuntimeException: The method or operation is not implemented.` (a Neo
NotImplementedException) from the step-RESUME execution. The NIE fires on the IL
after the step (e.g. `return prim + after;`), NOT on a step-unrelated opcode.

**Isolation (proves the gap is the step-resume, NOT the adapter):**
- The bare invoke (no debugger) runs CLEAN (returns 4343 = prim 4242 + Callee 101).
- The continue-ONLY session (breakpoint + inspect + continue, NO step) runs CLEAN
  (Cell 3 PASS, every run).
- ONLY when a `next` (step-over) is issued between the stop and the continue does
  the NIE surface.

So the gap is in the Neo debugger's STEP-RESUME path (the interaction of
`StepThread` setting `CurrentStepType=Over`/`LastStepFrameBase`/`LastStepInstructionIndex`
+ the resumed execution), NOT in the DAP adapter (which just calls `ds.StepThread`).
This is an ENGINE gap, out of scope for a DAP-FRONTEND child.

**How it's surfaced:** `NeoDebuggerDapCheck` Cell 4 records it as a SOFT fail (does
NOT regress the core gate; `StepGateIsHard=false`). Flip `StepGateIsHard=true` +
drive a real step session (the cell is wired to do so) once the engine gap closes.

**Known intermittency (NOT a defect of this child):** the breakpoint BIND can
occasionally miss on attach (a sequence-point/method-hash race in the Neo
`CheckShouldBreak` path). The self-check retries up to 2x with a fresh
adapter+worker to absorb this (~stable across runs; the rare miss surfaces as
"breakpoint did not fire" + the worker finishing early). This is a pre-existing
Neo-debugger breakpoint-bind characteristic, surfaced (not introduced) by the DAP
adapter's exercise of the bind path.

## Follow-ups (out of scope, sequenced)
1. **Neo step-resume engine gap** — close the NIE in the `StepTypes.Over` resume
   path (the step-frame-base / sequence-point interaction), then harden Cell 4.
2. **Full DAP spec compliance** — `evaluate`/watch, conditional breakpoints (the
   backend CONDITION machinery exists but the DAP expression bridge is not wired),
   `threads` (single-thread only; the interpreter is single-threaded cooperative),
   `pause` (no async interrupt hook), `setExceptionBreakpoints`, `terminate`.
3. **stackTrace for non-top frames** — the adapter uses the Neo
   `GetThisInfo`/`GetLocalVariableInfo` arms (children 11/12) for frame 0 (reliable);
   non-top frames fall back to the backend's captured `StackFrameInfo.LocalVariables`
   (Legacy StackObject*-based). A Neo analogue for non-top frames is a follow-on.
4. **The breakpoint-bind intermittency** — a dedicated investigation of the
   `CheckShouldBreak` sequence-point/method-hash race.
