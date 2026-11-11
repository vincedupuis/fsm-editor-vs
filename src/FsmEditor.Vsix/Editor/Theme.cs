using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace FsmEditor.Editor
{
    /// <summary>
    /// Colors of the diagram, taken from the current Visual Studio theme. The
    /// canvas redraws itself when <see cref="Changed"/> fires.
    /// </summary>
    internal sealed class Theme
    {
        private static Theme _current;

        public static Theme Current => _current ?? (_current = new Theme());

        /// <summary>The light colors of the SVG export, whatever the Visual Studio theme: for the PNG export.</summary>
        public static Theme Export { get; } = new Theme(Colors.White, Rgb(0x1f, 0x1f, 0x1f), Rgb(0x33, 0x33, 0x33), Rgb(0xfd, 0xfd, 0xfd), Rgb(0xfb, 0xf3, 0xcf));

        public static event EventHandler Changed;

        static Theme()
        {
            VSColorTheme.ThemeChanged += _ =>
            {
                _current = new Theme();
                Changed?.Invoke(null, EventArgs.Empty);
            };
        }

        private Theme()
        {
            Background = ThemeColor(EnvironmentColors.ToolWindowBackgroundColorKey, Colors.White);
            Foreground = ThemeColor(EnvironmentColors.ToolWindowTextColorKey, Rgb(0x1f, 0x1f, 0x1f));
            Muted = ThemeColor(EnvironmentColors.SystemGrayTextColorKey, Rgb(0x71, 0x71, 0x71));
            Accent = ThemeColor(EnvironmentColors.SystemHighlightColorKey, Rgb(0x00, 0x7a, 0xcc));
            Panel = ThemeColor(EnvironmentColors.ToolWindowBackgroundColorKey, Colors.White);
            Hover = ThemeColor(EnvironmentColors.CommandBarMouseOverBackgroundBeginColorKey, Rgb(0xe0, 0xe0, 0xe0));
            Border = Mix(Background, Foreground, 0.22);
            Line = Mix(Background, Foreground, 0.85);
            StateFill = Mix(Background, Foreground, 0.05);
            NoteFill = Mix(StateFill, Rgb(0xe8, 0xc5, 0x47), 0.22);
            Grid = Mix(Background, Foreground, 0.14);
            Widget = Mix(Background, Foreground, 0.06);
            IsDark = Luminance(Background) < 0.5;
        }

        private Theme(Color background, Color foreground, Color line, Color stateFill, Color noteFill)
        {
            Background = background;
            Foreground = foreground;
            Muted = Rgb(0x6f, 0x6f, 0x6f);
            Accent = Rgb(0x00, 0x78, 0xd4);
            Panel = background;
            Hover = Rgb(0xe0, 0xe0, 0xe0);
            Border = Mix(Background, Foreground, 0.22);
            Line = line;
            StateFill = stateFill;
            NoteFill = noteFill;
            Grid = Mix(Background, Foreground, 0.14);
            Widget = Mix(Background, Foreground, 0.06);
            IsDark = false;
        }

        public bool IsDark { get; }
        public Color Background { get; }
        public Color Foreground { get; }
        public Color Muted { get; }
        public Color Accent { get; }
        public Color Panel { get; }
        public Color Hover { get; }
        public Color Border { get; }
        public Color Line { get; }
        public Color StateFill { get; }
        public Color NoteFill { get; }
        public Color Grid { get; }
        public Color Widget { get; }
        public Color Error { get; } = Rgb(0xe5, 0x14, 0x00);
        public Color Warning { get; } = Rgb(0xbf, 0x88, 0x03);
        public Color Info { get; } = Rgb(0x1a, 0x85, 0xff);

        public Brush BrushOf(Color c, double opacity = 1)
        {
            var b = new SolidColorBrush(opacity < 1 ? System.Windows.Media.Color.FromArgb((byte)(c.A * opacity), c.R, c.G, c.B) : c);
            b.Freeze();
            return b;
        }

        public Pen PenOf(Color c, double width, double opacity = 1, double[] dashes = null)
        {
            var pen = new Pen(BrushOf(c, opacity), width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            if (dashes != null) pen.DashStyle = new DashStyle(Array.ConvertAll(dashes, d => d / width), 0);
            pen.Freeze();
            return pen;
        }

        private static Color ThemeColor(ThemeResourceKey key, Color fallback)
        {
            try
            {
                var c = VSColorTheme.GetThemedColor(key);
                return System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static Color Rgb(byte r, byte g, byte b) => System.Windows.Media.Color.FromRgb(r, g, b);

        /// <summary><paramref name="a"/> moved towards <paramref name="b"/> by <paramref name="k"/> (0..1).</summary>
        public static Color Mix(Color a, Color b, double k) => System.Windows.Media.Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * k),
            (byte)Math.Round(a.G + (b.G - a.G) * k),
            (byte)Math.Round(a.B + (b.B - a.B) * k));

        /// <summary>A diagram color of the model (<c>#rrggbb</c>), or null when there is none.</summary>
        public static Color? ColorOf(string hex)
        {
            if (FsmEditor.Core.ElementColors.Of(hex) == null) return null;
            var (r, g, b) = FsmEditor.Core.ElementColors.Rgb(hex);
            return Rgb((byte)r, (byte)g, (byte)b);
        }

        /// <summary>Fill of a colored shape: a light tint of its color over the shape background.</summary>
        public Color Tint(Color c, Color background) => Mix(background, c, FsmEditor.Core.ElementColors.TintAmount);

        private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

        /// <summary>Applies the themed Visual Studio style of a control, when the shell provides one.</summary>
        public static void Style(FrameworkElement element, object key)
        {
            if (key != null) element.SetResourceReference(FrameworkElement.StyleProperty, key);
        }

        public static void AssertUiThread() => ThreadHelper.ThrowIfNotOnUIThread();
    }
}
