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

        /// <summary>A protocol machine: A (A → choice on go), the choice ([ok()] → B, → A), B.</summary>
        private const string Protocol = @"{ ""version"": 1, ""name"": ""P"", ""type"": ""protocol"", ""context"": ""Door"",
  ""initial"": { ""target"": ""A"" },
  ""elements"": [
    { ""name"": ""A"", ""type"": ""state"", ""transitions"": [ { ""type"": ""external"", ""trigger"": ""go"", ""target"": ""ch*"" } ] },
    { ""name"": ""ch*"", ""type"": ""choice"", ""transitions"": [
      { ""type"": ""external"", ""precondition"": ""ok()"", ""target"": ""B"" },
      { ""type"": ""external"", ""target"": ""A"" } ] },
    { ""name"": ""B"", ""type"": ""state"" }
  ] }";

        [Fact]
        public void OffersAProtocolMachineNoHistoryAndElseOnlyOnABranchsPrecondition()
        {
            var f = new Fixture(Protocol);
            Assert.Equal("Door", f.M.Context);
            Assert.False(Tools.Allowed(f.M, "shallowHistory"));
            Assert.False(Tools.Allowed(f.M, "deepHistory"));
            Assert.True(Tools.Allowed(f.M, "choice"));
            Assert.Null(f.S.CreateVertex("shallowHistory", new PointD(400, 300)));
            Assert.Equal(new[] { VertexType.Choice, VertexType.Junction }, f.S.TypeOptions(f.Of(VertexType.Choice)));
            Assert.Equal(new[] { VertexType.Initial, VertexType.Choice, VertexType.Junction }, f.S.TypeOptions(f.TopInitial));
            Assert.StartsWith("Precondition:", f.S.ApplyLabel(f.Leaving(f.Named("A"))[0], "[else] go"));
            var branch = f.Leaving(f.Of(VertexType.Choice))[1];
            Assert.Null(f.S.ApplyLabel(branch, "[else]"));
            Assert.Equal("else", branch.Precondition);
            Assert.Contains("\"context\": \"Door\"", TestFiles.Write(f.M).Model);
            f.M.Kind = MachineKind.Behavioral;
            Assert.True(Tools.Allowed(f.M, "shallowHistory"));
            // A new machine of either kind.
            Assert.Contains("\"type\": \"protocol\"", TestFiles.Write(FsmModel.CreateDefault("Door", MachineKind.Protocol)).Model);
            Assert.Contains("\"type\": \"behavioral\"", TestFiles.Write(FsmModel.CreateDefault("Door")).Model);
        }

        [Fact]
        public void OffersATransitionLeavingAPseudostateNoTriggersAndNoOtherKind()
        {
            var f = new Fixture(Sample);
            var branch = f.Leaving(f.Of(VertexType.Choice))[0];
            Assert.False(DiagramSession.TakesTriggers(f.Of(VertexType.Choice), f.Named("A")));
            Assert.True(DiagramSession.TakesTriggers(f.Named("A"), f.Named("C")));
            Assert.Equal(new[] { TransitionKind.External }, f.S.KindOptions(branch));
            Assert.Equal(DiagramSession.NoTriggers, f.S.ApplyLabel(branch, "go [ok()]"));
            Assert.Null(f.S.ApplyLabel(branch, "[ready()]"));
            // A, simple: internal, not local; C, composite, to C1 inside it: local too.
            Assert.Equal(new[] { TransitionKind.External, TransitionKind.Internal }, f.S.KindOptions(f.Leaving(f.Named("A"))[0]));
            var inside = new Transition { Id = "t_in", Source = f.Named("C").Id, Target = f.Named("C1").Id };
            f.M.Transitions.Add(inside);
            Assert.Equal(new[] { TransitionKind.External, TransitionKind.Local, TransitionKind.Internal }, f.S.KindOptions(inside));
        }

        /// <summary>A → fork → X, Y (the regions of C, X2 beside X); X, Y → join → A; C's entry point in → X; a choice → A.</summary>
        private static string Segments(string type) => @"{ ""version"": 1, ""name"": ""S"", ""type"": """ + type + @""",
  ""initial"": { ""target"": ""A"" },
  ""elements"": [
    { ""name"": ""A"", ""type"": ""state"", ""transitions"": [ { ""type"": ""external"", ""target"": ""f*"" } ] },
    { ""name"": ""f*"", ""type"": ""fork"", ""transitions"": [ { ""type"": ""external"", ""target"": ""X"" }, { ""type"": ""external"", ""target"": ""Y"" } ] },
    { ""name"": ""C"", ""type"": ""state"", ""points"": [ { ""name"": ""in"", ""type"": ""entryPoint"", ""transitions"": [ { ""type"": ""local"", ""target"": ""X"" } ] } ],
      ""regions"": [
        { ""name"": ""r1"", ""elements"": [ { ""name"": ""X"", ""type"": ""state"", ""transitions"": [ { ""type"": ""external"", ""target"": ""j*"" } ] }, { ""name"": ""X2"", ""type"": ""state"" } ] },
        { ""name"": ""r2"", ""elements"": [ { ""name"": ""Y"", ""type"": ""state"", ""transitions"": [ { ""type"": ""external"", ""target"": ""j*"" } ] } ] } ] },
    { ""name"": ""j*"", ""type"": ""join"", ""transitions"": [ { ""type"": ""external"", ""target"": ""A"" } ] },
    { ""name"": ""ch*"", ""type"": ""choice"", ""transitions"": [ { ""type"": ""external"", ""target"": ""A"" } ] }
  ] }";

        [Theory]
        [InlineData("behavioral")]
        [InlineData("protocol")]
        public void OffersNoGuardOrTriggersOnAForkAnEntryPointOrAJoinAndDrawsNoneTheyCantHave(string type)
        {
            var f = new Fixture(Segments(type));
            Vertex V(string id) => f.M.Vertices.Single(v => v.Id == id);
            Transition T(string id) => f.M.Transitions.Single(t => t.Id == id);
            Assert.False(DiagramSession.TakesGuard(V("f*"), V("X")));
            Assert.False(DiagramSession.TakesGuard(V("C.in"), V("X")));
            Assert.False(DiagramSession.TakesGuard(V("X"), V("j*")));
            Assert.False(DiagramSession.TakesTriggers(V("X"), V("j*")));
            Assert.True(DiagramSession.TakesGuard(V("ch*"), V("A")));
            var guard = type == "protocol" ? "precondition" : "guard";
            Assert.Equal($"A transition leaving a fork or an entry point, or entering a join, cannot have a {guard}.", f.S.ApplyLabel(T("X#1"), "[ok()]"));
            Assert.Equal(DiagramSession.NoTriggers, f.S.ApplyLabel(T("X#1"), "go"));
            // Drawn: a fork to a choice, a choice to a join, a second transition from the entry point into r1.
            Assert.False(f.S.CanConnect(V("f*"), "ch*"));
            Assert.False(f.S.CanConnect(V("ch*"), "j*"));
            Assert.False(f.S.CanConnect(V("C.in"), "X2"));
            Assert.True(f.S.CanConnect(V("C.in"), "Y"));
            Assert.Equal(0, f.Commits);
            // Drawn from an entry point: local, the only kind it can be (UML's state_is_external).
            var drawn = f.S.Connect(V("C.in"), "Y");
            Assert.Equal(TransitionKind.Local, drawn.Kind);
            Assert.Equal(new[] { TransitionKind.Local }, f.S.KindOptions(drawn));
        }

        /// <summary>A (A → ch* on go), ch* ([else] → A), C (its points in and out, X inside), the machine's exit point stop.</summary>
        private const string Points = @"{ ""version"": 1, ""name"": ""M"", ""type"": ""behavioral"",
  ""points"": [ { ""name"": ""stop"", ""type"": ""exitPoint"" } ],
  ""initial"": { ""target"": ""A"" },
  ""elements"": [
    { ""name"": ""A"", ""type"": ""state"", ""transitions"": [ { ""type"": ""external"", ""trigger"": ""go"", ""target"": ""ch*"" } ] },
    { ""name"": ""ch*"", ""type"": ""choice"", ""transitions"": [ { ""type"": ""external"", ""guard"": ""else"", ""target"": ""A"" } ] },
    { ""name"": ""C"", ""type"": ""state"", ""points"": [ { ""name"": ""in"", ""type"": ""entryPoint"" }, { ""name"": ""out"", ""type"": ""exitPoint"" } ],
      ""elements"": [ { ""name"": ""X"", ""type"": ""state"" } ] }
  ] }";

        [Fact]
        public void DrawsNoTransitionFromAnEntryPointOutOfItsStateFromAnExitPointIntoItOrFromTheMachinesExitPoint()
        {
            var f = new Fixture(Points);
            Vertex V(string id) => f.M.Vertices.Single(v => v.Id == id);
            Assert.False(f.S.CanConnect(V("C.in"), "A"));
            Assert.True(f.S.CanConnect(V("C.in"), "X"));
            Assert.False(f.S.CanConnect(V("C.out"), "X"));
            Assert.True(f.S.CanConnect(V("C.out"), "A"));
            Assert.False(f.S.CanStartFrom(V("M.stop")));
            // The [else] branch can't be reversed: A would be its source.
            Assert.False(f.S.CanReverse(f.Leaving(V("ch*"))[0]));
            // The entry point swapped for an exit point: in and out then both leave C.
            var t = new Transition { Id = "t_in", Source = "C.in", Target = "X" };
            f.M.Transitions.Add(t);
            Assert.DoesNotContain(VertexType.ExitPoint, f.S.TypeOptions(V("C.in")));
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
            Assert.True(f.S.CanConnect(f.Named("note*"), init.Id));
            Assert.True(f.S.CanConnect(f.Named("note*"), f.Leaving(init)[0].Id));
            Assert.True(f.S.CanConnect(f.Named("note*"), f.Named("A").Id));
            Assert.False(f.S.CanConnect(f.Named("A"), init.Id));
        }

        [Fact]
        public void AttachesACommentToAnInitialPseudostateItsTransitionARegionAndTheMachine()
        {
            var f = new Fixture(Sample);
            var note = f.Named("note*");
            var region = f.Named("C").Regions[0].Id;
            Assert.True(f.S.CanConnect(note, region));
            Assert.True(f.S.CanConnect(note, DiagramSession.MachineAnchor));
            f.S.Connect(note, f.TopInitial.Id);
            f.S.Connect(note, f.Leaving(f.TopInitial)[0].Id);
            f.S.Connect(note, region);
            f.S.Connect(note, DiagramSession.MachineAnchor);
            Assert.Empty(f.Messages);
            // C's region, unnamed, gets a generated name, which references it.
            var model = TestFiles.Write(f.M).Model;
            Assert.Contains("\"M.initial\",", model);
            Assert.Contains("{ \"source\": \"M.initial\", \"target\": \"A\" }", model);
            Assert.Contains("\"region1*\",", model);
            Assert.Contains("\"M\"\n", model);
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
