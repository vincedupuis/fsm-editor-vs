using System.Collections.Generic;
using System.Linq;
using FsmEditor.Core;
using Xunit;

namespace FsmEditor.Core.Tests
{
    /// <summary>Validation rules, with the same cases and messages as the VS Code extension's test/unit/validation.test.ts.</summary>
    public class ValidationTests
    {
        /// <summary>Builds a model like the VS Code extension's test harness: no layout, ids given or numbered.</summary>
        private sealed class ModelBuilder
        {
            public FsmModel Model { get; }
            private int n;

            public ModelBuilder(string name) =>
                Model = new FsmModel { Id = "sm", Name = name, Regions = { new Region { Id = "r_root" } } };

            public string Vertex(VertexType type, string parent = "r_root", string id = null, string name = "")
            {
                id = id ?? $"v{++n}";
                Model.Vertices.Add(new Vertex { Id = id, Type = type, Name = name, Parent = parent, W = 10, H = 10 });
                return id;
            }

            /// <summary>A state; <paramref name="regions"/> > 0 makes it composite, with region ids <c>&lt;id&gt;_r0</c>, <c>&lt;id&gt;_r1</c>, ...</summary>
            public string State(string name, string parent = "r_root", int regions = 0)
            {
                var id = Vertex(VertexType.State, parent, name, name);
                Model.Vertices.Last().Regions = Enumerable.Range(0, regions).Select(i => new Region { Id = $"{id}_r{i}" }).ToList();
                return id;
            }

            public string T(string source, string target, string trigger = null, string guard = "", TransitionKind kind = TransitionKind.External)
            {
                var id = $"t{++n}";
                var t = new Transition { Id = id, Source = source, Target = target, Kind = kind, Guard = guard };
                if (trigger != null) t.Triggers.Add(trigger);
                Model.Transitions.Add(t);
                return id;
            }

            /// <summary>Initial pseudostate of <paramref name="region"/> targeting <paramref name="target"/>.</summary>
            public void Init(string target, string region = "r_root") => T(Vertex(VertexType.Initial, region), target);
        }

        /// <summary>The issues about cycles of pseudostates, as id|severity|message.</summary>
        private static List<string> Cycles(ModelBuilder b) =>
            Validation.Validate(b.Model)
                .Where(i => i.Message.Contains("form a cycle"))
                .Select(i => $"{i.Id}|{i.Severity.ToString().ToLowerInvariant()}|{i.Message}")
                .ToList();

        /// <summary>A machine with states A and B, A entered first: <c>A -e-> first</c> and <c>last -[else]-> B</c> are up to the test.</summary>
        private static ModelBuilder Machine()
        {
            var b = new ModelBuilder("M");
            b.State("A");
            b.State("B");
            b.Init("A");
            return b;
        }

        [Fact]
        public void ReportsTwoChoicesLeadingToEachOtherOnceOnTheFirstWithTheShortestCycle()
        {
            var b = Machine();
            var c1 = b.Vertex(VertexType.Choice, id: "c1", name: "c1");
            var c2 = b.Vertex(VertexType.Choice, id: "c2", name: "c2");
            b.T("A", c1, trigger: "e");
            b.T(c1, c2, guard: "g()");
            b.T(c1, "B", guard: "else");
            b.T(c2, c1, guard: "h()");
            b.T(c2, "B", guard: "else");
            Assert.Equal(new[] { "c1|error|Choices form a cycle without a state: 'c1' -> 'c2' -> 'c1'." }, Cycles(b));
        }

        [Fact]
        public void ReportsAChoiceTargetingItself()
        {
            var b = Machine();
            var c = b.Vertex(VertexType.Choice, id: "c", name: "c");
            b.T("A", c, trigger: "e");
            b.T(c, c, guard: "g()");
            b.T(c, "B", guard: "else");
            Assert.Equal(new[] { "c|error|Choices form a cycle without a state: 'c' -> 'c'." }, Cycles(b));
        }

        [Fact]
        public void NamesTheKindsOfPseudostatesInTheCycleAndUnnamedOnesByTheirKind()
        {
            var b = Machine();
            var c = b.Vertex(VertexType.Choice, id: "c");
            var j = b.Vertex(VertexType.Junction, id: "j");
            b.T("A", j, trigger: "e");
            b.T(j, c);
            b.T(c, j, guard: "g()");
            b.T(c, "B", guard: "else");
            Assert.Equal(new[] { "c|error|Junctions and choices form a cycle without a state: choice -> junction -> choice." }, Cycles(b));
        }

        [Fact]
        public void ReportsOneIssueForCyclesSharingPseudostatesThroughTheFirstTransitions()
        {
            var b = Machine();
            var j1 = b.Vertex(VertexType.Junction, id: "j1", name: "j1");
            var j2 = b.Vertex(VertexType.Junction, id: "j2", name: "j2");
            var j3 = b.Vertex(VertexType.Junction, id: "j3", name: "j3");
            var j4 = b.Vertex(VertexType.Junction, id: "j4", name: "j4");
            b.T("A", j1, trigger: "e");
            b.T(j1, j2, guard: "a()");
            b.T(j1, j3, guard: "b()");
            b.T(j1, "B", guard: "else");
            b.T(j2, j4);
            b.T(j4, j1);
            b.T(j3, j1);
            Assert.Equal(new[] { "j1|error|Junctions form a cycle without a state: 'j1' -> 'j3' -> 'j1'." }, Cycles(b));
        }

        [Fact]
        public void ReportsSeparateCyclesSeparatelyEvenWhenOneLeadsToTheOther()
        {
            var b = Machine();
            var j1 = b.Vertex(VertexType.Junction, id: "j1", name: "j1");
            var j2 = b.Vertex(VertexType.Junction, id: "j2", name: "j2");
            b.T("A", j1, trigger: "e");
            b.T(j1, j1, guard: "a()");
            b.T(j1, j2, guard: "else");
            b.T(j2, j2, guard: "a()");
            b.T(j2, "B", guard: "else");
            Assert.Equal(new[]
            {
                "j1|error|Junctions form a cycle without a state: 'j1' -> 'j1'.",
                "j2|error|Junctions form a cycle without a state: 'j2' -> 'j2'.",
            }, Cycles(b));
        }

        [Fact]
        public void ReportsCyclesThroughHistoryDefaultsForksAndTheEntryAndExitPointsOfStates()
        {
            // A history default leading back to the history through a choice.
            var b = Machine();
            b.State("S", regions: 1);
            b.State("S1", "S_r0");
            var h = b.Vertex(VertexType.ShallowHistory, "S_r0", id: "h");
            var c = b.Vertex(VertexType.Choice, "S_r0", id: "c", name: "c");
            b.T("A", h, trigger: "e");
            b.T(h, c);
            b.T(c, h, guard: "g()");
            b.T(c, "S1", guard: "else");
            Assert.Equal(new[] { "h|error|Choices and history pseudostates form a cycle without a state: shallow history -> 'c' -> shallow history." }, Cycles(b));

            // An entry point of a state and a choice inside it.
            b = Machine();
            b.State("S", regions: 1);
            b.State("S1", "S_r0");
            var ep = b.Vertex(VertexType.EntryPoint, "S", id: "ep", name: "ep");
            var c2 = b.Vertex(VertexType.Choice, "S_r0", id: "c", name: "c");
            b.T("A", ep, trigger: "e");
            b.T(ep, c2);
            b.T(c2, ep, guard: "g()");
            b.T(c2, "S1", guard: "else");
            Assert.Equal(new[] { "ep|error|Choices and entry points form a cycle without a state: 'ep' -> 'c' -> 'ep'." }, Cycles(b));

            // An exit point of a state and a junction outside it.
            b = Machine();
            b.State("S", regions: 1);
            b.State("S1", "S_r0");
            var xp = b.Vertex(VertexType.ExitPoint, "S", id: "xp", name: "xp");
            var j = b.Vertex(VertexType.Junction, id: "j", name: "j");
            b.T("S1", xp, trigger: "e");
            b.T(xp, j);
            b.T(j, xp, guard: "g()");
            b.T(j, "B", guard: "else");
            Assert.Equal(new[] { "xp|error|Junctions and exit points form a cycle without a state: 'xp' -> 'j' -> 'xp'." }, Cycles(b));

            // A fork and a choice.
            b = Machine();
            b.State("S", regions: 2);
            b.State("S1", "S_r0");
            b.State("S2", "S_r1");
            var f = b.Vertex(VertexType.Fork, id: "f", name: "f");
            var c3 = b.Vertex(VertexType.Choice, id: "c", name: "c");
            b.T("A", c3, trigger: "e");
            b.T(c3, f, guard: "g()");
            b.T(c3, "B", guard: "else");
            b.T(f, "S1");
            b.T(f, c3);
            Assert.Equal(new[] { "f|error|Choices and forks form a cycle without a state: 'f' -> 'c' -> 'f'." }, Cycles(b));
        }

        [Fact]
        public void AcceptsLoopsThroughStatesTheMachinesOwnEntryPointsJoinsAndInternalTransitions()
        {
            var b = Machine();
            // Through a state.
            var c = b.Vertex(VertexType.Choice, id: "c", name: "c");
            b.T("A", c, trigger: "e");
            b.T(c, "A", guard: "g()");
            b.T(c, "B", guard: "else");
            // An internal transition (an error on a pseudostate) does not go anywhere.
            b.T(c, c, kind: TransitionKind.Internal);
            // Through an entry point of the machine, which ends the transition.
            var ep = b.Vertex(VertexType.EntryPoint, id: "ep", name: "ep");
            var j = b.Vertex(VertexType.Junction, id: "j", name: "j");
            b.T(ep, j);
            b.T(j, ep, guard: "g()");
            b.T(j, "B", guard: "else");
            // Through a join.
            b.State("P", regions: 2);
            b.State("P1", "P_r0");
            b.State("P2", "P_r1");
            var jn = b.Vertex(VertexType.Join, id: "jn", name: "jn");
            var c2 = b.Vertex(VertexType.Choice, id: "c2", name: "c2");
            b.T("P1", jn);
            b.T("P2", jn);
            b.T(jn, c2);
            b.T(c2, jn, guard: "g()");
            b.T(c2, "B", guard: "else");
            Assert.Empty(Cycles(b));
        }
    }
}
