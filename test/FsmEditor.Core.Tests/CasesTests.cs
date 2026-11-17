using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FsmEditor.Core;
using Xunit;
using static FsmEditor.Core.Tests.TestFiles;

namespace FsmEditor.Core.Tests
{
    /// <summary>
    /// The reader and validator against fsm-cli's test cases (cases/, copied by scripts/fetch-cli.sh from
    /// ../fsm-cli/tests/cases): the same problems, at the same lines, with the same ids. fsm-cli's own messages about
    /// files it can't read (a missing submachine file, one that isn't valid) are its loader's, which the editor doesn't
    /// have: it reports such a submachine as not found, as fsm-cli does too. The VS Code edition's test/unit/cases.test.ts.
    /// </summary>
    public class CasesTests
    {
        private static readonly string[] Loader = { " does not exist or cannot be read.", " is not a valid state machine file." };

        public static IEnumerable<object[]> Folders() =>
            Directory.Exists(Cases)
                ? Directory.GetDirectories(Cases).Select(Path.GetFileName).Where(d => d != "layout").OrderBy(d => d).Select(d => new object[] { d })
                : new[] { new object[] { "(missing)" } };

        [Theory]
        [MemberData(nameof(Folders))]
        public void TheSameProblemsAsFsm(string folder)
        {
            Assert.True(Directory.Exists(Cases), "cases/ is missing: run scripts/fetch-cli.sh (it copies fsm-cli's tests/cases)");
            var dir = Path.Combine(Cases, folder);
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "expected.json")));
            var expected = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray()
                .Select(i => $"{i.GetProperty("line").GetInt32()} {i.GetProperty("id").GetString()} {i.GetProperty("severity").GetString()}: {i.GetProperty("message").GetString()}").ToList());
            var read = expected.Keys.ToDictionary(f => f, f => Format.ReadModel(File.ReadAllText(Path.Combine(dir, f))));
            var compared = 0;
            foreach (var kv in expected)
            {
                var r = read[kv.Key];
                List<string> found;
                if (r.Failed)
                {
                    found = r.Problems.Select(p => $"{p.Line}  error: {p.Message}").ToList();
                }
                else
                {
                    // Submachines as the editor resolves them: the machine of a readable file in the folder, else not found.
                    var info = new Dictionary<string, SubmachineInfo>();
                    foreach (var v in r.Model.Vertices.Where(v => v.Type == VertexType.State && v.Submachine.Length > 0))
                    {
                        info[v.Submachine] = read.TryGetValue(v.Submachine, out var sub) && !sub.Failed
                            ? new SubmachineInfo { Found = true, Name = sub.Model.Name, Points = sub.Model.MachineConnectionPoints() }
                            : new SubmachineInfo { Found = false };
                    }
                    found = Validation.Validate(r.Model, info)
                        .Select(i => $"{(r.Lines.TryGetValue(i.Id, out var l) ? l : 1)} {i.Id} {i.Severity.ToString().ToLowerInvariant()}: {i.Message}").ToList();
                }
                var wanted = kv.Value.Where(i => !Loader.Any(i.EndsWith)).ToList();
                Assert.Equal(wanted, found);
                compared += wanted.Count;
            }
            Assert.True(compared > 0 || folder == "examples", $"{folder}: nothing compared");
        }

        /// <summary>
        /// The layout file is the editions' own (the VS Code edition's schemas/fsm-layout.schema.json, copied in the VSIX
        /// project's Schemas/): the reader takes the files of fixtures/layout/valid, the reference's, and the examples'
        /// layout files, and refuses those of fixtures/layout/invalid, as the schema does.
        /// </summary>
        [Fact]
        public void LayoutFilesTheSchemaAcceptsAreTakenTheOthersRefused()
        {
            var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "layout");
            foreach (var (dir, valid) in new[] { (Path.Combine(fixtures, "valid"), true), (Path.Combine(fixtures, "invalid"), false), (Path.Combine(AppContext.BaseDirectory, "examples"), true) })
            {
                var files = Directory.GetFiles(dir, "*.fsm.layout");
                Assert.NotEmpty(files);
                foreach (var f in files)
                {
                    string problem;
                    try
                    {
                        problem = Format.LayoutShapeProblem(Json.Parse(File.ReadAllText(f)));
                    }
                    catch (JsonSyntaxException e)
                    {
                        problem = e.Message;
                    }
                    Assert.True(problem == null == valid, $"{f}: {problem}");
                }
            }
        }

        [Fact]
        public void TheSchemasAreThere()
        {
            Assert.True(File.Exists(Path.Combine(Schemas, "fsm.schema.json")), "schemas/ is missing: run scripts/fetch-cli.sh");
        }
    }
}
