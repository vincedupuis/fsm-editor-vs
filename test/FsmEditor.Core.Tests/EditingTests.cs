using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FsmEditor.Core;
using Xunit;

namespace FsmEditor.Core.Tests
{
    public class EditingTests
    {
        private static DiagramSession NewSession()
        {
            var s = new DiagramSession(FsmModel.CreateDefault("Test"));
            return s;
        }

        private static DiagramSession Example(string file) =>
            new DiagramSession(Xmi.FromXmi(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", file))));

        [Fact]
        public void CreatesStatesInTheRegionUnderThePoint()
        {
            var s = NewSession();
            var commits = 0;
            s.Committed += (o, e) => commits++;
            var comp = s.CreateVertex("composite", new PointD(500, 300));
            Assert.Equal("Composite", comp.Name);
            Assert.Single(comp.Regions);
            var inner = s.CreateVertex("state", new PointD(comp.X + 60, comp.Y + comp.H - 40));
            Assert.Equal(comp.Regions[0].Id, inner.Parent);
            Assert.Equal("State", inner.Name);
            Assert.Equal(2, commits);
            Assert.Equal(new HashSet<string> { inner.Id }, s.Selection);
        }

        [Fact]
        public void MachineLevelEntryPointsAreNamed()
        {
            var s = NewSession();
            var p = s.CreateVertex("entryPoint", new PointD(900, 900));
            Assert.Equal(s.RootRegion, p.Parent);
            Assert.Equal("in", p.Name);
            Assert.Single(s.Model.MachineConnectionPoints());
        }

        [Fact]
        public void ConnectionPointRefsNeedASubmachineState()
        {
            var s = NewSession();
            string message = null;
            s.Message += (o, m) => message = m;
            Assert.Null(s.CreateVertex("connectionPointRef", new PointD(900, 900)));
            Assert.Equal("Connection point references go on the border of a submachine state.", message);
        }

        [Fact]
        public void ConnectRefusesInvalidTransitions()
        {
            var s = NewSession();
            var messages = new List<string>();
            s.Message += (o, m) => messages.Add(m);
            var fin = s.CreateVertex("final", new PointD(600, 100));
            var idle = s.Index.Vertex("v_idle");
            Assert.Null(s.Connect(fin, idle.Id));
            Assert.Null(s.Connect(idle, "v_init"));
            var t = s.Connect(idle, fin.Id);
            Assert.NotNull(t);
            Assert.Equal(new[] { "A final state cannot have outgoing transitions.", "An initial pseudostate cannot be the target of a transition." }, messages);
        }

        [Fact]
        public void DeletingAStateRemovesItsContentsTransitionsAndAnchors()
        {
            var s = Example("MediaPlayer.fsm");
            var composite = s.Model.Vertices.First(v => v.Type == VertexType.State && v.Regions.Count > 0);
            var inside = s.Index.Descendants(composite).Select(v => v.Id).ToList();
            Assert.NotEmpty(inside);
            s.Selection = new HashSet<string> { composite.Id };
            s.DeleteSelection();
            Assert.DoesNotContain(s.Model.Vertices, v => v.Id == composite.Id || inside.Contains(v.Id));
            Assert.DoesNotContain(s.Model.Transitions, t => t.Source == composite.Id || t.Target == composite.Id || inside.Contains(t.Source) || inside.Contains(t.Target));
            Assert.All(s.Model.Vertices, v => Assert.All(v.Anchors, a => Assert.True(s.Index.Vertex(a) != null || s.Index.Transition(a) != null)));
        }

        [Fact]
        public void PasteCopiesWithNewIdsAndOffset()
        {
            var s = Example("MediaPlayer.fsm");
            var composite = s.Model.Vertices.First(v => v.Type == VertexType.State && v.Regions.Count > 0);
            s.Selection = new HashSet<string> { composite.Id };
            var data = s.CollectSelection();
            var count = s.Model.Vertices.Count;
            var transitions = s.Model.Transitions.Count;

            // Through the clipboard text, as between two editors.
            var text = ClipboardJson.Write(data);
            s.Paste(ClipboardJson.TryRead(text));

            Assert.Equal(count + data.Vertices.Count, s.Model.Vertices.Count);
            Assert.Equal(transitions + data.Transitions.Count, s.Model.Transitions.Count);
            var copy = s.Index.Vertex(s.Selection.Single());
            Assert.NotEqual(composite.Id, copy.Id);
            Assert.Equal(composite.X + 20, copy.X);
            Assert.Equal(composite.Name, copy.Name);
            Assert.Equal(s.Index.Descendants(composite).Count, s.Index.Descendants(copy).Count);
            Assert.DoesNotContain(Validation.Validate(s.Model), i => i.Message.StartsWith("Duplicate id"));
        }

        [Fact]
        public void ClipboardJsonRoundTrips()
        {
            var s = Example("Order.fsm");
            s.Selection = new HashSet<string>(s.Model.Vertices.Select(v => v.Id));
            var data = s.CollectSelection();
            var back = ClipboardJson.TryRead(ClipboardJson.Write(data));
            Assert.Equal(ClipboardJson.Write(data), ClipboardJson.Write(back));
            Assert.Null(ClipboardJson.TryRead("hello"));
            Assert.Null(ClipboardJson.TryRead("{\"vertices\":[]}"));
        }

        [Fact]
        public void ReadsClipboardTextOfTheVsCodeEditor()
        {
            const string text = "{\"fsmClipboard\":1,\"vertices\":[{\"id\":\"v_a\",\"type\":\"state\",\"name\":\"A\",\"parent\":\"r_root\",\"x\":10,\"y\":20,\"w\":140,\"h\":60,\"regions\":[],\"entry\":\"go()\",\"color\":\"#e5484d\",\"itemColors\":{\"entry\":\"#30a46c\",\"exit\":\"bad\"}}," +
                "{\"id\":\"v_b\",\"type\":\"shallowHistory\",\"name\":\"\",\"parent\":\"r_root\",\"x\":200,\"y\":20,\"w\":26,\"h\":26,\"regions\":[]}]," +
                "\"transitions\":[{\"id\":\"t1\",\"source\":\"v_b\",\"target\":\"v_a\",\"kind\":\"external\",\"triggers\":[],\"guard\":\"\",\"effect\":\"\",\"points\":[{\"x\":1.5,\"y\":2}],\"labelOffset\":{\"x\":0,\"y\":-4},\"itemColors\":{\"guard\":\"#0090ff\"}}]}";
            var data = ClipboardJson.TryRead(text);
            Assert.Equal(2, data.Vertices.Count);
            Assert.Equal(VertexType.ShallowHistory, data.Vertices[1].Type);
            Assert.Equal("go()", data.Vertices[0].Entry);
            Assert.Equal(new PointD(1.5, 2), data.Transitions[0].Points[0]);
            Assert.Equal(new PointD(0, -4), data.Transitions[0].LabelOffset);
            Assert.Equal("#e5484d", data.Vertices[0].Color);
            Assert.Equal(new Dictionary<string, string> { ["entry"] = "#30a46c" }, data.Vertices[0].ItemColors);
            Assert.Equal("", data.Transitions[0].Color);
            Assert.Equal("#0090ff", data.Transitions[0].ItemColors["guard"]);
            Assert.Contains("\"color\":\"#e5484d\",\"itemColors\":{\"entry\":\"#30a46c\"}", ClipboardJson.Write(data));
        }

        [Fact]
        public void SplitsTheFillOfACompositeState()
        {
            var s = Example("MediaPlayer.fsm");
            var v = s.Model.Vertices.First(x => x.Type == VertexType.State && x.Regions.Count > 0);
            Assert.DoesNotContain("linearGradient", SvgExport.Export(s));
            // The title color tints the header only.
            s.SetColor(v, null, "#0090ff");
            var svg = SvgExport.Export(s);
            Assert.Contains("style=\"stop-color:#d5ecfd\"/><stop offset=", svg);
            Assert.Contains("style=\"stop-color:#fdfdfd\"/></linearGradient>", svg);
            Assert.Contains("fill:url(#fsm-fill-1);stroke:#0090ff", svg);
            // The background color tints the regions.
            s.SetColor(v, "background", "#30a46c");
            Assert.Contains("style=\"stop-color:#dcefe6\"/></linearGradient>", SvgExport.Export(s));
            var back = Xmi.FromXmi(Xmi.ToXmi(s.Model)).Vertices.First(x => x.Id == v.Id);
            Assert.Equal("#0090ff", back.Color);
            Assert.Equal("#30a46c", back.ItemColors["background"]);
            // One color for both is a plain fill.
            s.SetColor(v, "background", "#0090ff");
            Assert.DoesNotContain("linearGradient", SvgExport.Export(s));
        }

        [Fact]
        public void SetsAndClearsColors()
        {
            var s = Example("MediaPlayer.fsm");
            var v = s.Model.Vertices.First(x => x.Type == VertexType.State);
            var t = s.Model.Transitions[0];
            s.SetColor(v, null, "#e5484d");
            s.SetColor(v, "entry", "#30a46c");
            s.SetColor(t, "guard", "#0090ff");
            s.SetColor(t, null, "not a color");
            var back = Xmi.FromXmi(Xmi.ToXmi(s.Model));
            var bv = back.Vertices.First(x => x.Id == v.Id);
            Assert.Equal("#e5484d", bv.Color);
            Assert.Equal("#30a46c", bv.ItemColors["entry"]);
            Assert.Equal("", back.Transitions.First(x => x.Id == t.Id).Color);
            Assert.Equal("#0090ff", back.Transitions.First(x => x.Id == t.Id).ItemColors["guard"]);
            Assert.Contains("fill:#30a46c", SvgExport.Export(s));
            s.SetColor(v, "entry", null);
            s.SetColor(v, null, null);
            Assert.Equal("", v.Color);
            Assert.Empty(v.ItemColors);
        }

        [Fact]
        public void LabelsParseAndRefuseBadText()
        {
            var s = NewSession();
            var t = s.Index.Transition("t_init");
            var idle = s.Index.Vertex("v_idle");
            var t2 = s.Connect(idle, idle.Id);
            Assert.Null(s.ApplyLabel(t2, "play, after(2s) [isReady() && !isBusy()] / doIt(); log()"));
            Assert.Equal(new[] { "play", "after(2s)" }, t2.Triggers);
            Assert.Equal("isReady() && !isBusy()", t2.Guard);
            Assert.Equal("doIt(); log()", t2.Effect);
            Assert.Equal("play, after(2s) [isReady() && !isBusy()] / doIt(); log()", Labels.TransitionLabel(s.Model, t2));
            Assert.StartsWith("Guard:", s.ApplyLabel(t2, "go [volume > 3]"));
            Assert.Equal("isReady() && !isBusy()", t2.Guard);
            Assert.StartsWith("Guard:", s.ApplyLabel(t, "[else]"));
        }

        [Fact]
        public void AddAllReferencesUsesTheSubmachinePoints()
        {
            var s = NewSession();
            var sub = s.CreateVertex("submachine", new PointD(400, 300));
            s.SetSubmachine(sub, "Payment.fsm#sm");
            s.Submachines = new Dictionary<string, SubmachineInfo>
            {
                ["Payment.fsm#sm"] = new SubmachineInfo
                {
                    Found = true,
                    Name = "Payment",
                    Points = { new ConnectionPointInfo("p1", "retry", PointKind.Entry), new ConnectionPointInfo("p2", "failed", PointKind.Exit), new ConnectionPointInfo("p3", "cancelled", PointKind.Exit) },
                },
            };
            s.AddAllReferences(sub);
            var refs = s.RefsOf(sub);
            Assert.Equal(3, refs.Count);
            Assert.All(refs.Where(r => r.PointKind == PointKind.Entry), r => Assert.Equal(sub.X, r.CenterX));
            Assert.All(refs.Where(r => r.PointKind == PointKind.Exit), r => Assert.Equal(sub.X + sub.W, r.CenterX));
            Assert.Empty(s.FreePoints(sub));
            Assert.Equal("Payment", s.MachineLabel("Payment.fsm#sm"));
        }

        [Fact]
        public void RegionsAreAddedAndRemovedWithTheirContents()
        {
            var s = NewSession();
            var idle = s.Index.Vertex("v_idle");
            s.AddRegion(idle);
            var region = idle.Regions.Single();
            var inner = s.CreateVertex("state", new PointD(idle.X + 50, idle.Y + idle.H - 30));
            Assert.Equal(region.Id, inner.Parent);
            s.RemoveRegion(idle, region.Id);
            Assert.Empty(idle.Regions);
            Assert.Null(s.Index.Vertex(inner.Id));
        }

        [Fact]
        public void SvgExportDrawsEveryElement()
        {
            var s = Example("MediaPlayer.fsm");
            var svg = SvgExport.Export(s);
            Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\"", svg);
            Assert.Equal(s.Model.Vertices.Count, CountOf(svg, "<g class=\"vertex "));
            Assert.Equal(s.Model.Transitions.Count(t => Geometry.IsDrawn(s.Index, t)), CountOf(svg, "<g class=\"transition\">"));
            System.Xml.Linq.XDocument.Parse(svg); // well-formed
        }

        private static int CountOf(string text, string part)
        {
            int n = 0, i = 0;
            while ((i = text.IndexOf(part, i, StringComparison.Ordinal)) >= 0)
            {
                n++;
                i += part.Length;
            }
            return n;
        }

        [Fact]
        public void HrefsAreRelativeAndEncoded()
        {
            var root = Path.GetTempPath();
            var doc = Path.Combine(root, "models", "Order.fsm");
            var other = Path.Combine(root, "models", "sub dir", "Payment.fsm");
            var href = Hrefs.For(doc, other, "sm");
            Assert.Equal("sub%20dir/Payment.fsm#sm", href);
            var (file, id) = Hrefs.Resolve(doc, href);
            Assert.Equal(Path.GetFullPath(other), file);
            Assert.Equal("sm", id);
            Assert.Equal("../Order.fsm#sm", Hrefs.For(other, doc, "sm"));
        }

        [Fact]
        public void NumbersPrintLikeTheFiles()
        {
            Assert.Equal("0", Num.Format(-0.0));
            Assert.Equal("12.5", Num.Format(12.5));
            Assert.Equal("-3", Num.Format(-3));
            Assert.Equal(0.3, Num.R1(0.25));
            Assert.Equal(-2, Num.Round(-2.5));
        }
    }
}
