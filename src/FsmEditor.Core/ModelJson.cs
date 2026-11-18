using System.Collections.Generic;
using System.Linq;

namespace FsmEditor.Core
{
    /// <summary>
    /// The model as JSON, the form fsm-editor-common takes and gives (its <c>api.schema.json</c>, "model"): the VS Code
    /// edition's model, flat, with ids and absolute coordinates. Vertices and transitions are written as the clipboard
    /// writes them (<see cref="ClipboardJson"/>), whose JSON is the same.
    /// </summary>
    public static class ModelJson
    {
        public static string Write(FsmModel m)
        {
            var o = new ClipboardJson.JsonObjectWriter();
            o.Num("version", 1).Str("name", m.Name).Str("kind", m.Kind == MachineKind.Protocol ? "protocol" : "behavioral").Str("documentation", m.Documentation)
                .Raw("regions", "[" + string.Join(",", m.Regions.Select(ClipboardJson.WriteRegion)) + "]")
                .Raw("vertices", "[" + string.Join(",", m.Vertices.Select(ClipboardJson.WriteVertex)) + "]")
                .Raw("transitions", "[" + string.Join(",", m.Transitions.Select(ClipboardJson.WriteTransition)) + "]");
            return o.ToString();
        }

        /// <summary>A model from its parsed JSON (<see cref="ClipboardJson.ParseJson"/>).</summary>
        public static FsmModel Read(object json)
        {
            var o = json as Dictionary<string, object> ?? new Dictionary<string, object>();
            var m = new FsmModel
            {
                Name = Str(o, "name"),
                Kind = Str(o, "kind") == "protocol" ? MachineKind.Protocol : MachineKind.Behavioral,
                Documentation = Str(o, "documentation"),
                Regions = Objects(o, "regions").Select(ReadRegion).ToList(),
            };
            foreach (var v in Objects(o, "vertices")) m.Vertices.Add(ReadVertex(v));
            foreach (var t in Objects(o, "transitions")) m.Transitions.Add(ReadTransition(t));
            return m;
        }

        private static Region ReadRegion(Dictionary<string, object> r) =>
            new Region { Id = Str(r, "id"), Name = Str(r, "name"), Size = r.TryGetValue("size", out var s) && s is double d ? d : (double?)null };

        private static Vertex ReadVertex(Dictionary<string, object> item)
        {
            VertexTypes.TryParseKey(Str(item, "type"), out var type);
            var layout = Str(item, "regionLayout");
            return new Vertex
            {
                Id = Str(item, "id"),
                Type = type,
                Name = Str(item, "name"),
                Parent = Str(item, "parent"),
                X = Num(item, "x"),
                Y = Num(item, "y"),
                W = Num(item, "w"),
                H = Num(item, "h"),
                Regions = Objects(item, "regions").Select(ReadRegion).ToList(),
                RegionLayout = layout == "horizontal" ? RegionLayout.Horizontal : layout == "vertical" ? RegionLayout.Vertical : (RegionLayout?)null,
                Entry = Str(item, "entry"),
                Exit = Str(item, "exit"),
                DoActivity = Str(item, "doActivity"),
                Deferrable = Strings(item, "deferrable"),
                Invariant = Str(item, "invariant"),
                Submachine = Str(item, "submachine"),
                Stereotype = Str(item, "stereotype"),
                Ref = Str(item, "ref"),
                PointKind = Str(item, "pointKind") == "exit" ? PointKind.Exit : PointKind.Entry,
                Text = Str(item, "text"),
                Anchors = Strings(item, "anchors"),
                Color = ClipboardJson.ReadColor(item, "color"),
                ItemColors = ClipboardJson.ReadItemColors(item),
                Group = Str(item, "group"),
            };
        }

        private static Transition ReadTransition(Dictionary<string, object> item)
        {
            var kind = Str(item, "kind");
            var t = new Transition
            {
                Id = Str(item, "id"),
                Source = Str(item, "source"),
                Target = Str(item, "target"),
                Kind = kind == "internal" ? TransitionKind.Internal : kind == "local" ? TransitionKind.Local : TransitionKind.External,
                Triggers = Strings(item, "triggers"),
                Guard = Str(item, "guard"),
                Action = Str(item, "action"),
                Precondition = Str(item, "precondition"),
                Postcondition = Str(item, "postcondition"),
                Points = Objects(item, "points").Select(p => new PointD(Num(p, "x"), Num(p, "y"))).ToList(),
                Color = ClipboardJson.ReadColor(item, "color"),
                ItemColors = ClipboardJson.ReadItemColors(item),
            };
            if (item.TryGetValue("labelOffset", out var lo) && lo is Dictionary<string, object> off) t.LabelOffset = new PointD(Num(off, "x"), Num(off, "y"));
            return t;
        }

        public static string Str(Dictionary<string, object> o, string key) => o != null && o.TryGetValue(key, out var v) && v is string s ? s : "";

        /// <summary>A number; null (a number JSON can't hold) and a missing one are 0.</summary>
        public static double Num(Dictionary<string, object> o, string key) => o != null && o.TryGetValue(key, out var v) && v is double d ? d : 0;

        public static List<string> Strings(Dictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v is List<object> l ? l.OfType<string>().ToList() : new List<string>();

        public static IEnumerable<Dictionary<string, object>> Objects(Dictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v is List<object> l ? l.OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();
    }
}
