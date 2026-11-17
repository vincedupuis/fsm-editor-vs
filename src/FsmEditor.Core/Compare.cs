using System;
using System.Collections.Generic;
using System.Linq;

namespace FsmEditor.Core
{
    public enum ChangeKind { Added, Removed, Changed }

    /// <summary>A property that differs, with its two values as text ("" when absent).</summary>
    public sealed class PropertyChange
    {
        public string Name { get; set; } = "";
        public string Old { get; set; } = "";
        public string New { get; set; } = "";
        /// <summary>A property of the drawing, not of the model.</summary>
        public bool Layout { get; set; }
    }

    public sealed class Change
    {
        /// <summary>What identifies the element in the newer version (see <see cref="Compare.ItemsOf"/>).</summary>
        public string Key { get; set; } = "";
        /// <summary>Its key in the older version, when it differs (a transition whose target or trigger changed).</summary>
        public string OldKey { get; set; }
        public ChangeKind Kind { get; set; }
        /// <summary><c>machine</c>, <c>state</c>, <c>choice</c>…, <c>region</c>, <c>transition</c>.</summary>
        public string What { get; set; } = "";
        /// <summary>How a person names it: <c>state 'Off'</c>, <c>transition Off -&gt; On (powerOn)</c>.</summary>
        public string Label { get; set; } = "";
        /// <summary>The element's id in each version.</summary>
        public string OldId { get; set; }
        public string NewId { get; set; }
        /// <summary>For <see cref="ChangeKind.Changed"/>: what differs.</summary>
        public List<PropertyChange> Properties { get; set; } = new List<PropertyChange>();
    }

    /// <summary>Properties as text, in the order they were set.</summary>
    public sealed class Props
    {
        private readonly List<string> _keys = new List<string>();
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>();

        public string this[string key]
        {
            get => _values.TryGetValue(key, out var v) ? v : null;
            set
            {
                if (!_values.ContainsKey(key)) _keys.Add(key);
                _values[key] = value;
            }
        }

        public IReadOnlyList<string> Keys => _keys;
    }

    /// <summary>What can be compared in a model: an element by key, with its properties.</summary>
    public sealed class CompareItem
    {
        public string Key { get; set; } = "";
        public string Id { get; set; } = "";
        public string What { get; set; } = "";
        public string Label { get; set; } = "";
        public Props Model { get; set; } = new Props();
        public Props Layout { get; set; } = new Props();
    }

    /// <summary>The items of a model in order, by key.</summary>
    public sealed class CompareItems
    {
        public List<CompareItem> List { get; } = new List<CompareItem>();
        public Dictionary<string, CompareItem> ByKey { get; } = new Dictionary<string, CompareItem>();

        public CompareItem Get(string key) => key != null && ByKey.TryGetValue(key, out var i) ? i : null;

        public bool Has(string key) => key != null && ByKey.ContainsKey(key);

        internal void Set(CompareItem item)
        {
            if (!ByKey.ContainsKey(item.Key)) List.Add(item);
            else List[List.FindIndex(i => i.Key == item.Key)] = item;
            ByKey[item.Key] = item;
        }
    }

    /// <summary>
    /// Comparing two versions of a machine, for the diagram diff (two panes) and merge (three panes). A port of the
    /// VS Code edition's <c>src/compare.ts</c>.
    ///
    /// Elements are matched by what identifies them in the file: an element by its name, a region of <c>regions</c> by
    /// its state and name, a point by its state and name, an initial or history pseudostate by its region, a transition
    /// by its source, trigger and target (then by order among identical ones; a transition left over is then paired with
    /// one of the same source and trigger, else of the same source, in order, its target or trigger having changed). A
    /// renamed element is one removed and one added.
    ///
    /// Each match lists the properties that differ, model and layout apart: positions are compared relative to the owner
    /// (moving a state doesn't change what it contains), bend points relative to the innermost state containing both ends.
    /// </summary>
    public static class Compare
    {
        private static string Num1(double? n) => n is double d && !double.IsNaN(d) && !double.IsInfinity(d) ? Num.Format(Num.R1(d)) : "";

        private static string WhatOf(VertexType t)
        {
            switch (t)
            {
                case VertexType.ShallowHistory: return "history";
                case VertexType.DeepHistory: return "deep history";
                case VertexType.EntryPoint: return "entry point";
                case VertexType.ExitPoint: return "exit point";
                case VertexType.ConnectionPointRef: return "point";
                case VertexType.Final: return "final state";
                case VertexType.Terminate: return "terminate";
                default: return t.Key();
            }
        }

        private static string Shown(string name) => Format.Shown(name);

        private static string KindKey(TransitionKind k) => k == TransitionKind.Internal ? "internal" : k == TransitionKind.Local ? "local" : "external";

        /// <summary>The items of a model, keyed so that the same element has the same key in another version.</summary>
        public static CompareItems ItemsOf(FsmModel model)
        {
            var V = new Dictionary<string, Vertex>();
            foreach (var v in model.Vertices)
            {
                if (!V.ContainsKey(v.Id)) V[v.Id] = v;
            }
            var regionOwner = new Dictionary<string, Vertex>();
            var regions = new Dictionary<string, Region>();
            foreach (var r in model.Regions)
            {
                regionOwner[r.Id] = null;
                regions[r.Id] = r;
            }
            foreach (var v in model.Vertices)
            {
                foreach (var r in v.Regions)
                {
                    regionOwner[r.Id] = v;
                    regions[r.Id] = r;
                }
            }
            var top = new HashSet<string>(model.Regions.Select(r => r.Id));
            bool IsBorder(Vertex v) => v.Type == VertexType.ConnectionPointRef || ((v.Type == VertexType.EntryPoint || v.Type == VertexType.ExitPoint) && V.ContainsKey(v.Parent));
            bool IsMachinePoint(Vertex v) => (v.Type == VertexType.EntryPoint || v.Type == VertexType.ExitPoint) && top.Contains(v.Parent);
            Vertex Get(string id) => id != null && V.TryGetValue(id, out var x) ? x : null;
            Vertex Owner(Vertex v) => IsBorder(v) ? Get(v.Parent) : v.Parent != null && regionOwner.TryGetValue(v.Parent, out var o) ? o : null;
            // A region's key: its owner and name, or index for a state's only region.
            string RegionKey(string rid)
            {
                regionOwner.TryGetValue(rid ?? "", out var o);
                var list = o != null ? o.Regions : model.Regions;
                regions.TryGetValue(rid ?? "", out var r);
                var at = list.Count > 1 ? $":{r?.Name ?? ""}" : "";
                return o != null ? $"{o.Name}{at}" : at;
            }
            string VertexKey(Vertex v)
            {
                if (v.Type == VertexType.Initial || v.Type == VertexType.ShallowHistory || v.Type == VertexType.DeepHistory) return $"{v.Type.Key()}@{RegionKey(v.Parent)}";
                if (IsBorder(v)) return $"point@{Get(v.Parent)?.Name ?? ""}.{(v.Type == VertexType.ConnectionPointRef ? v.Ref : v.Name)}";
                if (IsMachinePoint(v)) return $"point@.{v.Name}";
                return $"element@{v.Name}";
            }
            var keys = new Dictionary<string, string>();
            foreach (var v in model.Vertices) keys[v.Id] = VertexKey(v);
            string RefOf(string id)
            {
                if (!V.ContainsKey(id)) return id.StartsWith(Format.Unresolved, StringComparison.Ordinal) ? id.Substring(1) : id;
                var k = keys[id];
                if (k.StartsWith("element@", StringComparison.Ordinal)) return k.Substring(8);
                if (k.StartsWith("point@.", StringComparison.Ordinal)) return $"{model.Name}." + k.Substring(7);
                if (k.StartsWith("point@", StringComparison.Ordinal)) return k.Substring(6);
                return k;
            }
            string NameOf(Vertex v) => Shown(v.Type == VertexType.ConnectionPointRef ? v.Ref : v.Name);
            // Where a vertex is: its state, and the region of a state (or machine) with several.
            string PlaceOf(Vertex v)
            {
                var o = Owner(v);
                if (IsBorder(v)) return o != null ? $"'{Shown(o.Name)}'" : "";
                var list = o != null ? o.Regions : model.Regions;
                var i = list.FindIndex(r => r.Id == v.Parent);
                var regionName = i >= 0 ? Shown(list[i].Name) : "";
                var region = list.Count > 1 ? $"region '{(regionName.Length > 0 ? regionName : (i + 1).ToString())}'" : "";
                return o != null ? $"'{Shown(o.Name)}'{(region.Length > 0 ? $", {region}" : "")}" : region;
            }
            var T = new Dictionary<string, Transition>();
            foreach (var t in model.Transitions) T[t.Id] = t;
            // A comment's anchor: a reference, or a transition as `source -> target (trigger)`.
            string AnchorOf(string a)
            {
                if (V.ContainsKey(a)) return RefOf(a);
                if (!T.TryGetValue(a, out var t)) return a.StartsWith(Format.Unresolved, StringComparison.Ordinal) || a.StartsWith(Format.UnresolvedAnchor, StringComparison.Ordinal) ? a.Substring(1) : a;
                var trigger = string.Join(", ", t.Triggers);
                return $"{RefOf(t.Source)} -> {(t.Kind == TransitionKind.Internal ? "(internal)" : RefOf(t.Target))}{(trigger.Length > 0 ? $" ({trigger})" : "")}";
            }
            string LabelOf(Vertex v)
            {
                var what = WhatOf(v.Type);
                var n = NameOf(v);
                var o = Owner(v);
                return n.Length > 0 ? $"{what} '{n}'" : o != null && Shown(o.Name).Length > 0 ? $"{what} in '{Shown(o.Name)}'" : what;
            }
            Vertex Frame(Transition t)
            {
                List<Vertex> Chain(string id)
                {
                    var output = new List<Vertex>();
                    var v = Get(id);
                    if (v != null && v.Type != VertexType.State) v = Owner(v);
                    var seen = new HashSet<Vertex>();
                    while (v != null && seen.Add(v))
                    {
                        output.Add(v);
                        v = Owner(v);
                    }
                    return output;
                }
                var g = new HashSet<Vertex>(Chain(t.Target));
                return Chain(t.Source).FirstOrDefault(g.Contains);
            }
            void Colors(string color, Dictionary<string, string> items, Props into)
            {
                into["color"] = color ?? "";
                foreach (var k in ElementColors.ItemKeys)
                {
                    if (items.TryGetValue(k, out var c) && !string.IsNullOrEmpty(c)) into[$"color of {k}"] = c;
                }
            }

            var result = new CompareItems();
            var machine = new CompareItem { Key = "machine", Id = "", What = "machine", Label = "the state machine" };
            machine.Model["name"] = model.Name;
            machine.Model["type"] = model.Kind == MachineKind.Protocol ? "protocol" : "behavioral";
            machine.Model["documentation"] = model.Documentation ?? "";
            result.Set(machine);
            foreach (var v in model.Vertices)
            {
                var key = keys[v.Id];
                if (result.Has(key)) continue; // a name used twice: the first
                var o = Owner(v);
                var m = new Props { ["type"] = v.Type.Key(), ["in"] = PlaceOf(v) };
                if (v.Type == VertexType.State)
                {
                    m["entry"] = v.Entry;
                    m["exit"] = v.Exit;
                    m["do"] = v.DoActivity;
                    m["defer"] = string.Join(", ", v.Deferrable);
                    m["invariant"] = v.Invariant;
                    m["stereotype"] = v.Stereotype;
                    m["submachine"] = v.Submachine;
                    m["regions"] = v.Regions.Count > 1
                        ? string.Join(", ", v.Regions.Select(r => Shown(r.Name).Length > 0 ? Shown(r.Name) : "(unnamed)"))
                        : v.Regions.Count.ToString();
                }
                if (v.Type == VertexType.Comment)
                {
                    m["text"] = v.Text;
                    m["anchors"] = string.Join(", ", v.Anchors.Select(AnchorOf));
                }
                if (v.Type == VertexType.ConnectionPointRef) m["direction"] = v.PointKind == PointKind.Exit ? "exit" : "entry";
                // Where it is, relative to its owner.
                var ox = o != null ? o.X : 0;
                var oy = o != null ? o.Y : 0;
                var l = new Props { ["x"] = Num1(v.X - ox), ["y"] = Num1(v.Y - oy), ["width"] = Num1(v.W), ["height"] = Num1(v.H), ["group"] = v.Group.Length > 0 ? "grouped" : "" };
                if (v.Regions.Count > 1)
                {
                    l["region layout"] = v.RegionLayout == RegionLayout.Horizontal ? "horizontal" : "vertical";
                    for (var i = 0; i < v.Regions.Count; i++)
                    {
                        var r = v.Regions[i];
                        l[$"size of region {(Shown(r.Name).Length > 0 ? Shown(r.Name) : (i + 1).ToString())}"] = Num1(r.Size);
                    }
                }
                Colors(v.Color, v.ItemColors, l);
                result.Set(new CompareItem { Key = key, Id = v.Id, What = WhatOf(v.Type), Label = LabelOf(v), Model = m, Layout = l });
            }
            // Transitions: by source, trigger and target, then order.
            var seenKeys = new Dictionary<string, int>();
            foreach (var t in model.Transitions)
            {
                var s = Get(t.Source);
                var source = s != null ? keys[s.Id] : $"?{t.Source}";
                var trigger = string.Join(", ", t.Triggers);
                var target = t.Kind == TransitionKind.Internal ? "" : RefOf(t.Target);
                var baseKey = $"transition@{source}|{trigger}|{target}";
                var n = seenKeys.TryGetValue(baseKey, out var c) ? c : 0;
                seenKeys[baseKey] = n + 1;
                var key = $"{baseKey}|{n}";
                var sv = s != null ? (Shown(s.Name).Length > 0 ? Shown(s.Name) : LabelOf(s)) : t.Source;
                var label = $"transition {sv} -> {(t.Kind == TransitionKind.Internal ? "(internal)" : target.EndsWith("*", StringComparison.Ordinal) ? target.Substring(0, target.Length - 1) : target)}{(trigger.Length > 0 ? $" ({trigger})" : "")}";
                var f = Frame(t);
                var fx = f != null ? f.X : 0;
                var fy = f != null ? f.Y : 0;
                var l = new Props
                {
                    ["bend points"] = string.Join(" ", t.Points.Select(p => $"{Num1(p.X - fx)},{Num1(p.Y - fy)}")),
                    ["label"] = t.LabelOffset is PointD lo ? $"{Num1(lo.X)},{Num1(lo.Y)}" : "",
                };
                Colors(t.Color, t.ItemColors, l);
                var m = new Props
                {
                    ["kind"] = KindKey(t.Kind), ["trigger"] = trigger, ["guard"] = t.Guard, ["action"] = t.Action, ["precondition"] = t.Precondition ?? "",
                    ["postcondition"] = t.Postcondition ?? "", ["target"] = target, ["source"] = source,
                };
                result.Set(new CompareItem { Key = key, Id = t.Id, What = "transition", Label = label, Model = m, Layout = l });
            }
            return result;
        }

        /// <summary>The properties of two items that differ.</summary>
        private static List<PropertyChange> Differences(CompareItem a, CompareItem b)
        {
            var output = new List<PropertyChange>();
            foreach (var (layout, x, y) in new[] { (false, a.Model, b.Model), (true, a.Layout, b.Layout) })
            {
                foreach (var k in x.Keys.Concat(y.Keys).Distinct())
                {
                    if (k == "source") continue;
                    var xv = x[k] ?? "";
                    var yv = y[k] ?? "";
                    if (xv != yv) output.Add(new PropertyChange { Name = k, Old = xv, New = yv, Layout = layout });
                }
            }
            return output;
        }

        private static string[] Parts(string key) => key.Split('|');

        /// <summary>
        /// What changed from <paramref name="older"/> to <paramref name="newer"/>, in the order of the newer version
        /// (removed ones come last).
        /// </summary>
        public static List<Change> CompareModels(FsmModel older, FsmModel newer)
        {
            var a = ItemsOf(older);
            var b = ItemsOf(newer);
            var pairs = new Dictionary<string, string>(); // new key -> old key
            foreach (var i in b.List)
            {
                if (a.Has(i.Key)) pairs[i.Key] = i.Key;
            }
            // Transitions left over: the same source and trigger, else the same source, in order.
            var leftA = a.List.Where(i => i.What == "transition" && !pairs.ContainsKey(i.Key)).ToList();
            var leftB = b.List.Where(i => i.What == "transition" && !pairs.ContainsKey(i.Key)).ToList();
            var used = new HashSet<string>();
            foreach (var level in new[] { 2, 1 })
            {
                foreach (var t in leftB)
                {
                    if (pairs.ContainsKey(t.Key)) continue;
                    var p = Parts(t.Key);
                    var o = leftA.FirstOrDefault(x => !used.Contains(x.Key) && Parts(x.Key)[0] == p[0] && (level == 1 || Parts(x.Key)[1] == p[1]));
                    if (o != null)
                    {
                        pairs[t.Key] = o.Key;
                        used.Add(o.Key);
                    }
                }
            }
            var matchedOld = new HashSet<string>(pairs.Values);
            var changes = new List<Change>();
            foreach (var item in b.List)
            {
                if (!pairs.TryGetValue(item.Key, out var old))
                {
                    changes.Add(new Change { Key = item.Key, Kind = ChangeKind.Added, What = item.What, Label = item.Label, NewId = item.Id });
                    continue;
                }
                var props = Differences(a.Get(old), item);
                if (props.Count > 0)
                {
                    changes.Add(new Change
                    {
                        Key = item.Key, OldKey = old != item.Key ? old : null, Kind = ChangeKind.Changed, What = item.What, Label = item.Label,
                        OldId = a.Get(old).Id, NewId = item.Id, Properties = props,
                    });
                }
            }
            foreach (var item in a.List)
            {
                if (!matchedOld.Contains(item.Key)) changes.Add(new Change { Key = item.Key, Kind = ChangeKind.Removed, What = item.What, Label = item.Label, OldId = item.Id });
            }
            return changes;
        }

        /// <summary>Whether a change shows when layout changes are hidden.</summary>
        public static bool IsModelChange(Change c) => c.Kind != ChangeKind.Changed || c.Properties.Any(p => !p.Layout);

        /// <summary>A change's properties as lines: <c>guard: a() → b()</c>.</summary>
        public static List<string> DescribeChange(Change c, bool layout)
        {
            string Value(string s) => s.Length == 0 ? "(none)" : s.Length > 60 ? s.Substring(0, 57) + "…" : s;
            return c.Properties.Where(p => layout || !p.Layout).Select(p => $"{p.Name}: {Value(p.Old)} → {Value(p.New)}").ToList();
        }
    }
}
