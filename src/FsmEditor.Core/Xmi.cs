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
    /// Reads state machine files of earlier versions: XMI 2.5.1 holding a UML 2.5.1 model, plus a UML DI diagram
    /// (umldi:UMLStateMachineDiagram) with the layout. Nothing writes this format any more: saving such a file writes
    /// the JSON format (<see cref="Format"/>), converting it (<see cref="Conversion"/>). A port of the VS Code edition's
    /// <c>src/xmi.ts</c>, which reads coordinates as JavaScript does (<c>+x || 0</c>: an infinite one stays infinite,
    /// and the writer reports it).
    ///
    /// Layout mapping:
    /// - every vertex, region and comment has a UMLShape with dc:Bounds;
    /// - every drawn transition has a UMLEdge whose waypoints are the centers of its source and target with the bend
    ///   points in between, plus an optional UMLLabel whose center is the middle of those waypoints moved by the label
    ///   offset;
    /// - the region layout of an orthogonal state, and each region's size along it, follow from its region shapes;
    /// - colors and groups are kept in one xmi:Extension at the end of the diagram, with a
    ///   &lt;style element="id" color="#rrggbb" entry="#rrggbb" …/&gt; per colored vertex or transition (UML DI has no
    ///   color), then a &lt;group id="g" members="id id …"/&gt; per group of vertices.
    ///
    /// Files written by earlier versions may end with the model hash, in its own xmi:Extension
    /// (&lt;modelHash value="…"/&gt;): the reader ignores it.
    /// </summary>
    /// <summary>A model read from an old file, with what the JSON format has no place for.</summary>
    public sealed class XmiModel
    {
        public FsmModel Model { get; set; }
        /// <summary>xmi:id of the state machine, which hrefs to it name (<c>Payment.fsm#sm</c>).</summary>
        public string XmiId { get; set; } = "sm";
        /// <summary>Name of the context classifier, which the JSON format doesn't hold.</summary>
        public string Context { get; set; } = "";
    }

    public static class Xmi
    {
        public const string NsXmi = "http://www.omg.org/spec/XMI/20131001";
        public const string NsUml = "http://www.omg.org/spec/UML/20161101";
        public const string NsUmlDi = "http://www.omg.org/spec/UML/20161101/UMLDI";
        public const string NsDc = "http://www.omg.org/spec/DD/20100524/DC";
        public const string NsDi = "http://www.omg.org/spec/DD/20100524/DI";
        private const string Extender = "FSM Editor";

        private const string ProfileNs = "https://github.com/vincedupuis/fsm-editor/profiles/FsmStereotypes";

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
        public static FsmModel FromXmi(string text) => Read(text).Model;

        /// <summary>Parses a state machine file, with what the JSON format has no place for.</summary>
        public static XmiModel Read(string text)
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

            var xmiId = Or(IdOf(sm), "sm");
            var model = new FsmModel
            {
                Name = sm.Attr("name") ?? "",
                Kind = TypeOf(sm) == "uml:ProtocolStateMachine" ? MachineKind.Protocol : MachineKind.Behavioral,
            };
            foreach (var c in Kids(sm, "ownedComment"))
            {
                if (Refs(c, "annotatedElement").Contains(xmiId)) model.Documentation = BodyOf(c);
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
                    Action = BodyOf(Kid(el, "effect")),
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
            if (model.Regions.Count == 0) model.Regions.Add(new Region { Id = xmiId + "_region" });
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
                                var c = Read(k == "action" ? "effect" : k);
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
                        // Read as JavaScript reads them (+x || 0): an infinite coordinate stays infinite (the writer reports it).
                        var pts = Kids(el, "waypoint").Select(w => new PointD(Bound(w.Attr("x")), Bound(w.Attr("y")))).ToList();
                        if (pts.Count > 2) t.Points = pts.GetRange(1, pts.Count - 2);
                        var label = el.Children.FirstOrDefault(c => TypeOf(c) == "umldi:UMLLabel");
                        var lb = Kid(label, "bounds");
                        if (lb != null && pts.Count >= 2)
                        {
                            var mid = LabelMid(pts);
                            // Not rounded: the middle is off the 0.1 px grid, and an offset rounded to 0.1 px could move the
                            // label by 0.1 px on each save. Written again from the same waypoints, it gives the same label box.
                            t.LabelOffset = new PointD(
                                Num.ParseJs(lb.Attr("x")) + Num.ParseJs(lb.Attr("width")) / 2 - mid.X,
                                Num.ParseJs(lb.Attr("y")) + Num.ParseJs(lb.Attr("height")) / 2 - mid.Y);
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
                // Each region's own size, along the layout.
                foreach (var r in v.Regions)
                {
                    if (!regionShapes.TryGetValue(r.Id, out var box)) continue;
                    r.Size = v.RegionLayout == RegionLayout.Horizontal ? box.W : box.H;
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
            model.Normalize();
            return new XmiModel { Model = model, XmiId = xmiId, Context = contextEl?.Attr("name") ?? "" };
        }

        /// <summary>A bound of a shape as JavaScript's <c>+s || 0</c> reads it: missing or invalid gives 0, infinite stays infinite.</summary>
        private static double Bound(string s)
        {
            var d = Num.ParseJs(s);
            return double.IsNaN(d) ? 0 : d;
        }

    }
}
