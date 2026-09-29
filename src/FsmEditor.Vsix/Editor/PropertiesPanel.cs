using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using FsmEditor.Core;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace FsmEditor.Editor
{
    /// <summary>
    /// The properties of the selection (or of the machine when nothing is
    /// selected). Text is checked against the grammar as it is committed:
    /// invalid text is refused and stays in the field, marked, until fixed.
    /// </summary>
    internal sealed class PropertiesPanel : StackPanel
    {
        private readonly EditorControl _editor;

        public PropertiesPanel(EditorControl editor)
        {
            _editor = editor;
            Margin = new Thickness(12, 8, 12, 24);
        }

        private DiagramSession S => _editor.Session;
        private Theme T => Theme.Current;

        public void FocusFirstField()
        {
            var first = FindFirst(this);
            first?.Focus();
        }

        private static Control FindFirst(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBox || child is ComboBox) return (Control)child;
                var found = FindFirst(child);
                if (found != null) return found;
            }
            return null;
        }

        private bool _rendering;

        public void Render()
        {
            // Clearing the panel commits a field that had the focus, which may ask for another render.
            if (_rendering) return;
            _rendering = true;
            try
            {
                Build();
            }
            finally
            {
                _rendering = false;
            }
        }

        private void Build()
        {
            Children.Clear();
            if (S == null) return;
            S.Reindex();
            TextElement.SetForeground(this, T.BrushOf(T.Foreground));
            var ids = S.Selection.ToList();
            if (ids.Count > 1)
            {
                Add(H3($"{ids.Count} elements selected"));
                Add(PButton("Delete", _editor.DeleteSelection));
                Add(Muted("Drag to move them together. Ctrl+D duplicates, Ctrl+C and Ctrl+V copy between diagrams."));
                return;
            }
            if (ids.Count == 1 && S.Index.Vertex(ids[0]) is Vertex v)
            {
                VertexProps(v);
                return;
            }
            if (ids.Count == 1 && S.Index.Transition(ids[0]) is Transition t)
            {
                TransitionProps(t);
                return;
            }
            MachineProps();
        }

        private void Add(params UIElement[] elements)
        {
            foreach (var e in elements) Children.Add(e);
        }

        private void Commit(bool props = false)
        {
            S.Commit();
            if (props) Render();
        }

        // ---------------------------------------------------------------- building blocks

        private TextBlock H3(string text) => new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 4, 0, 10), TextWrapping = TextWrapping.Wrap };

        private TextBlock H4(string text) => new TextBlock { Text = text.ToUpperInvariant(), FontSize = 11, Foreground = T.BrushOf(T.Muted), Margin = new Thickness(0, 16, 0, 6) };

        private TextBlock Muted(string text) => new TextBlock { Text = text, FontSize = 12, Foreground = T.BrushOf(T.Muted), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };

        private Button PButton(string text, Action onClick, string tooltip = null, bool small = false)
        {
            var b = new Button
            {
                Content = text,
                ToolTip = tooltip,
                Padding = small ? new Thickness(7, 1, 7, 1) : new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 2, 4, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth = 0,
                MinHeight = 0,
            };
            Theme.Style(b, VsResourceKeys.ButtonStyleKey);
            b.Click += (s, e) => onClick();
            return b;
        }

        private StackPanel Field(string label, UIElement control, UIElement error = null)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            if (label != null) panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = T.BrushOf(T.Muted), Margin = new Thickness(0, 0, 0, 2) });
            panel.Children.Add(control);
            if (error != null) panel.Children.Add(error);
            return panel;
        }

        private TextBox Input(string value, bool multiline = false, int rows = 3)
        {
            var tb = new TextBox
            {
                Text = value ?? "",
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                MinHeight = multiline ? rows * 16 + 8 : 0,
                VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
                Padding = new Thickness(3, 2, 3, 2),
            };
            if (multiline) tb.FontFamily = new FontFamily("Consolas");
            Theme.Style(tb, VsResourceKeys.TextBoxStyleKey);
            return tb;
        }

        /// <summary>Shows <paramref name="placeholder"/> over an empty text box.</summary>
        private UIElement WithPlaceholder(TextBox tb, string placeholder)
        {
            if (string.IsNullOrEmpty(placeholder)) return tb;
            var hint = new TextBlock
            {
                Text = placeholder,
                Foreground = T.BrushOf(T.Muted),
                IsHitTestVisible = false,
                Margin = new Thickness(6, 3, 6, 0),
                VerticalAlignment = tb.AcceptsReturn ? VerticalAlignment.Top : VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            void Update() => hint.Visibility = tb.Text.Length == 0 && !tb.IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;
            tb.TextChanged += (s, e) => Update();
            tb.GotKeyboardFocus += (s, e) => Update();
            tb.LostKeyboardFocus += (s, e) => Update();
            Update();
            var grid = new Grid();
            grid.Children.Add(tb);
            grid.Children.Add(hint);
            return grid;
        }

        /// <summary>
        /// A text field applied when it loses focus or on Enter. With <paramref name="check"/>,
        /// text that breaks the grammar is refused and kept in the field so it can be fixed.
        /// </summary>
        private StackPanel TextField<TValue>(string label, string value, Action<TValue> onChange, Func<string, Check<TValue>> check,
            Func<TValue, string> display, string placeholder = null, bool multiline = false, int rows = 3)
        {
            var tb = Input(value, multiline, rows);
            var error = new TextBlock { Foreground = T.BrushOf(T.Error), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed };
            var normalBorder = tb.BorderBrush;
            var committed = tb.Text;
            void Apply()
            {
                if (tb.Text == committed) return;
                var r = check(tb.Text);
                if (!r.Ok)
                {
                    tb.BorderBrush = T.BrushOf(T.Error);
                    error.Text = r.Error;
                    error.Visibility = Visibility.Visible;
                    return;
                }
                tb.ClearValue(Control.BorderBrushProperty);
                error.Visibility = Visibility.Collapsed;
                committed = display(r.Value);
                if (tb.Text != committed) tb.Text = committed;
                onChange(r.Value);
            }
            tb.LostKeyboardFocus += (s, e) => Apply();
            if (!multiline)
            {
                tb.KeyDown += (s, e) =>
                {
                    if (e.Key != Key.Enter) return;
                    Apply();
                    e.Handled = true;
                };
            }
            return Field(label, WithPlaceholder(tb, placeholder), error);
        }

        private StackPanel TextField(string label, string value, Action<string> onChange, Func<string, Check<string>> check = null,
            string placeholder = null, bool multiline = false, int rows = 3) =>
            TextField(label, value, onChange, check ?? (text => Check<string>.Success(multiline ? text.Replace("\r\n", "\n") : text)), s => s, placeholder, multiline, rows);

        private StackPanel ListField(string label, IEnumerable<string> value, Action<List<string>> onChange, Func<string, Check<string>> itemCheck, string placeholder) =>
            TextField(label, string.Join(", ", value), onChange, text => Expressions.CheckList(text, itemCheck), l => string.Join(", ", l), placeholder);

        private StackPanel SelectField(string label, string value, IEnumerable<(string Value, string Label)> options, Action<string> onChange)
        {
            var combo = new ComboBox { Margin = new Thickness(0) };
            Theme.Style(combo, VsResourceKeys.ComboBoxStyleKey);
            foreach (var (v, l) in options)
            {
                var item = new ComboBoxItem { Content = l, Tag = v };
                combo.Items.Add(item);
                if (v == value) combo.SelectedItem = item;
            }
            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem is ComboBoxItem item && (string)item.Tag != value) onChange((string)item.Tag);
            };
            return Field(label, combo);
        }

        private UIElement IssuesFor(string id)
        {
            var list = _editor.IssuesFor(id);
            if (list.Count == 0) return null;
            var panel = new StackPanel();
            panel.Children.Add(H4("Problems"));
            foreach (var i in list)
            {
                var row = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 4) };
                row.Inlines.Add(new Run(EditorControl.SeverityGlyph(i.Severity) + "  ") { Foreground = T.BrushOf(EditorControl.SeverityColor(T, i.Severity)) });
                row.Inlines.Add(new Run(i.Message));
                panel.Children.Add(row);
            }
            return panel;
        }

        private DockPanel Row(UIElement main, params UIElement[] trailing)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
            foreach (var t in trailing.Reverse())
            {
                DockPanel.SetDock(t, Dock.Right);
                row.Children.Add(t);
            }
            row.Children.Add(main);
            return row;
        }

        private static string Or(string a, string b) => string.IsNullOrEmpty(a) ? b : a;

        // ---------------------------------------------------------------- machine

        private void MachineProps()
        {
            var m = S.Model;
            Add(H3("State Machine"),
                TextField("Name", m.Name, v =>
                {
                    m.Name = v;
                    Commit();
                }),
                SelectField("Kind", m.Kind == MachineKind.Protocol ? "protocol" : "behavioral",
                    new[] { ("behavioral", "Behavioral state machine"), ("protocol", "Protocol state machine") }, v =>
                    {
                        m.Kind = v == "protocol" ? MachineKind.Protocol : MachineKind.Behavioral;
                        Commit(true);
                    }),
                TextField("Context (owning classifier)", m.Context, v =>
                {
                    m.Context = v;
                    Commit();
                }, placeholder: "e.g. MediaPlayer"),
                TextField("Documentation", m.Documentation, v =>
                {
                    m.Documentation = v;
                    Commit();
                }, multiline: true, rows: 4));
            var issues = IssuesFor("");
            if (issues != null) Add(issues);
            Add(H4("Tips"));
            var tips = new[]
            {
                ("Double-click", "empty canvas to add a state; a name or label to edit it."),
                ("Drag", "the ⊕ handle of a selected element to draw a transition."),
                ("Double-click", "a transition to add a bend point, a bend point to remove it."),
                ("Drop", "an element into a region to nest it."),
                ("Right-drag", "Space+drag or middle-drag pans; the wheel zooms; F fits."),
                ("Shift+click", "a tool keeps it active."),
                ("Label syntax", m.Kind == MachineKind.Protocol ? "[isReady()] event / [isDone()]" : "event, after(2s) [isReady() && !isBusy()] / doIt(); log()"),
                ("Behaviors", "are calls without arguments, e.g. start(); there are no variables."),
                ("Time", "after(500ms), after(2s), after(5m) or after(1h)."),
            };
            foreach (var (k, v) in tips)
            {
                var line = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = T.BrushOf(T.Muted), Margin = new Thickness(0, 0, 0, 4) };
                line.Inlines.Add(new Run(k) { FontWeight = FontWeights.SemiBold, Foreground = T.BrushOf(T.Foreground) });
                line.Inlines.Add(new Run(" " + v));
                Add(line);
            }
        }

        // ---------------------------------------------------------------- vertices

        private void VertexProps(Vertex v)
        {
            var ix = S.Index;
            Add(H3(v.Type.Title()));
            var group = Core.Tools.Swappable.FirstOrDefault(g => g.Contains(v.Type));
            if (group != null)
            {
                Add(SelectField("Kind", v.Type.Key(), group.Select(t => (t.Key(), t.Title())), value =>
                {
                    if (VertexTypes.TryParseKey(value, out var type)) S.ChangeType(v, type);
                    Render();
                }));
            }
            if (v.Type == VertexType.ConnectionPointRef)
            {
                RefProps(v);
            }
            else if (v.Type == VertexType.Comment)
            {
                Add(TextField("Text", v.Text, value =>
                {
                    v.Text = value;
                    Commit();
                }, multiline: true, rows: 5));
                Add(H4("Annotated elements"));
                var anchors = v.Anchors.Select(a => (object)ix.Vertex(a) ?? ix.Transition(a)).Where(a => a != null).ToList();
                if (anchors.Count == 0) Add(Muted("Drag the ⊕ handle onto an element to attach this comment."));
                foreach (var a in anchors)
                {
                    var id = a is Vertex av ? av.Id : ((Transition)a).Id;
                    var text = a is Transition at ? $"Transition {Or(Labels.TransitionLabel(S.Model, at), at.Id)}" : Or(((Vertex)a).Name, ((Vertex)a).Type.Title());
                    Add(Row(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                        PButton("✕", () =>
                        {
                            v.Anchors.Remove(id);
                            Commit(true);
                        }, "Detach", true)));
                }
            }
            else
            {
                Add(TextField("Name", v.Name, value =>
                {
                    v.Name = value.Trim();
                    Commit();
                }));
            }

            if (v.Type == VertexType.State) StateProps(v);

            var owner = ix.OwnerState(v);
            Add(H4("Location"));
            var where = owner != null
                ? $"{(ix.IsBorderVertex(v) ? "On the border of" : "Inside")} {Or(owner.Name, owner.Type.Title())}"
                : v.Type.IsPointType() ? "Connection point of the state machine" : "Top level";
            Add(Muted($"{where} · {Math.Round(v.X)}, {Math.Round(v.Y)} · {Math.Round(v.W)}×{Math.Round(v.H)}"));
            var issues = IssuesFor(v.Id);
            if (issues != null) Add(issues);
            var delete = PButton("Delete", _editor.DeleteSelection);
            delete.Margin = new Thickness(0, 12, 0, 0);
            Add(delete);
        }

        private void StateProps(Vertex v)
        {
            Action<string> Set(Action<string> assign) => value =>
            {
                assign(value);
                Commit();
            };
            Add(TextField("Stereotype", v.Stereotype, Set(x => v.Stereotype = x), Expressions.CheckName, "e.g. Critical"),
                H4("Behaviors"),
                TextField("entry /", v.Entry, Set(x => v.Entry = x), Expressions.CheckActions, "e.g. start(); log()"),
                TextField("exit /", v.Exit, Set(x => v.Exit = x), Expressions.CheckActions, "e.g. stop()"),
                TextField("do /", v.DoActivity, Set(x => v.DoActivity = x), Expressions.CheckActions, "e.g. poll()"),
                ListField("Deferrable events", v.Deferrable, list =>
                {
                    v.Deferrable = list;
                    Commit();
                }, Expressions.CheckEvent, "evA, evB"),
                TextField("State invariant", v.Invariant, Set(x => v.Invariant = x), Expressions.CheckCondition, "e.g. isRunning() && !isFaulty()"));

            Add(H4("Submachine"));
            var current = v.Submachine;
            var options = new List<(string, string)> { ("", "(none: not a submachine state)") };
            options.AddRange(S.Machines.Select(m => (m.Href, $"{m.Name} ({m.File})")));
            if (current.Length > 0 && !S.Machines.Any(m => m.Href == current)) options.Add((current, $"{S.MachineLabel(current)} (not found)"));
            Add(SelectField("Referenced state machine", current, options, value =>
            {
                S.SetSubmachine(v, value);
                Render();
            }));
            if (S.Machines.Count == 0 && current.Length == 0) Add(Muted("No other state machine (.fsm) was found in the solution."));
            if (v.Submachine.Length > 0)
            {
                var href = v.Submachine;
                Add(PButton($"Open {S.MachineLabel(href)}", () => _editor.OpenSubmachine(href)));
                SubmachineRefsSection(v);
            }
            else
            {
                Add(H4(v.Regions.Count > 1 ? "Orthogonal regions" : "Regions"));
                for (int i = 0; i < v.Regions.Count; i++)
                {
                    var region = v.Regions[i];
                    var tb = Input(region.Name);
                    var committed = region.Name;
                    void Apply()
                    {
                        if (tb.Text.Trim() == committed) return;
                        committed = region.Name = tb.Text.Trim();
                        Commit();
                    }
                    tb.LostKeyboardFocus += (s, e) => Apply();
                    tb.KeyDown += (s, e) =>
                    {
                        if (e.Key == Key.Enter) Apply();
                    };
                    Add(Row(WithPlaceholder(tb, $"Region {i + 1}"), PButton("✕", () =>
                    {
                        S.RemoveRegion(v, region.Id);
                        Render();
                    }, "Remove region and its contents", true)));
                }
                Add(PButton("+ Add region", () =>
                {
                    S.AddRegion(v);
                    Render();
                }));
                if (v.Regions.Count > 1)
                {
                    Add(SelectField("Region layout", v.RegionLayout == RegionLayout.Horizontal ? "horizontal" : "vertical",
                        new[] { ("vertical", "Stacked (top to bottom)"), ("horizontal", "Side by side") }, value =>
                        {
                            v.RegionLayout = value == "horizontal" ? RegionLayout.Horizontal : RegionLayout.Vertical;
                            Commit();
                        }));
                }
            }

            Add(H4("Internal transitions"));
            foreach (var t in Labels.InternalTransitions(S.Model, v).ToList())
            {
                var tb = Input(Labels.TransitionLabel(S.Model, t));
                var committed = tb.Text;
                void Apply()
                {
                    if (tb.Text == committed) return;
                    var err = Labels.ApplyLabel(S.Model, t.Clone(), tb.Text, v);
                    tb.BorderBrush = err != null ? T.BrushOf(T.Error) : null;
                    if (err == null) tb.ClearValue(Control.BorderBrushProperty);
                    tb.ToolTip = err;
                    if (err != null)
                    {
                        _editor.Toast(err);
                        return;
                    }
                    S.ApplyLabel(t, tb.Text);
                    committed = tb.Text = Labels.TransitionLabel(S.Model, t);
                }
                tb.LostKeyboardFocus += (s, e) => Apply();
                tb.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter) Apply();
                };
                Add(Row(tb, PButton("✕", () =>
                {
                    S.RemoveTransition(t);
                    Render();
                }, "Remove", true)));
            }
            Add(PButton("+ Add internal transition", () =>
            {
                S.AddInternalTransition(v);
                Render();
            }));
        }

        private void RefProps(Vertex v)
        {
            var st = S.Index.Vertex(v.Parent);
            var href = st?.Submachine ?? "";
            S.Submachines.TryGetValue(href, out var info);
            var machine = href.Length > 0 ? S.MachineLabel(href) : "";
            Add(Muted(st != null ? $"On {Or(st.Name, "state")}; refers to a point of {Or(machine, "(no submachine set)")}." : "Not attached to a state."));
            var pts = st != null ? S.SubmachinePoints(st) : new List<ConnectionPointInfo>();
            string KindText(PointKind k) => k == PointKind.Exit ? "exit" : "entry";
            if (pts.Count > 0)
            {
                var cur = $"{KindText(v.PointKind)}:{v.Ref}";
                var options = pts.Select(q => ($"{KindText(q.Kind)}:{q.Id}", $"{Or(q.Name, q.Id)} ({KindText(q.Kind)} point)")).ToList();
                if (!pts.Any(q => $"{KindText(q.Kind)}:{q.Id}" == cur)) options.Insert(0, (cur, v.Ref.Length > 0 ? $"{v.Ref} (missing)" : "— choose a point —"));
                Add(SelectField("Referenced point", cur, options, value =>
                {
                    var i = value.IndexOf(':');
                    v.PointKind = value.Substring(0, i) == "exit" ? PointKind.Exit : PointKind.Entry;
                    v.Ref = value.Substring(i + 1);
                    Commit(true);
                }));
            }
            else
            {
                var why = href.Length == 0 ? ""
                    : info == null ? "Looking up the submachine…"
                    : !info.Found ? $"{machine} was not found, so its points cannot be listed."
                    : $"{machine} has no entry or exit points at its top level.";
                if (why.Length > 0) Add(Muted(why));
                Add(TextField("Referenced point id", v.Ref, value =>
                    {
                        v.Ref = value.Trim();
                        Commit();
                    }),
                    SelectField("Direction", KindText(v.PointKind), new[] { ("entry", "Entry point"), ("exit", "Exit point") }, value =>
                    {
                        v.PointKind = value == "exit" ? PointKind.Exit : PointKind.Entry;
                        Commit(true);
                    }));
            }
            if (href.Length > 0) Add(PButton($"Open {machine}", () => _editor.OpenSubmachine(href)));
        }

        private void SubmachineRefsSection(Vertex v)
        {
            Add(H4("Connection point references"));
            if (!S.Submachines.TryGetValue(v.Submachine, out var info))
            {
                Add(Muted("Looking up the submachine…"));
                return;
            }
            if (!info.Found)
            {
                Add(Muted($"{S.MachineLabel(v.Submachine)} was not found. Pick another state machine above."));
                return;
            }
            Add(Muted($"From {info.File}"));
            if (info.Points.Count == 0)
            {
                Add(Muted($"{S.MachineLabel(v.Submachine)} has no entry or exit points. Add them in that machine by placing entry/exit points on empty canvas."));
                return;
            }
            var refs = S.RefsOf(v);
            foreach (var q in info.Points)
            {
                var existing = refs.FirstOrDefault(r => r.Ref == q.Id);
                var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                label.Inlines.Add(new Run($"{(q.Kind == PointKind.Entry ? "○" : "⊗")} {Or(q.Name, q.Id)}"));
                label.Inlines.Add(new Run($" · {(q.Kind == PointKind.Entry ? "entry" : "exit")}") { Foreground = T.BrushOf(T.Muted) });
                var point = q;
                var action = existing != null
                    ? PButton("Select", () =>
                    {
                        _editor.Reveal(existing.Id);
                    }, "Select the reference", true)
                    : PButton("Add", () =>
                    {
                        S.AddAllReferences(v, point);
                        Render();
                    }, "Add a reference on the border", true);
                Add(Row(label, action));
            }
            if (S.FreePoints(v).Count > 1)
            {
                Add(PButton("Add all references", () =>
                {
                    S.AddAllReferences(v);
                    Render();
                }));
            }
        }

        // ---------------------------------------------------------------- transitions

        private void TransitionProps(Transition t)
        {
            var s = S.Index.Vertex(t.Source);
            var g = S.Index.Vertex(t.Target);
            string Nm(Vertex x) => x == null ? "?" : Or(x.Name, x.Type.Title());
            Add(H3("Transition"),
                Muted($"{Nm(s)} → {Nm(g)}"),
                SelectField("Kind", t.Kind.ToString().ToLowerInvariant(), new[] { ("external", "External"), ("local", "Local"), ("internal", "Internal") }, value =>
                {
                    S.SetKind(t, value == "internal" ? TransitionKind.Internal : value == "local" ? TransitionKind.Local : TransitionKind.External);
                    Render();
                }),
                ListField("Triggers", t.Triggers, list =>
                {
                    t.Triggers = list;
                    Commit();
                }, Expressions.CheckTrigger, "event, after(500ms), after(2s)"));
            if (S.Model.Kind == MachineKind.Protocol)
            {
                Add(TextField("Precondition", t.Precondition, value =>
                    {
                        t.Precondition = value;
                        Commit();
                    }, Expressions.CheckCondition, "e.g. isOpen()"),
                    TextField("Postcondition", t.Postcondition, value =>
                    {
                        t.Postcondition = value;
                        Commit();
                    }, Expressions.CheckCondition, "e.g. isClosed()"));
            }
            else
            {
                var allowElse = Labels.AllowsElse(s);
                Add(TextField("Guard", t.Guard, value =>
                    {
                        t.Guard = value;
                        Commit();
                    }, text => Expressions.CheckGuard(text, allowElse), allowElse ? "e.g. hasDisc() && !isJammed(), or else" : "e.g. hasDisc() && !isJammed()"),
                    TextField("Effect", t.Effect, value =>
                    {
                        t.Effect = value;
                        Commit();
                    }, Expressions.CheckActions, "e.g. notify(); log()"));
            }
            Add(H4("Routing"));
            var routing = new WrapPanel();
            routing.Children.Add(PButton("Straighten", () =>
            {
                S.Straighten(t);
                Render();
            }));
            routing.Children.Add(PButton("Reverse", () =>
            {
                S.Reverse(t);
                Render();
            }));
            Add(routing);
            var issues = IssuesFor(t.Id);
            if (issues != null) Add(issues);
            var delete = PButton("Delete", _editor.DeleteSelection);
            delete.Margin = new Thickness(0, 12, 0, 0);
            Add(delete);
        }
    }
}
