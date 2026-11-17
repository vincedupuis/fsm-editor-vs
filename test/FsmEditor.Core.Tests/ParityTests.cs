using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FsmEditor.Core;
using Xunit;

namespace FsmEditor.Core.Tests
{
    /// <summary>
    /// Parity with FSM Editor for VS Code beyond the file format (whose reader and validator are checked against
    /// fsm-cli's test cases, <see cref="CasesTests"/>): the grammar checks of fixtures/grammar.json, made from the
    /// reference by the umbrella repo's parity script, and the reading of old files as JavaScript reads them.
    /// </summary>
    public class ParityTests
    {
        private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
        private static readonly string Examples = Path.Combine(AppContext.BaseDirectory, "examples");

        private static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

        private static void AssertCheck<T>(JsonElement expected, Check<T> actual, Func<T, string> format, string what)
        {
            Assert.True(expected.GetProperty("ok").GetBoolean() == actual.Ok, $"{what}: expected ok={expected.GetProperty("ok")}, got {actual.Error}");
            if (actual.Ok)
            {
                var value = expected.GetProperty("value");
                var text = value.ValueKind == JsonValueKind.Array ? string.Join("|", value.EnumerateArray().Select(e => e.GetString())) : value.GetString();
                Assert.Equal(text, format(actual.Value));
            }
            else
            {
                Assert.Equal(expected.GetProperty("error").GetString(), actual.Error);
            }
        }

        [Fact]
        public void GrammarMatches()
        {
            var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "grammar.json"))).RootElement;
            foreach (var c in expected.GetProperty("grammar").EnumerateArray())
            {
                var t = c.GetProperty("text").GetString();
                AssertCheck(c.GetProperty("condition"), Expressions.CheckCondition(t), s => s, $"condition '{t}'");
                AssertCheck(c.GetProperty("guard"), Expressions.CheckGuard(t, true), s => s, $"guard '{t}'");
                AssertCheck(c.GetProperty("guardNoElse"), Expressions.CheckGuard(t, false), s => s, $"guard without else '{t}'");
                AssertCheck(c.GetProperty("actions"), Expressions.CheckActions(t), s => s, $"actions '{t}'");
                AssertCheck(c.GetProperty("event"), Expressions.CheckEvent(t), s => s, $"event '{t}'");
                AssertCheck(c.GetProperty("name"), Expressions.CheckName(t), s => s, $"name '{t}'");
                AssertCheck(c.GetProperty("trigger"), Expressions.CheckTrigger(t), s => s, $"trigger '{t}'");
                AssertCheck(c.GetProperty("triggers"), Expressions.CheckList(t + ", after(1h)", Expressions.CheckTrigger), l => string.Join("|", l), $"triggers '{t}'");
                var ms = c.GetProperty("ms");
                Assert.Equal(ms.ValueKind == JsonValueKind.Null ? (double?)null : ms.GetDouble(), Expressions.TimeTriggerMs(t));
            }
        }

        [Fact]
        public void ExamplesValidateWithTheirSubmachines()
        {
            var files = Directory.GetFiles(Examples, "*.fsm");
            MachineSummary SummaryOf(string f) => File.Exists(f) ? MachineSummary.TryRead(Read(f)) : null;
            foreach (var path in files)
            {
                var model = Format.ReadModel(Read(path)).Model;
                var machines = SubmachineResolver.ListMachines(path, files, SummaryOf, Path.GetFileName);
                var submachines = SubmachineResolver.Resolve(path, model, machines, SummaryOf, Path.GetFileName);
                Assert.Empty(Validation.Validate(model, submachines));
            }
            var order = Format.ReadModel(Read(Path.Combine(Examples, "Order.fsm"))).Model;
            var list = SubmachineResolver.ListMachines(Path.Combine(Examples, "Order.fsm"), files, SummaryOf, Path.GetFileName);
            Assert.Equal(new[] { "MediaPlayer.fsm", "Payment.fsm" }, list.Select(m => m.Href));
            Assert.Equal(new[] { "retry", "failed", "cancelled" }, list.Single(m => m.Name == "Payment").Points.Select(p => p.Id));
            Assert.True(SubmachineResolver.Resolve(Path.Combine(Examples, "Order.fsm"), order, new List<MachineInfo>(), SummaryOf, f => f)["Payment.fsm"].Found);
        }

        [Fact]
        public void CharacterReferencesReadAsInTheOtherEditions()
        {
            var none = new Dictionary<string, string>();
            Assert.Equal("\uD800", XmlTree.Parse("<a b=\"&#xD800;\"/>", none).Attr("b"));
            Assert.Equal("\U0001F600", XmlTree.Parse("<a b=\"&#128512;\"/>", none).Attr("b"));
            foreach (var bad in new[] { "&#x110000;", "&#xFFFFFFFF;", "&#99999999999;" })
            {
                var e = Assert.Throws<XmlParseException>(() => XmlTree.Parse($"<a>\n<b c=\"{bad}\"/></a>", none));
                Assert.Equal($"Line 2: Unknown entity {bad}.", e.Message);
            }
        }

        [Fact]
        public void SubmachinePathsThatAreNotPathsAreNotFound()
        {
            var model = FsmModel.CreateDefault("M");
            model.Vertices[1].Submachine = "a\0b.fsm";
            var info = SubmachineResolver.Resolve(Path.Combine(Examples, "M.fsm"), model, new List<MachineInfo>(), _ => null, f => f);
            Assert.False(info["a\0b.fsm"].Found);
            Assert.Equal("sm", Hrefs.Resolve(Path.Combine(Examples, "M.fsm"), "a.fsm#sm#x").Id);
        }

        [Fact]
        public void OldFilesReadNumbersAsJavaScriptDoes()
        {
            Assert.Equal(5, Num.Hypot(3, -4));
            // Math.hypot(1.5, 2.5) in JavaScript; Math.Sqrt(1.5 * 1.5 + 2.5 * 2.5) is 2.9154759474226504.
            Assert.Equal(2.91547594742265, Num.Hypot(1.5, 2.5));
            Assert.Equal(16, Num.Parse("0x10"));
            Assert.Equal(5, Num.Parse(" 5 "));
            Assert.Equal(0, Num.Parse("5px"));
            Assert.Equal(0, Num.Parse(null));
            Assert.Equal("Infinity", Num.Js(double.PositiveInfinity));
            Assert.Equal("1e+21", Num.Format(1e21));
            // +"1e400" || 0 is Infinity in xmi.ts: the state keeps it, and converting reports it.
            var old = File.ReadAllText(Path.Combine(Fixtures, "old", "Payment.fsm"));
            var i = old.IndexOf("width=\"", StringComparison.Ordinal);
            var infinite = old.Substring(0, i) + "width=\"1e400\" data-width=" + old.Substring(i + 6);
            Assert.Contains(Conversion.ConvertGroup(new[] { new ConvertInput("/x/Payment.fsm", infinite) })[0].Losses, l => l.Contains("not a number"));
        }

        [Fact]
        public void ItemTemplateIsTheDefaultMachine()
        {
            var template = Read(Path.Combine(AppContext.BaseDirectory, "templates", "StateMachine.fsm"));
            var layout = Read(Path.Combine(AppContext.BaseDirectory, "templates", "StateMachine.fsm.layout"));
            var files = MachineFiles.Write(FsmModel.CreateDefault("Machine"));
            Assert.Equal(files.ModelText, template.Replace("$fileinputname$", "Machine"));
            Assert.Equal(files.LayoutText, layout);
        }
    }
}
