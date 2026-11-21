using System;

namespace FsmEditor.Core
{
    /// <summary>
    /// Contrast of the diagram's signal colors (problems, the Trace Player) with the theme's background, as WCAG 2.1
    /// measures it: at least 4.5:1 for text (1.4.3), 3:1 for the lines and shapes that carry meaning (1.4.11). The
    /// colors are fixed, the themes aren't: a color too close to the background moves towards the foreground until it
    /// is readable, as the VS Code edition does for light themes.
    /// </summary>
    public static class Contrast
    {
        /// <summary>Text, as a transition's label in a trace color.</summary>
        public const double Text = 4.5;

        /// <summary>Lines and shapes that carry meaning: problem and trace marks.</summary>
        public const double Mark = 3;

        /// <summary>The contrast ratio of two colors, from 1 to 21.</summary>
        public static double Ratio((int R, int G, int B) a, (int R, int G, int B) b)
        {
            var la = Luminance(a);
            var lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        /// <summary>Relative luminance (WCAG 2.1).</summary>
        public static double Luminance((int R, int G, int B) c)
        {
            double Channel(int v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        /// <summary>
        /// <paramref name="color"/> when it stands out from <paramref name="background"/> by <paramref name="min"/>, else
        /// that color moved towards <paramref name="foreground"/> (by steps of 5%) until it does, or the foreground.
        /// </summary>
        public static (int R, int G, int B) Readable((int R, int G, int B) color, (int R, int G, int B) background, (int R, int G, int B) foreground, double min)
        {
            for (var k = 0; k <= 20; k++)
            {
                var c = Mix(color, foreground, k / 20.0);
                if (Ratio(c, background) >= min) return c;
            }
            return foreground;
        }

        /// <summary><paramref name="a"/> moved towards <paramref name="b"/> by <paramref name="k"/> (0..1).</summary>
        public static (int R, int G, int B) Mix((int R, int G, int B) a, (int R, int G, int B) b, double k) =>
            ((int)Math.Round(a.R + (b.R - a.R) * k), (int)Math.Round(a.G + (b.G - a.G) * k), (int)Math.Round(a.B + (b.B - a.B) * k));
    }
}
