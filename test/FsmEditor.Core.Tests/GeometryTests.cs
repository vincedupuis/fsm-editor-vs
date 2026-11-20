using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace FsmEditor.Core.Tests
{
    /// <summary>
    /// Geometry parity with the VS Code edition, the reference: <c>geometry/</c> is a copy of its
    /// <c>test/unit/fixtures/geometry</c> (machines and the SVG its webview exports for them, text measured with
    /// <see cref="Geometry.EstimateLabelWidth"/>). After a geometry change there, copy the folder here and port the
    /// change until this passes.
    /// </summary>
    public class GeometryTests
    {
        private static string Dir => Path.Combine(AppContext.BaseDirectory, "geometry");

        [Fact]
        public void DrawsTheGeometryFixturesAsTheReferenceSvgs()
        {
            var names = Directory.GetFiles(Dir, "*.fsm").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.True(names.Count >= 20, $"{names.Count} fixtures in {Dir}");
            var failed = new List<string>();
            foreach (var name in names)
            {
                var file = Path.Combine(Dir, name);
                var session = new DiagramSession(Load.LoadModel(File.ReadAllText(file + ".fsm"), File.ReadAllText(file + ".fsm.layout")));
                var svg = SvgExport.Export(session);
                var reference = File.ReadAllText(file + ".svg").Replace("\r\n", "\n");
                if (svg != reference) failed.Add(name);
            }
            Assert.True(failed.Count == 0, "drawn differently from the VS Code edition: " + string.Join(", ", failed));
        }
    }
}
