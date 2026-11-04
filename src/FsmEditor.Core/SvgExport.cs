using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FsmEditor.Core
{
    /// <summary>
    /// Standalone SVG drawing of a diagram, cropped to its content, in a light
    /// theme whatever the editor theme. It draws the same shapes as the canvas.
    /// </summary>
    public static class SvgExport
    {
        private const string Css = @"
text{font-family:-apple-system,""Segoe UI"",Helvetica,Arial,sans-serif;fill:#1f1f1f;font-size:12px}
.shape{fill:#fdfdfd;stroke:#333;stroke-width:1.4}
.fill{fill:#333}
.stroke{fill:none;stroke:#333;stroke-width:1.6}
.sep{stroke:#333;stroke-width:1;opacity:.8}
.sep.dashed{stroke-dasharray:7 4}
.name{font-weight:600}
.activity,.tlabel{font-size:11px}
.stereo,.region-name{font-size:10px;font-style:italic;opacity:.8}
.invariant{font-size:11px;font-style:italic}
.glyph{font-size:11px;font-weight:700}
.v-comment .shape{fill:#fbf3cf}
.v-comment text{font-size:11px}
.hit{fill:transparent}
.anchor{fill:none;stroke:#333;stroke-width:1;stroke-dasharray:4 3;opacity:.7}
.line,.arrow{fill:none;stroke:#333;stroke-width:1.3;stroke-linejoin:round}
.tlabel{paint-order:stroke;stroke:#fff;stroke-width:4px;stroke-linejoin:round}";

        /// <summary>Fill of a state (<c>.shape</c> in the style sheet).</summary>
        private const string StateFill = "#fdfdfd";

        public static string Export(DiagramSession session)
        {
            session.Reindex();
            var ix = session.Index;
            var model = session.Model;
            var b = Geometry.ContentBounds(ix);
            const double pad = 20;
            var x = Num.R1(b.X - pad);
            var y = Num.R1(b.Y - pad);
            var w = Num.R1(b.W + pad * 2);
            var h = Num.R1(b.H + pad * 2);
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{F(x)} {F(y)} {F(w)} {F(h)}\" width=\"{Math.Ceiling(b.W + pad * 2)}\" height=\"{Math.Ceiling(b.H + pad * 2)}\">\n");
            sb.Append($"<title>{Esc(model.Name.Length > 0 ? model.Name : "State machine")}</title>\n");
            sb.Append($"<style>{Css}</style>\n");
            sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" fill=\"#fff\"/>\n");
            var sorted = Geometry.DrawingOrder(ix);
            var fills = 0;
            foreach (var v in sorted) VertexShape(sb, session, v, ref fills);
            foreach (var v in sorted.Where(v => v.Type == VertexType.Comment)) Anchors(sb, ix, v);
            foreach (var t in model.Transitions) TransitionShape(sb, ix, model, t);
            sb.Append("\n</svg>\n");
            return sb.ToString();
        }

        private static string F(double n) => Num.Format(n);
        private static string F1(double n) => Num.Format(Num.R1(n));

        private static string Esc(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");

        private static string Text(double x, double y, string cls, string text, string anchor = "middle", string color = null) =>
            $"<text class=\"{cls}\" x=\"{F1(x)}\" y=\"{F1(y)}\" text-anchor=\"{anchor}\"{Style(color != null ? "fill:" + color : null)}>{Esc(text)}</text>";

        /// <summary>A style attribute with the given declarations (nulls skipped), or nothing.</summary>
        private static string Style(params string[] decls)
        {
            var d = string.Join(";", decls.Where(x => !string.IsNullOrEmpty(x)));
            return d.Length > 0 ? $" style=\"{d}\"" : "";
        }

        private static string Cross(double cx, double cy, double k, string style) =>
            $"<path class=\"stroke\" d=\"M{F(cx - k)},{F(cy - k)} L{F(cx + k)},{F(cy + k)} M{F(cx + k)},{F(cy - k)} L{F(cx - k)},{F(cy + k)}\"{style}/>";

        /// <summary>Draws a vertex; <paramref name="fills"/> numbers the fill gradients of composite states.</summary>
        private static void VertexShape(StringBuilder sb, DiagramSession session, Vertex v, ref int fills)
        {
            var model = session.Model;
            double x = v.X, y = v.Y, w = v.W, h = v.H;
            var cx = x + w / 2;
            var cy = y + h / 2;
            var r = Math.Min(w, h) / 2;
            sb.Append($"<g class=\"vertex v-{v.Type.Key()}\">");
            var c = ElementColors.Of(v.Color);
            var shapeSt = c == null ? "" : Style("fill:" + ElementColors.Tint(c), "stroke:" + c);
            var fillSt = c == null ? "" : Style("fill:" + c);
            string StrokeSt(string width) => Style(c != null ? "stroke:" + c : null, width != null ? "stroke-width:" + width : null);
            var sepSt = c == null ? "" : Style("stroke:" + c);
            switch (v.Type)
            {
                case VertexType.State:
                {
                    var lines = Geometry.ActivityRowItems(model, v);
                    var regions = Geometry.RegionRects(model, v);
                    var st = shapeSt;
                    if (regions.Count > 0)
                    {
                        // Composite state: its color tints the header, its background color the regions.
                        var bg = ElementColors.Background(v.ItemColors);
                        var head = c != null ? ElementColors.Tint(c) : StateFill;
                        var body = bg != null ? ElementColors.Tint(bg) : StateFill;
                        if (head != body)
                        {
                            var id = "fsm-fill-" + ++fills;
                            var at = F(Num.Round((regions[0].Rect.Y - y) / h * 10000) / 10000);
                            sb.Append($"<defs><linearGradient id=\"{id}\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">"
                                + $"<stop offset=\"{at}\" style=\"stop-color:{head}\"/><stop offset=\"{at}\" style=\"stop-color:{body}\"/></linearGradient></defs>");
                            st = Style($"fill:url(#{id})", c != null ? "stroke:" + c : null);
                        }
                    }
                    sb.Append($"<rect class=\"shape\" x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" rx=\"10\"{st}/>");
                    var title = v.Name + (v.Submachine.Length > 0 ? " : " + session.MachineLabel(v.Submachine) : "");
                    var centered = regions.Count == 0 && lines.Count == 0;
                    var ty = y + 16;
                    var stereo = v.Stereotype.Length > 0;
                    if (stereo)
                    {
                        sb.Append(Text(cx, y + (centered ? h / 2 - 4 : 13), "stereo", $"«{v.Stereotype}»", "middle", c));
                        ty += 12;
                    }
                    if (centered)
                    {
                        sb.Append(Text(cx, y + h / 2 + (stereo ? 10 : 4), "name", title, "middle", c));
                    }
                    else
                    {
                        sb.Append(Text(cx, ty, "name", title, "middle", c));
                        var ly = y + Geometry.NameHeight + (stereo ? 12 : 0);
                        if (lines.Count > 0)
                        {
                            sb.Append($"<line class=\"sep\" x1=\"{F(x)}\" y1=\"{F(ly)}\" x2=\"{F(x + w)}\" y2=\"{F(ly)}\"{sepSt}/>");
                            foreach (var l in lines)
                            {
                                ly += Geometry.LineHeight;
                                sb.Append(Text(x + 8, ly, "activity", l.Text, "start", l.Color));
                            }
                        }
                    }
                    if (regions.Count > 0)
                    {
                        var top = regions[0].Rect.Y;
                        sb.Append($"<line class=\"sep\" x1=\"{F(x)}\" y1=\"{F1(top)}\" x2=\"{F(x + w)}\" y2=\"{F1(top)}\"{sepSt}/>");
                        for (int i = 0; i < regions.Count; i++)
                        {
                            var rr = regions[i].Rect;
                            if (i > 0)
                            {
                                sb.Append(v.RegionLayout == RegionLayout.Horizontal
                                    ? $"<line class=\"sep dashed\" x1=\"{F1(rr.X)}\" y1=\"{F1(rr.Y)}\" x2=\"{F1(rr.X)}\" y2=\"{F1(rr.Y + rr.H)}\"{sepSt}/>"
                                    : $"<line class=\"sep dashed\" x1=\"{F1(rr.X)}\" y1=\"{F1(rr.Y)}\" x2=\"{F1(rr.X + rr.W)}\" y2=\"{F1(rr.Y)}\"{sepSt}/>");
                            }
                            if (regions[i].Name.Length > 0) sb.Append(Text(rr.X + 6, rr.Y + 12, "region-name", regions[i].Name, "start", c));
                        }
                    }
                    if (v.Submachine.Length > 0)
                    {
                        var gx = x + w - 26;
                        var gy = y + h - 14;
                        var glyphSt = StrokeSt("1.2");
                        sb.Append($"<rect class=\"stroke\" x=\"{F(gx)}\" y=\"{F(gy)}\" width=\"8\" height=\"6\" rx=\"2\"{glyphSt}/>");
                        sb.Append($"<rect class=\"stroke\" x=\"{F(gx + 13)}\" y=\"{F(gy)}\" width=\"8\" height=\"6\" rx=\"2\"{glyphSt}/>");
                        sb.Append($"<line class=\"stroke\" x1=\"{F(gx + 8)}\" y1=\"{F(gy + 3)}\" x2=\"{F(gx + 13)}\" y2=\"{F(gy + 3)}\"{glyphSt}/>");
                    }
                    if (v.Invariant.Length > 0) sb.Append(Text(x + 4, y + h + 14, "invariant", $"{{{v.Invariant}}}", "start", ElementColors.Item(v.Color, v.ItemColors, "invariant")));
                    break;
                }
                case VertexType.Final:
                    sb.Append($"<circle class=\"shape\" cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\"{shapeSt}/><circle class=\"fill\" cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(Math.Max(2, r - 5))}\"{fillSt}/>");
                    break;
                case VertexType.Initial:
                case VertexType.Junction:
                    sb.Append($"<circle class=\"fill\" cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\"{fillSt}/>");
                    break;
                case VertexType.ShallowHistory:
                case VertexType.DeepHistory:
                    sb.Append($"<circle class=\"shape\" cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\"{shapeSt}/>");
                    sb.Append(Text(cx, cy + 4, "glyph", v.Type == VertexType.DeepHistory ? "H*" : "H", "middle", c));
                    break;
                case VertexType.Choice:
                    sb.Append($"<path class=\"shape\" d=\"M{F(cx)},{F(y)} L{F(x + w)},{F(cy)} L{F(cx)},{F(y + h)} L{F(x)},{F(cy)} Z\"{shapeSt}/>");
                    break;
                case VertexType.Fork:
                case VertexType.Join:
                    sb.Append($"<rect class=\"fill\" x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" rx=\"1\"{fillSt}/>");
                    break;
                case VertexType.ConnectionPointRef:
                case VertexType.EntryPoint:
                    sb.Append($"<circle class=\"shape\" cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\"{shapeSt}/>");
                    if (v.Type == VertexType.ConnectionPointRef && v.PointKind == PointKind.Exit) sb.Append(Cross(cx, cy, r * 0.62, StrokeSt("1.3")));
                    break;
                case VertexType.ExitPoint:
                    sb.Append($"<circle class=\"shape\" cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\"{shapeSt}/>");
                    sb.Append(Cross(cx, cy, r * 0.62, StrokeSt("1.3")));
                    break;
                case VertexType.Terminate:
                    sb.Append($"<rect class=\"hit\" x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\"/>");
                    sb.Append($"<path class=\"stroke\" d=\"M{F(x)},{F(y)} L{F(x + w)},{F(y + h)} M{F(x + w)},{F(y)} L{F(x)},{F(y + h)}\"{StrokeSt("2")}/>");
                    break;
                case VertexType.Comment:
                {
                    const double f = 12;
                    sb.Append($"<path class=\"shape\" d=\"M{F(x)},{F(y)} H{F(x + w - f)} L{F(x + w)},{F(y + f)} V{F(y + h)} H{F(x)} Z\"{shapeSt}/>");
                    sb.Append($"<path class=\"stroke\" d=\"M{F(x + w - f)},{F(y)} V{F(y + f)} H{F(x + w)}\"{StrokeSt("1")}/>");
                    var lines = Geometry.CommentLines(v.Text, w - 16);
                    for (int i = 0; i < lines.Count; i++)
                    {
                        if (16 + i * 14 < h) sb.Append(Text(x + 8, y + 17 + i * 14, "", lines[i], "start", c));
                    }
                    break;
                }
            }
            var caption = v.Type == VertexType.ConnectionPointRef ? Or(session.PointName(session.Index.Vertex(v.Parent), v.Ref), "?") : v.Name;
            if (caption.Length > 0 && v.Type != VertexType.State && v.Type != VertexType.Comment)
            {
                var below = v.Type == VertexType.Fork || v.Type == VertexType.Join ? y + h + 13 : y + h + 12;
                sb.Append(Text(cx, below, "activity", caption, "middle", c));
            }
            sb.Append("</g>");
        }

        private static string Or(string a, string b) => a.Length > 0 ? a : b;

        /// <summary>Arrow head at <paramref name="b"/> for a line coming from <paramref name="a"/>.</summary>
        public static (PointD P1, PointD P2) ArrowHead(PointD a, PointD b)
        {
            var ang = Math.Atan2(b.Y - a.Y, b.X - a.X);
            const double L = 10;
            const double W = 0.42;
            return (new PointD(b.X - L * Math.Cos(ang - W), b.Y - L * Math.Sin(ang - W)),
                    new PointD(b.X - L * Math.Cos(ang + W), b.Y - L * Math.Sin(ang + W)));
        }

        private static void TransitionShape(StringBuilder sb, ModelIndex ix, FsmModel model, Transition t)
        {
            if (!Geometry.IsDrawn(ix, t)) return;
            var pts = Geometry.Route(ix, t);
            if (pts == null) return;
            var d = "M" + string.Join(" L", pts.Select(p => $"{F1(p.X)},{F1(p.Y)}"));
            sb.Append("<g class=\"transition\">");
            var c = ElementColors.Of(t.Color);
            var lineSt = c == null ? "" : Style("stroke:" + c);
            sb.Append($"<path class=\"line\" d=\"{d}\"{lineSt}/>");
            var b = pts[pts.Count - 1];
            var (p1, p2) = ArrowHead(pts[pts.Count - 2], b);
            sb.Append($"<path class=\"arrow\" d=\"M{F1(p1.X)},{F1(p1.Y)} L{F1(b.X)},{F1(b.Y)} L{F1(p2.X)},{F1(p2.Y)}\"{lineSt}/>");
            var lines = Labels.TransitionLabelItems(model, t);
            if (lines.Count > 0)
            {
                var lp = Geometry.LabelPos(t, pts);
                var anchor = lp.Anchor == TextAnchor.End ? "end" : lp.Anchor == TextAnchor.Start ? "start" : "middle";
                sb.Append($"<text class=\"tlabel\" x=\"{F1(lp.X)}\" y=\"{F1(Geometry.LabelLineY(lp, 0, lines.Count))}\" text-anchor=\"{anchor}\">"
                    + string.Concat(lines.Select((l, i) =>
                    {
                        var lc = ElementColors.Item(t.Color, t.ItemColors, l.Key);
                        return $"<tspan x=\"{F1(lp.X)}\" dy=\"{(i > 0 ? Geometry.LabelLineHeight : 0)}\"{Style(lc != null ? "fill:" + lc : null)}>{Esc(l.Text)}</tspan>";
                    })) + "</text>");
            }
            sb.Append("</g>");
        }

        private static void Anchors(StringBuilder sb, ModelIndex ix, Vertex v)
        {
            foreach (var (from, to) in AnchorLines(ix, v))
            {
                sb.Append($"<line class=\"anchor\" x1=\"{F1(from.X)}\" y1=\"{F1(from.Y)}\" x2=\"{F1(to.X)}\" y2=\"{F1(to.Y)}\"/>");
            }
        }

        /// <summary>The dashed lines linking a comment to the elements it annotates.</summary>
        public static IEnumerable<(PointD From, PointD To)> AnchorLines(ModelIndex ix, Vertex comment)
        {
            foreach (var a in comment.Anchors)
            {
                var target = ix.Vertex(a);
                PointD? p = null;
                if (target != null)
                {
                    p = target.Center;
                }
                else if (ix.Transition(a) is Transition t)
                {
                    var pts = Geometry.Route(ix, t);
                    if (pts != null) p = Geometry.PolylineMid(pts).Point;
                }
                if (p == null) continue;
                var from = Geometry.Clip(comment, p.Value);
                var to = target != null ? Geometry.Clip(target, from) : p.Value;
                yield return (from, to);
            }
        }
    }
}
