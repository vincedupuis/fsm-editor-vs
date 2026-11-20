using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace FsmEditor.Core.Tests
{
    /// <summary>
    /// Clipboard parity with the VS Code edition, the reference: <c>clipboard/</c> is a copy of its
    /// <c>test/unit/fixtures/clipboard</c>, each case a machine of <c>geometry/</c>, a selection (every vertex, or one
    /// with what it contains) and the JSON that edition copies for it. This edition must copy the same JSON, and read
    /// that JSON back without losing anything (a paste from VS Code). After a change there, copy the folder here.
    /// </summary>
    public class ClipboardTests
    {
        private static string Dir => Path.Combine(AppContext.BaseDirectory, "clipboard");
        private static string Geometry => Path.Combine(AppContext.BaseDirectory, "geometry");

        public static IEnumerable<object[]> Cases() =>
            Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "clipboard"), "*.json")
                .Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.Ordinal).Select(n => new object[] { n });

        [Fact]
        public void HasTheCases() => Assert.True(Cases().Count() >= 20, $"{Cases().Count()} cases in {Dir}");

        [Theory]
        [MemberData(nameof(Cases))]
        public void CopiesTheJsonOfTheVsCodeEdition(string name)
        {
            var c = (Dictionary<string, object>)ClipboardJson.ParseJson(File.ReadAllText(Path.Combine(Dir, name + ".json")));
            var machine = Path.Combine(Geometry, (string)c["machine"]);
            var session = new DiagramSession(Load.LoadModel(File.ReadAllText(machine + ".fsm"), File.ReadAllText(machine + ".fsm.layout")));
            var select = (string)c["select"];
            session.Selection = select == "*" ? new HashSet<string>(session.Model.Vertices.Select(v => v.Id)) : new HashSet<string> { select };
            var copied = ClipboardJson.ParseJson(ClipboardJson.Write(session.CollectSelection()));
            Assert.Empty(Differences("", c["clipboard"], copied));
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void ReadsTheJsonOfTheVsCodeEdition(string name)
        {
            var c = (Dictionary<string, object>)ClipboardJson.ParseJson(File.ReadAllText(Path.Combine(Dir, name + ".json")));
            var text = Write(c["clipboard"]);
            var read = ClipboardJson.TryRead(text);
            Assert.NotNull(read);
            Assert.Empty(Differences("", c["clipboard"], ClipboardJson.ParseJson(ClipboardJson.Write(read))));
        }

        /// <summary>Where two JSON values differ (key order aside), at most ten places.</summary>
        private static List<string> Differences(string path, object a, object b)
        {
            var out_ = new List<string>();
            void Walk(string p, object x, object y)
            {
                if (out_.Count >= 10) return;
                if (x is Dictionary<string, object> dx && y is Dictionary<string, object> dy)
                {
                    foreach (var k in dx.Keys.Union(dy.Keys).OrderBy(k => k, StringComparer.Ordinal))
                    {
                        if (!dx.ContainsKey(k)) out_.Add($"{p}.{k}: only here, {Write(dy[k])}");
                        else if (!dy.ContainsKey(k)) out_.Add($"{p}.{k}: missing, VS Code has {Write(dx[k])}");
                        else Walk($"{p}.{k}", dx[k], dy[k]);
                    }
                }
                else if (x is List<object> lx && y is List<object> ly)
                {
                    if (lx.Count != ly.Count) out_.Add($"{p}: {ly.Count} items, VS Code has {lx.Count}");
                    for (var i = 0; i < Math.Min(lx.Count, ly.Count); i++) Walk($"{p}[{i}]", lx[i], ly[i]);
                }
                else if (Write(x) != Write(y)) out_.Add($"{p}: {Write(y)}, VS Code has {Write(x)}");
            }
            Walk(path, a, b);
            return out_;
        }

        private static string Write(object v)
        {
            switch (v)
            {
                case null: return "null";
                case string s: return ClipboardJson.Quote(s);
                case bool b: return b ? "true" : "false";
                case double d: return Num.Format(d);
                case List<object> l: return "[" + string.Join(",", l.Select(Write)) + "]";
                case Dictionary<string, object> o: return "{" + string.Join(",", o.Select(kv => ClipboardJson.Quote(kv.Key) + ":" + Write(kv.Value))) + "}";
                default: return v.ToString();
            }
        }
    }
}
