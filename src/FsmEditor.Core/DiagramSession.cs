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
            ResetHeaders();
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
            ResetHeaders();
            Selection = new HashSet<string>(Selection.Where(id => Index.Vertices.ContainsKey(id) || Index.Transitions.ContainsKey(id)));
        }

        public void Reindex() => Index = new ModelIndex(Model);

        public void Commit()
        {
            Reindex();
            KeepHeaders();
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
            Reindex();
            FitInRegion(v, null, false);
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
            var old = BoxOf(state);
            state.H = Math.Max(state.H, 40 + 24 * Math.Max(free.Count(q => q.Kind == PointKind.Entry), free.Count(q => q.Kind == PointKind.Exit)));
            FitInRegion(state, old, false);
            Commit();
        }

        public void AddRegion(Vertex state)
        {
            var old = BoxOf(state);
            if (state.Regions.Count == 0)
            {
                // Make room for the region below the name compartment.
                state.W = Math.Max(state.W, 220);
                state.H = Math.Max(state.H, Geometry.HeaderHeight(Model, state) + 120);
                state.Regions.Add(new Region { Id = NewId("r") });
                _layoutHeader[state.Id] = HeaderOf(state);
            }
            else
            {
                // The new region comes after the others, which keep their sizes.
                KeepRegionSizes(state);
                var size = state.RegionLayout == RegionLayout.Horizontal ? 160 : 100;
                if (state.RegionLayout == RegionLayout.Horizontal) state.W += size;
                else state.H += size;
                state.Regions.Add(new Region { Id = NewId("r"), Size = size });
            }
            Reindex();
            FollowBorder(state, old);
            FitInRegion(state, old, false);
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
            var i = state.Regions.FindIndex(r => r.Id == regionId);
            if (i >= 0 && state.Regions.Count > 1)
            {
                // The state shrinks by the region's size, and the later regions move back with their contents.
                var old = BoxOf(state);
                KeepRegionSizes(state);
                var size = state.Regions[i].Size ?? 0;
                var horizontal = state.RegionLayout == RegionLayout.Horizontal;
                ShiftRegions(state, i + 1, state.Regions.Count - 1, horizontal ? -size : 0, horizontal ? 0 : -size);
                if (horizontal) state.W -= size;
                else state.H -= size;
                FollowBorder(state, old);
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
            var problem = ConnectProblem(source, target);
            if (problem != null)
            {
                Say(problem);
                return null;
            }
            var t = new Transition { Id = NewId("t"), Source = source.Id, Target = target.Id };
            Model.Transitions.Add(t);
            Selection = new HashSet<string> { t.Id };
            Commit();
            return t;
        }

        /// <summary>Why a transition can't go from <paramref name="source"/> to <paramref name="target"/>, or null when it can.</summary>
        private static string ConnectProblem(Vertex source, Vertex target)
        {
            if (source.Type == VertexType.ConnectionPointRef && source.PointKind == PointKind.Entry)
            {
                return "An entry reference only receives transitions; execution continues inside the submachine.";
            }
            if (target.Type == VertexType.ConnectionPointRef && target.PointKind == PointKind.Exit)
            {
                return "An exit reference only has outgoing transitions; it is reached when the submachine exits.";
            }
            if (source.Type == VertexType.Final || source.Type == VertexType.Terminate)
            {
                return $"A {source.Type.Title().ToLowerInvariant()} cannot have outgoing transitions.";
            }
            if (target.Type == VertexType.Initial) return "An initial pseudostate cannot be the target of a transition.";
            return null;
        }

        /// <summary>
        /// Moves the source (<paramref name="atSource"/>) or the target of <paramref name="t"/> to <paramref name="v"/>. Its bend
        /// points stay, unless it becomes or stops being a state's self-transition loop, whose bend points are its corners.
        /// </summary>
        public void Reconnect(Transition t, bool atSource, Vertex v)
        {
            if ((atSource ? t.Source : t.Target) == v.Id) return;
            var source = atSource ? v : Index.Vertex(t.Source);
            var target = atSource ? Index.Vertex(t.Target) : v;
            if (source == null || target == null) return;
            var problem = ConnectProblem(source, target);
            if (problem != null)
            {
                Say(problem);
                return;
            }
            var wasLoop = Geometry.IsLoop(Index, t);
            if (atSource) t.Source = v.Id;
            else t.Target = v.Id;
            if (Geometry.IsLoop(Index, t) != wasLoop) t.Points.Clear();
            Commit();
        }

        // ---------------------------------------------------------------- transitions

        /// <summary>Adds a bend point at <paramref name="p"/>, on the segment nearest to it.</summary>
        public void AddWaypoint(Transition t, PointD p)
        {
            if (Geometry.IsLoop(Index, t)) return; // a self-transition loop has no bend points but its corners
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

        /// <summary>
        /// Drags corner <paramref name="index"/> (in drawing order) of a state's self-transition loop to <paramref name="p"/>.
        /// The far corner resizes the loop and moves it to the state's corner in its quadrant; each other corner moves its
        /// leg and slides the leg's end along the state's edge. The loop then keeps its three corners as bend points.
        /// </summary>
        public void MoveLoopCorner(Transition t, int index, PointD p)
        {
            var s = Index.Vertex(t.Source);
            if (s == null || !Geometry.IsLoop(Index, t)) return;
            var f = Geometry.Loop(t, s);
            if (index == 1)
            {
                var right = p.X >= s.X + s.W / 2;
                var below = p.Y > s.Y + s.H / 2;
                // Moved to another corner of the state, the loop is mirrored.
                if (right != f.Right) f.Hx = 2 * s.X + s.W - f.Hx;
                if (below != f.Below) f.Vy = 2 * s.Y + s.H - f.Vy;
                f.Right = right;
                f.Below = below;
                f.Cx = p.X;
                f.Cy = p.Y;
            }
            else if ((index == 0) == f.StartsOnTopOrBottom)
            {
                f.Hx = p.X;
                f.Cy = p.Y;
            }
            else
            {
                f.Cx = p.X;
                f.Vy = p.Y;
            }
            t.Points = Geometry.ClampLoop(f, s).Corners();
        }

        public void RemoveWaypoint(Transition t, int index)
        {
            if (index < 0 || index >= t.Points.Count) return;
            if (Geometry.IsLoop(Index, t)) t.Points.Clear(); // back to the default loop
            else t.Points.RemoveAt(index);
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
            var old = BoxOf(v);
            v.Type = type;
            (v.W, v.H) = type.DefaultSize();
            v.X = c.X - v.W / 2;
            v.Y = c.Y - v.H / 2;
            FitInRegion(v, old, false);
            Commit();
        }

        /// <summary>Sets the color of a vertex or transition (<paramref name="key"/> null) or of one of its items; null or empty clears it.</summary>
        public void SetColor(Vertex v, string key, string color) => SetColor(c => v.Color = c, v.ItemColors, key, color);

        /// <inheritdoc cref="SetColor(Vertex, string, string)"/>
        public void SetColor(Transition t, string key, string color) => SetColor(c => t.Color = c, t.ItemColors, key, color);

        private void SetColor(Action<string> setOwn, Dictionary<string, string> items, string key, string color)
        {
            var c = ElementColors.IsColor(color) ? color : "";
            if (key == null) setOwn(c);
            else if (c.Length > 0) items[key] = c;
            else items.Remove(key);
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

        // ---------------------------------------------------------------- layout
        // Elements inside a composite state's region stay inside it: what would leave it grows the region,
        // its state and the states around it in turn, and moving back during the same drag undoes that.

        /// <summary>Space kept between a region's border and the elements in it.</summary>
        public const double FitMargin = 10;
        /// <summary>Smallest extent of a region along its state's region layout.</summary>
        public const double MinRegion = 40;

        /// <summary>
        /// Header height each composite state's regions were laid out for (by state id): when its internal
        /// activities change it, the state grows or shrinks by the difference and its regions' contents move
        /// with them. Reset whenever the model comes from the document.
        /// </summary>
        private Dictionary<string, double> _layoutHeader = new Dictionary<string, double>();

        private static RectD BoxOf(Vertex v) => new RectD(v.X, v.Y, v.W, v.H);

        private double HeaderOf(Vertex s) => Geometry.HeaderHeight(Model, s);

        private List<RegionRect> RectsOf(Vertex s) => Geometry.RegionRects(Model, s);

        /// <summary>The composite state owning region <paramref name="id"/>, or null.</summary>
        private Vertex RegionOwnerOf(string id) => Index.RegionOwner.TryGetValue(id ?? "", out var s) ? s : null;

        /// <summary>Records the current header height of every composite state.</summary>
        private void ResetHeaders()
        {
            _layoutHeader = new Dictionary<string, double>();
            foreach (var v in Model.Vertices)
            {
                if (v.Regions.Count > 0) _layoutHeader[v.Id] = HeaderOf(v);
            }
        }

        /// <summary>Geometry of every element (with region sizes and bend points), for <see cref="Restore"/>.</summary>
        public sealed class LayoutSnapshot
        {
            internal List<(Vertex V, RectD Box, List<double?> Sizes)> Vertices;
            internal List<(Transition T, List<PointD> Points)> Points;
            internal Dictionary<string, double> Headers;
        }

        public LayoutSnapshot Snapshot() => new LayoutSnapshot
        {
            Vertices = Model.Vertices.Select(v => (v, BoxOf(v), v.Regions.Select(r => r.Size).ToList())).ToList(),
            Points = Model.Transitions.Select(t => (t, t.Points.ToList())).ToList(),
            Headers = new Dictionary<string, double>(_layoutHeader),
        };

        public void Restore(LayoutSnapshot snap)
        {
            foreach (var (v, box, sizes) in snap.Vertices)
            {
                v.X = box.X;
                v.Y = box.Y;
                v.W = box.W;
                v.H = box.H;
                for (int i = 0; i < v.Regions.Count && i < sizes.Count; i++) v.Regions[i].Size = sizes[i];
            }
            foreach (var (t, points) in snap.Points) t.Points = points.ToList();
            _layoutHeader = new Dictionary<string, double>(snap.Headers);
        }

        /// <summary>The vertices in region <paramref name="id"/>, at any depth.</summary>
        private HashSet<string> RegionContents(string id)
        {
            var ids = new HashSet<string>();
            foreach (var v in Model.Vertices)
            {
                if (v.Parent != id) continue;
                ids.Add(v.Id);
                foreach (var d in Index.Descendants(v)) ids.Add(d.Id);
            }
            return ids;
        }

        /// <summary>Bounding box of the vertices directly in region <paramref name="id"/>, or null when it is empty.</summary>
        private RectD? ContentsBox(string id)
        {
            var kids = Model.Vertices.Where(v => v.Parent == id).ToList();
            if (kids.Count == 0) return null;
            var x = kids.Min(v => v.X);
            var y = kids.Min(v => v.Y);
            return new RectD(x, y, kids.Max(v => v.X + v.W) - x, kids.Max(v => v.Y + v.H) - y);
        }

        /// <summary>Moves the vertices <paramref name="ids"/> by <paramref name="dx"/>, <paramref name="dy"/>, with the bend points of the transitions between them.</summary>
        private void ShiftVertices(ISet<string> ids, double dx, double dy)
        {
            if ((dx == 0 && dy == 0) || ids.Count == 0) return;
            foreach (var v in Model.Vertices)
            {
                if (!ids.Contains(v.Id)) continue;
                v.X += dx;
                v.Y += dy;
            }
            foreach (var t in Model.Transitions)
            {
                if (t.Points.Count > 0 && ids.Contains(t.Source) && ids.Contains(t.Target)) t.Points = t.Points.Select(q => new PointD(q.X + dx, q.Y + dy)).ToList();
            }
        }

        /// <summary>Moves the contents of <paramref name="s"/>'s regions <paramref name="from"/>..<paramref name="to"/> (inclusive).</summary>
        private void ShiftRegions(Vertex s, int from, int to, double dx, double dy)
        {
            var ids = new HashSet<string>();
            for (int i = Math.Max(0, from); i <= to && i < s.Regions.Count; i++) ids.UnionWith(RegionContents(s.Regions[i].Id));
            ShiftVertices(ids, dx, dy);
        }

        /// <summary>Keeps the points on the border of <paramref name="s"/> on the edge they were on when its box was <paramref name="old"/>.</summary>
        private void FollowBorder(Vertex s, RectD old)
        {
            foreach (var cp in Model.Vertices)
            {
                if (cp.Parent != s.Id || !Index.IsBorderVertex(cp)) continue;
                var c = cp.Center;
                var d = new[] { Math.Abs(c.X - old.X), Math.Abs(old.Right - c.X), Math.Abs(c.Y - old.Y), Math.Abs(old.Bottom - c.Y) };
                var m = d.Min();
                var p = m == d[0] ? new PointD(s.X, c.Y) : m == d[1] ? new PointD(s.X + s.W, c.Y) : m == d[2] ? new PointD(c.X, s.Y) : new PointD(c.X, s.Y + s.H);
                Geometry.SnapToBorder(cp, s, p);
            }
        }

        /// <summary>Stores the drawn size of each of <paramref name="s"/>'s regions, so that they can change one by one.</summary>
        public void KeepRegionSizes(Vertex s)
        {
            var horizontal = s.RegionLayout == RegionLayout.Horizontal;
            var rects = RectsOf(s);
            for (int i = 0; i < rects.Count; i++) s.Regions[i].Size = horizontal ? rects[i].Rect.W : rects[i].Rect.H;
        }

        /// <summary>
        /// Changes the size of region <paramref name="i"/> of <paramref name="s"/> by <paramref name="d"/> at its far end
        /// (bottom, or right side by side): the state changes with it and the later regions move with their contents.
        /// </summary>
        private void ResizeRegionEnd(Vertex s, int i, double d)
        {
            if (d == 0) return;
            KeepRegionSizes(s);
            s.Regions[i].Size += d;
            if (s.RegionLayout == RegionLayout.Horizontal)
            {
                s.W += d;
                ShiftRegions(s, i + 1, s.Regions.Count - 1, d, 0);
            }
            else
            {
                s.H += d;
                ShiftRegions(s, i + 1, s.Regions.Count - 1, 0, d);
            }
        }

        /// <summary>
        /// Grows region <paramref name="i"/> of <paramref name="s"/> on its sides (each amount >= 0): the state grows on the
        /// same sides, and what lies beyond the region in its state moves with that side.
        /// </summary>
        private void GrowRegion(Vertex s, int i, double left, double right, double top, double bottom)
        {
            var old = BoxOf(s);
            KeepRegionSizes(s);
            if (s.RegionLayout == RegionLayout.Horizontal)
            {
                s.Y -= top;
                s.H += top + bottom;
                if (left > 0)
                {
                    s.X -= left;
                    s.W += left;
                    s.Regions[i].Size += left;
                    ShiftRegions(s, 0, i - 1, -left, 0);
                }
                ResizeRegionEnd(s, i, right);
            }
            else
            {
                s.X -= left;
                s.W += left + right;
                if (top > 0)
                {
                    s.Y -= top;
                    s.H += top;
                    s.Regions[i].Size += top;
                    ShiftRegions(s, 0, i - 1, 0, -top);
                }
                ResizeRegionEnd(s, i, bottom);
            }
            FollowBorder(s, old);
            KeepHeader(s);
        }

        /// <summary>
        /// After a change of <paramref name="s"/>'s header height (its internal activities, or its width rewrapping
        /// them), moves its bottom edge by the difference: its regions keep their sizes and their contents move with
        /// them. Returns whether it changed.
        /// </summary>
        private bool KeepHeader(Vertex s)
        {
            if (s.Regions.Count == 0) return false;
            var h = HeaderOf(s);
            var known = _layoutHeader.TryGetValue(s.Id, out var before);
            _layoutHeader[s.Id] = h;
            if (!known || before == h) return false;
            var old = BoxOf(s);
            s.H += h - before;
            ShiftRegions(s, 0, s.Regions.Count - 1, 0, h - before);
            FollowBorder(s, old);
            return true;
        }

        /// <summary><see cref="KeepHeader"/> for every composite state, growing the states around those that grew.</summary>
        private void KeepHeaders()
        {
            foreach (var s in Model.Vertices.Where(v => v.Regions.Count > 0).OrderByDescending(Index.Depth).ToList())
            {
                var old = BoxOf(s);
                if (KeepHeader(s)) FitInRegion(s, old, false);
            }
        }

        /// <summary>
        /// Grows the region holding <paramref name="v"/> (with its state, and the states around it in turn) so that
        /// <paramref name="v"/> lies inside it with the margin. <paramref name="old"/> is its box before the change, or
        /// null for a new element: where it already came closer to the region's border, that smaller gap is kept.
        /// The growth is a multiple of the grid unless <paramref name="exact"/>.
        /// </summary>
        public void FitInRegion(Vertex v, RectD? old, bool exact)
        {
            if (Index.IsBorderVertex(v)) return;
            var s = RegionOwnerOf(v.Parent);
            if (s == null) return;
            var i = s.Regions.FindIndex(r => r.Id == v.Parent);
            var R = RectsOf(s)[i].Rect;
            double Gap(double g) => old.HasValue ? Math.Min(FitMargin, g) : FitMargin;
            var o = old ?? default(RectD);
            var gl = Gap(o.X - R.X);
            var gr = Gap(R.Right - o.Right);
            var gt = Gap(o.Y - R.Y);
            var gb = Gap(R.Bottom - o.Bottom);
            double Up(double n) => n <= 1e-6 ? 0 : exact ? n : Math.Ceiling(n / Geometry.Grid - 1e-6) * Geometry.Grid;
            var left = Up(R.X + gl - v.X);
            var right = Up(v.X + v.W + gr - R.Right);
            var top = Up(R.Y + gt - v.Y);
            var bottom = Up(v.Y + v.H + gb - R.Bottom);
            if (left == 0 && right == 0 && top == 0 && bottom == 0) return;
            var before = BoxOf(s);
            GrowRegion(s, i, left, right, top, bottom);
            FitInRegion(s, before, exact);
        }

        /// <summary>
        /// How far each edge of composite state <paramref name="s"/> can move inward without leaving any of its contents
        /// outside their region: the largest left and top, the smallest right and bottom. A gap already smaller than the
        /// margin is kept, and each region keeps at least <see cref="MinRegion"/>.
        /// </summary>
        private (double Left, double Right, double Top, double Bottom) ResizeLimits(Vertex s)
        {
            var rects = RectsOf(s).Select(r => r.Rect).ToList();
            var n = rects.Count;
            var boxes = s.Regions.Select(r => ContentsBox(r.Id)).ToList();
            var all = boxes.Where(b => b.HasValue).Select(b => b.Value).ToList();
            RectD? union = null;
            if (all.Count > 0)
            {
                var ux = all.Min(b => b.X);
                var uy = all.Min(b => b.Y);
                union = new RectD(ux, uy, all.Max(b => b.Right) - ux, all.Max(b => b.Bottom) - uy);
            }
            // The edge at `edge` may come up to `content` minus the margin, or stay where it is when already closer.
            double Lo(double content, double edge) => Math.Max(content - FitMargin, Math.Min(edge, content));
            double Hi(double content, double edge) => Math.Min(content + FitMargin, Math.Max(edge, content));
            var first = rects[0];
            var last = rects[n - 1];
            var header = first.Y - s.Y;
            double left = double.PositiveInfinity, right = double.NegativeInfinity, top, bottom;
            if (s.RegionLayout == RegionLayout.Horizontal)
            {
                left = first.Right - MinRegion;
                if (boxes[0] is RectD b0) left = Math.Min(left, Lo(b0.X, first.X));
                right = last.X + MinRegion;
                if (boxes[n - 1] is RectD bn) right = Math.Max(right, Hi(bn.Right, last.Right));
                top = s.Y + s.H - MinRegion - header;
                bottom = first.Y + MinRegion;
                if (union is RectD u)
                {
                    top = Math.Min(top, Lo(u.Y, first.Y) - header);
                    bottom = Math.Max(bottom, Hi(u.Bottom, s.Y + s.H));
                }
            }
            else
            {
                if (union is RectD u)
                {
                    left = Lo(u.X, s.X);
                    right = Hi(u.Right, s.X + s.W);
                }
                top = first.Bottom - MinRegion - header;
                if (boxes[0] is RectD b0) top = Math.Min(top, Lo(b0.Y, first.Y) - header);
                bottom = last.Y + MinRegion;
                if (boxes[n - 1] is RectD bn) bottom = Math.Max(bottom, Hi(bn.Bottom, last.Bottom));
            }
            return (left, right, top, bottom);
        }

        /// <summary>Smallest size a vertex can be resized to (a composite state's regions add their own limits).</summary>
        public (double W, double H) MinimumSize(Vertex v)
        {
            var bar = v.Type == VertexType.Fork || v.Type == VertexType.Join;
            var minW = bar ? 6 : 40;
            var minH = bar ? 6 : v.Type == VertexType.State ? Geometry.HeaderHeight(Model, v) + (v.Regions.Count > 0 ? 20 : 6) : 30;
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

        /// <summary>Starts dragging a resize handle of <paramref name="v"/> or the line after one of its regions: the layout to restart from.</summary>
        public LayoutSnapshot BeginLayoutDrag(Vertex v)
        {
            if (v.Regions.Count > 0) KeepRegionSizes(v);
            return Snapshot();
        }

        /// <summary>
        /// Resizes <paramref name="v"/> from the layout <paramref name="snap"/> (taken with its box <paramref name="x0"/>,
        /// <paramref name="y0"/>, <paramref name="w0"/>, <paramref name="h0"/>) by dragging <paramref name="corner"/> by
        /// <paramref name="dx"/>, <paramref name="dy"/>; snapped to the grid unless <paramref name="exact"/>. A composite
        /// state keeps its contents in their regions; side by side the left edge resizes the first region and stacked
        /// the top edge does; the region holding <paramref name="v"/> grows when it no longer holds it.
        /// </summary>
        public void ResizeFrom(LayoutSnapshot snap, Vertex v, string corner, double x0, double y0, double w0, double h0, double dx, double dy, bool exact)
        {
            Restore(snap);
            var start = BoxOf(v);
            var west = corner.Contains('w');
            var north = corner.Contains('n');
            var n = v.Regions.Count;
            var horizontal = v.RegionLayout == RegionLayout.Horizontal;
            var (minW, _) = MinimumSize(v);
            var lim = n > 0 ? ResizeLimits(v) : ((double, double, double, double)?)null;
            if (west)
            {
                // The right edge stays put.
                var right = x0 + w0;
                v.X = Math.Min(right - minW, exact ? x0 + dx : Geometry.Snap(x0 + dx));
                if (lim.HasValue) v.X = Math.Min(v.X, lim.Value.Item1);
                v.W = right - v.X;
                if (n > 1 && horizontal) v.Regions[0].Size -= v.X - x0;
            }
            else
            {
                v.W = Math.Max(minW, exact ? w0 + dx : Geometry.Snap(v.X + w0 + dx) - v.X);
                if (lim.HasValue) v.W = Math.Max(v.W, lim.Value.Item2 - v.X);
            }
            // After the width: the wrapped text depends on it.
            if (n > 0)
            {
                KeepHeader(v);
                lim = ResizeLimits(v);
            }
            var y1 = v.Y;
            var h1 = v.H;
            var (_, minH) = MinimumSize(v);
            if (north)
            {
                // The bottom edge stays put.
                var bottom = y1 + h1;
                v.Y = Math.Min(bottom - minH, exact ? y0 + dy : Geometry.Snap(y0 + dy));
                if (lim.HasValue) v.Y = Math.Min(v.Y, lim.Value.Item3);
                v.H = bottom - v.Y;
                if (n > 1 && !horizontal) v.Regions[0].Size -= v.Y - y1;
            }
            else
            {
                v.H = Math.Max(minH, exact ? h1 + dy : Geometry.Snap(v.Y + h1 + dy) - v.Y);
                if (lim.HasValue) v.H = Math.Max(v.H, lim.Value.Item4 - v.Y);
            }
            FollowBorder(v, start);
            FitInRegion(v, start, exact);
        }

        /// <summary>
        /// Drags the line after region <paramref name="i"/> of <paramref name="s"/> by <paramref name="delta"/> from the
        /// layout <paramref name="snap"/>: the region resizes (not below its contents or <see cref="MinRegion"/>), its state
        /// with it, and the later regions move with their contents.
        /// </summary>
        public void ResizeRegionFrom(LayoutSnapshot snap, Vertex s, int i, double delta, bool exact)
        {
            Restore(snap);
            var start = BoxOf(s);
            var horizontal = s.RegionLayout == RegionLayout.Horizontal;
            var r = RectsOf(s)[i].Rect;
            var from = horizontal ? r.X : r.Y;
            var end0 = horizontal ? r.Right : r.Bottom;
            var end = end0 + (exact ? delta : Geometry.Snap(delta));
            var min = from + MinRegion;
            if (ContentsBox(s.Regions[i].Id) is RectD c)
            {
                var far = horizontal ? c.Right : c.Bottom;
                min = Math.Max(min, Math.Min(far + FitMargin, Math.Max(end0, far)));
            }
            end = Math.Max(end, min);
            ResizeRegionEnd(s, i, end - end0);
            FollowBorder(s, start);
            KeepHeader(s);
            FitInRegion(s, start, exact);
        }

        /// <summary>
        /// Moves the dragged elements from the layout <paramref name="snap"/>: <paramref name="items"/> (the roots and their
        /// contents) by <paramref name="dx"/>, <paramref name="dy"/> with the bend points of <paramref name="transitions"/>.
        /// A connection point dragged alone stays on its state's border, under <paramref name="p"/>; other roots keep inside
        /// their region, which grows (<paramref name="boxes"/> are their boxes at the start).
        /// </summary>
        public void MoveFrom(LayoutSnapshot snap, IReadOnlyList<(Vertex V, double X0, double Y0)> items,
            IReadOnlyList<(Transition T, List<PointD> Points0)> transitions, IReadOnlyList<Vertex> roots,
            IReadOnlyDictionary<string, RectD> boxes, double dx, double dy, PointD p, bool exact)
        {
            Restore(snap);
            foreach (var (v, x0, y0) in items)
            {
                v.X = x0 + dx;
                v.Y = y0 + dy;
            }
            foreach (var (t, pts0) in transitions) t.Points = pts0.Select(q => new PointD(q.X + dx, q.Y + dy)).ToList();
            var lead = roots.FirstOrDefault();
            if (roots.Count == 1 && Index.IsBorderVertex(lead))
            {
                var s = Index.Vertex(lead.Parent);
                if (s != null) Geometry.SnapToBorder(lead, s, p);
                return;
            }
            foreach (var r in roots) FitInRegion(r, boxes.TryGetValue(r.Id, out var b) ? b : (RectD?)null, exact);
        }

        /// <summary>
        /// Region a moved element is dropped into: the deepest one holding its whole box, when that is its own region
        /// or lies inside it (an element never leaves its region by a drag); null to keep its region.
        /// </summary>
        public RegionRect DropRegionOf(Vertex v, ISet<string> moving)
        {
            var r = Geometry.RegionHolding(Index, BoxOf(v), moving);
            return r != null && r.Id != v.Parent && Geometry.RegionWithin(Index, r.Id, v.Parent) ? r : null;
        }

        /// <summary>Region to highlight while <paramref name="lead"/> is dragged: where it would be dropped, else its own region.</summary>
        public RegionRect DropHighlight(Vertex lead, ISet<string> moving)
        {
            var r = DropRegionOf(lead, moving);
            if (r != null) return r;
            var s = RegionOwnerOf(lead.Parent);
            return s != null ? RectsOf(s).FirstOrDefault(x => x.Id == lead.Parent) : null;
        }

        /// <summary>
        /// Switches <paramref name="s"/> between stacked and side-by-side regions: they share it equally again, any region
        /// too small for its contents grows (with the state), and each region's contents move with it.
        /// </summary>
        public void SetRegionLayout(Vertex s, RegionLayout layout)
        {
            if (s.RegionLayout == layout) return;
            var old = BoxOf(s);
            var before = RectsOf(s).Select(r => r.Rect).ToList();
            // Each region's contents, relative to the region's top-left corner.
            var rel = s.Regions.Select((r, i) => ContentsBox(r.Id) is RectD b ? (Right: b.Right - before[i].X, Bottom: b.Bottom - before[i].Y) : (Right: 0.0, Bottom: 0.0)).ToList();
            var n = before.Count;
            var body = s.H - (before[0].Y - s.Y);
            s.RegionLayout = layout;
            foreach (var r in s.Regions) r.Size = null;
            if (layout == RegionLayout.Horizontal)
            {
                var sizes = rel.Select(c => Math.Max(Math.Max(s.W / n, c.Right + FitMargin), MinRegion)).ToList();
                s.W = sizes.Sum();
                for (int i = 0; i < n; i++) s.Regions[i].Size = sizes[i];
                s.H = HeaderOf(s) + Math.Max(body, rel.Max(c => c.Bottom + FitMargin));
            }
            else
            {
                s.W = Math.Max(s.W, rel.Max(c => c.Right + FitMargin));
                var sizes = rel.Select(c => Math.Max(Math.Max(body / n, c.Bottom + FitMargin), MinRegion)).ToList();
                for (int i = 0; i < n; i++) s.Regions[i].Size = sizes[i];
                s.H = HeaderOf(s) + sizes.Sum();
            }
            _layoutHeader[s.Id] = HeaderOf(s);
            var after = RectsOf(s).Select(r => r.Rect).ToList();
            for (int i = 0; i < n; i++) ShiftVertices(RegionContents(s.Regions[i].Id), after[i].X - before[i].X, after[i].Y - before[i].Y);
            FollowBorder(s, old);
            FitInRegion(s, old, false);
            Commit();
        }

        /// <summary>Warnings for elements whose boxes overlap, other than a state and what it contains; comments don't count.</summary>
        public List<Issue> OverlapIssues()
        {
            var output = new List<Issue>();
            var vs = Model.Vertices.Where(v => v.Type != VertexType.Comment).ToList();
            string Describe(Vertex v) => v.Name.Length > 0 ? $"'{v.Name}'" : $"an unnamed {v.Type.Title()}";
            for (int i = 0; i < vs.Count; i++)
            {
                var a = vs[i];
                for (int j = i + 1; j < vs.Count; j++)
                {
                    var b = vs[j];
                    if (!(a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H)) continue;
                    if (Index.IsInside(a, b.Id) || Index.IsInside(b, a.Id)) continue;
                    output.Add(new Issue(a.Id, Severity.Warning, $"Overlaps {Describe(b)}."));
                    output.Add(new Issue(b.Id, Severity.Warning, $"Overlaps {Describe(a)}."));
                }
            }
            return output;
        }

        /// <summary>The selected elements (without a selected ancestor) when they all lie in a composite state's region.</summary>
        private List<Vertex> LeavingElements()
        {
            var roots = SelectionRoots();
            return roots.Count > 0 && roots.All(v => !Index.IsBorderVertex(v) && RegionOwnerOf(v.Parent) != null) ? roots : new List<Vertex>();
        }

        public bool CanLeaveParent => LeavingElements().Count > 0;

        /// <summary>Moves the selected elements up one level, out of their composite state, without moving them on the diagram.</summary>
        public void LeaveParent()
        {
            var roots = LeavingElements();
            if (roots.Count == 0) return;
            foreach (var v in roots) v.Parent = RegionOwnerOf(v.Parent).Parent;
            Commit();
        }

        /// <summary>
        /// Aligns the selected states (without a selected ancestor) with <paramref name="anchorId"/>: on its
        /// horizontal center line when <paramref name="horizontally"/>, else on its vertical one. Each state
        /// moves with its contents and stays in its region; the enclosing composite states grow so that their
        /// regions still hold their states. Returns false, changing nothing, when fewer than two states are
        /// selected or when states of different regions of one state would overlap along its region layout.
        /// </summary>
        public bool Align(string anchorId, bool horizontally)
        {
            var states = SelectionRoots().Where(v => v.Type == VertexType.State).ToList();
            var clicked = Index.Vertex(anchorId);
            if (clicked == null) return false;
            var anchor = Index.Ancestors(clicked).LastOrDefault(a => Selection.Contains(a.Id)) ?? clicked;
            if (states.Count < 2 || !states.Contains(anchor)) return false;

            var snap = Snapshot();
            var moved = new List<(Vertex V, RectD Old)>();
            foreach (var v in states)
            {
                var dx = horizontally ? 0 : anchor.CenterX - v.CenterX;
                var dy = horizontally ? anchor.CenterY - v.CenterY : 0;
                if (dx == 0 && dy == 0) continue;
                moved.Add((v, BoxOf(v)));
                ShiftVertices(new HashSet<string>(new[] { v }.Concat(Index.Descendants(v)).Select(x => x.Id)), dx, dy);
            }
            if (moved.Count == 0) return false;

            // Innermost first: a state that grows may no longer fit in its own region.
            foreach (var (v, old) in moved.OrderByDescending(m => Index.Depth(m.V)).ToList()) FitInRegion(v, old, true);
            double Along(Vertex v) => horizontally ? v.CenterY : v.CenterX;
            var off = states.FirstOrDefault(v => Math.Abs(Along(v) - Along(anchor)) > 0.5);
            if (off != null)
            {
                // Growing one region moved the others: states in different regions of one state can't line up.
                Restore(snap);
                var shared = Index.Ancestors(off).FirstOrDefault(a => Index.IsInside(anchor, a.Id));
                Say($"The states cannot be aligned: they would leave their regions of '{shared?.Name ?? ""}'.");
                return false;
            }
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
                var r = DropRegionOf(v, moving);
                if (r != null) v.Parent = r.Id;
            }
            Commit();
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

        /// <summary>
        /// Adds a copy of <paramref name="data"/>. Without <paramref name="at"/> (keyboard paste, duplicate), each element
        /// goes into its original's region when there is one, offset further at each repeat; with <paramref name="at"/>
        /// (context menu), the copy's top-left corner goes there, into the region under it. Regions grow to hold the copy.
        /// </summary>
        public void Paste(ClipboardFragment data, PointD? at = null)
        {
            if (data == null || data.Vertices.Count == 0) return;
            Reindex();
            var map = new Dictionary<string, string>();
            foreach (var v in data.Vertices)
            {
                map[v.Id] = NewId("v");
                foreach (var r in v.Regions) map[r.Id] = NewId("r");
            }
            double offX, offY;
            RegionRect target = null;
            if (at is PointD point)
            {
                var roots = data.Vertices.Where(v => !map.ContainsKey(v.Parent)).ToList();
                offX = Geometry.Snap(point.X) - roots.Min(v => v.X);
                offY = Geometry.Snap(point.Y) - roots.Min(v => v.Y);
                target = Geometry.RegionAt(Index, point, new HashSet<string>());
            }
            else
            {
                _pasteCount++;
                offX = offY = 20 * _pasteCount;
            }
            var added = new List<Vertex>();
            foreach (var src in data.Vertices)
            {
                var v = src.Clone();
                v.Id = map[src.Id];
                v.X += offX;
                v.Y += offY;
                v.Regions = v.Regions.Select(r => new Region { Id = map[r.Id], Name = r.Name, Size = r.Size }).ToList();
                if (map.TryGetValue(v.Parent, out var parent))
                {
                    v.Parent = parent;
                }
                else if (Index.IsBorderVertex(v))
                {
                    // Pasted without its state: on that state's border when it still exists.
                    var state = Index.Vertex(v.Parent);
                    if (state == null) continue;
                    if (at.HasValue) Geometry.SnapToBorder(v, state, v.Center);
                }
                else if (at.HasValue && v.Type != VertexType.EntryPoint && v.Type != VertexType.ExitPoint)
                {
                    v.Parent = target != null ? target.Id : RootRegion;
                }
                else if (at.HasValue || !Index.IsRegion(v.Parent))
                {
                    v.Parent = RootRegion;
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
                t.Points = t.Points.Select(p => new PointD(p.X + offX, p.Y + offY)).ToList();
                Model.Transitions.Add(t);
            }
            Reindex();
            var addedIds = new HashSet<string>(added.Select(v => v.Id));
            var pastedRoots = added.Where(v => !Index.Ancestors(v).Any(a => addedIds.Contains(a.Id))).ToList();
            foreach (var v in pastedRoots) FitInRegion(v, null, false);
            Selection = new HashSet<string>(pastedRoots.Select(v => v.Id));
            Commit();
        }
    }
}
