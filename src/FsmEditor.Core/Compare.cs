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
        /// <summary>The properties as lines (<c>guard: a() → b()</c>): all of them, and the model's only.</summary>
        public List<string> Described { get; set; } = new List<string>();
        public List<string> ModelDescribed { get; set; } = new List<string>();
        /// <summary>Whether it shows when layout changes are hidden.</summary>
        public bool ModelChange { get; set; }
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
    /// Comparing two versions of a machine, for the diagram diff (two panes) and merge (three panes): fsm-editor-common's
    /// (<see cref="Common"/>). Elements are matched by what identifies them in the file (an element by its name, a
    /// transition by its source, trigger and target...); each match lists the properties that differ, model and layout
    /// apart. A renamed element is one removed and one added.
    /// </summary>
    public static class Compare
    {
        /// <summary>The elements of a model by the key that matches them across versions (their ids only).</summary>
        public static CompareItems ItemsOf(FsmModel model)
        {
            var items = new CompareItems();
            var r = Common.Call("keys", Common.Args(("model", ModelJson.Write(model)))) as Dictionary<string, object>;
            foreach (var kv in r ?? new Dictionary<string, object>()) items.Set(new CompareItem { Key = kv.Key, Id = kv.Value as string ?? "" });
            return items;
        }

        /// <summary>What changed from <paramref name="older"/> to <paramref name="newer"/>, in the order of the newer version (removed ones last).</summary>
        public static List<Change> CompareModels(FsmModel older, FsmModel newer)
        {
            var r = (List<object>)Common.Call("compare", Common.Args(("older", ModelJson.Write(older)), ("newer", ModelJson.Write(newer))));
            return r.OfType<Dictionary<string, object>>().Select(c => new Change
            {
                Key = ModelJson.Str(c, "key"),
                OldKey = c.TryGetValue("oldKey", out var ok) ? ok as string : null,
                Kind = ModelJson.Str(c, "kind") == "added" ? ChangeKind.Added : ModelJson.Str(c, "kind") == "removed" ? ChangeKind.Removed : ChangeKind.Changed,
                What = ModelJson.Str(c, "what"),
                Label = ModelJson.Str(c, "label"),
                OldId = c.TryGetValue("oldId", out var oi) ? oi as string : null,
                NewId = c.TryGetValue("newId", out var ni) ? ni as string : null,
                Properties = ModelJson.Objects(c, "properties").Select(p => new PropertyChange
                {
                    Name = ModelJson.Str(p, "name"),
                    Old = ModelJson.Str(p, "old"),
                    New = ModelJson.Str(p, "new"),
                    Layout = p.TryGetValue("layout", out var l) && l is bool b && b,
                }).ToList(),
                Described = ModelJson.Strings(c, "described"),
                ModelDescribed = ModelJson.Strings(c, "modelDescribed"),
                ModelChange = c.TryGetValue("modelChange", out var mc) && mc is bool m && m,
            }).ToList();
        }

        /// <summary>Whether a change shows when layout changes are hidden.</summary>
        public static bool IsModelChange(Change c) => c.ModelChange;

        /// <summary>A change's properties as lines: <c>guard: a() → b()</c>.</summary>
        public static List<string> DescribeChange(Change c, bool layout) => layout ? c.Described : c.ModelDescribed;
    }
}
