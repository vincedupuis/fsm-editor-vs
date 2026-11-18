using System.Collections.Generic;
using System.IO;
using System.Linq;
using FsmEditor.Core;
using Xunit;

namespace FsmEditor.Core.Tests
{
    /// <summary>
    /// fsm-editor-common through this edition's client (<see cref="Common"/>, <see cref="ModelJson"/>): its interface's
    /// cases (the requests and answers its own tests check) give the same answers here, and the examples are written back
    /// as they are, through the model's JSON both ways. The format, validation, the grammar, conversion, comparing and
    /// merging are tested in fsm-editor-common itself.
    /// </summary>
    public class CommonTests
    {
        private static bool Same(object a, object b)
        {
            switch (a)
            {
                case Dictionary<string, object> x when b is Dictionary<string, object> y:
                    return x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var v) && Same(kv.Value, v));
                case List<object> x when b is List<object> y:
                    return x.Count == y.Count && x.Zip(y, Same).All(s => s);
                default:
                    return Equals(a, b);
            }
        }

        [Fact]
        public void AnswersItsInterfaceCases()
        {
            var lines = File.ReadAllLines(TestFiles.ApiCases).Where(l => l.Length > 0).ToList();
            Assert.True(lines.Count > 30);
            foreach (var line in lines)
            {
                var c = (Dictionary<string, object>)ClipboardJson.ParseJson(line);
                var request = (Dictionary<string, object>)c["request"];
                var answer = (Dictionary<string, object>)c["answer"];
                var fn = (string)request["fn"];
                var args = (request.TryGetValue("args", out var a) ? (Dictionary<string, object>)a : new Dictionary<string, object>());
                // The arguments as JSON again: the cases' own parsed values, written back.
                var json = Write(args);
                if (answer.TryGetValue("error", out var error))
                {
                    var e = Assert.Throws<CommonException>(() => Common.Call(fn, json));
                    Assert.Equal(error, e.Message);
                }
                else
                {
                    Assert.True(Same(answer["result"], Common.Call(fn, json)), fn);
                }
            }
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

        [Theory]
        [InlineData("MediaPlayer.fsm")]
        [InlineData("Order.fsm")]
        [InlineData("Payment.fsm")]
        public void WritesTheExamplesBackAsTheyAre(string name)
        {
            var text = TestFiles.Example(name);
            var layout = TestFiles.Example(name + ".layout");
            var read = TestFiles.Read(text, layout);
            Assert.NotEmpty(read.Lines);
            var w = Format.WriteMachine(read.Model);
            Assert.Empty(w.Lost);
            Assert.Equal(text, w.Model);
            Assert.Equal(layout, w.Layout);
        }

        [Fact]
        public void ChecksTextThroughTheSidecar()
        {
            Assert.Equal("a() && !b()", Expressions.CheckGuard("a()&&!b()", false).Value);
            Assert.False(Expressions.CheckGuard("else", false).Ok);
            Assert.Equal(new[] { "go", "after(2s)" }, Expressions.CheckList("go, after( 2 s )", Expressions.CheckTrigger).Value);
        }
    }
}
