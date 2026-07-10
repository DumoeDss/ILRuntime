#if ENABLE_NEO_MODE && DEBUG
// neo-debugger-cli-protocol (child 13): the DAP adapter — a CLI/in-proc Debug
// Adapter Protocol frontend for the Neo debugger.
//
// The Neo DebugService BACKEND is complete (breakpoints, single-step, frame+
// variable inspection — children neo-frame / ilvt-local / aot-body). This is the
// FRONTEND: ~6 core DAP requests wired to that backend, so an external DAP client
// (VSCode, over stdio) can drive a Neo debug session.
//
// The adapter attaches to DebugService IN-PROC (no TCP loopback). It subclasses
// DebuggerServer (InProcDebuggerServer) and overrides Start/Stop (no listener) +
// the send-event methods (SendSCBreakpointHit / SendSCStepComplete) to CAPTURE
// the breakpoint-hit / step-complete events into a queue — the adapter IS the
// client. IsAttached is driven true so CheckShouldBreak (DebugService.cs:817)
// proceeds. The client->server requests (bind-breakpoint / execute / step) go
// DIRECTLY to the DebugService internals (SetBreakPoint / ExecuteThread /
// StepThread) — no socket round-trip.
//
// The single-threaded-cooperative contract (ILIntepreter.Break docs :56-71): the
// interpreter runs on a WORKER thread; on a breakpoint it calls DoBreak ->
// SendSCBreakpointHit -> intp.Break() which BLOCKS the worker (Monitor.Wait).
// The adapter serves READ-ONLY requests (stackTrace/scopes/variables) against
// the parked frame (safe), then `continue`/`next` call ExecuteThread/StepThread
// which Resume()s the worker.
//
// The DAP JSON-RPC layer is transport-agnostic (IDapTransport, see
// NeoDebuggerDapProtocol.cs): stdio for a real client, in-mem for the self-check.
//
// Neo-only + DEBUG.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.Runtime.Debugger.Protocol;
using ILRuntimeException = ILRuntime.Runtime.Intepreter.ILRuntimeException;

namespace ILRuntime.Runtime.Debugger
{
    // ===================== the adapter =====================
    // Owns a DebugService + an InProcDebuggerServer (the "client"). Serves DAP
    // requests off an IDapTransport. Thread model: RunSession() pumps the
    // transport on its own thread; requests are dispatched synchronously.
    public sealed class NeoDebuggerDapAdapter
    {
        readonly DebugService ds;
        readonly ILRuntime.Runtime.Enviorment.AppDomain domain;
        InProcDebuggerServer server;

        // the live breakpoint/step event (captured by the server overrides).
        // One at a time (single-threaded interpreter). Set on breakpoint-hit /
        // step-complete, cleared when execution resumes.
        volatile DapStoppedEvent currentStop;
        readonly object stopLock = new object();

        // seq counters for messages WE emit (responses + events + our own seq).
        int nextSeq = 1;

        volatile bool sessionOver;

        public NeoDebuggerDapAdapter(ILRuntime.Runtime.Enviorment.AppDomain domain)
        {
            this.domain = domain;
            this.ds = domain.DebugService;
        }

        // ----- attach (initialize/launch) -----
        // Create the in-proc server, install it into DebugService (so
        // CheckShouldBreak sees `server != null && server.IsAttached`), + mark
        // attached. Returns null on success, an error string otherwise.
        public string Attach()
        {
            server = new InProcDebuggerServer(ds, this);
            var err = server.Start(false);
            if (err != null) return err;
            ds.AttachInProcServer(server);
            server.MarkAttached();
            return null;
        }

        // Detach (disconnect): tear down the in-proc server.
        public void Detach()
        {
            if (server != null) server.Stop();
            ds.DetachInProcServer();
        }

        // ----- the breakpoint-hit / step-complete capture (called by the server) -----
        internal void OnBreakpointHit(int intpHash, int bpHash, KeyValuePair<int, StackFrameInfo[]>[] frames)
        {
            lock (stopLock)
            {
                currentStop = new DapStoppedEvent { Reason = "breakpoint", ThreadId = intpHash, AllFrames = frames };
                Monitor.PulseAll(stopLock);
            }
        }
        internal void OnStepComplete(int intpHash, KeyValuePair<int, StackFrameInfo[]>[] frames)
        {
            lock (stopLock)
            {
                currentStop = new DapStoppedEvent { Reason = "step", ThreadId = intpHash, AllFrames = frames };
                Monitor.PulseAll(stopLock);
            }
        }
        internal void OnThreadExited() { sessionOver = true; }

        // Block up to timeoutMs for the next stop event (breakpoint/step).
        public DapStoppedEvent WaitForStop(int timeoutMs)
        {
            lock (stopLock)
            {
                if (currentStop != null) return currentStop;
                if (sessionOver) return null;
                Monitor.Wait(stopLock, timeoutMs);
                return currentStop;
            }
        }
        void ClearStop()
        {
            lock (stopLock) { currentStop = null; }
        }

        public bool SessionOver { get { return sessionOver; } }

        // ===================== DAP request dispatch =====================
        // Process one parsed request; produce the Response body (a JsonValue, or
        // null for an empty body). Throws to signal a failure (caller wraps it).
        // command is one of: initialize/launch/setBreakpoints/stackTrace/scopes/
        // variables/continue/next/disconnect. Returns the response body.
        public JsonValue HandleRequest(string command, JsonValue args)
        {
            switch (command)
            {
                case "initialize": return HandleInitialize(args);
                case "launch": return HandleLaunch(args);
                case "attach": return HandleLaunch(args);
                case "setBreakpoints": return HandleSetBreakpoints(args);
                case "stackTrace": return HandleStackTrace(args);
                case "scopes": return HandleScopes(args);
                case "variables": return HandleVariables(args);
                case "continue": return HandleContinue(args);
                case "next": return HandleNext(args);
                case "disconnect": sessionOver = true; return null;
                default:
                    throw new DapErrorException("unknown command: " + command);
            }
        }

        JsonValue HandleInitialize(JsonValue args)
        {
            // advertise the capabilities we support (the ~6 core). DAP clients
            // gate their requests on these.
            return JsonValue.Object(
                JsonWriter.KV("supportsConfigurationDoneRequest", false),
                JsonWriter.KV("supportsEvaluateForHovers", false),
                JsonWriter.KV("supportsStepBack", false),
                JsonWriter.KV("supportsConditionalBreakpoints", false),
                JsonWriter.KV("supportsHitConditionalBreakpoints", false),
                JsonWriter.KV("supportsExceptionInfoRequest", false)
            );
        }

        JsonValue HandleLaunch(JsonValue args)
        {
            string err = Attach();
            if (err != null) throw new DapErrorException("attach failed: " + err);
            return null;
        }

        // setBreakpoints: DAP sends {source:{path}, breakpoints:[{line}]}.
        // We resolve line -> method via the SAME bind logic the TCP server uses
        // (find the ILMethod whose StartLine..EndLine contains the 1-based line,
        // then DebugService.SetBreakPoint at that line). Returns verified bps.
        JsonValue HandleSetBreakpoints(JsonValue args)
        {
            var bps = JsonReader.Get(args, "breakpoints");
            // source.path -> restrict the line-search to methods declared in
            // THAT source (DAP scopes setBreakpoints to one file). ILRuntime
            // methods don't carry a queryable source path, so we derive a type
            // stem from the source filename (basename minus extension) and match
            // it against the declaring type's FullName -- the conventional
            // one-type-per-file layout. null stem = search ALL types (legacy).
            string srcPath = JsonReader.GetStr(JsonReader.Get(args, "source"), "path");
            string srcStem = null;
            if (!string.IsNullOrEmpty(srcPath))
            {
                string bn = System.IO.Path.GetFileNameWithoutExtension(srcPath);
                if (!string.IsNullOrEmpty(bn)) srcStem = bn;
            }
            var results = new List<JsonValue>();
            if (bps != null && bps.Arr != null)
            {
                foreach (var bp in bps.Arr)
                {
                    long? line = JsonReader.GetNum(bp, "line");
                    if (!line.HasValue) { results.Add(BreakpointResult(0, false, "no line")); continue; }
                    int startLine = (int)line.Value - 1; // backend expects 0-based (StartLine compare sp.StartLine at :862)
                    var found = FindMethodByLine(startLine, srcStem);
                    if (found == null) { results.Add(BreakpointResult(line.Value, false, "code not found")); continue; }
                    int bpHash = System.Threading.Interlocked.Increment(ref nextSeq) + 100000;
                    var cond = new BreakpointCondition { Style = BreakpointConditionStyle.None };
                    ds.SetBreakPoint(found.GetHashCode(), bpHash, startLine, true, cond, null);
                    results.Add(BreakpointResult(line.Value, true, null));
                }
            }
            return JsonValue.Object(JsonWriter.KV("breakpoints", JsonValue.Array(results)));
        }

        static JsonValue BreakpointResult(long line, bool ok, string msg)
        {
            var pairs = new List<KeyValuePair<string, JsonValue>> {
                JsonWriter.KV("verified", ok),
                JsonWriter.KV("line", line),
            };
            if (msg != null) pairs.Add(JsonWriter.KV("message", msg));
            return JsonValue.Object(pairs);
        }

        // Find an ILMethod whose StartLine..EndLine contains startLine (0-based,
        // matching the backend's :862 compare `i.StartLine + 1 == sp.StartLine`).
        // Mirrors DebuggerServer.TryBindBreakpoint's line window (:491). If
        // srcStem is non-null, restrict to types whose FullName contains the stem
        // (the source-file basename, e.g. "NeoDebuggerDapProbe"); otherwise the
        // first method (any type) whose window contains the line wins.
        ILMethod FindMethodByLine(int startLine, string srcStem)
        {
            foreach (var kv in domain.LoadedTypes)
            {
                if (!(kv.Value is ILType it)) continue;
                if (srcStem != null && !it.FullName.Contains(srcStem)) continue;
                foreach (var m in it.GetMethods())
                {
                    if (!(m is ILMethod ilm)) continue;
                    if (ilm.StartLine <= startLine + 1 && ilm.EndLine >= startLine + 1)
                        return ilm;
                }
            }
            return null;
        }

        // The stopped event carries the frames for ALL interpreter threads; pick
        // the stopped thread's frame array.
        StackFrameInfo[] StoppedFrames()
        {
            var stop = currentStop;
            if (stop == null || stop.AllFrames == null) return null;
            foreach (var kv in stop.AllFrames)
                if (kv.Key == stop.ThreadId) return kv.Value;
            return null;
        }

        // stackTrace: return the call stack for the stopped thread's frames.
        JsonValue HandleStackTrace(JsonValue args)
        {
            long? threadId = JsonReader.GetNum(args, "threadId");
            var frames = StoppedFrames();
            var outFrames = new List<JsonValue>();
            if (frames != null)
            {
                // top frame first (DAP frame 0 = innermost); the backend returns
                // frames innermost-first already (GetStackFrameInfo :1094).
                for (int i = 0; i < frames.Length; i++)
                {
                    var f = frames[i];
                    outFrames.Add(JsonValue.Object(
                        JsonWriter.KV("id", (long)(stopFrameBaseId + i)),
                        JsonWriter.KV("name", f.MethodName ?? "<unknown>"),
                        JsonWriter.KV("source", JsonValue.Object(JsonWriter.KV("path", f.DocumentName ?? ""))),
                        JsonWriter.KV("line", (long)(f.StartLine + 1)),
                        JsonWriter.KV("column", (long)(f.StartColumn + 1)),
                        JsonWriter.KV("endLine", (long)(f.EndLine + 1)),
                        JsonWriter.KV("endColumn", (long)(f.EndColumn + 1))
                    ));
                }
            }
            return JsonValue.Object(
                JsonWriter.KV("stackFrames", JsonValue.Array(outFrames)),
                JsonWriter.KV("totalFrames", (long)outFrames.Count)
            );
        }
        const int stopFrameBaseId = 700000;

        // scopes + variables: DAP `scopes(frameId)` -> [{name,variablesReference,
        // presentationHint}]; `variables(variablesReference)` -> the vars.
        // We use a variablesReference RANGE: locals = base+frameIdx+1, this =
        // base+frameIdx+1 + OFFSET_THIS. A non-zero variablesReference means
        // "expandable"; the variables() handler re-derives the frame from it.
        const int varRefLocalsBase = 1000000; // + (frameIndex+1)
        const int varRefThisOffset = 500000;  // this = varRefLocalsBase + (frameIndex+1) + varRefThisOffset

        JsonValue HandleScopes(JsonValue args)
        {
            long? frameId = JsonReader.GetNum(args, "frameId");
            int frameIdx = frameId.HasValue ? (int)(frameId.Value - stopFrameBaseId) : 0;
            if (frameIdx < 0) frameIdx = 0;
            int localsRef = varRefLocalsBase + frameIdx + 1;
            int thisRef = localsRef + varRefThisOffset;
            var scopes = JsonValue.Array(
                JsonValue.Object(
                    JsonWriter.KV("name", "Locals"),
                    JsonWriter.KV("variablesReference", (long)localsRef),
                    JsonWriter.KV("expensive", false),
                    JsonWriter.KV("presentationHint", "locals")
                ),
                JsonValue.Object(
                    JsonWriter.KV("name", "This"),
                    JsonWriter.KV("variablesReference", (long)thisRef),
                    JsonWriter.KV("expensive", false),
                    JsonWriter.KV("presentationHint", "this")
                )
            );
            return JsonValue.Object(JsonWriter.KV("scopes", scopes));
        }

        JsonValue HandleVariables(JsonValue args)
        {
            long? refNum = JsonReader.GetNum(args, "variablesReference");
            var outVars = new List<JsonValue>();
            if (!refNum.HasValue) return JsonValue.Object(JsonWriter.KV("variables", JsonValue.Array(outVars)));
            bool isThis = refNum.Value >= varRefLocalsBase + varRefThisOffset;
            int frameIdx = isThis ? (int)(refNum.Value - varRefLocalsBase - varRefThisOffset - 1)
                                  : (int)(refNum.Value - varRefLocalsBase - 1);
            if (frameIdx < 0) frameIdx = 0;
            var frames = StoppedFrames();
            if (frames == null || frameIdx >= frames.Length)
                return JsonValue.Object(JsonWriter.KV("variables", JsonValue.Array(outVars)));

            // We need the LIVE interpreter for the Neo GetThisInfo/GetLocalVariableInfo
            // arms (children 11/12). Resolve it from the stopped thread.
            var intp = StoppedInterpreter();
            if (intp == null)
                return JsonValue.Object(JsonWriter.KV("variables", JsonValue.Array(outVars)));

            // IMPORTANT: the Neo frame read is reliable for the TOP frame
            // (GetThisInfo/GetLocalVariableInfo read Stack.Frames.Peek()). For
            // non-top frames the backend has no Neo analogue (the Legacy
            // GetStackFrameInfo path is StackObject*-based). So for frameIdx>0 we
            // fall back to the StackFrameInfo.LocalVariables the backend already
            // captured at the stop (best-effort); only frameIdx==0 uses the Neo
            // arms. This is a documented scope boundary (design.md).
            if (frameIdx == 0)
            {
                string info = isThis ? SafeGetThisInfo(intp) : SafeGetLocalInfo(intp);
                outVars = ParseInfoAsVariables(info);
            }
            else
            {
                var locals = frames[frameIdx].LocalVariables;
                if (locals != null)
                    foreach (var v in locals)
                        outVars.Add(VarEntry(v.Name, v.Value, v.TypeName));
            }
            return JsonValue.Object(JsonWriter.KV("variables", JsonValue.Array(outVars)));
        }

        static List<JsonValue> ParseInfoAsVariables(string info)
        {
            var list = new List<JsonValue>();
            if (string.IsNullOrEmpty(info)) return list;
            // The Neo arms render "Type name = value, ..." (comma-separated, 3 per
            // line). Split on the field separator and surface each as a variable.
            string[] parts = info.Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length == 0) continue;
                int eq = t.IndexOf('=');
                if (eq < 0) { list.Add(VarEntry(t, "", "")); continue; }
                string lhs = t.Substring(0, eq).Trim();
                string val = t.Substring(eq + 1).Trim();
                // lhs is "Type name" -- split last token as name, rest as type
                int sp = lhs.LastIndexOf(' ');
                string name = sp >= 0 ? lhs.Substring(sp + 1) : lhs;
                string type = sp >= 0 ? lhs.Substring(0, sp) : "";
                list.Add(VarEntry(name, val, type));
            }
            return list;
        }

        static JsonValue VarEntry(string name, string value, string type)
        {
            return JsonValue.Object(
                JsonWriter.KV("name", name ?? ""),
                JsonWriter.KV("value", (value ?? "null").Trim('"')),
                JsonWriter.KV("type", type ?? ""),
                JsonWriter.KV("variablesReference", 0L)
            );
        }

        ILRuntime.Runtime.Intepreter.ILIntepreter StoppedInterpreter()
        {
            var stop = currentStop;
            if (stop == null) return null;
            if (domain.Intepreters.TryGetValue(stop.ThreadId, out var intp))
                return intp;
            return null;
        }

        string SafeGetThisInfo(ILRuntime.Runtime.Intepreter.ILIntepreter intp)
        {
            try { return ds.GetThisInfo(intp); }
            catch (Exception ex) { return "<this read error: " + ex.GetType().Name + ">"; }
        }
        string SafeGetLocalInfo(ILRuntime.Runtime.Intepreter.ILIntepreter intp)
        {
            try { return ds.GetLocalVariableInfo(intp); }
            catch (Exception ex) { return "<locals read error: " + ex.GetType().Name + ">"; }
        }

        // continue: resume ALL interpreters (the backend ExecuteThread does this).
        JsonValue HandleContinue(JsonValue args)
        {
            ClearStop();
            var stop = currentStop;
            // ExecuteThread resumes all interpreters (DebugService.cs:746). It
            // needs a thread hash but ignores it (resumes ALL).
            ds.ExecuteThread(0);
            return JsonValue.Object(JsonWriter.KV("allThreadsContinued", true));
        }

        // next: step-over the stopped thread.
        JsonValue HandleNext(JsonValue args)
        {
            var stop = currentStop;
            ClearStop();
            ds.StepThread(stop != null ? stop.ThreadId : 0, StepTypes.Over);
            return null;
        }

        // ===================== session pump (stdio / transport-agnostic) =====================
        // Read framed requests, dispatch, write framed responses + events. Runs
        // until disconnect / EOF. NOT used by the self-check (which drives
        // HandleRequest directly via the in-mem transport).
        public void RunSession(IDapTransport transport)
        {
            while (!sessionOver)
            {
                string json;
                try { json = transport.ReadFrame(); }
                catch (Exception) { break; }
                if (json == null) break; // EOF
                JsonValue msg;
                try { msg = JsonReader.Parse(json); }
                catch (Exception) { continue; }
                if (msg == null || msg.Kind != 0) continue;

                string type = JsonReader.GetStr(msg, "type");
                if (type != "request") continue;
                string command = JsonReader.GetStr(msg, "command");
                var args = JsonReader.Get(msg, "arguments");

                int reqSeq = (int)(JsonReader.GetNum(msg, "seq") ?? 0);
                JsonValue body = null;
                string errMsg = null;
                try { body = HandleRequest(command, args); }
                catch (DapErrorException e) { errMsg = e.Message; }
                catch (Exception e) { errMsg = e.GetType().Name + ": " + e.Message; }

                var resp = JsonValue.Object(
                    JsonWriter.KV("seq", (long)nextSeq++),
                    JsonWriter.KV("type", "response"),
                    JsonWriter.KV("request_seq", (long)reqSeq),
                    JsonWriter.KV("success", errMsg == null),
                    JsonWriter.KV("command", command ?? "")
                );
                if (errMsg != null) resp.Obj.Add(JsonWriter.KV("message", errMsg));
                if (body != null) resp.Obj.Add(new KeyValuePair<string, JsonValue>("body", body));
                transport.WriteFrame(JsonWriter.ToJson(resp));
            }
            try { transport.Dispose(); } catch { }
        }
    }

    // A captured stop event (breakpoint-hit or step-complete).
    public sealed class DapStoppedEvent
    {
        public string Reason;      // "breakpoint" | "step"
        public int ThreadId;
        public KeyValuePair<int, StackFrameInfo[]>[] AllFrames;
    }

    // Signal a DAP-level error (becomes a `success:false` response).
    sealed class DapErrorException : Exception
    {
        public DapErrorException(string msg) : base(msg) { }
    }

    // ===================== the in-proc "server" (the client is the adapter) =====================
    // Subclasses DebuggerServer: Start/Stop do NOT bind a listener; the send-event
    // overrides capture breakpoint-hit / step-complete into the adapter. IsAttached
    // is overridden to return `attached` (the base property checks the null
    // clientSocket), so CheckShouldBreak (DebugService.cs:817) proceeds.
    sealed class InProcDebuggerServer : DebuggerServer
    {
        readonly NeoDebuggerDapAdapter adapter;
        volatile bool attached;
        public InProcDebuggerServer(DebugService ds, NeoDebuggerDapAdapter adapter) : base(ds) { this.adapter = adapter; }

        public override string Start(bool boardcastDebuggerInfo)
        {
            // No TCP listener, no UDP broadcast, no network thread. We are the
            // in-proc client.
            return null;
        }
        public override void Stop()
        {
            attached = false;
            adapter.OnThreadExited();
        }
        public void MarkAttached() { attached = true; }
        public override bool IsAttached { get { return attached; } }

        // The send-event overrides: the base would serialize to a (null) socket.
        // We capture the events into the adapter instead (NO serialization).
        internal override void SendSCBreakpointHit(int intpHash, int bpHash, KeyValuePair<int, StackFrameInfo[]>[] info, string error = "")
        {
            adapter.OnBreakpointHit(intpHash, bpHash, info);
        }
        internal override void SendSCStepComplete(int intpHash, KeyValuePair<int, StackFrameInfo[]>[] info)
        {
            adapter.OnStepComplete(intpHash, info);
        }
        internal override void SendAttachResult()
        {
            // The TCP server sends an attach-ack here; the in-proc adapter needs
            // none (Attach() returns synchronously). No-op.
        }
    }
}
#endif
