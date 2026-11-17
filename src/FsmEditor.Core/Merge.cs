using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FsmEditor.Core
{
    /// <summary>A text with conflict markers, split into its two sides.</summary>
    public sealed class ConflictSplit
    {
        /// <summary>The text with the current side of every conflict ("ours").</summary>
        public string Current { get; set; } = "";
        /// <summary>The text with the incoming side of every conflict ("theirs").</summary>
        public string Incoming { get; set; } = "";
        /// <summary>How many conflicts.</summary>
        public int Conflicts { get; set; }
    }

    public sealed class ConflictMarkerException : Exception
    {
        public ConflictMarkerException(string message) : base(message) { }
    }

    /// <summary>
    /// The diagram merge (three panes), without editor APIs: splitting a file with conflict markers into its two sides,
    /// and taking one side's version of an element into the result. A port of the VS Code edition's <c>src/merge.ts</c>.
    /// </summary>
    public static class Merge
    {
        private static readonly Regex Start = new Regex(@"^<{7}(?: |\r?$)", RegexOptions.Multiline);
        private static readonly Regex End = new Regex(@"^>{7}(?: |\r?$)", RegexOptions.Multiline);
        private static readonly Regex Marker = new Regex(@"^(<{7}|\|{7}|={7}|>{7})(?: |\r?\n|\r?$)");

        /// <summary>Whether a text holds Git's conflict markers.</summary>
        public static bool HasConflictMarkers(string text) => text != null && Start.IsMatch(text) && End.IsMatch(text);

        /// <summary>
        /// Splits a text with conflict markers into its two sides: <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> current
        /// <c>=======</c> incoming <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>, and the three-part style, whose base section
        /// (<c>|||||||</c>) is left out. Throws <see cref="ConflictMarkerException"/> when the markers don't nest.
        /// </summary>
        public static ConflictSplit SplitConflicts(string text)
        {
            var current = new StringBuilder();
            var incoming = new StringBuilder();
            var state = "both";
            var conflicts = 0;
            var at = 0;
            var start = 0;
            while (start < text.Length)
            {
                var nl = text.IndexOf('\n', start);
                var line = nl < 0 ? text.Substring(start) : text.Substring(start, nl + 1 - start);
                start = nl < 0 ? text.Length : nl + 1;
                at++;
                var m = Marker.Match(line);
                var marker = m.Success ? m.Groups[1].Value : null;
                if (marker == "<<<<<<<")
                {
                    if (state != "both") throw new ConflictMarkerException($"Line {at}: a conflict starts inside another one.");
                    state = "current";
                    conflicts++;
                }
                else if (marker == "|||||||" && state == "current")
                {
                    state = "base";
                }
                else if (marker == "=======" && (state == "current" || state == "base"))
                {
                    state = "incoming";
                }
                else if (marker == ">>>>>>>")
                {
                    if (state != "incoming") throw new ConflictMarkerException($"Line {at}: a conflict ends where none is open.");
                    state = "both";
                }
                else if (state == "both")
                {
                    current.Append(line);
                    incoming.Append(line);
                }
                else if (state == "current") current.Append(line);
                else if (state == "incoming") incoming.Append(line);
            }
            if (state != "both") throw new ConflictMarkerException("The last conflict has no end marker (>>>>>>>).");
            return new ConflictSplit { Current = current.ToString(), Incoming = incoming.ToString(), Conflicts = conflicts };
        }

        /// <summary>The owners in a model, as <see cref="Adopt"/> needs them.</summary>
        private sealed class Owners
        {
            public readonly Dictionary<string, Vertex> V = new Dictionary<string, Vertex>();
            public readonly Dictionary<string, Vertex> RegionOwner = new Dictionary<string, Vertex>();

            public Owners(FsmModel m)
            {
                foreach (var v in m.Vertices) V[v.Id] = v;
                foreach (var x in m.Regions) RegionOwner[x.Id] = null;
                foreach (var v in m.Vertices)
                {
                    foreach (var x in v.Regions) RegionOwner[x.Id] = v;
                }
            }

            public Vertex Get(string id) => id != null && V.TryGetValue(id, out var v) ? v : null;

            public bool Border(Vertex v) =>
                v.Type == VertexType.ConnectionPointRef || ((v.Type == VertexType.EntryPoint || v.Type == VertexType.ExitPoint) && V.ContainsKey(v.Parent));

            public Vertex Owner(Vertex v) => Border(v) ? Get(v.Parent) : v.Parent != null && RegionOwner.TryGetValue(v.Parent, out var o) ? o : null;

            public bool Inside(Vertex v, Vertex s)
            {
                var seen = new HashSet<Vertex>();
                for (var o = Owner(v); o != null && seen.Add(o); o = Owner(o))
                {
                    if (o == s) return true;
                }
                return false;
            }
        }

        /// <summary>A key that is no key (an element whose name is used twice has none): one value, as JavaScript's undefined.</summary>
        private const string NoKey = "\u0000undefined";

        /// <summary>
        /// The result with the element of <paramref name="keys"/> (see <see cref="Compare"/>: its key in either version,
        /// for a transition whose target or trigger changed) as <paramref name="source"/> has it: updated, added (with
        /// what it contains), or removed (with what it contains and the transitions to and from it). Positions are taken
        /// relative to the owner. The result's own ids stay; added elements get new ones.
        /// </summary>
        public static FsmModel Adopt(FsmModel result, FsmModel source, params string[] keys)
        {
            var key = keys[0];
            var r = result.Clone();
            var ri = Compare.ItemsOf(r);
            var si = Compare.ItemsOf(source);
            var SV = new Dictionary<string, Vertex>();
            foreach (var v in source.Vertices) SV[v.Id] = v;
            var ST = new Dictionary<string, Transition>();
            foreach (var t in source.Transitions) ST[t.Id] = t;
            var taken = new HashSet<string>(r.Vertices.Select(v => v.Id).Concat(r.Transitions.Select(t => t.Id)).Concat(r.Regions.Select(x => x.Id))
                .Concat(r.Vertices.SelectMany(v => v.Regions.Select(x => x.Id))));
            string Fresh(string baseId)
            {
                var id = $"{baseId}~m";
                for (var n = 2; taken.Contains(id); n++) id = $"{baseId}~m{n}";
                taken.Add(id);
                return id;
            }
            // Keys of the source's elements, by id, and the result's ids, by key.
            var keyOfS = new Dictionary<string, string>();
            foreach (var it in si.List)
            {
                if (it.Id.Length > 0) keyOfS[it.Id] = it.Key;
            }
            var idOfR = new Dictionary<string, string>();
            foreach (var it in ri.List)
            {
                if (it.Id.Length > 0) idOfR[it.Key] = it.Id;
            }
            string KeyOfS(string id) => id != null && keyOfS.TryGetValue(id, out var k) ? k : null;
            string IdOfR(string k) => idOfR.TryGetValue(k ?? NoKey, out var id) ? id : null;

            var so = new Owners(source);

            // The result's parent for a source vertex: the same region of the same owner, else the top-level region.
            string ParentIn(Vertex v)
            {
                var ro = new Owners(r);
                if (so.Border(v)) return IdOfR(KeyOfS(v.Parent)) ?? "";
                var owner = so.Owner(v);
                if (owner == null)
                {
                    var index = source.Regions.FindIndex(x => x.Id == v.Parent);
                    return (index >= 0 && index < r.Regions.Count ? r.Regions[index] : r.Regions[0]).Id;
                }
                var o = ro.Get(IdOfR(KeyOfS(owner.Id)) ?? "");
                if (o == null || o.Regions.Count == 0) return r.Regions[0].Id;
                var i = owner.Regions.FindIndex(x => x.Id == v.Parent);
                var wanted = i >= 0 && i < owner.Regions.Count ? owner.Regions[i].Name : null;
                var named = o.Regions.FirstOrDefault(x => x.Name.Length > 0 && x.Name == wanted);
                return (named ?? (i >= 0 && i < o.Regions.Count ? o.Regions[i] : o.Regions[0])).Id;
            }

            // Copies a source vertex's place onto a result vertex, relative to its owner.
            void Place(Vertex target, Vertex v)
            {
                var ro = new Owners(r);
                var sOwner = so.Owner(v);
                var rOwner = ro.Get(target.Parent) ?? (target.Parent != null && ro.RegionOwner.TryGetValue(target.Parent, out var ow) ? ow : null);
                var a = sOwner != null ? new PointD(sOwner.X, sOwner.Y) : new PointD(0, 0);
                var b = rOwner != null && rOwner != target ? new PointD(rOwner.X, rOwner.Y) : new PointD(0, 0);
                target.X = Num.R1(b.X + v.X - a.X);
                target.Y = Num.R1(b.Y + v.Y - a.Y);
            }
            void CopyVertex(Vertex target, Vertex v)
            {
                target.Type = v.Type;
                target.Name = v.Name;
                target.W = v.W;
                target.H = v.H;
                target.RegionLayout = v.RegionLayout;
                target.Entry = v.Entry;
                target.Exit = v.Exit;
                target.DoActivity = v.DoActivity;
                target.Deferrable = new List<string>(v.Deferrable);
                target.Invariant = v.Invariant;
                target.Submachine = v.Submachine;
                target.Stereotype = v.Stereotype;
                target.Ref = v.Ref;
                target.PointKind = v.PointKind;
                target.Text = v.Text;
                target.Color = v.Color;
                target.ItemColors = new Dictionary<string, string>(v.ItemColors);
                target.Group = v.Group;
                // Regions: the same count and names, keeping the result's ids where they line up.
                target.Regions = v.Regions.Select((x, i) => new Region { Id = i < target.Regions.Count ? target.Regions[i].Id : Fresh(x.Id), Name = x.Name, Size = x.Size }).ToList();
                // An anchor to what the result doesn't have (yet) is kept as the reference the file writes, which names it once
                // it is taken too.
                target.Anchors = v.Anchors.Select(a => IdOfR(KeyOfS(a) ?? "") ?? (KeyOfS(a) != null ? Format.UnresolvedAnchorOf(source, a) : null) ?? a).ToList();
            }
            void RemoveVertex(string id)
            {
                var ro = new Owners(r);
                var v = ro.Get(id);
                if (v == null) return;
                var gone = new HashSet<string>(new[] { v }.Concat(r.Vertices.Where(x => ro.Inside(x, v))).Select(x => x.Id));
                r.Vertices = r.Vertices.Where(x => !gone.Contains(x.Id)).ToList();
                r.Transitions = r.Transitions.Where(t => !gone.Contains(t.Source) && !gone.Contains(t.Target)).ToList();
                foreach (var x in r.Vertices) x.Anchors = x.Anchors.Where(a => !gone.Contains(a)).ToList();
            }
            void AddVertex(Vertex v)
            {
                var n = new Vertex { Id = Fresh(v.Id), Type = v.Type, Name = v.Name, Parent = ParentIn(v), X = 0, Y = 0, W = v.W, H = v.H };
                CopyVertex(n, v);
                r.Vertices.Add(n);
                idOfR[KeyOfS(v.Id) ?? NoKey] = n.Id;
                Place(n, v);
                // What it contains comes along.
                foreach (var x in source.Vertices)
                {
                    if (x == v || !so.Inside(x, v)) continue;
                    if (!idOfR.ContainsKey(KeyOfS(x.Id) ?? NoKey)) AddVertex(x);
                }
            }
            // The target in the result; one the result doesn't have (yet) is the reference the file writes, naming nothing.
            string TargetIn(Transition t)
            {
                var k = KeyOfS(t.Target);
                var reference = SV.ContainsKey(t.Target) ? Format.ReferenceOf(source, t.Target) : null;
                return (k != null ? IdOfR(k) : null) ?? (reference != null ? Format.Unresolved + reference : t.Target);
            }
            void CopyTransition(Transition target, Transition t)
            {
                target.Kind = t.Kind;
                target.Triggers = new List<string>(t.Triggers);
                target.Guard = t.Guard;
                target.Action = t.Action;
                target.Precondition = t.Precondition;
                target.Postcondition = t.Postcondition;
                target.Color = t.Color;
                target.ItemColors = new Dictionary<string, string>(t.ItemColors);
                target.LabelOffset = t.LabelOffset;
                target.Target = t.Kind == TransitionKind.Internal ? target.Source : TargetIn(t);
                // Bend points, moved with the difference between the two sources' places.
                SV.TryGetValue(t.Source, out var s1);
                var s2 = r.Vertices.FirstOrDefault(x => x.Id == target.Source);
                target.Points = s1 != null && s2 != null
                    ? t.Points.Select(p => new PointD(Num.R1(p.X - s1.X + s2.X), Num.R1(p.Y - s1.Y + s2.Y))).ToList()
                    : new List<PointD>();
            }

            if (key == "machine")
            {
                r.Name = source.Name;
                r.Kind = source.Kind;
                r.Documentation = source.Documentation;
                return r.Normalize();
            }
            var s = keys.Select(si.Get).FirstOrDefault(x => x != null);
            var mine = keys.Select(ri.Get).FirstOrDefault(x => x != null);
            if (s?.What == "transition" || mine?.What == "transition")
            {
                if (s == null) r.Transitions = r.Transitions.Where(t => t.Id != mine.Id).ToList();
                else
                {
                    var t = ST[s.Id];
                    var existing = mine != null ? r.Transitions.FirstOrDefault(x => x.Id == mine.Id) : null;
                    if (existing != null) CopyTransition(existing, t);
                    else
                    {
                        var source2 = IdOfR(KeyOfS(t.Source) ?? "");
                        if (source2 != null)
                        {
                            var n = new Transition { Id = Fresh(t.Id), Source = source2, Kind = t.Kind };
                            CopyTransition(n, t);
                            r.Transitions.Add(n);
                        }
                    }
                }
                return r.Normalize();
            }
            if (s == null)
            {
                if (mine != null) RemoveVertex(mine.Id);
                return r.Normalize();
            }
            var vs = SV[s.Id];
            var ex = mine != null ? r.Vertices.FirstOrDefault(x => x.Id == mine.Id) : null;
            if (ex != null)
            {
                var parent = ParentIn(vs);
                if (parent.Length > 0) ex.Parent = parent;
                var (x0, y0) = (ex.X, ex.Y);
                CopyVertex(ex, vs);
                Place(ex, vs);
                // What it contains moves with it, bend points between them included.
                var dx = ex.X - x0;
                var dy = ex.Y - y0;
                if (dx != 0 || dy != 0)
                {
                    var ro = new Owners(r);
                    var inner = new HashSet<string>(r.Vertices.Where(x => ro.Inside(x, ex)).Select(x => x.Id));
                    foreach (var x in r.Vertices)
                    {
                        if (!inner.Contains(x.Id)) continue;
                        x.X += dx;
                        x.Y += dy;
                    }
                    inner.Add(ex.Id);
                    foreach (var t in r.Transitions)
                    {
                        if (t.Points.Count > 0 && inner.Contains(t.Source) && inner.Contains(t.Target)) t.Points = t.Points.Select(p => new PointD(p.X + dx, p.Y + dy)).ToList();
                    }
                }
            }
            else AddVertex(vs);
            return r.Normalize();
        }
    }
}
