using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
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
            // Line ends as XML 1.0 (2.11) has them read: \r\n and a lone \r become \n (a &#13; reference still gives \r).
            // Kept, a file with CRLF line ends would gain a \r in multi-line text on each save.
            Resolve(new Reader(text.Replace("\r\n", "\n").Replace('\r', '\n')).Document(), new Dictionary<string, string>(), canonical);

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
                while (j < _text.Length && JsWhitespace.Contains(_text[j])) j++;
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
                        var ok = e[1] == 'x'
                            ? int.TryParse(e.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)
                            : int.TryParse(e.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out code);
                        if (!ok || code < 0 || code > 0x10FFFF) throw new XmlParseException($"Unknown entity {m.Value}.", line);
                        // A lone surrogate reads as that character, as in the other editions.
                        return code >= 0xD800 && code <= 0xDFFF ? ((char)code).ToString() : char.ConvertFromUtf32(code);
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

        /// <summary>
        /// JavaScript's <c>Math.hypot(a, b)</c> as V8 computes it (scaled by the larger value, with Kahan summation),
        /// which differs from <c>Math.Sqrt(a * a + b * b)</c> in the last bit for many values.
        /// </summary>
        public static double Hypot(double a, double b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            if (double.IsInfinity(a) || double.IsInfinity(b)) return double.PositiveInfinity;
            if (double.IsNaN(a) || double.IsNaN(b)) return double.NaN;
            var max = Math.Max(a, b);
            if (max == 0) return 0;
            double sum = 0, compensation = 0;
            foreach (var x in new[] { a, b })
            {
                var r = x / max;
                var summand = r * r - compensation;
                var preliminary = sum + summand;
                compensation = preliminary - sum - summand;
                sum = preliminary;
            }
            return Math.Sqrt(sum) * max;
        }

        /// <summary>
        /// The text JavaScript's <c>String(n)</c> gives: the shortest digits that read back
        /// as <paramref name="n"/>, <c>60</c>, <c>12.5</c>, <c>1e-7</c>, <c>1.5e+21</c>; <c>-0</c> gives <c>0</c>.
        /// NaN and infinities give <c>0</c>. Computed exactly, so it doesn't depend on the
        /// runtime's own formatting ("R" can print 17 digits on .NET Framework).
        /// </summary>
        public static string Format(double n)
        {
            if (double.IsNaN(n) || double.IsInfinity(n)) return "0";
            if (n == 0) return "0";
            if (n == Math.Floor(n) && Math.Abs(n) < 1e15) return ((long)n).ToString(CultureInfo.InvariantCulture);
            if (n < 0) return "-" + Format(-n);
            ShortestDigits(n, out var digits, out var point);
            return Layout(digits, point);
        }

        /// <summary>
        /// Shortest digits <paramref name="digits"/> (no trailing zero) such that
        /// 0.<paramref name="digits"/> × 10^<paramref name="point"/> reads back as <paramref name="n"/> (&gt; 0),
        /// the closest to <paramref name="n"/> when several do (ECMAScript Number::toString).
        /// </summary>
        private static void ShortestDigits(double n, out string digits, out int point)
        {
            long bits = BitConverter.DoubleToInt64Bits(n);
            int biased = (int)((bits >> 52) & 0x7FF);
            long fraction = bits & 0xFFFFFFFFFFFFFL;
            long f = biased == 0 ? fraction : fraction | (1L << 52);
            int e = biased == 0 ? -1074 : biased - 1075;
            // n = f × 2^e. In units of 2^(e-2): n is 4f, and the numbers that read back as n lie
            // between 4f-2 (4f-1 just above a power of two) and 4f+2, the bounds included when f is even.
            var v = new BigInteger(f) * 4;
            var hi = v + 2;
            var lo = v - (fraction == 0 && biased > 1 ? 1 : 2);
            bool inclusive = (f & 1) == 0;
            var twoUp = e - 2 >= 0 ? BigInteger.Pow(2, e - 2) : BigInteger.One;
            var twoDown = e - 2 < 0 ? BigInteger.Pow(2, 2 - e) : BigInteger.One;

            // Decimal exponent k with 10^k <= n < 10^(k+1).
            int k = (int)Math.Floor(Math.Log10(n));
            bool AtLeastPow10(int x) =>
                v * twoUp * (x < 0 ? BigInteger.Pow(10, -x) : BigInteger.One) >= twoDown * (x > 0 ? BigInteger.Pow(10, x) : BigInteger.One);
            while (!AtLeastPow10(k)) k--;
            while (AtLeastPow10(k + 1)) k++;

            for (int p = 1; p <= 17; p++)
            {
                // Candidates with p digits are N × 10^s; n / 10^s = num / den.
                int s = k - p + 1;
                var scale = twoUp * (s < 0 ? BigInteger.Pow(10, -s) : BigInteger.One);
                var den = twoDown * (s > 0 ? BigInteger.Pow(10, s) : BigInteger.One);
                var num = v * scale;
                var numLo = lo * scale;
                var numHi = hi * scale;
                var down = BigInteger.DivRem(num, den, out var rem);
                var up = down + 1;
                bool Reads(BigInteger c)
                {
                    var x = c * den;
                    return inclusive ? x >= numLo && x <= numHi : x > numLo && x < numHi;
                }
                bool downOk = Reads(down), upOk = Reads(up);
                if (!downOk && !upOk) continue;
                BigInteger chosen;
                if (downOk && upOk)
                {
                    int c = (rem * 2).CompareTo(den);
                    chosen = c < 0 ? down : c > 0 ? up : (down.IsEven ? down : up);
                }
                else
                {
                    chosen = downOk ? down : up;
                }
                var text = chosen.ToString(CultureInfo.InvariantCulture);
                point = s + text.Length;
                digits = text.TrimEnd('0');
                return;
            }
            // 17 digits always identify a double.
            throw new InvalidOperationException("No round-trip digits for " + n.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>Lays out 0.<paramref name="d"/> × 10^<paramref name="n"/> as ECMAScript Number::toString does.</summary>
        private static string Layout(string d, int n)
        {
            int k = d.Length;
            if (k <= n && n <= 21) return d + new string('0', n - k);
            if (0 < n && n <= 21) return d.Substring(0, n) + "." + d.Substring(n);
            if (-6 < n && n <= 0) return "0." + new string('0', -n) + d;
            int exp = n - 1;
            var tail = "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString(CultureInfo.InvariantCulture);
            return k == 1 ? d + tail : d.Substring(0, 1) + "." + d.Substring(1) + tail;
        }

        /// <summary>
        /// Parses a number like JavaScript's <c>+s || 0</c> does for XMI attributes (missing or invalid gives 0), infinite
        /// ones giving 0 too, as they do once the other editions' JSON has made them null.
        /// </summary>
        public static double Parse(string s)
        {
            var d = ParseJs(s);
            return double.IsNaN(d) || double.IsInfinity(d) ? 0 : d;
        }

        /// <summary>
        /// JavaScript's unary plus (<c>+s</c>): the number in <paramref name="s"/> once trimmed, 0 when that is empty,
        /// NaN when <paramref name="s"/> is null (a missing attribute) or isn't a number.
        /// </summary>
        public static double ParseJs(string s)
        {
            if (s == null) return double.NaN;
            var t = JsWhitespace.Trim(s);
            if (t.Length == 0) return 0;
            if (t.Length > 2 && t[0] == '0')
            {
                var radix = t[1] == 'x' || t[1] == 'X' ? 16 : t[1] == 'o' || t[1] == 'O' ? 8 : t[1] == 'b' || t[1] == 'B' ? 2 : 0;
                if (radix > 0)
                {
                    double n = 0;
                    for (var i = 2; i < t.Length; i++)
                    {
                        var c = t[i];
                        var digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'z' ? c - 'a' + 10 : c >= 'A' && c <= 'Z' ? c - 'A' + 10 : 99;
                        if (digit >= radix) return double.NaN;
                        n = n * radix + digit;
                    }
                    return n;
                }
            }
            var body = t[0] == '+' || t[0] == '-' ? t.Substring(1) : t;
            if (body == "Infinity") return t[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
            // A decimal literal: digits with an optional point and exponent (not the other forms .NET would accept).
            if (!Regex.IsMatch(body, @"^(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$")) return double.NaN;
            // .NET Framework refuses numbers too large for a double, which JavaScript reads as infinite.
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
                : t[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
        }
    }
}
