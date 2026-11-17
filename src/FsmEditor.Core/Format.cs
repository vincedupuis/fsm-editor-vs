using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FsmEditor.Core
{
    /// <summary>A problem of a file's text or shape, with its line.</summary>
    public sealed class Problem
    {
        public Problem(int line, string message)
        {
            Line = line;
            Message = message;
        }

        public int Line { get; }
        public string Message { get; }

        public override string ToString() => $"line {Line}: {Message}";
    }

    /// <summary>A machine read from its model file (and its layout file).</summary>
    public sealed class ReadMachine
    {
        /// <summary>The model, or null when the model file can't be read (see <see cref="Problems"/>).</summary>
        public FsmModel Model { get; set; }
        /// <summary>Line of each element, region, point and transition, by id.</summary>
        public Dictionary<string, int> Lines { get; set; } = new Dictionary<string, int>();
        /// <summary>Why the model file can't be read (not JSON, or a wrong shape), or null.</summary>
        public List<Problem> Problems { get; set; }
        /// <summary>Why the layout file couldn't be used (it is then left out entirely), or null.</summary>
        public string LayoutProblem { get; set; }
        /// <summary>How many elements had no layout entry and were placed automatically.</summary>
        public int Placed { get; set; }

        public bool Failed => Problems != null;
    }

    /// <summary>What writing a machine gives.</summary>
    public sealed class Written
    {
        /// <summary>The model file.</summary>
        public string Model { get; set; } = "";
        /// <summary>The layout file.</summary>
        public string Layout { get; set; } = "";
        /// <summary>Line of each vertex and transition in the model file, by id.</summary>
        public Dictionary<string, int> Lines { get; set; } = new Dictionary<string, int>();
        /// <summary>What the format can't hold, which was left out (an editor's model holds none of it).</summary>
        public List<string> Lost { get; set; } = new List<string>();
    }

    /// <summary>A name changed by <see cref="Format.AssignNames"/>.</summary>
    public sealed class Rename
    {
        public string What { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        /// <summary>Why, when it isn't the characters replaced.</summary>
        public string Why { get; set; }
    }

    /// <summary>
    /// The file format: a machine is two JSON files, <c>&lt;Name&gt;.fsm</c> (the model, without ids: elements are
    /// identified by their names) and <c>&lt;Name&gt;.fsm.layout</c> beside it (the drawing). Described in fsm-cli's
    /// README ("The .fsm file format") and by its JSON Schemas. A port of the VS Code edition's <c>src/format.ts</c>.
    ///
    /// Reading the model is fsm-cli's reader (<c>src/format.rs</c>), problem for problem: the same messages at the same
    /// lines, and the same ids, derived from the names (an element: its name; a point: <c>&lt;owner&gt;.&lt;point&gt;</c>;
    /// <c>&lt;owner&gt;.initial</c>, <c>.history</c>, <c>.deepHistory</c>; the only region of a state or of the machine:
    /// <c>&lt;owner&gt;.</c>; a transition: <c>&lt;source&gt;#&lt;n&gt;</c>; an id already taken gets <c>~2</c>, <c>~3</c>...).
    /// The editor works on the flat <see cref="FsmModel"/> with absolute coordinates; the layout file holds them relative
    /// to their owner (<see cref="ApplyLayout"/>, <see cref="WriteMachine"/>).
    ///
    /// A reference that names nothing is kept as it is: a transition's target is then <see cref="Unresolved"/> followed by
    /// the reference, a comment's anchor <see cref="Unresolved"/> (a reference) or <see cref="UnresolvedAnchor"/> (a
    /// transition, as JSON) followed by it.
    /// </summary>
    public static class Format
    {
        public const string Unresolved = "";
        public const string UnresolvedAnchor = "";

        /// <summary>The reference of a target that names nothing ("" for an <c>initial</c> without <c>target</c>), or null.</summary>
        public static string UnresolvedOf(string target) => target != null && target.StartsWith(Unresolved, StringComparison.Ordinal) ? target.Substring(1) : null;

        /// <summary>Whether a text is a file in the old format (XMI).</summary>
        public static bool IsOldFormat(string text)
        {
            foreach (var c in text ?? "")
            {
                if (c == '<') return true;
                if (c != '﻿' && c != ' ' && c != '\t' && c != '\r' && c != '\n') return false;
            }
            return false;
        }

        internal static double N1(double n) => Num.R1(n);

        /// <summary>The size of each kind of vertex when the layout has none (the editor's defaults).</summary>
        public static (double W, double H) DefaultSize(VertexType t) => t.DefaultSize();

        /// <summary>Kinds the editor lets the user resize: the layout holds their width and height.</summary>
        public static bool IsResizable(VertexType t) => t == VertexType.State || t == VertexType.Comment || t == VertexType.Fork || t == VertexType.Join;

        /// <summary>Item colors: their keys in the layout file, by the model's.</summary>
        private static readonly Dictionary<string, string> ItemColorFileKey = new Dictionary<string, string>
        {
            ["background"] = "background", ["entry"] = "entry", ["exit"] = "exit", ["doActivity"] = "do", ["deferrable"] = "defer", ["invariant"] = "invariant",
            ["triggers"] = "trigger", ["guard"] = "guard", ["action"] = "action", ["precondition"] = "precondition", ["postcondition"] = "postcondition",
        };
        private static readonly Dictionary<string, string> ItemColorFromFile = ItemColorFileKey.ToDictionary(kv => kv.Value, kv => kv.Key);

        /// <summary>Element types of the file.</summary>
        internal static readonly string[] ElementTypes = { "state", "choice", "junction", "fork", "join", "final", "terminate", "comment" };
        private static readonly string[] RegionFields = { "initial", "history", "deepHistory", "elements" };

        internal static bool IsElementKind(VertexType t) =>
            t == VertexType.State || t == VertexType.Choice || t == VertexType.Junction || t == VertexType.Fork || t == VertexType.Join
            || t == VertexType.Final || t == VertexType.Terminate || t == VertexType.Comment;

        private static readonly (string Key, VertexType Type)[] Pseudo =
            { ("initial", VertexType.Initial), ("history", VertexType.ShallowHistory), ("deepHistory", VertexType.DeepHistory) };

        // ---------------------------------------------------------------- shape of the model file (format.rs's Shape)

        private static string OneOf(IReadOnlyList<string> values)
        {
            var q = values.Select(v => $"\"{v}\"").ToList();
            return q.Count == 1 ? q[0] : $"{string.Join(", ", q.Take(q.Count - 1))} or {q[q.Count - 1]}";
        }

        private sealed class Shape
        {
            public readonly List<Problem> Problems = new List<Problem>();

            private void Add(int line, string message) => Problems.Add(new Problem(line, message));

            private bool Kind(JsonField f, string kind)
            {
                var ok = kind == "text" ? f.Value.Kind == JsonKind.String : kind == "object" ? f.Value.Kind == JsonKind.Object : f.Value.Kind == JsonKind.Array;
                if (!ok) Add(f.Value.Line, $"Field '{f.Key}' must be {(kind == "text" ? "a text" : kind == "object" ? "an object" : "a list")}.");
                return ok;
            }

            private void Object(JsonNode node, string what, IReadOnlyCollection<string> allowed, IEnumerable<string> required, Action<JsonField> check)
            {
                var fields = node.Fields ?? new List<JsonField>();
                foreach (var f in fields)
                {
                    if (allowed.Contains(f.Key)) check(f);
                    else Add(f.Line, $"Unknown field '{f.Key}' in {what}.");
                }
                foreach (var r in required)
                {
                    if (!fields.Any(f => f.Key == r)) Add(node.Line, $"Missing field '{r}' in {what}.");
                }
            }

            private void List(JsonField f, Action<JsonNode> item)
            {
                if (!Kind(f, "list")) return;
                foreach (var x in f.Value.Items)
                {
                    if (x.Kind != JsonKind.Object) Add(x.Line, $"Items of '{f.Key}' must be objects.");
                    else item(x);
                }
            }

            private bool Enumeration(JsonField f, IReadOnlyList<string> values)
            {
                if (!Kind(f, "text")) return false;
                if (values.Contains(f.Value.Str)) return true;
                Add(f.Value.Line, $"Field '{f.Key}' must be {OneOf(values)}.");
                return false;
            }

            public void Machine(JsonNode node)
            {
                var fields = node.Fields;
                if (fields == null)
                {
                    Add(node.Line, "The file must hold a JSON object.");
                    return;
                }
                var allowed = new[] { "$schema", "format", "name", "type", "documentation", "points", "regions" }.Concat(RegionFields).ToList();
                Object(node, "the machine", allowed, new[] { "format", "name", "type" }, f =>
                {
                    switch (f.Key)
                    {
                        case "format":
                        {
                            var n = f.Value.Number;
                            if (n == 1) break;
                            if (n != null && n > 1 && Math.Floor(n.Value) == n.Value) Add(f.Value.Line, $"This file is in format {Num.Format(n.Value)}: this version reads format 1 only.");
                            else Add(f.Value.Line, "Field 'format' must be 1.");
                            break;
                        }
                        case "type":
                            Enumeration(f, new[] { "behavioral", "protocol" });
                            break;
                        case "points":
                            List(f, Point);
                            break;
                        case "$schema":
                        case "name":
                        case "documentation":
                            Kind(f, "text");
                            break;
                        default:
                            RegionField(f);
                            break;
                    }
                });
                RegionsAlone(fields);
            }

            private void RegionsAlone(List<JsonField> fields)
            {
                if (!fields.Any(f => f.Key == "regions")) return;
                foreach (var f in fields)
                {
                    if (RegionFields.Contains(f.Key)) Add(f.Line, $"Field '{f.Key}' cannot be used with 'regions': each region has its own.");
                }
            }

            private void RegionField(JsonField f)
            {
                if (f.Key == "initial" || f.Key == "history" || f.Key == "deepHistory")
                {
                    if (Kind(f, "object")) Object(f.Value, $"'{f.Key}'", new[] { "target", "action" }, new string[0], g => Kind(g, "text"));
                    return;
                }
                if (f.Key == "elements")
                {
                    List(f, Element);
                    return;
                }
                if (!Kind(f, "list")) return;
                var items = f.Value.Items;
                if (items.Count < 2)
                {
                    Add(f.Value.Line, "Field 'regions' needs two regions or more: the fields of a single region go directly in its owner.");
                }
                foreach (var x in items)
                {
                    if (x.Kind != JsonKind.Object)
                    {
                        Add(x.Line, "Items of 'regions' must be objects.");
                        continue;
                    }
                    Object(x, "a region", new[] { "name" }.Concat(RegionFields).ToList(), new[] { "name" }, g =>
                    {
                        if (g.Key == "name") Kind(g, "text");
                        else RegionField(g);
                    });
                }
            }

            private void Element(JsonNode node)
            {
                var ty = node.Field("type")?.Value.Str;
                if (ty == null || !ElementTypes.Contains(ty))
                {
                    foreach (var f in node.Fields ?? new List<JsonField>())
                    {
                        if (f.Key == "name") Kind(f, "text");
                        else if (f.Key == "type") Enumeration(f, ElementTypes);
                    }
                    foreach (var r in new[] { "name", "type" })
                    {
                        if (node.Field(r) == null) Add(node.Line, $"Missing field '{r}' in an element.");
                    }
                    return;
                }
                string what;
                string[] extra;
                switch (ty)
                {
                    case "state":
                        what = "a state";
                        extra = new[] { "stereotype", "submachine", "entry", "do", "exit", "defer", "invariant", "transitions", "points", "initial", "history", "deepHistory", "elements", "regions" };
                        break;
                    case "choice":
                    case "junction":
                    case "fork":
                    case "join":
                        what = $"a {ty}";
                        extra = new[] { "transitions" };
                        break;
                    case "final":
                        what = "a final state";
                        extra = new string[0];
                        break;
                    case "terminate":
                        what = "a terminate pseudostate";
                        extra = new string[0];
                        break;
                    default:
                        what = "a comment";
                        extra = new[] { "text", "anchors" };
                        break;
                }
                Object(node, what, new[] { "name", "type" }.Concat(extra).ToList(), new[] { "name", "type" }, f =>
                {
                    switch (f.Key)
                    {
                        case "type":
                            break;
                        case "transitions":
                            List(f, Transition);
                            break;
                        case "points":
                            List(f, Point);
                            break;
                        case "anchors":
                            if (Kind(f, "list"))
                            {
                                foreach (var x in f.Value.Items)
                                {
                                    if (x.Kind == JsonKind.String) continue;
                                    if (x.Kind == JsonKind.Object) Object(x, "a comment anchor", new[] { "source", "trigger", "target" }, new[] { "source" }, g => Kind(g, "text"));
                                    else Add(x.Line, "Items of 'anchors' must be texts or objects.");
                                }
                            }
                            break;
                        case "initial":
                        case "history":
                        case "deepHistory":
                        case "elements":
                        case "regions":
                            RegionField(f);
                            break;
                        default:
                            Kind(f, "text");
                            break;
                    }
                });
                if (ty == "state") RegionsAlone(node.Fields);
            }

            private void Point(JsonNode node)
            {
                Object(node, "a point", new[] { "name", "type", "transitions" }, new[] { "name", "type" }, f =>
                {
                    if (f.Key == "type") Enumeration(f, new[] { "entryPoint", "exitPoint" });
                    else if (f.Key == "transitions") List(f, Transition);
                    else Kind(f, "text");
                });
            }

            private void Transition(JsonNode node)
            {
                Object(node, "a transition", new[] { "type", "trigger", "guard", "action", "precondition", "postcondition", "target" }, new[] { "type" }, f =>
                {
                    if (f.Key == "type") Enumeration(f, new[] { "external", "internal", "local" });
                    else Kind(f, "text");
                });
                var ty = node.Field("type")?.Value.Str;
                var target = node.Field("target");
                if (ty == "internal" && target != null) Add(target.Line, "An internal transition has no 'target'.");
                else if ((ty == "external" || ty == "local") && target == null) Add(node.Line, "Missing field 'target' in a transition.");
            }
        }

        /// <summary>The problems of a model document's shape (none when it is a machine file).</summary>
        public static List<Problem> ShapeProblems(JsonNode node)
        {
            var s = new Shape();
            s.Machine(node);
            return s.Problems;
        }

        // ---------------------------------------------------------------- reading the model (format.rs's Builder)

        private static string Text(JsonNode node, string key) => node.Field(key)?.Value.Str ?? "";

        private static List<JsonNode> ListOf(JsonNode node, string key) => node.Field(key)?.Value.Items ?? new List<JsonNode>();

        /// <summary>Triggers or deferred events written <c>a, b, c</c>.</summary>
        public static List<string> SplitEvents(string s) => (s ?? "").Split(',').Select(JsWhitespace.Trim).Where(x => x.Length > 0).ToList();

        private static Vertex NewVertex(string id, VertexType type, string name, string parent) =>
            new Vertex { Id = id, Type = type, Name = name, Parent = parent, X = double.NaN, Y = double.NaN, W = 0, H = 0 };

        private sealed class Builder
        {
            public readonly FsmModel Model;
            public readonly Dictionary<string, int> Lines = new Dictionary<string, int>();
            private readonly HashSet<string> _taken = new HashSet<string>();
            private readonly Dictionary<string, string> _byName = new Dictionary<string, string>();
            private readonly Dictionary<string, string> _pseudo = new Dictionary<string, string>();
            private readonly Dictionary<string, string> _points = new Dictionary<string, string>();
            public readonly List<(string Source, JsonNode Node, bool IsDefault)> Pending = new List<(string, JsonNode, bool)>();
            /// <summary>Comments and their anchors' nodes, resolved once every transition is known.</summary>
            private readonly List<(Vertex V, List<JsonNode> Nodes)> _anchors = new List<(Vertex, List<JsonNode>)>();

            public Builder(FsmModel model) => Model = model;

            public string Alloc(string baseId)
            {
                var id = baseId;
                for (var n = 2; _taken.Contains(id); n++) id = $"{baseId}~{n}";
                _taken.Add(id);
                return id;
            }

            public string Named(string name)
            {
                var id = Alloc(name.Length > 0 ? name : "~");
                if (!_byName.ContainsKey(name)) _byName[name] = id;
                return id;
            }

            public void Region(JsonNode node, string id, string owner)
            {
                foreach (var (key, type) in Pseudo)
                {
                    var f = node.Field(key);
                    if (f == null) continue;
                    var vid = Alloc($"{owner}.{key}");
                    if (key != "initial" && !_pseudo.ContainsKey($"{owner}.{key}")) _pseudo[$"{owner}.{key}"] = vid;
                    Lines[vid] = f.Line;
                    Model.Vertices.Add(NewVertex(vid, type, "", id));
                    var x = f.Value;
                    // The default transition, when there is one: a target or an action (an action without a target is reported).
                    if (x.Field("target") != null || x.Field("action") != null) Pending.Add((vid, x, true));
                }
                foreach (var e in ListOf(node, "elements")) Element(e, id);
            }

            private void Element(JsonNode e, string parent)
            {
                var name = Text(e, "name");
                var id = Named(name);
                Lines[id] = e.Line;
                var typeText = Text(e, "type");
                var type = VertexTypes.TryParseKey(typeText, out var t) && IsElementKind(t) ? t : VertexType.Comment;
                var v = NewVertex(id, type, name, parent);
                if (v.Type == VertexType.Comment)
                {
                    v.Text = Text(e, "text");
                    v.Anchors = new List<string>();
                    _anchors.Add((v, ListOf(e, "anchors")));
                }
                if (v.Type != VertexType.State)
                {
                    Model.Vertices.Add(v);
                    foreach (var tr in ListOf(e, "transitions")) Pending.Add((id, tr, false));
                    return;
                }
                v.Stereotype = Text(e, "stereotype");
                v.Entry = Text(e, "entry");
                v.Exit = Text(e, "exit");
                v.DoActivity = Text(e, "do");
                v.Invariant = Text(e, "invariant");
                v.Deferrable = SplitEvents(Text(e, "defer"));
                v.Submachine = e.Field("submachine")?.Value.Str ?? "";
                Model.Vertices.Add(v);
                foreach (var tr in ListOf(e, "transitions")) Pending.Add((id, tr, false));
                PointsOf(e, id, name, id, true);
                var sources = new List<(Region R, JsonNode Node, string Owner)>();
                var regs = e.Field("regions");
                if (regs != null)
                {
                    foreach (var r in regs.Value.Items ?? new List<JsonNode>())
                    {
                        var rname = Text(r, "name");
                        var rid = Named(rname);
                        Lines[rid] = r.Line;
                        sources.Add((new Region { Id = rid, Name = rname }, r, rname));
                    }
                }
                else if (RegionFields.Any(k => e.Field(k) != null))
                {
                    var rid = Alloc($"{name}.");
                    Lines[rid] = e.Line;
                    sources.Add((new Region { Id = rid, Name = "" }, e, name));
                }
                foreach (var (r, node, owner) in sources)
                {
                    v.Regions.Add(r);
                    Region(node, r.Id, owner);
                }
            }

            public void PointsOf(JsonNode node, string ownerId, string ownerName, string parent, bool state)
            {
                var refs = state && node.Field("submachine") != null;
                foreach (var p in ListOf(node, "points"))
                {
                    var name = Text(p, "name");
                    var entry = Text(p, "type") == "entryPoint";
                    var key = $"{ownerName}.{name}";
                    var id = Alloc(key);
                    if (!_points.ContainsKey(key)) _points[key] = id;
                    Lines[id] = p.Line;
                    var v = refs
                        ? new Vertex { Id = id, Type = VertexType.ConnectionPointRef, Name = "", Parent = ownerId, X = double.NaN, Y = double.NaN, Ref = name, PointKind = entry ? PointKind.Entry : PointKind.Exit }
                        : NewVertex(id, entry ? VertexType.EntryPoint : VertexType.ExitPoint, name, parent);
                    Model.Vertices.Add(v);
                    foreach (var t in ListOf(p, "transitions")) Pending.Add((id, t, false));
                }
            }

            public string Resolve(string reference)
            {
                var dot = reference.IndexOf('.');
                string found;
                if (dot < 0) _byName.TryGetValue(reference, out found);
                else
                {
                    var rest = reference.Substring(dot + 1);
                    if (rest == "history" || rest == "deepHistory") _pseudo.TryGetValue(reference, out found);
                    else _points.TryGetValue(reference, out found);
                }
                return found ?? Unresolved + reference;
            }

            /// <summary>
            /// A comment's anchors, as ids: a reference names an element, an object a transition (under its source, by
            /// its trigger and target, then by order among identical ones). One that names nothing is kept.
            /// </summary>
            public void ResolveAnchors(HashSet<string> vertexIds)
            {
                foreach (var (v, nodes) in _anchors)
                {
                    var used = new HashSet<Transition>();
                    v.Anchors = nodes.Select(a =>
                    {
                        if (a.Kind == JsonKind.String)
                        {
                            var id = Resolve(a.Str);
                            return vertexIds.Contains(id) ? id : Unresolved + a.Str;
                        }
                        var source = Resolve(Text(a, "source"));
                        var trigger = string.Join(", ", SplitEvents(Text(a, "trigger")));
                        var reference = a.Field("target")?.Value.Str;
                        var target = reference == null ? null : Resolve(reference);
                        var t = Model.Transitions.FirstOrDefault(x =>
                            !used.Contains(x) && x.Source == source && string.Join(", ", x.Triggers) == trigger
                            && (target == null ? x.Kind == TransitionKind.Internal : x.Kind != TransitionKind.Internal && x.Target == target));
                        if (t == null) return UnresolvedAnchor + Json.Stringify(Json.Plain(a));
                        used.Add(t);
                        return t.Id;
                    }).ToList();
                }
            }
        }

        /// <summary>Reads a model file. Its <see cref="ReadMachine.Problems"/> say why when it can't be read (not JSON, or a wrong shape).</summary>
        public static ReadMachine ReadModel(string text)
        {
            JsonNode root;
            try
            {
                root = Json.Parse(text);
            }
            catch (JsonSyntaxException e)
            {
                return new ReadMachine { Problems = new List<Problem> { new Problem(e.Line, e.Message) } };
            }
            var problems = ShapeProblems(root);
            if (problems.Count > 0) return new ReadMachine { Problems = problems };
            var name = Text(root, "name");
            var model = new FsmModel
            {
                Name = name,
                Kind = Text(root, "type") == "protocol" ? MachineKind.Protocol : MachineKind.Behavioral,
                Documentation = Text(root, "documentation"),
            };
            var b = new Builder(model);
            var regionSources = new List<(JsonNode Node, string Id, string Owner)>();
            var regs = root.Field("regions");
            if (regs != null)
            {
                foreach (var r in regs.Value.Items)
                {
                    var rname = Text(r, "name");
                    var rid = b.Named(rname);
                    b.Lines[rid] = r.Line;
                    model.Regions.Add(new Region { Id = rid, Name = rname });
                    regionSources.Add((r, rid, rname));
                }
            }
            else
            {
                var rid = b.Alloc($"{name}.");
                model.Regions.Add(new Region { Id = rid, Name = "" });
                regionSources.Add((root, rid, name));
            }
            foreach (var (regionNode, regionId, owner) in regionSources) b.Region(regionNode, regionId, owner);
            b.PointsOf(root, "", name, model.Regions[0].Id, false);

            var count = new Dictionary<string, int>();
            foreach (var (source, node, isDefault) in b.Pending)
            {
                var n = (count.TryGetValue(source, out var c) ? c : 0) + 1;
                count[source] = n;
                var id = b.Alloc($"{source}#{n}");
                b.Lines[id] = node.Line;
                var ty = Text(node, "type");
                var kind = ty == "internal" ? TransitionKind.Internal : ty == "local" ? TransitionKind.Local : TransitionKind.External;
                var reference = node.Field("target")?.Value.Str;
                var target = reference != null ? b.Resolve(reference) : kind == TransitionKind.Internal && !isDefault ? source : Unresolved;
                model.Transitions.Add(new Transition
                {
                    Id = id, Source = source, Target = target, Kind = kind, Triggers = SplitEvents(Text(node, "trigger")),
                    Guard = Text(node, "guard"), Action = Text(node, "action"),
                    Precondition = Text(node, "precondition"), Postcondition = Text(node, "postcondition"),
                });
            }
            b.ResolveAnchors(new HashSet<string>(model.Vertices.Select(v => v.Id)));
            return new ReadMachine { Model = model, Lines = b.Lines };
        }

        // ---------------------------------------------------------------- references

        /// <summary>The value <see cref="Index.Anchor"/> gives an anchor the file can't hold.</summary>
        private static readonly object NotHeld = new object();

        /// <summary>What the writer needs to know about the model: owners, names and references.</summary>
        private sealed class Index
        {
            public readonly FsmModel Model;
            public readonly Dictionary<string, Vertex> V = new Dictionary<string, Vertex>();
            public readonly Dictionary<string, Transition> T = new Dictionary<string, Transition>();
            public readonly Dictionary<string, Vertex> RegionOwner = new Dictionary<string, Vertex>();
            private readonly Dictionary<string, Region> _regions = new Dictionary<string, Region>();
            private readonly HashSet<string> _top;

            public Index(FsmModel model)
            {
                Model = model;
                foreach (var r in model.Regions)
                {
                    RegionOwner[r.Id] = null;
                    _regions[r.Id] = r;
                }
                foreach (var v in model.Vertices)
                {
                    if (!V.ContainsKey(v.Id)) V[v.Id] = v;
                    foreach (var r in v.Regions)
                    {
                        RegionOwner[r.Id] = v;
                        _regions[r.Id] = r;
                    }
                }
                foreach (var t in model.Transitions)
                {
                    if (!T.ContainsKey(t.Id)) T[t.Id] = t;
                }
                _top = new HashSet<string>(model.Regions.Select(r => r.Id));
            }

            public Vertex Get(string id) => id != null && V.TryGetValue(id, out var v) ? v : null;

            public bool IsBorder(Vertex v) =>
                v.Type == VertexType.ConnectionPointRef || ((v.Type == VertexType.EntryPoint || v.Type == VertexType.ExitPoint) && V.ContainsKey(v.Parent));

            public bool IsMachinePoint(Vertex v) => (v.Type == VertexType.EntryPoint || v.Type == VertexType.ExitPoint) && _top.Contains(v.Parent);

            /// <summary>The owner of a region's history pseudostates in references: its state, the region when there are several, the machine.</summary>
            private string RegionRefOwner(string regionId)
            {
                RegionOwner.TryGetValue(regionId, out var owner);
                _regions.TryGetValue(regionId, out var r);
                if (owner != null) return owner.Regions.Count > 1 ? r?.Name ?? "" : owner.Name;
                return Model.Regions.Count > 1 ? r?.Name ?? "" : Model.Name;
            }

            /// <summary>The point name of a border vertex or machine point.</summary>
            public string PointName(Vertex v) => v.Type == VertexType.ConnectionPointRef ? v.Ref : v.Name;

            /// <summary>The reference to a vertex, or null when it can't be referenced (an initial pseudostate).</summary>
            public string Ref(string id)
            {
                var u = UnresolvedOf(id);
                if (u != null) return u;
                var v = Get(id);
                if (v == null) return id;
                if (v.Type == VertexType.Initial) return null;
                if (v.Type == VertexType.ShallowHistory || v.Type == VertexType.DeepHistory)
                {
                    return $"{RegionRefOwner(v.Parent)}.{(v.Type == VertexType.ShallowHistory ? "history" : "deepHistory")}";
                }
                if (IsBorder(v)) return $"{Get(v.Parent)?.Name ?? ""}.{PointName(v)}";
                if (IsMachinePoint(v)) return $"{Model.Name}.{v.Name}";
                return v.Name;
            }

            public Vertex Owner(Vertex v)
            {
                if (IsBorder(v)) return Get(v.Parent);
                return v.Parent != null && RegionOwner.TryGetValue(v.Parent, out var o) ? o : null;
            }

            /// <summary>The innermost state that contains both ends of <paramref name="t"/> (or is one of them), or null: its bend points are relative to it.</summary>
            public Vertex Frame(Transition t)
            {
                List<Vertex> Chain(string id)
                {
                    var output = new List<Vertex>();
                    var v = Get(id);
                    var seen = new HashSet<Vertex>();
                    if (v != null && v.Type != VertexType.State) v = Owner(v);
                    while (v != null && seen.Add(v))
                    {
                        output.Add(v);
                        v = Owner(v);
                    }
                    return output;
                }
                var s = Chain(t.Source);
                var g = new HashSet<Vertex>(Chain(t.Target));
                return s.FirstOrDefault(g.Contains);
            }

            /// <summary>
            /// A comment's anchor as the file writes it: a reference (string), or for a transition <c>{ source, trigger,
            /// target }</c> (<see cref="OutObject"/>); one that named nothing as it was read. <see cref="NotHeld"/> when
            /// the file can't hold it (an initial pseudostate, a transition from one), null when it names nothing in the model.
            /// </summary>
            public object Anchor(string a)
            {
                if (a.StartsWith(Unresolved, StringComparison.Ordinal)) return a.Substring(1);
                if (a.StartsWith(UnresolvedAnchor, StringComparison.Ordinal)) return Json.ParseObject(a.Substring(1));
                if (V.ContainsKey(a)) return (object)Ref(a) ?? NotHeld;
                if (!T.TryGetValue(a, out var t)) return null;
                var source = Ref(t.Source);
                if (source == null || !V.ContainsKey(t.Source)) return NotHeld;
                var ao = new OutObject { ["source"] = source };
                if (t.Triggers.Count > 0) ao["trigger"] = string.Join(", ", t.Triggers);
                if (t.Kind != TransitionKind.Internal)
                {
                    var target = Ref(t.Target);
                    if (target != null) ao["target"] = target;
                }
                return ao;
            }
        }

        /// <summary>
        /// How <paramref name="model"/> refers to its element or transition <paramref name="id"/> in a comment's anchor,
        /// kept as a reference that names nothing (<see cref="Unresolved"/> or <see cref="UnresolvedAnchor"/> followed by
        /// it): for a model where it doesn't exist (yet). Null when it can't be referred to.
        /// </summary>
        public static string UnresolvedAnchorOf(FsmModel model, string id)
        {
            var a = new Index(model).Anchor(id);
            if (a == null || a == NotHeld) return null;
            return a is string s ? Unresolved + s : UnresolvedAnchor + Json.Stringify(a);
        }

        /// <summary>How <paramref name="model"/> refers to its vertex <paramref name="id"/> (a transition's target), or null when it can't be referred to.</summary>
        public static string ReferenceOf(FsmModel model, string id) => new Index(model).Ref(id);

        // ---------------------------------------------------------------- writing

        private static string Label(VertexType t)
        {
            switch (t)
            {
                case VertexType.Terminate: return "terminate pseudostate";
                default: return t.Label();
            }
        }

        private static string KindKey(TransitionKind k) => k == TransitionKind.Internal ? "internal" : k == TransitionKind.Local ? "local" : "external";

        private static bool Finite(double n) => !double.IsNaN(N1(n)) && !double.IsInfinity(N1(n));

        private static OutObject Xy(double x, double y) => new OutObject { ["x"] = N1(x), ["y"] = N1(y) };

        /// <summary>Writes a machine: its model and layout files. Names must have been given (<see cref="AssignNames"/>).</summary>
        public static Written WriteMachine(FsmModel model)
        {
            var ix = new Index(model);
            var lost = new List<string>();
            var done = new HashSet<Vertex>();
            var doneT = new HashSet<Transition>();
            var ids = new Dictionary<OutObject, string>();
            var tids = new Dictionary<OutObject, string>();
            string Describe(Vertex v) => v.Name.Length > 0 ? $"{Label(v.Type)} '{v.Name}'" : $"an unnamed {Label(v.Type)}";
            var outgoing = new Dictionary<string, List<Transition>>();
            foreach (var t in model.Transitions)
            {
                if (outgoing.TryGetValue(t.Source, out var l)) l.Add(t);
                else outgoing[t.Source] = new List<Transition> { t };
            }
            List<Transition> Out(string id) => outgoing.TryGetValue(id, out var l) ? l : new List<Transition>();
            string DescribeT(Transition t)
            {
                var s = ix.Get(t.Source);
                return $"the transition from {(s != null ? Describe(s) : $"'{t.Source}'")}{(t.Triggers.Count > 0 ? $" on {string.Join(", ", t.Triggers)}" : "")}";
            }

            // A vertex's place relative to `at` (a place that isn't a number, from an old file, is the origin's).
            OutObject Position(Vertex v, (double X, double Y) at)
            {
                var dx = v.X - at.X;
                var dy = v.Y - at.Y;
                if (Finite(dx) && Finite(dy)) return Xy(dx, dy);
                lost.Add($"the place of {Describe(v)}, which is not a number");
                return Xy(Finite(dx) ? dx : 0, Finite(dy) ? dy : 0);
            }
            void Colors(string color, Dictionary<string, string> itemColors, OutObject into)
            {
                if (ElementColors.IsColor(color)) into["color"] = color;
                var items = new OutObject();
                foreach (var k in ElementColors.ItemKeys)
                {
                    if (itemColors.TryGetValue(k, out var c) && ElementColors.IsColor(c)) items[ItemColorFileKey[k]] = c;
                }
                if (items.Count > 0) into["itemColors"] = items;
            }
            // Where the places inside `v` are measured from (its own place, when it is a number: else written as the origin's).
            (double X, double Y) Origin(Vertex v) => (v != null && Finite(v.X) ? v.X : 0, v != null && Finite(v.Y) ? v.Y : 0);

            OutObject TransitionLayout(Transition t, string trigger, string target)
            {
                var l = new OutObject();
                if (!string.IsNullOrEmpty(trigger)) l["trigger"] = trigger;
                if (target != null) l["target"] = target;
                var keys = l.Count;
                if (t.Points.Count > 0 && t.Kind != TransitionKind.Internal)
                {
                    var f = Origin(ix.Frame(t));
                    var bends = t.Points.Where(p => Finite(p.X - f.X) && Finite(p.Y - f.Y)).ToList();
                    if (bends.Count < t.Points.Count) lost.Add($"bend points of {DescribeT(t)} that are not numbers");
                    if (bends.Count > 0) l["bends"] = bends.Select(p => (object)Xy(p.X - f.X, p.Y - f.Y)).ToList();
                }
                if (t.LabelOffset is PointD lo && t.Kind != TransitionKind.Internal)
                {
                    if (Finite(lo.X) && Finite(lo.Y)) l["label"] = Xy(lo.X, lo.Y);
                    else lost.Add($"the label position of {DescribeT(t)}, which is not a number");
                }
                Colors(t.Color, t.ItemColors, l);
                return l.Count > keys ? l : null;
            }
            // A transition's fields in the model file, its layout entry (null when it has nothing to store) and that
            // entry without what it stores; null when the file can't hold it (a transition to an initial pseudostate).
            (OutObject O, OutObject Layout, OutObject Key)? TransitionOf(Transition t)
            {
                doneT.Add(t);
                var o = new OutObject { ["type"] = KindKey(t.Kind) };
                if (t.Triggers.Count > 0) o["trigger"] = string.Join(", ", t.Triggers);
                if (t.Guard.Length > 0) o["guard"] = t.Guard;
                if (t.Action.Length > 0) o["action"] = t.Action;
                if (t.Precondition.Length > 0) o["precondition"] = t.Precondition;
                if (t.Postcondition.Length > 0) o["postcondition"] = t.Postcondition;
                string target = null;
                if (t.Kind == TransitionKind.Internal)
                {
                    if (t.Target != t.Source) lost.Add($"the target of the internal {DescribeT(t).Substring(4)}");
                }
                else
                {
                    target = ix.Ref(t.Target);
                    if (target == null)
                    {
                        lost.Add($"{DescribeT(t)} to an initial pseudostate");
                        return null;
                    }
                    o["target"] = target;
                }
                tids[o] = t.Id;
                var key = new OutObject();
                if (o["trigger"] != null) key["trigger"] = o["trigger"];
                if (target != null) key["target"] = target;
                return (o, TransitionLayout(t, o["trigger"] as string, target), key);
            }
            // The transitions leaving `v`, into the model object and layout entry. Layout entries are found by trigger
            // and target, then by order among identical ones: a transition with nothing to store gets an entry anyway
            // (its trigger and target) when a later one with the same trigger and target has one, so that each keeps its own.
            void TransitionsOf(Vertex v, OutObject o, OutObject l)
            {
                var list = Out(v.Id);
                if (list.Count == 0) return;
                var mo = new List<object>();
                var written = new List<(OutObject Entry, OutObject Key, string K)>();
                foreach (var t in list)
                {
                    var w = TransitionOf(t);
                    if (w == null) continue;
                    mo.Add(w.Value.O);
                    written.Add((w.Value.Layout, w.Value.Key, Json.Stringify(w.Value.Key)));
                }
                var lo = new List<object>();
                for (var i = 0; i < written.Count; i++)
                {
                    var (entry, key, k) = written[i];
                    if (entry != null) lo.Add(entry);
                    else if (written.Skip(i + 1).Any(x => x.Entry != null && x.K == k)) lo.Add(key);
                }
                if (mo.Count > 0) o["transitions"] = mo;
                if (lo.Count > 0) l["transitions"] = lo;
            }

            void PointsOf(Vertex owner, List<Vertex> points, OutObject o, OutObject l)
            {
                if (points.Count == 0) return;
                var mo = new List<object>();
                var lo = new List<object>();
                var at = Origin(owner);
                foreach (var p in points)
                {
                    done.Add(p);
                    // On a submachine state, the points are references to the submachine's; on another state, its own.
                    if (owner != null && (p.Type == VertexType.ConnectionPointRef) != (owner.Submachine.Length > 0))
                    {
                        lost.Add($"{Describe(p)} on {Describe(owner)}, which {(owner.Submachine.Length > 0 ? "is" : "is not")} a submachine state");
                        foreach (var t in Out(p.Id)) doneT.Add(t);
                        continue;
                    }
                    var kind = p.Type == VertexType.ConnectionPointRef ? (p.PointKind == PointKind.Exit ? "exitPoint" : "entryPoint") : p.Type.Key();
                    var po = new OutObject { ["name"] = ix.PointName(p), ["type"] = kind };
                    var pl = new OutObject { ["name"] = ix.PointName(p) };
                    pl.Assign(Position(p, at));
                    Colors(p.Color, p.ItemColors, pl);
                    if (p.Group.Length > 0 && owner == null) pl["group"] = p.Group;
                    TransitionsOf(p, po, pl);
                    ids[po] = p.Id;
                    mo.Add(po);
                    lo.Add(pl);
                }
                if (mo.Count > 0) o["points"] = mo;
                if (lo.Count > 0) l["points"] = lo;
            }

            (OutObject, OutObject) Element(Vertex v, (double X, double Y) at)
            {
                done.Add(v);
                var o = new OutObject { ["name"] = v.Name, ["type"] = v.Type.Key() };
                var l = new OutObject { ["name"] = v.Name };
                l.Assign(Position(v, at));
                if (IsResizable(v.Type))
                {
                    var (dw, dh) = DefaultSize(v.Type);
                    l["width"] = N1(Finite(v.W) && v.W > 0 ? v.W : dw);
                    l["height"] = N1(Finite(v.H) && v.H > 0 ? v.H : dh);
                }
                Colors(v.Color, v.ItemColors, l);
                if (v.Group.Length > 0) l["group"] = v.Group;
                ids[o] = v.Id;
                switch (v.Type)
                {
                    case VertexType.State:
                    {
                        if (v.Stereotype.Length > 0) o["stereotype"] = v.Stereotype;
                        if (v.Submachine.Length > 0) o["submachine"] = v.Submachine;
                        if (v.Entry.Length > 0) o["entry"] = v.Entry;
                        if (v.DoActivity.Length > 0) o["do"] = v.DoActivity;
                        if (v.Exit.Length > 0) o["exit"] = v.Exit;
                        if (v.Deferrable.Count > 0) o["defer"] = string.Join(", ", v.Deferrable);
                        if (v.Invariant.Length > 0) o["invariant"] = v.Invariant;
                        if (v.Regions.Count > 1 && v.RegionLayout != null) l["regionLayout"] = v.RegionLayout == RegionLayout.Horizontal ? "horizontal" : "vertical";
                        TransitionsOf(v, o, l);
                        var points = model.Vertices.Where(p => p.Parent == v.Id && (p.Type == VertexType.EntryPoint || p.Type == VertexType.ExitPoint || p.Type == VertexType.ConnectionPointRef)).ToList();
                        PointsOf(v, points, o, l);
                        if (v.Regions.Count > 0) RegionsOf(v, v.Regions, o, l);
                        break;
                    }
                    case VertexType.Comment:
                    {
                        if (v.Text.Length > 0) o["text"] = v.Text;
                        var anchors = new List<object>();
                        foreach (var a in v.Anchors)
                        {
                            var w = ix.Anchor(a);
                            if (w == null) lost.Add($"the anchor of {Describe(v)} to '{a}', which is not an element or a transition");
                            else if (w != NotHeld) anchors.Add(w);
                            else if (ix.V.ContainsKey(a)) lost.Add($"the anchor of {Describe(v)} to an initial pseudostate");
                            else lost.Add($"the anchor of {Describe(v)} to {DescribeT(ix.T[a])}");
                        }
                        if (anchors.Count > 0) o["anchors"] = anchors;
                        break;
                    }
                    case VertexType.Final:
                    case VertexType.Terminate:
                        if (v.Regions.Count > 0 || v.Entry.Length > 0 || v.Exit.Length > 0 || v.DoActivity.Length > 0) lost.Add($"the regions and behaviors of {Describe(v)}");
                        if (Out(v.Id).Count > 0)
                        {
                            foreach (var t in Out(v.Id)) doneT.Add(t);
                            lost.Add($"the transitions leaving {Describe(v)}");
                        }
                        break;
                    default:
                        TransitionsOf(v, o, l);
                        break;
                }
                return (o, l);
            }

            // The fields of region `r` (owned by `owner`, null at the top level) into `o` and `l`.
            void RegionFieldsOf(Region r, Vertex owner, OutObject o, OutObject l)
            {
                // Each element is written once (a region id used twice in a file edited by hand could hold a state's own owner).
                var all = model.Vertices.Where(v => v.Parent == r.Id && !ix.IsMachinePoint(v) && !ix.IsBorder(v)).ToList();
                var kids = all.Where(v => !done.Contains(v)).ToList();
                if (kids.Count < all.Count) lost.Add($"the region of {(owner != null ? Describe(owner) : "the state machine")} as it was: its id is used twice, and its elements are written once");
                var at = Origin(owner);
                foreach (var (key, type) in Pseudo)
                {
                    var found = kids.Where(v => v.Type == type).ToList();
                    if (found.Count == 0) continue;
                    var v = found[0];
                    foreach (var x in found.Skip(1))
                    {
                        done.Add(x);
                        lost.Add($"{Describe(x)}: a region has at most one");
                    }
                    done.Add(v);
                    var po = new OutObject();
                    var pl = Position(v, at);
                    if (v.Group.Length > 0) pl["group"] = v.Group;
                    var outs = Out(v.Id);
                    if (outs.Count > 0)
                    {
                        var t = outs[0];
                        doneT.Add(t);
                        var target = ix.Ref(t.Target);
                        if (target == null) lost.Add($"the {key} transition to an initial pseudostate");
                        else if (target != "" || UnresolvedOf(t.Target) == null) po["target"] = target;
                        if (t.Action.Length > 0) po["action"] = t.Action;
                        if (t.Guard.Length > 0 || t.Triggers.Count > 0 || t.Kind != TransitionKind.External || t.Precondition.Length > 0 || t.Postcondition.Length > 0)
                        {
                            lost.Add($"the guard, triggers, kind or conditions of the {key} transition");
                        }
                        // Its route, when the file holds the transition (a target or an action).
                        var tl = TransitionLayout(t, null, null);
                        if (tl != null && (po["target"] != null || po["action"] != null)) pl.Assign(tl);
                        tids[po] = t.Id;
                    }
                    foreach (var x in outs.Skip(1))
                    {
                        doneT.Add(x);
                        lost.Add($"a second transition leaving the {key} pseudostate");
                    }
                    ids[po] = v.Id;
                    o[key] = po;
                    l[key] = pl;
                }
                var mo = new List<object>();
                var lo = new List<object>();
                foreach (var v in kids)
                {
                    if (v.Type == VertexType.Initial || v.Type == VertexType.ShallowHistory || v.Type == VertexType.DeepHistory || done.Contains(v)) continue;
                    if (!IsElementKind(v.Type))
                    {
                        lost.Add($"{Describe(v)}, which only goes on the border of a state or at the top level");
                        continue;
                    }
                    var (eo, el) = Element(v, at);
                    mo.Add(eo);
                    lo.Add(el);
                }
                // A composite state's only region, empty: written so that the state stays composite.
                if (mo.Count > 0 || (owner != null && owner.Regions.Count == 1 && o["initial"] == null && o["history"] == null && o["deepHistory"] == null)) o["elements"] = mo;
                if (lo.Count > 0) l["elements"] = lo;
            }

            void RegionsOf(Vertex owner, List<Region> regions, OutObject o, OutObject l)
            {
                if (regions.Count == 1)
                {
                    RegionFieldsOf(regions[0], owner, o, l);
                    return;
                }
                var mo = new List<object>();
                var lo = new List<object>();
                foreach (var r in regions)
                {
                    var ro = new OutObject { ["name"] = r.Name };
                    var rl = new OutObject { ["name"] = r.Name };
                    if (owner != null && r.Size is double size && Finite(size) && N1(size) > 0) rl["size"] = N1(size);
                    RegionFieldsOf(r, owner, ro, rl);
                    ids[ro] = r.Id;
                    mo.Add(ro);
                    lo.Add(rl);
                }
                o["regions"] = mo;
                l["regions"] = lo;
            }

            var mroot = new OutObject { ["format"] = 1.0, ["name"] = model.Name, ["type"] = model.Kind == MachineKind.Protocol ? "protocol" : "behavioral" };
            var lroot = new OutObject { ["format"] = 1.0 };
            if (model.Documentation.Length > 0) mroot["documentation"] = model.Documentation;
            PointsOf(null, model.Vertices.Where(ix.IsMachinePoint).ToList(), mroot, lroot);
            RegionsOf(null, model.Regions, mroot, lroot);
            foreach (var v in model.Vertices)
            {
                if (!done.Contains(v)) lost.Add($"{Describe(v)}, which is not inside a known region");
            }
            foreach (var t in model.Transitions)
            {
                if (doneT.Contains(t)) continue;
                var s = ix.Get(t.Source);
                lost.Add(s != null && s.Type == VertexType.Comment ? $"a transition leaving {Describe(s)}" : $"{DescribeT(t)}, whose source does not exist");
            }

            var lines = new Dictionary<string, int>();
            var modelText = Json.Write(mroot, o => o.Entries.All(e => !Json.IsComposite(e.Value)), (o, line) =>
            {
                if (ids.TryGetValue(o, out var id) && !lines.ContainsKey(id)) lines[id] = line;
                if (tids.TryGetValue(o, out var tid) && !lines.ContainsKey(tid)) lines[tid] = line;
            });
            var layoutText = Json.Write(lroot, o =>
            {
                var e = o.Entries;
                return e.Count == 2 && e[0].Key == "x" && e[1].Key == "y";
            });
            return new Written { Model = modelText, Layout = layoutText, Lines = lines, Lost = lost };
        }

        // ---------------------------------------------------------------- names

        /// <summary>Prefix of the names generated for each kind of element.</summary>
        private static string Generated(VertexType t)
        {
            switch (t)
            {
                case VertexType.EntryPoint: return "entry";
                case VertexType.ExitPoint: return "exit";
                default: return t.Key();
            }
        }

        /// <summary>Whether a name is a generated one, not shown on the diagram.</summary>
        public static bool IsGenerated(string name) => name != null && name.EndsWith("*", StringComparison.Ordinal);

        /// <summary>A name as the diagram shows it: nothing for a generated one.</summary>
        public static string Shown(string name) => IsGenerated(name) ? "" : name ?? "";

        private static readonly Regex JsSpaces = new Regex(JsWhitespace.Class + "+");

        private sealed class Named
        {
            public Func<string> Get;
            public Action<string> Set;
            public string What;
            public string Prefix;
        }

        /// <summary>
        /// Gives every element, point and region that needs a name one: a generated name (<c>choice1*</c>, smallest free
        /// number) for those without. With <paramref name="fix"/> (converting an old file), it also replaces spaces,
        /// <c>*</c> and <c>.</c> in names with <c>_</c>, numbers the names used twice (<c>Idle2</c>), and renames points
        /// named <c>history</c> or <c>deepHistory</c>; it returns the renames. Without it, a name used twice stays as it
        /// is (the validator reports it, and the file is saved back as it was). Changes <paramref name="model"/> in place.
        /// </summary>
        public static List<Rename> AssignNames(FsmModel model, bool fix = false)
        {
            var renames = new List<Rename>();
            var ix = new Index(model);
            string Clean(string s) => fix ? JsSpaces.Replace(JsWhitespace.Trim(s), "_").Replace('*', '_').Replace('.', '_') : s;
            var taken = new HashSet<string>();
            var machine = Clean(model.Name);
            if (machine != model.Name) renames.Add(new Rename { What = "the state machine", From = model.Name, To = machine });
            model.Name = machine;
            taken.Add(machine);
            // The named things in document order: regions of an owner with several, elements.
            var named = new List<Named>();
            void RegionsNamed(List<Region> regions, string owner)
            {
                if (regions.Count < 2) return;
                for (var i = 0; i < regions.Count; i++)
                {
                    var r = regions[i];
                    named.Add(new Named { Get = () => r.Name, Set = n => r.Name = n, What = $"region {i + 1} of {owner}", Prefix = "region" });
                }
            }
            RegionsNamed(model.Regions, "the state machine");
            foreach (var v in model.Vertices)
            {
                if (!IsElementKind(v.Type)) continue;
                named.Add(new Named { Get = () => v.Name, Set = n => v.Name = n, What = Label(v.Type), Prefix = Generated(v.Type) });
                if (v.Type == VertexType.State) RegionsNamed(v.Regions, v.Name.Length > 0 ? $"'{v.Name}'" : "a state");
            }
            // Names kept first (the first of each), then the others.
            var later = new List<Named>();
            foreach (var n in named)
            {
                var name = Clean(n.Get());
                if (name.Length > 0 && !taken.Contains(name))
                {
                    if (name != n.Get()) renames.Add(new Rename { What = $"the {n.What}", From = n.Get(), To = name });
                    n.Set(name);
                    taken.Add(name);
                }
                else later.Add(n);
            }
            string Generate(string prefix, HashSet<string> used)
            {
                for (var i = 1; ; i++)
                {
                    if (!used.Contains($"{prefix}{i}*")) return $"{prefix}{i}*";
                }
            }
            foreach (var n in later)
            {
                var old = n.Get();
                var baseName = Clean(old);
                string name;
                if (baseName.Length == 0) name = Generate(n.Prefix, taken);
                else if (!fix) continue;
                else
                {
                    var i = 2;
                    while (taken.Contains($"{baseName}{i}")) i++;
                    name = $"{baseName}{i}";
                    renames.Add(new Rename { What = $"the {n.What}", From = old, To = name, Why = "its name was used twice" });
                }
                n.Set(name);
                taken.Add(name);
            }
            // Points: unique on their owner.
            var owners = new List<string>();
            var byOwner = new Dictionary<string, List<Vertex>>();
            foreach (var v in model.Vertices)
            {
                if (!(ix.IsBorder(v) || ix.IsMachinePoint(v)) || v.Type == VertexType.ConnectionPointRef) continue;
                var key = ix.IsMachinePoint(v) ? "" : v.Parent;
                if (!byOwner.TryGetValue(key, out var list))
                {
                    byOwner[key] = list = new List<Vertex>();
                    owners.Add(key);
                }
                list.Add(v);
            }
            foreach (var key in owners)
            {
                var points = byOwner[key];
                var used = new HashSet<string>(points.Select(p => p.Name).Where(x => x.Length > 0));
                var seen = new HashSet<string>();
                foreach (var p in points)
                {
                    var name = Clean(p.Name);
                    if (fix && (name == "history" || name == "deepHistory")) name += "2";
                    if (name.Length > 0 && !seen.Contains(name))
                    {
                        if (name != p.Name) renames.Add(new Rename { What = $"the {Label(p.Type)}", From = p.Name, To = name });
                    }
                    else if (name.Length == 0) name = Generate(Generated(p.Type), used);
                    else if (fix)
                    {
                        var i = 2;
                        while (seen.Contains($"{name}{i}") || used.Contains($"{name}{i}")) i++;
                        renames.Add(new Rename { What = $"the {Label(p.Type)}", From = p.Name, To = $"{name}{i}", Why = "its name was used twice on its state" });
                        name = $"{name}{i}";
                    }
                    p.Name = name;
                    seen.Add(name);
                    used.Add(name);
                }
            }
            return renames;
        }

        // ---------------------------------------------------------------- layout

        private static bool IsNum(JsonNode n) => n != null && n.Kind == JsonKind.Number;

        /// <summary>Problems of the layout file's shape (the first one, enough for the warning), or null.</summary>
        public static string LayoutShapeProblem(JsonNode root)
        {
            var problems = new List<Problem>();
            void Add(int line, string message) => problems.Add(new Problem(line, message));
            var itemKeys = ItemColorFileKey.Values.ToList();
            void Check(JsonNode node, string what, IReadOnlyCollection<string> allowed, IEnumerable<string> required, Action<JsonField> each)
            {
                var fields = node.Fields;
                if (fields == null)
                {
                    Add(node.Line, $"{what} must be an object.");
                    return;
                }
                foreach (var f in fields)
                {
                    if (allowed.Contains(f.Key)) each(f);
                    else Add(f.Line, $"Unknown field '{f.Key}' in {what}.");
                }
                foreach (var r in required)
                {
                    if (!fields.Any(f => f.Key == r)) Add(node.Line, $"Missing field '{r}' in {what}.");
                }
                if (fields.Any(f => f.Key == "regions"))
                {
                    foreach (var f in fields)
                    {
                        if (RegionFields.Contains(f.Key)) Add(f.Line, $"Field '{f.Key}' cannot be used with 'regions'.");
                    }
                }
            }
            void Point(JsonNode n, string what) =>
                Check(n, what, new[] { "x", "y" }, new[] { "x", "y" }, f =>
                {
                    if (!IsNum(f.Value)) Add(f.Value.Line, $"Field '{f.Key}' must be a number.");
                });
            void ListOfItems(JsonField f, Action<JsonNode> item)
            {
                var items = f.Value.Items;
                if (items == null)
                {
                    Add(f.Value.Line, $"Field '{f.Key}' must be a list.");
                    return;
                }
                foreach (var x in items) item(x);
            }
            void Common(JsonField f)
            {
                switch (f.Key)
                {
                    case "x":
                    case "y":
                    case "width":
                    case "height":
                    case "size":
                        if (!IsNum(f.Value)) Add(f.Value.Line, $"Field '{f.Key}' must be a number.");
                        return;
                    case "name":
                    case "trigger":
                    case "target":
                    case "group":
                        if (f.Value.Kind != JsonKind.String) Add(f.Value.Line, $"Field '{f.Key}' must be a text.");
                        return;
                    case "color":
                        if (!ElementColors.IsColor(f.Value.Str)) Add(f.Value.Line, "Field 'color' must be a color (#rrggbb).");
                        return;
                    case "itemColors":
                        Check(f.Value, "'itemColors'", itemKeys, new string[0], g =>
                        {
                            if (!ElementColors.IsColor(g.Value.Str)) Add(g.Value.Line, $"Field '{g.Key}' must be a color (#rrggbb).");
                        });
                        return;
                    case "label":
                        Point(f.Value, "'label'");
                        return;
                    case "bends":
                        ListOfItems(f, n => Point(n, "a bend point"));
                        return;
                    case "regionLayout":
                        if (f.Value.Str != "horizontal" && f.Value.Str != "vertical") Add(f.Value.Line, "Field 'regionLayout' must be \"horizontal\" or \"vertical\".");
                        return;
                    case "transitions":
                        ListOfItems(f, n => Check(n, "a transition", new[] { "trigger", "target", "bends", "label", "color", "itemColors" }, new string[0], Common));
                        return;
                    case "points":
                        ListOfItems(f, n => Check(n, "a point", new[] { "name", "x", "y", "color", "itemColors", "group", "transitions" }, new[] { "name" }, Common));
                        return;
                    case "initial":
                    case "history":
                    case "deepHistory":
                        Check(f.Value, $"'{f.Key}'", new[] { "x", "y", "group", "bends", "label", "color", "itemColors" }, new string[0], Common);
                        return;
                    case "elements":
                        ListOfItems(f, n => Check(n, "an element",
                            new[] { "name", "x", "y", "width", "height", "color", "itemColors", "group", "regionLayout", "transitions", "points", "regions" }.Concat(RegionFields).ToList(),
                            new[] { "name" }, Common));
                        return;
                    case "regions":
                        ListOfItems(f, n => Check(n, "a region", new[] { "name", "size" }.Concat(RegionFields).ToList(), new[] { "name" }, Common));
                        return;
                }
            }
            if (root.Fields == null) return "The layout file must hold a JSON object.";
            Check(root, "the layout", new[] { "$schema", "format", "points", "regions" }.Concat(RegionFields).ToList(), new[] { "format" }, f =>
            {
                if (f.Key == "format")
                {
                    if (f.Value.Number != 1) Add(f.Value.Line, "Field 'format' must be 1.");
                }
                else if (f.Key == "$schema")
                {
                    if (f.Value.Kind != JsonKind.String) Add(f.Value.Line, "Field '$schema' must be a text.");
                }
                else Common(f);
            });
            return problems.Count > 0 ? $"line {problems[0].Line}: {problems[0].Message}" : null;
        }

        private static Dictionary<string, string> ItemColorsOf(JsonNode node)
        {
            var ic = new Dictionary<string, string>();
            foreach (var f in node?.Fields ?? new List<JsonField>())
            {
                if (ItemColorFromFile.TryGetValue(f.Key, out var k) && ElementColors.IsColor(f.Value.Str)) ic[k] = f.Value.Str;
            }
            return ic;
        }

        /// <summary>
        /// Gives the vertices of a model just read (<see cref="ReadModel"/>) their places and the transitions their routes,
        /// from the layout file's text (null when there is none). Elements without an entry are placed automatically:
        /// default size, the first free spot beside the elements already in their region, their state growing to fit.
        /// Entries that match nothing are left out. Sets <see cref="ReadMachine.LayoutProblem"/> and <see cref="ReadMachine.Placed"/>.
        /// </summary>
        public static void ApplyLayout(ReadMachine read, string layoutText)
        {
            var model = read.Model;
            var ix = new Index(model);
            read.Placed = 0;
            read.LayoutProblem = null;
            JsonNode root = null;
            if (layoutText != null)
            {
                try
                {
                    root = Json.Parse(layoutText);
                    var problem = LayoutShapeProblem(root);
                    if (problem != null)
                    {
                        read.LayoutProblem = problem;
                        root = null;
                    }
                }
                catch (JsonSyntaxException e)
                {
                    read.LayoutProblem = $"line {e.Line}: {e.Message}";
                }
            }
            // Positions relative to the owner, before they are made absolute.
            var rel = new Dictionary<Vertex, PointD>();
            var placedV = new HashSet<Vertex>();
            var routes = new Dictionary<Transition, JsonNode>();
            double? NumOf(JsonNode n, string k) => n.Field(k)?.Value.Number;
            // The place, size, colors and group of an entry; `colors` false for an initial or history entry, whose are its arrow's.
            void SetBox(Vertex v, JsonNode entry, bool colors)
            {
                var x = NumOf(entry, "x");
                var y = NumOf(entry, "y");
                if (x != null && y != null)
                {
                    rel[v] = new PointD(x.Value, y.Value);
                    placedV.Add(v);
                }
                var (dw, dh) = DefaultSize(v.Type);
                var w = NumOf(entry, "width");
                var h = NumOf(entry, "height");
                v.W = IsResizable(v.Type) && w != null && w > 0 ? w.Value : dw;
                v.H = IsResizable(v.Type) && h != null && h > 0 ? h.Value : dh;
                var color = entry.Field("color")?.Value.Str;
                if (colors && ElementColors.IsColor(color)) v.Color = color;
                var items = colors ? entry.Field("itemColors")?.Value : null;
                if (items?.Fields != null)
                {
                    var ic = ItemColorsOf(items);
                    if (ic.Count > 0) v.ItemColors = ic;
                }
                var group = entry.Field("group")?.Value.Str;
                if (!string.IsNullOrEmpty(group)) v.Group = group;
            }
            // Matches transition entries to the transitions leaving the sources: by trigger and target, then by order.
            void MatchTransitions(string sourceId, List<JsonNode> entries)
            {
                var list = model.Transitions.Where(t => t.Source == sourceId).ToList();
                var used = new HashSet<Transition>();
                foreach (var e in entries)
                {
                    // The trigger as the model file may write it (`a,b`): the events it names.
                    var trigger = string.Join(", ", SplitEvents(e.Field("trigger")?.Value.Str ?? ""));
                    var target = e.Field("target")?.Value.Str;
                    var t = list.FirstOrDefault(x => !used.Contains(x) && string.Join(", ", x.Triggers) == trigger
                        && (x.Kind == TransitionKind.Internal ? target == null : ix.Ref(x.Target) == target));
                    if (t == null) continue;
                    used.Add(t);
                    routes[t] = e;
                }
            }
            void ApplyPoints(Vertex owner, JsonNode node)
            {
                var points = model.Vertices.Where(p => owner != null ? p.Parent == owner.Id && ix.IsBorder(p) : ix.IsMachinePoint(p)).ToList();
                var used = new HashSet<Vertex>();
                foreach (var e in ListOf(node, "points"))
                {
                    var name = e.Field("name")?.Value.Str;
                    var p = points.FirstOrDefault(x => !used.Contains(x) && ix.PointName(x) == name);
                    if (p == null) continue;
                    used.Add(p);
                    SetBox(p, e, true);
                    MatchTransitions(p.Id, ListOf(e, "transitions"));
                }
            }
            void ApplyRegion(string regionId, JsonNode node)
            {
                var kids = model.Vertices.Where(v => v.Parent == regionId && !ix.IsMachinePoint(v) && !ix.IsBorder(v)).ToList();
                foreach (var (key, type) in Pseudo)
                {
                    var e = node.Field(key)?.Value;
                    var v = kids.FirstOrDefault(x => x.Type == type);
                    if (e == null || v == null) continue;
                    SetBox(v, e, false);
                    var t = model.Transitions.FirstOrDefault(x => x.Source == v.Id);
                    if (t != null) routes[t] = e;
                }
                var used = new HashSet<Vertex>();
                foreach (var e in ListOf(node, "elements"))
                {
                    var name = e.Field("name")?.Value.Str;
                    var v = kids.FirstOrDefault(x => !used.Contains(x) && IsElementKind(x.Type) && x.Name == name);
                    if (v == null) continue;
                    used.Add(v);
                    SetBox(v, e, true);
                    if (v.Type != VertexType.State)
                    {
                        MatchTransitions(v.Id, ListOf(e, "transitions"));
                        continue;
                    }
                    var layout = e.Field("regionLayout")?.Value.Str;
                    if (layout == "horizontal") v.RegionLayout = RegionLayout.Horizontal;
                    else if (layout == "vertical") v.RegionLayout = RegionLayout.Vertical;
                    MatchTransitions(v.Id, ListOf(e, "transitions"));
                    ApplyPoints(v, e);
                    var regs = v.Regions;
                    var items = e.Field("regions")?.Value.Items;
                    if (regs.Count == 1 && items == null) ApplyRegion(regs[0].Id, e);
                    else if (regs.Count > 1 && items != null)
                    {
                        foreach (var item in items)
                        {
                            var r = regs.FirstOrDefault(x => x.Name == item.Field("name")?.Value.Str);
                            if (r == null) continue;
                            var size = NumOf(item, "size");
                            if (size != null && size > 0) r.Size = size;
                            ApplyRegion(r.Id, item);
                        }
                    }
                }
            }
            if (root != null)
            {
                ApplyPoints(null, root);
                var items = root.Field("regions")?.Value.Items;
                if (model.Regions.Count == 1 && items == null) ApplyRegion(model.Regions[0].Id, root);
                else if (model.Regions.Count > 1 && items != null)
                {
                    foreach (var item in items)
                    {
                        var r = model.Regions.FirstOrDefault(x => x.Name == item.Field("name")?.Value.Str);
                        if (r != null) ApplyRegion(r.Id, item);
                    }
                }
            }

            // Automatic places, region by region, innermost first: a state grows to hold what was placed in it
            // automatically (a state that grew is in turn new to its own region) before the elements beside it are placed.
            var auto = new HashSet<Vertex>();
            foreach (var v in model.Vertices)
            {
                if (v.W == 0 || v.H == 0 || double.IsNaN(v.W) || double.IsNaN(v.H)) (v.W, v.H) = DefaultSize(v.Type);
            }
            void Grow(Vertex v)
            {
                var regionIds = new HashSet<string>(v.Regions.Select(r => r.Id));
                var kids = model.Vertices.Where(k => regionIds.Contains(k.Parent) && !ix.IsBorder(k)).ToList();
                if (!kids.Any(auto.Contains)) return;
                var (w0, h0) = (v.W, v.H);
                foreach (var k in kids)
                {
                    var p = rel[k];
                    v.W = Math.Max(v.W, N1(p.X + k.W + 20));
                    v.H = Math.Max(v.H, N1(p.Y + k.H + 20));
                }
                if (v.W != w0 || v.H != h0) auto.Add(v);
            }
            // The model lists a state before what it contains, and a state's regions together: reversed, a state's
            // regions come after everything inside them, its first region last.
            var allRegionIds = model.Regions.Select(r => r.Id).ToList();
            foreach (var v in model.Vertices) allRegionIds.AddRange(v.Regions.Select(r => r.Id));
            allRegionIds.Reverse();
            foreach (var rid in allRegionIds)
            {
                ix.RegionOwner.TryGetValue(rid, out var owner);
                var kids = model.Vertices.Where(v => v.Parent == rid && !ix.IsBorder(v)).ToList();
                var index = owner != null ? owner.Regions.FindIndex(r => r.Id == rid) : 0;
                foreach (var v in kids)
                {
                    if (placedV.Contains(v)) continue;
                    var placed = kids.Where(placedV.Contains).Select(k => (P: rel[k], k.W, k.H)).ToList();
                    var p = placed.Count > 0
                        ? new PointD(placed.Max(b => b.P.X + b.W) + 40, placed.Min(b => b.P.Y))
                        : owner != null ? new PointD(30, 50 + Math.Max(0, index) * 80) : new PointD(40, 40);
                    rel[v] = new PointD(N1(p.X), N1(p.Y));
                    placedV.Add(v);
                    auto.Add(v);
                    read.Placed++;
                }
                if (owner != null && index == 0 && owner.Type == VertexType.State) Grow(owner);
            }
            // Points without a place: on their state's border, entries on the left, exits on the right.
            foreach (var v in model.Vertices)
            {
                if (!ix.IsBorder(v) || placedV.Contains(v)) continue;
                var owner = ix.Get(v.Parent);
                if (owner == null) continue;
                var same = model.Vertices.Where(p => p.Parent == owner.Id && ix.IsBorder(p) && !placedV.Contains(p) && p.Type == v.Type).ToList();
                var i = same.IndexOf(v);
                var exit = v.Type == VertexType.ExitPoint || (v.Type == VertexType.ConnectionPointRef && v.PointKind == PointKind.Exit);
                rel[v] = new PointD(N1(exit ? owner.W - v.W / 2 : -v.W / 2), N1(Math.Min(owner.H - v.H, 10 + i * 24)));
                read.Placed++;
            }
            // Absolute coordinates, owners first (the model lists a state before what it contains).
            var busy = new HashSet<Vertex>();
            PointD Abs(Vertex v)
            {
                if (!double.IsNaN(v.X)) return new PointD(v.X, v.Y);
                var owner = busy.Add(v) ? ix.Owner(v) : null;
                var o = owner != null ? Abs(owner) : new PointD(0, 0);
                var p = rel.TryGetValue(v, out var r) ? r : new PointD(0, 0);
                v.X = N1(o.X + p.X);
                v.Y = N1(o.Y + p.Y);
                return new PointD(v.X, v.Y);
            }
            foreach (var v in model.Vertices) Abs(v);
            // Routes
            foreach (var kv in routes)
            {
                var t = kv.Key;
                var e = kv.Value;
                var f = ix.Frame(t);
                var o = f != null ? new PointD(f.X, f.Y) : new PointD(0, 0);
                var bends = e.Field("bends")?.Value.Items;
                if (bends != null && bends.Count > 0 && t.Kind != TransitionKind.Internal)
                {
                    t.Points = bends.Select(b => new PointD(N1(o.X + NumOf(b, "x").Value), N1(o.Y + NumOf(b, "y").Value))).ToList();
                }
                var label = e.Field("label")?.Value;
                if (label != null && t.Kind != TransitionKind.Internal) t.LabelOffset = new PointD(NumOf(label, "x").Value, NumOf(label, "y").Value);
                var color = e.Field("color")?.Value.Str;
                if (ElementColors.IsColor(color)) t.Color = color;
                var items = e.Field("itemColors")?.Value;
                if (items?.Fields != null)
                {
                    var ic = ItemColorsOf(items);
                    if (ic.Count > 0) t.ItemColors = ic;
                }
            }
            model.NormalizeGroups();
        }

        /// <summary>Reads a machine's two files (the layout file's text null when there is none).</summary>
        public static ReadMachine ReadMachine(string modelText, string layoutText)
        {
            var read = ReadModel(modelText);
            if (!read.Failed) ApplyLayout(read, layoutText);
            return read;
        }

        /// <summary>
        /// Resolves the references of <paramref name="model"/> that named nothing when they were read (a transition's
        /// target, a comment's anchors) and name something now, as reading its file again would
        /// (<paramref name="written"/>: <c>WriteMachine(model)</c>, which holds every anchor). Changes the model in place;
        /// returns whether it changed.
        /// </summary>
        public static bool ResolveReferences(FsmModel model, Written written)
        {
            bool Open(string a) => a.StartsWith(Unresolved, StringComparison.Ordinal) || a.StartsWith(UnresolvedAnchor, StringComparison.Ordinal);
            var pending = model.Transitions.Any(t => !string.IsNullOrEmpty(UnresolvedOf(t.Target))) || model.Vertices.Any(v => v.Anchors.Any(Open));
            if (!pending) return false;
            var read = ReadModel(written.Model);
            if (read.Failed) return false;
            var ids = DerivedIds(model, written);
            var own = new Dictionary<string, string>();
            foreach (var kv in ids)
            {
                own[kv.Value] = kv.Key;
            }
            var rt = new Dictionary<string, Transition>();
            foreach (var t in read.Model.Transitions) rt[t.Id] = t;
            var rv = new Dictionary<string, Vertex>();
            foreach (var v in read.Model.Vertices) rv[v.Id] = v;
            var changed = false;
            foreach (var t in model.Transitions)
            {
                if (string.IsNullOrEmpty(UnresolvedOf(t.Target))) continue;
                if (!ids.TryGetValue(t.Id, out var did) || !rt.TryGetValue(did, out var rtt)) continue;
                if (own.TryGetValue(rtt.Target, out var id))
                {
                    t.Target = id;
                    changed = true;
                }
            }
            foreach (var v in model.Vertices)
            {
                if (!v.Anchors.Any(Open)) continue;
                var anchors = ids.TryGetValue(v.Id, out var did) && rv.TryGetValue(did, out var rvv) ? rvv.Anchors : new List<string>();
                v.Anchors = v.Anchors.Select((a, i) =>
                {
                    if (!Open(a) || i >= anchors.Count || !own.TryGetValue(anchors[i], out var id)) return a;
                    changed = true;
                    return id;
                }).ToList();
            }
            return changed;
        }

        /// <summary>
        /// The ids reading <paramref name="model"/> back from its file would give (what fsm-cli calls the elements, in
        /// generated code's traces and in its problems), by the model's own ids: vertices and transitions.
        /// </summary>
        public static Dictionary<string, string> DerivedIds(FsmModel model, Written written = null)
        {
            written = written ?? WriteMachine(model);
            var read = ReadModel(written.Model);
            var output = new Dictionary<string, string>();
            if (read.Failed) return output;
            var byLine = new Dictionary<int, string>();
            var byLineT = new Dictionary<int, string>();
            foreach (var v in read.Model.Vertices) byLine[read.Lines[v.Id]] = v.Id;
            foreach (var t in read.Model.Transitions) byLineT[read.Lines[t.Id]] = t.Id;
            foreach (var v in model.Vertices)
            {
                if (written.Lines.TryGetValue(v.Id, out var line) && byLine.TryGetValue(line, out var id)) output[v.Id] = id;
            }
            foreach (var t in model.Transitions)
            {
                if (written.Lines.TryGetValue(t.Id, out var line) && byLineT.TryGetValue(line, out var id)) output[t.Id] = id;
            }
            return output;
        }
    }
}
