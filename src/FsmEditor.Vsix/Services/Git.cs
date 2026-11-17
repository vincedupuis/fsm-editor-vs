using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using FsmEditor.Core;

namespace FsmEditor.Services
{
    /// <summary>
    /// The few things the diagram diff and merge ask of Git: a file's text at a version, the common version of a file
    /// with merge conflicts, marking files resolved. Runs git (on the PATH, else the one Visual Studio installs); without
    /// it, or outside a repository, each gives nothing and the views do without.
    /// </summary>
    internal static class Git
    {
        private static string _exe;

        private static string Exe
        {
            get
            {
                if (_exe != null) return _exe;
                var bundled = "";
                try
                {
                    var ide = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                    bundled = Path.Combine(ide, "CommonExtensions", "Microsoft", "TeamFoundation", "Team Explorer", "Git", "cmd", "git.exe");
                }
                catch (Exception e) when (e is Win32Exception || e is InvalidOperationException || e is NotSupportedException)
                {
                    // the PATH's
                }
                return _exe = File.Exists(bundled) ? bundled : "git";
            }
        }

        /// <summary>Runs git in <paramref name="dir"/>; its output, or null when it fails.</summary>
        private static string Run(string dir, params string[] args)
        {
            try
            {
                var info = new ProcessStartInfo(Exe)
                {
                    Arguments = string.Join(" ", args.Select(Quote)),
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                };
                using (var p = new Process { StartInfo = info })
                {
                    // Its errors are drained as they come, so that a full pipe never blocks it.
                    p.ErrorDataReceived += (s, e) => { };
                    p.Start();
                    p.BeginErrorReadLine();
                    var stdout = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(15000)) return null;
                    return p.ExitCode == 0 ? stdout : null;
                }
            }
            catch (Exception e) when (e is Win32Exception || e is InvalidOperationException || e is IOException)
            {
                return null;
            }
        }

        private static string Quote(string a) => a.Length > 0 && a.IndexOfAny(new[] { ' ', '"', '\t' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

        /// <summary>The root of the repository holding <paramref name="file"/>, or null.</summary>
        public static string RootOf(string file)
        {
            var root = Run(Path.GetDirectoryName(file), "rev-parse", "--show-toplevel")?.Trim();
            return string.IsNullOrEmpty(root) ? null : Path.GetFullPath(root);
        }

        private static string Relative(string root, string file) => Path.GetFullPath(file).Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');

        /// <summary>The text of <paramref name="file"/> at <paramref name="rev"/> (<c>HEAD</c>, <c>:1</c> the common version of a merge…), or null.</summary>
        public static string Show(string file, string rev)
        {
            var root = RootOf(file);
            if (root == null) return null;
            var spec = rev.StartsWith(":", StringComparison.Ordinal) ? $"{rev}:{Relative(root, file)}" : $"{rev}:{Relative(root, file)}";
            return Run(root, "show", spec);
        }

        /// <summary>The common version of a file with merge conflicts, and of its layout file, when Git gives it.</summary>
        public static MachineTexts CommonVersion(string file)
        {
            var model = Show(file, ":1");
            return model == null ? null : new MachineTexts(model, Show(MachineFiles.LayoutPathOf(file), ":1"));
        }

        /// <summary>Marks files resolved (stages them); false when Git couldn't.</summary>
        public static bool Add(params string[] files)
        {
            var root = RootOf(files[0]);
            if (root == null) return false;
            return Run(root, new[] { "add", "--" }.Concat(files.Where(File.Exists).Select(f => Relative(root, f))).ToArray()) != null;
        }
    }
}
