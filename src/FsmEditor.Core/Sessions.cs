using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace FsmEditor.Core
{
    /// <summary>What a pane of a diagram diff or merge shows: its role, the marks of its elements and the list to step through.</summary>
    public sealed class PaneDiff
    {
        /// <summary><c>old</c>, <c>new</c>; <c>incoming</c>, <c>current</c>, <c>result</c>.</summary>
        public string Role { get; set; } = "";
        public string Title { get; set; } = "";
        /// <summary>By element id: <c>added</c>, <c>removed</c>, <c>changed</c>; <c>conflict</c>, <c>merged</c>, <c>resolved</c>.</summary>
        public Dictionary<string, string> Marks { get; set; } = new Dictionary<string, string>();
        /// <summary>What changed in each marked element, by id.</summary>
        public Dictionary<string, List<string>> Details { get; set; } = new Dictionary<string, List<string>>();
        /// <summary>How many changes (or conflicts).</summary>
        public int Count { get; set; }
        public bool ShowLayout { get; set; }
        /// <summary>The conflicts, in the result pane only.</summary>
        public List<ConflictRow> Conflicts { get; set; }
    }

    /// <summary>A conflict of a merge, as the result pane lists it.</summary>
    public sealed class ConflictRow
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        /// <summary><c>differs</c>, <c>only in incoming</c>, <c>only in current</c>.</summary>
        public string How { get; set; } = "";
        public List<string> Details { get; set; } = new List<string>();
        /// <summary><c>incoming</c>, <c>current</c>, or null when not chosen yet.</summary>
        public string Choice { get; set; }
    }

    /// <summary>An open diagram, as sessions see it.</summary>
    public interface IDiffPane
    {
        /// <summary>The model it shows (with its layout), or null when it can't be read.</summary>
        FsmModel CurrentModel();
        /// <summary>Shows the pane's role, marks and list; null: no diff any more.</summary>
        void ShowDiff(PaneDiff diff);
        /// <summary>Selects and shows an element (null: selects nothing).</summary>
        void Reveal(string id);
        /// <summary>Says something to the user, in the pane.</summary>
        void Toast(string message);
    }

    /// <summary>
    /// A diagram diff: two panes, the older version and the newer one. The logic of the VS Code edition's
    /// <c>DiffSession</c> (<c>src/sessions.ts</c>), without editor APIs: the editions link their panes through it.
    /// </summary>
    public sealed class DiffSession
    {
        private bool _showLayout;
        private int _index = -1;
        private List<Change> _changes = new List<Change>();
        private readonly string _olderTitle;
        private readonly string _newerTitle;

        public DiffSession(IDiffPane older, IDiffPane newer, string olderTitle, string newerTitle)
        {
            Older = older;
            Newer = newer;
            _olderTitle = olderTitle;
            _newerTitle = newerTitle;
        }

        public IDiffPane Older { get; }
        public IDiffPane Newer { get; }
        public bool ShowLayout => _showLayout;
        public IReadOnlyList<Change> Changes => _changes;

        public IDiffPane Other(IDiffPane pane) => pane == Older ? Newer : Older;

        /// <summary>Compares the two panes again and shows the result in both.</summary>
        public void Update()
        {
            var a = Older.CurrentModel();
            var b = Newer.CurrentModel();
            var all = a != null && b != null ? Compare.CompareModels(a, b) : new List<Change>();
            _changes = _showLayout ? all : all.Where(Compare.IsModelChange).ToList();
            if (_index >= _changes.Count) _index = -1;
            foreach (var (pane, role) in new[] { (Older, "old"), (Newer, "new") })
            {
                var diff = new PaneDiff { Role = role, Title = role == "old" ? _olderTitle : _newerTitle, Count = _changes.Count, ShowLayout = _showLayout };
                foreach (var c in _changes)
                {
                    var id = role == "old" ? c.OldId : c.NewId;
                    if (id == null) continue;
                    diff.Marks[id] = c.Kind.ToString().ToLowerInvariant();
                    diff.Details[id] = c.Kind == ChangeKind.Changed ? Compare.DescribeChange(c, _showLayout) : new List<string> { c.Kind == ChangeKind.Added ? "Added" : "Removed" };
                }
                pane.ShowDiff(diff);
            }
        }

        /// <summary>Shows layout changes too, or not (off by default).</summary>
        public void SetShowLayout(bool show)
        {
            _showLayout = show;
            Update();
        }

        /// <summary>The next (<paramref name="dir"/> &gt; 0) or previous change, selected in both panes.</summary>
        public void Navigate(int dir)
        {
            if (_changes.Count == 0) return;
            var n = _changes.Count;
            _index = (_index + (dir < 0 ? -1 : 1) + n) % n;
            var c = _changes[_index];
            Older.Reveal(c.OldId);
            Newer.Reveal(c.NewId);
        }

        /// <summary>A pane closed: the other one is no longer part of a diff.</summary>
        public void Remove(IDiffPane pane) => Other(pane).ShowDiff(null);
    }

    /// <summary>Two texts of a machine: its model file and its layout file (null when there is none).</summary>
    public sealed class MachineTexts
    {
        public MachineTexts(string model, string layout)
        {
            Model = model;
            Layout = layout;
        }

        public string Model { get; }
        public string Layout { get; }
    }

    /// <summary>What the merge needs from the edition.</summary>
    public interface IMergeHost
    {
        /// <summary>Applies a model to the result as an undoable edit; a message when the format can't hold it.</summary>
        string Edit(FsmModel model, string label);
        /// <summary>The result's state now: a new object at each edit, undo or redo (an earlier one again when it comes back).</summary>
        object State();
    }

    /// <summary>
    /// A diagram merge: the conflicted file is the result, beside its two sides (incoming, current). The logic of the VS
    /// Code edition's <c>MergeSession</c> (<c>src/sessions.ts</c>), without editor APIs.
    /// </summary>
    public sealed class MergeSession
    {
        private readonly Dictionary<string, IDiffPane> _sides = new Dictionary<string, IDiffPane>();
        private readonly List<IDiffPane> _results = new List<IDiffPane>();
        private readonly List<Change> _conflicts;
        /// <summary>
        /// The choices made, by state of the result: undo and redo take them back with the result. A state reached
        /// otherwise (an edit of the result) keeps the choices of the one before.
        /// </summary>
        private readonly ConditionalWeakTable<object, Dictionary<string, string>> _history = new ConditionalWeakTable<object, Dictionary<string, string>>();
        private Dictionary<string, string> _last = new Dictionary<string, string>();
        private readonly FsmModel _incomingModel;
        private readonly FsmModel _currentModel;
        /// <summary>Changes of each side since the common version, when it is known: highlighted lightly.</summary>
        private readonly List<Change> _ours = new List<Change>();
        private readonly List<Change> _theirs = new List<Change>();
        private readonly IMergeHost _host;
        private readonly Func<FsmModel> _result;
        private int _index = -1;

        /// <param name="read">The model of a side's texts (with its layout), or null when it can't be read.</param>
        /// <param name="result">The model of the result, as it is now.</param>
        public MergeSession(string id, MachineTexts current, MachineTexts incoming, MachineTexts baseTexts, Func<MachineTexts, FsmModel> read, IMergeHost host, Func<FsmModel> result)
        {
            Id = id;
            Current = current;
            Incoming = incoming;
            _host = host;
            _result = result;
            _currentModel = read(current);
            _incomingModel = read(incoming);
            _conflicts = _currentModel != null && _incomingModel != null ? Compare.CompareModels(_currentModel, _incomingModel) : new List<Change>();
            var baseModel = baseTexts != null ? read(baseTexts) : null;
            if (baseModel != null && _currentModel != null && _incomingModel != null)
            {
                _ours = Compare.CompareModels(baseModel, _currentModel).Where(Compare.IsModelChange).ToList();
                _theirs = Compare.CompareModels(baseModel, _incomingModel).Where(Compare.IsModelChange).ToList();
            }
        }

        public string Id { get; }
        public MachineTexts Current { get; }
        public MachineTexts Incoming { get; }
        public int ConflictCount => _conflicts.Count;
        public IReadOnlyList<Change> Conflicts => _conflicts;
        public IReadOnlyDictionary<string, IDiffPane> Sides => _sides;
        public IReadOnlyList<IDiffPane> Results => _results;

        /// <summary>The choices of the result as it is now (by conflict key: <c>incoming</c> or <c>current</c>).</summary>
        public Dictionary<string, string> Choices
        {
            get
            {
                var state = _host.State();
                if (!_history.TryGetValue(state, out var choices))
                {
                    choices = new Dictionary<string, string>(_last);
                    _history.Add(state, choices);
                }
                _last = choices;
                return choices;
            }
        }

        /// <param name="role"><c>incoming</c>, <c>current</c> or <c>result</c>.</param>
        public void Attach(IDiffPane pane, string role)
        {
            if (role == "result")
            {
                if (!_results.Contains(pane)) _results.Add(pane);
            }
            else _sides[role] = pane;
            Update();
        }

        private static List<string> DetailsOf(Change c) =>
            c.Kind == ChangeKind.Changed ? Compare.DescribeChange(c, true) : new List<string> { c.Kind == ChangeKind.Added ? "Only in incoming" : "Only in current" };

        /// <summary>The item of a conflict in the result, by its key in either version.</summary>
        private static CompareItem ItemIn(CompareItems items, Change c) => items.Get(c.OldKey ?? c.Key) ?? items.Get(c.Key);

        public void Update()
        {
            var conflictKeys = new HashSet<string>(_conflicts.SelectMany(c => new[] { c.Key, c.OldKey ?? c.Key }));
            var merged = _ours.Concat(_theirs).Where(c => !conflictKeys.Contains(c.Key) && !conflictKeys.Contains(c.OldKey ?? c.Key)).ToList();
            var choices = Choices;
            var list = _conflicts.Select(c => new ConflictRow
            {
                Key = c.Key,
                Label = c.Label,
                How = c.Kind == ChangeKind.Changed ? "differs" : c.Kind == ChangeKind.Added ? "only in incoming" : "only in current",
                Details = DetailsOf(c),
                Choice = choices.TryGetValue(c.Key, out var ch) ? ch : null,
            }).ToList();
            foreach (var kv in _sides)
            {
                var role = kv.Key;
                var diff = new PaneDiff { Role = role, Title = role == "incoming" ? "theirs" : "yours", Count = _conflicts.Count };
                foreach (var c in role == "incoming" ? _theirs : _ours)
                {
                    if (c.NewId != null && !conflictKeys.Contains(c.Key)) diff.Marks[c.NewId] = "merged";
                }
                foreach (var c in _conflicts)
                {
                    var id = role == "incoming" ? c.NewId : c.OldId;
                    if (id == null) continue;
                    diff.Marks[id] = "conflict";
                    diff.Details[id] = DetailsOf(c);
                }
                kv.Value.ShowDiff(diff);
            }
            var r = _result();
            var items = r != null ? Compare.ItemsOf(r) : new CompareItems();
            var resultDiff = new PaneDiff { Role = "result", Title = $"{choices.Count} of {_conflicts.Count} chosen", Count = _conflicts.Count, Conflicts = list };
            foreach (var c in merged)
            {
                var it = items.Get(c.Key);
                if (!string.IsNullOrEmpty(it?.Id)) resultDiff.Marks[it.Id] = "merged";
            }
            foreach (var c in _conflicts)
            {
                var it = ItemIn(items, c);
                if (string.IsNullOrEmpty(it?.Id)) continue;
                resultDiff.Marks[it.Id] = choices.ContainsKey(c.Key) ? "resolved" : "conflict";
                resultDiff.Details[it.Id] = DetailsOf(c);
            }
            foreach (var pane in _results) pane.ShowDiff(resultDiff);
        }

        /// <summary>The result with conflict <paramref name="c"/> taken from <paramref name="side"/>.</summary>
        private FsmModel Take(FsmModel model, Change c, string side)
        {
            var source = side == "incoming" ? _incomingModel : _currentModel;
            var keys = side == "incoming" ? new[] { c.Key, c.OldKey ?? c.Key } : new[] { c.OldKey ?? c.Key, c.Key };
            return Merge.Adopt(model, source, keys);
        }

        /// <summary>The next or previous conflict (<paramref name="dir"/>), or the one of <paramref name="key"/>, shown in every pane.</summary>
        public void Navigate(int dir, string key = null)
        {
            if (_conflicts.Count == 0) return;
            var n = _conflicts.Count;
            _index = key != null ? Math.Max(0, _conflicts.FindIndex(c => c.Key == key)) : (_index + (dir < 0 ? -1 : 1) + n) % n;
            var conflict = _conflicts[_index];
            if (_sides.TryGetValue("incoming", out var inc)) inc.Reveal(conflict.NewId);
            if (_sides.TryGetValue("current", out var cur)) cur.Reveal(conflict.OldId);
            var r = _result();
            var it = r != null ? ItemIn(Compare.ItemsOf(r), conflict) : null;
            foreach (var p in _results) p.Reveal(it?.Id);
        }

        /// <summary>Takes one side for the conflict of <paramref name="key"/>, or for all of them (<paramref name="key"/> null).</summary>
        /// <param name="side"><c>incoming</c> or <c>current</c>.</param>
        public void Choose(string key, string side)
        {
            var model = _result();
            if (model == null || _incomingModel == null || _currentModel == null) return;
            side = side == "incoming" ? "incoming" : "current";
            var list = key == null ? _conflicts : _conflicts.Where(c => c.Key == key).ToList();
            var choices = new Dictionary<string, string>(Choices);
            foreach (var c in list)
            {
                model = Take(model, c, side);
                choices[c.Key] = side;
            }
            var problem = _host.Edit(model, side == "incoming" ? "Take Incoming" : "Keep Current");
            // The choices go with the result's new state (the same one when the choice changed nothing in it).
            if (problem != null)
            {
                foreach (var p in _results) p.Toast(problem);
            }
            else
            {
                var state = _host.State();
                _history.Remove(state);
                _history.Add(state, choices);
                _last = choices;
            }
            Update();
        }

        public void Remove(IDiffPane pane)
        {
            _results.Remove(pane);
            foreach (var k in _sides.Where(kv => kv.Value == pane).Select(kv => kv.Key).ToList()) _sides.Remove(k);
        }
    }
}
