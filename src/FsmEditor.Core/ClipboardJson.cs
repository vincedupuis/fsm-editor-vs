using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FsmEditor.Core
{
    /// <summary>
    /// Clipboard text of copied diagram elements: <c>{"fsmClipboard":1,"vertices":[…],"transitions":[…]}</c>,
    /// the same JSON as FSM Editor for VS Code, so elements can be pasted between the two editors.
    /// </summary>
    public static class ClipboardJson
    {
        public static string Write(ClipboardFragment data)
        {
            var sb = new StringBuilder("{\"fsmClipboard\":1,\"vertices\":[");
            sb.Append(string.Join(",", data.Vertices.Select(WriteVertex)));
            sb.Append("],\"transitions\":[");
            sb.Append(string.Join(",", data.Transitions.Select(WriteTransition)));
            sb.Append("]}");
            return sb.ToString();
        }

        private static string WriteVertex(Vertex v)
        {
            var o = new JsonObjectWriter();
            o.Str("id", v.Id).Str("type", v.Type.Key()).Str("name", v.Name).Str("parent", v.Parent)
                .Num("x", v.X).Num("y", v.Y).Num("w", v.W).Num("h", v.H);
            o.Raw("regions", "[" + string.Join(",", v.Regions.Select(r => new JsonObjectWriter().Str("id", r.Id).Str("name", r.Name).ToString())) + "]");
            if (v.Regions.Count > 1) o.Str("regionLayout", v.RegionLayout == RegionLayout.Horizontal ? "horizontal" : "vertical");
            o.Opt("entry", v.Entry).Opt("exit", v.Exit).Opt("doActivity", v.DoActivity).Opt("invariant", v.Invariant)
                .Opt("submachine", v.Submachine).Opt("stereotype", v.Stereotype);
            if (v.Deferrable.Count > 0) o.Raw("deferrable", StringArray(v.Deferrable));
            if (v.Type == VertexType.ConnectionPointRef) o.Str("ref", v.Ref).Str("pointKind", v.PointKind == PointKind.Exit ? "exit" : "entry");
            if (v.Type == VertexType.Comment) o.Str("text", v.Text).Raw("anchors", StringArray(v.Anchors));
            return o.ToString();
        }

        private static string WriteTransition(Transition t)
        {
            var o = new JsonObjectWriter();
            o.Str("id", t.Id).Str("source", t.Source).Str("target", t.Target).Str("kind", t.Kind.ToString().ToLowerInvariant())
                .Raw("triggers", StringArray(t.Triggers)).Str("guard", t.Guard).Str("effect", t.Effect)
                .Opt("precondition", t.Precondition).Opt("postcondition", t.Postcondition);
            if (t.Points.Count > 0)
            {
                o.Raw("points", "[" + string.Join(",", t.Points.Select(p => new JsonObjectWriter().Num("x", p.X).Num("y", p.Y).ToString())) + "]");
            }
            if (t.LabelOffset is PointD off) o.Raw("labelOffset", new JsonObjectWriter().Num("x", off.X).Num("y", off.Y).ToString());
            return o.ToString();
        }

        private static string StringArray(IEnumerable<string> items) => "[" + string.Join(",", items.Select(Quote)) + "]";

        public static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private sealed class JsonObjectWriter
        {
            private readonly List<string> _parts = new List<string>();

            public JsonObjectWriter Raw(string key, string json)
            {
                _parts.Add(Quote(key) + ":" + json);
                return this;
            }

            public JsonObjectWriter Str(string key, string value) => Raw(key, Quote(value ?? ""));

            public JsonObjectWriter Opt(string key, string value) => string.IsNullOrEmpty(value) ? this : Str(key, value);

            public JsonObjectWriter Num(string key, double value) => Raw(key, Core.Num.Format(value));

            public override string ToString() => "{" + string.Join(",", _parts) + "}";
        }

        /// <summary>Reads clipboard text; null when it isn't copied diagram elements.</summary>
        public static ClipboardFragment TryRead(string text)
        {
            object root;
            try
            {
                root = new JsonReader(text ?? "").ReadDocument();
            }
            catch (FormatException)
            {
                return null;
            }
            if (!(root is Dictionary<string, object> obj) || !obj.ContainsKey("fsmClipboard")) return null;
            var data = new ClipboardFragment();
            foreach (var item in List(obj, "vertices").OfType<Dictionary<string, object>>())
            {
                if (!VertexTypes.TryParseKey(Str(item, "type"), out var type)) continue;
                data.Vertices.Add(new Vertex
                {
                    Id = Str(item, "id"),
                    Type = type,
                    Name = Str(item, "name"),
                    Parent = Str(item, "parent"),
                    X = Dbl(item, "x"),
                    Y = Dbl(item, "y"),
                    W = Dbl(item, "w", 40),
                    H = Dbl(item, "h", 40),
                    Regions = List(item, "regions").OfType<Dictionary<string, object>>().Select(r => new Region { Id = Str(r, "id"), Name = Str(r, "name") }).ToList(),
                    RegionLayout = Str(item, "regionLayout") == "horizontal" ? RegionLayout.Horizontal : RegionLayout.Vertical,
                    Entry = Str(item, "entry"),
                    Exit = Str(item, "exit"),
                    DoActivity = Str(item, "doActivity"),
                    Deferrable = List(item, "deferrable").OfType<string>().ToList(),
                    Invariant = Str(item, "invariant"),
                    Submachine = Str(item, "submachine"),
                    Stereotype = Str(item, "stereotype"),
                    Ref = Str(item, "ref"),
                    PointKind = Str(item, "pointKind") == "exit" ? PointKind.Exit : PointKind.Entry,
                    Text = Str(item, "text"),
                    Anchors = List(item, "anchors").OfType<string>().ToList(),
                });
            }
            foreach (var item in List(obj, "transitions").OfType<Dictionary<string, object>>())
            {
                var kind = Str(item, "kind");
                var t = new Transition
                {
                    Id = Str(item, "id"),
                    Source = Str(item, "source"),
                    Target = Str(item, "target"),
                    Kind = kind == "internal" ? TransitionKind.Internal : kind == "local" ? TransitionKind.Local : TransitionKind.External,
                    Triggers = List(item, "triggers").OfType<string>().ToList(),
                    Guard = Str(item, "guard"),
                    Effect = Str(item, "effect"),
                    Precondition = Str(item, "precondition"),
                    Postcondition = Str(item, "postcondition"),
                    Points = List(item, "points").OfType<Dictionary<string, object>>().Select(p => new PointD(Dbl(p, "x"), Dbl(p, "y"))).ToList(),
                };
                if (item.TryGetValue("labelOffset", out var lo) && lo is Dictionary<string, object> off) t.LabelOffset = new PointD(Dbl(off, "x"), Dbl(off, "y"));
                data.Transitions.Add(t);
            }
            return data;
        }

        private static string Str(Dictionary<string, object> o, string key) => o.TryGetValue(key, out var v) && v is string s ? s : "";

        /// <summary>A number, or <paramref name="fallback"/> when missing or zero (like <c>Number(x) || fallback</c>).</summary>
        private static double Dbl(Dictionary<string, object> o, string key, double fallback = 0) =>
            o.TryGetValue(key, out var v) && v is double d && d != 0 ? d : fallback;

        private static List<object> List(Dictionary<string, object> o, string key) => o.TryGetValue(key, out var v) && v is List<object> l ? l : new List<object>();

        /// <summary>Minimal JSON reader: objects become dictionaries, arrays lists, numbers doubles.</summary>
        private sealed class JsonReader
        {
            private readonly string _s;
            private int _i;

            public JsonReader(string s) => _s = s;

            public object ReadDocument()
            {
                var v = Value();
                Space();
                if (_i != _s.Length) throw new FormatException("Trailing content.");
                return v;
            }

            private void Space()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            private char Peek()
            {
                Space();
                if (_i >= _s.Length) throw new FormatException("Unexpected end.");
                return _s[_i];
            }

            private void Expect(char c)
            {
                if (Peek() != c) throw new FormatException($"Expected '{c}'.");
                _i++;
            }

            private object Value()
            {
                var c = Peek();
                if (c == '{') return Object();
                if (c == '[') return Array();
                if (c == '"') return String();
                if (Word("true")) return true;
                if (Word("false")) return false;
                if (Word("null")) return null;
                return Number();
            }

            private bool Word(string w)
            {
                if (string.CompareOrdinal(_s, _i, w, 0, w.Length) != 0) return false;
                _i += w.Length;
                return true;
            }

            private Dictionary<string, object> Object()
            {
                var o = new Dictionary<string, object>();
                Expect('{');
                if (Peek() == '}')
                {
                    _i++;
                    return o;
                }
                for (;;)
                {
                    Peek();
                    var key = String();
                    Expect(':');
                    o[key] = Value();
                    if (Peek() == ',')
                    {
                        _i++;
                        continue;
                    }
                    Expect('}');
                    return o;
                }
            }

            private List<object> Array()
            {
                var a = new List<object>();
                Expect('[');
                if (Peek() == ']')
                {
                    _i++;
                    return a;
                }
                for (;;)
                {
                    a.Add(Value());
                    if (Peek() == ',')
                    {
                        _i++;
                        continue;
                    }
                    Expect(']');
                    return a;
                }
            }

            private string String()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (_i < _s.Length)
                {
                    var c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (_i >= _s.Length) break;
                    var e = _s[_i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw new FormatException("Bad escape.");
                            sb.Append((char)Convert.ToInt32(_s.Substring(_i, 4), 16));
                            _i += 4;
                            break;
                        default: sb.Append(e); break;
                    }
                }
                throw new FormatException("Unterminated string.");
            }

            private double Number()
            {
                var start = _i;
                while (_i < _s.Length && "+-0123456789.eE".IndexOf(_s[_i]) >= 0) _i++;
                if (start == _i || !double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    throw new FormatException("Bad number.");
                }
                return d;
            }
        }
    }
}
