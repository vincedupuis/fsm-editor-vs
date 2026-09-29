using System;
using System.Collections.Generic;
using System.Linq;

namespace FsmEditor.Core
{
    public struct RectD
    {
        public double X, Y, W, H;

        public RectD(double x, double y, double w, double h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }

        public double Right => X + W;
        public double Bottom => Y + H;

        public bool Contains(PointD p) => p.X >= X && p.X <= X + W && p.Y >= Y && p.Y <= Y + H;

        public static RectD FromPoints(PointD a, PointD b) =>
            new RectD(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    /// <summary>The area of a region inside its composite state.</summary>
    public sealed class RegionRect
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public RectD Rect;
    }

    public enum TextAnchor { Start, Middle, End }

    public struct LabelPlacement
    {
        public double X, Y;
        public TextAnchor Anchor;
    }

    /// <summary>
    /// Diagram geometry shared by the canvas, the SVG export and the XMI writer
    /// (region shapes): compartments, regions, borders, routes and labels.
    /// </summary>
    public static class Geometry
    {
        public const double Grid = 10;
        public const double NameHeight = 24;
        public const double LineHeight = 14;

        public static double Snap(double n) => Num.Round(n / Grid) * Grid;

        public static double Clamp(double n, double min, double max) => Math.Max(min, Math.Min(max, n));

        /// <summary>Height of the name compartment plus the internal activities compartment.</summary>
        public static double HeaderHeight(FsmModel model, Vertex v)
        {
            var h = NameHeight + (v.Stereotype.Length > 0 ? 12 : 0);
            var n = Labels.ActivityLines(model, v).Count;
            if (n > 0) h += n * LineHeight + 6;
            return h;
        }

        public static List<RegionRect> RegionRects(FsmModel model, Vertex v)
        {
            var regs = v.Regions;
            var output = new List<RegionRect>();
            if (regs.Count == 0) return output;
            var top = v.Y + Math.Min(HeaderHeight(model, v), v.H - 20);
            var bh = v.Y + v.H - top;
            var n = regs.Count;
            var horizontal = v.RegionLayout == RegionLayout.Horizontal;
            for (int i = 0; i < n; i++)
            {
                var rect = horizontal
                    ? new RectD(v.X + i * v.W / n, top, v.W / n, bh)
                    : new RectD(v.X, top + i * bh / n, v.W, bh / n);
                output.Add(new RegionRect { Id = regs[i].Id, Name = regs[i].Name, Rect = rect });
            }
            return output;
        }

        /// <summary>Deepest region under <paramref name="p"/>, ignoring the states in <paramref name="exclude"/>.</summary>
        public static RegionRect RegionAt(ModelIndex ix, PointD p, ISet<string> exclude)
        {
            var states = ix.Model.Vertices
                .Where(v => v.Type == VertexType.State && v.Regions.Count > 0 && !exclude.Contains(v.Id))
                .OrderByDescending(ix.Depth);
            foreach (var s in states)
            {
                foreach (var r in RegionRects(ix.Model, s))
                {
                    if (r.Rect.Contains(p)) return r;
                }
            }
            return null;
        }

        /// <summary>Deepest state under <paramref name="p"/> (with a small margin around its border).</summary>
        public static Vertex StateAt(ModelIndex ix, PointD p, ISet<string> exclude) =>
            ix.Model.Vertices
                .Where(v => v.Type == VertexType.State && !exclude.Contains(v.Id) && new RectD(v.X - 6, v.Y - 6, v.W + 12, v.H + 12).Contains(p))
                .OrderByDescending(ix.Depth)
                .FirstOrDefault();

        /// <summary>Nearest point on the border of <paramref name="s"/>.</summary>
        public static PointD BorderPoint(Vertex s, PointD p)
        {
            var x = Clamp(p.X, s.X, s.X + s.W);
            var y = Clamp(p.Y, s.Y, s.Y + s.H);
            var d0 = Math.Abs(x - s.X);
            var d1 = Math.Abs(s.X + s.W - x);
            var d2 = Math.Abs(y - s.Y);
            var d3 = Math.Abs(s.Y + s.H - y);
            var m = Math.Min(Math.Min(d0, d1), Math.Min(d2, d3));
            if (m == d0) return new PointD(s.X, y);
            if (m == d1) return new PointD(s.X + s.W, y);
            if (m == d2) return new PointD(x, s.Y);
            return new PointD(x, s.Y + s.H);
        }

        /// <summary>Centers the connection point <paramref name="cp"/> on the border of <paramref name="s"/>, nearest to <paramref name="p"/>.</summary>
        public static void SnapToBorder(Vertex cp, Vertex s, PointD p)
        {
            var b = BorderPoint(s, p);
            cp.X = b.X - cp.W / 2;
            cp.Y = b.Y - cp.H / 2;
        }

        private enum ShapeKind { Rect, Diamond, Circle }

        private static ShapeKind KindOf(Vertex v)
        {
            switch (v.Type)
            {
                case VertexType.State:
                case VertexType.Comment:
                case VertexType.Fork:
                case VertexType.Join:
                    return ShapeKind.Rect;
                case VertexType.Choice:
                    return ShapeKind.Diamond;
                default:
                    return ShapeKind.Circle;
            }
        }

        /// <summary>Point where the ray from the center of <paramref name="v"/> towards <paramref name="p"/> leaves its outline.</summary>
        public static PointD Clip(Vertex v, PointD p)
        {
            var c = v.Center;
            var dx = p.X - c.X;
            var dy = p.Y - c.Y;
            if (dx == 0 && dy == 0) return c;
            switch (KindOf(v))
            {
                case ShapeKind.Circle:
                {
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    var r = Math.Min(v.W, v.H) / 2;
                    return new PointD(c.X + dx / d * r, c.Y + dy / d * r);
                }
                case ShapeKind.Diamond:
                {
                    var t = 1 / (Math.Abs(dx) / (v.W / 2) + Math.Abs(dy) / (v.H / 2));
                    return new PointD(c.X + dx * t, c.Y + dy * t);
                }
                default:
                {
                    var tx = dx != 0 ? v.W / 2 / Math.Abs(dx) : double.PositiveInfinity;
                    var ty = dy != 0 ? v.H / 2 / Math.Abs(dy) : double.PositiveInfinity;
                    var t = Math.Min(tx, ty);
                    return new PointD(c.X + dx * t, c.Y + dy * t);
                }
            }
        }

        /// <summary>The polyline a transition is drawn along, or null when an end is missing.</summary>
        public static List<PointD> Route(ModelIndex ix, Transition t)
        {
            var s = ix.Vertex(t.Source);
            var g = ix.Vertex(t.Target);
            if (s == null || g == null) return null;
            var pts = t.Points;
            if (s == g && pts.Count == 0)
            {
                // Self transition: a loop over the top-right corner.
                var gap = Math.Max(18, Math.Min(30, s.W / 3));
                var a = new PointD(s.X + s.W * 0.72, s.Y - gap);
                var b = new PointD(s.X + s.W + gap, s.Y - gap);
                var c = new PointD(s.X + s.W + gap, s.Y + Math.Min(s.H * 0.3, 22));
                var start0 = s.Type == VertexType.State ? new PointD(a.X, s.Y) : Clip(s, a);
                var end0 = s.Type == VertexType.State ? new PointD(s.X + s.W, c.Y) : Clip(s, c);
                return new List<PointD> { start0, a, b, c, end0 };
            }
            PointD start, end;
            var first = pts.Count > 0 ? pts[0] : (PointD?)null;
            var last = pts.Count > 0 ? pts[pts.Count - 1] : (PointD?)null;
            if (ix.IsInside(g, s.Id))
            {
                start = BorderPoint(s, first ?? g.Center);
                end = Clip(g, last ?? start);
            }
            else if (ix.IsInside(s, g.Id))
            {
                end = BorderPoint(g, last ?? s.Center);
                start = Clip(s, first ?? end);
            }
            else
            {
                start = Clip(s, first ?? g.Center);
                end = Clip(g, last ?? s.Center);
            }
            var output = new List<PointD> { start };
            output.AddRange(pts);
            output.Add(end);
            return output;
        }

        /// <summary>Point halfway along the polyline, with the direction of the segment it is on.</summary>
        public static (PointD Point, double Dx, double Dy) PolylineMid(IReadOnlyList<PointD> pts)
        {
            double total = 0;
            for (int i = 1; i < pts.Count; i++) total += Dist(pts[i - 1], pts[i]);
            var half = total / 2;
            for (int i = 1; i < pts.Count; i++)
            {
                var a = pts[i - 1];
                var b = pts[i];
                var len = Dist(a, b);
                if (len >= half && len > 0)
                {
                    var k = half / len;
                    return (new PointD(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k), b.X - a.X, b.Y - a.Y);
                }
                half -= len;
            }
            return (pts[0], 1, 0);
        }

        public static double Dist(PointD a, PointD b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

        /// <summary>Label position and alignment: beside the middle of the line, above it or to its left.</summary>
        public static LabelPlacement LabelPos(Transition t, IReadOnlyList<PointD> pts)
        {
            var (m, dx, dy) = PolylineMid(pts);
            var off = t.LabelOffset ?? new PointD(0, 0);
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len == 0) len = 1;
            var nx = -dy / len;
            var ny = dx / len;
            if (ny > 0.2 || (Math.Abs(ny) <= 0.2 && nx > 0))
            {
                nx = -nx;
                ny = -ny;
            }
            var anchor = nx < -0.5 ? TextAnchor.End : nx > 0.5 ? TextAnchor.Start : TextAnchor.Middle;
            return new LabelPlacement
            {
                X = m.X + nx * 6 + off.X,
                Y = m.Y + ny * 6 + off.Y + (ny < -0.5 ? -2 : 4),
                Anchor = anchor,
            };
        }

        public static double DistToSegment(PointD p, PointD a, PointD b)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var l2 = dx * dx + dy * dy;
            var t = l2 != 0 ? Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1) : 0;
            return Dist(p, new PointD(a.X + t * dx, a.Y + t * dy));
        }

        /// <summary>Estimated width of a transition label in the 11px diagram font.</summary>
        public static double EstimateLabelWidth(string label) => label.Length * 6.6;

        /// <summary>Bounds of everything drawn, including captions and transition labels.</summary>
        public static RectD ContentBounds(ModelIndex ix, Func<string, double> labelWidth = null)
        {
            var model = ix.Model;
            labelWidth = labelWidth ?? EstimateLabelWidth;
            if (model.Vertices.Count == 0) return new RectD(0, 0, 400, 300);
            double x1 = double.PositiveInfinity, y1 = double.PositiveInfinity, x2 = double.NegativeInfinity, y2 = double.NegativeInfinity;
            void Add(double x, double y)
            {
                x1 = Math.Min(x1, x);
                y1 = Math.Min(y1, y);
                x2 = Math.Max(x2, x);
                y2 = Math.Max(y2, y);
            }
            foreach (var v in model.Vertices)
            {
                Add(v.X, v.Y);
                var caption = v.Invariant.Length > 0 || (v.Name.Length > 0 && v.Type != VertexType.State);
                Add(v.X + v.W, v.Y + v.H + (caption ? 16 : 0));
            }
            foreach (var t in model.Transitions)
            {
                var pts = Route(ix, t);
                if (pts == null) continue;
                foreach (var p in pts) Add(p.X, p.Y);
                var label = Labels.TransitionLabel(model, t);
                if (label.Length > 0)
                {
                    var lp = LabelPos(t, pts);
                    var w = labelWidth(label);
                    var x0 = lp.Anchor == TextAnchor.End ? lp.X - w : lp.Anchor == TextAnchor.Start ? lp.X : lp.X - w / 2;
                    Add(x0, lp.Y - 12);
                    Add(x0 + w, lp.Y + 4);
                }
            }
            return new RectD(x1, y1, x2 - x1, y2 - y1);
        }

        /// <summary>Vertices in drawing order: outer ones first, border points above their state.</summary>
        public static List<Vertex> DrawingOrder(ModelIndex ix) =>
            ix.Model.Vertices
                .Select((v, i) => (v, i, d: ix.Depth(v) + (ix.IsBorderVertex(v) ? 0.5 : 0)))
                .OrderBy(o => o.d)
                .ThenBy(o => o.i)
                .Select(o => o.v)
                .ToList();

        /// <summary>Whether a transition is drawn as a line (internal transitions are listed in the state instead).</summary>
        public static bool IsDrawn(ModelIndex ix, Transition t) =>
            !(t.Kind == TransitionKind.Internal && t.Source == t.Target && ix.Vertex(t.Source)?.Type == VertexType.State);
    }
}
