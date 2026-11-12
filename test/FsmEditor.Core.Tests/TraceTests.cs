using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FsmEditor.Core;
using Xunit;

namespace FsmEditor.Core.Tests
{
    /// <summary>The Trace Player's formats, replay and session, as test/unit/trace.test.ts of the VS Code edition checks them.</summary>
    public class TraceTests
    {
        private static string Examples(string file) => Path.Combine(AppContext.BaseDirectory, "examples", file);

        private static FsmModel Example(string file) => Xmi.FromXmi(File.ReadAllText(Examples(file)));

        private static TraceInstance Instance(string key, FsmModel model, string file = null) =>
            new TraceInstance { Key = key, File = file ?? $"/{key}.fsm", Model = model, Index = new ModelIndex(model) };

        /// <summary>The timeline of <paramref name="lines"/> read with <paramref name="format"/>, every record about the instance of its machine (or <paramref name="fallback"/>).</summary>
        private static List<TimelineEntry> Timeline(IEnumerable<string> lines, TraceFormat format, Dictionary<string, TraceInstance> instances, TraceInstance fallback = null) =>
            lines.Select((text, line) => format.Parse(text, line))
                .Where(r => r != null)
                .Select(r => new TimelineEntry { Record = r, Instance = instances.TryGetValue(r.Machine, out var i) ? i : fallback })
                .ToList();

        private static Dictionary<string, HashSet<TraceKind>> KindsOf(List<TimelineEntry> entries)
        {
            var kinds = new Dictionary<string, HashSet<TraceKind>>();
            foreach (var e in entries.Where(e => e.Instance != null))
            {
                if (!kinds.TryGetValue(e.Instance.Key, out var set)) kinds[e.Instance.Key] = set = new HashSet<TraceKind>();
                set.Add(e.Record.Kind);
            }
            return kinds;
        }

        private static TraceHighlight At(List<TimelineEntry> entries, int cursor, string key) =>
            TraceReplay.Replay(entries, cursor, KindsOf(entries)).Where(x => x.Instance.Key == key).Select(x => x.Highlight).FirstOrDefault();

        private static List<string> Sorted(IEnumerable<string> ids) => ids.OrderBy(i => i, StringComparer.Ordinal).ToList();

        /// <summary>The model of the tracing tests: S, a junction leading to a choice, X and Y.</summary>
        private static FsmModel Traced()
        {
            var m = new FsmModel { Id = "sm", Name = "Traced", Regions = { new Region { Id = "r_root" } } };
            void V(string id, VertexType type, string name = null) => m.Vertices.Add(new Vertex { Id = id, Type = type, Name = name ?? id, Parent = "r_root", W = 10, H = 10 });
            void T(string id, string s, string t, string trigger = null, string guard = "")
            {
                var tr = new Transition { Id = id, Source = s, Target = t, Guard = guard };
                if (trigger != null) tr.Triggers.Add(trigger);
                m.Transitions.Add(tr);
            }
            V("S", VertexType.State);
            V("X", VertexType.State);
            V("Y", VertexType.State);
            V("j", VertexType.Junction);
            V("c", VertexType.Choice);
            V("i", VertexType.Initial, "");
            T("tInit", "i", "S");
            T("tSJ", "S", "j", "e");
            T("tJC", "j", "c", guard: "g()");
            T("tJY", "j", "Y", guard: "else");
            T("tCX", "c", "X", guard: "h()");
            T("tCY", "c", "Y", guard: "else");
            T("tXS", "X", "S", "back");
            T("tYS", "Y", "S", "back");
            return m;
        }

        /// <summary>What the onTrace hook of the code generated for Traced writes: start, e (to Y through the junction's else), back, e (to X through the choice).</summary>
        private static readonly string[] TracedLog =
        {
            "fsm Traced transition tInit initial -> S",
            "fsm Traced enter S S",
            "fsm Traced event e e",
            "fsm Traced exit S S",
            "fsm Traced transition tSJ S -> j (e)",
            "fsm Traced transition tJY j -> Y [else]",
            "fsm Traced enter Y Y",
            "fsm Traced event back back",
            "fsm Traced exit Y Y",
            "fsm Traced transition tYS Y -> S (back)",
            "fsm Traced enter S S",
            "fsm Traced event e e",
            "fsm Traced exit S S",
            "fsm Traced transition tSJ S -> j (e)",
            "fsm Traced transition tJC j -> c [g()]",
            "fsm Traced transition tCX c -> X [h()]",
            "fsm Traced enter X X",
        };

        // ---------------------------------------------------------------- formats

        [Fact]
        public void ReadsTheBuiltInFormatAfterAnyPrefixWithItsTime()
        {
            var r = TraceFormats.BuiltIn.Parse("2026-11-11T10:02:03.120Z INFO [main] fsm Order.Payment enter p_auth Authorizing", 4);
            Assert.Equal(TraceKind.Enter, r.Kind);
            Assert.Equal("Order.Payment", r.Machine);
            Assert.Equal("p_auth", r.Id);
            Assert.Equal("Authorizing", r.Name);
            Assert.Equal(4, r.Line);
            Assert.Equal(new DateTimeOffset(2026, 11, 11, 10, 2, 3, 120, TimeSpan.Zero).ToUnixTimeMilliseconds(), r.Time);
            Assert.Equal("Cart -> Payment (checkout)", TraceFormats.BuiltIn.Parse("fsm Order transition t1 Cart -> Payment (checkout)", 0).Name);
            Assert.Null(TraceFormats.BuiltIn.Parse("fsm Order enter", 0));
            Assert.Null(TraceFormats.BuiltIn.Parse("the fsm Order is ready", 0));
        }

        [Fact]
        public void ParsesTimes()
        {
            var local = new DateTimeOffset(new DateTime(2026, 11, 11, 10, 2, 3, 500, DateTimeKind.Local)).ToUnixTimeMilliseconds();
            Assert.Equal(local, TraceFormats.ParseTime("2026-11-11 10:02:03,5"));
            Assert.Equal((10 * 3600 + 2 * 60 + 3) * 1000 + 250, TraceFormats.ParseTime("10:02:03.250"));
            Assert.Equal(1762855323500, TraceFormats.ParseTime("1762855323.5"));
            Assert.Equal(1762855323500, TraceFormats.ParseTime("1762855323500"));
            Assert.Null(TraceFormats.ParseTime("soon"));
        }

        [Fact]
        public void ReadsOtherLogsWithTheRulesOfAFormat()
        {
            var f = TraceFormats.Load(@"[{
                ""name"": ""App"",
                ""time"": ""^\\[(?<time>[^\\]]+)\\]"",
                ""rules"": [
                    { ""kind"": ""event"", ""match"": ""got (?<name>\\w+)"" },
                    { ""match"": ""(?<kind>enter|exit)ing (?<machine>\\w+)/(?<name>\\w+)"" },
                    { ""kind"": ""transition"", ""match"": ""(?<source>\\w+) => (?<target>\\w+)"" }
                ],
                ""machine"": ""Door""
            }]")[1];
            var lines = new[] { "[10:00:00.000] got open", "[10:00:00.001] entering Car/Opened", "[10:00:00.002] Closed => Opened", "noise" };
            var got = lines.Select((l, i) => f.Parse(l, i)).Select(r => r == null ? null : $"{r.Kind.Text()}|{r.Machine}|{r.Name}|{r.Source}|{r.Target}|{r.Time}").ToList();
            Assert.Equal(new[] { "event|Door|open|||36000000", "enter|Car|Opened|||36000001", "transition|Door|Closed -> Opened|Closed|Opened|36000002", null }, got);
        }

        [Fact]
        public void ReportsBadFormatsAndPicksTheFormatRecognizingTheMostLines()
        {
            object Spec(string json) => ClipboardJson.ParseJson(json);
            Assert.Throws<TraceFormatException>(() => TraceFormats.Compile(Spec(@"{ ""name"": ""X"", ""rules"": [{ ""kind"": ""jump"", ""match"": ""a"" }] }")));
            Assert.Contains("not a valid regular expression", Assert.Throws<TraceFormatException>(() => TraceFormats.Compile(Spec(@"{ ""name"": ""X"", ""rules"": [{ ""match"": ""a("" }] }"))).Message);
            Assert.Contains("needs a kind", Assert.Throws<TraceFormatException>(() => TraceFormats.Compile(Spec(@"{ ""name"": ""X"", ""rules"": [{ ""match"": ""a"" }] }"))).Message);
            var errors = new List<string>();
            var formats = TraceFormats.Load(@"[{ ""name"": ""Bad"", ""rules"": [] }, { ""name"": ""Mine"", ""rules"": [{ ""kind"": ""enter"", ""match"": ""-> (?<name>\\w+)"" }] }]", errors);
            Assert.Equal(new[] { "FSM Editor (built-in)", "Mine" }, formats.Select(x => x.Name));
            Assert.Equal(new[] { "Trace format 'Bad' has no rules." }, errors);
            Assert.Equal("Mine", TraceFormats.Detect(formats, new[] { "-> A", "-> B", "fsm M enter s1 A" }).Name);
            Assert.Equal("FSM Editor (built-in)", TraceFormats.Detect(formats, new[] { "nothing here" }).Name);
            errors.Clear();
            Assert.Single(TraceFormats.Load("[{", errors));
            Assert.StartsWith("The trace formats setting is not valid JSON", errors.Single());
        }

        // ---------------------------------------------------------------- replay

        [Fact]
        public void ReplaysALogWrittenByTheOnTraceHook()
        {
            var entries = Timeline(TracedLog, TraceFormats.BuiltIn, new Dictionary<string, TraceInstance> { ["Traced"] = Instance("Traced", Traced()) });
            Assert.Null(At(entries, -1, "Traced"));
            var started = At(entries, 1, "Traced");
            Assert.Equal(new[] { "S" }, started.Active);
            Assert.Equal(new[] { "tInit" }, started.Taken);
            Assert.Equal(new[] { "S" }, started.Current);

            var firstE = entries.FindIndex(e => e.Record.Kind == TraceKind.Event);
            var toY = At(entries, firstE + 4, "Traced"); // event, exit S, tSJ, tJY, enter Y
            Assert.Equal(new[] { "Y" }, toY.Active);
            Assert.Equal(new[] { "tSJ", "tJY" }, toY.Taken);
            Assert.Equal(new[] { "j" }, toY.Via);
            Assert.Equal("e", toY.Event);

            var end = At(entries, entries.Count - 1, "Traced");
            Assert.Equal(new[] { "X" }, end.Active);
            Assert.Equal(new[] { "tSJ", "tJC", "tCX" }, end.Taken);
            Assert.Equal(new[] { "c", "j" }, Sorted(end.Via));
            Assert.Equal(new[] { "X" }, end.Current);

            // Going back in time: the middle of the second step.
            var mid = At(entries, entries.Count - 3, "Traced"); // after tJC
            Assert.Empty(mid.Active);
            Assert.Equal(new[] { "tSJ", "tJC" }, mid.Taken);
        }

        [Fact]
        public void ShowsASubmachineAsAnActiveStateInItsParentAndItsStatesInItsOwnInstance()
        {
            var lines = File.ReadLines(Examples("demo.log")).TakeWhile(l => !l.Contains("Order event cancel")).ToList();
            var instances = new Dictionary<string, TraceInstance>
            {
                ["Order"] = Instance("Order", Example("Order.fsm")),
                ["Order.Payment"] = Instance("Order.Payment", Example("Payment.fsm"), "/Payment.fsm"),
            };
            var entries = Timeline(lines, TraceFormats.BuiltIn, instances);
            var output = TraceReplay.Replay(entries, entries.Count - 1, KindsOf(entries));
            Assert.Equal(new[] { "o_pay" }, output.Single(x => x.Instance.Key == "Order").Highlight.Active);
            var payment = output.Single(x => x.Instance.Key == "Order.Payment");
            Assert.Equal(new[] { "p_auth" }, payment.Highlight.Active);
            Assert.Equal("/Payment.fsm", payment.Instance.File);
        }

        [Fact]
        public void FindsStatesAndTransitionsByNameAndFollowsTransitionsWhenTheLogHasNoEntries()
        {
            var model = Traced();
            var ix = new ModelIndex(model);
            Assert.Equal("S", TraceReplay.FindState(ix, "S")?.Id);
            Assert.Null(TraceReplay.FindState(ix, "nope"));
            Assert.Equal(new[] { "tSJ", "tJC", "tCX" }, TraceReplay.FindTransitions(ix, "", "S -> X (e)").Select(t => t.Id));
            Assert.Equal(new[] { "tSJ", "tJY" }, TraceReplay.FindTransitions(ix, "", "", "S", "Y").Select(t => t.Id));

            var f = TraceFormats.Load(@"[{ ""name"": ""Moves"", ""rules"": [{ ""kind"": ""transition"", ""match"": ""(?<source>\\w+) -> (?<target>\\w+)"" }] }]")[1];
            var entries = Timeline(new[] { "S -> X", "X -> S", "S -> Y" }, f, new Dictionary<string, TraceInstance>(), Instance("Traced", model));
            var h = At(entries, 2, "Traced");
            Assert.Equal(new[] { "Y" }, h.Active);
            Assert.Equal(new[] { "tSJ", "tJY" }, h.Taken);
            Assert.Equal(new[] { "X" }, At(entries, 0, "Traced").Active);
        }

        [Fact]
        public void EntersTheEnclosingStatesAndLeavesWhatElseWasActiveInTheRegion()
        {
            var m = new FsmModel { Id = "sm", Name = "Nest", Regions = { new Region { Id = "r_root" } } };
            void S(string id, string parent, int regions = 0) => m.Vertices.Add(new Vertex
            {
                Id = id, Name = id, Type = VertexType.State, Parent = parent, W = 10, H = 10,
                Regions = Enumerable.Range(0, regions).Select(i => new Region { Id = $"{id}_r{i}" }).ToList(),
            });
            S("A", "r_root");
            S("C", "r_root", 2);
            S("C1", "C_r0");
            S("C2", "C_r0");
            S("D1", "C_r1");
            var entries = Timeline(new[] { "fsm Nest enter A", "fsm Nest enter C1", "fsm Nest enter D1", "fsm Nest enter C2", "fsm Nest exit C", "fsm Nest enter C::C1" },
                TraceFormats.BuiltIn, new Dictionary<string, TraceInstance> { ["Nest"] = Instance("Nest", m) });
            List<string> Active(int i) => Sorted(At(entries, i, "Nest").Active);
            Assert.Equal(new[] { "C", "C1" }, Active(1));
            Assert.Equal(new[] { "C", "C2", "D1" }, Active(3));
            Assert.Empty(Active(4));
            Assert.Equal(new[] { "C", "C1" }, Active(5));
        }

        // ---------------------------------------------------------------- session

        private static TraceSession ExampleSession()
        {
            var s = new TraceSession();
            var machines = new[] { "MediaPlayer.fsm", "Order.fsm", "Payment.fsm" }.Select(f => TraceMachine.Of(Examples(f), Example(f))).ToList();
            s.SetMachines(machines, _ => null);
            return s;
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "fsm-trace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void ReplaysTheDemoLogOnTheExamples()
        {
            var s = ExampleSession();
            var log = s.Open(Examples("demo.log"));
            Assert.Null(s.Open(Examples("demo.log")));
            Assert.True(s.ReadNew(log));
            Assert.Equal(TraceFormats.BuiltInName, log.Format.Name);
            Assert.Equal(153, s.Entries.Count);
            Assert.Equal(152, s.Cursor);
            Assert.Empty(s.Problems);
            Assert.False(log.NeedsMachine);

            var views = s.Views();
            Assert.Equal(new[] { "o_final" }, views[Examples("Order.fsm")].Instances.Single().Active);
            Assert.Equal(-1, views[Examples("Order.fsm")].Instances.Single().Color);
            Assert.Equal(new[] { "v_final" }, views[Examples("MediaPlayer.fsm")].Instances.Single().Active);
            // Payment runs alone first, then inside Order: two instances, colored in that order.
            var payment = views[Examples("Payment.fsm")];
            Assert.Equal(new[] { "Payment|0", "Order.Payment|1" }, payment.Instances.Select(i => $"{i.Label}|{i.Color}"));
            Assert.Equal(new[] { 0 }, payment.Active["p_final"]);

            // Back in time, to the second checkout: Order in its Payment state, the Payment instance inside it authorizing.
            var at = s.Entries.Select((e, i) => (e, i)).Where(x => x.e.Record.Machine == "Order.Payment" && x.e.Record.Id == "p_auth" && x.e.Record.Kind == TraceKind.Enter).ElementAt(1).i;
            s.Seek(at);
            Assert.False(s.Live);
            views = s.Views();
            Assert.Equal(new[] { "o_pay" }, views[Examples("Order.fsm")].Instances.Single().Active);
            Assert.Equal(new[] { "p_auth" }, views[Examples("Payment.fsm")].Instances.Single(i => i.Label == "Order.Payment").Active);
            Assert.Contains("p_auth", views[Examples("Payment.fsm")].Current);
            Assert.Equal("▶ Trace · event checkout", views[Examples("Order.fsm")].StatusText);
            // Order's event is the step of the Payment instance inside it too: not its own last event (cancel), nor that transition.
            Assert.Equal("▶ Trace · event checkout", views[Examples("Payment.fsm")].StatusText);
            Assert.Equal(new[] { "pt1" }, views[Examples("Payment.fsm")].Instances.Single(i => i.Label == "Order.Payment").Taken);
            var rows = s.Rows();
            Assert.Equal(s.Cursor, rows.Single(r => r.Index == s.Cursor).Index);
            Assert.All(rows, r => Assert.True(r.Resolved));
            Assert.Equal("Order.Payment", rows.Single(r => r.Index == s.Cursor).Machine);
            Assert.Equal(1, rows.Single(r => r.Index == s.Cursor).Color);
        }

        [Fact]
        public void PlaysAtTheSpeedOfTheLog()
        {
            var s = ExampleSession();
            s.ReadNew(s.Open(Examples("demo.log")));
            s.Play();
            Assert.True(s.Playing);
            Assert.Equal(-1, s.Cursor);
            Assert.Equal(0, s.NextDelay());
            s.Advance();
            Assert.Equal(150, s.NextDelay()); // 150 ms between the first two lines
            s.Speed = 2;
            Assert.Equal(75, s.NextDelay());
            s.Seek(s.Entries.Count - 1);
            Assert.False(s.Playing);
            Assert.Null(s.NextDelay());
            Assert.False(s.Live); // reaching the end by hand doesn't follow the log
            s.GoLive();
            Assert.True(s.Live);
            // Played to the end, it follows the log.
            s.Play();
            Assert.False(s.Live);
            while (s.NextDelay() != null) s.Advance();
            Assert.False(s.Playing);
            Assert.True(s.Live);
            Assert.Equal(s.Entries.Count - 1, s.Cursor);
        }

        [Fact]
        public void FollowsALogAsItGrowsReadingCompleteLinesOnly()
        {
            var dir = TempDir();
            var path = Path.Combine(dir, "app.log");
            File.WriteAllText(path, "2026-11-11T10:00:00.000Z fsm Order transition ot1 initial -> Cart\n2026-11-11T10:00:00.100Z fsm Order enter o_ca");
            var s = ExampleSession();
            var log = s.Open(path);
            s.ReadNew(log);
            Assert.Single(s.Entries);
            File.AppendAllText(path, "rt Cart\n");
            Assert.True(s.ReadNew(log));
            Assert.Equal(2, s.Entries.Count);
            Assert.Equal(1, s.Cursor);
            Assert.Equal(new[] { "o_cart" }, s.Views()[Examples("Order.fsm")].Instances.Single().Active);
            Assert.False(s.ReadNew(log));
            // Truncated (rotated): read again from the start.
            File.WriteAllText(path, "2026-11-11T10:00:01.000Z fsm Order transition ot1 initial -> Cart\n");
            Assert.True(s.ReadNew(log));
            Assert.Single(s.Entries);
            File.Delete(path);
            Assert.True(s.ReadNew(log));
            Assert.Equal("app.log does not exist.", log.Error);
            Directory.Delete(dir, true);
        }

        [Fact]
        public void AsksOnceForTheMachineOfLinesNamingNone()
        {
            var dir = TempDir();
            var path = Path.Combine(dir, "door.log");
            File.WriteAllText(path, "-> Cart\n-> Payment\n-> Nowhere\n");
            var s = ExampleSession();
            s.SetFormats(@"[{ ""name"": ""Arrows"", ""rules"": [{ ""kind"": ""enter"", ""match"": ""-> (?<name>\\w+)"" }] }]");
            var log = s.Open(path);
            s.ReadNew(log);
            Assert.Equal("Arrows", log.Format.Name);
            Assert.True(log.NeedsMachine);
            Assert.Equal(new[] { "Choose the state machine of the lines of door.log that name none.", "3 records designate nothing in their state machine (shown crossed out)." }, s.Problems);
            Assert.True(s.ShouldAsk(log));
            Assert.False(s.ShouldAsk(log));
            s.ChooseMachine(log, Examples("Order.fsm"));
            Assert.Equal("Order", log.MachineName);
            Assert.Equal(new[] { "1 record designates nothing in their state machine (shown crossed out)." }, s.Problems);
            s.Seek(1);
            var order = s.Views()[Examples("Order.fsm")].Instances.Single();
            Assert.Equal("Order (door.log)", order.Label);
            Assert.Equal(new[] { "o_pay" }, order.Active);
            Assert.False(s.Rows().Last().Resolved);
            Directory.Delete(dir, true);
        }

        [Fact]
        public void ReportsMachinesThatCannotBeResolved()
        {
            var dir = TempDir();
            var path = Path.Combine(dir, "x.log");
            File.WriteAllText(path, "fsm Gate enter a A\nfsm Order.Nope enter b B\n");
            var s = ExampleSession();
            s.ReadNew(s.Open(path));
            Assert.Equal(new[]
            {
                "No state machine named 'Gate' in the workspace (Gate).",
                "Order has no submachine state 'Nope' (Order.Nope).",
                "2 records designate nothing in their state machine (shown crossed out).",
            }, s.Problems);
            Directory.Delete(dir, true);
        }

        [Fact]
        public void FormatsTimesForDisplay()
        {
            Assert.Equal("", TraceSession.FormatTime(0));
            Assert.Equal("10:02:03.250", TraceSession.FormatTime((10 * 3600 + 2 * 60 + 3) * 1000 + 250));
            var local = new DateTimeOffset(new DateTime(2026, 11, 11, 10, 2, 3, 120, DateTimeKind.Local)).ToUnixTimeMilliseconds();
            Assert.Equal("10:02:03.120", TraceSession.FormatTime(local));
        }
    }
}
