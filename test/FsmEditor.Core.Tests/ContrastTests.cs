using System.Collections.Generic;
using Xunit;

namespace FsmEditor.Core.Tests
{
    /// <summary>
    /// The diagram's signal colors (the trace palette, the warning and the error colors, as <c>DiagramCanvas</c> draws
    /// them) made readable by <see cref="Contrast.Readable"/> on the backgrounds of the IDE's default themes: WCAG 4.5:1
    /// for text, 3:1 for marks. The Vsix's <c>Theme.Mark</c> and <c>Theme.TextOf</c> apply it.
    /// </summary>
    public class ContrastTests
    {
        private static (int, int, int) Rgb(string c) => ElementColors.Rgb(c);

        /// <summary>Background and text color of the tool windows of Visual Studio's and VS Code's default themes.</summary>
        public static IEnumerable<object[]> Themes() => new[]
        {
            new object[] { "VS Light", "#f5f5f5", "#1e1e1e" },
            new object[] { "VS Dark", "#1f1f1f", "#fafafa" },
            new object[] { "VS Blue", "#f6f6f6", "#1e1e1e" },
            new object[] { "VS Code Light Modern", "#ffffff", "#3b3b3b" },
            new object[] { "VS Code Dark Modern", "#1f1f1f", "#cccccc" },
            new object[] { "High contrast dark", "#000000", "#ffffff" },
            new object[] { "High contrast light", "#ffffff", "#292929" },
        };

        /// <summary>The trace palette, the warning and the error colors.</summary>
        private static readonly string[] Signals = { "#388a34", "#1a85ff", "#652d90", "#d18616", "#00a3a3", "#d16da8", "#bf8803", "#e51400" };

        [Theory]
        [MemberData(nameof(Themes))]
        public void MakesTheSignalColorsReadable(string theme, string background, string foreground)
        {
            foreach (var c in Signals)
            {
                foreach (var min in new[] { Contrast.Mark, Contrast.Text })
                {
                    // As Theme does it: against a state's fill (the background moved 5% towards the foreground), the
                    // stricter of the two; then readable on the background too.
                    var fill = Contrast.Mix(Rgb(background), Rgb(foreground), 0.05);
                    var r = Contrast.Readable(Rgb(c), fill, Rgb(foreground), min);
                    Assert.True(Contrast.Ratio(r, fill) >= min, $"{theme}: {c} at {min} on a state: {Contrast.Ratio(r, fill):F2}");
                    Assert.True(Contrast.Ratio(r, Rgb(background)) >= min, $"{theme}: {c} at {min}: {Contrast.Ratio(r, Rgb(background)):F2}");
                    // A color that already stands out is kept.
                    if (Contrast.Ratio(Rgb(c), fill) >= min) Assert.Equal(Rgb(c), r);
                }
            }
        }

        [Fact]
        public void MeasuresAsWcag()
        {
            Assert.Equal(21, Contrast.Ratio((0, 0, 0), (255, 255, 255)), 3);
            Assert.Equal(1, Contrast.Ratio((128, 128, 128), (128, 128, 128)), 3);
            // The values that showed the need, in VS Code's Light Modern: its chart green on white, its warning yellow on a state's fill.
            Assert.InRange(Contrast.Ratio(Rgb("#388a34"), (255, 255, 255)), 4.3, 4.4);
            Assert.InRange(Contrast.Ratio(Rgb("#bf8803"), Rgb("#f8f8f8")), 2.9, 3.0);
        }
    }
}
