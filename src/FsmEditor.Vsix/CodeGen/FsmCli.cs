using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FsmEditor.Core;

namespace FsmEditor.CodeGen
{
    /// <summary>A problem the generator reported (<c>file:line: severity: message</c> on stderr).</summary>
    internal sealed class CliIssue
    {
        public string File { get; set; } = "";
        /// <summary>One-based line, as printed.</summary>
        public int Line { get; set; }
        public Severity Severity { get; set; }
        public string Message { get; set; } = "";
    }

    internal sealed class CliResult
    {
        public int ExitCode { get; set; }
        public List<string> Output { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();
        public List<CliIssue> Issues { get; } = new List<CliIssue>();
        /// <summary>Files the generator wrote (full paths).</summary>
        public List<string> Written { get; } = new List<string>();
    }

    /// <summary>
    /// Runs the <c>fsm</c> command-line code generator of FSM Editor (the same
    /// program as in VS Code and CI): <c>fsm &lt;file&gt; --template &lt;hbs&gt; --out &lt;folder&gt;</c>,
    /// or, for Jinja templates, fsm-generator (the same generator in Rust), which takes the same
    /// arguments and prints the same lines. The extension bundles them in cli/ and generator/,
    /// each with its templates in a templates/ folder next to it.
    /// </summary>
    internal static class FsmCli
    {
        private static readonly Regex IssueLine = new Regex(@"^(.+?):(\d+): (error|warning|info): (.*)$");

        private static string ExtensionDir => Path.GetDirectoryName(typeof(FsmCli).Assembly.Location) ?? "";

        /// <summary>The bundled executable, next to the extension's assembly.</summary>
        public static string BundledPath => Path.Combine(ExtensionDir, "cli", "fsm.exe");

        /// <summary>The bundled fsm-generator, which runs the Jinja templates.</summary>
        public static string BundledGeneratorPath => Path.Combine(ExtensionDir, "generator", "fsm-generator.exe");

        /// <summary>Whether a template is a Jinja one, run by fsm-generator rather than fsm.</summary>
        public static bool IsJinja(string template) => template.EndsWith(".jinja", StringComparison.OrdinalIgnoreCase);

        /// <summary>The fsm executable to run: the configured one, the bundled one, or one on the PATH. Null when none is found.</summary>
        public static string Locate(string configured) => Locate(configured, BundledPath, "fsm.exe", "fsm.cmd");

        /// <summary>The fsm-generator executable to run, found like <see cref="Locate(string)"/>.</summary>
        public static string LocateGenerator(string configured) => Locate(configured, BundledGeneratorPath, "fsm-generator.exe");

        private static string Locate(string configured, string bundled, params string[] names)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var p = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
                return File.Exists(p) ? p : null;
            }
            if (File.Exists(bundled)) return bundled;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                foreach (var name in names)
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim('"'), name);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch (ArgumentException)
                    {
                        // malformed PATH entry
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Templates shipped with an executable (templates/*.hbs next to fsm, templates/*.jinja next to
        /// fsm-generator), where <c>-t &lt;name&gt;</c> finds them. Empty when <paramref name="cli"/> is null.
        /// </summary>
        public static List<string> BundledTemplates(string cli, string pattern = "*.hbs")
        {
            if (cli == null) return new List<string>();
            var dir = Path.Combine(Path.GetDirectoryName(cli) ?? "", "templates");
            return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
        }

        /// <summary>Quotes one argument for the Windows command line.</summary>
        public static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            return sb.Append('"').ToString();
        }

        public static string CommandLine(string cli, IEnumerable<string> args) => string.Join(" ", new[] { Quote(cli) }.Concat(args.Select(Quote)));

        public static async Task<CliResult> RunAsync(string cli, IReadOnlyList<string> args, string workingDirectory)
        {
            var result = new CliResult();
            var start = new ProcessStartInfo
            {
                FileName = cli,
                Arguments = string.Join(" ", args.Select(Quote)),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using (var process = new Process { StartInfo = start, EnableRaisingEvents = true })
            {
                var exited = new TaskCompletionSource<int>();
                process.Exited += (s, e) => exited.TrySetResult(0);
                process.Start();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(stdout, stderr, exited.Task).ConfigureAwait(false);
                process.WaitForExit();
                result.ExitCode = process.ExitCode;
                result.Output.AddRange(Lines(await stdout.ConfigureAwait(false)));
                result.Errors.AddRange(Lines(await stderr.ConfigureAwait(false)));
            }
            foreach (var line in result.Output)
            {
                if (line.StartsWith("wrote ", StringComparison.Ordinal)) result.Written.Add(Resolve(workingDirectory, line.Substring(6)));
            }
            foreach (var line in result.Errors)
            {
                var m = IssueLine.Match(line);
                if (!m.Success) continue;
                result.Issues.Add(new CliIssue
                {
                    File = Resolve(workingDirectory, m.Groups[1].Value),
                    Line = int.Parse(m.Groups[2].Value),
                    Severity = m.Groups[3].Value == "error" ? Severity.Error : m.Groups[3].Value == "warning" ? Severity.Warning : Severity.Info,
                    Message = m.Groups[4].Value,
                });
            }
            return result;
        }

        private static IEnumerable<string> Lines(string text) =>
            text.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0);

        private static string Resolve(string dir, string path)
        {
            try
            {
                return Path.GetFullPath(Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException)
            {
                return path;
            }
        }

        /// <summary>
        /// Arguments given to the Generate Code command (from the Command Window or a
        /// key binding), in the CLI's syntax: <c>-t &lt;template&gt; -o &lt;folder&gt;</c>.
        /// </summary>
        public static (string Template, string Out) ParseCommandArgs(string text)
        {
            string template = null, output = null;
            var tokens = Tokenize(text ?? "");
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                string Value() => i + 1 < tokens.Count ? tokens[++i] : null;
                if (t == "-t" || t == "--template") template = Value();
                else if (t == "-o" || t == "--out") output = Value();
                else if (t.StartsWith("--template=", StringComparison.Ordinal)) template = t.Substring(11);
                else if (t.StartsWith("--out=", StringComparison.Ordinal)) output = t.Substring(6);
            }
            return (template, output);
        }

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            var cur = new StringBuilder();
            var quoted = false;
            var any = false;
            foreach (var c in text)
            {
                if (c == '"')
                {
                    quoted = !quoted;
                    any = true;
                }
                else if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (any) tokens.Add(cur.ToString());
                    cur.Clear();
                    any = false;
                }
                else
                {
                    cur.Append(c);
                    any = true;
                }
            }
            if (any) tokens.Add(cur.ToString());
            return tokens;
        }
    }
}
