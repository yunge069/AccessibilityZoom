// MIT licensed Reticly component. Copyright (c) 2026 Jonah.
// Source: https://github.com/corund207/Reticly/blob/ea9364b5ac090106db02cfdf6e5be288c31e1ae3/src/Core/Json.cs
// License: Assets/ThirdParty/Reticly-LICENSE.txt
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Reticly.Core
{
    /// <summary>
    /// Minimal JSON reader/writer. Objects become Dictionary&lt;string, object&gt; (insertion ordered),
    /// arrays become List&lt;object&gt;, numbers become double, plus string/bool/null.
    /// Crosshair models are kept as raw JSON trees so that every field from Crosshair X
    /// survives a round-trip, even ones this app does not understand yet.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var p = new Parser(text);
            p.SkipWs();
            var v = p.ReadValue();
            p.SkipWs();
            if (!p.End) throw new FormatException("Unexpected trailing characters in JSON at " + p.Pos);
            return v;
        }

        public static bool TryParse(string text, out object value)
        {
            try { value = Parse(text); return true; }
            catch { value = null; return false; }
        }

        public static string Serialize(object value, bool indent = false)
        {
            var sb = new StringBuilder();
            Write(sb, value, indent, 0);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object v, bool indent, int depth)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case double d: WriteNumber(sb, d); return;
                case float f: WriteNumber(sb, f); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
                case IDictionary<string, object> obj:
                    {
                        if (obj.Count == 0) { sb.Append("{}"); return; }
                        sb.Append('{');
                        bool first = true;
                        foreach (var kv in obj)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            if (indent) { sb.Append('\n'); sb.Append(' ', (depth + 1) * 2); }
                            WriteString(sb, kv.Key);
                            sb.Append(indent ? ": " : ":");
                            Write(sb, kv.Value, indent, depth + 1);
                        }
                        if (indent) { sb.Append('\n'); sb.Append(' ', depth * 2); }
                        sb.Append('}');
                        return;
                    }
                case System.Collections.IEnumerable list:
                    {
                        sb.Append('[');
                        bool first = true;
                        bool any = false;
                        foreach (var item in list)
                        {
                            any = true;
                            if (!first) sb.Append(',');
                            first = false;
                            if (indent) { sb.Append('\n'); sb.Append(' ', (depth + 1) * 2); }
                            Write(sb, item, indent, depth + 1);
                        }
                        if (indent && any) { sb.Append('\n'); sb.Append(' ', depth * 2); }
                        sb.Append(']');
                        return;
                    }
                default:
                    WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
                    return;
            }
        }

        static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append('0'); return; }
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15) sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
            else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        sealed class Parser
        {
            readonly string s;
            int i;
            public Parser(string text) { s = text ?? ""; }
            public bool End => i >= s.Length;
            public int Pos => i;

            public void SkipWs()
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r' || s[i] == '﻿')) i++;
            }

            public object ReadValue()
            {
                if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
                char c = s[i];
                if (c == '{') return ReadObject();
                if (c == '[') return ReadArray();
                if (c == '"') return ReadString();
                if (c == 't' && Match("true")) return true;
                if (c == 'f' && Match("false")) return false;
                if (c == 'n' && Match("null")) return null;
                return ReadNumber();
            }

            bool Match(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) == 0) { i += word.Length; return true; }
                throw new FormatException("Invalid token at " + i);
            }

            Dictionary<string, object> ReadObject()
            {
                var d = new Dictionary<string, object>();
                i++;
                SkipWs();
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    SkipWs();
                    if (i >= s.Length || s[i] != '"') throw new FormatException("Expected property name at " + i);
                    string key = ReadString();
                    SkipWs();
                    if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' at " + i);
                    i++;
                    SkipWs();
                    d[key] = ReadValue();
                    SkipWs();
                    if (i >= s.Length) throw new FormatException("Unexpected end of object");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException("Expected ',' or '}' at " + i);
                }
            }

            List<object> ReadArray()
            {
                var l = new List<object>();
                i++;
                SkipWs();
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    SkipWs();
                    l.Add(ReadValue());
                    SkipWs();
                    if (i >= s.Length) throw new FormatException("Unexpected end of array");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return l; }
                    throw new FormatException("Expected ',' or ']' at " + i);
                }
            }

            string ReadString()
            {
                i++;
                var sb = new StringBuilder();
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\')
                    {
                        if (i >= s.Length) break;
                        char e = s[i++];
                        switch (e)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'u':
                                if (i + 4 > s.Length) throw new FormatException("Bad unicode escape");
                                sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber));
                                i += 4;
                                break;
                            default: sb.Append(e); break;
                        }
                    }
                    else sb.Append(c);
                }
                throw new FormatException("Unterminated string");
            }

            object ReadNumber()
            {
                int start = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                if (start == i) throw new FormatException("Unexpected character '" + s[i] + "' at " + i);
                return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Typed accessors over raw JSON trees with sensible fallbacks.</summary>
    public static class J
    {
        public static Dictionary<string, object> Obj(object o, string key)
        {
            if (o is Dictionary<string, object> d && d.TryGetValue(key, out var v)) return v as Dictionary<string, object>;
            return null;
        }

        public static Dictionary<string, object> ObjOrEmpty(object o, string key) => Obj(o, key) ?? Empty;

        static readonly Dictionary<string, object> Empty = new Dictionary<string, object>();

        public static Dictionary<string, object> EnsureObj(Dictionary<string, object> o, string key)
        {
            if (o.TryGetValue(key, out var v) && v is Dictionary<string, object> d) return d;
            d = new Dictionary<string, object>();
            o[key] = d;
            return d;
        }

        public static bool Has(object o, string key) =>
            o is Dictionary<string, object> d && d.TryGetValue(key, out var v) && v != null;

        public static object Get(object o, string key) =>
            o is Dictionary<string, object> d && d.TryGetValue(key, out var v) ? v : null;

        public static double Num(object o, string key, double def = 0)
        {
            var v = Get(o, key);
            return ToNum(v, def);
        }

        public static double? NumOpt(object o, string key)
        {
            var v = Get(o, key);
            if (v == null) return null;
            double d = ToNum(v, double.NaN);
            return double.IsNaN(d) ? (double?)null : d;
        }

        public static double ToNum(object v, double def = 0)
        {
            switch (v)
            {
                case double d: return double.IsNaN(d) ? def : d;
                case int i: return i;
                case long l: return l;
                case float f: return f;
                case decimal m: return (double)m;
                case bool b: return b ? 1 : 0;
                case string s:
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : def;
                default: return def;
            }
        }

        public static string Str(object o, string key, string def = null)
        {
            var v = Get(o, key);
            if (v == null) return def;
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static bool Bool(object o, string key, bool def = false)
        {
            var v = Get(o, key);
            if (v is bool b) return b;
            if (v == null) return def;
            return ToNum(v, def ? 1 : 0) != 0;
        }

        public static List<object> List(object o, string key) => Get(o, key) as List<object>;

        public static object DeepClone(object v)
        {
            switch (v)
            {
                case Dictionary<string, object> d:
                    {
                        var n = new Dictionary<string, object>(d.Count);
                        foreach (var kv in d) n[kv.Key] = DeepClone(kv.Value);
                        return n;
                    }
                case List<object> l:
                    {
                        var n = new List<object>(l.Count);
                        foreach (var x in l) n.Add(DeepClone(x));
                        return n;
                    }
                default: return v;
            }
        }

        public static Dictionary<string, object> CloneObj(Dictionary<string, object> d) => (Dictionary<string, object>)DeepClone(d);

        /// <summary>Recursively merges <paramref name="over"/> into a clone of <paramref name="baseObj"/>. Null values are ignored.</summary>
        public static Dictionary<string, object> Merge(Dictionary<string, object> baseObj, Dictionary<string, object> over, ICollection<string> skipKeys = null)
        {
            var r = CloneObj(baseObj);
            if (over == null) return r;
            foreach (var kv in over)
            {
                if (kv.Value == null) continue;
                if (skipKeys != null && skipKeys.Contains(kv.Key)) continue;
                if (kv.Value is Dictionary<string, object> od && r.TryGetValue(kv.Key, out var bv) && bv is Dictionary<string, object> bd)
                    r[kv.Key] = Merge(bd, od);
                else if (kv.Value is string sv && sv.Length == 0 && r.ContainsKey(kv.Key) && r[kv.Key] is string)
                    continue; // "" means "unchanged" for colors in Crosshair X animation targets
                else
                    r[kv.Key] = DeepClone(kv.Value);
            }
            return r;
        }

        public static List<string> StrList(object o, string key)
        {
            var l = List(o, key);
            var r = new List<string>();
            if (l == null) return r;
            foreach (var x in l) if (x != null) r.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
            return r;
        }

        public static Dictionary<string, object> O(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = Normalize(kv[i + 1]);
            return d;
        }

        public static List<object> A(params object[] items)
        {
            var l = new List<object>();
            foreach (var x in items) l.Add(Normalize(x));
            return l;
        }

        static object Normalize(object v)
        {
            switch (v)
            {
                case int i: return (double)i;
                case float f: return (double)f;
                case long l: return (double)l;
                case decimal m: return (double)m;
                case string[] sa: { var l2 = new List<object>(); foreach (var s in sa) l2.Add(s); return l2; }
                default: return v;
            }
        }
    }
}
