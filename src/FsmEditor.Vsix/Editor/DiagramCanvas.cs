using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FsmEditor.Core;
using Geo = FsmEditor.Core.Geometry;

// WPF controls: Dispatcher.BeginInvoke only defers work on the UI thread (after the current input), it never switches threads.
#pragma warning disable VSTHRD001, VSTHRD110

namespace FsmEditor.Editor
{
    /// <summary>
    /// The diagram: draws the model with a pan/zoom view and turns mouse and
    /// keyboard input into editing operations of the <see cref="DiagramSession"/>.
    /// </summary>
    internal sealed class DiagramCanvas : FrameworkElement
    {
        public const string ToolDataFormat = "FsmEditor.Tool";

        private static readonly HashSet<VertexType> Resizable = new HashSet<VertexType> { VertexType.State, VertexType.Comment, VertexType.Fork, VertexType.Join };

        private readonly EditorControl _editor;
        private readonly List<(Rect Rect, string Id)> _labels = new List<(Rect, string)>();
        private DragState _drag;
        private bool _space;

        public DiagramCanvas(EditorControl editor)
        {
            _editor = editor;
            Geo.TextMeasure = LabelWidth;
            Focusable = true;
            FocusVisualStyle = null;
            AllowDrop = true;
            ClipToBounds = true;
            _hook = WndProc;
            PresentationSource.AddSourceChangedHandler(this, OnSourceChanged);
        }

        private DiagramSession S => _editor.Session;

        public double ViewX { get; private set; } = 40;
        public double ViewY { get; private set; } = 40;
        public double Zoom { get; private set; } = 1;

        /// <summary>The armed toolbox tool, or null for selection.</summary>
        public string Tool { get; private set; }
        public bool ToolSticky { get; private set; }

        public event EventHandler ViewChanged;
        public event EventHandler ToolChanged;

        // ---------------------------------------------------------------- view

        public Point ToScreen(double x, double y) => new Point(x * Zoom + ViewX, y * Zoom + ViewY);

        private PointD ToWorld(Point screen) => new PointD((screen.X - ViewX) / Zoom, (screen.Y - ViewY) / Zoom);

        private void SetView(double x, double y, double zoom)
        {
            ViewX = x;
            ViewY = y;
            Zoom = zoom;
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ZoomAt(double factor, double sx, double sy)
        {
            var z = Geo.Clamp(Zoom * factor, 0.15, 4);
            var k = z / Zoom;
            SetView(sx - (sx - ViewX) * k, sy - (sy - ViewY) * k, z);
        }

        public void ZoomCenter(double factor) => ZoomAt(factor, ActualWidth / 2, ActualHeight / 2);

        public void Fit()
        {
            if (S == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            S.Reindex();
            var b = Geo.ContentBounds(S.Index);
            const double pad = 40;
            var z = Geo.Clamp(Math.Min((ActualWidth - pad * 2) / Math.Max(b.W, 1), (ActualHeight - pad * 2) / Math.Max(b.H, 1)), 0.2, 1.5);
            SetView((ActualWidth - b.W * z) / 2 - b.X * z, (ActualHeight - b.H * z) / 2 - b.Y * z, z);
        }

        /// <summary>Centers the view on an element.</summary>
        public void Reveal(string id)
        {
            S.Reindex();
            PointD? p = S.Index.Vertex(id)?.Center;
            if (p == null && S.Index.Transition(id) is Transition t && Geo.Route(S.Index, t) is List<PointD> pts) p = Geo.PolylineMid(pts).Point;
            if (p == null) return;
            SetView(ActualWidth / 2 - p.Value.X * Zoom, ActualHeight / 2 - p.Value.Y * Zoom, Zoom);
        }

        // ---------------------------------------------------------------- tools

        public void SetTool(string id, bool sticky = false)
        {
            Tool = id != null && Tool == id && !sticky ? null : id;
            ToolSticky = Tool != null && sticky;
            Cursor = Tool != null ? Cursors.Cross : null;
            ToolChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            switch (Tool)
            {
                case "transition": _editor.Toast("Drag from a source to a target. Esc to cancel."); break;
                case "region": _editor.Toast("Click a state to add a region to it."); break;
                case "entryPoint":
                case "exitPoint": _editor.Toast("Click a state's border, or empty canvas for a connection point of the state machine itself."); break;
                case "connectionPointRef": _editor.Toast("Click the border of a submachine state."); break;
            }
        }

        private void ToolDone()
        {
            if (!ToolSticky) SetTool(null);
        }

        /// <summary>Places an element of a toolbox tool at a point of the view.</summary>
        public void CreateAt(string toolId, Point screen) => Create(toolId, ToWorld(screen));

        private void Create(string toolId, PointD p)
        {
            var v = S.CreateVertex(toolId, p);
            _editor.RenderProps();
            ToolDone();
            if (v != null && (v.Type == VertexType.State || v.Type == VertexType.Comment))
            {
                Dispatcher.BeginInvoke(new Action(() => _editor.StartInlineEdit(v.Id)));
            }
        }

        // ---------------------------------------------------------------- rendering

        private Theme T => Theme.Current;

        protected override void OnRender(DrawingContext dc)
        {
            var th = T;
            dc.DrawRectangle(th.BrushOf(th.Background), null, new Rect(RenderSize));
            DrawGrid(dc);
            if (S == null) return;
            S.Reindex();
            _editor.RefreshLayoutIssues();
            _labels.Clear();
            dc.PushTransform(new MatrixTransform(Zoom, 0, 0, Zoom, ViewX, ViewY));
            var ix = S.Index;
            var sorted = Geo.DrawingOrder(ix);
            foreach (var v in sorted) DrawVertex(dc, v);
            foreach (var v in sorted.Where(v => v.Type == VertexType.Comment)) DrawAnchors(dc, v);
            foreach (var t in S.Model.Transitions) DrawTransition(dc, t);
            DrawOverlay(dc);
            dc.Pop();
        }

        private void DrawGrid(DrawingContext dc)
        {
            var g = Geo.Grid * 2 * Zoom;
            if (g < 4) return;
            var tile = new DrawingGroup();
            tile.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, g, g))));
            tile.Children.Add(new GeometryDrawing(T.BrushOf(T.Grid), null, new EllipseGeometry(new Point(g / 2, g / 2), 1.1, 1.1)));
            var brush = new DrawingBrush(tile)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(Mod(ViewX, g), Mod(ViewY, g), g, g),
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, g, g),
                Stretch = Stretch.None,
            };
            dc.DrawRectangle(brush, null, new Rect(RenderSize));
        }

        private static double Mod(double a, double m) => a % m;

        private double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

        private FormattedText Text(string text, double size, FontWeight weight, FontStyle style, Brush brush) =>
            new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), style, weight, FontStretches.Normal), size, brush, PixelsPerDip);

        /// <summary>Draws text with its baseline at <paramref name="y"/>, like SVG text; returns its box.</summary>
        private Rect DrawText(DrawingContext dc, double x, double y, string text, double size, Brush brush,
            TextAnchor anchor = TextAnchor.Middle, FontWeight? weight = null, FontStyle? style = null, Color? halo = null)
        {
            if (string.IsNullOrEmpty(text)) return Rect.Empty;
            var ft = Text(text, size, weight ?? FontWeights.Normal, style ?? FontStyles.Normal, brush);
            var left = anchor == TextAnchor.Middle ? x - ft.WidthIncludingTrailingWhitespace / 2 : anchor == TextAnchor.End ? x - ft.WidthIncludingTrailingWhitespace : x;
            var origin = new Point(left, y - ft.Baseline);
            if (halo is Color h)
            {
                var geometry = ft.BuildGeometry(origin);
                dc.DrawGeometry(null, new Pen(T.BrushOf(h), 4) { LineJoin = PenLineJoin.Round }, geometry);
                dc.DrawGeometry(brush, null, geometry);
            }
            else
            {
                dc.DrawText(ft, origin);
            }
            return new Rect(origin, new Size(ft.WidthIncludingTrailingWhitespace, ft.Height));
        }

        /// <summary>Width of a transition label as drawn, for fitting the view.</summary>
        public double LabelWidth(string label) => Text(label, 11, FontWeights.Normal, FontStyles.Normal, Brushes.Black).WidthIncludingTrailingWhitespace;

        private enum IssueLevel { None, Warning, Error }

        private IssueLevel IssueOf(string id)
        {
            var list = _editor.IssuesFor(id);
            if (list.Any(i => i.Severity == Severity.Error)) return IssueLevel.Error;
            if (list.Any(i => i.Severity == Severity.Warning)) return IssueLevel.Warning;
            return IssueLevel.None;
        }

        private void DrawVertex(DrawingContext dc, Vertex v)
        {
            var th = T;
            var ix = S.Index;
            var selected = S.Selection.Contains(v.Id);
            var issue = IssueOf(v.Id);
            var dropTarget = _drag != null && _drag.DropState == v.Id;

            // Outline of the element's own shape: its color, then selection, warnings and errors, win in that order.
            var own = Theme.ColorOf(v.Color);
            var stroke = own ?? th.Line;
            double shapeWidth = 1.4;
            var fill = own ?? th.Line;
            if (selected)
            {
                stroke = th.Accent;
                shapeWidth = 2.4;
                fill = th.Accent;
            }
            if (issue == IssueLevel.Warning) stroke = th.Warning;
            if (issue == IssueLevel.Error)
            {
                stroke = th.Error;
                shapeWidth = 2;
                fill = th.Error;
            }
            var shapePen = dropTarget ? th.PenOf(th.Accent, 2.5, 1, new[] { 6.0, 3.0 }) : th.PenOf(stroke, shapeWidth);
            Pen StrokePen(double w) => th.PenOf(stroke, w);
            var fillBrush = th.BrushOf(fill);
            var shapeFill = th.BrushOf(own is Color oc ? th.Tint(oc, th.StateFill) : v.Type == VertexType.Comment ? th.NoteFill : th.StateFill);
            var textBrush = th.BrushOf(own ?? th.Foreground);
            var dimText = th.BrushOf(own ?? th.Foreground, 0.8);
            Brush ItemBrush(string color) => color != null && Theme.ColorOf(color) is Color ic ? th.BrushOf(ic) : textBrush;

            double x = v.X, y = v.Y, w = v.W, h = v.H;
            var cx = x + w / 2;
            var cy = y + h / 2;
            var r = Math.Min(w, h) / 2;
            var sepPen = th.PenOf(own ?? th.Line, 1, 0.8);
            switch (v.Type)
            {
                case VertexType.State:
                {
                    var lines = Geo.ActivityRowItems(S.Model, v);
                    var regions = Geo.RegionRects(S.Model, v);
                    var box = new Rect(x, y, w, h);
                    if (regions.Count > 0)
                    {
                        // Composite state: its color tints the header, its background color the regions.
                        var head = own is Color hc ? th.Tint(hc, th.StateFill) : th.StateFill;
                        var body = Theme.ColorOf(ElementColors.Background(v.ItemColors)) is Color bc ? th.Tint(bc, th.StateFill) : th.StateFill;
                        dc.DrawRoundedRectangle(th.BrushOf(head), null, box, 10, 10);
                        if (body != head)
                        {
                            var top = regions[0].Rect.Y;
                            dc.PushClip(new RectangleGeometry(new Rect(x, top, w, Math.Max(0, y + h - top))));
                            dc.DrawRoundedRectangle(th.BrushOf(body), null, box, 10, 10);
                            dc.Pop();
                        }
                        dc.DrawRoundedRectangle(null, shapePen, box, 10, 10);
                    }
                    else
                    {
                        dc.DrawRoundedRectangle(shapeFill, shapePen, box, 10, 10);
                    }
                    var title = v.Name + (v.Submachine.Length > 0 ? " : " + S.MachineLabel(v.Submachine) : "");
                    var centered = regions.Count == 0 && lines.Count == 0;
                    var stereo = v.Stereotype.Length > 0;
                    var ty = y + 16;
                    if (stereo)
                    {
                        DrawText(dc, cx, y + (centered ? h / 2 - 4 : 13), $"«{v.Stereotype}»", 10, dimText, style: FontStyles.Italic);
                        ty += 12;
                    }
                    if (centered)
                    {
                        DrawText(dc, cx, y + h / 2 + (stereo ? 10 : 4), title, 12, textBrush, weight: FontWeights.SemiBold);
                    }
                    else
                    {
                        DrawText(dc, cx, ty, title, 12, textBrush, weight: FontWeights.SemiBold);
                        var ly = y + Geo.NameHeight + (stereo ? 12 : 0);
                        if (lines.Count > 0)
                        {
                            dc.DrawLine(sepPen, new Point(x, ly), new Point(x + w, ly));
                            foreach (var l in lines)
                            {
                                ly += Geo.LineHeight;
                                DrawText(dc, x + 8, ly, l.Text, 11, ItemBrush(l.Color), TextAnchor.Start);
                            }
                        }
                    }
                    if (regions.Count > 0)
                    {
                        var top = regions[0].Rect.Y;
                        dc.DrawLine(sepPen, new Point(x, top), new Point(x + w, top));
                        var dashed = th.PenOf(own ?? th.Line, 1, 0.8, new[] { 7.0, 4.0 });
                        for (int i = 0; i < regions.Count; i++)
                        {
                            var rr = regions[i].Rect;
                            if (i > 0)
                            {
                                if (v.RegionLayout == RegionLayout.Horizontal) dc.DrawLine(dashed, new Point(rr.X, rr.Y), new Point(rr.X, rr.Y + rr.H));
                                else dc.DrawLine(dashed, new Point(rr.X, rr.Y), new Point(rr.X + rr.W, rr.Y));
                            }
                            if (regions[i].Name.Length > 0) DrawText(dc, rr.X + 6, rr.Y + 12, regions[i].Name, 10, dimText, TextAnchor.Start, style: FontStyles.Italic);
                            if (_drag != null && _drag.DropRegion == regions[i].Id)
                            {
                                dc.DrawRectangle(th.BrushOf(th.Accent, 0.08), null, new Rect(rr.X, rr.Y, rr.W, rr.H));
                            }
                        }
                    }
                    if (v.Submachine.Length > 0)
                    {
                        var gx = x + w - 26;
                        var gy = y + h - 14;
                        var glyph = StrokePen(1.2);
                        dc.DrawRoundedRectangle(null, glyph, new Rect(gx, gy, 8, 6), 2, 2);
                        dc.DrawRoundedRectangle(null, glyph, new Rect(gx + 13, gy, 8, 6), 2, 2);
                        dc.DrawLine(glyph, new Point(gx + 8, gy + 3), new Point(gx + 13, gy + 3));
                    }
                    if (v.Invariant.Length > 0) DrawText(dc, x + 4, y + h + 14, $"{{{v.Invariant}}}", 11, ItemBrush(ElementColors.Item(v.Color, v.ItemColors, "invariant")), TextAnchor.Start, style: FontStyles.Italic);
                    break;
                }
                case VertexType.Final:
                    dc.DrawEllipse(shapeFill, shapePen, new Point(cx, cy), r, r);
                    dc.DrawEllipse(fillBrush, null, new Point(cx, cy), Math.Max(2, r - 5), Math.Max(2, r - 5));
                    break;
                case VertexType.Initial:
                case VertexType.Junction:
                    dc.DrawEllipse(fillBrush, null, new Point(cx, cy), r, r);
                    break;
                case VertexType.ShallowHistory:
                case VertexType.DeepHistory:
                    dc.DrawEllipse(shapeFill, shapePen, new Point(cx, cy), r, r);
                    DrawText(dc, cx, cy + 4, v.Type == VertexType.DeepHistory ? "H*" : "H", 11, textBrush, weight: FontWeights.Bold);
                    break;
                case VertexType.Choice:
                {
                    var diamond = new StreamGeometry();
                    using (var ctx = diamond.Open())
                    {
                        ctx.BeginFigure(new Point(cx, y), true, true);
                        ctx.PolyLineTo(new[] { new Point(x + w, cy), new Point(cx, y + h), new Point(x, cy) }, true, true);
                    }
                    dc.DrawGeometry(shapeFill, shapePen, diamond);
                    break;
                }
                case VertexType.Fork:
                case VertexType.Join:
                    dc.DrawRoundedRectangle(fillBrush, null, new Rect(x, y, w, h), 1, 1);
                    break;
                case VertexType.ConnectionPointRef:
                case VertexType.EntryPoint:
                case VertexType.ExitPoint:
                    dc.DrawEllipse(shapeFill, shapePen, new Point(cx, cy), r, r);
                    if (v.Type == VertexType.ExitPoint || (v.Type == VertexType.ConnectionPointRef && v.PointKind == PointKind.Exit))
                    {
                        var k = r * 0.62;
                        var cross = StrokePen(1.3);
                        dc.DrawLine(cross, new Point(cx - k, cy - k), new Point(cx + k, cy + k));
                        dc.DrawLine(cross, new Point(cx + k, cy - k), new Point(cx - k, cy + k));
                    }
                    break;
                case VertexType.Terminate:
                {
                    var cross = StrokePen(2);
                    dc.DrawLine(cross, new Point(x, y), new Point(x + w, y + h));
                    dc.DrawLine(cross, new Point(x + w, y), new Point(x, y + h));
                    break;
                }
                case VertexType.Comment:
                {
                    const double f = 12;
                    var note = new StreamGeometry();
                    using (var ctx = note.Open())
                    {
                        ctx.BeginFigure(new Point(x, y), true, true);
                        ctx.PolyLineTo(new[] { new Point(x + w - f, y), new Point(x + w, y + f), new Point(x + w, y + h), new Point(x, y + h) }, true, true);
                    }
                    dc.DrawGeometry(shapeFill, shapePen, note);
                    var fold = StrokePen(1);
                    dc.DrawLine(fold, new Point(x + w - f, y), new Point(x + w - f, y + f));
                    dc.DrawLine(fold, new Point(x + w - f, y + f), new Point(x + w, y + f));
                    var lines = Geo.CommentLines(v.Text, w - 16, LabelWidth);
                    for (int i = 0; i < lines.Count; i++)
                    {
                        if (16 + i * 14 < h) DrawText(dc, x + 8, y + 17 + i * 14, lines[i], 11, textBrush, TextAnchor.Start);
                    }
                    break;
                }
            }
            var caption = v.Type == VertexType.ConnectionPointRef ? Or(S.PointName(ix.Vertex(v.Parent), v.Ref), "?") : v.Name;
            if (caption.Length > 0 && v.Type != VertexType.State && v.Type != VertexType.Comment)
            {
                var below = v.Type == VertexType.Fork || v.Type == VertexType.Join ? y + h + 13 : y + h + 12;
                DrawText(dc, cx, below, caption, 11, textBrush);
            }
        }

        private static string Or(string a, string b) => a.Length > 0 ? a : b;

        private void DrawTransition(DrawingContext dc, Transition t)
        {
            var ix = S.Index;
            if (!Geo.IsDrawn(ix, t)) return;
            var pts = Geo.Route(ix, t);
            if (pts == null) return;
            var th = T;
            var selected = S.Selection.Contains(t.Id);
            var issue = IssueOf(t.Id);
            var color = issue == IssueLevel.Error ? th.Error : issue == IssueLevel.Warning ? th.Warning : selected ? th.Accent : Theme.ColorOf(t.Color) ?? th.Line;
            var pen = th.PenOf(color, selected ? 2 : 1.3);
            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(P(pts[0]), false, false);
                ctx.PolyLineTo(pts.Skip(1).Select(P).ToList(), true, true);
            }
            dc.DrawGeometry(null, pen, line);
            var b = pts[pts.Count - 1];
            var (p1, p2) = SvgExport.ArrowHead(pts[pts.Count - 2], b);
            var arrow = new StreamGeometry();
            using (var ctx = arrow.Open())
            {
                ctx.BeginFigure(P(p1), false, false);
                ctx.PolyLineTo(new[] { P(b), P(p2) }, true, true);
            }
            dc.DrawGeometry(null, pen, arrow);
            var lines = Labels.TransitionLabelItems(S.Model, t);
            if (lines.Count > 0)
            {
                var lp = Geo.LabelPos(t, pts);
                var box = Rect.Empty;
                for (var i = 0; i < lines.Count; i++)
                {
                    var text = Theme.ColorOf(ElementColors.Item(t.Color, t.ItemColors, lines[i].Key)) ?? (selected ? th.Accent : th.Foreground);
                    box.Union(DrawText(dc, lp.X, Geo.LabelLineY(lp, i, lines.Count), lines[i].Text, 11, th.BrushOf(text), lp.Anchor, halo: th.Background));
                }
                _labels.Add((box, t.Id));
            }
            if (selected && S.Selection.Count == 1)
            {
                var wpPen = th.PenOf(th.Accent, 1.5);
                foreach (var p in WaypointHandles(t)) dc.DrawEllipse(th.BrushOf(th.Background), wpPen, P(p), 4.5, 4.5);
                // Its ends: dragged onto another element, they reconnect it.
                if (!(_drag?.Kind == DragKind.Reconnect && _drag.Transition == t))
                {
                    foreach (var p in new[] { pts[0], pts[pts.Count - 1] }) dc.DrawEllipse(th.BrushOf(th.Accent), th.PenOf(th.Background, 1), P(p), 4.5, 4.5);
                }
            }
        }

        /// <summary>The bend points to drag: the corners of a state's self-transition loop, or the transition's bend points.</summary>
        private IReadOnlyList<PointD> WaypointHandles(Transition t) =>
            Geo.IsLoop(S.Index, t) ? Geo.Loop(t, S.Index.Vertex(t.Source)).Corners() : t.Points;

        private static Point P(PointD p) => new Point(p.X, p.Y);

        private void DrawAnchors(DrawingContext dc, Vertex comment)
        {
            var pen = T.PenOf(T.Line, 1, 0.7, new[] { 4.0, 3.0 });
            foreach (var (from, to) in SvgExport.AnchorLines(S.Index, comment)) dc.DrawLine(pen, P(from), P(to));
        }

        private void DrawOverlay(DrawingContext dc)
        {
            var th = T;
            var accent = th.Accent;
            var selBox = th.PenOf(accent, 1, 1, new[] { 4.0, 3.0 });
            var sel = S.Selection.Select(S.Index.Vertex).Where(v => v != null).ToList();
            foreach (var v in sel) dc.DrawRoundedRectangle(null, selBox, new Rect(v.X - 4, v.Y - 4, v.W + 8, v.H + 8), 3, 3);
            if (sel.Count == 1 && S.Selection.Count == 1)
            {
                var v = sel[0];
                if (Resizable.Contains(v.Type))
                {
                    foreach (var corner in Corners) dc.DrawRoundedRectangle(th.BrushOf(accent), th.PenOf(th.Background, 1), ResizeHandle(v, corner), 1.5, 1.5);
                }
                if (v.Type != VertexType.Final && v.Type != VertexType.Terminate)
                {
                    var c = ConnectHandle(v);
                    var pen = th.PenOf(accent, 1.5);
                    dc.DrawEllipse(th.BrushOf(th.Background), pen, c, 7, 7);
                    dc.DrawLine(pen, new Point(c.X - 3, c.Y), new Point(c.X + 3, c.Y));
                    dc.DrawLine(pen, new Point(c.X + 0.5, c.Y - 2.5), new Point(c.X + 3, c.Y));
                    dc.DrawLine(pen, new Point(c.X + 3, c.Y), new Point(c.X + 0.5, c.Y + 2.5));
                }
            }
            if (_drag?.Kind == DragKind.Connect && _drag.Cur is PointD cur)
            {
                var target = _drag.Hover != null ? S.Index.Vertex(_drag.Hover) : null;
                var from = Geo.Clip(_drag.Source, target != null ? target.Center : cur);
                var to = target != null ? Geo.Clip(target, from) : cur;
                dc.DrawLine(th.PenOf(accent, 1.5, 1, new[] { 5.0, 3.0 }), P(from), P(to));
            }
            if (_drag?.Kind == DragKind.Reconnect && _drag.Cur is PointD end)
            {
                // From the bend point next to the dragged end, or from the element at the other end.
                var t = _drag.Transition;
                var pts = t.Points.Count > 0 && !Geo.IsLoop(S.Index, t) ? t.Points : new List<PointD>();
                var target = _drag.Hover != null ? S.Index.Vertex(_drag.Hover) : null;
                var toward = target != null ? target.Center : end;
                PointD? near = pts.Count == 0 ? (PointD?)null : _drag.AtSource ? pts[0] : pts[pts.Count - 1];
                var fixedEnd = S.Index.Vertex(_drag.AtSource ? t.Target : t.Source);
                var from = near ?? (fixedEnd != null ? Geo.Clip(fixedEnd, toward) : toward);
                var to = target != null ? Geo.Clip(target, from) : end;
                dc.DrawLine(th.PenOf(accent, 1.5, 1, new[] { 5.0, 3.0 }), P(from), P(to));
            }
            if (_drag?.Kind == DragKind.Band && _drag.Cur is PointD bandEnd)
            {
                var r = RectD.FromPoints(_drag.Start, bandEnd);
                dc.DrawRectangle(th.BrushOf(accent, 0.08), th.PenOf(accent, 1), new Rect(r.X, r.Y, r.W, r.H));
            }
        }

        private static readonly string[] Corners = { "nw", "ne", "sw", "se" };

        private static Rect ResizeHandle(Vertex v, string corner) =>
            new Rect(corner.Contains('w') ? v.X - 8 : v.X + v.W - 1, corner.Contains('n') ? v.Y - 8 : v.Y + v.H - 1, 9, 9);

        private static Point ConnectHandle(Vertex v) => new Point(v.X + v.W + 18, v.Y + v.H / 2);

        // ---------------------------------------------------------------- hit testing

        private enum HitKind { Empty, Handle, Waypoint, End, Label, Vertex, Transition }

        private struct Hit
        {
            public HitKind Kind;
            public string Id;
            public string Handle;
            public int Index;
            /// <summary>For an end: its source end rather than its target end.</summary>
            public bool AtSource;
        }

        private Hit HitTest(PointD p)
        {
            var ix = S.Index;
            var tolerance = 2 / Zoom;
            if (S.Selection.Count == 1)
            {
                var only = ix.Vertex(S.Selection.First());
                if (only != null)
                {
                    var c = ConnectHandle(only);
                    if (only.Type != VertexType.Final && only.Type != VertexType.Terminate && Geo.Dist(p, new PointD(c.X, c.Y)) <= 7 + tolerance)
                    {
                        return new Hit { Kind = HitKind.Handle, Handle = "connect" };
                    }
                    if (Resizable.Contains(only.Type))
                    {
                        foreach (var corner in Corners)
                        {
                            var rh = ResizeHandle(only, corner);
                            rh.Inflate(tolerance, tolerance);
                            if (rh.Contains(P(p))) return new Hit { Kind = HitKind.Handle, Handle = "resize-" + corner };
                        }
                    }
                }
                var selected = ix.Transition(S.Selection.First());
                if (selected != null && Geo.IsDrawn(ix, selected) && Geo.Route(ix, selected) is List<PointD> route)
                {
                    if (Geo.Dist(p, route[route.Count - 1]) <= 4.5 + tolerance) return new Hit { Kind = HitKind.End, Id = selected.Id };
                    if (Geo.Dist(p, route[0]) <= 4.5 + tolerance) return new Hit { Kind = HitKind.End, Id = selected.Id, AtSource = true };
                }
                if (selected != null)
                {
                    var handles = WaypointHandles(selected);
                    for (int i = 0; i < handles.Count; i++)
                    {
                        if (Geo.Dist(p, handles[i]) <= 4.5 + tolerance) return new Hit { Kind = HitKind.Waypoint, Id = selected.Id, Index = i };
                    }
                }
            }
            // The lines between regions: dragged, they resize the region before them.
            for (int k = ix.Model.Vertices.Count - 1; k >= 0; k--)
            {
                var s = ix.Model.Vertices[k];
                var rects = Geo.RegionRects(S.Model, s);
                for (int i = 0; i < rects.Count - 1; i++)
                {
                    var r = rects[i].Rect;
                    var near = s.RegionLayout == RegionLayout.Horizontal
                        ? Math.Abs(p.X - r.Right) <= 3 + tolerance && p.Y >= r.Y && p.Y <= r.Bottom
                        : Math.Abs(p.Y - r.Bottom) <= 3 + tolerance && p.X >= r.X && p.X <= r.Right;
                    if (near) return new Hit { Kind = HitKind.Handle, Handle = "sep", Id = s.Id, Index = i };
                }
            }
            var item = ItemAt(p, true);
            if (item.Kind != HitKind.Empty) return item;
            return new Hit { Kind = HitKind.Empty };
        }

        /// <summary>The topmost transition (line or label) or vertex at <paramref name="p"/>.</summary>
        private Hit ItemAt(PointD p, bool labels)
        {
            var ix = S.Index;
            var transitions = S.Model.Transitions;
            for (int i = transitions.Count - 1; i >= 0; i--)
            {
                var t = transitions[i];
                if (labels && _labels.Any(l => l.Id == t.Id && l.Rect.Contains(P(p)))) return new Hit { Kind = HitKind.Label, Id = t.Id };
                if (!Geo.IsDrawn(ix, t)) continue;
                var pts = Geo.Route(ix, t);
                if (pts == null) continue;
                for (int k = 0; k < pts.Count - 1; k++)
                {
                    if (Geo.DistToSegment(p, pts[k], pts[k + 1]) <= 6) return new Hit { Kind = HitKind.Transition, Id = t.Id };
                }
            }
            var sorted = Geo.DrawingOrder(ix);
            for (int i = sorted.Count - 1; i >= 0; i--)
            {
                if (Contains(sorted[i], p)) return new Hit { Kind = HitKind.Vertex, Id = sorted[i].Id };
            }
            return new Hit { Kind = HitKind.Empty };
        }

        private static bool Contains(Vertex v, PointD p)
        {
            double x = v.X, y = v.Y, w = v.W, h = v.H;
            var cx = x + w / 2;
            var cy = y + h / 2;
            var bar = v.Type == VertexType.Fork || v.Type == VertexType.Join;
            // Small elements get a bigger target.
            if (!bar && Math.Min(w, h) < 20 && Math.Abs(p.X - cx) <= 11 && Math.Abs(p.Y - cy) <= 11) return true;
            if (bar && Math.Min(w, h) < 14 && new RectD(x - 4, y - 5, w + 8, h + 10).Contains(p)) return true;
            switch (v.Type)
            {
                case VertexType.State:
                case VertexType.Comment:
                case VertexType.Fork:
                case VertexType.Join:
                case VertexType.Terminate:
                    return new RectD(x, y, w, h).Contains(p);
                case VertexType.Choice:
                    return Math.Abs(p.X - cx) / (w / 2) + Math.Abs(p.Y - cy) / (h / 2) <= 1;
                default:
                    return Geo.Dist(p, new PointD(cx, cy)) <= Math.Min(w, h) / 2;
            }
        }

        /// <summary>The element under the pointer to connect to: a vertex, or any element for a comment.</summary>
        private string ConnectTargetAt(PointD p, Vertex source)
        {
            var hit = ItemAt(p, true);
            if (hit.Kind == HitKind.Vertex) return hit.Id;
            if ((hit.Kind == HitKind.Transition || hit.Kind == HitKind.Label) && source.Type == VertexType.Comment) return hit.Id;
            return null;
        }

        /// <summary>The topmost state or pseudostate at <paramref name="p"/>, through transitions and comments.</summary>
        private string ReconnectTargetAt(PointD p)
        {
            var sorted = Geo.DrawingOrder(S.Index);
            for (int i = sorted.Count - 1; i >= 0; i--)
            {
                if (sorted[i].Type != VertexType.Comment && Contains(sorted[i], p)) return sorted[i].Id;
            }
            return null;
        }

        // ---------------------------------------------------------------- mouse

        private enum DragKind { Pan, Move, Resize, Region, Connect, Waypoint, Reconnect, Label, Band }

        private sealed class DragState
        {
            public DragKind Kind;
            public PointD Start;
            public Point ScreenStart;
            public double ViewX0, ViewY0;
            public List<(Vertex V, double X0, double Y0)> Items;
            public List<(Transition T, List<PointD> Points0)> Transitions;
            public List<Vertex> Roots;
            public HashSet<string> Moving;
            public bool Moved;
            public string ClickedId;
            public string DropRegion;
            public string DropState;
            public Vertex Vertex;
            public double X0, Y0, W0, H0;
            public string Corner;
            public Vertex Source;
            public PointD? Cur;
            public string Hover;
            public Transition Transition;
            public int Index;
            public bool AtSource;
            public PointD Offset0;
            public bool Additive;
            public HashSet<string> Base;
            /// <summary>A right-button pan, which opens the context menu when the pointer doesn't move.</summary>
            public bool RightButton;
            /// <summary>Move, resize and region drags: the layout at the start, restored at each step.</summary>
            public DiagramSession.LayoutSnapshot Snapshot;
            /// <summary>Move: the boxes of the roots at the start.</summary>
            public Dictionary<string, RectD> Boxes;
        }

        private static bool Additive => (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0;
        private static bool Alt => (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (S == null) return;
            Focus();
            _editor.CommitInlineEdit();
            _editor.HideProblems();
            var screen = e.GetPosition(this);
            var p = ToWorld(screen);
            e.Handled = true;

            if (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Right || _space)
            {
                _drag = new DragState { Kind = DragKind.Pan, ScreenStart = screen, ViewX0 = ViewX, ViewY0 = ViewY, Start = p, RightButton = e.ChangedButton == MouseButton.Right && !_space };
                Cursor = Cursors.ScrollAll;
                CaptureMouse();
                return;
            }
            if (e.ChangedButton != MouseButton.Left) return;
            if (e.ClickCount == 2)
            {
                _drag = null;
                DoubleClick(p);
                return;
            }
            S.Reindex();
            var hit = HitTest(p);

            if (Tool != null)
            {
                var def = Core.Tools.ById(Tool);
                if (Tool == "transition")
                {
                    if (hit.Kind == HitKind.Vertex) StartConnect(S.Index.Vertex(hit.Id), p);
                    else _editor.Toast("Start the transition on a state or pseudostate.");
                }
                else if (Tool == "region")
                {
                    var s = hit.Kind == HitKind.Vertex ? S.Index.Vertex(hit.Id) : Geo.StateAt(S.Index, p, new HashSet<string>());
                    if (s != null && s.Type == VertexType.State)
                    {
                        if (s.Submachine.Length > 0) _editor.Toast("A submachine state cannot own regions.");
                        else
                        {
                            S.AddRegion(s);
                            _editor.RenderProps();
                        }
                        ToolDone();
                    }
                    else
                    {
                        _editor.Toast("Click a state to add a region to it.");
                    }
                }
                else if (def?.Create != null)
                {
                    Create(Tool, p);
                }
                if (_drag != null) CaptureMouse();
                InvalidateVisual();
                return;
            }

            // A composite state moves by its title bar: its regions are empty canvas, for a selection band.
            if (hit.Kind == HitKind.Vertex && Geo.RegionRects(S.Model, S.Index.Vertex(hit.Id)).Any(r => r.Rect.Contains(p))) hit = new Hit { Kind = HitKind.Empty };

            switch (hit.Kind)
            {
                case HitKind.Handle:
                {
                    if (hit.Handle == "sep")
                    {
                        var s = S.Index.Vertex(hit.Id);
                        _drag = new DragState { Kind = DragKind.Region, Vertex = s, Index = hit.Index, Start = p, Snapshot = S.BeginLayoutDrag(s) };
                        break;
                    }
                    var v = S.Index.Vertex(S.Selection.First());
                    if (v == null) break;
                    if (hit.Handle.StartsWith("resize-")) _drag = new DragState { Kind = DragKind.Resize, Corner = hit.Handle.Substring(7), Vertex = v, Start = p, X0 = v.X, Y0 = v.Y, W0 = v.W, H0 = v.H, Snapshot = S.BeginLayoutDrag(v) };
                    else StartConnect(v, p);
                    break;
                }
                case HitKind.Waypoint:
                    _drag = new DragState { Kind = DragKind.Waypoint, Transition = S.Index.Transition(hit.Id), Index = hit.Index };
                    break;
                case HitKind.End:
                    _drag = new DragState { Kind = DragKind.Reconnect, Transition = S.Index.Transition(hit.Id), AtSource = hit.AtSource, Cur = p };
                    break;
                case HitKind.Label:
                {
                    var t = S.Index.Transition(hit.Id);
                    S.Selection = new HashSet<string> { t.Id };
                    _drag = new DragState { Kind = DragKind.Label, Transition = t, Start = p, Offset0 = t.LabelOffset ?? new PointD(0, 0) };
                    _editor.RenderProps();
                    break;
                }
                case HitKind.Vertex:
                case HitKind.Transition:
                {
                    var id = hit.Id;
                    var clickedSelected = false;
                    if (Additive)
                    {
                        if (!S.Selection.Remove(id)) S.Selection.Add(id);
                    }
                    else if (!S.Selection.Contains(id))
                    {
                        S.Selection = new HashSet<string> { id };
                    }
                    else
                    {
                        clickedSelected = true;
                    }
                    _editor.RenderProps();
                    if (hit.Kind == HitKind.Vertex && S.Selection.Contains(id)) StartMove(p, clickedSelected ? id : null);
                    break;
                }
                default:
                    if (!Additive) S.Selection.Clear();
                    _drag = new DragState { Kind = DragKind.Band, Start = p, Additive = Additive, Base = new HashSet<string>(S.Selection) };
                    _editor.RenderProps();
                    break;
            }
            if (_drag != null) CaptureMouse();
            InvalidateVisual();
            _editor.RenderStatus();
        }

        private void StartConnect(Vertex source, PointD p) =>
            _drag = new DragState { Kind = DragKind.Connect, Source = source, Cur = p };

        private void StartMove(PointD p, string clickedId)
        {
            var ix = S.Index;
            var moving = new Dictionary<string, Vertex>();
            var roots = new List<Vertex>();
            foreach (var id in S.Selection)
            {
                var v = ix.Vertex(id);
                if (v == null || ix.Ancestors(v).Any(a => S.Selection.Contains(a.Id))) continue;
                roots.Add(v);
                moving[v.Id] = v;
                foreach (var d in ix.Descendants(v)) moving[d.Id] = d;
            }
            _drag = new DragState
            {
                Kind = DragKind.Move,
                Start = p,
                Items = moving.Values.Select(v => (v, v.X, v.Y)).ToList(),
                Transitions = S.Model.Transitions
                    .Where(t => t.Points.Count > 0 && moving.ContainsKey(t.Source) && moving.ContainsKey(t.Target))
                    .Select(t => (t, t.Points.ToList()))
                    .ToList(),
                Roots = roots,
                Moving = new HashSet<string>(moving.Keys),
                ClickedId = clickedId,
                Snapshot = S.Snapshot(),
                Boxes = roots.ToDictionary(v => v.Id, v => new RectD(v.X, v.Y, v.W, v.H)),
            };
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (S == null) return;
            var screen = e.GetPosition(this);
            var p = ToWorld(screen);
            var d = _drag;
            if (d == null)
            {
                UpdateHoverCursor(p);
                return;
            }
            switch (d.Kind)
            {
                case DragKind.Pan:
                    if (!d.Moved && (screen - d.ScreenStart).Length < 3) return;
                    d.Moved = true;
                    SetView(d.ViewX0 + screen.X - d.ScreenStart.X, d.ViewY0 + screen.Y - d.ScreenStart.Y, Zoom);
                    return;
                case DragKind.Move:
                {
                    var dx = p.X - d.Start.X;
                    var dy = p.Y - d.Start.Y;
                    if (!d.Moved && Math.Sqrt(dx * dx + dy * dy) * Zoom < 3) return;
                    d.Moved = true;
                    var ix = S.Index;
                    var lead = d.Roots.FirstOrDefault();
                    if (lead != null && !Alt && !ix.IsBorderVertex(lead))
                    {
                        var it = d.Items.First(i => i.V == lead);
                        dx = Geo.Snap(it.X0 + dx) - it.X0;
                        dy = Geo.Snap(it.Y0 + dy) - it.Y0;
                    }
                    // From the layout at the start: the regions grown for the previous position shrink back.
                    S.MoveFrom(d.Snapshot, d.Items, d.Transitions, d.Roots, d.Boxes, dx, dy, p, Alt);
                    var rr = lead != null && d.Roots.All(r => !ix.IsBorderVertex(r)) ? S.DropHighlight(lead, d.Moving) : null;
                    d.DropRegion = rr?.Id;
                    d.DropState = rr != null && ix.RegionOwner.TryGetValue(rr.Id, out var owner) ? owner?.Id : null;
                    break;
                }
                case DragKind.Resize:
                    S.ResizeFrom(d.Snapshot, d.Vertex, d.Corner, d.X0, d.Y0, d.W0, d.H0, p.X - d.Start.X, p.Y - d.Start.Y, Alt);
                    break;
                case DragKind.Region:
                    // The line after region Index: the region resizes, its state with it, and the later regions move.
                    S.ResizeRegionFrom(d.Snapshot, d.Vertex, d.Index, d.Vertex.RegionLayout == RegionLayout.Horizontal ? p.X - d.Start.X : p.Y - d.Start.Y, Alt);
                    break;
                case DragKind.Connect:
                    d.Cur = p;
                    d.Hover = ConnectTargetAt(p, d.Source);
                    if (d.Hover != null && S.Index.Vertex(d.Hover) == null) d.Hover = null;
                    break;
                case DragKind.Reconnect:
                    d.Cur = p;
                    d.Hover = ReconnectTargetAt(p);
                    break;
                case DragKind.Waypoint:
                {
                    var q = Alt ? p : new PointD(Geo.Snap(p.X), Geo.Snap(p.Y));
                    if (Geo.IsLoop(S.Index, d.Transition)) S.MoveLoopCorner(d.Transition, d.Index, q);
                    else d.Transition.Points[d.Index] = q;
                    break;
                }
                case DragKind.Label:
                    d.Moved = true;
                    d.Transition.LabelOffset = new PointD(Num.Round(d.Offset0.X + p.X - d.Start.X), Num.Round(d.Offset0.Y + p.Y - d.Start.Y));
                    break;
                case DragKind.Band:
                {
                    d.Cur = p;
                    var r = RectD.FromPoints(d.Start, p);
                    var next = new HashSet<string>(d.Additive ? d.Base : Enumerable.Empty<string>());
                    foreach (var v in S.Model.Vertices)
                    {
                        if (v.X >= r.X && v.Y >= r.Y && v.X + v.W <= r.X + r.W && v.Y + v.H <= r.Y + r.H) next.Add(v.Id);
                    }
                    foreach (var t in S.Model.Transitions)
                    {
                        if (next.Contains(t.Source) && next.Contains(t.Target)) next.Add(t.Id);
                    }
                    S.Selection = next;
                    _editor.RenderStatus();
                    break;
                }
            }
            InvalidateVisual();
        }

        private void UpdateHoverCursor(PointD p)
        {
            if (Tool != null)
            {
                Cursor = Cursors.Cross;
                return;
            }
            if (_space)
            {
                Cursor = Cursors.Hand;
                return;
            }
            var hit = HitTest(p);
            switch (hit.Kind)
            {
                case HitKind.Handle when hit.Handle == "sep":
                    Cursor = S.Index.Vertex(hit.Id)?.RegionLayout == RegionLayout.Horizontal ? Cursors.SizeWE : Cursors.SizeNS;
                    break;
                case HitKind.Handle: Cursor = hit.Handle == "resize-nw" || hit.Handle == "resize-se" ? Cursors.SizeNWSE : hit.Handle.StartsWith("resize-") ? Cursors.SizeNESW : Cursors.Cross; break;
                case HitKind.End: Cursor = Cursors.Cross; break;
                case HitKind.Waypoint:
                case HitKind.Label: Cursor = Cursors.SizeAll; break;
                default: Cursor = null; break;
            }
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            var d = _drag;
            if (d == null || S == null) return;
            _drag = null;
            if (IsMouseCaptured) ReleaseMouseCapture();
            Cursor = Tool != null ? Cursors.Cross : null;
            switch (d.Kind)
            {
                case DragKind.Pan:
                    if (d.RightButton && !d.Moved) ShowContextMenu(d.Start);
                    InvalidateVisual();
                    break;
                case DragKind.Move:
                    if (!d.Moved)
                    {
                        if (d.ClickedId != null)
                        {
                            S.Selection = new HashSet<string> { d.ClickedId };
                            _editor.RenderProps();
                        }
                        InvalidateVisual();
                        break;
                    }
                    S.DropMoved(d.Roots, d.Moving);
                    _editor.RenderProps();
                    break;
                case DragKind.Resize:
                case DragKind.Region:
                case DragKind.Waypoint:
                    S.Commit();
                    break;
                case DragKind.Label:
                    if (d.Moved) S.Commit();
                    else InvalidateVisual();
                    break;
                case DragKind.Connect:
                {
                    var target = ConnectTargetAt(ToWorld(e.GetPosition(this)), d.Source);
                    if (target != null)
                    {
                        var t = S.Connect(d.Source, target);
                        _editor.RenderProps();
                        if (t != null && d.Source.Type == VertexType.State)
                        {
                            Dispatcher.BeginInvoke(new Action(() => _editor.StartInlineEdit(t.Id)));
                        }
                    }
                    InvalidateVisual();
                    ToolDone();
                    break;
                }
                case DragKind.Reconnect:
                {
                    var target = ReconnectTargetAt(ToWorld(e.GetPosition(this)));
                    if (target != null)
                    {
                        S.Reconnect(d.Transition, d.AtSource, S.Index.Vertex(target));
                        _editor.RenderProps();
                    }
                    InvalidateVisual();
                    break;
                }
                case DragKind.Band:
                    _editor.RenderProps();
                    InvalidateVisual();
                    break;
            }
            _editor.RenderStatus();
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            if (_drag?.Kind == DragKind.Pan)
            {
                _drag = null;
                Cursor = null;
            }
        }

        private void DoubleClick(PointD p)
        {
            if (Tool != null) return;
            S.Reindex();
            var hit = HitTest(p);
            switch (hit.Kind)
            {
                case HitKind.Waypoint:
                    S.RemoveWaypoint(S.Index.Transition(hit.Id), hit.Index);
                    break;
                case HitKind.Label:
                    _editor.StartInlineEdit(hit.Id);
                    break;
                case HitKind.Transition:
                    S.AddWaypoint(S.Index.Transition(hit.Id), p);
                    _editor.RenderProps();
                    break;
                case HitKind.Vertex:
                {
                    var v = S.Index.Vertex(hit.Id);
                    if (v.Type == VertexType.State && v.Submachine.Length > 0 && Alt) _editor.OpenSubmachine(v.Submachine);
                    else _editor.StartInlineEdit(v.Id);
                    break;
                }
                case HitKind.Empty:
                    Create("state", p);
                    break;
            }
            InvalidateVisual();
        }

        // A horizontal scroll pans by about 100 pixels per wheel notch (120).
        private const double HorizontalPixels = 100.0 / 120;
        private const int WmMouseHWheel = 0x020E;
        private readonly HwndSourceHook _hook;

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            e.Handled = true;
            var pos = e.GetPosition(this);
            // The wheel zooms around the pointer; Shift+wheel pans sideways.
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) SetView(ViewX + e.Delta * 0.5, ViewY, Zoom);
            else ZoomAt(Math.Exp(e.Delta * 0.0018), pos.X, pos.Y);
        }

        // WPF has no horizontal wheel event: WM_MOUSEHWHEEL (trackpad, tilt wheel) goes to the
        // window, so the canvas hooks the window it is shown in.
        private void OnSourceChanged(object sender, SourceChangedEventArgs e)
        {
            (e.OldSource as HwndSource)?.RemoveHook(_hook);
            (e.NewSource as HwndSource)?.AddHook(_hook);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WmMouseHWheel || S == null || !IsVisible) return IntPtr.Zero;
            var lp = lParam.ToInt64();
            var p = PointFromScreen(new Point(unchecked((short)(lp & 0xFFFF)), unchecked((short)((lp >> 16) & 0xFFFF))));
            if (p.X < 0 || p.Y < 0 || p.X > ActualWidth || p.Y > ActualHeight) return IntPtr.Zero;
            var delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
            SetView(ViewX - delta * HorizontalPixels, ViewY, Zoom);
            handled = true;
            return IntPtr.Zero;
        }

        /// <summary>
        /// The menu of a right-click on an element or on empty canvas. A right-click
        /// on an element selects it unless it is already selected.
        /// </summary>
        private void ShowContextMenu(PointD p)
        {
            if (Tool != null) return;
            S.Reindex();
            var at = Mouse.GetPosition(this);
            var hit = HitTest(p);
            var id = hit.Kind == HitKind.Vertex || hit.Kind == HitKind.Transition || hit.Kind == HitKind.Label || hit.Kind == HitKind.Waypoint || hit.Kind == HitKind.End ? hit.Id
                : hit.Kind == HitKind.Handle && S.Selection.Count == 1 ? S.Selection.First()
                : null;
            if (id != null && !S.Selection.Contains(id))
            {
                S.Selection = new HashSet<string> { id };
                _editor.RenderProps();
                _editor.RenderStatus();
                InvalidateVisual();
            }
            var th = Theme.Current;
            var items = new StackPanel { Margin = new Thickness(2) };
            var popup = new Popup
            {
                PlacementTarget = this,
                Placement = PlacementMode.MousePoint,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = new Border
                {
                    Child = items,
                    Background = th.BrushOf(th.Panel),
                    BorderBrush = th.BrushOf(th.Border),
                    BorderThickness = new Thickness(1),
                    MinWidth = 160,
                },
            };
            Border Separator() => new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4), Background = th.BrushOf(th.Border) };
            void Item(string label, Action onClick, bool enabled = true)
            {
                var b = EditorControl.FlatButton(new TextBlock { Text = label, Margin = new Thickness(4, 0, 12, 0) }, null);
                b.HorizontalContentAlignment = HorizontalAlignment.Left;
                b.Padding = new Thickness(4);
                b.IsEnabled = enabled;
                if (!enabled) b.Opacity = 0.45;
                b.Click += (s, e) =>
                {
                    popup.IsOpen = false;
                    onClick();
                };
                items.Children.Add(b);
            }
            var state = id != null ? S.Index.Vertex(id) : null;
            if (state != null && state.Type == VertexType.State)
            {
                var canAlign = S.SelectionRoots().Count(v => v.Type == VertexType.State) >= 2;
                void Align(bool horizontally)
                {
                    if (S.Align(state.Id, horizontally)) _editor.RenderProps();
                    InvalidateVisual();
                }
                Item("Align Horizontally", () => Align(true), canAlign);
                Item("Align Vertically", () => Align(false), canAlign);
                items.Children.Add(Separator());
            }
            if (S.CanLeaveParent)
            {
                Item("Leave Parent", () =>
                {
                    S.LeaveParent();
                    _editor.RenderProps();
                    InvalidateVisual();
                });
                items.Children.Add(Separator());
            }
            var copyable = S.CollectSelection() != null;
            Item("Cut", () => _editor.Copy(true), copyable);
            Item("Copy", () => _editor.Copy(false), copyable);
            Item("Paste", () =>
            {
                _editor.PasteAt(p);
                InvalidateVisual();
            }, _editor.ClipboardHoldsElements);
            items.Children.Add(Separator());
            if (id != null)
            {
                Item("Delete", () =>
                {
                    _editor.DeleteSelection();
                    InvalidateVisual();
                });
                items.Children.Add(Separator());
            }
            Item("Properties…", () => _editor.OpenPropertiesWindow(id, at));
            popup.IsOpen = true;
        }

        protected override void OnContextMenuOpening(System.Windows.Controls.ContextMenuEventArgs e) => e.Handled = true;

        // ---------------------------------------------------------------- toolbox drag and drop

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            e.Effects = e.Data.GetDataPresent(ToolDataFormat) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        protected override void OnDrop(DragEventArgs e)
        {
            base.OnDrop(e);
            if (S == null || !(e.Data.GetData(ToolDataFormat) is string id)) return;
            e.Handled = true;
            Focus();
            CreateAt(id, e.GetPosition(this));
        }

        // ---------------------------------------------------------------- keyboard

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (S == null) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var mods = Keyboard.Modifiers;
            if (key == Key.Space)
            {
                if (!_space)
                {
                    _space = true;
                    Cursor = Cursors.Hand;
                }
                e.Handled = true;
                return;
            }
            if ((mods & ModifierKeys.Control) != 0)
            {
                switch (key)
                {
                    case Key.A: _editor.SelectAll(); break;
                    case Key.D: _editor.Duplicate(); break;
                    case Key.OemPlus:
                    case Key.Add: ZoomCenter(1.2); break;
                    case Key.OemMinus:
                    case Key.Subtract: ZoomCenter(1 / 1.2); break;
                    case Key.D0:
                    case Key.NumPad0: Fit(); break;
                    default: return;
                }
                e.Handled = true;
                return;
            }
            switch (key)
            {
                case Key.Delete:
                case Key.Back:
                    _editor.DeleteSelection();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    if (_drag != null)
                    {
                        if (_drag.Snapshot != null) S.Restore(_drag.Snapshot);
                        _drag = null;
                        if (IsMouseCaptured) ReleaseMouseCapture();
                        InvalidateVisual();
                    }
                    else if (Tool != null)
                    {
                        SetTool(null);
                    }
                    else
                    {
                        S.Selection.Clear();
                        _editor.RenderProps();
                        InvalidateVisual();
                    }
                    _editor.HideProblems();
                    _editor.RenderStatus();
                    e.Handled = true;
                    return;
                case Key.Left:
                case Key.Right:
                case Key.Up:
                case Key.Down:
                    // Arrow keys don't move elements: elements move by dragging, which keeps them in their regions.
                    e.Handled = true;
                    return;
                case Key.Enter:
                case Key.F2:
                    if (S.Selection.Count == 1)
                    {
                        _editor.StartInlineEdit(S.Selection.First());
                        e.Handled = true;
                    }
                    return;
                case Key.F:
                    Fit();
                    e.Handled = true;
                    return;
                case Key.V:
                    SetTool(null);
                    e.Handled = true;
                    return;
            }
            if ((mods & ModifierKeys.Alt) != 0) return;
            var letter = KeyLetter(key);
            var tool = letter.HasValue ? Core.Tools.ByKey(letter.Value) : null;
            if (tool != null)
            {
                SetTool(tool.Id, (mods & ModifierKeys.Shift) != 0);
                e.Handled = true;
            }
        }

        private static char? KeyLetter(Key key) => key >= Key.A && key <= Key.Z ? (char)('a' + (key - Key.A)) : (char?)null;

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.Key == Key.Space)
            {
                _space = false;
                Cursor = Tool != null ? Cursors.Cross : null;
                e.Handled = true;
            }
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            _space = false;
        }
    }
}
