using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FsmEditor.Core
{
    public sealed class XmiException : Exception
    {
        public XmiException(string message) : base(message) { }
    }

    /// <summary>
    /// Reads and writes state machine files: XMI 2.5.1 holding a UML 2.5.1 model,
    /// plus a UML DI diagram (umldi:UMLStateMachineDiagram) with the layout.
    ///
    /// Layout mapping:
    /// - every vertex, region and comment has a UMLShape with dc:Bounds;
    /// - every drawn transition has a UMLEdge whose waypoints are the centers of
    ///   its source and target with the bend points in between, plus an optional
    ///   UMLLabel whose center is the middle of those waypoints moved by the
    ///   label offset;
    /// - the region layout of an orthogonal state follows from its region shapes;
    /// - coordinates are rounded to 0.1 px, and what is derived from them (centers,
    ///   region shapes, the middle of an edge) is computed from the rounded values,
    ///   the region sizes as the file holds them: reading a written file and writing
    ///   it again gives the same text;
    /// - the diagram lists the shapes, edges and styles in the order reading the
    ///   model gives its elements (document order), each element written once;
    /// - colors and groups are kept in one xmi:Extension at the end of the diagram,
    ///   with a &lt;style element="id" color="#rrggbb" entry="#rrggbb" …/&gt; per colored
    ///   vertex or transition (UML DI has no color), then a
    ///   &lt;group id="g" members="id id …"/&gt; per group of vertices (members in file
    ///   order, groups in the order of their first member).
    ///
    /// Files written by earlier versions end with the model hash, in its own
    /// xmi:Extension (&lt;modelHash value="…"/&gt;): the reader ignores it, and the
    /// writer no longer writes it.
    ///
    /// <c>ToXmi(FromXmi(x))</c> round-trips without loss (coordinates are rounded to 0.1 px),
    /// and the output is identical to the one of FSM Editor for VS Code.
    /// </summary>
    public static class Xmi
    {
        public const string NsXmi = "http://www.omg.org/spec/XMI/20131001";
        public const string NsUml = "http://www.omg.org/spec/UML/20161101";
        public const string NsUmlDi = "http://www.omg.org/spec/UML/20161101/UMLDI";
        public const string NsDc = "http://www.omg.org/spec/DD/20100524/DC";
        public const string NsDi = "http://www.omg.org/spec/DD/20100524/DI";
        private const string Extender = "FSM Editor";

        private const string ProfileName = "FsmStereotypes";
        private const string ProfileNs = "https://github.com/vincedupuis/fsm-editor/profiles/FsmStereotypes";
        private const string UmlState = NsUml + "/UML.xmi#State";
        /// <summary>Language of the opaque behaviors and expressions (argument-less calls, see <see cref="Expressions"/>).</summary>
        private const string Language = "FSM";

        private const double LabelW = 100;
        private const double LabelH = 16;

        private static bool IsPseudoKind(VertexType t) => t.IsPseudostate();

        /// <summary>
        /// Point halfway along the polyline, for transition labels, computed as polylineMid of xmi.ts does (its own
        /// formula and JavaScript's Math.hypot, not <see cref="Geometry.PolylineMid"/>), so the file stays byte-identical.
        /// </summary>
        private static PointD LabelMid(IReadOnlyList<PointD> pts)
        {
            double total = 0;
            for (int i = 1; i < pts.Count; i++) total += Num.Hypot(pts[i].X - pts[i - 1].X, pts[i].Y - pts[i - 1].Y);
            var half = total / 2;
            for (int i = 1; i < pts.Count; i++)
            {
                var a = pts[i - 1];
                var b = pts[i];
                var len = Num.Hypot(b.X - a.X, b.Y - a.Y);
                if (len >= half && len > 0) return new PointD(a.X + (b.X - a.X) * half / len, a.Y + (b.Y - a.Y) * half / len);
                half -= len;
            }
            return pts[0];
        }

        /// <summary>
        /// The region sizes a file holds: <see cref="Geometry.RegionSizes"/> with the sizes rounded to 0.1 px as they are
        /// written, so that the sizes read back from the region shapes give the same regions (equal parts of 10 px in three
        /// would otherwise be read back as 3.3, 3.3 and the rest, and move on the next save).
        /// </summary>
        private static List<double> FileRegionSizes(IReadOnlyList<Region> regs, double total)
        {
            List<double> Rounded(List<double> sizes) => Geometry.RegionSizes(sizes.Select(x => new Region { Size = Num.R1(x) }).ToList(), total);
            return Rounded(Rounded(Geometry.RegionSizes(regs, total)));
        }

        /// <summary>The region shapes of a state: <see cref="Geometry.RegionRects"/> with the estimated text width and the region sizes a file holds.</summary>
        private static List<RegionRect> RegionShapes(FsmModel model, Vertex v)
        {
            var regs = v.Regions;
            var output = new List<RegionRect>();
            if (regs.Count == 0) return output;
            var top = v.Y + Math.Min(Geometry.HeaderHeight(model, v, Geometry.EstimateLabelWidth), v.H - 20);
            var bh = v.Y + v.H - top;
            var horizontal = v.RegionLayout == RegionLayout.Horizontal;
            var sizes = FileRegionSizes(regs, horizontal ? v.W : bh);
            var at = horizontal ? v.X : top;
            for (int i = 0; i < regs.Count; i++)
            {
                var rect = horizontal ? new RectD(at, top, sizes[i], bh) : new RectD(v.X, at, v.W, sizes[i]);
                at += sizes[i];
                output.Add(new RegionRect { Id = regs[i].Id, Name = regs[i].Name, Rect = rect });
            }
            return output;
        }

        // ------------------------------------------------------------------ writing

        /// <summary>
        /// Writes a state machine file. Reading it back and writing it again gives the same text. When ids are used
        /// twice (files edited by hand), reading gives a shape, bend points or a color to the last element with that id
        /// and default places to the others, so what reading gives is written again, until the text no longer changes.
        /// </summary>
        public static string ToXmi(FsmModel source)
        {
            var text = WriteModel(source);
            if (!SharesIds(source)) return text;
            for (int i = 0; i < 10; i++)
            {
                var again = WriteModel(FromXmi(text));
                if (again == text) break;
                text = again;
            }
            return text;
        }

        /// <summary>Whether vertices, transitions and regions use some id more than once.</summary>
        private static bool SharesIds(FsmModel model)
        {
            var ids = new HashSet<string>();
            int n = 0;
            void Add(string id)
            {
                ids.Add(id ?? "");
                n++;
            }
            foreach (var r in model.Regions ?? new List<Region>()) Add(r.Id);
            foreach (var v in model.Vertices ?? new List<Vertex>())
            {
                Add(v.Id);
                foreach (var r in v.Regions ?? new List<Region>()) Add(r.Id);
            }
            foreach (var t in model.Transitions ?? new List<Transition>()) Add(t.Id);
            return ids.Count != n;
        }

        /// <summary>Zero, or NaN (JavaScript's falsy numbers).</summary>
        private static bool Falsy(double n) => n == 0 || double.IsNaN(n);

        private static string WriteModel(FsmModel source)
        {
            var m = source.Clone().Normalize();
            // Bounds as the file holds them, so that what is derived from them (centers, header lines, region shapes)
            // comes out the same when the file is read back and written again. A size of 0 is read back as the
            // default size: it is written as that.
            foreach (var v in m.Vertices)
            {
                v.X = Num.R1(v.X);
                v.Y = Num.R1(v.Y);
                v.W = Num.R1(v.W);
                v.H = Num.R1(v.H);
                if (Falsy(v.W) || Falsy(v.H)) (v.W, v.H) = v.Type.DefaultSize();
            }
            var ix = new ModelIndex(m);
            var topRegions = new HashSet<string>(m.Regions.Select(r => r.Id));
            var protocol = m.Kind == MachineKind.Protocol;
            var smId = m.Id;

            bool IsOnBorder(Vertex v) => ix.IsBorderVertex(v);
            bool IsMachinePoint(Vertex v) => (v.Type == VertexType.EntryPoint || v.Type == VertexType.ExitPoint) && topRegions.Contains(v.Parent);

            // Events: one signal event (and signal) per event name, one time event per time trigger.
            var events = new Dictionary<string, string>();
            var eventElements = new List<XNode>();
            string EventFor(string trigger)
            {
                if (events.TryGetValue(trigger, out var id)) return id;
                var time = Regex.Match(trigger, @"^after\((.+)\)$");
                var safe = Regex.Replace(trigger, "[^A-Za-z0-9_]", "_");
                // after(1s) and an event named after_1s_ would share an id: the later one gets another _.
                var taken = new HashSet<string>(events.Values);
                while (taken.Contains("ev_" + safe)) safe += "_";
                id = "ev_" + safe;
                if (time.Success)
                {
                    eventElements.Add(new XNode("packagedElement", ("xmi:type", "uml:TimeEvent"), ("xmi:id", id), ("name", trigger), ("isRelative", "true")).Add(
                        new XNode("when", ("xmi:type", "uml:TimeExpression"), ("xmi:id", id + "_when")).Add(
                            new XNode("expr", ("xmi:type", "uml:LiteralString"), ("xmi:id", id + "_expr"), ("value", time.Groups[1].Value)))));
                }
                else
                {
                    eventElements.Add(new XNode("packagedElement", ("xmi:type", "uml:SignalEvent"), ("xmi:id", id), ("name", trigger), ("signal", "sig_" + safe)));
                    eventElements.Add(new XNode("packagedElement", ("xmi:type", "uml:Signal"), ("xmi:id", "sig_" + safe), ("name", trigger)));
                }
                events[trigger] = id;
                return id;
            }

            IEnumerable<XNode> Behavior(string tag, string id, string body) =>
                string.IsNullOrEmpty(body)
                    ? Enumerable.Empty<XNode>()
                    : new[] { new XNode(tag, ("xmi:type", "uml:OpaqueBehavior"), ("xmi:id", id)).Add(new XNode("language").Add(Language), new XNode("body").Add(body)) };

            IEnumerable<XNode> Constraint(string tag, string id, string body) =>
                string.IsNullOrEmpty(body)
                    ? Enumerable.Empty<XNode>()
                    : new[]
                    {
                        new XNode(tag, ("xmi:type", "uml:Constraint"), ("xmi:id", id)).Add(
                            new XNode("specification", ("xmi:type", "uml:OpaqueExpression"), ("xmi:id", id + "_spec")).Add(
                                new XNode("language").Add(Language), new XNode("body").Add(body))),
                    };

            IEnumerable<XNode> Triggers(string tag, string ownerId, List<string> list) =>
                list.Select((t, i) => new XNode(tag, ("xmi:type", "uml:Trigger"), ("xmi:id", $"{ownerId}_{tag}{i + 1}"), ("name", t), ("event", EventFor(t)))).ToList();

            // Regions enclosing a vertex, innermost first.
            List<string> RegionChain(Vertex v)
            {
                var output = new List<string>();
                var seen = new HashSet<string>();
                var cur = v;
                while (cur != null && seen.Add(cur.Id))
                {
                    if (IsOnBorder(cur))
                    {
                        cur = ix.Vertex(cur.Parent);
                        continue;
                    }
                    output.Add(cur.Parent);
                    cur = ix.RegionOwner.TryGetValue(cur.Parent, out var o) ? o : null;
                }
                return output;
            }

            // A transition is owned by the innermost region that contains both of its ends.
            var transitionsByRegion = new Dictionary<string, List<Transition>>();
            foreach (var t in m.Transitions)
            {
                var s = ix.Vertex(t.Source);
                var g = ix.Vertex(t.Target);
                var sc = s != null ? RegionChain(s) : new List<string>();
                var gc = new HashSet<string>(g != null ? RegionChain(g) : new List<string>());
                var container = sc.FirstOrDefault(gc.Contains) ?? m.Regions[0].Id;
                if (!transitionsByRegion.TryGetValue(container, out var list)) transitionsByRegion[container] = list = new List<Transition>();
                list.Add(t);
            }

            XNode TransitionNode(Transition t) =>
                new XNode("transition",
                    ("xmi:type", protocol ? "uml:ProtocolTransition" : "uml:Transition"),
                    ("xmi:id", t.Id),
                    ("kind", t.Kind.ToString().ToLowerInvariant()),
                    ("source", t.Source),
                    ("target", t.Target))
                .Add(Triggers("trigger", t.Id, t.Triggers))
                .Add(protocol
                    ? Constraint("preCondition", t.Id + "_pre", t.Precondition).Concat(Constraint("postCondition", t.Id + "_post", t.Postcondition))
                    : Constraint("guard", t.Id + "_guard", t.Guard))
                .Add(Behavior("effect", t.Id + "_effect", t.Effect));

            XNode CommentNode(Vertex c) =>
                new XNode("ownedComment", ("xmi:type", "uml:Comment"), ("xmi:id", c.Id), ("annotatedElement", string.Join(" ", c.Anchors)))
                    .Add(new XNode("body").Add(c.Text));

            XNode PointNode(string tag, Vertex v) =>
                new XNode(tag, ("xmi:type", "uml:Pseudostate"), ("xmi:id", v.Id), ("name", v.Name), ("kind", v.Type.Key()));

            // Each vertex and transition is written once, in the first place that holds it (two states with one id,
            // or a region id used again, would otherwise write it twice, and the file would grow on each save).
            // docVertices and docTransitions are what the document holds, in the order reading it gives them.
            var writtenVertices = new HashSet<Vertex>();
            var writtenTransitions = new HashSet<Transition>();
            var docVertices = new List<Vertex>();
            var docTransitions = new List<Transition>();
            // States on the way down: a region id used again (a file edited by hand) can make a state its own descendant.
            var writing = new HashSet<Vertex>();

            XNode RegionNode(Region r)
            {
                var kids = m.Vertices.Where(v => v.Parent == r.Id && !IsMachinePoint(v)).ToList();
                var comments = kids.Where(v => v.Type == VertexType.Comment && !writtenVertices.Contains(v)).ToList();
                foreach (var c in comments)
                {
                    writtenVertices.Add(c);
                    docVertices.Add(c);
                }
                var vertices = new List<XNode>();
                foreach (var v in kids)
                {
                    if (v.Type == VertexType.Comment) continue;
                    // Failing like the VS Code edition (which runs out of stack), instead of overflowing it, which ends the process.
                    if (writing.Contains(v)) throw new XmiException($"State '{v.Id}' is inside one of its own regions.");
                    if (!writtenVertices.Contains(v)) vertices.Add(VertexNode(v));
                }
                var transitions = (transitionsByRegion.TryGetValue(r.Id, out var ts) ? ts : new List<Transition>()).Where(t => !writtenTransitions.Contains(t)).ToList();
                foreach (var t in transitions)
                {
                    writtenTransitions.Add(t);
                    docTransitions.Add(t);
                }
                return new XNode("region", ("xmi:type", "uml:Region"), ("xmi:id", r.Id), ("name", r.Name))
                    .Add(comments.Select(CommentNode).ToList())
                    .Add(vertices)
                    .Add(transitions.Select(TransitionNode).ToList());
            }

            XNode VertexNode(Vertex v)
            {
                writtenVertices.Add(v);
                docVertices.Add(v);
                if (v.Type == VertexType.Final) return new XNode("subvertex", ("xmi:type", "uml:FinalState"), ("xmi:id", v.Id), ("name", v.Name));
                if (IsPseudoKind(v.Type)) return PointNode("subvertex", v);
                var node = new XNode("subvertex", ("xmi:type", "uml:State"), ("xmi:id", v.Id), ("name", v.Name))
                    .Add(Behavior("entry", v.Id + "_entry", v.Entry))
                    .Add(Behavior("exit", v.Id + "_exit", v.Exit))
                    .Add(Behavior("doActivity", v.Id + "_do", v.DoActivity))
                    .Add(Constraint("stateInvariant", v.Id + "_inv", v.Invariant))
                    .Add(Triggers("deferrableTrigger", v.Id, v.Deferrable));
                // Read back: the connection points, then the connection point references.
                var points = new List<Vertex>();
                var references = new List<Vertex>();
                foreach (var cp in m.Vertices)
                {
                    if (cp.Parent != v.Id || writtenVertices.Contains(cp)) continue;
                    if (cp.Type == VertexType.EntryPoint || cp.Type == VertexType.ExitPoint)
                    {
                        writtenVertices.Add(cp);
                        points.Add(cp);
                        node.Add(PointNode("connectionPoint", cp));
                    }
                    if (cp.Type == VertexType.ConnectionPointRef)
                    {
                        writtenVertices.Add(cp);
                        references.Add(cp);
                        var file = v.Submachine.Split('#')[0];
                        node.Add(new XNode("connection", ("xmi:type", "uml:ConnectionPointReference"), ("xmi:id", cp.Id)).Add(
                            new XNode(cp.PointKind == PointKind.Exit ? "exit" : "entry", ("href", $"{file}#{cp.Ref}"))));
                    }
                }
                docVertices.AddRange(points);
                docVertices.AddRange(references);
                writing.Add(v);
                foreach (var r in v.Regions) node.Add(RegionNode(r));
                writing.Remove(v);
                if (v.Submachine.Length > 0) node.Add(new XNode("submachine", ("href", v.Submachine)));
                return node;
            }

            var machine = new XNode("packagedElement",
                ("xmi:type", protocol ? "uml:ProtocolStateMachine" : "uml:StateMachine"), ("xmi:id", smId), ("name", m.Name));
            if (m.Documentation.Length > 0)
            {
                machine.Add(new XNode("ownedComment", ("xmi:type", "uml:Comment"), ("xmi:id", smId + "_doc"), ("annotatedElement", smId))
                    .Add(new XNode("body").Add(m.Documentation)));
            }
            // The machine's own connection points come first in the file, but are read after its regions.
            var regionNodes = m.Regions.Select(RegionNode).ToList();
            var machinePoints = m.Vertices.Where(v => IsMachinePoint(v) && !writtenVertices.Contains(v)).ToList();
            foreach (var v in machinePoints)
            {
                writtenVertices.Add(v);
                docVertices.Add(v);
            }
            machine.Add(machinePoints.Select(v => PointNode("connectionPoint", v)).ToList());
            machine.Add(regionNodes);

            var packaged = new List<XNode>();
            if (m.Context.Length > 0)
            {
                // The context classifier owns the state machine as its classifier behavior.
                machine.Name = "ownedBehavior";
                packaged.Add(new XNode("packagedElement", ("xmi:type", "uml:Class"), ("xmi:id", smId + "_context"), ("name", m.Context), ("classifierBehavior", smId)).Add(machine));
            }
            else
            {
                packaged.Add(machine);
            }
            // Events are collected while building the machine, so they come after it.
            packaged.AddRange(eventElements);

            // Stereotypes: an embedded profile with one stereotype per name, extending UML::State.
            var stereotyped = docVertices.Where(v => v.Type == VertexType.State && v.Stereotype.Length > 0).ToList();
            var stereoNames = stereotyped.Select(v => v.Stereotype).Distinct().ToList();
            var profileApplication = new List<XNode>();
            var applications = new List<XNode>();
            if (stereoNames.Count > 0)
            {
                var profile = new XNode("packagedElement", ("xmi:type", "uml:Profile"), ("xmi:id", "fsm_profile"), ("name", ProfileName), ("URI", ProfileNs)).Add(
                    new XNode("metaclassReference", ("xmi:type", "uml:ElementImport"), ("xmi:id", "fsm_profile_state")).Add(
                        new XNode("importedElement", ("xmi:type", "uml:Class"), ("href", UmlState))));
                foreach (var s in stereoNames)
                {
                    profile.Add(
                        new XNode("packagedElement", ("xmi:type", "uml:Stereotype"), ("xmi:id", $"st_{s}"), ("name", s)).Add(
                            new XNode("ownedAttribute", ("xmi:type", "uml:Property"), ("xmi:id", $"st_{s}_base"), ("name", "base_State"), ("association", $"ext_{s}")).Add(
                                new XNode("type", ("href", UmlState)))),
                        new XNode("packagedElement", ("xmi:type", "uml:Extension"), ("xmi:id", $"ext_{s}"), ("name", $"E_{s}_State"), ("memberEnd", $"st_{s}_base ext_{s}_end")).Add(
                            new XNode("ownedEnd", ("xmi:type", "uml:ExtensionEnd"), ("xmi:id", $"ext_{s}_end"), ("name", $"extension_{s}"), ("type", $"st_{s}"), ("association", $"ext_{s}"), ("aggregation", "composite"))));
                }
                packaged.Add(profile);
                profileApplication.Add(new XNode("profileApplication", ("xmi:type", "uml:ProfileApplication"), ("xmi:id", "fsm_profile_app"), ("appliedProfile", "fsm_profile")));
                foreach (var v in stereotyped)
                {
                    applications.Add(new XNode($"{ProfileName}:{v.Stereotype}", ("xmi:id", v.Id + "_st"), ("base_State", v.Id)));
                }
            }

            // Diagram: what the document holds, in the order reading it gives (a model in another order, or holding
            // elements the document doesn't, would otherwise be written differently once read back).
            var held = new FsmModel { Id = m.Id, Name = m.Name, Kind = m.Kind, Context = m.Context, Documentation = m.Documentation, Regions = m.Regions, Vertices = docVertices, Transitions = docTransitions };
            var docById = new Dictionary<string, Vertex>();
            foreach (var v in docVertices) docById[v.Id] = v;
            var docIds = new HashSet<string>(docVertices.Select(v => v.Id).Concat(docTransitions.Select(t => t.Id)));
            XNode Bounds(double x, double y, double w, double h) =>
                new XNode("bounds", ("xmi:type", "dc:Bounds"), ("x", Num.R1(x)), ("y", Num.R1(y)), ("width", Num.R1(w)), ("height", Num.R1(h)));
            var shapes = new List<XNode>();
            foreach (var v in docVertices)
            {
                shapes.Add(new XNode("ownedElement", ("xmi:type", "umldi:UMLShape"), ("xmi:id", "di_" + v.Id), ("modelElement", v.Id)).Add(Bounds(v.X, v.Y, v.W, v.H)));
                if (v.Type == VertexType.State)
                {
                    foreach (var rb in RegionShapes(held, v))
                    {
                        shapes.Add(new XNode("ownedElement", ("xmi:type", "umldi:UMLShape"), ("xmi:id", "di_" + rb.Id), ("modelElement", rb.Id))
                            .Add(Bounds(rb.Rect.X, rb.Rect.Y, rb.Rect.W, rb.Rect.H)));
                    }
                }
            }
            var edges = new List<XNode>();
            foreach (var t in docTransitions)
            {
                var s = docById.TryGetValue(t.Source ?? "", out var sv) ? sv : null;
                var g = docById.TryGetValue(t.Target ?? "", out var gv) ? gv : null;
                if (s == null || g == null) continue;
                if (t.Kind == TransitionKind.Internal && s == g && s.Type == VertexType.State) continue; // drawn in the state's compartment
                // The waypoints as written: the label's middle is measured on them, as reading does.
                var pts = new List<PointD> { s.Center };
                pts.AddRange(t.Points);
                pts.Add(g.Center);
                pts = pts.Select(p => new PointD(Num.R1(p.X), Num.R1(p.Y))).ToList();
                var edge = new XNode("ownedElement", ("xmi:type", "umldi:UMLEdge"), ("xmi:id", "di_" + t.Id), ("modelElement", t.Id), ("source", "di_" + s.Id), ("target", "di_" + g.Id))
                    .Add(pts.Select(p => new XNode("waypoint", ("xmi:type", "dc:Point"), ("x", p.X), ("y", p.Y))).ToList());
                if (t.LabelOffset is PointD off)
                {
                    var mid = LabelMid(pts);
                    var cx = mid.X + off.X;
                    var cy = mid.Y + off.Y;
                    edge.Add(new XNode("ownedElement", ("xmi:type", "umldi:UMLLabel"), ("xmi:id", $"di_{t.Id}_label"), ("modelElement", t.Id))
                        .Add(Bounds(cx - LabelW / 2, cy - LabelH / 2, LabelW, LabelH)));
                }
                edges.Add(edge);
            }
            foreach (var c in docVertices.Where(v => v.Type == VertexType.Comment))
            {
                for (int i = 0; i < c.Anchors.Count; i++)
                {
                    var a = c.Anchors[i];
                    if (a == null || !docIds.Contains(a)) continue;
                    edges.Add(new XNode("ownedElement", ("xmi:type", "umldi:UMLEdge"), ("xmi:id", $"di_{c.Id}_anchor{i + 1}"), ("modelElement", c.Id), ("source", "di_" + c.Id), ("target", "di_" + a)));
                }
            }
            var styles = new List<XNode>();
            void AddStyle(string id, string color, Dictionary<string, string> items)
            {
                var style = new XNode("style", ("element", id));
                if (ElementColors.IsColor(color)) style.Attrs.Add(("color", color));
                foreach (var k in ElementColors.ItemKeys)
                {
                    if (items.TryGetValue(k, out var c) && ElementColors.IsColor(c)) style.Attrs.Add((k, c));
                }
                if (style.Attrs.Count > 1) styles.Add(style);
            }
            foreach (var v in docVertices) AddStyle(v.Id, v.Color, v.ItemColors);
            foreach (var t in docTransitions) AddStyle(t.Id, t.Color, t.ItemColors);
            var groups = new List<(string Id, List<string> Members)>();
            foreach (var v in docVertices)
            {
                if (v.Group.Length == 0) continue;
                var g = groups.FindIndex(gr => gr.Id == v.Group);
                if (g < 0) groups.Add((v.Group, new List<string> { v.Id }));
                else groups[g].Members.Add(v.Id);
            }
            foreach (var (id, members) in groups) styles.Add(new XNode("group", ("id", id), ("members", string.Join(" ", members))));
            var diagram = new XNode("umldi:UMLStateMachineDiagram", ("xmi:id", smId + "_diagram"), ("name", m.Name), ("modelElement", smId), ("isFrame", "false"))
                .Add(shapes, edges);
            if (styles.Count > 0) diagram.Add(new XNode("xmi:Extension", ("extender", Extender)).Add(styles));

            var root = new XNode("xmi:XMI",
                ("xmi:version", "20131001"),
                ("xmlns:xmi", NsXmi),
                ("xmlns:uml", NsUml),
                ("xmlns:umldi", NsUmlDi),
                ("xmlns:dc", NsDc),
                ("xmlns:di", NsDi));
            if (stereoNames.Count > 0) root.Attrs.Add(($"xmlns:{ProfileName}", ProfileNs));
            root.Add(new XNode("uml:Model", ("xmi:id", smId + "_model"), ("name", m.Name)).Add(profileApplication, packaged), diagram, applications);
            return XmlTree.Write(root);
        }

        // ------------------------------------------------------------------ reading

        private static readonly Dictionary<string, string> Canonical = new Dictionary<string, string>
        {
            [NsXmi] = "xmi",
            [NsUml] = "uml",
            [NsUmlDi] = "umldi",
            [NsDc] = "dc",
            [NsDi] = "di",
        };

        private static string TypeOf(XmlElem el) => el.Attr("xmi:type") ?? (el.Ns == NsUml ? "uml:" + el.Local : "");
        private static string IdOf(XmlElem el) => el.Attr("xmi:id") ?? "";
        private static IEnumerable<XmlElem> Kids(XmlElem el, string name) => el.Children.Where(c => c.Name == name);
        private static XmlElem Kid(XmlElem el, string name) => el?.Children.FirstOrDefault(c => c.Name == name);

        /// <summary>A reference property, written as an attribute (<c>a="id1 id2"</c>) or as child elements (<c>&lt;a xmi:idref|href&gt;</c>).</summary>
        private static List<string> Refs(XmlElem el, string name)
        {
            var output = JsWhitespace.Split(el.Attr(name));
            foreach (var c in Kids(el, name))
            {
                var r = c.Attr("xmi:idref") ?? c.Attr("href");
                if (!string.IsNullOrEmpty(r)) output.Add(r);
            }
            return output;
        }

        private static string FirstRef(XmlElem el, string name) => el.Attr(name) ?? Refs(el, name).FirstOrDefault();

        private static string LocalId(string reference)
        {
            reference = reference ?? "";
            var i = reference.IndexOf('#');
            return i >= 0 ? reference.Substring(i + 1) : reference;
        }

        /// <summary>Text of an opaque behavior or constraint (its first body).</summary>
        private static string BodyOf(XmlElem el, bool trim = true)
        {
            if (el == null) return "";
            var spec = Kid(el, "specification") ?? el;
            var b = Kid(spec, "body");
            var text = b != null ? b.Text : spec.Attr("body") ?? spec.Attr("value") ?? "";
            return trim ? JsWhitespace.Trim(text) : text;
        }

        private static IEnumerable<XmlElem> Walk(XmlElem el)
        {
            yield return el;
            foreach (var c in el.Children)
            {
                foreach (var d in Walk(c)) yield return d;
            }
        }

        private static string Or(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";

        /// <summary>Parses a state machine file. Throws <see cref="XmlParseException"/> or <see cref="XmiException"/> when it cannot be read.</summary>
        public static FsmModel FromXmi(string text)
        {
            var root = XmlTree.Parse(text, Canonical);
            if (root.Name != "xmi:XMI" && root.Name != "uml:Model")
            {
                throw new XmiException($"Not an XMI document: the root element is <{root.Name}>, expected <xmi:XMI>.");
            }
            XmlElem machineEl = null;
            XmlElem contextEl = null;
            var elementsById = new Dictionary<string, XmlElem>();
            void Visit(XmlElem el, XmlElem parent)
            {
                var id = IdOf(el);
                if (id.Length > 0) elementsById[id] = el;
                var t = TypeOf(el);
                if (machineEl == null && (t == "uml:StateMachine" || t == "uml:ProtocolStateMachine"))
                {
                    machineEl = el;
                    if (parent != null && TypeOf(parent) == "uml:Class") contextEl = parent;
                }
                foreach (var c in el.Children) Visit(c, el);
            }
            Visit(root, null);
            if (machineEl == null) throw new XmiException("The document does not contain a UML state machine.");
            var sm = machineEl;

            string EventText(XmlElem trig)
            {
                elementsById.TryGetValue(LocalId(FirstRef(trig, "event")), out var ev);
                if (ev != null && TypeOf(ev) == "uml:TimeEvent")
                {
                    var expr = Kid(Kid(ev, "when") ?? ev, "expr");
                    var value = expr != null ? expr.Attr("value") ?? BodyOf(expr) : "";
                    if (value.Length > 0) return $"after({value})";
                }
                if (ev != null && TypeOf(ev) == "uml:SignalEvent")
                {
                    elementsById.TryGetValue(LocalId(ev.Attr("signal")), out var sig);
                    return Or(sig?.Attr("name"), ev.Attr("name"), trig.Attr("name"));
                }
                return Or(ev?.Attr("name"), trig.Attr("name"));
            }

            var model = new FsmModel
            {
                Id = Or(IdOf(sm), "sm"),
                Name = sm.Attr("name") ?? "",
                Kind = TypeOf(sm) == "uml:ProtocolStateMachine" ? MachineKind.Protocol : MachineKind.Behavioral,
                Context = contextEl?.Attr("name") ?? "",
            };
            foreach (var c in Kids(sm, "ownedComment"))
            {
                if (Refs(c, "annotatedElement").Contains(model.Id)) model.Documentation = BodyOf(c);
            }

            Vertex VertexBase(XmlElem el, VertexType type, string parent) => new Vertex
            {
                Id = IdOf(el),
                Type = type,
                Name = el.Attr("name") ?? "",
                Parent = parent,
                X = double.NaN,
                Y = double.NaN,
            };

            void ReadTransition(XmlElem el)
            {
                var kindText = el.Attr("kind");
                var t = new Transition
                {
                    Id = IdOf(el),
                    Source = LocalId(FirstRef(el, "source")),
                    Target = LocalId(FirstRef(el, "target")),
                    Kind = kindText == "internal" ? TransitionKind.Internal : kindText == "local" ? TransitionKind.Local : TransitionKind.External,
                    Triggers = Kids(el, "trigger").Select(EventText).Where(s => s.Length > 0).ToList(),
                    Guard = BodyOf(Kid(el, "guard")),
                    Effect = BodyOf(Kid(el, "effect")),
                    Precondition = BodyOf(Kid(el, "preCondition")),
                    Postcondition = BodyOf(Kid(el, "postCondition")),
                };
                model.Transitions.Add(t);
            }

            void ReadPoint(XmlElem el, string parent)
            {
                var kind = el.Attr("kind") == "exitPoint" ? VertexType.ExitPoint : VertexType.EntryPoint;
                model.Vertices.Add(VertexBase(el, kind, parent));
            }

            void ReadVertex(XmlElem el, string parent)
            {
                var t = TypeOf(el);
                if (t == "uml:FinalState")
                {
                    model.Vertices.Add(VertexBase(el, VertexType.Final, parent));
                    return;
                }
                if (t == "uml:Pseudostate")
                {
                    var kind = VertexTypes.TryParseKey(el.Attr("kind") ?? "initial", out var k) && k.IsPseudostate() ? k : VertexType.Initial;
                    model.Vertices.Add(VertexBase(el, kind, parent));
                    return;
                }
                var v = VertexBase(el, VertexType.State, parent);
                v.Entry = BodyOf(Kid(el, "entry"));
                v.Exit = BodyOf(Kid(el, "exit"));
                v.DoActivity = BodyOf(Kid(el, "doActivity"));
                v.Invariant = BodyOf(Kid(el, "stateInvariant"));
                v.Deferrable = Kids(el, "deferrableTrigger").Select(EventText).Where(s => s.Length > 0).ToList();
                v.Submachine = Refs(el, "submachine").FirstOrDefault() ?? "";
                model.Vertices.Add(v);
                foreach (var cp in Kids(el, "connectionPoint")) ReadPoint(cp, v.Id);
                foreach (var c in Kids(el, "connection"))
                {
                    var entryRef = Refs(c, "entry").FirstOrDefault();
                    var exitRef = Refs(c, "exit").FirstOrDefault();
                    var reference = VertexBase(c, VertexType.ConnectionPointRef, v.Id);
                    reference.Name = "";
                    reference.PointKind = exitRef != null && entryRef == null ? PointKind.Exit : PointKind.Entry;
                    reference.Ref = LocalId(entryRef ?? exitRef ?? "");
                    model.Vertices.Add(reference);
                }
                foreach (var r in Kids(el, "region"))
                {
                    v.Regions.Add(new Region { Id = IdOf(r), Name = r.Attr("name") ?? "" });
                    ReadRegion(r);
                }
            }

            void ReadRegion(XmlElem el)
            {
                var id = IdOf(el);
                foreach (var c in el.Children)
                {
                    if (c.Name == "subvertex") ReadVertex(c, id);
                    else if (c.Name == "transition") ReadTransition(c);
                    else if (c.Name == "ownedComment")
                    {
                        var comment = VertexBase(c, VertexType.Comment, id);
                        comment.Text = BodyOf(c, false);
                        comment.Anchors = Refs(c, "annotatedElement");
                        model.Vertices.Add(comment);
                    }
                }
            }

            foreach (var r in Kids(sm, "region"))
            {
                model.Regions.Add(new Region { Id = IdOf(r), Name = r.Attr("name") ?? "" });
                ReadRegion(r);
            }
            if (model.Regions.Count == 0) model.Regions.Add(new Region { Id = model.Id + "_region" });
            foreach (var cp in Kids(sm, "connectionPoint")) ReadPoint(cp, model.Regions[0].Id);

            // Stereotype applications: elements of the embedded profile's namespace.
            var byId = new Dictionary<string, Vertex>();
            foreach (var v in model.Vertices) byId[v.Id] = v;
            foreach (var c in root.Children)
            {
                if (c.Ns != ProfileNs) continue;
                if (byId.TryGetValue(LocalId(c.Attr("base_State")), out var v)) v.Stereotype = c.Local;
            }

            // Layout
            var transitions = new Dictionary<string, Transition>();
            foreach (var t in model.Transitions) transitions[t.Id] = t;
            var regionShapes = new Dictionary<string, RectD>();
            foreach (var d in root.Children)
            {
                if (d.Ns != NsUmlDi) continue;
                foreach (var ext in Kids(d, "xmi:Extension"))
                {
                    if (ext.Attr("extender") != Extender) continue;
                    foreach (var st in Kids(ext, "style"))
                    {
                        var id = st.Attr("element") ?? "";
                        string Read(string name) => (st.Attr(name) ?? "").ToLowerInvariant();
                        void Apply(Action<string> setColor, Dictionary<string, string> items)
                        {
                            var color = Read("color");
                            if (ElementColors.IsColor(color)) setColor(color);
                            var read = new Dictionary<string, string>();
                            foreach (var k in ElementColors.ItemKeys)
                            {
                                var c = Read(k);
                                if (ElementColors.IsColor(c)) read[k] = c;
                            }
                            if (read.Count > 0)
                            {
                                items.Clear();
                                foreach (var kv in read) items[kv.Key] = kv.Value;
                            }
                        }
                        if (byId.TryGetValue(id, out var sv)) Apply(c => sv.Color = c, sv.ItemColors);
                        else if (transitions.TryGetValue(id, out var st2)) Apply(c => st2.Color = c, st2.ItemColors);
                    }
                    // A vertex listed in several groups stays in the first.
                    foreach (var g in Kids(ext, "group"))
                    {
                        var id = g.Attr("id") ?? "";
                        if (id.Length == 0) continue;
                        foreach (var member in JsWhitespace.Split(g.Attr("members")))
                        {
                            if (byId.TryGetValue(member, out var gv) && gv.Group.Length == 0) gv.Group = id;
                        }
                    }
                }
                foreach (var el in Walk(d))
                {
                    var type = TypeOf(el);
                    var target = FirstRef(el, "modelElement");
                    if (string.IsNullOrEmpty(target)) continue;
                    var b = Kid(el, "bounds");
                    if (type == "umldi:UMLShape" && b != null)
                    {
                        var box = new RectD(Bound(b.Attr("x")), Bound(b.Attr("y")), Bound(b.Attr("width")), Bound(b.Attr("height")));
                        if (byId.TryGetValue(target, out var v))
                        {
                            v.X = box.X;
                            v.Y = box.Y;
                            v.W = box.W;
                            v.H = box.H;
                        }
                        else
                        {
                            regionShapes[target] = box;
                        }
                    }
                    else if (type == "umldi:UMLEdge" && transitions.TryGetValue(target, out var t))
                    {
                        var pts = Kids(el, "waypoint").Select(w => new PointD(Num.Parse(w.Attr("x")), Num.Parse(w.Attr("y")))).ToList();
                        if (pts.Count > 2) t.Points = pts.GetRange(1, pts.Count - 2);
                        var label = el.Children.FirstOrDefault(c => TypeOf(c) == "umldi:UMLLabel");
                        var lb = Kid(label, "bounds");
                        if (lb != null && pts.Count >= 2)
                        {
                            var mid = LabelMid(pts);
                            // A missing or invalid bound gives NaN, which the other editions' JSON turns into 0.
                            double Offset(double n) => double.IsNaN(n) || double.IsInfinity(n) ? 0 : n;
                            // Not rounded: the middle is off the 0.1 px grid, and an offset rounded to 0.1 px could move the
                            // label by 0.1 px on each save. Written again from the same waypoints, it gives the same label box.
                            t.LabelOffset = new PointD(
                                Offset(Num.ParseJs(lb.Attr("x")) + Num.ParseJs(lb.Attr("width")) / 2 - mid.X),
                                Offset(Num.ParseJs(lb.Attr("y")) + Num.ParseJs(lb.Attr("height")) / 2 - mid.Y));
                        }
                    }
                }
            }
            foreach (var v in model.Vertices)
            {
                if (v.Regions.Count >= 2 && regionShapes.TryGetValue(v.Regions[0].Id, out var a) && regionShapes.TryGetValue(v.Regions[1].Id, out var b))
                {
                    v.RegionLayout = Math.Abs(b.X - a.X) > Math.Abs(b.Y - a.Y) ? RegionLayout.Horizontal : RegionLayout.Vertical;
                }
                // Each region's own size, along the layout (an infinite one, none, as the other editions' JSON makes it).
                foreach (var r in v.Regions)
                {
                    if (!regionShapes.TryGetValue(r.Id, out var box)) continue;
                    var size = v.RegionLayout == RegionLayout.Horizontal ? box.W : box.H;
                    r.Size = double.IsInfinity(size) ? (double?)null : size;
                }
            }

            // Elements without a shape get a default place and size.
            int slot = 0;
            foreach (var v in model.Vertices)
            {
                if (v.W == 0 || v.H == 0) (v.W, v.H) = v.Type.DefaultSize();
                if (double.IsNaN(v.X) || double.IsNaN(v.Y))
                {
                    v.X = 40 + slot % 5 * 180;
                    v.Y = 40 + slot / 5 * 120;
                    slot++;
                }
                // Infinite bounds count as sizes above, then become 0 as the other editions' JSON makes them.
                v.X = Finite(v.X);
                v.Y = Finite(v.Y);
                v.W = Finite(v.W);
                v.H = Finite(v.H);
            }
            return model.Normalize();
        }

        /// <summary>A bound of a shape as JavaScript's <c>+s || 0</c> reads it: missing or invalid gives 0, infinite stays infinite.</summary>
        private static double Bound(string s)
        {
            var d = Num.ParseJs(s);
            return double.IsNaN(d) ? 0 : d;
        }

        private static double Finite(double n) => double.IsInfinity(n) ? 0 : n;
    }
}
