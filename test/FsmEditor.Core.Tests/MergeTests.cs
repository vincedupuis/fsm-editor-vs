using System;
using System.Collections.Generic;
using System.Linq;
using FsmEditor.Core;
using Xunit;
using static FsmEditor.Core.Tests.TestFiles;

namespace FsmEditor.Core.Tests
{
    /// <summary>The diagram merge's logic (the VS Code edition's test/unit/merge.test.ts), and the sessions linking the panes.</summary>
    public class MergeTests
    {
        private static Vertex V(FsmModel m, string name) => m.Vertices.First(x => x.Name == name);

        private static string ReplaceFirst(string s, string a, string b)
        {
            var i = s.IndexOf(a, StringComparison.Ordinal);
            return s.Substring(0, i) + b + s.Substring(i + a.Length);
        }

        [Fact]
        public void SplitsIntoTheCurrentAndIncomingSidesInBothStyles()
        {
            var two = "a\n<<<<<<< HEAD\nmine\n=======\ntheirs\n>>>>>>> feature\nb\n";
            Assert.True(Merge.HasConflictMarkers(two));
            var s = Merge.SplitConflicts(two);
            Assert.Equal(("a\nmine\nb\n", "a\ntheirs\nb\n", 1), (s.Current, s.Incoming, s.Conflicts));
            var three = "a\r\n<<<<<<< HEAD\r\nmine\r\n||||||| base\r\nold\r\n=======\r\ntheirs\r\n>>>>>>> feature\r\n";
            s = Merge.SplitConflicts(three);
            Assert.Equal(("a\r\nmine\r\n", "a\r\ntheirs\r\n", 1), (s.Current, s.Incoming, s.Conflicts));
            Assert.False(Merge.HasConflictMarkers("a\n======= not a conflict\n"));
        }

        [Fact]
        public void RefusesMarkersThatDoNotNest()
        {
            Assert.Contains("no end marker", Assert.Throws<ConflictMarkerException>(() => Merge.SplitConflicts("<<<<<<< a\nx\n")).Message);
            Assert.Contains("ends where none is open", Assert.Throws<ConflictMarkerException>(() => Merge.SplitConflicts(">>>>>>> a\n")).Message);
        }

        [Fact]
        public void AModelConflictInTheRealFile()
        {
            var b = Example("Payment.fsm");
            var current = ReplaceFirst(b, "\"entry\": \"capture()\"", "\"entry\": \"capture(); log()\"");
            var incoming = ReplaceFirst(b, "\"entry\": \"capture()\"", "\"entry\": \"settle()\"");
            var marked = ReplaceFirst(b, "      \"entry\": \"capture()\",\n", "<<<<<<< HEAD\n      \"entry\": \"capture(); log()\",\n=======\n      \"entry\": \"settle()\",\n>>>>>>> feature\n");
            var split = Merge.SplitConflicts(marked);
            Assert.Equal(current, split.Current);
            Assert.Equal(incoming, split.Incoming);
            var c = Format.ReadModel(split.Current);
            var i = Format.ReadModel(split.Incoming);
            var changes = Compare.CompareModels(c.Model, i.Model);
            Assert.Equal(new[] { ("state 'Captured'", "entry") }, changes.Select(x => (x.Label, string.Join(",", x.Properties.Select(p => p.Name)))));
        }

        [Fact]
        public void ASideThatIsNotAValidMachineFileIsReported()
        {
            var split = Merge.SplitConflicts("{\n<<<<<<< HEAD\n  \"format\": 1,\n=======\n  \"format\": 1\n>>>>>>> x\n  \"name\": \"M\", \"type\": \"behavioral\" }\n");
            Assert.False(Format.ReadModel(split.Current).Failed);
            Assert.True(Format.ReadModel(split.Incoming).Failed);
        }

        [Fact]
        public void TakesAPropertyOfEachKind()
        {
            var current = ExampleModel("MediaPlayer");
            var incoming = current.Clone();
            V(incoming, "Off").Entry = "sleep()";
            V(incoming, "Paused").DoActivity = "blink(); beep()";
            var t = incoming.Transitions.First(x => x.Triggers.FirstOrDefault() == "eject");
            t.Guard = "hasDisc()";
            t.Target = V(incoming, "Paused").Id;
            V(incoming, "Fault").X += 30;
            incoming.Documentation = "Theirs";
            var result = current.Clone();
            var conflicts = Compare.CompareModels(current, incoming);
            Assert.Equal(5, conflicts.Count);
            foreach (var c in conflicts)
            {
                result = Merge.Adopt(result, incoming, c.Key, c.OldKey ?? c.Key);
                Assert.DoesNotContain(Compare.CompareModels(result, incoming), x => x.Key == c.Key);
            }
            Assert.Empty(Compare.CompareModels(result, incoming));
            // Keeping current again takes it back.
            foreach (var c in conflicts) result = Merge.Adopt(result, current, c.OldKey ?? c.Key, c.Key);
            Assert.Empty(Compare.CompareModels(result, current));
        }

        [Fact]
        public void AnElementOnOneSideOnlyIsAddedWithWhatItContainsOrRemovedWithItsTransitions()
        {
            var current = ExampleModel("MediaPlayer");
            var incoming = current.Clone();
            // Theirs removed Playing (and its regions' contents) and the transitions to it.
            var gone = new HashSet<string>(incoming.Vertices.Where(x => new[] { "Playing", "Stereo", "Mono", "HD", "SD" }.Contains(x.Name) || new[] { "audio", "video" }.Contains(x.Parent)).Select(x => x.Id));
            incoming.Vertices = incoming.Vertices.Where(x => !gone.Contains(x.Id)).ToList();
            incoming.Transitions = incoming.Transitions.Where(x => !gone.Contains(x.Source) && !gone.Contains(x.Target)).ToList();
            var removed = Compare.CompareModels(current, incoming).First(c => c.Label == "state 'Playing'");
            Assert.Equal(ChangeKind.Removed, removed.Kind);
            var result = Merge.Adopt(current, incoming, removed.Key);
            Assert.DoesNotContain(result.Vertices, x => new[] { "Playing", "Stereo", "HD" }.Contains(x.Name));
            Assert.DoesNotContain(result.Transitions, x => x.Target == "Playing" || x.Source == "Playing");
            // The other way: taking current's Playing back brings its contents along.
            var back = Merge.Adopt(result, current, removed.Key);
            Assert.All(new[] { "Playing", "Stereo", "Mono", "HD", "SD" }, n => Assert.Contains(back.Vertices, x => x.Name == n));
            var stereo = V(back, "Stereo");
            Assert.Equal((V(current, "Stereo").X, V(current, "Stereo").Y), (stereo.X, stereo.Y));
            // The result can be written: names given, nothing lost.
            var m = back.Clone();
            Format.AssignNames(m);
            Assert.Empty(Format.WriteMachine(m).Lost);
        }

        [Fact]
        public void TakingATransitionBeforeItsTargetTargetsTheReferenceTheFileWrites()
        {
            FsmModel M(string body) => Read("{ \"format\": 1, \"name\": \"M\", \"type\": \"behavioral\", \"initial\": { \"target\": \"A\" }, " + body + " }").Model;
            var current = M("\"elements\": [{ \"name\": \"A\", \"type\": \"state\" }]");
            var incoming = M("\"elements\": [{ \"name\": \"A\", \"type\": \"state\", \"transitions\": [{ \"type\": \"external\", \"trigger\": \"go\", \"target\": \"Foo\" }] }, { \"name\": \"Foo\", \"type\": \"state\" }]");
            var changes = Compare.CompareModels(current, incoming);
            var tr = changes.First(c => c.What == "transition");
            var r = Merge.Adopt(current, incoming, tr.Key);
            Transition T() => r.Transitions.First(x => x.Triggers.Contains("go"));
            Assert.Equal(Format.Unresolved + "Foo", T().Target);
            Assert.Contains("\"target\": \"Foo\"", Format.WriteMachine(r).Model);
            r = Merge.Adopt(r, incoming, changes.First(c => c.Key == "element@Foo").Key);
            Format.AssignNames(r);
            Assert.True(Format.ResolveReferences(r, Format.WriteMachine(r)));
            Assert.Equal(V(r, "Foo").Id, T().Target);
        }

        // ---------------------------------------------------------------- sessions

        private sealed class Pane : IDiffPane
        {
            public FsmModel Model;
            public PaneDiff Diff;
            public string Revealed;
            public readonly List<string> Toasts = new List<string>();

            public FsmModel CurrentModel() => Model;
            public void ShowDiff(PaneDiff diff) => Diff = diff;
            public void Reveal(string id) => Revealed = id;
            public void Toast(string message) => Toasts.Add(message);
        }

        /// <summary>A result document with undo and redo, as the editions hold one: a state per edit.</summary>
        private sealed class Host : IMergeHost
        {
            public readonly List<(object State, FsmModel Model)> History = new List<(object, FsmModel)>();
            public int At;

            public Host(FsmModel model) => History.Add((new object(), model));
            public FsmModel Model => History[At].Model;
            public object State() => History[At].State;

            public string Edit(FsmModel model, string label)
            {
                History.RemoveRange(At + 1, History.Count - At - 1);
                History.Add((new object(), model));
                At++;
                return null;
            }
        }

        [Fact]
        public void ADiffMarksBothPanesAndHidesLayoutChanges()
        {
            var a = ExampleModel("Payment");
            var b = a.Clone();
            V(b, "Captured").Entry = "settle()";
            V(b, "Declined").X += 40;
            var older = new Pane { Model = a };
            var newer = new Pane { Model = b };
            var d = new DiffSession(older, newer, "Payment.fsm (HEAD)", "Payment.fsm");
            d.Update();
            Assert.Equal(1, newer.Diff.Count);
            Assert.Equal("changed", newer.Diff.Marks[V(b, "Captured").Id]);
            Assert.Equal(new[] { "entry: capture() → settle()" }, newer.Diff.Details[V(b, "Captured").Id]);
            Assert.Equal("old", older.Diff.Role);
            d.SetShowLayout(true);
            Assert.Equal(2, newer.Diff.Count);
            d.Navigate(1);
            Assert.Equal(V(a, "Captured").Id, older.Revealed);
            d.Remove(older);
            Assert.Null(newer.Diff);
        }

        [Fact]
        public void MergeChoicesFollowUndoAndRedo()
        {
            var current = ExampleModel("Payment");
            var incoming = current.Clone();
            V(incoming, "Captured").Entry = "settle()";
            var texts = new MachineTexts(Example("Payment.fsm"), null);
            var host = new Host(current.Clone());
            var reads = new Queue<FsmModel>(new[] { current, incoming });
            var session = new MergeSession("m1", texts, texts, null, _ => reads.Dequeue(), host, () => host.Model);
            var result = new Pane();
            session.Attach(result, "result");
            Assert.Equal(1, session.ConflictCount);
            Assert.Null(result.Diff.Conflicts[0].Choice);
            session.Choose(result.Diff.Conflicts[0].Key, "incoming");
            Assert.Equal("settle()", V(host.Model, "Captured").Entry);
            Assert.Equal("incoming", result.Diff.Conflicts[0].Choice);
            Assert.Equal("resolved", result.Diff.Marks[V(host.Model, "Captured").Id]);
            // Undo: the result is back to the current side, and so is the choice.
            host.At--;
            session.Update();
            Assert.Null(result.Diff.Conflicts[0].Choice);
            Assert.Equal("conflict", result.Diff.Marks[V(host.Model, "Captured").Id]);
            // Redo: chosen again.
            host.At++;
            session.Update();
            Assert.Equal("incoming", result.Diff.Conflicts[0].Choice);
            Assert.Equal("1 of 1 chosen", result.Diff.Title);
        }
    }
}
