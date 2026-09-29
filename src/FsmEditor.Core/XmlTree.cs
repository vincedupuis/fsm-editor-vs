using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FsmEditor.Core
{
    /// <summary>
    /// A read-only XML element whose element and attribute names use canonical
    /// prefixes for known namespace URIs, so documents using other prefixes read
    /// the same (<c>uml:State</c> whatever prefix the file binds to the UML namespace).
    /// </summary>
    public sealed class XmlElem
    {
        /// <summary>Qualified name using the canonical prefix when the namespace is known, e.g. <c>uml:Model</c>.</summary>
        public string Name { get; set; } = "";
        /// <summary>Namespace URI of the element ("" when none).</summary>
        public string Ns { get; set; } = "";
        /// <summary>Local part of the name.</summary>
        public string Local { get; set; } = "";
        public Dictionary<string, string> Attrs { get; } = new Dictionary<string, string>();
        public List<XmlElem> Children { get; } = new List<XmlElem>();
        public string Text { get; set; } = "";
        public int Line { get; set; }

        public string Attr(string name) => Attrs.TryGetValue(name, out var v) ? v : null;
    }

    public sealed class XmlParseException : Exception
    {
        public XmlParseException(string message, int line) : base($"Line {line}: {message}") => Line = line;

        public int Line { get; }
    }

    /// <summary>An element to write: name, attributes in order, and children (elements or text).</summary>
    public sealed class XNode
    {
        public XNode(string name, params (string Key, object Value)[] attrs)
        {
            Name = name;
            Attrs.AddRange(attrs);
        }

        public string Name { get; set; }
        public List<(string Key, object Value)> Attrs { get; } = new List<(string, object)>();
        /// <summary>Child <see cref="XNode"/>s or strings.</summary>
        public List<object> Children { get; } = new List<object>();

        public XNode Add(params object[] children)
        {
            foreach (var c in children)
            {
                if (c is IEnumerable<XNode> many) Children.AddRange(many);
                else if (c != null) Children.Add(c);
            }
            return this;
        }
    }

    public static class XmlTree
    {
        /// <summary>
        /// Parses <paramref name="text"/> and returns the document element.
        /// <paramref name="canonical"/> maps namespace URIs to the prefix used in the returned names.
        /// Handles elements, attributes, text, CDATA, comments, processing instructions and namespaces,
        /// and accepts any name the diagram editors may write.
        /// </summary>
        public static XmlElem Parse(string text, IReadOnlyDictionary<string, string> canonical) =>
            Resolve(new Reader(text).Document(), new Dictionary<string, string>(), canonical);

        private sealed class Raw
        {
            public string QName = "";
            public List<KeyValuePair<string, string>> Attrs = new List<KeyValuePair<string, string>>();
            public List<Raw> Children = new List<Raw>();
            public StringBuilder Text = new StringBuilder();
            public int Line;
        }

        private sealed class Reader
        {
            private static readonly Regex NameRe = new Regex(@"\G[A-Za-z_][A-Za-z0-9_.\-:]*");
            private static readonly Regex EntityRe = new Regex("&(#x[0-9a-fA-F]+|#[0-9]+|[a-zA-Z]+);");
            private readonly string _text;
            private int _i;
            private int _line = 1;

            public Reader(string text) => _text = text;

            private bool At(string s) => string.CompareOrdinal(_text, _i, s, 0, s.Length) == 0;

            private void Advance(int to)
            {
                for (int k = _i; k < to; k++)
                {
                    if (_text[k] == '\n') _line++;
                }
                _i = to;
            }

            private void Expect(string s)
            {
                if (!At(s)) throw new XmlParseException($"Expected '{s}'.", _line);
                Advance(_i + s.Length);
            }

            private string SkipUntil(string end, string what)
            {
                var j = _text.IndexOf(end, _i, StringComparison.Ordinal);
                if (j < 0) throw new XmlParseException($"Unterminated {what}.", _line);
                var content = _text.Substring(_i, j - _i);
                Advance(j + end.Length);
                return content;
            }

            private void SkipSpace()
            {
                int j = _i;
                while (j < _text.Length && char.IsWhiteSpace(_text[j])) j++;
                Advance(j);
            }

            private string Name()
            {
                var m = NameRe.Match(_text, _i);
                if (!m.Success) throw new XmlParseException("Expected a name.", _line);
                Advance(_i + m.Length);
                return m.Value;
            }

            private string Decode(string s, int line) =>
                EntityRe.Replace(s, m =>
                {
                    var e = m.Groups[1].Value;
                    if (e[0] == '#')
                    {
                        var code = e[1] == 'x' ? Convert.ToInt32(e.Substring(2), 16) : int.Parse(e.Substring(1), CultureInfo.InvariantCulture);
                        return char.ConvertFromUtf32(code);
                    }
                    switch (e)
                    {
                        case "lt": return "<";
                        case "gt": return ">";
                        case "amp": return "&";
                        case "quot": return "\"";
                        case "apos": return "'";
                        default: throw new XmlParseException($"Unknown entity {m.Value}.", line);
                    }
                });

            private Raw Element()
            {
                var el = new Raw { Line = _line };
                Expect("<");
                el.QName = Name();
                for (;;)
                {
                    SkipSpace();
                    if (At("/>"))
                    {
                        Advance(_i + 2);
                        return el;
                    }
                    if (_i < _text.Length && _text[_i] == '>')
                    {
                        Advance(_i + 1);
                        break;
                    }
                    var an = Name();
                    SkipSpace();
                    Expect("=");
                    SkipSpace();
                    var q = _i < _text.Length ? _text[_i] : '\0';
                    if (q != '"' && q != '\'') throw new XmlParseException($"Attribute {an} must be quoted.", _line);
                    Advance(_i + 1);
                    var at = _line;
                    var raw = SkipUntil(q.ToString(), $"attribute {an}");
                    if (raw.IndexOf('<') >= 0) throw new XmlParseException($"'<' is not allowed in attribute {an}.", at);
                    el.Attrs.Add(new KeyValuePair<string, string>(an, Decode(raw, at)));
                }
                for (;;)
                {
                    if (_i >= _text.Length) throw new XmlParseException($"Element <{el.QName}> is not closed.", el.Line);
                    if (At("</"))
                    {
                        Advance(_i + 2);
                        var close = Name();
                        if (close != el.QName) throw new XmlParseException($"Expected </{el.QName}>, found </{close}>.", _line);
                        SkipSpace();
                        Expect(">");
                        return el;
                    }
                    if (At("<!--"))
                    {
                        Advance(_i + 4);
                        SkipUntil("-->", "comment");
                    }
                    else if (At("<![CDATA["))
                    {
                        Advance(_i + 9);
                        el.Text.Append(SkipUntil("]]>", "CDATA section"));
                    }
                    else if (At("<?"))
                    {
                        Advance(_i + 2);
                        SkipUntil("?>", "processing instruction");
                    }
                    else if (_text[_i] == '<')
                    {
                        el.Children.Add(Element());
                    }
                    else
                    {
                        var j = _text.IndexOf('<', _i);
                        if (j < 0) j = _text.Length;
                        var at = _line;
                        var chunk = _text.Substring(_i, j - _i);
                        Advance(j);
                        el.Text.Append(Decode(chunk, at));
                    }
                }
            }

            public Raw Document()
            {
                // Prolog
                for (;;)
                {
                    SkipSpace();
                    if (At("<?"))
                    {
                        Advance(_i + 2);
                        SkipUntil("?>", "processing instruction");
                    }
                    else if (At("<!--"))
                    {
                        Advance(_i + 4);
                        SkipUntil("-->", "comment");
                    }
                    else if (At("<!DOCTYPE"))
                    {
                        SkipUntil(">", "DOCTYPE");
                    }
                    else
                    {
                        break;
                    }
                }
                if (_i >= _text.Length || _text[_i] != '<') throw new XmlParseException("The document does not start with an element.", _line);
                var root = Element();
                SkipSpace();
                while (At("<!--"))
                {
                    Advance(_i + 4);
                    SkipUntil("-->", "comment");
                    SkipSpace();
                }
                if (_i < _text.Length) throw new XmlParseException("Content after the document element.", _line);
                return root;
            }
        }

        // Namespace resolution
        private static XmlElem Resolve(Raw raw, Dictionary<string, string> scope, IReadOnlyDictionary<string, string> canonical)
        {
            var s = new Dictionary<string, string>(scope);
            foreach (var a in raw.Attrs)
            {
                if (a.Key == "xmlns") s[""] = a.Value;
                else if (a.Key.StartsWith("xmlns:", StringComparison.Ordinal)) s[a.Key.Substring(6)] = a.Value;
            }
            (string Name, string Ns, string Local) Qualify(string qname, bool isAttr)
            {
                var c = qname.IndexOf(':');
                var prefix = c < 0 ? "" : qname.Substring(0, c);
                var local = c < 0 ? qname : qname.Substring(c + 1);
                if (isAttr && c < 0) return (local, "", local);
                var ns = s.TryGetValue(prefix, out var u) ? u : "";
                var p = canonical.TryGetValue(ns, out var cp) ? cp : prefix;
                return (p.Length > 0 ? $"{p}:{local}" : local, ns, local);
            }
            var q = Qualify(raw.QName, false);
            var el = new XmlElem { Name = q.Name, Ns = q.Ns, Local = q.Local, Text = raw.Text.ToString(), Line = raw.Line };
            foreach (var a in raw.Attrs)
            {
                if (a.Key == "xmlns" || a.Key.StartsWith("xmlns:", StringComparison.Ordinal)) continue;
                var name = Qualify(a.Key, true).Name;
                // Qualified values (xmi:type="uml:State") follow the same prefix mapping.
                el.Attrs[name] = name.EndsWith(":type", StringComparison.Ordinal) ? Qualify(a.Value, false).Name : a.Value;
            }
            foreach (var c in raw.Children) el.Children.Add(Resolve(c, s, canonical));
            return el;
        }

        public static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\r': sb.Append("&#13;"); break;
                    case '\n': sb.Append("&#10;"); break;
                    case '\t': sb.Append("&#9;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Writes the document with two-space indentation and LF line ends.</summary>
        public static string Write(XNode root)
        {
            var output = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            WriteNode(root, "", output);
            return output.ToString();
        }

        private static void WriteNode(XNode node, string indent, StringBuilder output)
        {
            output.Append(indent).Append('<').Append(node.Name);
            foreach (var (key, value) in node.Attrs)
            {
                var s = FormatValue(value);
                if (string.IsNullOrEmpty(s)) continue;
                output.Append(' ').Append(key).Append("=\"").Append(Escape(s)).Append('"');
            }
            if (node.Children.Count == 0)
            {
                output.Append("/>\n");
            }
            else if (node.Children.Count == 1 && node.Children[0] is string only)
            {
                output.Append('>').Append(Escape(only).Replace("&#10;", "\n")).Append("</").Append(node.Name).Append(">\n");
            }
            else
            {
                output.Append(">\n");
                foreach (var c in node.Children)
                {
                    if (c is XNode child) WriteNode(child, indent + "  ", output);
                    else output.Append(indent).Append("  ").Append(Escape(c.ToString())).Append('\n');
                }
                output.Append(indent).Append("</").Append(node.Name).Append(">\n");
            }
        }

        private static string FormatValue(object value)
        {
            switch (value)
            {
                case null: return null;
                case double d: return Num.Format(d);
                case string s: return s;
                default: return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Number helpers matching how the diagram and the files have always printed coordinates.</summary>
    public static class Num
    {
        /// <summary>Rounds half up (towards +∞), like the rounding used for saved coordinates.</summary>
        public static double Round(double n) => Math.Floor(n + 0.5);

        /// <summary>Rounds to 0.1.</summary>
        public static double R1(double n) => Math.Floor(n * 10 + 0.5) / 10;

        /// <summary>Shortest invariant text of a number: <c>60</c>, <c>12.5</c>, never <c>-0</c> or an exponent.</summary>
        public static string Format(double n)
        {
            if (double.IsNaN(n) || double.IsInfinity(n)) return "0";
            if (n == 0) return "0";
            if (n == Math.Floor(n) && Math.Abs(n) < 1e15) return ((long)n).ToString(CultureInfo.InvariantCulture);
            var s = n.ToString("R", CultureInfo.InvariantCulture);
            if (s.IndexOf('E') >= 0) s = n.ToString("0.###############", CultureInfo.InvariantCulture);
            return s;
        }

        /// <summary>Parses a number like JavaScript's unary plus would for XMI attributes (invalid gives 0).</summary>
        public static double Parse(string s) =>
            double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) ? d : 0;
    }
}
