using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FsmEditor.Core
{
    /// <summary>The selection cannot be extracted; the message says why.</summary>
    public sealed class ExtractException : Exception
    {
        public ExtractException(string message) : base(message) { }
    }

    public sealed class ExtractResult
    {
        /// <summary>The edited machine, with the submachine state in place of the extracted elements.</summary>
        public FsmModel Parent { get; set; }
        /// <summary>The new state machine.</summary>
        public FsmModel Machine { get; set; }
        /// <summary>Id of the submachine state in <see cref="Parent"/>.</summary>
        public string StateId { get; set; }
    }

    /// <summary>
    /// Extract to Submachine (src/extract.ts of the VS Code edition): moves selected elements of a state machine into a
    /// new state machine, and puts a submachine state referencing it in their place.
    /// <list type="bullet">
    /// <item>Several elements of one region: they move with their contents, the transitions between them, and the
    /// comments and groups among them. A submachine state covering their bounding box replaces them.</item>
    /// <item>One composite state with one region: its contents move, and the state becomes the submachine state,
    /// keeping its name, behaviors, transitions and place. Its own entry/exit points become the new machine's, and
    /// connection point references with the same ids take their place on its border. A composite state with several
    /// regions moves whole, as above, since the new machine has one region.</item>
    /// </list>
    /// A transition crossing the boundary is split at a connection point, keeping its trigger, guard and effect on its
    /// source's side: an incoming one goes to an entry point reference on the submachine state, then from the new
    /// machine's entry point to its target; an outgoing one goes to an exit point of the new machine, then from the exit
    /// point reference to its target. There is one entry point per inner target and one exit point per outer target. An
    /// incoming transition to the target of the new machine's initial transition (without guard or effect) targets the
    /// submachine state itself (default entry) instead.
    /// </summary>
    public static class Extraction
    {
        private const double Point = 16;
        private const string MachineId = "sm";

        /// <summary>The name proposed for the new machine: the extracted state's name when there is one, else <c>Submachine</c>.</summary>
        public static string ProposedName(FsmModel model, IEnumerable<string> selected)
        {
            var states = ExtractedRoots(model, selected).Where(v => v.Type == VertexType.State).ToList();
            var name = states.Count == 1 ? states[0].Name.Trim() : "";
            return name.Length > 0 ? name : "Submachine";
        }

        /// <summary>Why <paramref name="selected"/> can't be extracted, or null when it can.</summary>
        public static string Problem(FsmModel model, IEnumerable<string> selected)
        {
            try
            {
                Extract(model, selected, "Submachine", "Submachine.fsm#sm");
                return null;
            }
            catch (ExtractException e)
            {
                return e.Message;
            }
        }

        /// <summary>The selected vertices without a selected ancestor, with the other members of their groups, in file order.</summary>
        private static List<Vertex> ExtractedRoots(FsmModel model, IEnumerable<string> selected)
        {
            var ix = new ModelIndex(model);
            var ids = new HashSet<string>(selected.Where(id => ix.Vertices.ContainsKey(id)));
            var roots = new HashSet<string>(ids.Where(id => !ix.Ancestors(ix.Vertices[id]).Any(a => ids.Contains(a.Id))));
            var groups = new HashSet<string>(model.Vertices.Where(v => roots.Contains(v.Id) && v.Group.Length > 0).Select(v => v.Group));
            return model.Vertices.Where(v => roots.Contains(v.Id) || (v.Group.Length > 0 && groups.Contains(v.Group))).ToList();
        }

        /// <summary>
        /// Extracts the <paramref name="selected"/> vertices of <paramref name="model"/> into a new state machine named
        /// <paramref name="name"/>, referenced as <paramref name="href"/>. <paramref name="model"/> is left unchanged.
        /// Throws an <see cref="ExtractException"/> when the selection can't be extracted.
        /// </summary>
        public static ExtractResult Extract(FsmModel model, IEnumerable<string> selected, string name, string href)
        {
            var m = model.Clone().Normalize();
            var ix = new ModelIndex(m);
            var roots = ExtractedRoots(m, selected);
            string Label(Vertex v) => v != null && v.Name.Length > 0 ? $"'{v.Name}'" : $"the {(v != null ? v.Type.Key() : "element")}";

            if (!roots.Any(v => v.Type != VertexType.Comment)) throw new ExtractException("Select the states and pseudostates to extract.");
            var border = roots.FirstOrDefault(ix.IsBorderVertex);
            if (border != null) throw new ExtractException($"Points on a state's border can't be extracted without their state ({Label(border)}).");
            if (roots.Any(v => v.Parent != roots[0].Parent)) throw new ExtractException("Select elements of one region to extract them.");

            // A composite state with several regions moves whole instead: the new machine has one region.
            var single = roots.Count == 1 && roots[0].Type == VertexType.State && roots[0].Regions.Count == 1 && roots[0].Submachine.Length == 0 ? roots[0] : null;

            // Vertices moving to the new machine.
            var moved = new HashSet<string>();
            // Entry/exit points of the extracted composite state, which become the new machine's.
            var ownPoints = new List<string>();
            if (single != null)
            {
                foreach (var v in m.Vertices)
                {
                    if (v.Parent == single.Id && ix.IsBorderVertex(v)) ownPoints.Add(v.Id);
                    else if (ix.IsInside(v, single.Id)) moved.Add(v.Id);
                }
                foreach (var id in ownPoints) moved.Add(id);
            }
            else
            {
                foreach (var r in roots) moved.Add(r.Id);
                foreach (var v in m.Vertices)
                {
                    if (roots.Any(r => ix.IsInside(v, r.Id))) moved.Add(v.Id);
                }
            }

            bool IsIn(string id) => moved.Contains(id);
            var crossing = m.Transitions
                .Where(t => IsIn(t.Source) != IsIn(t.Target) && !ownPoints.Contains(t.Source) && !ownPoints.Contains(t.Target))
                .ToList();
            bool History(Vertex v) => v != null && (v.Type == VertexType.ShallowHistory || v.Type == VertexType.DeepHistory);
            foreach (var t in crossing)
            {
                var s = ix.Vertex(t.Source);
                var g = ix.Vertex(t.Target);
                var what = $"the transition from {Label(s)} to {Label(g)}";
                if (t.Kind != TransitionKind.External)
                {
                    throw new ExtractException($"Make {what} external first: a {KindKey(t.Kind)} transition can't cross the submachine's boundary.");
                }
                if (History(s) || History(g))
                {
                    throw new ExtractException($"{Capitalize(what)} goes to or from a history pseudostate across the boundary, which a submachine can't express.");
                }
            }

            // Ids, new ones unique in the model they go to.
            var parentIds = new HashSet<string>(m.Vertices.Select(v => v.Id).Concat(m.Transitions.Select(t => t.Id))
                .Concat(m.Regions.Select(r => r.Id)).Concat(m.Vertices.SelectMany(v => v.Regions.Select(r => r.Id))));
            var machineIds = new HashSet<string>();
            foreach (var v in m.Vertices)
            {
                if (!IsIn(v.Id)) continue;
                machineIds.Add(v.Id);
                foreach (var r in v.Regions) machineIds.Add(r.Id);
            }
            foreach (var t in m.Transitions)
            {
                if (IsIn(t.Source) && IsIn(t.Target)) machineIds.Add(t.Id);
            }
            string Fresh(HashSet<string> taken, string prefix)
            {
                string id;
                for (var n = 1; taken.Contains(id = $"{prefix}_x{n}"); n++) { }
                taken.Add(id);
                return id;
            }

            // The region of the new machine.
            var region = single != null ? new Region { Id = single.Regions[0].Id, Name = single.Regions[0].Name } : new Region { Id = Fresh(machineIds, "r") };
            var outerRegion = roots[0].Parent;

            // The submachine state.
            Vertex state;
            if (single != null)
            {
                state = single;
                state.Regions = new List<Region>();
                state.RegionLayout = default;
                state.Submachine = href;
            }
            else
            {
                var box = UnionBox(roots);
                var names = new HashSet<string>(m.Vertices.Where(v => !IsIn(v.Id)).Select(v => v.Name));
                var stateName = name;
                for (var n = 2; names.Contains(stateName); n++) stateName = name + n;
                state = new Vertex
                {
                    Id = Fresh(parentIds, "v"),
                    Type = VertexType.State,
                    Name = stateName,
                    Parent = outerRegion,
                    X = box.X,
                    Y = box.Y,
                    W = Math.Max(box.W, 140),
                    H = Math.Max(box.H, 60),
                    Submachine = href,
                };
            }

            // Layout of the new machine: its contents from (120, 60), entry points on the left, exit points on the right.
            var content = m.Vertices.Where(v => IsIn(v.Id) && !ownPoints.Contains(v.Id)).ToList();
            var contentBox = content.Count > 0 ? UnionBox(content) : new RectD(0, 0, 0, 0);
            var dx = 120 - contentBox.X;
            var dy = 60 - contentBox.Y;

            var machineVertices = new List<Vertex>();
            var machineTransitions = new List<Transition>();
            var parentVertices = new List<Vertex>();
            var parentTransitions = new List<Transition>();
            foreach (var v in m.Vertices)
            {
                if (!IsIn(v.Id))
                {
                    parentVertices.Add(v);
                    continue;
                }
                if (ownPoints.Contains(v.Id)) continue; // placed below
                v.X += dx;
                v.Y += dy;
                if (single == null && v.Parent == outerRegion) v.Parent = region.Id;
                machineVertices.Add(v);
            }
            if (single == null) parentVertices.Insert(FirstIndex(m.Vertices, roots, IsIn), state);
            // Transitions inside move; those outside stay, and so do those of the composite state's own points.
            foreach (var t in m.Transitions)
            {
                if (IsIn(t.Source) && IsIn(t.Target))
                {
                    t.Points = t.Points.Select(p => new PointD(p.X + dx, p.Y + dy)).ToList();
                    machineTransitions.Add(t);
                }
                else if (!crossing.Contains(t))
                {
                    parentTransitions.Add(t);
                }
            }

            var entries = new List<Vertex>();
            var exits = new List<Vertex>();
            var entryRefs = new List<Vertex>();
            var exitRefs = new List<Vertex>();
            var pointNames = new HashSet<string>();
            string PointName(string baseName)
            {
                var n = baseName;
                for (var i = 2; pointNames.Contains(n); i++) n = baseName + i;
                pointNames.Add(n);
                return n;
            }

            // The composite state's own points: same ids in both machines (a reference in the parent).
            foreach (var id in ownPoints)
            {
                var p = ix.Vertices[id];
                var kind = p.Type == VertexType.EntryPoint ? PointKind.Entry : PointKind.Exit;
                var r = Reference(p.Id, state.Id, p.Id, kind);
                r.X = p.X;
                r.Y = p.Y;
                r.W = p.W;
                r.H = p.H;
                r.Color = p.Color;
                parentVertices.Add(r);
                var point = p.Clone();
                point.Parent = region.Id;
                point.Name = PointName(p.Name.Length > 0 ? p.Name : kind == PointKind.Entry ? "in" : "out");
                point.Group = "";
                (kind == PointKind.Entry ? entries : exits).Add(point);
            }

            // Default entry: the target of the new machine's initial transition, when it has no guard or effect.
            var initial = machineVertices.FirstOrDefault(v => v.Type == VertexType.Initial && v.Parent == region.Id);
            var initialOut = initial != null ? machineTransitions.Where(t => t.Source == initial.Id).ToList() : new List<Transition>();
            var defaultTarget = initialOut.Count == 1 && initialOut[0].Guard.Trim().Length == 0 && initialOut[0].Effect.Trim().Length == 0
                ? initialOut[0].Target : null;

            // By inner target: the reference on the submachine state that incoming transitions go to.
            var entryRefFor = new Dictionary<string, string>();
            // By outer target: the new machine's exit point that outgoing transitions go to.
            var exitFor = new Dictionary<string, string>();
            string TargetName(Vertex v)
            {
                if (v == null) return "";
                if (v.Type == VertexType.ConnectionPointRef)
                {
                    var owner = TargetName(ix.Vertex(v.Parent));
                    return owner.Length > 0 ? owner : "Point";
                }
                var id = Identifier(v.Name);
                return id.Length > 0 ? id : Capitalize(TypeName(v.Type));
            }
            foreach (var t in crossing)
            {
                t.Points = new List<PointD>();
                t.LabelOffset = null;
                if (IsIn(t.Target))
                {
                    if (t.Target == defaultTarget)
                    {
                        t.Target = state.Id;
                        parentTransitions.Add(t);
                        continue;
                    }
                    if (!entryRefFor.TryGetValue(t.Target, out var refId))
                    {
                        var entry = NewPoint(Fresh(machineIds, "v"), VertexType.EntryPoint, PointName("to" + TargetName(ix.Vertex(t.Target))), region.Id);
                        entries.Add(entry);
                        machineTransitions.Add(Plain(Fresh(machineIds, "t"), entry.Id, t.Target));
                        var r = Reference(Fresh(parentIds, "v"), state.Id, entry.Id, PointKind.Entry);
                        entryRefs.Add(r);
                        entryRefFor[t.Target] = refId = r.Id;
                    }
                    t.Target = refId;
                    parentTransitions.Add(t);
                }
                else
                {
                    if (!exitFor.TryGetValue(t.Target, out var exitId))
                    {
                        var exit = NewPoint(Fresh(machineIds, "v"), VertexType.ExitPoint, PointName("to" + TargetName(ix.Vertex(t.Target))), region.Id);
                        exits.Add(exit);
                        var r = Reference(Fresh(parentIds, "v"), state.Id, exit.Id, PointKind.Exit);
                        exitRefs.Add(r);
                        parentTransitions.Add(Plain(Fresh(parentIds, "t"), r.Id, t.Target));
                        exitFor[t.Target] = exitId = exit.Id;
                    }
                    t.Target = exitId;
                    machineTransitions.Add(t);
                }
            }

            // Connection points of the new machine, beside the vertex they lead to or come from.
            var contentRight = content.Count > 0 ? 120 + contentBox.W : 120;
            double BesideOf(Vertex p)
            {
                var linked = p.Type == VertexType.EntryPoint
                    ? machineTransitions.FirstOrDefault(t => t.Source == p.Id)?.Target
                    : machineTransitions.FirstOrDefault(t => t.Target == p.Id)?.Source;
                var v = machineVertices.FirstOrDefault(x => x.Id == linked);
                return v != null ? v.Y + v.H / 2 : 60;
            }
            PlaceColumn(entries, 40, BesideOf);
            PlaceColumn(exits, contentRight + 60, BesideOf);
            machineVertices.AddRange(entries);
            machineVertices.AddRange(exits);

            // References on the submachine state's border, in the order of their points: entries on the left, exits on the right.
            var pointY = entries.Concat(exits).ToDictionary(p => p.Id, p => p.Y);
            entryRefs = entryRefs.OrderBy(r => pointY[r.Ref]).ToList();
            exitRefs = exitRefs.OrderBy(r => pointY[r.Ref]).ToList();
            SpreadOnBorder(entryRefs, state, state.X);
            SpreadOnBorder(exitRefs, state, state.X + state.W);
            parentVertices.AddRange(entryRefs);
            parentVertices.AddRange(exitRefs);

            // The initial pseudostate moved out of the region: the region gets a new one, to the submachine state.
            if (single == null && initial != null && initial.Parent == region.Id && roots.Any(r => r.Id == initial.Id))
            {
                var init = new Vertex
                {
                    Id = Fresh(parentIds, "v"),
                    Type = VertexType.Initial,
                    Parent = outerRegion,
                    X = state.X - 60,
                    Y = state.Y + state.H / 2 - 10,
                    W = 20,
                    H = 20,
                };
                parentVertices.Add(init);
                parentTransitions.Add(Plain(Fresh(parentIds, "t"), init.Id, state.Id));
            }

            // Comment anchors: an outside comment anchored to an extracted element anchors the submachine state instead.
            var machineElements = new HashSet<string>(machineVertices.Select(v => v.Id).Concat(machineTransitions.Select(t => t.Id)));
            var parentElements = new HashSet<string>(parentVertices.Select(v => v.Id).Concat(parentTransitions.Select(t => t.Id)));
            foreach (var v in machineVertices) v.Anchors = v.Anchors.Where(machineElements.Contains).ToList();
            foreach (var v in parentVertices)
            {
                v.Anchors = v.Anchors
                    .Select(a => parentElements.Contains(a) ? a : machineElements.Contains(a) ? state.Id : null)
                    .Where(a => a != null).Distinct().ToList();
            }

            var parent = m;
            parent.Vertices = parentVertices;
            parent.Transitions = parentTransitions;
            var machine = new FsmModel
            {
                Id = MachineId,
                Name = name,
                Kind = m.Kind,
                Regions = new List<Region> { region },
                Vertices = machineVertices,
                Transitions = machineTransitions,
            };
            return new ExtractResult { Parent = parent.Normalize(), Machine = machine.Normalize(), StateId = state.Id };
        }

        private static string KindKey(TransitionKind k) => k == TransitionKind.Local ? "local" : k == TransitionKind.Internal ? "internal" : "external";

        private static string TypeName(VertexType t)
        {
            switch (t)
            {
                case VertexType.EntryPoint: return "entry";
                case VertexType.ExitPoint: return "exit";
                default: return t.Key();
            }
        }

        private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary><paramref name="name"/> as one word: letters, digits and underscores, each word capitalized (<c>Wait for payment</c> → <c>WaitForPayment</c>).</summary>
        private static string Identifier(string name) =>
            string.Concat(Regex.Split(name, "[^A-Za-z0-9_]+").Where(w => w.Length > 0).Select(Capitalize));

        private static Vertex NewPoint(string id, VertexType type, string name, string parent) =>
            new Vertex { Id = id, Type = type, Name = name, Parent = parent, W = Point, H = Point };

        private static Vertex Reference(string id, string parent, string pointId, PointKind kind) =>
            new Vertex { Id = id, Type = VertexType.ConnectionPointRef, Parent = parent, W = Point, H = Point, Ref = pointId, PointKind = kind };

        private static Transition Plain(string id, string source, string target) => new Transition { Id = id, Source = source, Target = target };

        private static RectD UnionBox(IReadOnlyCollection<Vertex> vs)
        {
            var x = vs.Min(v => v.X);
            var y = vs.Min(v => v.Y);
            return new RectD(x, y, vs.Max(v => v.X + v.W) - x, vs.Max(v => v.Y + v.H) - y);
        }

        /// <summary>Where the submachine state goes in the vertex list: where the first extracted vertex was.</summary>
        private static int FirstIndex(List<Vertex> all, List<Vertex> roots, Func<string, bool> isIn)
        {
            var kept = 0;
            foreach (var v in all)
            {
                if (v.Id == roots[0].Id) return kept;
                if (!isIn(v.Id)) kept++;
            }
            return kept;
        }

        /// <summary>Stacks points in a column at <paramref name="x"/>, each near the height <paramref name="yOf"/> gives, without overlapping.</summary>
        private static void PlaceColumn(List<Vertex> points, double x, Func<Vertex, double> yOf)
        {
            var next = double.NegativeInfinity;
            foreach (var w in points.Select(p => (P: p, Y: yOf(p) - Point / 2)).OrderBy(w => w.Y).ToList())
            {
                w.P.X = x;
                w.P.Y = Math.Max(w.Y, next);
                next = w.P.Y + Point + 24;
            }
        }

        /// <summary>Spreads references evenly along the vertical border of <paramref name="state"/> at <paramref name="x"/>.</summary>
        private static void SpreadOnBorder(List<Vertex> refs, Vertex state, double x)
        {
            for (var i = 0; i < refs.Count; i++)
            {
                var r = refs[i];
                r.X = x - r.W / 2;
                r.Y = state.Y + state.H * (i + 1) / (refs.Count + 1) - r.H / 2;
            }
        }
    }
}
