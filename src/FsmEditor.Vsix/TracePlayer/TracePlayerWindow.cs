using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Community.VisualStudio.Toolkit;
using FsmEditor.Core;
using FsmEditor.Editor;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

// WPF controls: Dispatcher.BeginInvoke only defers work on the UI thread.
#pragma warning disable VSTHRD001, VSTHRD110

namespace FsmEditor.TracePlayer
{
    /// <summary>The Trace Player tool window (View › Other Windows › Trace Player), docked with the Output window.</summary>
    public sealed class TracePlayerWindow : BaseToolWindow<TracePlayerWindow>
    {
        public override string GetTitle(int toolWindowId) => "Trace Player";

        public override Type PaneType => typeof(Pane);

        public override System.Threading.Tasks.Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult<FrameworkElement>(new TracePlayerControl());

        [Guid("8f0d4c3e-6a0b-4e55-9a9f-3b5c2d7e1f40")]
        internal sealed class Pane : ToolWindowPane
        {
            public Pane() => BitmapImageMoniker = KnownMonikers.Timeline;
        }
    }

    /// <summary>
    /// The window's content, as media/tracePlayer.js of the VS Code edition: controls, timeline, open logs, problems,
    /// and the records around the cursor. The slider is kept across renders, so dragging it isn't interrupted.
    /// </summary>
    internal sealed class TracePlayerControl : DockPanel
    {
        private static readonly double[] Speeds = { 0.25, 0.5, 1, 2, 5, 10 };
        private readonly TracePlayerService _player = TracePlayerService.Instance;
        private readonly Slider _slider = new Slider { Minimum = -1, Maximum = 0, SmallChange = 1, LargeChange = 10, IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(0, 4, 0, 4) };
        private readonly StackPanel _top = new StackPanel();
        private readonly ContentControl _bar = new ContentControl();
        private readonly ContentControl _traces = new ContentControl();
        private readonly StackPanel _problems = new StackPanel();
        private readonly StackPanel _rows = new StackPanel();
        private readonly ScrollViewer _records;
        private readonly Border _recordsBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 4, 0, 0) };
        /// <summary>Whether the layout of a session with logs is in place (it is built once, so the slider survives renders).</summary>
        private bool _built;
        private bool _updating;
        private bool _dragging;

        public TracePlayerControl()
        {
            LastChildFill = true;
            Margin = new Thickness(8, 6, 8, 8);
            SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _slider.ValueChanged += (s, e) =>
            {
                if (!_updating) _player.Seek((int)Math.Round(e.NewValue));
            };
            _slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((s, e) => _dragging = true));
            _slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((s, e) => _dragging = false));
            _records = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _rows };
            _recordsBorder.Child = _records;
            _top.Children.Add(_bar);
            _top.Children.Add(_slider);
            _top.Children.Add(_traces);
            _top.Children.Add(_problems);
            SetDock(_top, Dock.Top);
            _player.Changed += Render;
            Theme.Changed += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Render();
            };
            Unloaded += (s, e) => _player.Changed -= Render;
            Loaded += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _player.Changed -= Render;
                _player.Changed += Render;
                Render();
            };
            Render();
        }

        private static Button Btn(string label, string tip, Action click, bool enabled = true, bool primary = false)
        {
            var b = new Button { Content = label, ToolTip = tip, IsEnabled = enabled, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 4, 0), MinWidth = 28 };
            b.SetResourceReference(StyleProperty, VsResourceKeys.ButtonStyleKey);
            if (primary) b.FontWeight = FontWeights.SemiBold;
            b.Click += (s, e) => click();
            return b;
        }

        /// <summary>A button showing the icon <paramref name="icon"/> of the editor's icons.</summary>
        private static Button IconBtn(string icon, string tip, Action click, bool enabled = true)
        {
            var th = Theme.Current;
            var b = Btn("", tip, click, enabled);
            b.Content = Icons.Create(icon, th.Foreground, th.Panel, 16);
            b.Padding = new Thickness(5, 1, 5, 1);
            if (!enabled) ((UIElement)b.Content).Opacity = 0.4;
            return b;
        }

        /// <summary>One color per log, the same in the list of logs and in the records (as the VS Code edition's).</summary>
        private static Brush Swatch(int i) => new SolidColorBrush(Hsl((i * 67 + 200) % 360, 0.6, 0.55));

        private static Color Hsl(double h, double s, double l)
        {
            double F(double n)
            {
                var k = (n + h / 30) % 12;
                return l - s * Math.Min(l, 1 - l) * Math.Max(-1, Math.Min(k - 3, Math.Min(9 - k, 1)));
            }
            return Color.FromRgb((byte)Math.Round(F(0) * 255), (byte)Math.Round(F(8) * 255), (byte)Math.Round(F(4) * 255));
        }

        private static FrameworkElement Dot(Brush brush) => new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = brush, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

        private void Render()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var s = _player.Session;
            var th = Theme.Current;
            if (s.Traces.Count == 0)
            {
                Children.Clear();
                _built = false;
                var empty = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
                empty.Children.Add(new TextBlock
                {
                    Text = "Replay a log of your state machines on their diagrams: the active states, the transitions taken and the branches chosen at choices and junctions.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = th.BrushOf(th.Muted),
                    Margin = new Thickness(0, 0, 0, 10),
                    MaxWidth = 700,
                    HorizontalAlignment = HorizontalAlignment.Left,
                });
                var open = Btn("Open Log…", "Open one or more log files", OpenLogs, primary: true);
                open.HorizontalAlignment = HorizontalAlignment.Left;
                empty.Children.Add(open);
                Children.Add(empty);
                return;
            }
            if (!_built)
            {
                Children.Clear();
                Children.Add(_top);
                Children.Add(_recordsBorder);
                _built = true;
            }
            var none = s.Entries.Count == 0;
            var atEnd = s.Cursor >= s.Entries.Count - 1;
            var bar = new WrapPanel { Orientation = Orientation.Horizontal };
            bar.Children.Add(Btn("Open Log…", "Open more log files", OpenLogs));
            bar.Children.Add(new Border { Width = 8 });
            bar.Children.Add(IconBtn("first", "First record", _player.First, !none));
            bar.Children.Add(IconBtn("stepBack", "Previous record", () => _player.Step(-1), s.Cursor >= 0));
            bar.Children.Add(s.Playing
                ? Btn("⏸", "Pause", _player.Pause)
                : Btn("▶", "Play from here (from the start at the end)", _player.Play, !none));
            bar.Children.Add(IconBtn("stepOver", "Next record", () => _player.Step(1), !atEnd));
            var live = Btn("● Live", "Follow the last record as the files grow", _player.GoLive);
            if (s.Live)
            {
                live.Background = new SolidColorBrush(Color.FromRgb(0xe5, 0x14, 0x00));
                live.Foreground = Brushes.White;
            }
            bar.Children.Add(live);
            var speed = new ComboBox { ToolTip = "Playing speed", Margin = new Thickness(0, 0, 4, 0), MinWidth = 60 };
            speed.SetResourceReference(StyleProperty, VsResourceKeys.ComboBoxStyleKey);
            foreach (var sp in Speeds) speed.Items.Add($"{sp}×");
            speed.SelectedIndex = Math.Max(0, Array.IndexOf(Speeds, s.Speed));
            speed.SelectionChanged += (o, e) => _player.SetSpeed(Speeds[Math.Max(0, speed.SelectedIndex)]);
            bar.Children.Add(speed);
            bar.Children.Add(new TextBlock
            {
                Text = none ? "No records yet" : $"{s.Cursor + 1} / {s.Entries.Count}{(s.CursorTime.Length > 0 ? " · " + s.CursorTime : "")}",
                Foreground = th.BrushOf(th.Muted),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            });
            _bar.Content = bar;

            _updating = true;
            _slider.Maximum = Math.Max(s.Entries.Count - 1, 0);
            if (!_dragging) _slider.Value = s.Cursor;
            _slider.IsEnabled = !none;
            _updating = false;

            var traces = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            for (var i = 0; i < s.Traces.Count; i++)
            {
                var t = s.Traces[i];
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 16, 2), ToolTip = t.Path };
                row.Children.Add(Dot(Swatch(i)));
                row.Children.Add(new TextBlock { Text = t.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                var format = new ComboBox { ToolTip = "Format of the lines", Margin = new Thickness(0, 0, 6, 0) };
                format.SetResourceReference(StyleProperty, VsResourceKeys.ComboBoxStyleKey);
                foreach (var f in s.Formats) format.Items.Add(f.Name);
                format.SelectedItem = t.Format.Name;
                format.SelectionChanged += (o, e) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    if (format.SelectedItem is string name && name != t.Format.Name) _player.SetFormat(t, name);
                };
                row.Children.Add(format);
                if (t.NeedsMachine)
                {
                    row.Children.Add(Btn(t.MachineName.Length > 0 ? t.MachineName : "Choose machine…", "State machine of the lines that name none: click to choose it",
                        () =>
                        {
                            ThreadHelper.ThrowIfNotOnUIThread();
                            _player.AskMachine(t);
                        }, primary: t.MachineName.Length == 0));
                }
                row.Children.Add(new TextBlock
                {
                    Text = t.Error ?? $"{t.Records.Count} record{(t.Records.Count == 1 ? "" : "s")}",
                    Foreground = th.BrushOf(t.Error != null ? th.Error : th.Muted),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0),
                });
                var close = Btn("×", "Close this log", () => _player.Close(t));
                close.Background = Brushes.Transparent;
                close.BorderBrush = Brushes.Transparent;
                row.Children.Add(close);
                traces.Children.Add(row);
            }

            _traces.Content = traces;
            _problems.Children.Clear();
            foreach (var p in s.Problems)
            {
                _problems.Children.Add(new TextBlock { Text = "⚠ " + p, Foreground = th.BrushOf(th.Warning), TextWrapping = TextWrapping.Wrap });
            }
            _recordsBorder.BorderBrush = th.BrushOf(th.Border);
            RenderRows(s, th);
        }

        private void OpenLogs() => _player.OpenAsync().FileAndForget("FsmEditor/OpenTrace");

        private static readonly Dictionary<TraceKind, Color> KindColors = new Dictionary<TraceKind, Color>
        {
            [TraceKind.Event] = Color.FromRgb(0x1a, 0x85, 0xff),
            [TraceKind.Enter] = Color.FromRgb(0x38, 0x8a, 0x34),
            [TraceKind.Transition] = Color.FromRgb(0xd1, 0x86, 0x16),
        };

        private void RenderRows(TraceSession s, Theme th)
        {
            _rows.Children.Clear();
            var mono = new FontFamily("Consolas");
            FrameworkElement current = null;
            foreach (var r in s.Rows())
            {
                var grid = new Grid { Cursor = Cursors.Hand };
                foreach (var w in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
                void Cell(int col, UIElement e)
                {
                    Grid.SetColumn(e, col);
                    grid.Children.Add(e);
                }
                TextBlock Text(string text, Brush fg = null, FontWeight? weight = null) =>
                    new TextBlock { Text = text, FontFamily = mono, FontSize = 12, Foreground = fg, FontWeight = weight ?? FontWeights.Normal, Margin = new Thickness(0, 1, 8, 1) };
                Cell(0, Text(r.Time, th.BrushOf(th.Muted)));
                Cell(1, Dot(Swatch(r.Trace)));
                var machine = new StackPanel { Orientation = Orientation.Horizontal };
                if (r.Color >= 0) machine.Children.Add(Text("● ", new SolidColorBrush(DiagramCanvas.TraceColor(r.Color))));
                machine.Children.Add(Text(r.Machine));
                Cell(2, machine);
                Cell(3, Text(r.Kind.Text(), th.BrushOf(KindColors.TryGetValue(r.Kind, out var kc) ? kc : th.Muted), FontWeights.SemiBold));
                var what = Text(r.Name);
                what.TextWrapping = TextWrapping.Wrap;
                if (!r.Resolved)
                {
                    what.TextDecorations = TextDecorations.Strikethrough;
                    what.Opacity = 0.6;
                }
                Cell(4, what);
                var row = new Border { Child = grid, Padding = new Thickness(2, 0, 2, 0) };
                if (r.Index == s.Cursor)
                {
                    row.Background = th.BrushOf(th.Accent, 0.3);
                    current = row;
                }
                else if (r.Index > s.Cursor) row.Opacity = 0.55;
                var index = r.Index;
                row.MouseLeftButtonUp += (o, e) => _player.Seek(index);
                row.MouseEnter += (o, e) => { if (index != _player.Session.Cursor) row.Background = th.BrushOf(th.Hover, 0.6); };
                row.MouseLeave += (o, e) => { if (index != _player.Session.Cursor) row.Background = null; };
                _rows.Children.Add(row);
            }
            if (current != null && !_dragging) Dispatcher.BeginInvoke(new Action(() => current.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>Asks which machine the lines of a log naming none are about.</summary>
    internal static class MachinePicker
    {
        /// <summary>The index of the chosen machine, or -1 when cancelled.</summary>
        public static int Show(string title, string prompt, List<(string Name, string Where, bool Open)> machines, int selected)
        {
            var w = Services.Dialogs.Window(title, 460);
            var list = new ListBox { Height = 240, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var (name, where, open) in machines)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal };
                item.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) });
                item.Children.Add(new TextBlock { Text = where + (open ? "  (open)" : ""), Opacity = 0.7 });
                list.Items.Add(item);
            }
            list.SelectedIndex = selected >= 0 ? selected : 0;
            var ok = Services.Dialogs.Button("OK", isDefault: true);
            var cancel = Services.Dialogs.Button("Cancel", isCancel: true);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(Services.Dialogs.Label(prompt));
            panel.Children.Add(list);
            panel.Children.Add(Services.Dialogs.Buttons(ok, cancel));
            w.Content = panel;
            var result = -1;
            ok.Click += (s, e) =>
            {
                result = list.SelectedIndex;
                w.DialogResult = true;
            };
            list.MouseDoubleClick += (s, e) =>
            {
                if (list.SelectedIndex < 0) return;
                result = list.SelectedIndex;
                w.DialogResult = true;
            };
            w.Loaded += (s, e) => list.Focus();
            w.ShowModal();
            return result;
        }
    }
}
