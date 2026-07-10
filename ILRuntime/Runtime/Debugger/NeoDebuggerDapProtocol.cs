#if ENABLE_NEO_MODE && DEBUG
// neo-debugger-cli-protocol (child 13): a MINIMAL Debug Adapter Protocol (DAP)
// frontend for the Neo debugger. The Neo DebugService BACKEND already exists
// (breakpoints / single-step / frame+variable inspection, children neo-frame /
// ilvt-local 11 / aot-body 12). This is the FRONTEND: a DAP adapter so an
// external client (VSCode, over DAP/stdio) can drive a Neo debug session via the
// ~6 core requests (initialize / launch / setBreakpoints / stackTrace / scopes /
// variables / continue / next).
//
// DESIGN (see design.md): the adapter attaches to DebugService IN-PROC (no TCP
// loopback -- the DebuggerServer binds a real listener). The DAP JSON-RPC layer
// (THIS file) is transport-agnostic: an IDapTransport moves the framed JSON
// bytes. The stdio transport lets VSCode drive it; the in-mem transport lets the
// self-check (NeoDebuggerDapCheck) drive the SAME handlers without a fork. DAP
// framing is the spec `Content-Length: N\r\n\r\n<JSON>`.
//
// JSON is hand-rolled (a minimal writer + reader) to keep ILRuntime dependency-
// free (LitJson is a separate project the core does not reference). DAP messages
// are a small, fixed shape.
//
// Neo-only + DEBUG. The adapter subclass of DebuggerServer + this protocol are
// compiled out under Legacy.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace ILRuntime.Runtime.Debugger
{
    // ===================== transport abstraction =====================
    // A DAP frame is `Content-Length: N\r\n\r\n` + N UTF-8 JSON bytes. The
    // transport moves those bytes; the JSON-RPC layer (below) parses them.
    public interface IDapTransport : IDisposable
    {
        // Block until a full frame is available, return its JSON payload (null on EOF/close).
        string ReadFrame();
        // Write a JSON payload as a single framed message.
        void WriteFrame(string json);
    }

    // stdio transport (for a real external DAP client like VSCode). Reads
    // Console.OpenStandardInput, writes Console.OpenStandardOutput. NOT used by
    // the in-proc self-check (which uses InMemDapTransport).
    sealed class StdioDapTransport : IDapTransport
    {
        readonly Stream input;
        readonly Stream output;
        public StdioDapTransport()
        {
            input = Console.OpenStandardInput();
            output = Console.OpenStandardOutput();
        }

        public string ReadFrame()
        {
            int contentLength = -1;
            // read header lines until the blank line
            while (true)
            {
                string line = ReadLineCrLf(input);
                if (line == null) return null;          // EOF
                if (line.Length == 0) break;            // blank line -> body next
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line.Substring("Content-Length:".Length).Trim(), out contentLength);
                }
                // other headers (Content-Type) ignored
            }
            if (contentLength < 0) contentLength = 0;
            var buf = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = input.Read(buf, read, contentLength - read);
                if (n <= 0) break;
                read += n;
            }
            return Encoding.UTF8.GetString(buf, 0, read);
        }

        public void WriteFrame(string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
            output.Write(header, 0, header.Length);
            output.Write(body, 0, body.Length);
            output.Flush();
        }

        public void Dispose()
        {
            try { input.Dispose(); } catch { }
            try { output.Dispose(); } catch { }
        }

        // Read one CRLF-terminated line (no trailing CR/LF). null on EOF.
        static string ReadLineCrLf(Stream s)
        {
            var sb = new StringBuilder();
            bool sawCR = false;
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0)
                    return sb.Length == 0 ? null : sb.ToString();
                if (b == '\r') { sawCR = true; continue; }
                if (b == '\n')
                {
                    // line complete (the CR, if any, was consumed)
                    return sb.ToString();
                }
                if (sawCR) { sb.Append('\r'); sawCR = false; }
                sb.Append((char)b);
            }
        }
    }

    // In-memory transport for the host-side self-check. Two paired transports
    // form a pipe: the "client" side writes requests + reads responses/events;
    // the "adapter" side reads requests + writes responses/events. The self-check
    // drives the client side against a NeoDebuggerDapAdapter wired to a real
    // DebugService + a probe running on a worker thread.
    sealed class InMemDapTransport : IDapTransport
    {
        // One InMemDapTransport is one END of a pipe. Frames written here appear
        // (in order) on the paired end's ReadFrame.
        readonly Queue<string> outgoing = new Queue<string>();
        readonly Queue<string> incoming = new Queue<string>();
        InMemDapTransport peer;
        readonly object lockObj = new object();
        bool closed;
        public bool IsClosed { get { return closed; } }

        InMemDapTransport() { }

        // Create a connected pair (item1 <-> item2).
        public static Tuple<InMemDapTransport, InMemDapTransport> CreatePair()
        {
            var a = new InMemDapTransport();
            var b = new InMemDapTransport();
            a.peer = b; b.peer = a;
            return Tuple.Create(a, b);
        }

        public string ReadFrame()
        {
            lock (lockObj)
            {
                while (incoming.Count == 0 && !closed)
                    Monitor.Wait(lockObj);
                if (incoming.Count > 0)
                    return incoming.Dequeue();
                return null; // closed + empty -> EOF
            }
        }

        public void WriteFrame(string json)
        {
            lock (peer.lockObj)
            {
                peer.incoming.Enqueue(json);
                Monitor.Pulse(peer.lockObj);
            }
        }

        public void Close()
        {
            lock (lockObj) { closed = true; Monitor.PulseAll(lockObj); }
        }

        public void Dispose() { Close(); }
    }
}
#endif
