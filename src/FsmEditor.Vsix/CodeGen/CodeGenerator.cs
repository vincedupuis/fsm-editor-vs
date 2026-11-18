using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FsmEditor.Core;

namespace FsmEditor.CodeGen
{
    /// <summary>A problem the generator reported (validation or the template), at a file's line.</summary>
    internal sealed class GenerateIssue
    {
        /// <summary>Full path of the .fsm file.</summary>
        public string File { get; set; } = "";
        /// <summary>One-based line.</summary>
        public int Line { get; set; }
        public Severity Severity { get; set; }
        public string Message { get; set; } = "";
    }

    internal sealed class GenerateResult
    {
        /// <summary>0 generated, 1 errors in the machines or the template, 2 a request it refuses (such as a .hbs template).</summary>
        public int ExitCode { get; set; }
        /// <summary>Why nothing was generated, or null.</summary>
        public string Error { get; set; }
        public List<GenerateIssue> Issues { get; } = new List<GenerateIssue>();
        /// <summary>Files written (full paths).</summary>
        public List<string> Written { get; } = new List<string>();
    }

    /// <summary>
    /// Code generation: fsm-cli's (its Jinja templates and code model), run by fsm-editor-common's sidecar (the same as
    /// in VS Code and in fsm-cli's <c>fsm write</c>). The extension bundles the sidecar in cli/, with fsm-cli's templates
    /// in cli/templates. The sidecar reads no files: the machine's text and those of the machines its submachine states
    /// use go in the request, with the template's text, and the files to write come back.
    /// </summary>
    internal static class CodeGenerator
    {
        private static string ExtensionDir => Path.GetDirectoryName(typeof(CodeGenerator).Assembly.Location) ?? "";

        /// <summary>The bundled sidecar, next to the extension's assembly.</summary>
        public static string CommonPath => Path.Combine(ExtensionDir, "cli", "fsm-editor-common.exe");

        /// <summary>The bundled templates (cli/templates/*.jinja), where a bare name (<c>-t ts</c>) finds them.</summary>
        public static List<string> BundledTemplates()
        {
            var dir = Path.Combine(ExtensionDir, "cli", "templates");
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jinja").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
        }

        /// <summary>A Windows path as the generator's paths are written: <c>/</c>-separated, absolute (<c>/C:/work/Order.fsm</c>).</summary>
        private static string Slash(string path)
        {
            var p = path.Replace('\\', '/');
            return p.StartsWith("/", StringComparison.Ordinal) ? p : "/" + p;
        }

        /// <summary>A path the generator displays (relative to <paramref name="dir"/>, or absolute) as a full Windows path.</summary>
        private static string Unslash(string dir, string path)
        {
            try
            {
                if (path.Length >= 3 && path[0] == '/' && path[2] == ':') path = path.Substring(1);
                return Path.GetFullPath(Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException)
            {
                return path;
            }
        }

        /// <summary>
        /// The texts of <paramref name="source"/> and of the machines its submachine states use, transitively, by
        /// generator path. A file that can't be read is left out: the generator reports it.
        /// </summary>
        private static Dictionary<string, string> ReadMachines(string source)
        {
            var files = new Dictionary<string, string>();
            var queue = new Queue<string>();
            queue.Enqueue(Path.GetFullPath(source));
            while (queue.Count > 0)
            {
                var path = queue.Dequeue();
                var key = Slash(path);
                if (files.ContainsKey(key)) continue;
                string text;
                try
                {
                    text = File.ReadAllText(path, Encoding.UTF8);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
                {
                    continue;
                }
                files[key] = text;
                foreach (var sub in Load.SubmachineFilesOf(text))
                {
                    if (sub.Length > 0) queue.Enqueue(Unslash(Path.GetDirectoryName(path) ?? "", sub));
                }
            }
            return files;
        }

        /// <summary>Generates code for <paramref name="source"/> with a template's file into <paramref name="output"/>.</summary>
        public static GenerateResult Generate(string source, string templatePath, string output)
        {
            var result = new GenerateResult();
            string template;
            try
            {
                template = File.ReadAllText(templatePath, Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                result.ExitCode = 2;
                result.Error = $"Cannot read the template {templatePath}: {e.Message}";
                return result;
            }
            var dir = Path.GetDirectoryName(Path.GetFullPath(source)) ?? "";
            var files = ReadMachines(source);
            var args = Common.Args(
                ("cwd", Common.Str(Slash(dir))),
                ("files", "{" + string.Join(",", files.Select(kv => ClipboardJson.Quote(kv.Key) + ":" + Common.Str(kv.Value))) + "}"),
                ("generate", "[" + Common.Str(Slash(Path.GetFullPath(source))) + "]"),
                ("template", Common.Str(template)),
                ("templateName", Common.Str(Path.GetFileName(templatePath))));
            var r = (Dictionary<string, object>)Common.Call("generate", args);
            result.ExitCode = (int)ModelJson.Num(r, "exitCode");
            result.Error = r.TryGetValue("error", out var error) ? error as string : null;
            foreach (var i in ModelJson.Objects(r, "issues"))
            {
                var severity = ModelJson.Str(i, "severity");
                result.Issues.Add(new GenerateIssue
                {
                    File = Unslash(dir, ModelJson.Str(i, "file")),
                    Line = (int)ModelJson.Num(i, "line"),
                    Severity = severity == "error" ? Severity.Error : severity == "warning" ? Severity.Warning : Severity.Info,
                    Message = ModelJson.Str(i, "message"),
                });
            }
            if (result.ExitCode != 0) return result;
            foreach (var f in ModelJson.Objects(r, "files"))
            {
                var target = Path.GetFullPath(Path.Combine(output, ModelJson.Str(f, "path").Replace('/', Path.DirectorySeparatorChar)));
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? output);
                File.WriteAllText(target, ModelJson.Str(f, "content"), new UTF8Encoding(false));
                result.Written.Add(target);
            }
            return result;
        }

        /// <summary>
        /// Arguments given to the Generate Code command (from the Command Window or a key binding), in the syntax of
        /// fsm-cli's <c>fsm write</c>: <c>-t &lt;template&gt; -o &lt;folder&gt;</c>.
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
