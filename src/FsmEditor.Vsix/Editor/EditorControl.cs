using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FsmEditor.Core;
using Microsoft.VisualStudio.Shell;

// WPF controls: Dispatcher.BeginInvoke only defers work on the UI thread (after the current input), it never switches threads.
#pragma warning disable VSTHRD001, VSTHRD110

namespace FsmEditor.Editor
{
    /// <summary>What the diagram editor asks of the document window hosting it.</summary>
    internal interface IEditorHost
    {
        /// <summary>Writes the model to the document (as XMI).</summary>
        void Write(FsmModel model);
        void Undo();
        void Redo();
        void GenerateCode();
        void ExportSvg();
        void OpenAsText();
        void OpenSubmachine(string href);
    }

    /// <summary>
    /// The whole diagram editor: toolbar, toolbox, canvas, properties panel and
    /// status bar. It works on a <see cref="DiagramSession"/> and hands every
    /// change to its <see cref="IEditorHost"/>, which writes the document.
    /// </summary>
    internal sealed class EditorControl : DockPanel
    {
        private readonly IEditorHost _host;
        private readonly DiagramCanvas _canvas;
        private readonly PropertiesPanel _props;
        private readonly Grid _canvasHost = new Grid();
        private readonly Canvas _overlay = new Canvas { ClipToBounds = true };
        private readonly Border _toolbar = new Border();
        private readonly ScrollViewer _toolbox = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 176 };
        private readonly ScrollViewer _propsScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 270 };
        private readonly Border _status = new Border();
        private readonly Border _problems = new Border { Visibility = Visibility.Collapsed };
        private readonly Border _toast = new Border { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        private readonly Border _parseError = new Border { Visibility = Visibility.Collapsed };
        private readonly DispatcherTimer _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
        private readonly Dictionary<string, Button> _toolButtons = new Dictionary<string, Button>();
        private TextBlock _title;
        private TextBlock _zoom;
        private List<Issue> _issues = new List<Issue>();
        private Dictionary<string, List<Issue>> _issuesById = new Dictionary<string, List<Issue>>();
        private bool _firstLoad = true;
        private InlineEdit _inline;

        public EditorControl(IEditorHost host)
        {
            _host = host;
            _canvas = new DiagramCanvas(this);
            _props = new PropertiesPanel(this);
            LastChildFill = true;
            Focusable = false;

            SetDock(_toolbar, Dock.Top);
            SetDock(_status, Dock.Bottom);
            SetDock(_toolbox, Dock.Left);
            SetDock(_propsScroll, Dock.Right);
            _propsScroll.Content = _props;
            _canvasHost.Children.Add(_canvas);
            _canvasHost.Children.Add(_overlay);
            _problems.HorizontalAlignment = HorizontalAlignment.Left;
            _problems.VerticalAlignment = VerticalAlignment.Bottom;
            _problems.Margin = new Thickness(12);
            _problems.MaxWidth = 560;
            _toast.HorizontalAlignment = HorizontalAlignment.Center;
            _toast.VerticalAlignment = VerticalAlignment.Top;
            _toast.Margin = new Thickness(12);
            _canvasHost.Children.Add(_problems);
            _canvasHost.Children.Add(_toast);
            _canvasHost.Children.Add(_parseError);
            Children.Add(_toolbar);
            Children.Add(_status);
            Children.Add(_toolbox);
            Children.Add(_propsScroll);
            Children.Add(_canvasHost);

            _canvas.ViewChanged += (s, e) => UpdateZoomLabel();
            _canvas.ToolChanged += (s, e) =>
            {
                UpdateToolbox();
                RenderStatus();
            };
            _canvas.SizeChanged += (s, e) =>
            {
                if (_firstLoad && Session != null && e.NewSize.Width > 0)
                {
                    _firstLoad = false;
                    _canvas.Fit();
                }
            };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                _toast.Visibility = Visibility.Collapsed;
            };
            Theme.Changed += OnThemeChanged;
            Unloaded += (s, e) => Theme.Changed -= OnThemeChanged;
            BuildChrome();
        }

        public DiagramSession Session { get; private set; }

        public DiagramCanvas Canvas => _canvas;

        private void OnThemeChanged(object sender, EventArgs e)
        {
            BuildChrome();
            RenderProps();
            _canvas.InvalidateVisual();
        }

        // ---------------------------------------------------------------- document updates

        /// <summary>
        /// New state from the document: the model when it changed (null when the
        /// document still holds what this editor wrote), the validation results,
        /// or the reason the file can't be read.
        /// </summary>
        public void Update(FsmModel model, List<Issue> issues, string error,
            IReadOnlyDictionary<string, SubmachineInfo> submachines, IReadOnlyList<MachineInfo> machines)
        {
            _issues = issues ?? new List<Issue>();
            _issuesById = _issues.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.ToList());
            if (error != null)
            {
                ShowParseError(error);
                RenderStatus();
                return;
            }
            _parseError.Visibility = Visibility.Collapsed;
            if (model != null)
            {
                if (Session == null)
                {
                    Session = new DiagramSession(model);
                    Session.Committed += (s, e) => OnCommitted();
                    Session.Message += (s, message) => Toast(message);
                }
                else
                {
                    Session.ReplaceModel(model);
                }
            }
            if (Session == null) return;
            Session.Submachines = submachines ?? new Dictionary<string, SubmachineInfo>();
            Session.Machines = machines ?? new List<MachineInfo>();
            if (model != null)
            {
                if (_firstLoad && _canvas.ActualWidth > 0)
                {
                    _firstLoad = false;
                    Dispatcher.BeginInvoke(new Action(_canvas.Fit), DispatcherPriority.Loaded);
                }
                RenderProps();
            }
            else if (!_props.IsKeyboardFocusWithin)
            {
                RenderProps();
            }
            _canvas.InvalidateVisual();
            UpdateTitle();
            RenderStatus();
            if (_problems.Visibility == Visibility.Visible) RenderProblems();
        }

        private void OnCommitted()
        {
            _host.Write(Session.Model);
            _canvas.InvalidateVisual();
            UpdateTitle();
            RenderStatus();
        }

        public List<Issue> IssuesFor(string id) => _issuesById.TryGetValue(id, out var list) ? list : new List<Issue>();

        public void RenderProps()
        {
            if (Session != null) _props.Render();
        }

        public void OpenSubmachine(string href) => _host.OpenSubmachine(href);

        public void Reveal(string id)
        {
            if (Session == null) return;
            Session.Selection = id.Length > 0 ? new HashSet<string> { id } : new HashSet<string>();
            if (id.Length > 0) _canvas.Reveal(id);
            RenderProps();
            _canvas.InvalidateVisual();
            RenderStatus();
        }

        public string ExportSvg() => Session == null ? null : SvgExport.Export(Session);

        // ---------------------------------------------------------------- editing commands

        public bool HasSelection => Session != null && Session.Selection.Count > 0;

        public void DeleteSelection()
        {
            if (Session == null) return;
            Session.DeleteSelection();
            RenderProps();
        }

        public void SelectAll()
        {
            if (Session == null) return;
            Session.Selection = new HashSet<string>(Session.Model.Vertices.Select(v => v.Id));
            RenderProps();
            _canvas.InvalidateVisual();
            RenderStatus();
        }

        public void Duplicate()
        {
            var data = Session?.CollectSelection();
            if (data == null) return;
            Session.ResetPasteOffset();
            Session.Paste(data);
            RenderProps();
        }

        public void Copy(bool cut)
        {
            var data = Session?.CollectSelection();
            if (data == null) return;
            try
            {
                Clipboard.SetText(ClipboardJson.Write(data));
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Toast("The clipboard is busy; try again.");
                return;
            }
            Session.ResetPasteOffset();
            if (cut) DeleteSelection();
        }

        /// <summary>Pastes copied elements; returns false when the clipboard holds something else.</summary>
        public bool Paste()
        {
            if (Session == null) return false;
            string text;
            try
            {
                text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return false;
            }
            var data = ClipboardJson.TryRead(text);
            if (data == null) return false;
            Session.Paste(data);
            RenderProps();
            return true;
        }

        public bool CanPaste
        {
            get
            {
                try
                {
                    return Session != null && Clipboard.ContainsText();
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    return false;
                }
            }
        }

        // ---------------------------------------------------------------- inline editing

        private sealed class InlineEdit
        {
            public TextBox Box;
            public string Id;
            public bool Cancelled;
            public bool Multiline;
        }

        public void StartInlineEdit(string id)
        {
            CommitInlineEdit();
            if (Session == null) return;
            Session.Reindex();
            var ix = Session.Index;
            var v = ix.Vertex(id);
            var t = ix.Transition(id);
            if (v == null && t == null) return;
            if (v != null && (v.Type == VertexType.ConnectionPointRef ||
                (v.Type.IsPseudostate() && v.Type != VertexType.EntryPoint && v.Type != VertexType.ExitPoint &&
                 v.Type != VertexType.Choice && v.Type != VertexType.Junction && v.Type != VertexType.Fork && v.Type != VertexType.Join)))
            {
                // Unnamed markers: edit them in the properties panel instead.
                _props.FocusFirstField();
                return;
            }
            RectD box;
            string value;
            var multiline = false;
            if (v != null)
            {
                if (v.Type == VertexType.Comment)
                {
                    box = new RectD(v.X, v.Y, v.W, v.H);
                    value = v.Text;
                    multiline = true;
                }
                else if (v.Type == VertexType.State)
                {
                    var atTop = v.Regions.Count > 0 || Labels.ActivityLines(Session.Model, v).Count > 0;
                    var y = atTop ? v.Y + (v.Stereotype.Length > 0 ? 14 : 2) : v.Y + v.H / 2 - 12 + (v.Stereotype.Length > 0 ? 6 : 0);
                    box = new RectD(v.X + 4, y, v.W - 8, 24);
                    value = v.Name;
                }
                else
                {
                    box = new RectD(v.X + v.W / 2 - 60, v.Y + v.H + 2, 120, 22);
                    value = v.Name;
                }
            }
            else
            {
                var pts = Core.Geometry.Route(ix, t);
                if (pts == null) return;
                var lp = Core.Geometry.LabelPos(t, pts);
                var x = lp.Anchor == TextAnchor.End ? lp.X - 220 : lp.Anchor == TextAnchor.Start ? lp.X : lp.X - 110;
                box = new RectD(x, lp.Y - 16, 220, 22);
                value = Labels.TransitionLabel(Session.Model, t);
            }
            var th = Theme.Current;
            var tb = new TextBox
            {
                Text = value,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextAlignment = multiline ? TextAlignment.Left : TextAlignment.Center,
                VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
                Background = th.BrushOf(th.Background),
                Foreground = th.BrushOf(th.Foreground),
                CaretBrush = th.BrushOf(th.Foreground),
                BorderBrush = th.BrushOf(th.Accent),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(2, 1, 2, 1),
                Width = Math.Max(80, box.W * _canvas.Zoom),
                Height = Math.Max(22, box.H * _canvas.Zoom),
            };
            if (t != null)
            {
                tb.ToolTip = Session.Model.Kind == MachineKind.Protocol ? "[isReady()] event / [isDone()]" : "event, after(2s) [isReady()] / doIt()";
            }
            var at = _canvas.ToScreen(box.X, box.Y);
            System.Windows.Controls.Canvas.SetLeft(tb, at.X);
            System.Windows.Controls.Canvas.SetTop(tb, at.Y);
            _overlay.Children.Add(tb);
            _inline = new InlineEdit { Box = tb, Id = id, Multiline = multiline };
            tb.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    if (_inline != null) _inline.Cancelled = true;
                    CommitInlineEdit();
                    _canvas.Focus();
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter && (!multiline || (Keyboard.Modifiers & ModifierKeys.Control) != 0))
                {
                    e.Handled = true;
                    // Keep editing when the label breaks the grammar.
                    if (CommitInlineEdit(true)) _canvas.Focus();
                }
            };
            tb.LostKeyboardFocus += (s, e) => CommitInlineEdit();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                tb.Focus();
                tb.SelectAll();
            }), DispatcherPriority.Input);
        }

        /// <summary>Applies the inline editor. Returns false when it stays open because of an invalid label.</summary>
        public bool CommitInlineEdit(bool fromKey = false)
        {
            var edit = _inline;
            if (edit == null) return true;
            var text = edit.Box.Text;
            var ix = Session?.Index;
            var v = ix?.Vertex(edit.Id);
            var t = ix?.Transition(edit.Id);
            if (t != null && !edit.Cancelled && Labels.TransitionLabel(Session.Model, t) != text.Trim())
            {
                var probe = t.Clone();
                var err = Labels.ApplyLabel(Session.Model, probe, text, ix.Vertex(t.Source));
                if (err != null && fromKey)
                {
                    edit.Box.BorderBrush = Theme.Current.BrushOf(Theme.Current.Error);
                    edit.Box.ToolTip = err;
                    Toast(err);
                    return false;
                }
                CloseInline();
                if (err != null)
                {
                    Toast($"Label not changed. {err}");
                }
                else
                {
                    Session.ApplyLabel(t, text);
                    RenderProps();
                }
                return true;
            }
            CloseInline();
            if (edit.Cancelled || Session == null || v == null) return true;
            if (v.Type == VertexType.Comment)
            {
                var normalized = text.Replace("\r\n", "\n");
                if (v.Text == normalized) return true;
                v.Text = normalized;
            }
            else
            {
                if (v.Name == text.Trim()) return true;
                v.Name = text.Trim();
            }
            Session.Commit();
            RenderProps();
            return true;
        }

        private void CloseInline()
        {
            if (_inline == null) return;
            _overlay.Children.Remove(_inline.Box);
            _inline = null;
        }

        // ---------------------------------------------------------------- chrome

        private void BuildChrome()
        {
            var th = Theme.Current;
            Background = th.BrushOf(th.Background);
            TextElement.SetForeground(this, th.BrushOf(th.Foreground));
            BuildToolbar();
            BuildToolbox();
            _propsScroll.Background = th.BrushOf(th.Panel);
            _propsScroll.BorderBrush = th.BrushOf(th.Border);
            _status.BorderBrush = th.BrushOf(th.Border);
            _status.BorderThickness = new Thickness(0, 1, 0, 0);
            _status.Padding = new Thickness(10, 2, 10, 2);
            _problems.Background = th.BrushOf(th.Widget);
            _problems.BorderBrush = th.BrushOf(th.Border);
            _problems.BorderThickness = new Thickness(1);
            _problems.CornerRadius = new CornerRadius(4);
            _problems.Padding = new Thickness(0, 4, 0, 4);
            _toast.Background = th.BrushOf(th.IsDark ? Theme.Mix(th.Background, Colors.White, 0.15) : Color.FromRgb(0x33, 0x33, 0x33));
            _toast.CornerRadius = new CornerRadius(4);
            _toast.Padding = new Thickness(12, 6, 12, 6);
            _parseError.Background = th.BrushOf(th.Background);
            RenderStatus();
        }

        private static Border Separator(Theme th) => new Border
        {
            Width = 1,
            Height = 18,
            Margin = new Thickness(6, 0, 6, 0),
            Background = th.BrushOf(th.Border),
        };

        private Button ToolbarButton(string icon, string tooltip, Action onClick, string text = null)
        {
            var th = Theme.Current;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var image = Icons.Create(icon, th.Foreground, th.Background);
            if (image != null) content.Children.Add(image);
            if (text != null) content.Children.Add(new TextBlock { Text = text, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var b = FlatButton(content, tooltip);
            b.Click += (s, e) => onClick();
            return b;
        }

        /// <summary>A borderless button that highlights on hover.</summary>
        public static Button FlatButton(object content, string tooltip)
        {
            var th = Theme.Current;
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "bg";
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, th.BrushOf(th.Hover), "bg"));
            template.Triggers.Add(hover);
            return new Button
            {
                Content = content,
                ToolTip = tooltip,
                Template = template,
                Padding = new Thickness(6, 3, 6, 3),
                Foreground = th.BrushOf(th.Foreground),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Focusable = false,
                Cursor = Cursors.Hand,
            };
        }

        private void BuildToolbar()
        {
            var th = Theme.Current;
            var bar = new DockPanel { LastChildFill = false };
            _title = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis };
            _zoom = new TextBlock { MinWidth = 44, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(_title);
            left.Children.Add(ToolbarButton("undo", "Undo (Ctrl+Z)", _host.Undo));
            left.Children.Add(ToolbarButton("redo", "Redo (Ctrl+Y)", _host.Redo));
            left.Children.Add(Separator(th));
            left.Children.Add(ToolbarButton("zoomOut", "Zoom out (Ctrl+-)", () => _canvas.ZoomCenter(1 / 1.2)));
            left.Children.Add(_zoom);
            left.Children.Add(ToolbarButton("zoomIn", "Zoom in (Ctrl+=)", () => _canvas.ZoomCenter(1.2)));
            left.Children.Add(ToolbarButton("fit", "Fit diagram (F)", _canvas.Fit));
            left.Children.Add(Separator(th));
            left.Children.Add(ToolbarButton("trash", "Delete selection (Del)", DeleteSelection));
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(ToolbarButton("code", "Generate code from a template", _host.GenerateCode, "Code"));
            right.Children.Add(ToolbarButton("export", "Export as SVG", _host.ExportSvg, "SVG"));
            right.Children.Add(Separator(th));
            right.Children.Add(ToolbarButton("xmi", "Open as XMI text", _host.OpenAsText, "XMI"));
            DockPanel.SetDock(left, Dock.Left);
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(left);
            bar.Children.Add(right);
            _toolbar.Child = bar;
            _toolbar.Padding = new Thickness(8, 3, 8, 3);
            _toolbar.BorderBrush = th.BrushOf(th.Border);
            _toolbar.BorderThickness = new Thickness(0, 0, 0, 1);
            UpdateTitle();
            UpdateZoomLabel();
        }

        private void UpdateTitle()
        {
            if (_title == null || Session == null) return;
            var m = Session.Model;
            _title.Text = (m.Kind == MachineKind.Protocol ? "{protocol} " : "") + (m.Name.Length > 0 ? m.Name : "State machine");
        }

        private void UpdateZoomLabel()
        {
            if (_zoom != null) _zoom.Text = $"{Math.Round(_canvas.Zoom * 100)}%";
        }

        private void BuildToolbox()
        {
            var th = Theme.Current;
            var panel = new StackPanel { Margin = new Thickness(6, 6, 6, 16) };
            _toolButtons.Clear();
            foreach (var group in Core.Tools.Groups)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = group.Title.ToUpperInvariant(),
                    FontSize = 11,
                    Foreground = th.BrushOf(th.Muted),
                    Margin = new Thickness(4, 10, 4, 4),
                });
                foreach (var tool in group.Items)
                {
                    var row = new DockPanel();
                    var icon = Icons.Create(tool.Id, th.Foreground, th.Panel, 20);
                    if (icon != null)
                    {
                        icon.Margin = new Thickness(0, 0, 8, 0);
                        DockPanel.SetDock(icon, Dock.Left);
                        row.Children.Add(icon);
                    }
                    if (tool.Key.HasValue)
                    {
                        var key = new TextBlock { Text = char.ToUpperInvariant(tool.Key.Value).ToString(), FontSize = 10, Foreground = th.BrushOf(th.Muted), VerticalAlignment = VerticalAlignment.Center };
                        DockPanel.SetDock(key, Dock.Right);
                        row.Children.Add(key);
                    }
                    row.Children.Add(new TextBlock { Text = tool.Label, VerticalAlignment = VerticalAlignment.Center });
                    var tip = tool.Label + (tool.Key.HasValue ? $" ({char.ToUpperInvariant(tool.Key.Value)})" : "") + (tool.IsMode ? "" : " — click then click the canvas, or drag onto it");
                    var b = FlatButton(row, tip);
                    b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    b.Padding = new Thickness(6, 4, 6, 4);
                    var id = tool.Id;
                    b.Click += (s, e) =>
                    {
                        _canvas.SetTool(id, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                        _canvas.Focus();
                    };
                    if (!tool.IsMode) EnableDrag(b, id);
                    _toolButtons[id] = b;
                    panel.Children.Add(b);
                }
            }
            panel.Children.Add(new TextBlock
            {
                Text = "Shift+click a tool to keep it active. Esc returns to selection.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = th.BrushOf(th.Muted),
                Margin = new Thickness(4, 12, 4, 0),
            });
            _toolbox.Content = panel;
            _toolbox.Background = th.BrushOf(th.Panel);
            _toolbox.BorderBrush = th.BrushOf(th.Border);
            UpdateToolbox();
        }

        private static void EnableDrag(Button b, string toolId)
        {
            Point? down = null;
            b.PreviewMouseLeftButtonDown += (s, e) => down = e.GetPosition(b);
            b.PreviewMouseLeftButtonUp += (s, e) => down = null;
            b.PreviewMouseMove += (s, e) =>
            {
                if (down == null || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(b);
                if (Math.Abs(p.X - down.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(p.Y - down.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                down = null;
                DragDrop.DoDragDrop(b, new DataObject(DiagramCanvas.ToolDataFormat, toolId), DragDropEffects.Copy);
            };
        }

        private void UpdateToolbox()
        {
            var th = Theme.Current;
            foreach (var pair in _toolButtons)
            {
                var active = pair.Key == _canvas.Tool;
                pair.Value.Background = active ? th.BrushOf(th.Accent, 0.2) : Brushes.Transparent;
                pair.Value.BorderBrush = active ? th.BrushOf(th.Accent) : Brushes.Transparent;
            }
        }

        public void RenderStatus()
        {
            var th = Theme.Current;
            var errors = _issues.Count(i => i.Severity == Severity.Error);
            var warnings = _issues.Count(i => i.Severity == Severity.Warning);
            var infos = _issues.Count - errors - warnings;
            var bar = new DockPanel { LastChildFill = false };
            var problems = new TextBlock { Cursor = Cursors.Hand, ToolTip = "Show problems", Margin = new Thickness(0, 0, 16, 0) };
            problems.Inlines.Add(new System.Windows.Documents.Run($"✕ {errors}") { Foreground = th.BrushOf(errors > 0 ? th.Error : th.Muted) });
            problems.Inlines.Add(new System.Windows.Documents.Run("  "));
            problems.Inlines.Add(new System.Windows.Documents.Run($"⚠ {warnings}") { Foreground = th.BrushOf(warnings > 0 ? th.Warning : th.Muted) });
            if (infos > 0) problems.Inlines.Add(new System.Windows.Documents.Run($"  ℹ {infos}") { Foreground = th.BrushOf(th.Muted) });
            problems.MouseLeftButtonUp += (s, e) => ToggleProblems();
            var counts = new TextBlock
            {
                Foreground = th.BrushOf(th.Muted),
                Text = Session == null ? "" : $"{Session.Model.Vertices.Count(v => v.Type == VertexType.State)} states · {Session.Model.Transitions.Count} transitions",
            };
            var toolName = _canvas.Tool != null ? Core.Tools.ById(_canvas.Tool)?.Label : null;
            var right = new TextBlock
            {
                Foreground = th.BrushOf(th.Muted),
                Text = toolName != null ? $"Tool: {toolName}{(_canvas.ToolSticky ? " (sticky)" : "")}" : Session != null && Session.Selection.Count > 0 ? $"{Session.Selection.Count} selected" : "",
            };
            DockPanel.SetDock(problems, Dock.Left);
            DockPanel.SetDock(counts, Dock.Left);
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(problems);
            bar.Children.Add(counts);
            bar.Children.Add(right);
            TextElement.SetFontSize(bar, 12);
            _status.Child = bar;
        }

        private void ToggleProblems()
        {
            if (_problems.Visibility == Visibility.Visible)
            {
                HideProblems();
                return;
            }
            RenderProblems();
            _problems.Visibility = Visibility.Visible;
        }

        public void HideProblems() => _problems.Visibility = Visibility.Collapsed;

        private void RenderProblems()
        {
            var th = Theme.Current;
            var list = new StackPanel();
            if (_issues.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = "No problems. The state machine is well-formed.", Foreground = th.BrushOf(th.Muted), Margin = new Thickness(12, 4, 12, 4) });
            }
            foreach (var issue in _issues.OrderBy(i => i.Severity))
            {
                var row = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 3, 12, 3) };
                row.Inlines.Add(new System.Windows.Documents.Run(SeverityGlyph(issue.Severity) + "  ") { Foreground = th.BrushOf(SeverityColor(th, issue.Severity)) });
                row.Inlines.Add(new System.Windows.Documents.Run(issue.Message));
                var item = FlatButton(row, null);
                item.HorizontalContentAlignment = HorizontalAlignment.Left;
                item.Padding = new Thickness(0);
                var id = issue.Id;
                item.Click += (s, e) => Reveal(id);
                list.Children.Add(item);
            }
            TextElement.SetFontSize(list, 12);
            _problems.Child = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(120, _canvasHost.ActualHeight * 0.4) };
        }

        public static string SeverityGlyph(Severity s) => s == Severity.Error ? "✕" : s == Severity.Warning ? "⚠" : "ℹ";

        public static Color SeverityColor(Theme th, Severity s) => s == Severity.Error ? th.Error : s == Severity.Warning ? th.Warning : th.Info;

        public void Toast(string message)
        {
            _toast.Child = new TextBlock { Text = message, Foreground = Brushes.White, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
            _toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void ShowParseError(string error)
        {
            var th = Theme.Current;
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock
            {
                Text = "This file cannot be displayed because it is not a readable UML state machine (XMI):",
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10),
            });
            panel.Children.Add(new TextBlock
            {
                Text = error,
                Foreground = th.BrushOf(th.Error),
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10),
            });
            var open = new Button { Content = "Open as Text", HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(10, 4, 10, 4) };
            Theme.Style(open, VsResourceKeys.ButtonStyleKey);
            open.Click += (s, e) => _host.OpenAsText();
            panel.Children.Add(open);
            _parseError.Child = panel;
            _parseError.Visibility = Visibility.Visible;
        }
    }
}
