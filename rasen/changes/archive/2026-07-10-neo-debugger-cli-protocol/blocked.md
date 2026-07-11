# Blocked / PARKED — neo-debugger-cli-protocol (child 13)

**Status:** NOT blocked — SHIPPED (the core DAP adapter + the ~7 working core methods
+ an end-to-end self-check are GREEN). The ONE parked method (`next` / step-over)
was **RESOLVED** by the child-13 follow-up (neo-debugger-step-resume); see the
"RESOLVED" section below. This file is retained as the resolution record.

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
1. **~~Neo step-resume engine gap~~** — **RESOLVED** (see the RESOLVED section
   above). The NIE was NOT a deep step-engine gap; it was the Legacy
   `GetStackFrameInfo`/`AddStackFrameInfoVariables` (`StackObject*`-based) reading
   the Neo compact `byte*` frame in the step-complete `DoBreak` frame capture.
   Closed by the Neo-gated `AddStackFrameInfoVariablesNeo` (byte* slot read,
   mirroring `GetLocalVariableInfo`/`GetThisInfo`). Cell 4 is now a HARD gate
   (`StepGateIsHard=true`).
2. **Full DAP spec compliance** — `evaluate`/watch, conditional breakpoints (the
   backend CONDITION machinery exists but the DAP expression bridge is not wired),
   `threads` (single-thread only; the interpreter is single-threaded cooperative),
   `pause` (no async interrupt hook), `setExceptionBreakpoints`, `terminate`.
3. **stackTrace for non-top frames** — the adapter uses the Neo
   `GetThisInfo`/`GetLocalVariableInfo` arms (children 11/12) for frame 0 (reliable);
   non-top frames fall back to the backend's captured `StackFrameInfo.LocalVariables`
   (now also Neo-populated by `AddStackFrameInfoVariablesNeo`, so the captured
   LocalVariables are correct under Neo too; a direct Neo analogue for non-top
   frames remains a follow-on).
4. **The breakpoint-bind intermittency** — a dedicated investigation of the
   `CheckShouldBreak` sequence-point/method-hash race.

## RESOLVED — `next` (step-over): Neo step-resume (child-13 follow-up)
**The gap was mis-framed.** The "deep step-engine gap" was in fact a SPECIFIC,
tractable NIE in the debugger's frame-capture path (the child-4/8/17/17sub3
"likely a specific NIE, not a deep gap" lesson held -- 5-for-5).

**Root cause (re-audited, fresh eyes):** the NIE site was
`StackObject.ToObject`'s `default` branch (`StackObject.cs:131`), reached from
`DebugService.AddStackFrameInfoVariables` -> `StackObject.ToObject` -- the LEGACY
`StackObject*`-based frame-variable read. Under Neo the frame is a compact
`byte[] Primitives + AutoList ManagedObjects` (NOT a `StackObject[]`), so the
`StackObject*` arithmetic (`Add(basePointer, i)`) reads raw bytes as
`StackObject`s -- the `ObjectType` field lands on an unrecognized value -> the
`default` -> NIE.

**Why ONLY `next` (not breakpoint-hit / continue-only):** the NIE is in
`DoBreak`'s frame capture (`DoBreak` -> `GetStackFrameInfo` ->
`InitializeStackFrameInfo` -> `AddStackFrameInfoVariables`), which runs on BOTH
the breakpoint-hit AND the step-complete. On the breakpoint-hit (IP at
`int after = Callee();`) the raw bytes happened to land on a recognized
`ObjectTypes` value (a coincidence -- the read was garbage but did not throw),
so Cell 3 passed. On the step-complete (IP at `return prim + after;`, a
different byte alignment) the bytes hit the `default` -> NIE. The NIE, thrown
INSIDE `DoBreak` (the step-complete path, `isStep=true`), ABORTED `DoBreak`
BEFORE `intp.Break()` parked the worker -> the step-complete never fired AND
the NIE propagated up the resumed worker. The bare-invoke + continue-only
sessions never reach the step-complete `DoBreak`, so they ran clean -- which is
why the gap APPEARED to be the step-resume engine.

**The fix (Neo-gated, Legacy-neutral):** added
`DebugService.AddStackFrameInfoVariablesNeo(ILIntepreter, StackFrame,
StackFrameInfo, ILMethod)` -- a Neo-aware population of a `StackFrameInfo`'s
`LocalVariables` (args + locals) off the compact `byte*` frame, reusing the
SAME byte* slot dispatch `GetLocalVariableInfo`/`GetThisInfo` use (children
11/12): `ReadNeoLocalValue` for locals/typed params + the absolute-`mStack`-index
read for `this`/reference params. `AddStackFrameInfoVariables` was given a
`StackFrame f` parameter (4 call sites in `InitializeStackFrameInfo` updated) +
an `#if ENABLE_NEO_MODE` early-out that calls the Neo helper; the Neo helper
returns false for non-Neo frames (Legacy falls through unchanged). Each
variable entry is wrapped so a single unreadable slot does NOT abort the
capture (mirrors the per-iteration resilience of the Neo `GetLocalVariableInfo`
arm). No step-engine change was needed -- the `StepTypes.Over` logic in
`CheckShouldBreak` (the `basePointer <= LastStepFrameBase` compare) was already
correct; it simply never got to report its stop because `DoBreak` NIE'd first.

**Verification:** `NeoDebuggerDap` Cell 4 is now a HARD gate
(`StepGateIsHard=true`) that drives a REAL step session: breakpoint -> `next`
(step-over) -> assert the step stops in the SAME frame (`RunToBreakpoint`, NOT
descending into `Callee` = step-IN) -> `continue` -> completion (result 4343 =
prim 4242 + Callee 101). NeoDebuggerDap 4/4 (stable 3/3 re-runs), NeoStep 279/0,
NeoDebuggerFrame 6/6, NeoDebuggerAotBody 10/10. Plain `Debug` build of ILRuntime
+ CLI = 0 errors (the `StackFrame f` signature change is additive; the Neo
helper is `#if ENABLE_NEO_MODE`-gated).
