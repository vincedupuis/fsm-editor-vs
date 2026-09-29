using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FsmEditor.Editor
{
    /// <summary>Line icons of the toolbox and toolbar, drawn on a 24×24 grid in the current text color.</summary>
    internal static class Icons
    {
        private enum Paint { Stroke, Fill, Hole, Faint, Dashed, Heavy }

        private static (Paint, System.Windows.Media.Geometry) S(string path) => (Paint.Stroke, System.Windows.Media.Geometry.Parse(path));
        private static (Paint, System.Windows.Media.Geometry) F(string path) => (Paint.Fill, System.Windows.Media.Geometry.Parse(path));
        private static (Paint, System.Windows.Media.Geometry) Rect(double x, double y, double w, double h, double r, Paint p = Paint.Stroke) =>
            (p, new RectangleGeometry(new System.Windows.Rect(x, y, w, h), r, r));
        private static (Paint, System.Windows.Media.Geometry) Circle(double cx, double cy, double r, Paint p = Paint.Stroke) =>
            (p, new EllipseGeometry(new Point(cx, cy), r, r));
        private static (Paint, System.Windows.Media.Geometry) Line(double x1, double y1, double x2, double y2, Paint p = Paint.Stroke) =>
            (p, new LineGeometry(new Point(x1, y1), new Point(x2, y2)));

        private static readonly Dictionary<string, (Paint Paint, System.Windows.Media.Geometry Geometry)[]> Parts = new Dictionary<string, (Paint, System.Windows.Media.Geometry)[]>
        {
            ["state"] = new[] { Rect(3, 6, 18, 12, 4) },
            ["composite"] = new[] { Rect(2, 3, 20, 18, 4), Line(2, 8, 22, 8), Rect(6, 11, 7, 6, 2) },
            ["orthogonal"] = new[] { Rect(2, 3, 20, 18, 4), Line(2, 8, 22, 8), Line(2, 14.5, 22, 14.5, Paint.Dashed) },
            ["submachine"] = new[] { Rect(2, 5, 20, 14, 4), Rect(11, 12, 4, 3, 1), Rect(17, 12, 3, 3, 1), Line(15, 13.5, 17, 13.5) },
            ["final"] = new[] { Circle(12, 12, 8), Circle(12, 12, 4.5, Paint.Fill) },
            ["initial"] = new[] { Circle(12, 12, 5.5, Paint.Fill) },
            ["shallowHistory"] = new[] { Circle(12, 12, 8.5), S("M9,8 v8 M15,8 v8 M9,12 h6") },
            ["deepHistory"] = new[] { Circle(12, 12, 8.5), S("M7.5,8 v8 M12.5,8 v8 M7.5,12 h5 M16.5,8.5 v4 M14.7,9.5 l3.6,2 M18.3,9.5 l-3.6,2") },
            ["choice"] = new[] { S("M12,4 l8,8 l-8,8 l-8,-8 z") },
            ["junction"] = new[] { Circle(12, 12, 4.5, Paint.Fill) },
            ["fork"] = new[] { Line(12, 2, 12, 9), Rect(3, 9, 18, 3.5, 0, Paint.Fill), Line(7, 12.5, 7, 21), Line(17, 12.5, 17, 21) },
            ["join"] = new[] { Line(7, 2, 7, 11), Line(17, 2, 17, 11), Rect(3, 11, 18, 3.5, 0, Paint.Fill), Line(12, 14.5, 12, 22) },
            ["entryPoint"] = new[] { (Paint.Faint, System.Windows.Media.Geometry.Parse("M2,12 h5 M17,12 h5")), Circle(12, 12, 5) },
            ["exitPoint"] = new[] { (Paint.Faint, System.Windows.Media.Geometry.Parse("M2,12 h5 M17,12 h5")), Circle(12, 12, 5), S("M9.2,9.2 l5.6,5.6 M14.8,9.2 l-5.6,5.6") },
            ["terminate"] = new[] { (Paint.Heavy, System.Windows.Media.Geometry.Parse("M6,6 l12,12 M18,6 L6,18")) },
            ["connectionPointRef"] = new[] { Rect(2, 5, 14, 14, 3.5), Circle(16, 9, 3, Paint.Hole), Circle(16, 16, 3, Paint.Hole), S("M14.2,14.2 l3.6,3.6 M17.8,14.2 l-3.6,3.6") },
            ["transition"] = new[] { S("M3,18 L20,5"), S("M13,5 h7 v7") },
            ["region"] = new[] { Rect(2, 3, 20, 18, 4), Line(2, 12, 22, 12, Paint.Dashed), S("M12,15 v4 M10,17 h4") },
            ["comment"] = new[] { S("M4,3 h11 l5,5 v13 H4 z"), S("M15,3 v5 h5"), S("M7,12 h9 M7,16 h7") },
            ["select"] = new[] { S("M5,3 l14,8 l-6,1.5 L10,19 z") },
            ["undo"] = new[] { S("M9,14 L4,9 l5,-5"), S("M4,9 H15 A5,5 0 0 1 15,19 H12") },
            ["redo"] = new[] { S("M15,14 l5,-5 l-5,-5"), S("M20,9 H9 A5,5 0 0 0 9,19 H12") },
            ["zoomIn"] = new[] { Circle(11, 11, 7), S("M21,21 l-5,-5 M8,11 h6 M11,8 v6") },
            ["zoomOut"] = new[] { Circle(11, 11, 7), S("M21,21 l-5,-5 M8,11 h6") },
            ["fit"] = new[] { S("M4,9 V4 h5 M20,9 V4 h-5 M4,15 v5 h5 M20,15 v5 h-5") },
            ["export"] = new[] { S("M12,3 v12 M7,8 l5,-5 l5,5"), S("M5,14 v6 h14 v-6") },
            ["xmi"] = new[] { S("M8,4 C6,4 6,6 6,8 C6,10 4,12 4,12 C4,12 6,12 6,16 C6,20 6,20 8,20 M16,4 C18,4 18,6 18,8 C18,10 20,12 20,12 C20,12 18,12 18,16 C18,20 18,20 16,20") },
            ["code"] = new[] { S("M8,7 l-5,5 l5,5 M16,7 l5,5 l-5,5 M14,4 l-4,16") },
            ["trash"] = new[] { S("M4,7 h16 M9,7 V4 h6 v3 M6,7 l1,13 h10 l1,-13") },
        };

        /// <summary>The icon <paramref name="name"/> (a tool id or a toolbar action), or null when unknown.</summary>
        public static FrameworkElement Create(string name, Color foreground, Color background, double size = 16)
        {
            if (!Parts.TryGetValue(name, out var parts)) return null;
            var fg = new SolidColorBrush(foreground);
            var faint = new SolidColorBrush(Color.FromArgb(128, foreground.R, foreground.G, foreground.B));
            var group = new DrawingGroup();
            // Fixes the bounds to the whole 24×24 grid.
            group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new System.Windows.Rect(0, 0, 24, 24))));
            Pen Pen(Brush b, double w, bool dashed = false)
            {
                var p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                if (dashed) p.DashStyle = new DashStyle(new[] { 2.5 / w, 2 / w }, 0);
                return p;
            }
            foreach (var (paint, geometry) in parts)
            {
                switch (paint)
                {
                    case Paint.Fill: group.Children.Add(new GeometryDrawing(fg, null, geometry)); break;
                    case Paint.Hole: group.Children.Add(new GeometryDrawing(new SolidColorBrush(background), Pen(fg, 1.5), geometry)); break;
                    case Paint.Faint: group.Children.Add(new GeometryDrawing(null, Pen(faint, 1.5), geometry)); break;
                    case Paint.Dashed: group.Children.Add(new GeometryDrawing(null, Pen(fg, 1.5, true), geometry)); break;
                    case Paint.Heavy: group.Children.Add(new GeometryDrawing(null, Pen(fg, 2), geometry)); break;
                    default: group.Children.Add(new GeometryDrawing(null, Pen(fg, 1.5), geometry)); break;
                }
            }
            group.Freeze();
            return new Image { Source = new DrawingImage(group), Width = size, Height = size, SnapsToDevicePixels = true };
        }
    }
}
