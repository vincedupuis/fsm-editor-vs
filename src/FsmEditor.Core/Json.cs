using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FsmEditor.Core
{
    public enum JsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>A JSON value and the line it starts on (1-based). Objects hold their fields in order, arrays their items.</summary>
    public sealed class JsonNode
    {
        public JsonNode(JsonKind kind, object value, int line)
        {
            Kind = kind;
            Value = value;
            Line = line;
        }

        public JsonKind Kind { get; }
        /// <summary>null, bool, double, string, <c>List&lt;JsonNode&gt;</c> or <c>List&lt;JsonField&gt;</c>.</summary>
        public object Value { get; }
        public int Line { get; }

        public List<JsonField> Fields => Kind == JsonKind.Object ? (List<JsonField>)Value : null;
        public List<JsonNode> Items => Kind == JsonKind.Array ? (List<JsonNode>)Value : null;
        public string Str => Kind == JsonKind.String ? (string)Value : null;
        public double? Number => Kind == JsonKind.Number ? (double?)(double)Value : null;

        /// <summary>The field <paramref name="key"/> of an object, or null.</summary>
        public JsonField Field(string key) => Fields?.FirstOrDefault(f => f.Key == key);
    }

    public sealed class JsonField
    {
        public JsonField(string key, int line, JsonNode value)
        {
            Key = key;
            Line = line;
            Value = value;
        }

        public string Key { get; }
        /// <summary>Line of the field's name.</summary>
        public int Line { get; }
        public JsonNode Value { get; }
    }

    public sealed class JsonSyntaxException : Exception
    {
        public JsonSyntaxException(int line, string message) : base(message) => Line = line;

        public int Line { get; }
    }

    /// <summary>
    /// A value to write (<see cref="Json.Write"/>): its fields in order, a field set again keeping its place (as
    /// JavaScript objects do). Values are strings, numbers (double), booleans, <c>List&lt;object&gt;</c> and
    /// <see cref="OutObject"/>; a null value is left out.
    /// </summary>
    public sealed class OutObject
    {
        private readonly List<KeyValuePair<string, object>> _fields = new List<KeyValuePair<string, object>>();

        public object this[string key]
        {
            get
            {
                foreach (var f in _fields) if (f.Key == key) return f.Value;
                return null;
            }
            set
            {
                for (var i = 0; i < _fields.Count; i++)
                {
                    if (_fields[i].Key != key) continue;
                    _fields[i] = new KeyValuePair<string, object>(key, value);
                    return;
                }
                _fields.Add(new KeyValuePair<string, object>(key, value));
            }
        }

        public bool Has(string key) => this[key] != null;

        /// <summary>The fields with a value, in order.</summary>
        public List<KeyValuePair<string, object>> Entries => _fields.Where(f => f.Value != null).ToList();

        public int Count => _fields.Count(f => f.Value != null);

        /// <summary>Copies the fields of <paramref name="other"/> into this one (<c>Object.assign</c>).</summary>
        public void Assign(OutObject other)
        {
            foreach (var f in other._fields) this[f.Key] = f.Value;
        }
    }

    /// <summary>
    /// A JSON reader that keeps the line of every value and of every field, for the <c>.fsm</c> and
    /// <c>.fsm.layout</c> files: their problems are reported with their lines. Strict JSON (RFC 8259), plus a byte order
    /// mark at the start, which is skipped; a field given twice in one object is an error. The same reader as fsm-cli's
    /// <c>src/json.rs</c> and the VS Code edition's <c>src/json.ts</c>: the same values and errors, at the same lines.
    /// </summary>
    public static class Json
    {
        /// <summary>How a character is shown in a message: itself, or <c>\uXXXX</c> for control and invisible characters.</summary>
        private static string Show(int code)
        {
            if (code < 0x20 || (code >= 0x7f && code <= 0x9f) || code == 0xfeff) return "\\u" + code.ToString("x4", CultureInfo.InvariantCulture);
            return char.ConvertFromUtf32(code);
        }

        private static bool IsDigit(string text, int i) => i < text.Length && text[i] >= '0' && text[i] <= '9';

        /// <summary>Parses a JSON document; throws <see cref="JsonSyntaxException"/>.</summary>
        public static JsonNode Parse(string text) => new Parser(text).Document();

        private sealed class Parser
        {
            private readonly string _text;
            private int _i;
            private int _line = 1;

            public Parser(string text)
            {
                _text = text;
                _i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
            }

            private Exception Fail(string message, int? at = null) => new JsonSyntaxException(at ?? _line, message);

            private Exception Unexpected()
            {
                if (_i >= _text.Length) return Fail("Unexpected end of the file.");
                // The code point at _i (a lone surrogate as itself).
                int code = char.IsHighSurrogate(_text[_i]) && _i + 1 < _text.Length && char.IsLowSurrogate(_text[_i + 1])
                    ? char.ConvertToUtf32(_text[_i], _text[_i + 1])
                    : _text[_i];
                return Fail($"Unexpected character '{(code > 0xFFFF || !char.IsSurrogate((char)code) ? Show(code) : _text[_i].ToString())}'.");
            }

            private char? At(int i) => i < _text.Length ? _text[i] : (char?)null;

            private void Space()
            {
                while (_i < _text.Length)
                {
                    var c = _text[_i];
                    if (c == '\n') _line++;
                    else if (c != ' ' && c != '\t' && c != '\r') return;
                    _i++;
                }
            }

            private int Hex4()
            {
                if (_i + 4 > _text.Length) throw Fail("Invalid escape in a text.");
                var h = _text.Substring(_i, 4);
                foreach (var c in h)
                {
                    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) throw Fail("Invalid escape in a text.");
                }
                _i += 4;
                return Convert.ToInt32(h, 16);
            }

            private string String()
            {
                _i++; // the opening quote
                var output = new StringBuilder();
                for (;;)
                {
                    if (_i >= _text.Length) throw Fail("Unexpected end of the file in a text.");
                    var c = _text[_i];
                    if (c == '"')
                    {
                        _i++;
                        return output.ToString();
                    }
                    if (c == '\\')
                    {
                        var e = At(_i + 1);
                        _i += 2;
                        switch (e)
                        {
                            case '"': output.Append('"'); break;
                            case '\\': output.Append('\\'); break;
                            case '/': output.Append('/'); break;
                            case 'b': output.Append('\b'); break;
                            case 'f': output.Append('\f'); break;
                            case 'n': output.Append('\n'); break;
                            case 'r': output.Append('\r'); break;
                            case 't': output.Append('\t'); break;
                            case 'u':
                            {
                                var u = Hex4();
                                if (u >= 0xd800 && u < 0xdc00)
                                {
                                    if (_i + 2 > _text.Length || _text[_i] != '\\' || _text[_i + 1] != 'u') throw Fail("Invalid escape in a text.");
                                    _i += 2;
                                    var low = Hex4();
                                    if (low < 0xdc00 || low >= 0xe000) throw Fail("Invalid escape in a text.");
                                    output.Append((char)u).Append((char)low);
                                }
                                else if (u >= 0xdc00 && u < 0xe000)
                                {
                                    throw Fail("Invalid escape in a text.");
                                }
                                else output.Append((char)u);
                                break;
                            }
                            default:
                                throw Fail("Invalid escape in a text.");
                        }
                        continue;
                    }
                    if (c < 0x20) throw Fail($"Control character '{Show(c)}' in a text: write it as an escape.");
                    output.Append(c);
                    _i++;
                }
            }

            private double Number()
            {
                var start = _i;
                if (At(_i) == '-') _i++;
                if (!IsDigit(_text, _i)) throw Fail("Invalid number.");
                if (_text[_i] == '0')
                {
                    _i++;
                    if (IsDigit(_text, _i)) throw Fail("Invalid number.");
                }
                else
                {
                    while (IsDigit(_text, _i)) _i++;
                }
                if (At(_i) == '.')
                {
                    _i++;
                    if (!IsDigit(_text, _i)) throw Fail("Invalid number.");
                    while (IsDigit(_text, _i)) _i++;
                }
                if (At(_i) == 'e' || At(_i) == 'E')
                {
                    _i++;
                    if (At(_i) == '+' || At(_i) == '-') _i++;
                    if (!IsDigit(_text, _i)) throw Fail("Invalid number.");
                    while (IsDigit(_text, _i)) _i++;
                }
                var n = Num.ParseJs(_text.Substring(start, _i - start));
                if (double.IsNaN(n) || double.IsInfinity(n)) throw Fail("Invalid number.");
                return n;
            }

            private object Word(string w, object v)
            {
                if (string.CompareOrdinal(_text, _i, w, 0, w.Length) == 0)
                {
                    _i += w.Length;
                    return v;
                }
                throw Unexpected();
            }

            private JsonNode Value(int depth)
            {
                if (depth > 200) throw Fail("The document is nested too deeply.");
                Space();
                var at = _line;
                if (_i >= _text.Length) throw Unexpected();
                var c = _text[_i];
                if (c == '{')
                {
                    _i++;
                    var fields = new List<JsonField>();
                    Space();
                    if (At(_i) == '}') _i++;
                    else
                    {
                        for (;;)
                        {
                            Space();
                            if (At(_i) != '"') throw Unexpected();
                            var keyLine = _line;
                            var key = String();
                            if (fields.Any(f => f.Key == key)) throw Fail($"Field '{key}' is given twice.", keyLine);
                            Space();
                            if (At(_i) != ':') throw Unexpected();
                            _i++;
                            var v = Value(depth + 1);
                            fields.Add(new JsonField(key, keyLine, v));
                            Space();
                            if (At(_i) == ',') _i++;
                            else if (At(_i) == '}')
                            {
                                _i++;
                                break;
                            }
                            else throw Unexpected();
                        }
                    }
                    return new JsonNode(JsonKind.Object, fields, at);
                }
                if (c == '[')
                {
                    _i++;
                    var items = new List<JsonNode>();
                    Space();
                    if (At(_i) == ']') _i++;
                    else
                    {
                        for (;;)
                        {
                            items.Add(Value(depth + 1));
                            Space();
                            if (At(_i) == ',') _i++;
                            else if (At(_i) == ']')
                            {
                                _i++;
                                break;
                            }
                            else throw Unexpected();
                        }
                    }
                    return new JsonNode(JsonKind.Array, items, at);
                }
                if (c == '"') return new JsonNode(JsonKind.String, String(), at);
                if (c == 't') return new JsonNode(JsonKind.Bool, Word("true", true), at);
                if (c == 'f') return new JsonNode(JsonKind.Bool, Word("false", false), at);
                if (c == 'n') return new JsonNode(JsonKind.Null, Word("null", null), at);
                if (c == '-' || (c >= '0' && c <= '9')) return new JsonNode(JsonKind.Number, Number(), at);
                throw Unexpected();
            }

            public JsonNode Document()
            {
                var root = Value(0);
                Space();
                if (_i < _text.Length) throw Unexpected();
                return root;
            }
        }

        // ---------------------------------------------------------------- plain values

        /// <summary>A node as a value to write (objects with their fields in order).</summary>
        public static object Plain(JsonNode n)
        {
            switch (n.Kind)
            {
                case JsonKind.Array: return n.Items.Select(Plain).ToList();
                case JsonKind.Object:
                {
                    var o = new OutObject();
                    foreach (var f in n.Fields) o[f.Key] = Plain(f.Value);
                    return o;
                }
                default: return n.Value;
            }
        }

        /// <summary>A value as JavaScript's <c>JSON.stringify</c> writes it, compactly (texts, numbers, booleans, lists, objects).</summary>
        public static string Stringify(object v)
        {
            switch (v)
            {
                case null: return "null";
                case string s: return StringifyText(s);
                case bool b: return b ? "true" : "false";
                case double d: return Num.Format(d);
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case OutObject o: return "{" + string.Join(",", o.Entries.Select(e => StringifyText(e.Key) + ":" + Stringify(e.Value))) + "}";
                case System.Collections.IEnumerable list: return "[" + string.Join(",", list.Cast<object>().Select(Stringify)) + "]";
                default: throw new ArgumentException("Not a JSON value: " + v.GetType());
            }
        }

        private static string StringifyText(string s)
        {
            var sb = new StringBuilder("\"");
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
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
                        var lone = char.IsHighSurrogate(c) ? !(i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                            : char.IsLowSurrogate(c) && !(i > 0 && char.IsHighSurrogate(s[i - 1]));
                        if (c < 0x20 || lone) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>Reads back what <see cref="Stringify"/> wrote of an object (texts only).</summary>
        public static OutObject ParseObject(string json) => Plain(Parse(json)) as OutObject ?? new OutObject();

        // ---------------------------------------------------------------- writing

        /// <summary>A text as the files write it: minimal escapes (<c>\"</c>, <c>\\</c>, <c>\n</c>, <c>\t</c>, <c>\u00XX</c> for other control characters).</summary>
        public static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        private static bool IsList(object v) => v is System.Collections.IList;

        private static string Flat(object v)
        {
            switch (v)
            {
                case string s: return Quote(s);
                case double d: return Num.Format(d);
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case bool b: return b ? "true" : "false";
                case OutObject o:
                    var e = o.Entries;
                    return e.Count > 0 ? "{ " + string.Join(", ", e.Select(x => Quote(x.Key) + ": " + Flat(x.Value))) + " }" : "{}";
                case System.Collections.IList list: return "[" + string.Join(", ", list.Cast<object>().Select(Flat)) + "]";
                default: throw new ArgumentException("Not a JSON value: " + v?.GetType());
            }
        }

        /// <summary>
        /// Writes a document, 2-space indented, with a newline at the end. <paramref name="inline"/> says whether an
        /// object goes on one line (lists are always one item per line, the top-level object one field per line).
        /// <paramref name="onLine"/> is told the line where each object starts.
        /// </summary>
        public static string Write(OutObject root, Func<OutObject, bool> inline, Action<OutObject, int> onLine = null)
        {
            var lines = new List<string>();
            void Put(object v, string indent, string prefix, string suffix, bool top)
            {
                if (v is OutObject o)
                {
                    onLine?.Invoke(o, lines.Count + 1);
                    var e = o.Entries;
                    if (e.Count == 0 || (!top && inline(o)))
                    {
                        lines.Add(indent + prefix + Flat(o) + suffix);
                        return;
                    }
                    lines.Add(indent + prefix + "{");
                    for (var i = 0; i < e.Count; i++) Put(e[i].Value, indent + "  ", Quote(e[i].Key) + ": ", i < e.Count - 1 ? "," : "", false);
                    lines.Add(indent + "}" + suffix);
                    return;
                }
                if (IsList(v))
                {
                    var list = ((System.Collections.IList)v).Cast<object>().ToList();
                    if (list.Count == 0)
                    {
                        lines.Add(indent + prefix + "[]" + suffix);
                        return;
                    }
                    lines.Add(indent + prefix + "[");
                    for (var i = 0; i < list.Count; i++) Put(list[i], indent + "  ", "", i < list.Count - 1 ? "," : "", false);
                    lines.Add(indent + "]" + suffix);
                    return;
                }
                lines.Add(indent + prefix + Flat(v) + suffix);
            }
            Put(root, "", "", "", true);
            return string.Join("\n", lines) + "\n";
        }

        /// <summary>Whether a value to write is an object or a list (JavaScript's <c>typeof x === 'object'</c>).</summary>
        public static bool IsComposite(object v) => v is OutObject || IsList(v);
    }
}
