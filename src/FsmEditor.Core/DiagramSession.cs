using System;
using System.Collections.Generic;
using System.Linq;

namespace FsmEditor.Core
{
    /// <summary>Vertices and transitions copied to the clipboard.</summary>
    public sealed class ClipboardFragment
    {
        public List<Vertex> Vertices { get; set; } = new List<Vertex>();
        public List<Transition> Transitions { get; set; } = new List<Transition>();
    }

    /// <summary>
    /// The model being edited in a diagram, its selection and what is known about
    /// the other machines of the workspace, with every editing operation of the
    /// diagram editor. Operations that change the model call <see cref="Commit"/>,
    /// which asks the host to write the document; problems they refuse are
    /// reported through <see cref="Message"/>.
    /// </summary>
    public sealed class DiagramSession
    {
        private readonly Random _random = new Random();
        private int _pasteCount;

        public DiagramSession(FsmModel model)
        {
            Model = model;
            Reindex();
        }

        public FsmModel Model { get; private set; }
        public ModelIndex Index { get; private set; }
        public HashSet<string> Selection { get; set; } = new HashSet<string>();
        /// <summary>Machines referenced by submachine states, keyed by href.</summary>
        public IReadOnlyDictionary<string, SubmachineInfo> Submachines { get; set; } = new Dictionary<string, SubmachineInfo>();
        /// <summary>State machines in the workspace that can be used as submachines.</summary>
        public IReadOnlyList<MachineInfo> Machines { get; set; } = new List<MachineInfo>();

        /// <summary>Raised after a change that must be written to the document.</summary>
        public event EventHandler Committed;
        /// <summary>Raised with a short explanation when an operation is refused or needs attention.</summary>
        public event EventHandler<string> Message;

        public void ReplaceModel(FsmModel model)
        {
            Model = model;
            Reindex();
            Selection = new HashSet<string>(Selection.Where(id => Index.Vertices.ContainsKey(id) || Index.Transitions.ContainsKey(id)));
        }

        public void Reindex() => Index = new ModelIndex(Model);

        public void Commit()
        {
            Reindex();
            Committed?.Invoke(this, EventArgs.Empty);
        }

        public void Say(string message) => Message?.Invoke(this, message);

        public string RootRegion => Model.Regions[0].Id;

        // ---------------------------------------------------------------- names and ids

        public string NewId(string prefix)
        {
            const string chars = "0123456789abcdefghijklmnopqrstuvwxyz";
            string id;
            do
            {
                var suffix = new char[6];
                for (int i = 0; i < suffix.Length; i++) suffix[i] = chars[_random.Next(chars.Length)];
                id = prefix + "_" + new string(suffix);
            }
            while (Index.Vertices.ContainsKey(id) || Index.Transitions.ContainsKey(id) || Index.RegionOwner.ContainsKey(id));
            return id;
        }

        public string UniqueName(string baseName)
        {
            var names = new HashSet<string>(Model.Vertices.Select(v => v.Name));
            if (!names.Contains(baseName)) return baseName;
            for (int i = 2; ; i++)
            {
                if (!names.Contains(baseName + i)) return baseName + i;
            }
        }

        /// <summary>Display name of the machine a submachine href points to.</summary>
        public string MachineLabel(string href)
        {
            if (Submachines.TryGetValue(href ?? "", out var info) && !string.IsNullOrEmpty(info.Name)) return info.Name;
            var known = Machines.FirstOrDefault(m => m.Href == href);
            if (known != null && known.Name.Length > 0) return known.Name;
            var file = Uri.UnescapeDataString((href ?? "").Split('#')[0]);
            file = file.Substring(file.LastIndexOf('/') + 1);
            return file.EndsWith(".fsm", StringComparison.Ordinal) ? file.Substring(0, file.Length - 4) : file;
        }

        /// <summary>Entry/exit points of a submachine state's referenced machine, as far as they could be resolved.</summary>
        public List<ConnectionPointInfo> SubmachinePoints(Vertex state)
        {
            if (state == null || state.Submachine.Length == 0) return new List<ConnectionPointInfo>();
            return Submachines.TryGetValue(state.Submachine, out var info) && info.Found ? info.Points : new List<ConnectionPointInfo>();
        }

        /// <summary>Name of the point <paramref name="id"/> in the machine referenced by <paramref name="state"/>.</summary>
        public string PointName(Vertex state, string id) => SubmachinePoints(state).FirstOrDefault(q => q.Id == id)?.Name ?? "";

        public List<Vertex> RefsOf(Vertex state) =>
            Model.Vertices.Where(v => v.Type == VertexType.ConnectionPointRef && v.Parent == state.Id).ToList();

        /// <summary>Points of the submachine not yet referenced on <paramref name="state"/>.</summary>
        public List<ConnectionPointInfo> FreePoints(Vertex state)
        {
            var used = new HashSet<string>(RefsOf(state).Select(r => r.Ref));
            return SubmachinePoints(state).Where(q => !used.Contains(q.Id)).ToList();
        }

        // ---------------------------------------------------------------- creating

        /// <summary>Places a new element of tool <paramref name="toolId"/> at <paramref name="p"/>. Returns it, or null when refused.</summary>
        public Vertex CreateVertex(string toolId, PointD p)
        {
            var def = Tools.ById(toolId);
            if (def?.Create == null) return null;
            Reindex();
            var v = def.Create(this);
            v.Id = NewId("v");
            if (v.Type.IsPointType())
            {
                var s = Geometry.StateAt(Index, p, new HashSet<string>());
                if (s != null && s.Submachine.Length > 0 && v.Type != VertexType.ConnectionPointRef)
                {
                    // On a submachine state, UML uses a reference to the submachine's own entry/exit point.
                    var kind = v.Type == VertexType.ExitPoint ? PointKind.Exit : PointKind.Entry;
                    v.Type = VertexType.ConnectionPointRef;
                    v.PointKind = kind;
                    v.Ref = "";
                    Say($"Added a connection point reference: pick the {(kind == PointKind.Exit ? "exit" : "entry")} point of '{MachineLabel(s.Submachine)}' it refers to.");
                }
                if (v.Type == VertexType.ConnectionPointRef)
                {
                    if (s == null || s.Submachine.Length == 0)
                    {
                        Say("Connection point references go on the border of a submachine state.");
                        return null;
                    }
                    var free = FreePoints(s);
                    var pick = free.FirstOrDefault(q => v.Ref.Length == 0 && q.Kind == v.PointKind) ?? free.FirstOrDefault();
                    if (pick != null)
                    {
                        v.Ref = pick.Id;
                        v.PointKind = pick.Kind;
                    }
                }
                if (s != null)
                {
                    v.Parent = s.Id;
                    Geometry.SnapToBorder(v, s, p);
                }
                else
                {
                    // Not on a state: a connection point of the state machine itself.
                    v.Parent = RootRegion;
                    v.Name = UniqueName(v.Type == VertexType.EntryPoint ? "in" : "out");
                    v.X = Geometry.Snap(p.X) - v.W / 2;
                    v.Y = Geometry.Snap(p.Y) - v.H / 2;
                }
            }
            else
            {
                v.X = Geometry.Snap(p.X - v.W / 2);
                v.Y = Geometry.Snap(p.Y - v.H / 2);
                var r = Geometry.RegionAt(Index, p, new HashSet<string>());
                v.Parent = r != null ? r.Id : RootRegion;
            }
            Model.Vertices.Add(v);
            Selection = new HashSet<string> { v.Id };
            Commit();
            return v;
        }

        /// <summary>Adds a reference for every unreferenced point (or only <paramref name="only"/>): entries on the left border, exits on the right.</summary>
        public void AddAllReferences(Vertex state, ConnectionPointInfo only = null)
        {
            var free = only != null ? new List<ConnectionPointInfo> { only } : FreePoints(state);
            if (free.Count == 0) return;
            foreach (var kind in new[] { PointKind.Entry, PointKind.Exit })
            {
                var list = free.Where(q => q.Kind == kind).ToList();
                var existing = RefsOf(state).Count(r => r.PointKind == kind);
                var total = list.Count + existing;
                for (int i = 0; i < list.Count; i++)
                {
                    var v = new Vertex
                    {
                        Id = NewId("v"),
                        Type = VertexType.ConnectionPointRef,
                        Ref = list[i].Id,
                        PointKind = kind,
                        Parent = state.Id,
                        W = 16,
                        H = 16,
                    };
                    var y = state.Y + state.H * (existing + i + 1) / (total + 1);
                    Geometry.SnapToBorder(v, state, new PointD(kind == PointKind.Entry ? state.X : state.X + state.W, y));
                    Model.Vertices.Add(v);
                    Reindex();
                }
            }
            // Leave room for the captions under the points.
            state.H = Math.Max(state.H, 40 + 24 * Math.Max(free.Count(q => q.Kind == PointKind.Entry), free.Count(q => q.Kind == PointKind.Exit)));
            Commit();
        }

        public void AddRegion(Vertex state)
        {
            if (state.Regions.Count == 0)
            {
                // Make room for the region below the name compartment.
                state.W = Math.Max(state.W, 220);
                state.H = Math.Max(state.H, Geometry.HeaderHeight(Model, state) + 120);
            }
            else if (state.RegionLayout == RegionLayout.Horizontal)
            {
                state.W += 160;
            }
            else
            {
                state.H += 100;
            }
            state.Regions.Add(new Region { Id = NewId("r") });
            Selection = new HashSet<string> { state.Id };
            Commit();
        }

        public void RemoveRegion(Vertex state, string regionId)
        {
            var doomed = new HashSet<string>();
            foreach (var v in Model.Vertices)
            {
                if (v.Parent != regionId) continue;
                doomed.Add(v.Id);
                foreach (var d in Index.Descendants(v)) doomed.Add(d.Id);
            }
            state.Regions = state.Regions.Where(r => r.Id != regionId).ToList();
            RemoveVertices(doomed, new HashSet<string>());
            Commit();
        }

        public void AddInternalTransition(Vertex state)
        {
            Model.Transitions.Add(new Transition
            {
                Id = NewId("t"),
                Source = state.Id,
                Target = state.Id,
                Kind = TransitionKind.Internal,
                Triggers = { "event" },
                Effect = "action()",
            });
            Commit();
        }

        // ---------------------------------------------------------------- deleting

        public void RemoveVertices(ISet<string> ids, ISet<string> transitionIds)
        {
            Model.Vertices = Model.Vertices.Where(v => !ids.Contains(v.Id)).ToList();
            Model.Transitions = Model.Transitions
                .Where(t => !transitionIds.Contains(t.Id) && !ids.Contains(t.Source) && !ids.Contains(t.Target))
                .ToList();
            var alive = new HashSet<string>(Model.Vertices.Select(v => v.Id).Concat(Model.Transitions.Select(t => t.Id)));
            foreach (var v in Model.Vertices) v.Anchors = v.Anchors.Where(alive.Contains).ToList();
            Selection = new HashSet<string>(Selection.Where(alive.Contains));
            Reindex();
        }

        public void DeleteSelection()
        {
            if (Selection.Count == 0) return;
            var vs = new HashSet<string>();
            var ts = new HashSet<string>();
            foreach (var id in Selection)
            {
                var v = Index.Vertex(id);
                if (v != null)
                {
                    vs.Add(id);
                    foreach (var d in Index.Descendants(v)) vs.Add(d.Id);
                }
                else if (Index.Transition(id) != null)
                {
                    ts.Add(id);
                }
            }
            RemoveVertices(vs, ts);
            Selection.Clear();
            Commit();
        }

        public void RemoveTransition(Transition t)
        {
            Model.Transitions.Remove(t);
            Selection.Remove(t.Id);
            Commit();
        }

        // ---------------------------------------------------------------- connecting

        /// <summary>
        /// Draws a transition from <paramref name="source"/> to <paramref name="targetId"/>, or attaches a comment.
        /// Returns the new transition, or null when none was created.
        /// </summary>
        public Transition Connect(Vertex source, string targetId)
        {
            if (source.Type == VertexType.Comment)
            {
                if (targetId == source.Id) return null;
                if (!source.Anchors.Contains(targetId)) source.Anchors.Add(targetId);
                Selection = new HashSet<string> { source.Id };
                Commit();
                return null;
            }
            var target = Index.Vertex(targetId);
            if (target == null) return null;
            if (target.Type == VertexType.Comment)
            {
                if (!target.Anchors.Contains(source.Id)) target.Anchors.Add(source.Id);
                Commit();
                return null;
            }
            if (source.Type == VertexType.ConnectionPointRef && source.PointKind == PointKind.Entry)
            {
                Say("An entry reference only receives transitions; execution continues inside the submachine.");
                return null;
            }
            if (target.Type == VertexType.ConnectionPointRef && target.PointKind == PointKind.Exit)
            {
                Say("An exit reference only has outgoing transitions; it is reached when the submachine exits.");
                return null;
            }
            if (source.Type == VertexType.Final || source.Type == VertexType.Terminate)
            {
                Say($"A {source.Type.Title().ToLowerInvariant()} cannot have outgoing transitions.");
                return null;
            }
            if (target.Type == VertexType.Initial)
            {
                Say("An initial pseudostate cannot be the target of a transition.");
                return null;
            }
            var t = new Transition { Id = NewId("t"), Source = source.Id, Target = target.Id };
            Model.Transitions.Add(t);
            Selection = new HashSet<string> { t.Id };
            Commit();
            return t;
        }

        // ---------------------------------------------------------------- transitions

        /// <summary>Adds a bend point at <paramref name="p"/>, on the segment nearest to it.</summary>
        public void AddWaypoint(Transition t, PointD p)
        {
            var pts = Geometry.Route(Index, t);
            if (pts == null) return;
            if (t.Points.Count == 0) t.Points = pts.Skip(1).Take(pts.Count - 2).Select(q => new PointD(Num.Round(q.X), Num.Round(q.Y))).ToList();
            var full = Geometry.Route(Index, t);
            int best = 0;
            var bestD = double.PositiveInfinity;
            for (int i = 0; i < full.Count - 1; i++)
            {
                var d = Geometry.DistToSegment(p, full[i], full[i + 1]);
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            t.Points.Insert(best, new PointD(Geometry.Snap(p.X), Geometry.Snap(p.Y)));
            Selection = new HashSet<string> { t.Id };
            Commit();
        }

        public void RemoveWaypoint(Transition t, int index)
        {
            if (index < 0 || index >= t.Points.Count) return;
            t.Points.RemoveAt(index);
            Commit();
        }

        public void Straighten(Transition t)
        {
            t.Points.Clear();
            t.LabelOffset = null;
            Commit();
        }

        public void Reverse(Transition t)
        {
            var s = Index.Vertex(t.Source);
            var g = Index.Vertex(t.Target);
            if (g == null || g.Type == VertexType.Initial || (s != null && (s.Type == VertexType.Final || s.Type == VertexType.Terminate)))
            {
                Say("This transition cannot be reversed.");
                return;
            }
            (t.Source, t.Target) = (t.Target, t.Source);
            t.Points.Reverse();
            Commit();
        }

        public void SetKind(Transition t, TransitionKind kind)
        {
            t.Kind = kind;
            if (kind == TransitionKind.Internal)
            {
                t.Target = t.Source;
                t.Points.Clear();
            }
            Commit();
        }

        /// <summary>Applies a label typed on the diagram. Returns an error message, or null when applied.</summary>
        public string ApplyLabel(Transition t, string text)
        {
            var err = Labels.ApplyLabel(Model, t, text, Index.Vertex(t.Source));
            if (err == null) Commit();
            return err;
        }

        // ---------------------------------------------------------------- vertices

        /// <summary>Swaps a pseudostate for another kind of its group, keeping its center.</summary>
        public void ChangeType(Vertex v, VertexType type)
        {
            var c = v.Center;
            v.Type = type;
            (v.W, v.H) = type.DefaultSize();
            v.X = c.X - v.W / 2;
            v.Y = c.Y - v.H / 2;
            Commit();
        }

        public bool SetSubmachine(Vertex v, string href)
        {
            if (href.Length > 0 && v.Regions.Count > 0)
            {
                Say("Remove the regions first: a submachine state cannot own regions.");
                return false;
            }
            v.Submachine = href;
            Commit();
            return true;
        }

        /// <summary>Moves the selected elements (and everything inside them) by a few pixels.</summary>
        public bool Nudge(double dx, double dy)
        {
            var moved = new HashSet<string>();
            foreach (var id in Selection)
            {
                var v = Index.Vertex(id);
                if (v == null || Index.Ancestors(v).Any(a => Selection.Contains(a.Id))) continue;
                foreach (var x in new[] { v }.Concat(Index.Descendants(v)))
                {
                    if (!moved.Add(x.Id)) continue;
                    x.X += dx;
                    x.Y += dy;
                }
            }
            if (moved.Count == 0) return false;
            Commit();
            return true;
        }

        /// <summary>Selected elements without a selected ancestor, which lead a move.</summary>
        public List<Vertex> SelectionRoots() =>
            Selection.Select(Index.Vertex).Where(v => v != null && !Index.Ancestors(v).Any(a => Selection.Contains(a.Id))).ToList();

        /// <summary>After a move: re-parents the moved elements into the region (or state border) they were dropped on.</summary>
        public void DropMoved(IEnumerable<Vertex> roots, ISet<string> moving)
        {
            Reindex();
            foreach (var v in roots)
            {
                if (Index.IsBorderVertex(v)) continue;
                if (v.Type == VertexType.EntryPoint || v.Type == VertexType.ExitPoint)
                {
                    // A machine-level point dropped on a state becomes that state's point; elsewhere it stays top-level.
                    var s = Geometry.StateAt(Index, v.Center, moving);
                    if (s != null && s.Submachine.Length == 0)
                    {
                        v.Parent = s.Id;
                        Geometry.SnapToBorder(v, s, v.Center);
                    }
                    else
                    {
                        v.Parent = RootRegion;
                    }
                    continue;
                }
                var r = Geometry.RegionAt(Index, v.Center, moving);
                v.Parent = r != null ? r.Id : RootRegion;
            }
            Commit();
        }

        /// <summary>Smallest size a vertex can be resized to.</summary>
        public (double W, double H) MinimumSize(Vertex v)
        {
            var bar = v.Type == VertexType.Fork || v.Type == VertexType.Join;
            var minW = bar ? 6 : 40;
            var minH = bar ? 6 : v.Type == VertexType.State ? Geometry.HeaderHeight(Model, v) + (v.Regions.Count > 0 ? 30 : 6) : 30;
            return (minW, minH);
        }

        /// <summary>Keeps the connection points of <paramref name="state"/> on its border after a resize.</summary>
        public void KeepPointsOnBorder(Vertex state)
        {
            foreach (var cp in Model.Vertices)
            {
                if (cp.Parent == state.Id && Index.IsBorderVertex(cp)) Geometry.SnapToBorder(cp, state, cp.Center);
            }
        }

        // ---------------------------------------------------------------- clipboard

        /// <summary>The selected elements with everything they contain and the transitions between them.</summary>
        public ClipboardFragment CollectSelection()
        {
            var ids = new HashSet<string>();
            foreach (var id in Selection)
            {
                var v = Index.Vertex(id);
                if (v == null) continue;
                ids.Add(id);
                foreach (var d in Index.Descendants(v)) ids.Add(d.Id);
            }
            if (ids.Count == 0) return null;
            return new ClipboardFragment
            {
                Vertices = Model.Vertices.Where(v => ids.Contains(v.Id)).Select(v => v.Clone()).ToList(),
                Transitions = Model.Transitions.Where(t => ids.Contains(t.Source) && ids.Contains(t.Target)).Select(t => t.Clone()).ToList(),
            };
        }

        /// <summary>Copying starts a new series of paste offsets.</summary>
        public void ResetPasteOffset() => _pasteCount = 0;

        public void Paste(ClipboardFragment data)
        {
            if (data == null || data.Vertices.Count == 0) return;
            Reindex();
            _pasteCount++;
            var off = 20 * _pasteCount;
            var map = new Dictionary<string, string>();
            foreach (var v in data.Vertices)
            {
                map[v.Id] = NewId("v");
                foreach (var r in v.Regions) map[r.Id] = NewId("r");
            }
            var added = new List<Vertex>();
            foreach (var src in data.Vertices)
            {
                var v = src.Clone();
                v.Id = map[src.Id];
                v.X += off;
                v.Y += off;
                v.Regions = v.Regions.Select(r => new Region { Id = map[r.Id], Name = r.Name }).ToList();
                if (map.TryGetValue(v.Parent, out var parent))
                {
                    v.Parent = parent;
                }
                else
                {
                    // Pasted without its owner: keep it where it was when that still exists.
                    var border = Index.IsBorderVertex(v);
                    if (border ? Index.Vertex(v.Parent) == null : !Index.IsRegion(v.Parent))
                    {
                        if (border) continue;
                        v.Parent = RootRegion;
                    }
                }
                v.Anchors = v.Anchors.Where(map.ContainsKey).Select(a => map[a]).ToList();
                Model.Vertices.Add(v);
                added.Add(v);
            }
            foreach (var src in data.Transitions)
            {
                if (!map.ContainsKey(src.Source) || !map.ContainsKey(src.Target)) continue;
                var t = src.Clone();
                t.Id = NewId("t");
                t.Source = map[src.Source];
                t.Target = map[src.Target];
                t.Points = t.Points.Select(p => new PointD(p.X + off, p.Y + off)).ToList();
                Model.Transitions.Add(t);
            }
            Reindex();
            var addedIds = new HashSet<string>(added.Select(v => v.Id));
            Selection = new HashSet<string>(added.Where(v => !Index.Ancestors(v).Any(a => addedIds.Contains(a.Id))).Select(v => v.Id));
            Commit();
        }
    }
}
