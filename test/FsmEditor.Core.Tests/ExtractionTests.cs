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
        private const string Href = "Sub.fsm#sm";

        private static FsmModel Example(string file) =>
            Xmi.FromXmi(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", file)));

        /// <summary>Extracts <paramref name="ids"/> into machine <c>Sub</c>, checking that both machines are valid and survive an XMI round trip.</summary>
        private static ExtractResult Extract(FsmModel model, string[] ids, string name = "Sub")
        {
            var before = Xmi.ToXmi(model);
            var r = Extraction.Extract(model, ids, name, name + ".fsm#sm");
            Assert.Equal(before, Xmi.ToXmi(model));
            var info = new Dictionary<string, SubmachineInfo> { [name + ".fsm#sm"] = new SubmachineInfo { Found = true, Name = name, Points = r.Machine.MachineConnectionPoints() } };
            Assert.Empty(Validation.Validate(r.Parent, info));
            Assert.Empty(Validation.Validate(r.Machine));
            foreach (var m in new[] { r.Parent, r.Machine }) Assert.Equal(Essentials(m), Essentials(Xmi.FromXmi(Xmi.ToXmi(m))));
            return r;
        }

        /// <summary>What a model says, without layout or element order.</summary>
        private static string Essentials(FsmModel m) => string.Join("\n",
            m.Vertices.OrderBy(v => v.Id, StringComparer.Ordinal).Select(v => string.Join("|", v.Id, v.Type, v.Name, v.Parent, v.Submachine, v.Ref, v.PointKind, v.Group, string.Join(",", v.Anchors), string.Join(",", v.Regions.Select(r => r.Id))))
                .Concat(m.Transitions.OrderBy(t => t.Id, StringComparer.Ordinal).Select(t => string.Join("|", t.Id, t.Source, t.Target, t.Kind, string.Join(",", t.Triggers), t.Guard, t.Effect))));

        private static Vertex V(FsmModel m, string id) => m.Vertices.FirstOrDefault(v => v.Id == id);
        private static Transition T(FsmModel m, string id) => m.Transitions.First(t => t.Id == id);
        private static Vertex Named(FsmModel m, VertexType type, string name) => m.Vertices.First(v => v.Type == type && v.Name == name);
        private static List<string> Points(FsmModel m, VertexType type) => m.Vertices.Where(v => v.Type == type).Select(v => v.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        private static string Label(Transition t) => string.Join("|", string.Join(",", t.Triggers), t.Guard, t.Effect);
        private static List<Transition> From(FsmModel m, string source) => m.Transitions.Where(t => t.Source == source).ToList();
        private static List<Transition> To(FsmModel m, string target) => m.Transitions.Where(t => t.Target == target).ToList();
        private static Vertex RefOf(ExtractResult r, string pointId) => r.Parent.Vertices.First(v => v.Type == VertexType.ConnectionPointRef && v.Ref == pointId);

        /// <summary>MediaPlayer without its deep history, so that On can be extracted.</summary>
        private static FsmModel MediaPlayerWithoutHistory()
        {
            var m = Example("MediaPlayer.fsm");
            m.Vertices.RemoveAll(v => v.Id == "v_hist");
            m.Transitions.RemoveAll(t => t.Source == "v_hist" || t.Target == "v_hist");
            foreach (var v in m.Vertices) v.Anchors.Remove("v_hist");
            return m;
        }

        [Fact]
        public void SplitsCrossingTransitionsKeepingTheLabelOnTheSourceSide()
        {
            var r = Extract(Example("MediaPlayer.fsm"), new[] { "v_paused", "v_choice" });
            var state = V(r.Parent, r.StateId);
            Assert.Equal(Href, state.Submachine);
            Assert.Equal("r_on", state.Parent);
            Assert.Null(V(r.Parent, "v_paused"));
            Assert.NotNull(V(r.Machine, "v_paused"));
            Assert.NotNull(V(r.Machine, "v_choice"));
            Assert.Equal(new[] { "toChoice", "toPaused" }, Points(r.Machine, VertexType.EntryPoint));
            Assert.Equal(new[] { "toFault", "toPlaying", "toStopped" }, Points(r.Machine, VertexType.ExitPoint));

            // Incoming: the outer part keeps the label and goes to the entry reference; the inner part is plain.
            var toPaused = Named(r.Machine, VertexType.EntryPoint, "toPaused");
            Assert.Equal(RefOf(r, toPaused.Id).Id, T(r.Parent, "t8").Target);
            Assert.Equal("pause||", Label(T(r.Parent, "t8")));
            Assert.Equal(new[] { "v_paused ||" }, From(r.Machine, toPaused.Id).Select(t => t.Target + " " + Label(t)));

            // Outgoing: the inner part keeps the label and goes to the exit point; the outer part is plain.
            var toStopped = Named(r.Machine, VertexType.ExitPoint, "toStopped");
            Assert.Equal(new[] { "t10 stop||", "t12 |else|openTray()", "t23 after(5m)||saveBookmark()" }, To(r.Machine, toStopped.Id).Select(t => t.Id + " " + Label(t)));
            Assert.Equal(new[] { "v_stopped ||" }, From(r.Parent, RefOf(r, toStopped.Id).Id).Select(t => t.Target + " " + Label(t)));
            var toFault = Named(r.Machine, VertexType.ExitPoint, "toFault");
            Assert.Equal("|isTrayJammed()|", Label(T(r.Machine, "t13")));
            Assert.Equal(new[] { "v_fault_xp" }, From(r.Parent, RefOf(r, toFault.Id).Id).Select(t => t.Target));

            // Inside transitions move as they are.
            Assert.Equal("volumeChanged||updateVolume()", Label(T(r.Machine, "t22")));
            Assert.Equal(TransitionKind.Internal, T(r.Machine, "t22").Kind);
        }

        [Fact]
        public void MovesTheRegionInitialAlongAndUsesDefaultEntry()
        {
            var r = Extract(Example("MediaPlayer.fsm"), new[] { "v_init", "v_off" });
            var initial = Assert.Single(r.Parent.Vertices, v => v.Type == VertexType.Initial && v.Parent == "r_root");
            Assert.Equal(new[] { r.StateId }, From(r.Parent, initial.Id).Select(t => t.Target));
            Assert.Equal(r.StateId, T(r.Parent, "t3").Target);
            Assert.Empty(Points(r.Machine, VertexType.EntryPoint));
            Assert.Equal(new[] { "toFinal", "toOn" }, Points(r.Machine, VertexType.ExitPoint));
            Assert.Equal(new[] { "v_off" }, From(r.Machine, "v_init").Select(t => t.Target));
        }

        [Fact]
        public void TurnsACompositeStateWithOneRegionIntoTheSubmachineState()
        {
            var r = Extract(MediaPlayerWithoutHistory(), new[] { "v_on" });
            Assert.Equal("v_on", r.StateId);
            var on = V(r.Parent, "v_on");
            Assert.Equal(("On", Href, 0), (on.Name, on.Submachine, on.Regions.Count));
            Assert.Equal(("v_on", "v_on"), (T(r.Parent, "t2").Target, T(r.Parent, "t3").Source));
            // Its own exit point becomes the new machine's, with a reference of the same id in its place.
            var reference = V(r.Parent, "v_fault_xp");
            Assert.Equal((VertexType.ConnectionPointRef, "v_on", "v_fault_xp", PointKind.Exit), (reference.Type, reference.Parent, reference.Ref, reference.PointKind));
            Assert.Equal("v_fault_xp", T(r.Parent, "t14").Source);
            var point = V(r.Machine, "v_fault_xp");
            Assert.Equal((VertexType.ExitPoint, "r_on", "fault"), (point.Type, point.Parent, point.Name));
            Assert.Equal(new[] { "r_on" }, r.Machine.Regions.Select(g => g.Id));
            Assert.Equal(2, V(r.Machine, "v_playing").Regions.Count);
        }

        [Fact]
        public void MovesACompositeStateWithSeveralRegionsWhole()
        {
            var r = Extract(Example("MediaPlayer.fsm"), new[] { "v_playing" }, "Playing");
            Assert.NotEqual("v_playing", r.StateId);
            Assert.Equal("Playing", V(r.Parent, r.StateId).Name);
            Assert.Equal(new[] { "r_audio", "r_video" }, V(r.Machine, "v_playing").Regions.Select(g => g.Id));
            Assert.Equal(new[] { "toPlaying" }, Points(r.Machine, VertexType.EntryPoint));
            Assert.Equal(new[] { "toPaused" }, Points(r.Machine, VertexType.ExitPoint));
        }

        [Fact]
        public void KeepsSubmachineStatesAndTheirReferencesInsideTheExtractedPart()
        {
            var r = Extract(Example("Order.fsm"), new[] { "o_cart", "o_pay" });
            Assert.Equal("Payment.fsm#sm", V(r.Machine, "o_pay").Submachine);
            Assert.Equal(new[] { "o_ref_cancel", "o_ref_failed", "o_ref_retry" }, r.Machine.Vertices.Where(v => v.Parent == "o_pay").Select(v => v.Id).OrderBy(id => id, StringComparer.Ordinal));
            // retryPayment came from outside to the entry reference 'retry': it now goes through an entry point.
            var entry = Named(r.Machine, VertexType.EntryPoint, "toPayment");
            Assert.Equal(new[] { "o_ref_retry" }, From(r.Machine, entry.Id).Select(t => t.Target));
            Assert.Equal(RefOf(r, entry.Id).Id, T(r.Parent, "ot5").Target);
        }

        [Fact]
        public void ReanchorsOutsideCommentsAndKeepsGroupsAmongTheExtractedElements()
        {
            var m = Example("MediaPlayer.fsm");
            V(m, "v_note").Anchors = new List<string> { "v_hist", "v_paused", "t22" };
            V(m, "v_paused").Group = "g1";
            V(m, "v_choice").Group = "g1";
            var r = Extract(m, new[] { "v_paused" }); // the group brings the choice along
            Assert.NotNull(V(r.Machine, "v_choice"));
            Assert.Equal(new[] { "v_hist", r.StateId }, V(r.Parent, "v_note").Anchors);
            Assert.Equal(("g1", "g1"), (V(r.Machine, "v_paused").Group, V(r.Machine, "v_choice").Group));
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
            m.Transitions.Add(new Transition { Id = "t2", Source = "i2", Target = "X", Effect = effect });
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
            Assert.Equal("Playing", Extraction.ProposedName(m, new[] { "v_playing" }));
            Assert.Equal("Paused", Extraction.ProposedName(m, new[] { "v_paused", "v_choice" }));
            Assert.Equal("Submachine", Extraction.ProposedName(m, new[] { "v_paused", "v_stopped" }));
        }

        [Fact]
        public void RefusesWhatASubmachineCannotExpress()
        {
            void Refused(FsmModel model, string[] ids, string message) =>
                Assert.Contains(message, Assert.Throws<ExtractException>(() => Extraction.Extract(model, ids, "Sub", Href)).Message);
            var m = Example("MediaPlayer.fsm");
            Refused(m, new[] { "v_on" }, "history pseudostate across the boundary");
            Refused(m, new[] { "v_fault_xp" }, "Points on a state's border can't be extracted without their state ('fault')");
            Refused(m, new[] { "v_note" }, "Select the states and pseudostates to extract");
            Refused(m, new[] { "v_off", "v_paused" }, "Select elements of one region");
            Refused(m, new string[0], "Select the states and pseudostates to extract");
            Assert.Null(Extraction.Problem(m, new[] { "v_paused" }));
            Assert.Equal("Select elements of one region to extract them.", Extraction.Problem(m, new[] { "v_off", "v_paused" }));

            var b = new FsmModel { Name = "M", Regions = { new Region { Id = "r_root" } } };
            b.Vertices.Add(new Vertex { Id = "S", Type = VertexType.State, Name = "S", Parent = "r_root", Regions = { new Region { Id = "S_r0" } } });
            b.Vertices.Add(new Vertex { Id = "X", Type = VertexType.State, Name = "X", Parent = "S_r0" });
            b.Vertices.Add(new Vertex { Id = "Y", Type = VertexType.State, Name = "Y", Parent = "S_r0" });
            b.Transitions.Add(new Transition { Id = "t1", Source = "S", Target = "Y", Kind = TransitionKind.Local, Triggers = { "e" } });
            Refused(b, new[] { "Y" }, "Make the transition from 'S' to 'Y' external first");
        }
    }
}
