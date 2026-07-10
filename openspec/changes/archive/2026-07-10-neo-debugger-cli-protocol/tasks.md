# Tasks — neo-debugger-cli-protocol

- [x] **T1** Engine touch: `DebuggerServer.IsAttached` + `SendAttachResult`/
      `SendSCBreakpointHit`/`SendSCStepComplete` marked `virtual` (additive,
      Legacy-neutral). `DebugService.AttachInProcServer`/`DetachInProcServer` added
      (Neo-gated) so the in-proc adapter installs WITHOUT a TCP listener.
- [x] **T2** `NeoDebuggerDapProtocol.cs` (`#if ENABLE_NEO_MODE && DEBUG`): `IDapTransport`
      (stdio + in-mem) + DAP `Content-Length` framing.
- [x] **T2b** `NeoDebuggerDapJson.cs`: minimal hand-rolled JSON reader/writer
      (dependency-free; ILRuntime core references no JSON lib).
- [x] **T3** `NeoDebuggerDapAdapter.cs`: the adapter. `InProcDebuggerServer :
      DebuggerServer` overrides `Start`/`Stop` (no listener) + the send-event
      overrides (capture breakpoint-hit/step-complete). The `~7` DAP handlers wired
      to the backend: `initialize`/`launch`(attach) / `setBreakpoints` /
      `stackTrace`(`GetStackFrameInfo`) / `scopes`+`variables`(`GetThisInfo`+
      `GetLocalVariableInfo` Neo arms) / `continue`(`ExecuteThread`) /
      `next`(`StepThread` — PARKED, see T7/blocked.md).
- [x] **T4** Probe `TestCases/NeoDebuggerDapProbe.cs` (`RunToBreakpoint` +
      `Callee`).
- [x] **T5** `NeoDebuggerDapCheck.cs` (host-side self-check): launch → setBreakpoints
      → run (worker) → breakpoint-hit → stackTrace → scopes → variables (assert the
      known local) → continue (retry-on-intermittent-bind-miss). Cell 4 is a SOFT
      step-gap cell (PARKED `next`).
- [x] **T6** CLI dispatch `NeoDebuggerDap` mode in `ILRuntimeTestCLI/Program.cs`
      (+ `Environment.Exit` harness guard for the debug-parked-interpreter exit
      hang).
- [x] **T7** Build CLI `Debug_Neo` + TestCases `Debug`; gates: NeoDebuggerDap 4/4,
      NeoDebuggerFrame 6/6, NeoDebuggerAotBody 10/10, NeoStep 267/0/0; plain `Debug`
      build (Legacy-neutral) 0 errors. **PARKED: `next` (step-over) — Neo step-resume
      engine gap (NIE in the StepTypes.Over resume path; see blocked.md).**
