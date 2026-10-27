using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace FsmEditor.Editor
{
    /// <summary>Where a <see cref="ToolPane"/> is shown, as for a Visual Studio tool window.</summary>
    internal enum PaneMode { Dock, AutoHide, Float }

    /// <summary>
    /// The toolbox or the properties panel, shown like a Visual Studio tool window:
    /// a title bar with Window Position (Float, Dock, Auto Hide, Hide), the Auto Hide
    /// pushpin and Close. <see cref="EditorControl"/> places it beside the diagram,
    /// over it (auto-hidden and slid out) or floating over it.
    /// </summary>
    internal sealed class ToolPane : Border
    {
        private readonly Border _head = new Border { Height = 24 };
        private readonly Action<ToolPane> _changed;
        private Point? _dragFrom;
        private Popup _menu;

        public ToolPane(string title, FrameworkElement content, Action<ToolPane> changed)
        {
            Title = title;
            _changed = changed;
            var body = new DockPanel();
            DockPanel.SetDock(_head, Dock.Top);
            body.Children.Add(_head);
            body.Children.Add(content);
            Child = body;
            IsKeyboardFocusWithinChanged += (s, e) => BuildHeader();
            _head.MouseLeftButtonDown += OnHeadDown;
            _head.MouseMove += OnHeadMove;
            _head.MouseLeftButtonUp += (s, e) =>
            {
                _dragFrom = null;
                _head.ReleaseMouseCapture();
            };
        }

        public string Title { get; }

        /// <summary>The Window Position menu is open (it is outside the pane, so the mouse may have left the pane).</summary>
        public bool MenuOpen => _menu?.IsOpen == true;

        public PaneMode Mode { get; private set; } = PaneMode.Dock;

        public bool Closed { get; private set; }

        /// <summary>An auto-hidden pane slid out over the diagram.</summary>
        public bool Slid { get; set; }

        /// <summary>Top-left corner of a floating pane in the diagram area, once placed.</summary>
        public Point? FloatAt { get; set; }

        public void Set(PaneMode mode, bool closed)
        {
            Mode = mode;
            Closed = closed;
            Slid = false;
            BuildHeader();
        }

        /// <summary>A change made in the title bar, which the editor saves and applies.</summary>
        private void Change(PaneMode mode, bool closed)
        {
            Set(mode, closed);
            _changed(this);
        }

        public void BuildHeader()
        {
            var th = Theme.Current;
            var active = IsKeyboardFocusWithin;
            var fg = active ? Colors.White : th.Foreground;
            var bg = active ? th.Accent : Theme.Mix(th.Panel, th.Foreground, 0.06);
            Background = th.BrushOf(th.Panel);
            BorderBrush = th.BrushOf(th.Border);
            var bar = new DockPanel();
            Button HeadButton(string icon, string tooltip, Action<Button> onClick)
            {
                var b = EditorControl.FlatButton(Icons.Create(icon, fg, bg, 14), tooltip);
                b.Padding = new Thickness(2);
                b.Margin = new Thickness(1, 0, 1, 0);
                b.Click += (s, e) => onClick(b);
                DockPanel.SetDock(b, Dock.Right);
                return b;
            }
            bar.Children.Add(HeadButton("close", "Close", b => Change(Mode, true)));
            if (Mode != PaneMode.Float)
            {
                var auto = Mode == PaneMode.AutoHide;
                bar.Children.Add(HeadButton(auto ? "pinOff" : "pin", auto ? "Dock" : "Auto Hide",
                    b => Change(auto ? PaneMode.Dock : PaneMode.AutoHide, false)));
            }
            bar.Children.Add(HeadButton("caret", "Window Position", ShowPositionMenu));
            bar.Children.Add(new TextBlock
            {
                Text = Title,
                FontSize = 12,
                Margin = new Thickness(8, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            _head.Child = bar;
            _head.Background = th.BrushOf(bg);
            TextElement.SetForeground(_head, th.BrushOf(fg));
            _head.Cursor = Mode == PaneMode.Float ? Cursors.SizeAll : null;
        }

        private void ShowPositionMenu(Button anchor)
        {
            var th = Theme.Current;
            var items = new StackPanel { Margin = new Thickness(2) };
            var popup = new Popup
            {
                PlacementTarget = anchor,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = new Border
                {
                    Child = items,
                    Background = th.BrushOf(th.Panel),
                    BorderBrush = th.BrushOf(th.Border),
                    BorderThickness = new Thickness(1),
                    MinWidth = 150,
                },
            };
            void Item(string label, bool check, PaneMode mode, bool closed)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock { Text = check ? "✓" : "", Width = 18, TextAlignment = TextAlignment.Center });
                row.Children.Add(new TextBlock { Text = label, Margin = new Thickness(4, 0, 12, 0) });
                var b = EditorControl.FlatButton(row, null);
                b.HorizontalContentAlignment = HorizontalAlignment.Left;
                b.Padding = new Thickness(4, 4, 4, 4);
                b.Click += (s, e) =>
                {
                    popup.IsOpen = false;
                    Change(mode, closed);
                };
                items.Children.Add(b);
            }
            Item("Float", Mode == PaneMode.Float, PaneMode.Float, false);
            Item("Dock", Mode == PaneMode.Dock, PaneMode.Dock, false);
            Item("Auto Hide", Mode == PaneMode.AutoHide, PaneMode.AutoHide, false);
            Item("Hide", false, Mode, true);
            _menu = popup;
            popup.IsOpen = true;
        }

        // Dragging a floating pane by its title bar (the buttons handle their own clicks).
        private void OnHeadDown(object sender, MouseButtonEventArgs e)
        {
            if (Mode != PaneMode.Float || !(Parent is Canvas)) return;
            _dragFrom = e.GetPosition(this);
            _head.CaptureMouse();
            e.Handled = true;
        }

        private void OnHeadMove(object sender, MouseEventArgs e)
        {
            if (_dragFrom == null || !(Parent is Canvas layer)) return;
            var p = e.GetPosition(layer);
            var x = Math.Max(0, Math.Min(layer.ActualWidth - ActualWidth, p.X - _dragFrom.Value.X));
            var y = Math.Max(0, Math.Min(layer.ActualHeight - 40, p.Y - _dragFrom.Value.Y));
            FloatAt = new Point(x, y);
            Canvas.SetLeft(this, x);
            Canvas.SetTop(this, y);
        }
    }
}
