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
    /// - the region layout of an orthogonal state follows from its region shapes.
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

        private const string ProfileName = "FsmStereotypes";
        private const string ProfileNs = "https://github.com/vincedupuis/fsm-editor/profiles/FsmStereotypes";
        private const string UmlState = NsUml + "/UML.xmi#State";
        /// <summary>Language of the opaque behaviors and expressions (argument-less calls, see <see cref="Expressions"/>).</summary>
        private const string Language = "FSM";

        private const double LabelW = 100;
        private const double LabelH = 16;

        private static bool IsPseudoKind(VertexType t) => t.IsPseudostate();

        // ------------------------------------------------------------------ writing

        public static string ToXmi(FsmModel source)
        {
            var m = source.Clone().Normalize();
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

            XNode RegionNode(Region r)
            {
                var kids = m.Vertices.Where(v => v.Parent == r.Id && !IsMachinePoint(v)).ToList();
                return new XNode("region", ("xmi:type", "uml:Region"), ("xmi:id", r.Id), ("name", r.Name))
                    .Add(kids.Where(v => v.Type == VertexType.Comment).Select(CommentNode).ToList())
                    .Add(kids.Where(v => v.Type != VertexType.Comment).Select(VertexNode).ToList())
                    .Add((transitionsByRegion.TryGetValue(r.Id, out var ts) ? ts : new List<Transition>()).Select(TransitionNode).ToList());
            }

            XNode VertexNode(Vertex v)
            {
                if (v.Type == VertexType.Final) return new XNode("subvertex", ("xmi:type", "uml:FinalState"), ("xmi:id", v.Id), ("name", v.Name));
                if (IsPseudoKind(v.Type)) return PointNode("subvertex", v);
                var node = new XNode("subvertex", ("xmi:type", "uml:State"), ("xmi:id", v.Id), ("name", v.Name))
                    .Add(Behavior("entry", v.Id + "_entry", v.Entry))
                    .Add(Behavior("exit", v.Id + "_exit", v.Exit))
                    .Add(Behavior("doActivity", v.Id + "_do", v.DoActivity))
                    .Add(Constraint("stateInvariant", v.Id + "_inv", v.Invariant))
                    .Add(Triggers("deferrableTrigger", v.Id, v.Deferrable));
                foreach (var cp in m.Vertices)
                {
                    if (cp.Parent != v.Id) continue;
                    if (cp.Type == VertexType.EntryPoint || cp.Type == VertexType.ExitPoint) node.Add(PointNode("connectionPoint", cp));
                    if (cp.Type == VertexType.ConnectionPointRef)
                    {
                        var file = v.Submachine.Split('#')[0];
                        node.Add(new XNode("connection", ("xmi:type", "uml:ConnectionPointReference"), ("xmi:id", cp.Id)).Add(
                            new XNode(cp.PointKind == PointKind.Exit ? "exit" : "entry", ("href", $"{file}#{cp.Ref}"))));
                    }
                }
                foreach (var r in v.Regions) node.Add(RegionNode(r));
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
            machine.Add(m.Vertices.Where(IsMachinePoint).Select(v => PointNode("connectionPoint", v)).ToList());
            machine.Add(m.Regions.Select(RegionNode).ToList());

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
            var stereotyped = m.Vertices.Where(v => v.Type == VertexType.State && v.Stereotype.Length > 0).ToList();
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

            // Diagram
            XNode Bounds(double x, double y, double w, double h) =>
                new XNode("bounds", ("xmi:type", "dc:Bounds"), ("x", Num.R1(x)), ("y", Num.R1(y)), ("width", Num.R1(w)), ("height", Num.R1(h)));
            var shapes = new List<XNode>();
            foreach (var v in m.Vertices)
            {
                shapes.Add(new XNode("ownedElement", ("xmi:type", "umldi:UMLShape"), ("xmi:id", "di_" + v.Id), ("modelElement", v.Id)).Add(Bounds(v.X, v.Y, v.W, v.H)));
                if (v.Type == VertexType.State)
                {
                    foreach (var rb in Geometry.RegionRects(m, v))
                    {
                        shapes.Add(new XNode("ownedElement", ("xmi:type", "umldi:UMLShape"), ("xmi:id", "di_" + rb.Id), ("modelElement", rb.Id))
                            .Add(Bounds(rb.Rect.X, rb.Rect.Y, rb.Rect.W, rb.Rect.H)));
                    }
                }
            }
            var edges = new List<XNode>();
            foreach (var t in m.Transitions)
            {
                var s = ix.Vertex(t.Source);
                var g = ix.Vertex(t.Target);
                if (s == null || g == null) continue;
                if (t.Kind == TransitionKind.Internal && s == g && s.Type == VertexType.State) continue; // drawn in the state's compartment
                var pts = new List<PointD> { s.Center };
                pts.AddRange(t.Points);
                pts.Add(g.Center);
                var edge = new XNode("ownedElement", ("xmi:type", "umldi:UMLEdge"), ("xmi:id", "di_" + t.Id), ("modelElement", t.Id), ("source", "di_" + s.Id), ("target", "di_" + g.Id))
                    .Add(pts.Select(p => new XNode("waypoint", ("xmi:type", "dc:Point"), ("x", Num.R1(p.X)), ("y", Num.R1(p.Y)))).ToList());
                if (t.LabelOffset is PointD off)
                {
                    var (mid, _, _) = Geometry.PolylineMid(pts);
                    var cx = mid.X + off.X;
                    var cy = mid.Y + off.Y;
                    edge.Add(new XNode("ownedElement", ("xmi:type", "umldi:UMLLabel"), ("xmi:id", $"di_{t.Id}_label"), ("modelElement", t.Id))
                        .Add(Bounds(cx - LabelW / 2, cy - LabelH / 2, LabelW, LabelH)));
                }
                edges.Add(edge);
            }
            foreach (var c in m.Vertices.Where(v => v.Type == VertexType.Comment))
            {
                for (int i = 0; i < c.Anchors.Count; i++)
                {
                    var a = c.Anchors[i];
                    if (ix.Vertex(a) == null && ix.Transition(a) == null) continue;
                    edges.Add(new XNode("ownedElement", ("xmi:type", "umldi:UMLEdge"), ("xmi:id", $"di_{c.Id}_anchor{i + 1}"), ("modelElement", c.Id), ("source", "di_" + c.Id), ("target", "di_" + a)));
                }
            }
            var diagram = new XNode("umldi:UMLStateMachineDiagram", ("xmi:id", smId + "_diagram"), ("name", m.Name), ("modelElement", smId), ("isFrame", "false"))
                .Add(shapes, edges);

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
            var output = (el.Attr(name) ?? "").Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
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
        private static string BodyOf(XmlElem el)
        {
            if (el == null) return "";
            var spec = Kid(el, "specification") ?? el;
            var b = Kid(spec, "body");
            if (b != null) return b.Text.Trim();
            return (spec.Attr("body") ?? spec.Attr("value") ?? "").Trim();
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
                        comment.Text = BodyOf(c);
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
                foreach (var el in Walk(d))
                {
                    var type = TypeOf(el);
                    var target = FirstRef(el, "modelElement");
                    if (string.IsNullOrEmpty(target)) continue;
                    var b = Kid(el, "bounds");
                    if (type == "umldi:UMLShape" && b != null)
                    {
                        var box = new RectD(Num.Parse(b.Attr("x")), Num.Parse(b.Attr("y")), Num.Parse(b.Attr("width")), Num.Parse(b.Attr("height")));
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
                            var (mid, _, _) = Geometry.PolylineMid(pts);
                            t.LabelOffset = new PointD(
                                Num.R1(Num.Parse(lb.Attr("x")) + Num.Parse(lb.Attr("width")) / 2 - mid.X),
                                Num.R1(Num.Parse(lb.Attr("y")) + Num.Parse(lb.Attr("height")) / 2 - mid.Y));
                        }
                    }
                }
            }
            foreach (var v in model.Vertices)
            {
                if (v.Regions.Count < 2) continue;
                if (regionShapes.TryGetValue(v.Regions[0].Id, out var a) && regionShapes.TryGetValue(v.Regions[1].Id, out var b))
                {
                    v.RegionLayout = Math.Abs(b.X - a.X) > Math.Abs(b.Y - a.Y) ? RegionLayout.Horizontal : RegionLayout.Vertical;
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
            }
            return model.Normalize();
        }
    }
}
