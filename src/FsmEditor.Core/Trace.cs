using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FsmEditor.Core
{
    // The Trace Player's logic, as src/trace/format.ts, replay.ts and player.ts of the VS Code edition: log formats,
    // replaying records on the machines, and the session (logs, timeline, cursor) behind the Trace Player window.

    public enum TraceKind { Event, Enter, Exit, Transition }

    /// <summary>One line of a log that a format recognized.</summary>
    public sealed class TraceRecord
    {
        public TraceKind Kind { get; set; }
        /// <summary>Milliseconds (since 1970, or since midnight for times without a date); null when the line has none.</summary>
        public double? Time { get; set; }
        /// <summary>Machine name, followed for a submachine by the submachine states leading to it (<c>Order.Payment</c>); "" when the line has none.</summary>
        public string Machine { get; set; } = "";
        /// <summary>xmi:id of the state or transition, or the event's name.</summary>
        public string Id { get; set; } = "";
        /// <summary>Name of the state, transition or event.</summary>
        public string Name { get; set; } = "";
        /// <summary>Transitions without an id: the names of their source and target.</summary>
        public string Source { get; set; }
        public string Target { get; set; }
        /// <summary>0-based line number in the file.</summary>
        public int Line { get; set; }
        /// <summary>The line as written, for display.</summary>
        public string Text { get; set; } = "";
    }

    public static class TraceKinds
    {
        public static string Text(this TraceKind k) => k.ToString().ToLowerInvariant();

        public static TraceKind? Parse(string s)
        {
            switch (s)
            {
                case "event": return TraceKind.Event;
                case "enter": return TraceKind.Enter;
                case "exit": return TraceKind.Exit;
                case "transition": return TraceKind.Transition;
                default: return null;
            }
        }
    }

    public sealed class TraceFormatException : Exception
    {
        public TraceFormatException(string message) : base(message) { }
    }

    /// <summary>How the lines of a log are recognized: the built-in format, or one of the Trace formats setting.</summary>
    public abstract class TraceFormat
    {
        public abstract string Name { get; }
        public abstract bool BuiltIn { get; }
        /// <summary>The record of a line, or null when the format doesn't recognize it.</summary>
        public abstract TraceRecord Parse(string text, int line);
    }

    /// <summary>
    /// The trace formats. The built-in one is what an <c>onTrace</c> hook of the generated code can write, after any
    /// prefix of the application's logger: <c>2026-11-11T10:02:03.120Z INFO fsm Order.Payment enter p_auth Authorizing</c>.
    /// Others come from the setting: a JSON array of <c>{ name, rules: [{ kind?, match }], time?, machine? }</c>, the
    /// rules being regular expressions with named groups (time, machine, id, name, source, target, kind).
    /// </summary>
    public static class TraceFormats
    {
        public const string BuiltInName = "FSM Editor (built-in)";

        /// <summary>An ISO 8601 date and time, or a time of day, as loggers print them.</summary>
        private static readonly Regex IsoTime = new Regex(@"[0-9]{4}-[0-9]{2}-[0-9]{2}[T ][0-9]{2}:[0-9]{2}:[0-9]{2}(?:[.,][0-9]+)?(?:Z|[+-][0-9]{2}:?[0-9]{2})?|\b[0-9]{2}:[0-9]{2}:[0-9]{2}(?:[.,][0-9]+)?", RegexOptions.CultureInvariant);
        private static readonly Regex BuiltInLine = new Regex(@"(?:^|\s)fsm\s+(\S+)\s+(event|enter|exit|transition)\s+(\S+)(?:\s+(.*?))?\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex Number = new Regex(@"^[0-9]+(\.[0-9]+)?$");
        private static readonly Regex TimeOfDay = new Regex(@"^([0-9]{2}):([0-9]{2}):([0-9]{2})(?:[.,]([0-9]+))?$");
        private static readonly Regex DateOnly = new Regex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}$");

        public static TraceFormat BuiltIn { get; } = new BuiltInFormat();

        private sealed class BuiltInFormat : TraceFormat
        {
            public override string Name => BuiltInName;
            public override bool BuiltIn => true;

            public override TraceRecord Parse(string text, int line)
            {
                var m = BuiltInLine.Match(text);
                if (!m.Success) return null;
                var t = IsoTime.Match(text.Substring(0, m.Index));
                return new TraceRecord
                {
                    Kind = TraceKinds.Parse(m.Groups[2].Value).Value,
                    Time = t.Success ? ParseTime(t.Value) : null,
                    Machine = m.Groups[1].Value,
                    Id = m.Groups[3].Value,
                    Name = m.Groups[4].Success ? m.Groups[4].Value : m.Groups[3].Value,
                    Line = line,
                    Text = text,
                };
            }
        }

        /// <summary>
        /// Milliseconds of a time as written in a log: an ISO 8601 date and time (since 1970), a time of day (since
        /// midnight), or a number (milliseconds, or seconds when below 10^11, as Unix timestamps in seconds are). Null otherwise.
        /// </summary>
        public static double? ParseTime(string text)
        {
            var s = (text ?? "").Trim();
            if (s.Length == 0) return null;
            if (Number.IsMatch(s))
            {
                var n = double.Parse(s, CultureInfo.InvariantCulture);
                return n < 1e11 ? n * 1000 : n;
            }
            var day = TimeOfDay.Match(s);
            if (day.Success)
            {
                var ms = day.Groups[4].Success ? double.Parse("0." + day.Groups[4].Value, CultureInfo.InvariantCulture) * 1000 : 0;
                return ((int.Parse(day.Groups[1].Value) * 60 + int.Parse(day.Groups[2].Value)) * 60 + int.Parse(day.Groups[3].Value)) * 1000 + ms;
            }
            var iso = s.Replace(' ', 'T').Replace(',', '.');
            // Like JavaScript's Date.parse: a date alone is UTC, a date and time without an offset local time.
            var styles = DateOnly.IsMatch(iso) ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal;
            if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, styles, out var dto)) return null;
            return Math.Floor((dto.UtcTicks - Epoch) / (double)TimeSpan.TicksPerMillisecond);
        }

        private static readonly long Epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

        private sealed class Rule
        {
            public TraceKind? Kind;
            public Regex Re;
        }

        private sealed class CompiledFormat : TraceFormat
        {
            public string FormatName;
            public List<Rule> Rules;
            public Regex TimeRe;
            public string FixedMachine;

            public override string Name => FormatName;
            public override bool BuiltIn => false;

            public override TraceRecord Parse(string text, int line)
            {
                foreach (var rule in Rules)
                {
                    var m = rule.Re.Match(text);
                    if (!m.Success) continue;
                    string G(string name)
                    {
                        var g = m.Groups[name];
                        return g.Success ? g.Value : null;
                    }
                    var kind = rule.Kind ?? TraceKinds.Parse(G("kind")?.Trim().ToLowerInvariant());
                    if (kind == null) continue;
                    var timeText = G("time");
                    if (timeText == null && TimeRe != null)
                    {
                        var tm = TimeRe.Match(text);
                        if (tm.Success && tm.Groups["time"].Success) timeText = tm.Groups["time"].Value;
                    }
                    var id = G("id")?.Trim() ?? "";
                    var nm = G("name")?.Trim() ?? "";
                    var source = G("source")?.Trim();
                    var target = G("target")?.Trim();
                    if (id.Length == 0 && nm.Length == 0 && !(kind == TraceKind.Transition && !string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(target))) continue;
                    var machine = G("machine")?.Trim();
                    return new TraceRecord
                    {
                        Kind = kind.Value,
                        Time = timeText != null ? ParseTime(timeText) : null,
                        Machine = string.IsNullOrEmpty(machine) ? FixedMachine : machine,
                        Id = id,
                        Name = nm.Length > 0 ? nm : id.Length > 0 ? id : $"{source} -> {target}",
                        Source = source,
                        Target = target,
                        Line = line,
                        Text = text,
                    };
                }
                return null;
            }
        }

        /// <summary>A format of the setting (a JSON object, parsed), compiled. Throws a TraceFormatException for a bad specification.</summary>
        public static TraceFormat Compile(object spec)
        {
            var o = spec as Dictionary<string, object>;
            var name = o != null && o.TryGetValue("name", out var n) && n is string ns && ns.Trim().Length > 0 ? ns.Trim() : "Unnamed format";
            Regex Regex(object source, string what)
            {
                if (!(source is string src) || src.Length == 0) throw new TraceFormatException($"Trace format '{name}': {what} needs a regular expression.");
                try
                {
                    return new Regex(src, RegexOptions.CultureInvariant);
                }
                catch (ArgumentException e)
                {
                    throw new TraceFormatException($"Trace format '{name}': {what} is not a valid regular expression: {e.Message}");
                }
            }
            if (o == null || !o.TryGetValue("rules", out var rs) || !(rs is List<object> list) || list.Count == 0) throw new TraceFormatException($"Trace format '{name}' has no rules.");
            var rules = new List<Rule>();
            for (var i = 0; i < list.Count; i++)
            {
                var r = list[i] as Dictionary<string, object>;
                object k = null;
                r?.TryGetValue("kind", out k);
                var kindText = k == null || (k is string ks && ks.Length == 0) ? null : Convert.ToString(k, CultureInfo.InvariantCulture);
                var kind = kindText != null ? TraceKinds.Parse(kindText) : null;
                if (kindText != null && kind == null)
                    throw new TraceFormatException($"Trace format '{name}': rule {i + 1} has kind '{kindText}'; use event, enter, exit or transition.");
                object match = null;
                r?.TryGetValue("match", out match);
                var re = Regex(match, $"rule {i + 1}");
                if (kind == null && !re.ToString().Contains("?<kind>")) throw new TraceFormatException($"Trace format '{name}': rule {i + 1} needs a kind, or a 'kind' group.");
                rules.Add(new Rule { Kind = kind, Re = re });
            }
            var timeRe = o.TryGetValue("time", out var t) && t is string ts && ts.Length > 0 ? Regex(ts, "time") : null;
            var fixedMachine = o.TryGetValue("machine", out var fm) && fm is string fms ? fms.Trim() : "";
            return new CompiledFormat { FormatName = name, Rules = rules, TimeRe = timeRe, FixedMachine = fixedMachine };
        }

        /// <summary>The built-in format, then those of the setting (a JSON array); bad ones are left out and reported in <paramref name="errors"/>.</summary>
        public static List<TraceFormat> Load(string settingJson, List<string> errors = null)
        {
            var output = new List<TraceFormat> { BuiltIn };
            if (string.IsNullOrWhiteSpace(settingJson)) return output;
            object specs;
            try
            {
                specs = ClipboardJson.ParseJson(settingJson);
            }
            catch (FormatException e)
            {
                errors?.Add($"The trace formats setting is not valid JSON: {e.Message}");
                return output;
            }
            foreach (var spec in specs as List<object> ?? new List<object>())
            {
                try
                {
                    output.Add(Compile(spec));
                }
                catch (TraceFormatException e)
                {
                    errors?.Add(e.Message);
                }
            }
            return output;
        }

        /// <summary>The format recognizing the most of <paramref name="lines"/> (the first one on a tie, so the built-in format when none does).</summary>
        public static TraceFormat Detect(IReadOnlyList<TraceFormat> formats, IReadOnlyList<string> lines)
        {
            var best = formats[0];
            var bestCount = -1;
            foreach (var f in formats)
            {
                var n = 0;
                for (var i = 0; i < lines.Count; i++)
                {
                    if (f.Parse(lines[i], i) != null) n++;
                }
                if (n > bestCount)
                {
                    best = f;
                    bestCount = n;
                }
            }
            return best;
        }
    }

    /// <summary>A machine instance the records are about: a machine, or a submachine reached through submachine states (<c>Order.Payment</c>).</summary>
    public sealed class TraceInstance
    {
        /// <summary>Its path, e.g. <c>Order</c> or <c>Order.Payment</c>, or another key unique in the timeline.</summary>
        public string Key { get; set; } = "";
        /// <summary>Its name for display, when not its key.</summary>
        public string Label { get; set; }
        /// <summary>Full path of its .fsm file.</summary>
        public string File { get; set; } = "";
        public FsmModel Model { get; set; }
        public ModelIndex Index { get; set; }

        /// <summary>What each record designates in its model, computed once.</summary>
        internal readonly Dictionary<TraceRecord, TraceReplay.Resolved> Resolved = new Dictionary<TraceRecord, TraceReplay.Resolved>();

        public TraceInstance With(string key, string label) => new TraceInstance { Key = key, Label = label, File = File, Model = Model, Index = Index };
    }

    public sealed class TimelineEntry
    {
        public TraceRecord Record { get; set; }
        /// <summary>Index of the log it comes from.</summary>
        public int Trace { get; set; }
        /// <summary>Its effective time (its own, or the previous record's).</summary>
        public double Time { get; set; }
        /// <summary>Null when the record's machine could not be resolved.</summary>
        public TraceInstance Instance { get; set; }
    }

    /// <summary>What a diagram highlights for one instance.</summary>
    public sealed class TraceHighlight
    {
        /// <summary>Active states (and final states).</summary>
        public List<string> Active { get; set; } = new List<string>();
        /// <summary>Transitions taken by the current step: since the instance's last event, or the last one when it has no events.</summary>
        public List<string> Taken { get; set; } = new List<string>();
        /// <summary>Pseudostates on the way of the taken transitions (choices, junctions, forks, ...).</summary>
        public List<string> Via { get; set; } = new List<string>();
        /// <summary>Elements of the current record, when it is about this instance.</summary>
        public List<string> Current { get; set; } = new List<string>();
        /// <summary>The event of the current step, when there is one.</summary>
        public string Event { get; set; }
        /// <summary>Set for a diagram: the instance's label, and its color (an index in a palette, or -1 when its machine has only one instance).</summary>
        public string Label { get; set; } = "";
        public int Color { get; set; } = -1;
    }

    /// <summary>Replays records on the machines they are about: active states, transitions taken by the current step, current element.</summary>
    public static class TraceReplay
    {
        internal sealed class Resolved
        {
            public Vertex State;
            public List<Transition> Transitions = new List<Transition>();
        }

        private static bool IsState(Vertex v) => v != null && (v.Type == VertexType.State || v.Type == VertexType.Final);

        /// <summary>Vertices a compound transition goes through: pseudostates and connection point references.</summary>
        private static bool IsWaypoint(Vertex v) => v.Type.IsPseudostate() || v.Type == VertexType.ConnectionPointRef;

        private static readonly Regex NonIdent = new Regex("[^A-Za-z0-9_]+");
        private static readonly Regex EdgeUnderscores = new Regex("^_+|_+$");
        private static readonly Regex PathSeparators = new Regex(@"::|\.|/");
        private static readonly Regex Way = new Regex(@"^\s*(.+?)\s*->\s*(.+?)(?:\s+\(.*\))?\s*$");

        /// <summary>Identifier form of a name, as the code generator makes it (letters, digits, <c>_</c>).</summary>
        internal static string Ident(string text)
        {
            var s = EdgeUnderscores.Replace(NonIdent.Replace(text.Trim(), "_"), "");
            return s.Length > 0 && char.IsDigit(s[0]) ? "_" + s : s;
        }

        private static bool SameName(Vertex v, string name) => v.Name == name || (v.Name.Length > 0 && Ident(v.Name) == name);

        /// <summary>The state designated by <paramref name="reference"/>: an xmi:id, a name, or a path of names (<c>On::Playing</c>, <c>On.Playing</c>, <c>On/Playing</c>).</summary>
        public static Vertex FindState(ModelIndex ix, string reference)
        {
            if (string.IsNullOrEmpty(reference)) return null;
            var byId = ix.Vertex(reference);
            if (IsState(byId)) return byId;
            var states = ix.Model.Vertices.Where(IsState).ToList();
            var exact = states.FirstOrDefault(v => SameName(v, reference));
            if (exact != null) return exact;
            var path = PathSeparators.Split(reference).Where(p => p.Length > 0).ToList();
            if (path.Count < 2) return null;
            var last = path[path.Count - 1];
            return states.FirstOrDefault(v =>
            {
                if (!SameName(v, last)) return false;
                // The other names, innermost first, are enclosing states in that order (not necessarily direct parents).
                var ancestors = ix.Ancestors(v);
                var k = 0;
                for (var i = path.Count - 2; i >= 0; i--)
                {
                    while (k < ancestors.Count && !SameName(ancestors[k], path[i])) k++;
                    if (k++ >= ancestors.Count) return false;
                }
                return true;
            });
        }

        /// <summary>
        /// The transitions designated by a transition record: the one with its xmi:id, else the way from its source to its
        /// target (given by Source/Target, or a name like <c>A -> B (e)</c>) through pseudostates, shortest first.
        /// </summary>
        public static List<Transition> FindTransitions(ModelIndex ix, string id, string name, string source = null, string target = null)
        {
            var byId = ix.Model.Transitions.FirstOrDefault(t => t.Id == id);
            if (byId != null) return new List<Transition> { byId };
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
            {
                var m = Way.Match(string.IsNullOrEmpty(name) ? id ?? "" : name);
                if (!m.Success) return new List<Transition>();
                source = m.Groups[1].Value;
                target = m.Groups[2].Value;
            }
            var from = FindVertex(ix, source);
            var to = FindVertex(ix, target);
            if (from == null || to == null) return new List<Transition>();
            // Breadth-first through pseudostates, so the shortest way wins.
            var queue = new Queue<(Vertex At, List<Transition> Way)>();
            queue.Enqueue((from, new List<Transition>()));
            var seen = new HashSet<string> { from.Id };
            while (queue.Count > 0)
            {
                var (at, way) = queue.Dequeue();
                foreach (var t in ix.Outgoing(at.Id))
                {
                    var next = ix.Vertex(t.Target);
                    if (next == null) continue;
                    if (next.Id == to.Id) return new List<Transition>(way) { t };
                    if (IsWaypoint(next) && seen.Add(next.Id)) queue.Enqueue((next, new List<Transition>(way) { t }));
                }
            }
            return new List<Transition>();
        }

        /// <summary>A state by FindState(), or any vertex by xmi:id or name (transitions may start or end at pseudostates).</summary>
        private static Vertex FindVertex(ModelIndex ix, string reference) =>
            FindState(ix, reference) ?? ix.Vertex(reference) ?? ix.Model.Vertices.FirstOrDefault(v => SameName(v, reference));

        internal static Resolved Resolve(TimelineEntry entry)
        {
            var inst = entry.Instance;
            if (!inst.Resolved.TryGetValue(entry.Record, out var r)) inst.Resolved[entry.Record] = r = ResolveIn(inst.Index, entry.Record);
            return r;
        }

        private static Resolved ResolveIn(ModelIndex ix, TraceRecord rec)
        {
            switch (rec.Kind)
            {
                case TraceKind.Enter:
                case TraceKind.Exit:
                    return new Resolved { State = FindState(ix, rec.Id) ?? FindState(ix, rec.Name) };
                case TraceKind.Transition:
                    return new Resolved { Transitions = FindTransitions(ix, rec.Id, rec.Name, rec.Source, rec.Target) };
                default:
                    return new Resolved();
            }
        }

        /// <summary>Whether the record designates something in its instance's model (events always do).</summary>
        public static bool IsResolved(TimelineEntry entry)
        {
            if (entry.Instance == null) return false;
            var r = Resolve(entry);
            return entry.Record.Kind == TraceKind.Event || r.State != null || r.Transitions.Count > 0;
        }

        private sealed class Run
        {
            public TraceInstance Inst;
            /// <summary>Active states, in the order they became active.</summary>
            public readonly List<string> Active = new List<string>();
            public List<string> Taken = new List<string>();
            public string Event;
        }

        /// <summary>
        /// The highlights of every instance (in the order they first appear) after the entries up to <paramref name="cursor"/>
        /// (included; -1 for none). <paramref name="kinds"/> tells, for each instance key, which kinds of records the whole
        /// timeline has for it: without events, a step is a single transition; without <c>enter</c> records, the active
        /// states follow the transitions' targets.
        /// </summary>
        public static List<(TraceInstance Instance, TraceHighlight Highlight)> Replay(IReadOnlyList<TimelineEntry> entries, int cursor, IReadOnlyDictionary<string, HashSet<TraceKind>> kinds)
        {
            var runs = new Dictionary<string, Run>();
            var order = new List<Run>();
            var last = Math.Min(cursor, entries.Count - 1);
            for (var i = 0; i <= last; i++)
            {
                var e = entries[i];
                if (e.Instance == null) continue;
                if (!runs.TryGetValue(e.Instance.Key, out var run))
                {
                    // A submachine's first records belong to the step of the machine containing it.
                    var parent = order.Where(o => e.Instance.Key.StartsWith(o.Inst.Key + ".", StringComparison.Ordinal)).OrderByDescending(o => o.Inst.Key.Length).FirstOrDefault();
                    runs[e.Instance.Key] = run = new Run { Inst = e.Instance, Event = parent?.Event };
                    order.Add(run);
                }
                kinds.TryGetValue(e.Instance.Key, out var has);
                var r = Resolve(e);
                switch (e.Record.Kind)
                {
                    case TraceKind.Event:
                        // A step starts, in the submachines of the instance too (Order.Payment for Order).
                        foreach (var o in order)
                        {
                            if (o != run && !o.Inst.Key.StartsWith(run.Inst.Key + ".", StringComparison.Ordinal)) continue;
                            o.Taken = new List<string>();
                            o.Event = e.Record.Name;
                        }
                        break;
                    case TraceKind.Enter:
                        if (r.State != null) Enter(run, r.State);
                        break;
                    case TraceKind.Exit:
                        if (r.State != null) Leave(run, r.State);
                        break;
                    case TraceKind.Transition:
                    {
                        var ids = r.Transitions.Select(t => t.Id).ToList();
                        if (has != null && has.Contains(TraceKind.Event)) run.Taken.AddRange(ids);
                        else run.Taken = ids;
                        if (!(has != null && has.Contains(TraceKind.Enter)) && r.Transitions.Count > 0)
                        {
                            var target = run.Inst.Index.Vertex(r.Transitions[r.Transitions.Count - 1].Target);
                            if (IsState(target)) Enter(run, target);
                        }
                        break;
                    }
                }
            }
            var output = new List<(TraceInstance, TraceHighlight)>();
            var cur = last >= 0 ? entries[last] : null;
            foreach (var run in order)
            {
                var ix = run.Inst.Index;
                var taken = run.Taken.Distinct().ToList();
                var via = new List<string>();
                foreach (var id in taken)
                {
                    var t = ix.Transition(id);
                    if (t == null) continue;
                    foreach (var end in new[] { t.Source, t.Target })
                    {
                        var v = ix.Vertex(end);
                        if (v != null && IsWaypoint(v) && !via.Contains(v.Id)) via.Add(v.Id);
                    }
                }
                var current = new List<string>();
                if (cur?.Instance != null && cur.Instance.Key == run.Inst.Key)
                {
                    var r = Resolve(cur);
                    if (r.State != null) current.Add(r.State.Id);
                    current.AddRange(r.Transitions.Select(t => t.Id));
                }
                output.Add((run.Inst, new TraceHighlight { Active = run.Active.ToList(), Taken = taken, Via = via, Current = current, Event = run.Event }));
            }
            return output;
        }

        /// <summary>Enters <paramref name="v"/>: its enclosing states become active, and whatever else was active in its region is left.</summary>
        private static void Enter(Run run, Vertex v)
        {
            var ix = run.Inst.Index;
            var chain = ix.Ancestors(v);
            chain.Reverse();
            chain.Add(v);
            foreach (var s in chain)
            {
                if (run.Active.Contains(s.Id)) continue;
                foreach (var id in run.Active.ToList())
                {
                    var other = ix.Vertex(id);
                    if (other != null && other.Parent == s.Parent && other.Id != s.Id) Leave(run, other);
                }
                run.Active.Add(s.Id);
            }
        }

        /// <summary>Leaves <paramref name="v"/> and everything active inside it.</summary>
        private static void Leave(Run run, Vertex v)
        {
            var ix = run.Inst.Index;
            run.Active.Remove(v.Id);
            foreach (var id in run.Active.ToList())
            {
                var inner = ix.Vertex(id);
                if (inner != null && ix.IsInside(inner, v.Id)) run.Active.Remove(id);
            }
        }
    }

    /// <summary>
    /// What a diagram shows of a trace: the highlights of the instances of its machine, merged by element id with the
    /// colors of the instances where it is active, taken (transitions and the pseudostates on their way) or current.
    /// </summary>
    public sealed class TraceView
    {
        /// <summary>Instance colors of the palette (see the editors' themes).</summary>
        public const int Colors = 6;

        public List<TraceHighlight> Instances { get; } = new List<TraceHighlight>();
        public Dictionary<string, List<int>> Active { get; } = new Dictionary<string, List<int>>();
        public Dictionary<string, List<int>> Taken { get; } = new Dictionary<string, List<int>>();
        public HashSet<string> Current { get; } = new HashSet<string>();
        /// <summary>The event of the instance of the current record, else of the first instance that has one; "" without.</summary>
        public string Event { get; private set; } = "";

        public TraceView(IEnumerable<TraceHighlight> instances)
        {
            Instances.AddRange(instances.OrderBy(i => i.Color));
            void Add(Dictionary<string, List<int>> map, string id, int color)
            {
                if (!map.TryGetValue(id, out var list)) map[id] = list = new List<int>();
                list.Add(color);
            }
            foreach (var inst in Instances)
            {
                foreach (var id in inst.Active) Add(Active, id, inst.Color);
                foreach (var id in inst.Taken.Concat(inst.Via)) Add(Taken, id, inst.Color);
                foreach (var id in inst.Current) Current.Add(id);
            }
            var at = Instances.FirstOrDefault(i => i.Current.Count > 0) ?? Instances.FirstOrDefault(i => !string.IsNullOrEmpty(i.Event));
            Event = at?.Event ?? "";
        }

        /// <summary>The color an element is highlighted with: that of the first instance where it is active or taken (-1: the only instance), or null.</summary>
        public int? ColorOf(string id) =>
            Active.TryGetValue(id, out var a) ? a[0] : Taken.TryGetValue(id, out var t) ? t[0] : (int?)null;

        /// <summary>The status bar text: <c>▶ Trace</c>, and the event of the current step.</summary>
        public string StatusText => "▶ Trace" + (Event.Length > 0 ? $" · event {Event}" : "");
    }

    /// <summary>A machine file the session can use, parsed.</summary>
    public sealed class TraceMachine
    {
        /// <summary>Full path of the .fsm file.</summary>
        public string File { get; set; } = "";
        public FsmModel Model { get; set; }
        public ModelIndex Index { get; set; }

        public static TraceMachine Of(string file, FsmModel model) => new TraceMachine { File = file, Model = model, Index = new ModelIndex(model) };
    }

    /// <summary>A log opened in the Trace Player.</summary>
    public sealed class TraceLog
    {
        internal TraceLog(string path, TraceFormat format)
        {
            Path = path;
            Format = format;
        }

        /// <summary>Full path of the file.</summary>
        public string Path { get; }
        public string Name => System.IO.Path.GetFileName(Path);
        public TraceFormat Format { get; internal set; }
        /// <summary>Whether the format was picked: detected from the first lines, or chosen.</summary>
        public bool FormatPicked { get; internal set; }
        public List<TraceRecord> Records { get; private set; } = new List<TraceRecord>();
        /// <summary>Effective time of each record (its own, the previous record's, or the time it was read).</summary>
        public List<double> Times { get; private set; } = new List<double>();
        /// <summary>Bytes of the file already read, up to the last line break.</summary>
        public long Offset { get; internal set; }
        public int Lines { get; internal set; }
        /// <summary>Whether the file was read once: lines without a time read after that get the time they were read.</summary>
        public bool Followed { get; internal set; }
        public string Error { get; internal set; }
        /// <summary>The instances of the machine paths its lines name (each log is a run of its own: its instances are its own).</summary>
        internal Dictionary<string, TraceInstance> Instances { get; private set; } = new Dictionary<string, TraceInstance>();
        /// <summary>Lines naming no machine: the file of the machine chosen for them ("" before), whether it was asked, and the instance.</summary>
        public string MachineFile { get; internal set; } = "";
        public bool MachineAsked { get; internal set; }
        internal TraceInstance Instance { get; set; }

        /// <summary>Lines naming no machine: the name of the one chosen for them, "" when none yet.</summary>
        public string MachineName => Instance?.Model.Name ?? "";

        /// <summary>Whether some of its lines name no machine (so the window offers to choose it).</summary>
        public bool NeedsMachine => Records.Any(r => r.Machine.Length == 0);

        /// <summary>Forgets what was read.</summary>
        internal void Restart()
        {
            Records = new List<TraceRecord>();
            Times = new List<double>();
            Offset = 0;
            Lines = 0;
            Instance = null;
            Instances = new Dictionary<string, TraceInstance>();
        }
    }

    /// <summary>A row of the Trace Player's list of records.</summary>
    public sealed class TraceRow
    {
        public int Index { get; set; }
        public string Time { get; set; } = "";
        public int Trace { get; set; }
        public string Machine { get; set; } = "";
        public int Color { get; set; } = -1;
        public TraceKind Kind { get; set; }
        public string Name { get; set; } = "";
        public bool Resolved { get; set; }
    }

    /// <summary>
    /// The Trace Player's state, without the IDE: the logs, each read with a format and followed as it grows (the IDE
    /// calls <see cref="ReadNew"/> when a file changes); the timeline, their records merged by time (lines without a time
    /// take the previous line's); the cursor, which Live keeps on the last record; and the machine instance of every
    /// record, <c>Order</c> or <c>Order.Payment</c>, resolved to the .fsm file of the machine among those the IDE gives
    /// (<see cref="SetMachines"/>). The lines of a log that name no machine are about one instance of a machine the user
    /// chooses (<see cref="ShouldAsk"/>, <see cref="ChooseMachine"/>).
    /// </summary>
    public sealed class TraceSession
    {
        /// <summary>Records listed around the cursor.</summary>
        public const int Window = 150;

        private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
        private List<TraceFormat> _formats = new List<TraceFormat> { TraceFormats.BuiltIn };
        private List<string> _formatErrors = new List<string>();
        private Dictionary<string, HashSet<TraceKind>> _kinds = new Dictionary<string, HashSet<TraceKind>>();
        /// <summary>Color of each instance key: its rank among the instances of its file, or -1 when it is the only one.</summary>
        private Dictionary<string, int> _colors = new Dictionary<string, int>();
        /// <summary>The machines the IDE gave (open ones first), files read for submachines, and the instances of machine paths.</summary>
        private List<TraceMachine> _machineList = new List<TraceMachine>();
        private Dictionary<string, TraceMachine> _machines = new Dictionary<string, TraceMachine>(Paths);
        private Func<string, TraceMachine> _read = _ => null;
        private readonly Dictionary<string, TraceInstance> _instances = new Dictionary<string, TraceInstance>();
        /// <summary>Why an instance could not be resolved, by machine path.</summary>
        private readonly Dictionary<string, string> _instanceProblems = new Dictionary<string, string>();
        private List<string> _problems = new List<string>();

        public List<TraceLog> Traces { get; } = new List<TraceLog>();
        public List<TimelineEntry> Entries { get; private set; } = new List<TimelineEntry>();
        public int Cursor { get; private set; } = -1;
        /// <summary>Following the end of the logs as they grow.</summary>
        public bool Live { get; private set; } = true;
        public bool Playing { get; private set; }
        public double Speed { get; set; } = 1;

        public IReadOnlyList<TraceFormat> Formats => _formats;
        /// <summary>The setting's errors, then the problems of the instances and records.</summary>
        public List<string> Problems => _formatErrors.Concat(_problems).ToList();

        /// <summary>Reads the formats of the setting (a JSON array).</summary>
        public void SetFormats(string settingJson)
        {
            var errors = new List<string>();
            _formats = TraceFormats.Load(settingJson, errors);
            _formatErrors = errors;
            foreach (var t in Traces)
            {
                // A format that is gone: back to the built-in one.
                t.Format = _formats.FirstOrDefault(f => f.Name == t.Format.Name) ?? _formats[0];
            }
        }

        /// <summary>Gives the machines records can be about, the open ones first; <paramref name="read"/> reads other machine files (submachines).</summary>
        public void SetMachines(IEnumerable<TraceMachine> machines, Func<string, TraceMachine> read)
        {
            _machineList = machines.ToList();
            _machines = new Dictionary<string, TraceMachine>(Paths);
            foreach (var m in _machineList) _machines[m.File] = m;
            _read = read ?? (_ => null);
            _instances.Clear();
            _instanceProblems.Clear();
            foreach (var t in Traces) t.Instance = null;
            foreach (var t in Traces) t.Instances.Clear();
        }

        public IReadOnlyList<TraceMachine> Machines => _machineList;

        // ---------------------------------------------------------------- logs

        /// <summary>Adds a log (read it with <see cref="ReadNew"/>); null when it is open already.</summary>
        public TraceLog Open(string path)
        {
            path = System.IO.Path.GetFullPath(path);
            if (Traces.Any(t => Paths.Equals(t.Path, path))) return null;
            var trace = new TraceLog(path, _formats[0]);
            Traces.Add(trace);
            Live = true;
            return trace;
        }

        public void Close(TraceLog trace)
        {
            if (!Traces.Remove(trace)) return;
            if (Traces.Count == 0)
            {
                Playing = false;
                Cursor = -1;
                Live = true;
            }
            Rebuild();
        }

        /// <summary>Reads <paramref name="trace"/> again from the start with another format (then call <see cref="ReadNew"/>).</summary>
        public bool SetFormat(TraceLog trace, string name)
        {
            var format = _formats.FirstOrDefault(f => f.Name == name);
            if (format == null || !Traces.Contains(trace)) return false;
            trace.Format = format;
            trace.FormatPicked = true;
            trace.Restart();
            return true;
        }

        /// <summary>A log that doesn't exist (any more).</summary>
        public void Missing(TraceLog trace) => trace.Error = $"{trace.Name} does not exist.";

        /// <summary>
        /// Reads what was added to <paramref name="trace"/> since the last read (complete lines only; all of it again when
        /// it was truncated or replaced), then rebuilds the timeline. False when nothing changed.
        /// </summary>
        public bool ReadNew(TraceLog trace, double? now = null)
        {
            if (!Traces.Contains(trace)) return false;
            byte[] bytes;
            try
            {
                bytes = ReadFrom(trace.Path, trace.Offset, out var size);
                if (size < trace.Offset)
                {
                    // Truncated or replaced (log rotation): read it again.
                    trace.Restart();
                    bytes = ReadFrom(trace.Path, 0, out _);
                }
            }
            catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException)
            {
                Missing(trace);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                trace.Error = $"Cannot read {trace.Name}: {e.Message}";
                return true;
            }
            var hadError = trace.Error != null;
            trace.Error = null;
            // Complete lines only: a line break byte never occurs inside a UTF-8 character.
            var end = Array.LastIndexOf(bytes, (byte)10) + 1;
            var text = end > 0 ? Encoding.UTF8.GetString(bytes, 0, end) : "";
            if (trace.Offset == 0 && text.StartsWith("﻿", StringComparison.Ordinal)) text = text.Substring(1);
            var lines = end > 0 ? text.Split('\n') : new string[0];
            trace.Offset += end;
            var count = Math.Max(0, lines.Length - 1);
            if (count > 0 && !trace.FormatPicked)
            {
                trace.Format = TraceFormats.Detect(_formats, lines.Take(Math.Min(count, 1000)).ToList());
                trace.FormatPicked = true;
            }
            var added = false;
            var stamp = now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (var i = 0; i < count; i++)
            {
                var raw = lines[i];
                if (raw.EndsWith("\r", StringComparison.Ordinal)) raw = raw.Substring(0, raw.Length - 1);
                var rec = trace.Format.Parse(raw, trace.Lines++);
                if (rec == null) continue;
                double? prev = trace.Times.Count > 0 ? trace.Times[trace.Times.Count - 1] : (double?)null;
                // Without a time: the previous record's, else (lines appended while followed) the time it was read.
                trace.Times.Add(rec.Time ?? prev ?? (trace.Followed ? stamp : 0));
                trace.Records.Add(rec);
                added = true;
            }
            trace.Followed = true;
            if (added) Rebuild();
            return added || hadError;
        }

        private static byte[] ReadFrom(string path, long offset, out long size)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                size = stream.Length;
                if (size <= offset) return new byte[0];
                stream.Seek(offset, SeekOrigin.Begin);
                var bytes = new byte[size - offset];
                var read = 0;
                while (read < bytes.Length)
                {
                    var n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < bytes.Length) Array.Resize(ref bytes, read);
                return bytes;
            }
        }

        /// <summary>
        /// Whether to ask which machine the lines of <paramref name="trace"/> naming none are about: true once, when such
        /// lines were read and none was chosen.
        /// </summary>
        public bool ShouldAsk(TraceLog trace)
        {
            if (trace.MachineAsked || trace.MachineFile.Length > 0 || !trace.NeedsMachine) return false;
            trace.MachineAsked = true;
            return true;
        }

        /// <summary>The machine the lines of <paramref name="trace"/> naming none are about.</summary>
        public void ChooseMachine(TraceLog trace, string file)
        {
            trace.MachineFile = System.IO.Path.GetFullPath(file);
            trace.Instance = null;
            Rebuild();
        }

        // ---------------------------------------------------------------- machines

        private TraceMachine Machine(string file)
        {
            if (_machines.TryGetValue(file, out var m)) return m;
            m = _read(file);
            if (m != null) _machines[file] = m;
            return m;
        }

        private static string PlainIdent(string s) => Regex.Replace(Regex.Replace(s.Trim(), "[^A-Za-z0-9_]+", "_"), "^_+|_+$", "");

        /// <summary>Resolves the machine of every record not resolved yet.</summary>
        private void ResolveInstances()
        {
            foreach (var trace in Traces)
            {
                if (trace.MachineFile.Length > 0 && trace.Instance == null)
                {
                    var m = Machine(trace.MachineFile);
                    if (m != null) trace.Instance = new TraceInstance { Key = trace.Path + "#", Label = $"{m.Model.Name} ({trace.Name})", File = m.File, Model = m.Model, Index = m.Index };
                }
                foreach (var rec in trace.Records)
                {
                    var path = rec.Machine;
                    if (path.Length == 0 || trace.Instances.ContainsKey(path)) continue;
                    if (!_instances.TryGetValue(path, out var found))
                    {
                        string problem = null;
                        _instances[path] = found = ResolveInstance(path, text => problem = problem ?? text);
                        if (problem != null) _instanceProblems[path] = problem;
                    }
                    trace.Instances[path] = found?.With($"{trace.Path}|{path}", path);
                }
            }
        }

        private TraceInstance InstanceOf(TraceLog trace, TraceRecord rec) =>
            rec.Machine.Length > 0 ? (trace.Instances.TryGetValue(rec.Machine, out var i) ? i : null) : trace.Instance;

        /// <summary>The instance <paramref name="path"/> designates (<c>Order</c>, <c>Order.Payment</c>), or null when no machine fits.</summary>
        private TraceInstance ResolveInstance(string path, Action<string> problem)
        {
            bool Named(TraceMachine m, string name) => m.Model.Name == name || PlainIdent(m.Model.Name) == name;
            var segments = path.Split('.');
            var machine = _machineList.FirstOrDefault(m => Named(m, segments[0]));
            if (machine == null)
            {
                problem($"No state machine named '{segments[0]}' in the workspace ({path}).");
                return null;
            }
            foreach (var seg in segments.Skip(1))
            {
                var state = machine.Model.Vertices.FirstOrDefault(v => v.Type == VertexType.State && v.Submachine.Length > 0 && (v.Id == seg || v.Name == seg || PlainIdent(v.Name) == seg));
                if (state == null)
                {
                    problem($"{machine.Model.Name} has no submachine state '{seg}' ({path}).");
                    return null;
                }
                var hash = state.Submachine.IndexOf('#');
                var file = hash >= 0 ? state.Submachine.Substring(0, hash) : state.Submachine;
                string subFile = null, id = "";
                try
                {
                    (subFile, id) = Hrefs.Resolve(machine.File, state.Submachine);
                }
                catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
                {
                    // Not a file path: a submachine that can't be read.
                }
                if (id.Length == 0) id = "sm";
                var sub = subFile != null ? Machine(subFile) : null;
                if (sub == null || (string.IsNullOrEmpty(sub.Model.Id) ? "sm" : sub.Model.Id) != id)
                {
                    problem($"The submachine of {machine.Model.Name}.{(state.Name.Length > 0 ? state.Name : seg)} ({file}) cannot be read ({path}).");
                    return null;
                }
                machine = sub;
            }
            return new TraceInstance { Key = path, File = machine.File, Model = machine.Model, Index = machine.Index };
        }

        // ---------------------------------------------------------------- timeline

        /// <summary>Merges the logs into the timeline, keeping the cursor on the same record (or on the last one when live).</summary>
        public void Rebuild()
        {
            ResolveInstances();
            var at = Cursor >= 0 && Cursor < Entries.Count ? Entries[Cursor] : null;
            var list = new List<TimelineEntry>();
            for (var t = 0; t < Traces.Count; t++)
            {
                var trace = Traces[t];
                for (var i = 0; i < trace.Records.Count; i++)
                {
                    var record = trace.Records[i];
                    list.Add(new TimelineEntry { Record = record, Trace = t, Time = trace.Times[i], Instance = InstanceOf(trace, record) });
                }
            }
            list = list.OrderBy(e => e.Time).ThenBy(e => e.Trace).ThenBy(e => e.Record.Line).ToList();
            Entries = list;
            _kinds = new Dictionary<string, HashSet<TraceKind>>();
            // The instances of each file, in the order they first appear: their colors.
            var byFile = new Dictionary<string, List<string>>(Paths);
            foreach (var e in list)
            {
                if (e.Instance == null) continue;
                if (!_kinds.TryGetValue(e.Instance.Key, out var set))
                {
                    _kinds[e.Instance.Key] = set = new HashSet<TraceKind>();
                    if (!byFile.TryGetValue(e.Instance.File, out var keys)) byFile[e.Instance.File] = keys = new List<string>();
                    keys.Add(e.Instance.Key);
                }
                set.Add(e.Record.Kind);
            }
            // The same path in several logs: labeled with the log's name.
            var paths = new Dictionary<string, int>();
            foreach (var t in Traces)
            {
                foreach (var path in t.Instances.Keys) paths[path] = (paths.TryGetValue(path, out var n) ? n : 0) + 1;
            }
            foreach (var t in Traces)
            {
                foreach (var kv in t.Instances)
                {
                    if (kv.Value != null) kv.Value.Label = paths[kv.Key] > 1 ? $"{kv.Key} ({t.Name})" : kv.Key;
                }
            }
            _colors = new Dictionary<string, int>();
            foreach (var keys in byFile.Values)
            {
                for (var i = 0; i < keys.Count; i++) _colors[keys[i]] = keys.Count > 1 ? i : -1;
            }
            var problems = new List<string>();
            void Problem(string p)
            {
                if (!problems.Contains(p)) problems.Add(p);
            }
            foreach (var trace in Traces)
            {
                if (trace.Instance == null && trace.NeedsMachine)
                {
                    Problem(trace.MachineFile.Length > 0 ? $"The state machine chosen for {trace.Name} cannot be read." : $"Choose the state machine of the lines of {trace.Name} that name none.");
                }
                foreach (var key in trace.Records.Select(r => r.Machine).Distinct())
                {
                    if (key.Length > 0 && _instanceProblems.TryGetValue(key, out var p)) Problem(p);
                }
            }
            var unresolved = list.Count(e => !TraceReplay.IsResolved(e));
            if (unresolved > 0) Problem($"{unresolved} record{(unresolved == 1 ? "" : "s")} designate{(unresolved == 1 ? "s" : "")} nothing in their state machine (shown crossed out).");
            _problems = problems;
            if (Live) Cursor = list.Count - 1;
            else if (at != null) Cursor = Math.Max(-1, list.FindIndex(e => e.Record == at.Record));
            else Cursor = Math.Min(Cursor, list.Count - 1);
        }

        public void Seek(int index)
        {
            Live = false;
            Cursor = Math.Max(-1, Math.Min(index, Entries.Count - 1));
            if (Playing && Cursor >= Entries.Count - 1) Playing = false;
        }

        public void Step(int delta) => Seek(Cursor + (delta < 0 ? -1 : 1));

        public void First() => Seek(Entries.Count > 0 ? 0 : -1);

        /// <summary>Plays from the cursor (from the start at the end); the IDE then calls <see cref="NextDelay"/> and <see cref="Advance"/>.</summary>
        public void Play()
        {
            if (Entries.Count == 0) return;
            Live = false;
            if (Cursor >= Entries.Count - 1) Cursor = -1;
            Playing = true;
        }

        public void Pause() => Playing = false;

        /// <summary>While playing, the milliseconds until the next record, at the speed; null at the end, where playing turns to Live.</summary>
        public double? NextDelay()
        {
            if (!Playing || Cursor + 1 >= Entries.Count)
            {
                if (Playing) Live = true;
                Playing = false;
                return null;
            }
            var next = Entries[Cursor + 1];
            return Cursor >= 0 ? Math.Max(0, (next.Time - Entries[Cursor].Time) / Speed) : 0;
        }

        /// <summary>Moves the cursor to the next record while playing.</summary>
        public void Advance()
        {
            if (Playing && Cursor + 1 < Entries.Count) Cursor++;
        }

        public void GoLive()
        {
            Playing = false;
            Live = true;
            Cursor = Entries.Count - 1;
        }

        /// <summary>The highlights at the cursor of every machine file with instances, by full path.</summary>
        public Dictionary<string, TraceView> Views()
        {
            var byFile = new Dictionary<string, List<TraceHighlight>>(Paths);
            foreach (var (inst, highlight) in TraceReplay.Replay(Entries, Cursor, _kinds))
            {
                highlight.Label = inst.Label ?? inst.Key;
                highlight.Color = _colors.TryGetValue(inst.Key, out var c) ? c : -1;
                if (!byFile.TryGetValue(inst.File, out var list)) byFile[inst.File] = list = new List<TraceHighlight>();
                list.Add(highlight);
            }
            var views = new Dictionary<string, TraceView>(Paths);
            foreach (var kv in byFile) views[kv.Key] = new TraceView(kv.Value);
            return views;
        }

        /// <summary>The records around the cursor.</summary>
        public List<TraceRow> Rows(int window = Window)
        {
            var from = Math.Max(0, Cursor - window / 2);
            return Entries.Skip(from).Take(window).Select((e, k) => new TraceRow
            {
                Index = from + k,
                Time = FormatTime(e.Time),
                Trace = e.Trace,
                Machine = e.Instance != null ? e.Instance.Label ?? e.Instance.Key : e.Record.Machine,
                Color = e.Instance != null && _colors.TryGetValue(e.Instance.Key, out var c) ? c : -1,
                Kind = e.Record.Kind,
                Name = e.Record.Name,
                Resolved = TraceReplay.IsResolved(e),
            }).ToList();
        }

        /// <summary>The cursor's time for display, "" without.</summary>
        public string CursorTime => Cursor >= 0 && Cursor < Entries.Count ? FormatTime(Entries[Cursor].Time) : "";

        /// <summary>A record's time for display: the time of day with milliseconds, or "" for lines without a time.</summary>
        public static string FormatTime(double ms)
        {
            if (ms == 0) return "";
            double day;
            if (ms > 1e11)
            {
                var d = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(ms)).ToLocalTime();
                day = d.TimeOfDay.TotalMilliseconds;
            }
            else day = ms % 86400000;
            string Pad(double n, int w = 2) => ((long)Math.Floor(n)).ToString(CultureInfo.InvariantCulture).PadLeft(w, '0');
            return $"{Pad(day / 3600000)}:{Pad(day / 60000 % 60)}:{Pad(day / 1000 % 60)}.{Pad(day % 1000, 3)}";
        }
    }
}
