using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FsmEditor.Core.Tests
{
    /// <summary>
    /// The tests inside Visual Studio (test/FsmEditor.IntegrationTests) against the scenarios every edition's IDE tests
    /// check: its <c>scenarios.md</c>, a copy of the VS Code edition's <c>test/scenarios.md</c>. Each scenario (a
    /// <c>###</c> heading) is implemented by a test naming it (<c>[Trait("Scenario", "&lt;id&gt;")]</c>) or listed under
    /// "Not applicable" for Visual Studio; a test names only scenarios of the list. Read from the sources, so it runs
    /// anywhere, without Visual Studio.
    /// </summary>
    public class ScenarioTests
    {
        private static readonly string Tests = Path.Combine(FindRoot(), "test", "FsmEditor.IntegrationTests");

        private static string FindRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "FsmEditor.sln"))) return dir.FullName;
            }
            throw new InvalidOperationException("FsmEditor.sln not found above " + AppContext.BaseDirectory);
        }

        private static string[] Lines() => File.ReadAllLines(Path.Combine(Tests, "scenarios.md"));

        /// <summary>The scenarios' ids: the <c>###</c> headings.</summary>
        private static string[] Ids() => Lines().Select(l => Regex.Match(l, @"^###\s+(\S+)\s*$")).Where(m => m.Success).Select(m => m.Groups[1].Value).ToArray();

        /// <summary>The scenarios of the "Not applicable" table's rows for Visual Studio.</summary>
        private static string[] NotApplicable() => Lines()
            .SkipWhile(l => !Regex.IsMatch(l, @"^##\s+Not applicable\s*$"))
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("|", StringComparison.Ordinal))
            .Select(l => l.Trim('|').Split('|').Select(c => c.Trim()).ToArray())
            .Where(c => c.Length >= 2 && c[1] == "Visual Studio")
            .Select(c => c[0].Trim('`'))
            .ToArray();

        /// <summary>The scenarios the tests name.</summary>
        private static string[] Implemented() => Directory.GetFiles(Tests, "*.cs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\[Trait\(\s*""Scenario""\s*,\s*""([^""]+)""\s*\)\]").Cast<Match>())
            .Select(m => m.Groups[1].Value)
            .ToArray();

        [Fact]
        public void TheListHasScenarios() => Assert.NotEmpty(Ids());

        [Fact]
        public void EveryScenarioIsImplementedOrNotApplicable()
        {
            var done = Implemented().Concat(NotApplicable()).ToHashSet();
            var missing = Ids().Where(id => !done.Contains(id)).ToList();
            Assert.True(missing.Count == 0, "scenarios neither implemented by a test of FsmEditor.IntegrationTests nor not applicable to Visual Studio: " + string.Join(", ", missing));
        }

        [Fact]
        public void TestsNameOnlyScenariosOfTheList()
        {
            var ids = Ids().ToHashSet();
            var unknown = Implemented().Where(id => !ids.Contains(id)).Distinct().ToList();
            Assert.True(unknown.Count == 0, "tests name scenarios that scenarios.md doesn't list: " + string.Join(", ", unknown));
        }
    }
}
