using System.Windows.Media;
using FsmEditor.Core;

namespace FsmEditor.Editor
{
    /// <summary>
    /// How the panes of a diagram diff or merge mark their elements, the colors of the VS Code edition's editor.css:
    /// added green, removed red, changed amber; a conflict red and dashed, a change Git merged blue, a resolved conflict green.
    /// </summary>
    internal static class DiffMarks
    {
        internal sealed class Mark
        {
            public Color Color;
            public double VertexWidth;
            public double TransitionWidth;
            public double[] Dash;
        }

        private static Color Hex(string c) => (Color)ColorConverter.ConvertFromString(c);

        /// <summary>The mark of element <paramref name="id"/> in <paramref name="diff"/>, or null.</summary>
        public static Mark Of(PaneDiff diff, string id)
        {
            if (diff == null || id == null || !diff.Marks.TryGetValue(id, out var kind)) return null;
            switch (kind)
            {
                case "added": return new Mark { Color = Hex("#2ea043"), VertexWidth = 3, TransitionWidth = 2.6 };
                case "removed": return new Mark { Color = Hex("#e5534b"), VertexWidth = 3, TransitionWidth = 2.6 };
                case "changed": return new Mark { Color = Hex("#d29922"), VertexWidth = 3, TransitionWidth = 2.6 };
                case "conflict": return new Mark { Color = Hex("#e5534b"), VertexWidth = 3, TransitionWidth = 2.6, Dash = new[] { 6.0, 3.0 } };
                case "merged": return new Mark { Color = Hex("#539bf5"), VertexWidth = 2, TransitionWidth = 2 };
                case "resolved": return new Mark { Color = Hex("#2ea043"), VertexWidth = 2, TransitionWidth = 2 };
                default: return null;
            }
        }
    }
}
