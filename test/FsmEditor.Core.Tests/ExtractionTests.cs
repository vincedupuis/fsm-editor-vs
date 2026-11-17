using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FsmEditor.Core;
using Xunit;

namespace FsmEditor.Core.Tests
{
    /// <summary>Extract to Submachine, as test/unit/extract.test.ts of the VS Code edition checks it.</summary>
    public class ExtractionTests
    {
        private const string Href = "Sub.fsm";

        private static FsmModel Example(string file) => Load.LoadModel(TestFiles.Example(file), TestFiles.Example(file + ".layout"));

        /// <summary>Extracts <paramref name="ids"/> into machine <c>Sub</c>, checking that both machines are valid and are saved without loss.</summary>
        private static ExtractResult Extract(FsmModel model, string[] ids, string name = "Sub")
        {
            var before = TestFiles.Write(model).Model;
            var r = Extraction.Extract(model, ids, name, name + ".fsm");
            Assert.Equal(before, TestFiles.Write(model).Model);
            var info = new Dictionary<string, SubmachineInfo> { [name + ".fsm"] = new SubmachineInfo { Found = true, Name = name, Points = r.Machine.MachineConnectionPoints() } };
            Assert.Empty(Validation.Validate(r.Parent, info));
            Assert.Empty(Validation.Validate(r.Machine));
            foreach (var m in new[] { r.Parent, r.Machine })
            {
                var w = TestFiles.Write(m);
                var back = TestFiles.Read(w.Model, w.Layout).Model;
                Assert.Equal(w.Model, TestFiles.Write(back).Model);
                Assert.Equal(w.Layout, TestFiles.Write(back).Layout);
            }
            return r;
        }

        /// <summary>What a model says, without layout or element order.</summary>
        private static string Essentials(FsmModel m) => string.Join("\n",
            m.Vertices.OrderBy(v => v.Id, StringComparer.Ordinal).Select(v => string.Join("|", v.Id, v.Type, v.Name, v.Parent, v.Submachine, v.Ref, v.PointKind, v.Group, string.Join(",", v.Anchors), string.Join(",", v.Regions.Select(r => r.Id))))
                .Concat(m.Transitions.OrderBy(t => t.Id, StringComparer.Ordinal).Select(t => string.Join("|", t.Id, t.Source, t.Target, t.Kind, string.Join(",", t.Triggers), t.Guard, t.Action))));

        private static Vertex V(FsmModel m, string id) => m.Vertices.FirstOrDefault(v => v.Id == id);
        private static Transition T(FsmModel m, string id) => m.Transitions.First(t => t.Id == id);
        private static Vertex Named(FsmModel m, VertexType type, string name) => m.Vertices.First(v => v.Type == type && v.Name == name);
        private static List<string> Points(FsmModel m, VertexType type) => m.Vertices.Where(v => v.Type == type).Select(v => v.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        private static string Label(Transition t) => string.Join("|", string.Join(",", t.Triggers), t.Guard, t.Action);
        private static List<Transition> From(FsmModel m, string source) => m.Transitions.Where(t => t.Source == source).ToList();
        private static List<Transition> To(FsmModel m, string target) => m.Transitions.Where(t => t.Target == target).ToList();
        /// <summary>The reference on the submachine state for the new machine's point <paramref name="pointId"/> (references name their points).</summary>
        private static Vertex RefOf(ExtractResult r, string pointId)
        {
            var name = r.Machine.Vertices.First(p => p.Id == pointId).Name;
            return r.Parent.Vertices.First(v => v.Type == VertexType.ConnectionPointRef && v.Ref == name);
        }

        /// <summary>MediaPlayer without its deep history, so that On can be extracted.</summary>
        private static FsmModel MediaPlayerWithoutHistory()
        {
            var m = Example("MediaPlayer.fsm");
            m.Vertices.RemoveAll(v => v.Id == "On.deepHistory");
            m.Transitions.RemoveAll(t => t.Source == "On.deepHistory" || t.Target == "On.deepHistory");
            foreach (var v in m.Vertices) v.Anchors.Remove("On.deepHistory");
            return m;
        }

        [Fact]
        public void SplitsCrossingTransitionsKeepingTheLabelOnTheSourceSide()
        {
            var r = Extract(Example("MediaPlayer.fsm"), new[] { "Paused", "choice1*" });
            var state = V(r.Parent, r.StateId);
            Assert.Equal(Href, state.Submachine);
            Assert.Equal("On.", state.Parent);
            Assert.Null(V(r.Parent, "Paused"));
            Assert.NotNull(V(r.Machine, "Paused"));
            Assert.NotNull(V(r.Machine, "choice1*"));
            Assert.Equal(new[] { "toChoice", "toPaused" }, Points(r.Machine, VertexType.EntryPoint));
            Assert.Equal(new[] { "toFault", "toPlaying", "toStopped" }, Points(r.Machine, VertexType.ExitPoint));

            // Incoming: the outer part keeps the label and goes to the entry reference; the inner part is plain.
            var toPaused = Named(r.Machine, VertexType.EntryPoint, "toPaused");
            Assert.Equal(RefOf(r, toPaused.Id).Id, T(r.Parent, "Playing#1").Target);
            Assert.Equal("pause||", Label(T(r.Parent, "Playing#1")));
            Assert.Equal(new[] { "Paused ||" }, From(r.Machine, toPaused.Id).Select(t => t.Target + " " + Label(t)));

            // Outgoing: the inner part keeps the label and goes to the exit point; the outer part is plain.
            var toStopped = Named(r.Machine, VertexType.ExitPoint, "toStopped");
            Assert.Equal(new[] { "Paused#2 stop||", "Paused#4 after(5m)||saveBookmark()", "choice1*#1 |else|openTray()" }, To(r.Machine, toStopped.Id).Select(t => t.Id + " " + Label(t)));
            Assert.Equal(new[] { "Stopped ||" }, From(r.Parent, RefOf(r, toStopped.Id).Id).Select(t => t.Target + " " + Label(t)));
            var toFault = Named(r.Machine, VertexType.ExitPoint, "toFault");
            Assert.Equal("|isTrayJammed()|", Label(T(r.Machine, "choice1*#2")));
            Assert.Equal(new[] { "On.fault" }, From(r.Parent, RefOf(r, toFault.Id).Id).Select(t => t.Target));

            // Inside transitions move as they are.
            Assert.Equal("volumeChanged||updateVolume()", Label(T(r.Machine, "Paused#3")));
            Assert.Equal(TransitionKind.Internal, T(r.Machine, "Paused#3").Kind);
        }

        [Fact]
        public void MovesTheRegionInitialAlongAndUsesDefaultEntry()
        {
            var r = Extract(Example("MediaPlayer.fsm"), new[] { "MediaPlayer.initial", "Off" });
            var initial = Assert.Single(r.Parent.Vertices, v => v.Type == VertexType.Initial && v.Parent == "MediaPlayer.");
            Assert.Equal(new[] { r.StateId }, From(r.Parent, initial.Id).Select(t => t.Target));
            Assert.Equal(r.StateId, T(r.Parent, "On#1").Target);
            Assert.Empty(Points(r.Machine, VertexType.EntryPoint));
            Assert.Equal(new[] { "toFinal", "toOn" }, Points(r.Machine, VertexType.ExitPoint));
            Assert.Equal(new[] { "Off" }, From(r.Machine, "MediaPlayer.initial").Select(t => t.Target));
        }

        [Fact]
        public void TurnsACompositeStateWithOneRegionIntoTheSubmachineState()
        {
            var r = Extract(MediaPlayerWithoutHistory(), new[] { "On" });
            Assert.Equal("On", r.StateId);
            var on = V(r.Parent, "On");
            Assert.Equal(("On", Href, 0), (on.Name, on.Submachine, on.Regions.Count));
            Assert.Equal(("On", "On"), (T(r.Parent, "Off#1").Target, T(r.Parent, "On#1").Source));
            // Its own exit point becomes the new machine's, with a reference to it by name in its place.
            var reference = V(r.Parent, "On.fault");
            Assert.Equal((VertexType.ConnectionPointRef, "On", "fault", PointKind.Exit), (reference.Type, reference.Parent, reference.Ref, reference.PointKind));
            Assert.Equal("On.fault", T(r.Parent, "On.fault#1").Source);
            var point = V(r.Machine, "On.fault");
            Assert.Equal((VertexType.ExitPoint, "On.", "fault"), (point.Type, point.Parent, point.Name));
            Assert.Equal(new[] { "On." }, r.Machine.Regions.Select(g => g.Id));
            Assert.Equal(2, V(r.Machine, "Playing").Regions.Count);
        }

        [Fact]
        public void MovesACompositeStateWithSeveralRegionsWhole()
        {
            // Its name stays in the new machine, which can't have it too.
            Assert.Equal("PlayingMachine", Extraction.ProposedName(Example("MediaPlayer.fsm"), new[] { "Playing" }));
            var r = Extract(Example("MediaPlayer.fsm"), new[] { "Playing" }, "PlayingMachine");
            Assert.NotEqual("Playing", r.StateId);
            Assert.Equal("PlayingMachine", V(r.Parent, r.StateId).Name);
            Assert.Equal(new[] { "audio", "video" }, V(r.Machine, "Playing").Regions.Select(g => g.Id));
            Assert.Equal(new[] { "toPlaying" }, Points(r.Machine, VertexType.EntryPoint));
            Assert.Equal(new[] { "toPaused" }, Points(r.Machine, VertexType.ExitPoint));
        }

        [Fact]
        public void KeepsSubmachineStatesAndTheirReferencesInsideTheExtractedPart()
        {
            var r = Extract(Example("Order.fsm"), new[] { "Cart", "Payment" });
            Assert.Equal("Payment.fsm", V(r.Machine, "Payment").Submachine);
            Assert.Equal(new[] { "Payment.cancelled", "Payment.failed", "Payment.retry" }, r.Machine.Vertices.Where(v => v.Parent == "Payment").Select(v => v.Id).OrderBy(id => id, StringComparer.Ordinal));
            // retryPayment came from outside to the entry reference 'retry': it now goes through an entry point.
            var entry = Named(r.Machine, VertexType.EntryPoint, "toPayment");
            Assert.Equal(new[] { "Payment.retry" }, From(r.Machine, entry.Id).Select(t => t.Target));
            Assert.Equal(RefOf(r, entry.Id).Id, T(r.Parent, "PaymentFailed#1").Target);
        }

        [Fact]
        public void ReanchorsOutsideCommentsAndKeepsGroupsAmongTheExtractedElements()
        {
            var m = Example("MediaPlayer.fsm");
            V(m, "comment1*").Anchors = new List<string> { "On.deepHistory", "Paused", "Paused#3" };
            V(m, "Paused").Group = "g1";
            V(m, "choice1*").Group = "g1";
            var r = Extract(m, new[] { "Paused" }); // the group brings the choice along
            Assert.NotNull(V(r.Machine, "choice1*"));
            Assert.Equal(new[] { "On.deepHistory", r.StateId }, V(r.Parent, "comment1*").Anchors);
            Assert.Equal(("g1", "g1"), (V(r.Machine, "Paused").Group, V(r.Machine, "choice1*").Group));
        }

        [Theory]
        [InlineData("")]
        [InlineData("log()")]
        public void UsesDefaultEntryOnlyWhenTheInitialTransitionHasNoGuardOrEffect(string effect)
        {
            var m = new FsmModel { Name = "M", Regions = { new Region { Id = "r_root" } } };
            m.Vertices.Add(new Vertex { Id = "O", Type = VertexType.State, Name = "O", Parent = "r_root" });
            m.Vertices.Add(new Vertex { Id = "S", Type = VertexType.State, Name = "S", Parent = "r_root", Regions = { new Region { Id = "S_r0" } } });
            m.Vertices.Add(new Vertex { Id = "X", Type = VertexType.State, Name = "X", Parent = "S_r0" });
            m.Vertices.Add(new Vertex { Id = "i1", Type = VertexType.Initial, Parent = "r_root" });
            m.Vertices.Add(new Vertex { Id = "i2", Type = VertexType.Initial, Parent = "S_r0" });
            m.Transitions.Add(new Transition { Id = "t1", Source = "i1", Target = "O" });
            m.Transitions.Add(new Transition { Id = "t2", Source = "i2", Target = "X", Action = effect });
            m.Transitions.Add(new Transition { Id = "t3", Source = "O", Target = "X", Triggers = { "e" } });
            var r = Extract(m, new[] { "S" });
            var e = T(r.Parent, "t3");
            if (effect.Length > 0) Assert.Equal(VertexType.ConnectionPointRef, V(r.Parent, e.Target).Type);
            else Assert.Equal("S", e.Target);
        }

        [Fact]
        public void ProposesTheNameOfTheExtractedState()
        {
            var m = Example("MediaPlayer.fsm");
            Assert.Equal("PlayingMachine", Extraction.ProposedName(m, new[] { "Playing" }));
            Assert.Equal("PausedMachine", Extraction.ProposedName(m, new[] { "Paused", "choice1*" }));
            Assert.Equal("Submachine", Extraction.ProposedName(m, new[] { "Paused", "Stopped" }));
        }

        [Fact]
        public void RefusesWhatASubmachineCannotExpress()
        {
            void Refused(FsmModel model, string[] ids, string message) =>
                Assert.Contains(message, Assert.Throws<ExtractException>(() => Extraction.Extract(model, ids, "Sub", Href)).Message);
            var m = Example("MediaPlayer.fsm");
            Refused(m, new[] { "On" }, "history pseudostate across the boundary");
            Refused(m, new[] { "On.fault" }, "Points on a state's border can't be extracted without their state ('fault')");
            Refused(m, new[] { "comment1*" }, "Select the states and pseudostates to extract");
            Refused(m, new[] { "Off", "Paused" }, "Select elements of one region");
            Refused(m, new string[0], "Select the states and pseudostates to extract");
            Assert.Null(Extraction.Problem(m, new[] { "Paused" }));
            Assert.Equal("Select elements of one region to extract them.", Extraction.Problem(m, new[] { "Off", "Paused" }));

            var b = new FsmModel { Name = "M", Regions = { new Region { Id = "r_root" } } };
            b.Vertices.Add(new Vertex { Id = "S", Type = VertexType.State, Name = "S", Parent = "r_root", Regions = { new Region { Id = "S_r0" } } });
            b.Vertices.Add(new Vertex { Id = "X", Type = VertexType.State, Name = "X", Parent = "S_r0" });
            b.Vertices.Add(new Vertex { Id = "Y", Type = VertexType.State, Name = "Y", Parent = "S_r0" });
            b.Transitions.Add(new Transition { Id = "t1", Source = "S", Target = "Y", Kind = TransitionKind.Local, Triggers = { "e" } });
            Refused(b, new[] { "Y" }, "Make the transition from 'S' to 'Y' external first");
        }
    }
}
