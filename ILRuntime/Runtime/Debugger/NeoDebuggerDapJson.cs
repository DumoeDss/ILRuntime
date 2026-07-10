#if ENABLE_NEO_MODE && DEBUG
// neo-debugger-cli-protocol (child 13): the minimal DAP JSON-RPC message model
// + a hand-rolled JSON reader/writer. Dependency-free (ILRuntime core does not
// reference LitJson / System.Text.Json). DAP messages are a small fixed shape:
//   Request  { seq, type:"request", command, arguments? }
//   Response { seq, type:"response", request_seq, success, message?, body? }
//   Event    { seq, type:"event", event, body? }
//
// Only the ~6 core commands the adapter implements are modeled; the JSON writer
// is general (ordered key list -> object) so the handlers build bodies directly.
//
// Neo-only + DEBUG (compiled out under Legacy).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ILRuntime.Runtime.Debugger
{
    // ===================== minimal JSON =====================
    // A JsonObject is an ordered list of (key, value) pairs. Values are string /
    // number / bool / null / JsonObject / JsonArray. This is JUST enough to emit
    // DAP responses/events with the exact key order the spec/clients expect
    // (Content-Length framing + the body sub-objects). No schema validation.
    public sealed class JsonValue
    {
        // kind: 0=object, 1=array, 2=string, 3=number, 4=bool, 5=null
        public byte Kind;
        public List<KeyValuePair<string, JsonValue>> Obj;  // kind 0
        public List<JsonValue> Arr;                          // kind 1
        public string Str;                                   // kind 2
        public string Num;                                   // kind 3 (raw, invariant)
        public bool Bool;                                    // kind 4

        public static JsonValue Object(params KeyValuePair<string, JsonValue>[] pairs)
        {
            var v = new JsonValue { Kind = 0, Obj = new List<KeyValuePair<string, JsonValue>>(pairs) };
            return v;
        }
        public static JsonValue Object(List<KeyValuePair<string, JsonValue>> pairs)
        {
            return new JsonValue { Kind = 0, Obj = pairs };
        }
        public static JsonValue Array(List<JsonValue> items) { return new JsonValue { Kind = 1, Arr = items }; }
        public static JsonValue Array(params JsonValue[] items) { return new JsonValue { Kind = 1, Arr = new List<JsonValue>(items) }; }
        public static JsonValue String(string s) { return new JsonValue { Kind = 2, Str = s ?? "" }; }
        public static JsonValue Number(long n) { return new JsonValue { Kind = 3, Num = n.ToString(CultureInfo.InvariantCulture) }; }
        public static JsonValue Number(double n) { return new JsonValue { Kind = 3, Num = n.ToString(CultureInfo.InvariantCulture) }; }
        public static JsonValue OfBool(bool b) { return new JsonValue { Kind = 4, Bool = b }; }
        public static readonly JsonValue Null = new JsonValue { Kind = 5 };
        public static JsonValue NullVal() { return Null; }

        public void Write(StringBuilder sb)
        {
            switch (Kind)
            {
                case 0:
                    sb.Append('{');
                    if (Obj != null)
                    {
                        for (int i = 0; i < Obj.Count; i++)
                        {
                            if (i > 0) sb.Append(',');
                            JsonWriter.WriteString(sb, Obj[i].Key);
                            sb.Append(':');
                            Obj[i].Value.Write(sb);
                        }
                    }
                    sb.Append('}');
                    break;
                case 1:
                    sb.Append('[');
                    if (Arr != null)
                    {
                        for (int i = 0; i < Arr.Count; i++)
                        {
                            if (i > 0) sb.Append(',');
                            Arr[i].Write(sb);
                        }
                    }
                    sb.Append(']');
                    break;
                case 2:
                    JsonWriter.WriteString(sb, Str);
                    break;
                case 3:
                    sb.Append(Num);
                    break;
                case 4:
                    sb.Append(Bool ? "true" : "false");
                    break;
                default:
                    sb.Append("null");
                    break;
            }
        }
    }

    static class JsonWriter
    {
        public static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20)
                                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else
                                sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        public static string ToJson(JsonValue v)
        {
            var sb = new StringBuilder();
            v.Write(sb);
            return sb.ToString();
        }

        // Convenience: kv pairs for building an object literal inline.
        public static KeyValuePair<string, JsonValue> KV(string k, JsonValue v) { return new KeyValuePair<string, JsonValue>(k, v); }
        public static KeyValuePair<string, JsonValue> KV(string k, string v) { return new KeyValuePair<string, JsonValue>(k, JsonValue.String(v)); }
        public static KeyValuePair<string, JsonValue> KV(string k, long v) { return new KeyValuePair<string, JsonValue>(k, JsonValue.Number(v)); }
        public static KeyValuePair<string, JsonValue> KV(string k, bool v) { return new KeyValuePair<string, JsonValue>(k, JsonValue.OfBool(v)); }
    }

    // A minimal JSON parser (recursive descent). Used to PARSE incoming DAP
    // requests (initialize/launch/setBreakpoints/stackTrace/scopes/variables/
    // continue/next) -- we only need to pull a few well-known fields out of
    // arguments. Tolerant: ignores trailing/unknown keys.
    sealed class JsonReader
    {
        readonly string s;
        int pos;
        JsonReader(string s) { this.s = s; }

        public static JsonValue Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var r = new JsonReader(json);
            r.SkipWs();
            return r.ParseValue();
        }

        void SkipWs() { while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\n' || s[pos] == '\r')) pos++; }

        JsonValue ParseValue()
        {
            SkipWs();
            if (pos >= s.Length) return JsonValue.Null;
            char c = s[pos];
            if (c == '{') return ParseObject();
            if (c == '[') return ParseArray();
            if (c == '"') return JsonValue.String(ParseStringRaw());
            if (c == 't' || c == 'f') return ParseBool();
            if (c == 'n') { pos += 4; return JsonValue.Null; }
            return ParseNumber();
        }

        JsonValue ParseObject()
        {
            pos++; // {
            var pairs = new List<KeyValuePair<string, JsonValue>>();
            SkipWs();
            if (pos < s.Length && s[pos] == '}') { pos++; return JsonValue.Object(pairs); }
            while (pos < s.Length)
            {
                SkipWs();
                string key = ParseStringRaw();
                SkipWs();
                if (pos < s.Length && s[pos] == ':') pos++;
                var val = ParseValue();
                pairs.Add(new KeyValuePair<string, JsonValue>(key, val));
                SkipWs();
                if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                if (pos < s.Length && s[pos] == '}') { pos++; break; }
                break;
            }
            return JsonValue.Object(pairs);
        }

        JsonValue ParseArray()
        {
            pos++; // [
            var items = new List<JsonValue>();
            SkipWs();
            if (pos < s.Length && s[pos] == ']') { pos++; return JsonValue.Array(items); }
            while (pos < s.Length)
            {
                items.Add(ParseValue());
                SkipWs();
                if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                if (pos < s.Length && s[pos] == ']') { pos++; break; }
                break;
            }
            return JsonValue.Array(items);
        }

        string ParseStringRaw()
        {
            if (pos >= s.Length || s[pos] != '"') return "";
            pos++;
            var sb = new StringBuilder();
            while (pos < s.Length && s[pos] != '"')
            {
                char c = s[pos++];
                if (c == '\\' && pos < s.Length)
                {
                    char e = s[pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (pos + 3 < s.Length)
                            {
                                string hex = s.Substring(pos, 4);
                                int code;
                                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                    sb.Append((char)code);
                                pos += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            if (pos < s.Length && s[pos] == '"') pos++;
            return sb.ToString();
        }

        JsonValue ParseBool()
        {
            if (pos + 3 < s.Length && s[pos] == 't') { pos += 4; return JsonValue.OfBool(true); }
            pos += 5; return JsonValue.OfBool(false);
        }

        JsonValue ParseNumber()
        {
            int start = pos;
            while (pos < s.Length)
            {
                char c = s[pos];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') pos++;
                else break;
            }
            return JsonValue.Number(long.Parse(s.Substring(start, pos - start), CultureInfo.InvariantCulture));
        }

        // ---- typed accessors on a parsed object ----
        public static string GetStr(JsonValue o, string key)
        {
            var v = Get(o, key);
            return v == null || v.Kind != 2 ? null : v.Str;
        }
        public static long? GetNum(JsonValue o, string key)
        {
            var v = Get(o, key);
            if (v == null || v.Kind != 3) return null;
            long n;
            if (long.TryParse(v.Num, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            return null;
        }
        public static bool? GetBool(JsonValue o, string key)
        {
            var v = Get(o, key);
            if (v == null || v.Kind != 4) return null;
            return v.Bool;
        }
        public static JsonValue Get(JsonValue o, string key)
        {
            if (o == null || o.Kind != 0 || o.Obj == null) return null;
            foreach (var p in o.Obj) if (p.Key == key) return p.Value;
            return null;
        }
    }
}
#endif
