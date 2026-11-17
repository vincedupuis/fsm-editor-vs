using System;
using System.Collections.Generic;
using System.Linq;
using FsmEditor.Core;
using Xunit;
using static FsmEditor.Core.Tests.TestFiles;

namespace FsmEditor.Core.Tests
{
    /// <summary>The file format (the VS Code edition's test/unit/format.test.ts).</summary>
    public class FormatTests
    {
        private static readonly string[] ExampleNames = { "MediaPlayer", "Order", "Payment" };

        /// <summary>A model of most features: composite and orthogonal states, points, history, layout of every kind.</summary>
        private static FsmModel Featured()
        {
            var b = new ModelBuilder("Featured");
            b.State("A", "r_root", v =>
            {
                v.X = 40.04; v.Y = 60.06; v.W = 140; v.H = 60; v.Entry = "a(); b()"; v.Color = "#112233";
                v.ItemColors = new Dictionary<string, string> { ["entry"] = "#445566" }; v.Group = "g1";
            });
            b.State("B", "r_root", v =>
            {
                v.X = 230; v.Y = 60; v.W = 120; v.H = 60; v.Group = "g1"; v.Deferrable = new List<string> { "x", "y" };
            });
            b.State("C", "r_root", v =>
            {
                v.X = 400.33; v.Y = 40; v.W = 340; v.H = 260; v.RegionLayout = RegionLayout.Horizontal;
                v.ItemColors = new Dictionary<string, string> { ["background"] = "#eeeeee" };
            }, 2);
            b.Model.Vertices.First(v => v.Id == "C").Regions[0].Size = 170.15;
            b.State("C1", "C_r0", v => { v.X = 420; v.Y = 120; v.W = 100; v.H = 50; });
            b.State("C2", "C_r1", v => { v.X = 600; v.Y = 120; v.W = 100; v.H = 50; });
            var init = b.Vertex(VertexType.Initial, "C_r0", v => { v.X = 430; v.Y = 90; v.W = 20; v.H = 20; });
            b.T(init, "C1", t => { t.Action = "go()"; t.Points = new List<PointD> { new PointD(440, 140.05) }; });
            b.Init("C2", "C_r1");
            b.Vertex(VertexType.DeepHistory, "C_r1", v => { v.X = 660; v.Y = 70; v.W = 26; v.H = 26; }, "H");
            b.T("H", "C2");
            b.Vertex(VertexType.ExitPoint, "C", v => { v.Name = "out"; v.X = 732; v.Y = 150; v.W = 16; v.H = 16; v.Color = "#00ff00"; }, "Cx");
            b.T("Cx", "A");
            b.Vertex(VertexType.EntryPoint, "r_root", v => { v.Name = "start"; v.X = 10; v.Y = 10; v.W = 16; v.H = 16; }, "M_in");
            b.T("M_in", "A");
            b.Vertex(VertexType.Choice, "r_root", v => { v.X = 260; v.Y = 200; v.W = 28; v.H = 28; }, "ch");
            b.Init("A");
            b.T("A", "B", t =>
            {
                t.Triggers = new List<string> { "go", "after(1s)" }; t.Guard = "ok()"; t.Action = "log()"; t.Points = new List<PointD> { new PointD(200, 50) };
                t.LabelOffset = new PointD(3.25, -8); t.Color = "#aa0000"; t.ItemColors = new Dictionary<string, string> { ["guard"] = "#00aa00" };
            });
            b.T("B", "ch", t => t.Triggers = new List<string> { "pick" });
            b.T("ch", "C", t => t.Guard = "p()");
            b.T("ch", "H", t => t.Guard = "else");
            b.T("A", "A", t => { t.Kind = TransitionKind.Internal; t.Triggers = new List<string> { "tick" }; t.Action = "t()"; });
            b.T("A", "A", t => { t.Triggers = new List<string> { "again" }; t.Points = new List<PointD> { new PointD(160, 50), new PointD(190, 50), new PointD(190, 90) }; });
            b.T("C1", "C", t => { t.Kind = TransitionKind.Local; t.Triggers = new List<string> { "up" }; t.Points = new List<PointD> { new PointD(470, 280) }; });
            b.Vertex(VertexType.Comment, "r_root", v =>
            {
                v.Text = "Note \"quoted\"\n\twith a tab"; v.Anchors = new List<string> { "A", "Cx", "H" }; v.X = 40; v.Y = 300; v.W = 160; v.H = 64;
            }, "n");
            var ab = b.Model.Transitions.First(x => x.Source == "A" && x.Target == "B");
            b.Model.Vertices.First(v => v.Id == "n").Anchors.Add(ab.Id);
            b.Model.Documentation = "Doc with \"quotes\" and é";
            return b.Model.Normalize();
        }

        // ---------------------------------------------------------------- examples

        [Theory]
        [InlineData("MediaPlayer")]
        [InlineData("Order")]
        [InlineData("Payment")]
        public void ExamplesReadAndWriteTheSameBytes(string name)
        {
            var r = Read(Example(name + ".fsm"), Example(name + ".fsm.layout"));
            Assert.Equal(0, r.Placed);
            var w = Write(r.Model);
            Assert.Equal(Example(name + ".fsm"), w.Model);
            Assert.Equal(Example(name + ".fsm.layout"), w.Layout);
        }

        [Fact]
        public void ExamplesAreTheOldOnesConverted()
        {
            var results = Conversion.ConvertGroup(ExampleNames.Select(n => new ConvertInput($"/x/{n}.fsm", Old(n))).ToList());
            for (var i = 0; i < ExampleNames.Length; i++)
            {
                Assert.Equal(Example(ExampleNames[i] + ".fsm"), results[i].ModelText);
                Assert.Equal(Example(ExampleNames[i] + ".fsm.layout"), results[i].LayoutText);
                Assert.Empty(results[i].Renames);
            }
            Assert.Equal(new[] { "the context 'MediaPlayer' of the state machine" }, results[0].Losses);
            Assert.Contains("{ \"type\": \"internal\", \"trigger\": \"volumeChanged\", \"action\": \"updateVolume()\" }", Example("MediaPlayer.fsm"));
            Assert.Contains("\"submachine\": \"Payment.fsm\"", Example("Order.fsm"));
        }

        [Fact]
        public void ExamplesAreValid()
        {
            var payment = new SubmachineInfo
            {
                Found = true, Name = "Payment",
                Points = new List<ConnectionPointInfo> { new ConnectionPointInfo("retry", "retry", PointKind.Entry), new ConnectionPointInfo("failed", "failed", PointKind.Exit), new ConnectionPointInfo("cancelled", "cancelled", PointKind.Exit) },
            };
            foreach (var name in ExampleNames)
            {
                var issues = Validation.Validate(Read(Example(name + ".fsm")).Model, new Dictionary<string, SubmachineInfo> { ["Payment.fsm"] = payment });
                Assert.Empty(issues);
            }
        }

        // ---------------------------------------------------------------- writing

        [Fact]
        public void WriteReadWriteGivesTheSameBytesAndOneSaveSettles()
        {
            var w1 = Write(Featured());
            var r = Read(w1.Model, w1.Layout);
            var w2 = Write(r.Model);
            Assert.Equal(w1.Model, w2.Model);
            Assert.Equal(w1.Layout, w2.Layout);
            var w3 = Write(Read(w2.Model, w2.Layout).Model);
            Assert.Equal(w2.Layout, w3.Layout);
        }

        [Fact]
        public void KeepsTheDrawing()
        {
            var m = Featured();
            var w = Write(m);
            var r = Read(w.Model, w.Layout).Model;
            Vertex ByName(string n) => r.Vertices.First(v => v.Name == n);
            Assert.Equal((40.0, 60.1), (ByName("A").X, ByName("A").Y));
            Assert.Equal((400.3, 340.0, (RegionLayout?)RegionLayout.Horizontal), (ByName("C").X, ByName("C").W, ByName("C").RegionLayout));
            Assert.Equal(170.2, ByName("C").Regions[0].Size);
            Assert.Equal(new Dictionary<string, string> { ["entry"] = "#445566" }, ByName("A").ItemColors);
            Assert.Equal(ByName("A").Group, ByName("B").Group);
            var t = r.Transitions.First(x => x.Triggers.Contains("go"));
            Assert.Equal(new[] { new PointD(200, 50) }, t.Points);
            Assert.Equal(new PointD(3.3, -8), t.LabelOffset);
            Assert.Equal("#aa0000", t.Color);
            Assert.Equal(new Dictionary<string, string> { ["guard"] = "#00aa00" }, t.ItemColors);
            var loop = r.Transitions.First(x => x.Triggers.Contains("again"));
            Assert.Equal(new[] { new PointD(160, 50), new PointD(190, 50), new PointD(190, 90) }, loop.Points);
            var point = r.Vertices.First(v => v.Type == VertexType.ExitPoint);
            Assert.Equal(("out", 732.0, 150.0, "#00ff00"), (point.Name, point.X, point.Y, point.Color));
        }

        [Fact]
        public void WritesTheFormattingOfTheFormat()
        {
            var w = Write(Featured());
            Assert.Matches("(?m)^ {2}\"documentation\": \"Doc with \\\\\"quotes\\\\\" and é\",$", w.Model);
            Assert.Contains("\"text\": \"Note \\\"quoted\\\"\\n\\twith a tab\"", w.Model);
            Assert.Matches("(?m)^ {8}\\{ \"type\": \"external\", \"trigger\": \"go, after\\(1s\\)\", \"guard\": \"ok\\(\\)\", \"action\": \"log\\(\\)\", \"target\": \"B\" \\},$", w.Model);
            Assert.Matches("(?m)^ {12}\\{ \"x\": 200, \"y\": 50 \\}$", w.Layout);
            Assert.Matches("(?m)^ {6}\"group\": \"g1\",$", w.Layout);
            Assert.EndsWith("}\n", w.Model);
            Assert.DoesNotContain("\r", w.Model);
            Assert.Matches("\"name\": \"choice1\\*\", \"type\": \"choice\"|\"name\": \"choice1\\*\",\\n\\s+\"type\": \"choice\"", w.Model);
            Assert.Contains("\"name\": \"region1*\"", w.Model);
        }

        /// <summary>The lines that differ between two texts of the same length in lines.</summary>
        private static List<(string, string)> DiffLines(string a, string b)
        {
            var x = a.Split('\n');
            var y = b.Split('\n');
            Assert.Equal(x.Length, y.Length);
            return x.Select((l, i) => (l, y[i])).Where(p => p.Item1 != p.Item2).ToList();
        }

        [Fact]
        public void PositionsAreRelative()
        {
            var m = Featured();
            var before = Write(m).Layout;
            var moved = m.Clone();
            // Moving C moves what it contains and the bend points between them, as the editor does.
            var inside = new HashSet<string>(new[] { "C", "C1", "C2", "H", "Cx" }.Concat(moved.Vertices.Where(v => v.Parent == "C_r0" || v.Parent == "C_r1").Select(v => v.Id)));
            foreach (var v in moved.Vertices.Where(v => inside.Contains(v.Id)))
            {
                v.X += 33;
                v.Y -= 7;
            }
            foreach (var t in moved.Transitions.Where(t => inside.Contains(t.Source) && inside.Contains(t.Target)))
            {
                t.Points = t.Points.Select(p => new PointD(p.X + 33, p.Y - 7)).ToList();
            }
            Assert.Equal(new[] { ("      \"x\": 400.3,", "      \"x\": 433.3,"), ("      \"y\": 40,", "      \"y\": 33,") }, DiffLines(before, Write(moved).Layout));
            // Moving B alone (one end of A -> B and of B -> choice): only B's own place.
            var one = m.Clone();
            one.Vertices.First(v => v.Id == "B").X += 50;
            Assert.Equal(new[] { ("      \"x\": 230,", "      \"x\": 280,") }, DiffLines(before, Write(one).Layout));
        }

        [Fact]
        public void KeepsAReferenceThatNamesNothing()
        {
            var text = Example("Order.fsm").Replace("\"target\": \"Shipped\"", "\"target\": \"Shiped\"");
            var r = Read(text);
            var t = r.Model.Transitions.FirstOrDefault(x => x.Target == Format.Unresolved + "Shiped");
            Assert.NotNull(t);
            Assert.Contains(Validation.Validate(r.Model), i => i.Id == t.Id && i.Message == "Transition target 'Shiped' does not exist.");
            Assert.Equal(text, Write(r.Model).Model);
        }

        [Fact]
        public void ReportsWhatTheFormatCanNotHold()
        {
            var b = new ModelBuilder("Lost");
            b.State("A");
            b.Init("A");
            b.Vertex(VertexType.Initial, "r_root", id: "i2");
            Assert.Equal(new[] { "an unnamed initial pseudostate: a region has at most one" }, Format.WriteMachine(b.Model).Lost);
        }

        // ---------------------------------------------------------------- reading

        [Fact]
        public void ReportsProblemsWithTheirLines()
        {
            var r = Format.ReadModel("{\n  \"format\": 1,\n  \"name\": \"X\",\n  \"type\": \"behavioral\",\n  \"elements\": [\n    { \"name\": \"A\", \"type\": \"state\", \"gaurd\": \"x\" }\n  ]\n}\n");
            Assert.Null(r.Model);
            Assert.Equal(new[] { "line 6: Unknown field 'gaurd' in a state." }, r.Problems.Select(p => p.ToString()));
            Assert.Equal(new[] { "line 1: This file is in format 3: this version reads format 1 only." },
                Format.ReadModel("{ \"format\": 3, \"name\": \"X\", \"type\": \"behavioral\" }").Problems.Select(p => p.ToString()));
        }

        [Fact]
        public void ReadsCrlfAndASchemaFieldWhichIsNotWrittenBack()
        {
            var text = ReplaceFirst(Example("Payment.fsm"), "{\n", "{\n  \"$schema\": \"../schemas/fsm.schema.json\",\n").Replace("\n", "\r\n");
            Assert.Equal(Example("Payment.fsm"), Write(Read(text).Model).Model);
        }

        private static string ReplaceFirst(string s, string a, string b)
        {
            var i = s.IndexOf(a, StringComparison.Ordinal);
            return s.Substring(0, i) + b + s.Substring(i + a.Length);
        }

        [Fact]
        public void PlacesElementsWithoutALayoutEntryTheSameWayOnEveryOpen()
        {
            var layout = Example("MediaPlayer.fsm.layout");
            // Fault's entry, the last element of the top level.
            var start = layout.IndexOf("    {\n      \"name\": \"Fault\"", StringComparison.Ordinal);
            var end = layout.IndexOf("\n    }", start, StringComparison.Ordinal) + 6;
            var without = layout.Substring(0, start - 2) + layout.Substring(end);
            var r1 = Read(Example("MediaPlayer.fsm"), without);
            var r2 = Read(Example("MediaPlayer.fsm"), without);
            Assert.Null(r1.LayoutProblem);
            Assert.Equal(1, r1.Placed);
            var fault = r1.Model.Vertices.First(v => v.Name == "Fault");
            Assert.Equal((r2.Model.Vertices.First(v => v.Name == "Fault").X, 20.0, 140.0, 60.0), (fault.X, fault.Y, fault.W, fault.H));
            // No layout file: everything, level by level; the model file is untouched.
            var none = Read(Example("MediaPlayer.fsm"));
            Assert.Equal(none.Model.Vertices.Count, none.Placed);
            Assert.Equal(Example("MediaPlayer.fsm"), Write(none.Model).Model);
        }

        [Fact]
        public void IgnoresLayoutEntriesThatMatchNothingAndAnUnreadableLayoutFile()
        {
            var extra = ReplaceFirst(Example("Payment.fsm.layout"), "\"elements\": [", "\"elements\": [\n    { \"name\": \"Gone\", \"x\": 1, \"y\": 2 },");
            Assert.Equal(Example("Payment.fsm.layout"), Write(Read(Example("Payment.fsm"), extra).Model).Layout);
            var r = Read(Example("Payment.fsm"), "{ \"format\": 1, \"elements\": 3 }");
            Assert.Equal("line 1: Field 'elements' must be a list.", r.LayoutProblem);
            Assert.Equal(r.Model.Vertices.Count, r.Placed);
        }

        [Fact]
        public void DerivesTheIdsFsmCliGives()
        {
            var m = ExampleModel("MediaPlayer");
            m.Vertices.First(v => v.Name == "Fault").Id = "v_new";
            var t = m.Transitions.First(x => x.Source == "Paused" && x.Triggers.Contains("stop"));
            t.Id = "t_new";
            foreach (var x in m.Transitions)
            {
                if (x.Source == "Fault") x.Source = "v_new";
                if (x.Target == "Fault") x.Target = "v_new";
            }
            var d = Format.DerivedIds(m);
            Assert.Equal("Fault", d["v_new"]);
            Assert.Equal("Paused#2", d["t_new"]);
            Assert.Equal("On.deepHistory", d["On.deepHistory"]);
        }

        [Fact]
        public void AppliesALayoutToAModelReadAlone()
        {
            var r = Format.ReadModel(Example("Order.fsm"));
            Format.ApplyLayout(r, Example("Order.fsm.layout"));
            Assert.Equal(0, r.Placed);
            Assert.Equal(Example("Order.fsm.layout"), Write(r.Model).Layout);
        }

        // ---------------------------------------------------------------- review fixes

        private static string ModelText(string body) => "{\n  \"format\": 1,\n  \"name\": \"M\",\n  \"type\": \"behavioral\",\n" + body + "\n}\n";

        [Fact]
        public void ResolvesCommentAnchors()
        {
            var r = Read(ModelText(@"  ""initial"": { ""target"": ""A"" },
  ""elements"": [
    { ""name"": ""A"", ""type"": ""state"", ""transitions"": [{ ""type"": ""external"", ""trigger"": ""go"", ""target"": ""B"" }, { ""type"": ""internal"", ""trigger"": ""tick"" }] },
    { ""name"": ""B"", ""type"": ""state"" },
    { ""name"": ""c"", ""type"": ""comment"", ""anchors"": [""A"", { ""source"": ""A"", ""trigger"": ""go"", ""target"": ""B"" }, { ""source"": ""A"", ""trigger"": ""tick"" }, ""Nope"", { ""source"": ""A"", ""trigger"": ""x"", ""target"": ""B"" }] }
  ]"));
            var anchors = r.Model.Vertices.First(v => v.Name == "c").Anchors;
            Assert.Equal(new[] { "A", "A#1", "A#2" }, anchors.Take(3));
            Assert.Equal(Format.Unresolved + "Nope", anchors[3]);
            Assert.StartsWith(Format.UnresolvedAnchor, anchors[4]);
            var w = Format.WriteMachine(r.Model);
            Assert.Empty(w.Lost);
            Assert.Contains("\"Nope\"", w.Model);
            Assert.Contains("{ \"source\": \"A\", \"trigger\": \"x\", \"target\": \"B\" }", w.Model);
        }

        [Fact]
        public void KeepsTheLayoutOfEachOfTwoTransitionsWithTheSameTriggerAndTarget()
        {
            var b = new ModelBuilder("M");
            b.Vertex(VertexType.Choice, "r_root", v => { v.Name = "c"; v.X = 10; v.Y = 10; v.W = 28; v.H = 28; }, "c");
            b.State("Next", "r_root", v => { v.X = 200; v.Y = 10; });
            b.Init("Next");
            b.T("c", "Next", t => { t.Guard = "x()"; t.Action = "a()"; });
            b.T("c", "Next", t => { t.Guard = "else"; t.Action = "b()"; t.Points = new List<PointD> { new PointD(100, 120) }; t.Color = "#ff0000"; });
            var w = Write(b.Model);
            var back = Read(w.Model, w.Layout).Model.Transitions.Where(t => t.Source == "c").ToList();
            Assert.Equal(new[] { ("x()", 0, ""), ("else", 1, "#ff0000") }, back.Select(t => (t.Guard, t.Points.Count, t.Color)));
            Assert.Equal(w.Layout, Write(Read(w.Model, w.Layout).Model).Layout);
        }

        [Fact]
        public void FindsALayoutEntryByTheTriggerAsTheModelFileWritesIt()
        {
            var text = ModelText(@"  ""initial"": { ""target"": ""A"" },
  ""elements"": [{ ""name"": ""A"", ""type"": ""state"", ""transitions"": [{ ""type"": ""external"", ""trigger"": ""a,b"", ""target"": ""B"" }] }, { ""name"": ""B"", ""type"": ""state"" }]");
            var layout = "{ \"format\": 1, \"elements\": [{ \"name\": \"A\", \"x\": 10, \"y\": 10, \"transitions\": [{ \"trigger\": \"a,b\", \"target\": \"B\", \"bends\": [{ \"x\": 5, \"y\": 5 }] }] }] }";
            var t = Read(text, layout).Model.Transitions.First(x => x.Source == "A");
            Assert.Equal(new[] { new PointD(5, 5) }, t.Points);
        }

        [Fact]
        public void AnInitialPseudostatesColorIsItsTransitions()
        {
            var b = new ModelBuilder("M");
            b.State("A", "r_root", v => { v.X = 100; v.Y = 10; });
            b.Init("A");
            b.Model.Transitions[0].Color = "#00ff00";
            var w = Write(b.Model);
            var r = Read(w.Model, w.Layout).Model;
            Assert.Equal("", r.Vertices.First(v => v.Type == VertexType.Initial).Color);
            Assert.Equal("#00ff00", r.Transitions[0].Color);
        }

        [Fact]
        public void PlacesAStateGrownToHoldWhatItContainsBeforeTheElementsBesideIt()
        {
            var r = Read(ModelText(@"  ""initial"": { ""target"": ""A"" },
  ""elements"": [
    { ""name"": ""A"", ""type"": ""state"", ""initial"": { ""target"": ""A1"" }, ""elements"": [{ ""name"": ""A1"", ""type"": ""state"" }, { ""name"": ""A2"", ""type"": ""state"" }, { ""name"": ""A3"", ""type"": ""state"" }] },
    { ""name"": ""B"", ""type"": ""state"" }
  ]"));
            var a = r.Model.Vertices.First(v => v.Name == "A");
            var bState = r.Model.Vertices.First(v => v.Name == "B");
            Assert.True(bState.X >= a.X + a.W, $"B (x {bState.X}) is beside A ({a.X}..{a.X + a.W})");
        }

        [Fact]
        public void WritesPlacesThatAreNumbersUnderAStateWhosePlaceIsNotOne()
        {
            var b = new ModelBuilder("M");
            b.State("A", "r_root", v => { v.X = double.PositiveInfinity; v.Y = 10; v.W = 300; v.H = 200; }, 1);
            b.State("B", "A_r0", v => { v.X = 100; v.Y = 50; });
            var m = b.Model.Clone();
            Format.AssignNames(m);
            var w = Format.WriteMachine(m);
            Assert.Equal(new[] { "the place of state 'A', which is not a number" }, w.Lost);
            Assert.Null(Read(w.Model, w.Layout).LayoutProblem);
        }

        [Fact]
        public void KeepsANameUsedTwiceWithoutFixAndNumbersItWhenConverting()
        {
            var b = new ModelBuilder("M");
            b.State("Idle");
            b.State("Idle", id: "Idle2");
            var m = b.Model.Clone();
            Assert.Empty(Format.AssignNames(m));
            Assert.Equal(new[] { "Idle", "Idle" }, m.Vertices.Select(v => v.Name));
            Assert.Equal(new[] { "Idle2" }, Format.AssignNames(m, true).Select(x => x.To));
        }

        [Fact]
        public void ResolvesATargetOrAnAnchorThatNamedNothingOnceItNamesSomething()
        {
            var r = Read(ModelText(@"  ""initial"": { ""target"": ""A"" },
  ""elements"": [
    { ""name"": ""A"", ""type"": ""state"", ""transitions"": [{ ""type"": ""external"", ""trigger"": ""go"", ""target"": ""Foo"" }] },
    { ""name"": ""c"", ""type"": ""comment"", ""anchors"": [""Foo"", { ""source"": ""A"", ""trigger"": ""go"", ""target"": ""Foo"" }] }
  ]")).Model;
            var t = r.Transitions.First(x => x.Source == "A");
            Assert.Equal(Format.Unresolved + "Foo", t.Target);
            Assert.False(Format.ResolveReferences(r, Format.WriteMachine(r)));
            r.Vertices.Add(new Vertex { Id = "v_foo", Type = VertexType.State, Name = "Foo", Parent = r.Regions[0].Id, X = 300, Y = 10, W = 140, H = 60 });
            Assert.True(Format.ResolveReferences(r, Format.WriteMachine(r)));
            Assert.Equal("v_foo", t.Target);
            Assert.Equal(new[] { "v_foo", t.Id }, r.Vertices.First(v => v.Name == "c").Anchors);
        }

        // ---------------------------------------------------------------- JSON

        [Fact]
        public void JsonReaderGivesLinesAndTheEditorsMessages()
        {
            var n = Json.Parse("\uFEFF{\n  \"a\": [1, 2.5e1, -0],\n  \"b\": { \"c\": \"\\u00e9\\ud83d\\ude00\" }\n}");
            Assert.Equal(2, n.Field("a").Line);
            Assert.Equal(25, n.Field("a").Value.Items[1].Number);
            Assert.Equal("é😀", n.Field("b").Value.Field("c").Value.Str);
            void Fails(string text, int line, string message)
            {
                var e = Assert.Throws<JsonSyntaxException>(() => Json.Parse(text));
                Assert.Equal((line, message), (e.Line, e.Message));
            }
            Fails("{ \"a\": 1, \"a\": 2 }", 1, "Field 'a' is given twice.");
            Fails("{\n\"a\": 01 }", 2, "Invalid number.");
            Fails("{ \"a\": 1e400 }", 1, "Invalid number.");
            Fails("[\"a\tb\"]", 1, "Control character '\\u0009' in a text: write it as an escape.");
            Fails("[\"\\ud800\"]", 1, "Invalid escape in a text.");
            Fails("{\n", 2, "Unexpected end of the file.");
            Fails("[1] x", 1, "Unexpected character 'x'.");
            Fails("\u00a0", 1, "Unexpected character '\\u00a0'.".Replace("\\u00a0", "\u00a0"));
        }

        [Fact]
        public void IsOldFormatSeesXml()
        {
            Assert.True(Format.IsOldFormat(Old("Order")));
            Assert.True(Format.IsOldFormat("\uFEFF \n<x/>"));
            Assert.False(Format.IsOldFormat(Example("Order.fsm")));
            Assert.False(Format.IsOldFormat(""));
        }
    }
}
