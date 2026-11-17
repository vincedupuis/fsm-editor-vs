using System.Collections.Generic;
using System.Linq;
using FsmEditor.Core;
using Xunit;
using static FsmEditor.Core.Tests.TestFiles;

namespace FsmEditor.Core.Tests
{
    /// <summary>Comparing two versions of a machine (the VS Code edition's test/unit/compare.test.ts).</summary>
    public class CompareTests
    {
        private static Vertex V(FsmModel m, string name) => m.Vertices.First(x => x.Name == name);

        private static List<(string, string, string)> Summary(FsmModel older, FsmModel newer) =>
            Compare.CompareModels(older, newer).Select(c => (c.Kind.ToString().ToLowerInvariant(), c.Label, string.Join("|", Compare.DescribeChange(c, true)))).ToList();

        [Fact]
        public void FindsNothingBetweenEqualMachines() => Assert.Empty(Compare.CompareModels(ExampleModel("MediaPlayer"), ExampleModel("MediaPlayer")));

        [Fact]
        public void ALayoutOnlyChangeIsHiddenByDefaultAndMovingAStateChangesNothingInsideIt()
        {
            var a = ExampleModel("MediaPlayer");
            var b = a.Clone();
            foreach (var x in b.Vertices)
            {
                if (x.Name == "On" || a.Vertices.FirstOrDefault(y => y.Id == x.Parent)?.Name == "On" || new[] { "On.", "audio", "video" }.Contains(x.Parent)
                    || new[] { "Stopped", "Paused", "Playing", "Stereo", "Mono", "HD", "SD", "choice1*" }.Contains(x.Name))
                {
                    x.X += 25;
                }
            }
            foreach (var t in b.Transitions)
            {
                if (t.Points.Count > 0 && b.Vertices.First(x => x.Id == t.Source).X != a.Vertices.First(x => x.Id == t.Source).X
                    && b.Vertices.FirstOrDefault(x => x.Id == t.Target)?.X != a.Vertices.FirstOrDefault(x => x.Id == t.Target)?.X)
                {
                    t.Points = t.Points.Select(p => new PointD(p.X + 25, p.Y)).ToList();
                }
            }
            var changes = Compare.CompareModels(a, b);
            var on = changes.First(c => c.Label == "state 'On'");
            Assert.Equal(new[] { "x: 340 → 365" }, Compare.DescribeChange(on, true));
            Assert.False(Compare.IsModelChange(on));
            Assert.DoesNotContain(changes, Compare.IsModelChange);
            Assert.DoesNotContain(changes, c => c.Label == "state 'Stopped'");
        }

        [Fact]
        public void ARenameIsOneElementRemovedAndOneAdded()
        {
            var a = ExampleModel("Order");
            var b = a.Clone();
            V(b, "Shipped").Name = "Delivered";
            var kinds = Compare.CompareModels(a, b).Select(c => (c.Kind, c.Label)).ToList();
            Assert.Equal(new[]
            {
                (ChangeKind.Added, "state 'Delivered'"), (ChangeKind.Added, "transition Delivered -> final1"),
                (ChangeKind.Removed, "state 'Shipped'"), (ChangeKind.Removed, "transition Shipped -> final1"),
            }, kinds.Where(k => k.Kind != ChangeKind.Changed));
            var t = Compare.CompareModels(a, b).First(c => c.Kind == ChangeKind.Changed && c.What == "transition");
            Assert.Equal(new[] { "target: Shipped → Delivered" }, Compare.DescribeChange(t, false));
        }

        [Fact]
        public void AChangeInEachKindOfPropertyIsListed()
        {
            var a = ExampleModel("MediaPlayer");
            var b = a.Clone();
            V(b, "Off").Entry = "sleep()";
            V(b, "Paused").Deferrable = new List<string> { "eject" };
            var t = b.Transitions.First(x => x.Triggers.FirstOrDefault() == "eject");
            t.Guard = "hasDisc()";
            t.Action = "beep()";
            b.Transitions.First(x => x.Triggers.FirstOrDefault() == "powerOff").Kind = TransitionKind.Local;
            V(b, "comment1*").Text = "Other text";
            b.Documentation = "Changed";
            var output = Summary(a, b);
            Assert.Equal(new[]
            {
                ("changed", "the state machine", string.Join("|", new[] { "documentation: Example UML state machine: composite and orthogonal state… → Changed" })),
                ("changed", "comment", string.Join("|", new[] { "text: Deep history restores the\nlast active playback substates\n… → Other text" })),
                ("changed", "state 'Off'", string.Join("|", new[] { "entry: powerDown() → sleep()" })),
                ("changed", "state 'Paused'", string.Join("|", new[] { "defer: (none) → eject" })),
                ("changed", "transition On -> Off (powerOff)", string.Join("|", new[] { "kind: external → local" })),
                ("changed", "transition Stopped -> choice1 (eject)", string.Join("|", new[] { "guard: (none) → hasDisc()", "action: (none) → beep()" })),
            }, output);
        }

        [Fact]
        public void ARetargetedTransitionIsChangedNotRemovedAndAdded()
        {
            var a = ExampleModel("Payment");
            var b = a.Clone();
            b.Transitions.First(x => x.Triggers.FirstOrDefault() == "declined").Target = V(b, "Captured").Id;
            Assert.Equal(new[] { ("changed", "transition Authorizing -> Captured (declined)", string.Join("|", new[] { "target: Declined → Captured" })) }, Summary(a, b));
        }

        [Fact]
        public void TheDiffOfAConversionShowsNothing()
        {
            var converted = Conversion.ConvertGroup(new[] { new ConvertInput("/x/MediaPlayer.fsm", Old("MediaPlayer")) })[0];
            Assert.Empty(Compare.CompareModels(converted.Model, ExampleModel("MediaPlayer")));
        }

        [Fact]
        public void MovingAnElementToAnotherRegionOfItsStateIsAChangeOfTheModel()
        {
            FsmModel Mk(bool inFirst) => Read("{ \"format\": 1, \"name\": \"M\", \"type\": \"behavioral\", \"elements\": [{ \"name\": \"S\", \"type\": \"state\", \"regions\": ["
                + "{ \"name\": \"R1\", \"elements\": [" + (inFirst ? "{ \"name\": \"X\", \"type\": \"state\" }, { \"name\": \"Y\", \"type\": \"state\" }" : "{ \"name\": \"Y\", \"type\": \"state\" }") + "] },"
                + "{ \"name\": \"R2\", \"elements\": [" + (inFirst ? "{ \"name\": \"Z\", \"type\": \"state\" }" : "{ \"name\": \"Z\", \"type\": \"state\" }, { \"name\": \"X\", \"type\": \"state\" }") + "] }"
                + "] }] }").Model;
            var x = Compare.CompareModels(Mk(true), Mk(false)).Where(Compare.IsModelChange).ToList();
            Assert.Equal(new[] { ("state 'X'", "in: 'S', region 'R1' → 'S', region 'R2'") },
                x.Select(c => (c.Label, string.Join("|", c.Properties.Where(p => !p.Layout).Select(p => $"{p.Name}: {p.Old} → {p.New}")))));
        }
    }
}
