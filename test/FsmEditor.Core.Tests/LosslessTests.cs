using System.Collections.Generic;
using System.Linq;
using FsmEditor.Core;
using Xunit;

namespace FsmEditor.Core.Tests
{
    /// <summary>
    /// The session doesn't offer what the file can't hold, so that the writer never has to leave something out (as the
    /// VS Code edition's webviewLossless tests): the canvas and the properties panel ask it what to offer
    /// (<see cref="DiagramSession.IsStart"/> for a transition's kind, triggers, guard and conditions, the ⊕ handle,
    /// connection and reconnection targets, Reverse, kinds, submachines, drop targets, Leave Parent), and its
    /// operations refuse the rest.
    /// </summary>
    public class LosslessTests
    {
        private const string InitialOnly = "A transition leaving an initial pseudostate holds its target and an action only.";
        private const string HistoryOnly = "A transition leaving a history pseudostate holds its target and an action only.";
        private const string Anchor = "A comment cannot be attached to an initial pseudostate or to its transition.";

        /// <summary>
        /// A (A → C on go, A → choice on pick), C (composite: its initial → C1, a history without a transition,
        /// C1 → C.history on back, C1 → A on tick), a choice ([ok()] → A), a comment; the machine's initial → A / start().
        /// </summary>
        private const string Sample = @"{ ""version"": 1, ""name"": ""M"", ""type"": ""behavioral"",
  ""initial"": { ""target"": ""A"", ""action"": ""start()"" },
  ""elements"": [
    { ""name"": ""A"", ""type"": ""state"", ""transitions"": [
      { ""type"": ""external"", ""trigger"": ""go"", ""target"": ""C"" },
      { ""type"": ""external"", ""trigger"": ""pick"", ""target"": ""ch*"" } ] },
    { ""name"": ""C"", ""type"": ""state"", ""initial"": { ""target"": ""C1"" }, ""history"": {}, ""elements"": [
      { ""name"": ""C1"", ""type"": ""state"", ""transitions"": [
        { ""type"": ""external"", ""trigger"": ""back"", ""target"": ""C.history"" },
        { ""type"": ""external"", ""trigger"": ""tick"", ""target"": ""A"" } ] } ] },
    { ""name"": ""ch*"", ""type"": ""choice"", ""transitions"": [ { ""type"": ""external"", ""guard"": ""ok()"", ""target"": ""A"" } ] },
    { ""name"": ""note*"", ""type"": ""comment"", ""text"": ""n"" }
  ] }";

        private sealed class Fixture
        {
            public readonly DiagramSession S;
            public readonly List<string> Messages = new List<string>();
            public int Commits;

            public Fixture(string text)
            {
                S = new DiagramSession(TestFiles.Read(text).Model);
                S.Message += (o, m) => Messages.Add(m);
                S.Committed += (o, e) => Commits++;
            }

            public FsmModel M => S.Model;
            public Vertex Named(string name) => M.Vertices.Single(v => v.Name == name);
            public Vertex Of(VertexType type, string parent = null) => M.Vertices.First(v => v.Type == type && (parent == null || v.Parent == parent));
            public List<Transition> Leaving(Vertex v) => M.Transitions.Where(t => t.Source == v.Id).ToList();
            public Vertex TopInitial => Of(VertexType.Initial, S.RootRegion);
        }

        [Fact]
        public void StartTransitionsAreKnownToThePanel()
        {
            var f = new Fixture(Sample);
            Assert.True(DiagramSession.IsStart(f.TopInitial));
            Assert.True(DiagramSession.IsStart(f.Of(VertexType.ShallowHistory)));
            Assert.False(DiagramSession.IsStart(f.Named("A")));
            Assert.False(DiagramSession.IsStart(f.Of(VertexType.Choice)));
        }

        [Fact]
        public void RefusesTriggersAndAGuardInTheLabelOfAnInitialTransition()
        {
            var f = new Fixture(Sample);
            var t = f.Leaving(f.TopInitial)[0];
            Assert.Equal(InitialOnly, f.S.ApplyLabel(t, "go / start()"));
            Assert.Equal(InitialOnly, f.S.ApplyLabel(t, "[ready()] / start()"));
            Assert.Equal(0, f.Commits);
            Assert.Null(f.S.ApplyLabel(t, "/ begin()"));
            Assert.Equal("begin()", t.Action);
            Assert.Empty(t.Triggers);
            TestFiles.Write(f.M);
        }

        [Fact]
        public void OffersOnlyTheKindsAPseudostateCanBe()
        {
            var f = new Fixture(Sample);
            var choice = f.Of(VertexType.Choice);
            Assert.Equal(new[] { VertexType.Choice, VertexType.Junction }, f.S.TypeOptions(choice));
            Assert.Equal(new[] { VertexType.ShallowHistory, VertexType.DeepHistory, VertexType.Choice, VertexType.Junction, VertexType.Terminate },
                f.S.TypeOptions(f.Of(VertexType.ShallowHistory)));
            f.S.ChangeType(choice, VertexType.Initial);
            Assert.Equal(VertexType.Choice, choice.Type);
            Assert.Equal(new[] { "An initial pseudostate cannot be the target of a transition." }, f.Messages);
            Assert.Equal(0, f.Commits);
        }

        [Fact]
        public void MovesTheTransitionOfAnInitialPseudostateWhenItIsDrawnAgainAndDoesntReverseIt()
        {
            var f = new Fixture(Sample);
            var init = f.TopInitial;
            var t = f.Leaving(init)[0];
            Assert.True(f.S.CanStartFrom(init));
            // Drawn again to the same state: nothing, and no message.
            Assert.True(f.S.CanConnect(init, f.Named("A").Id));
            Assert.Same(t, f.S.Connect(init, f.Named("A").Id));
            Assert.Equal(0, f.Commits);
            // To C: its one transition goes there.
            Assert.True(f.S.CanConnect(init, f.Named("C").Id));
            Assert.Same(t, f.S.Connect(init, f.Named("C").Id));
            Assert.Equal(f.Named("C").Id, t.Target);
            Assert.Single(f.Leaving(init));
            Assert.Equal(1, f.Commits);
            Assert.Empty(f.Messages);
            TestFiles.Write(f.M);
            Assert.False(f.S.CanReverse(f.Leaving(init)[0]));
            Assert.True(f.S.CanReverse(f.Leaving(f.Named("A"))[0]));
            Assert.False(f.S.CanConnect(f.Named("note*"), init.Id));
            Assert.False(f.S.CanConnect(f.Named("note*"), f.Leaving(init)[0].Id));
            Assert.True(f.S.CanConnect(f.Named("note*"), f.Named("A").Id));
            Assert.False(f.S.CanConnect(f.Named("A"), init.Id));
        }

        [Fact]
        public void RefusesToAttachACommentToAnInitialPseudostateOrItsTransition()
        {
            var f = new Fixture(Sample);
            var note = f.Named("note*");
            Assert.Null(f.S.Connect(note, f.TopInitial.Id));
            Assert.Null(f.S.Connect(note, f.Leaving(f.TopInitial)[0].Id));
            Assert.Null(f.S.Connect(f.TopInitial, note.Id));
            Assert.Equal(new[] { Anchor, Anchor, Anchor }, f.Messages);
            Assert.Empty(note.Anchors);
            Assert.Equal(0, f.Commits);
        }

        [Fact]
        public void RefusesToDropAnInitialPseudostateIntoARegionThatHasOne()
        {
            var f = new Fixture(Sample);
            var c = f.Named("C");
            c.W += 200;
            c.H += 200;
            f.S.Reindex();
            var region = Geometry.RegionRects(f.M, c)[0].Rect;
            var init = f.TopInitial;
            init.X = region.X + region.W - 60;
            init.Y = region.Y + region.H - 60;
            var moving = new HashSet<string> { init.Id };
            Assert.Null(f.S.DropHighlight(init, moving)); // not C's region
            f.S.DropMoved(new[] { init }, moving);
            Assert.Equal(f.S.RootRegion, init.Parent);
            Assert.Empty(f.Messages);
            TestFiles.Write(f.M);
        }

        [Fact]
        public void LeaveParentIsNotOfferedWhenTheOuterRegionHasOne()
        {
            var f = new Fixture(Sample);
            var c = f.Named("C");
            f.S.Selection = new HashSet<string> { f.Of(VertexType.Initial, c.Regions[0].Id).Id };
            Assert.False(f.S.CanLeaveParent);
            f.S.Selection = new HashSet<string> { f.Named("C1").Id };
            Assert.True(f.S.CanLeaveParent);
        }

        [Fact]
        public void LeavesOutAPastedInitialPseudostateThatWouldBeASecond()
        {
            var f = new Fixture(Sample);
            f.S.Selection = new HashSet<string> { f.TopInitial.Id };
            var data = f.S.CollectSelection();
            var count = f.M.Vertices.Count;
            f.S.Paste(data);
            Assert.Equal(count, f.M.Vertices.Count);
            Assert.Empty(f.Messages);
            Assert.Equal(0, f.Commits);
            // With a state: the state is pasted, the initial pseudostate and its transition are left out.
            f.S.Selection = new HashSet<string> { f.TopInitial.Id, f.Named("A").Id };
            f.S.Paste(f.S.CollectSelection());
            Assert.Equal(count + 1, f.M.Vertices.Count);
            Assert.Single(f.M.Vertices, v => v.Type == VertexType.Initial && v.Parent == f.S.RootRegion);
            TestFiles.Write(f.M);
        }

        [Fact]
        public void RefusesASubmachineForAStateWithPointsAndNoneForAStateWithReferences()
        {
            var f = new Fixture(@"{ ""version"": 1, ""name"": ""M"", ""type"": ""behavioral"", ""initial"": { ""target"": ""S"" }, ""elements"": [
  { ""name"": ""S"", ""type"": ""state"", ""points"": [ { ""name"": ""p"", ""type"": ""entryPoint"" } ] },
  { ""name"": ""P"", ""type"": ""state"", ""submachine"": ""Payment.fsm"", ""points"": [ { ""name"": ""retry"", ""type"": ""entryPoint"" } ] } ] }");
            Assert.False(f.S.CanBeSubmachine(f.Named("S")));
            Assert.False(f.S.CanBePlain(f.Named("P")));
            Assert.True(f.S.CanBeSubmachine(f.Named("P")));
            Assert.True(f.S.CanBePlain(f.Named("S")));
            Assert.False(f.S.SetSubmachine(f.Named("S"), "Payment.fsm"));
            Assert.False(f.S.SetSubmachine(f.Named("P"), ""));
            Assert.Equal("", f.Named("S").Submachine);
            Assert.Equal("Payment.fsm", f.Named("P").Submachine);
            Assert.StartsWith("Remove the entry and exit points first", f.Messages[0]);
            Assert.StartsWith("Remove the connection point references first", f.Messages[1]);
            Assert.Equal(0, f.Commits);
        }

        [Fact]
        public void RefusesToReconnectOrReverseATransitionWithATriggerOntoAHistoryPseudostate()
        {
            var f = new Fixture(Sample);
            var c1 = f.Named("C1");
            var history = f.Of(VertexType.ShallowHistory);
            var back = f.Leaving(c1)[0];
            var tick = f.Leaving(c1)[1];
            Assert.False(f.S.CanReconnect(tick, true, history));
            Assert.True(f.S.CanReconnect(tick, false, f.Named("C")));
            Assert.False(f.S.CanReverse(back));
            f.S.Reconnect(tick, true, history);
            Assert.Equal(c1.Id, tick.Source);
            f.S.Reverse(back);
            Assert.Equal(c1.Id, back.Source);
            Assert.Equal(new[] { HistoryOnly }, f.Messages);
            Assert.Equal(0, f.Commits);
        }
    }
}
