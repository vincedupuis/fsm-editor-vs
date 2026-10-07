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
    /// Compares the C# implementation with the reference results in fixtures/,
    /// produced by FSM Editor for VS Code (fixtures/README.md), so both editors
    /// read, write and validate files the same way.
    /// </summary>
    public class ParityTests
    {
        private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
        private static readonly string Examples = Path.Combine(AppContext.BaseDirectory, "examples");
        private static readonly JsonElement Expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "expected.json"))).RootElement;

        public static IEnumerable<object[]> ExampleFiles() =>
            new[] { "MediaPlayer.fsm", "Order.fsm", "Payment.fsm" }.Select(f => new object[] { f });

        public static IEnumerable<object[]> CaseNames() => new[] { "grammar", "structure", "protocol" }.Select(c => new object[] { c });

        private static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

        private static List<string> Format(IEnumerable<Issue> issues) =>
            issues.Select(i => $"{i.Severity.ToString().ToLowerInvariant()}|{i.Id}|{i.Message}").ToList();

        private static List<string> Format(JsonElement issues) =>
            issues.EnumerateArray().Select(i => $"{i.GetProperty("severity").GetString()}|{i.GetProperty("id").GetString()}|{i.GetProperty("message").GetString()}").ToList();

        [Theory]
        [MemberData(nameof(ExampleFiles))]
        public void ExamplesRoundTripUnchanged(string file)
        {
            var text = Read(Path.Combine(Examples, file));
            Assert.Equal(text, Xmi.ToXmi(Xmi.FromXmi(text)));
        }

        [Theory]
        [MemberData(nameof(CaseNames))]
        public void WritesTheSameXmi(string name)
        {
            var text = Read(Path.Combine(Fixtures, name + ".fsm"));
            Assert.Equal(Read(Path.Combine(Fixtures, name + ".roundtrip.xmi")), Xmi.ToXmi(Xmi.FromXmi(text)));
        }

        [Theory]
        [MemberData(nameof(ExampleFiles))]
        public void ExamplesValidateTheSame(string file)
        {
            var path = Path.Combine(Examples, file);
            var model = Xmi.FromXmi(Read(path));
            var files = Directory.GetFiles(Examples, "*.fsm");
            MachineSummary SummaryOf(string f) => File.Exists(f) ? MachineSummary.TryRead(Read(f)) : null;
            var machines = SubmachineResolver.ListMachines(path, files, SummaryOf, Path.GetFileName);
            var submachines = SubmachineResolver.Resolve(path, model, machines, SummaryOf, Path.GetFileName);
            Assert.Equal(Format(Expected.GetProperty("examples").GetProperty(file).GetProperty("issues")), Format(Validation.Validate(model, submachines)));
        }

        [Theory]
        [MemberData(nameof(CaseNames))]
        public void CasesValidateTheSame(string name)
        {
            var model = Xmi.FromXmi(Read(Path.Combine(Fixtures, name + ".fsm")));
            Assert.Equal(Format(Expected.GetProperty("cases").GetProperty(name).GetProperty("issues")), Format(Validation.Validate(model)));
        }

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
        public void ItemTemplateIsTheDefaultMachine()
        {
            var template = Read(Path.Combine(AppContext.BaseDirectory, "templates", "StateMachine.fsm"));
            Assert.Equal(Xmi.ToXmi(FsmModel.CreateDefault("Machine")), template.Replace("$fileinputname$", "Machine"));
        }

        [Fact]
        public void GrammarMatches()
        {
            foreach (var c in Expected.GetProperty("grammar").EnumerateArray())
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
    }
}
