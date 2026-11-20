using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace FsmEditor.Core.Tests
{
    /// <summary>
    /// The Marketplace listing, checked before a release builds the VSIX: the limits the VSIX build and VsixPublisher
    /// enforce (<c>source.extension.vsixmanifest</c>), Help › About's name (<c>VSPackage.resx</c>), the overview's links
    /// and images, which must be absolute (the Marketplace resolves no relative link) and name files of this repo, and
    /// a name the VS Code edition's listing doesn't use (both are on the Visual Studio Marketplace; checked when
    /// ../fsm-editor-vscode is beside this repo).
    /// </summary>
    public class ListingTests
    {
        private const string Repo = "vincedupuis/fsm-editor-vs";

        /// <summary>The repo's root: the folder of FsmEditor.sln, above the test's output folder.</summary>
        private static readonly string Root = FindRoot();

        private static string FindRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "FsmEditor.sln"))) return dir.FullName;
            }
            throw new InvalidOperationException("FsmEditor.sln not found above " + AppContext.BaseDirectory);
        }

        private static string Vsix => Path.Combine(Root, "src", "FsmEditor.Vsix");

        private static readonly XNamespace Ns = "http://schemas.microsoft.com/developer/vsx-schema/2011";

        private static XElement Metadata() =>
            XDocument.Load(Path.Combine(Vsix, "source.extension.vsixmanifest")).Root!.Element(Ns + "Metadata")!;

        [Fact]
        public void TheManifestKeepsTheMarketplaceLimits()
        {
            var m = Metadata();
            var name = m.Element(Ns + "DisplayName")!.Value;
            var description = m.Element(Ns + "Description")!.Value;
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.True(name.Length <= 50, $"DisplayName has {name.Length} characters, at most 50");
            Assert.False(string.IsNullOrWhiteSpace(description));
            Assert.True(description.Length < 280, $"Description has {description.Length} characters, under 280");
            var tags = m.Element(Ns + "Tags")!.Value.Split(';');
            Assert.All(tags, t =>
            {
                Assert.False(string.IsNullOrWhiteSpace(t), "an empty tag");
                Assert.True(t.Length <= 50, $"tag \"{t}\" has {t.Length} characters, at most 50");
            });
            foreach (var file in new[] { "Icon", "License" })
            {
                var path = m.Element(Ns + file)!.Value;
                Assert.True(File.Exists(Path.Combine(Vsix, path.Replace('\\', Path.DirectorySeparatorChar))), $"{file} {path} exists");
            }
        }

        [Fact]
        public void HelpAboutNamesTheExtensionAsTheManifestDoes()
        {
            var resx = XDocument.Load(Path.Combine(Vsix, "VSPackage.resx")).Root!.Elements("data")
                .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);
            Assert.Equal(Metadata().Element(Ns + "DisplayName")!.Value, resx["111"]);
            Assert.False(string.IsNullOrWhiteSpace(resx["112"]), "a description");
        }

        [Fact]
        public void TheOverviewLinksResolve()
        {
            var publish = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "vs-publish.json"))).RootElement;
            var overview = Path.Combine(Root, publish.GetProperty("overview").GetString()!);
            Assert.True(File.Exists(overview), overview);
            Assert.Empty(BrokenLinks(overview));
        }

        [Fact]
        public void TheNameIsNotTheVsCodeEditions()
        {
            var package = Path.Combine(Root, "..", "fsm-editor-vscode", "package.json");
            if (!File.Exists(package)) return; // ../fsm-editor-vscode is not beside this repo
            var vscode = JsonDocument.Parse(File.ReadAllText(package)).RootElement.GetProperty("displayName").GetString()!;
            Assert.NotEqual(vscode.Trim().ToLowerInvariant(), Metadata().Element(Ns + "DisplayName")!.Value.Trim().ToLowerInvariant());
        }

        /// <summary>GitHub's anchor of a heading: lowercase, punctuation dropped, spaces as hyphens.</summary>
        private static string Slug(string heading) =>
            Regex.Replace(Regex.Replace(heading.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}\s_-]", ""), @"\s", "-");

        /// <summary>
        /// The problems of the links and images of the overview: every one absolute; images over https; those to this
        /// repo (its files on GitHub, raw or not) naming a file of it, and a heading of it when they have an anchor.
        /// </summary>
        private static List<string> BrokenLinks(string file)
        {
            var text = Regex.Replace(File.ReadAllText(file), "```[\\s\\S]*?```", "");
            var own = new Regex($@"^https://(?:github\.com/{Regex.Escape(Repo)}/blob/main|raw\.githubusercontent\.com/{Regex.Escape(Repo)}/main)/([^#?]+)(?:#(.+))?$");
            var problems = new List<string>();
            foreach (Match m in Regex.Matches(text, @"(!?)\[[^\]]*\]\(([^)\s]+)(?:\s+""[^""]*"")?\)|<img [^>]*src=""([^""]+)"""))
            {
                var image = m.Groups[1].Value == "!" || m.Groups[3].Success;
                var target = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[2].Value;
                if (target.StartsWith("mailto:")) continue;
                if (!target.StartsWith("https://") && !target.StartsWith("http://"))
                {
                    problems.Add($"{target}: relative, which the Marketplace doesn't resolve");
                    continue;
                }
                if (image && !target.StartsWith("https://")) problems.Add($"{target}: an image over http");
                var o = own.Match(target);
                if (!o.Success) continue;
                var path = Path.Combine(Root, Uri.UnescapeDataString(o.Groups[1].Value).Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    problems.Add($"{target}: no such file in this repo");
                    continue;
                }
                if (o.Groups[2].Success && path.EndsWith(".md"))
                {
                    var headings = Regex.Matches(File.ReadAllText(path), @"^#+\s+(.*)$", RegexOptions.Multiline).Select(h => Slug(h.Groups[1].Value));
                    if (!headings.Contains(o.Groups[2].Value)) problems.Add($"{target}: no heading #{o.Groups[2].Value}");
                }
            }
            return problems;
        }
    }
}
