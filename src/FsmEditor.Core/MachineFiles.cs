using System;
using System.Collections.Generic;
using System.Linq;

namespace FsmEditor.Core
{
    /// <summary>The format can't hold an edit: what it would leave out.</summary>
    public sealed class FormatLossException : Exception
    {
        public FormatLossException(IReadOnlyList<string> lost) : base($"the file can't hold {string.Join("; ", lost)}.") => Lost = lost;

        public IReadOnlyList<string> Lost { get; }
    }

    /// <summary>A machine's two files as the diagram edits them, at one point of its history.</summary>
    public sealed class MachineState
    {
        /// <summary>The model file's text: what saving writes (the file's own text when it can't be read).</summary>
        public string ModelText { get; set; } = "";
        /// <summary>The layout file's text: what saving writes; null when there is none to write (no file, unreadable model).</summary>
        public string LayoutText { get; set; }
        /// <summary>The model the diagram works on; null when the model file can't be read.</summary>
        public FsmModel Model { get; set; }
        /// <summary>Why the model file can't be read: no diagram then.</summary>
        public List<Problem> Problems { get; set; }
        /// <summary>Line of each element in <see cref="ModelText"/>, by id.</summary>
        public Dictionary<string, int> Lines { get; set; } = new Dictionary<string, int>();
        /// <summary>Elements placed automatically when the files were read (no layout entry).</summary>
        public int Placed { get; set; }
        /// <summary>Why the layout file couldn't be read, or null.</summary>
        public string LayoutProblem { get; set; }

        /// <summary>The same texts (what saving would write).</summary>
        public bool SameFiles(MachineState other) => other != null && ModelText == other.ModelText && LayoutText == other.LayoutText;
    }

    /// <summary>
    /// The two files of a machine and the diagram's model: reading them into a state, writing an edit of the model
    /// into one, and finding the old files linked to one (they are converted together). The parts of the VS Code
    /// edition's <c>src/fsmEditorProvider.ts</c> that use no editor API, shared by the editions.
    /// </summary>
    public static class MachineFiles
    {
        /// <summary>The layout file of a model file.</summary>
        public static string LayoutPathOf(string path) => path + ".layout";

        /// <summary>The state of a document whose files hold these texts (a new-format model; an old one is converted first).</summary>
        public static MachineState StateOf(string modelText, string layoutText)
        {
            var read = Format.ReadMachine(modelText, layoutText);
            if (read.Failed) return new MachineState { ModelText = modelText, Problems = read.Problems };
            return new MachineState { ModelText = modelText, LayoutText = layoutText, Model = read.Model, Lines = read.Lines, Placed = read.Placed, LayoutProblem = read.LayoutProblem };
        }

        /// <summary>
        /// The state after an edit of the diagram: its model, written. The model is changed in place as saving it and
        /// reading it again would: generated names given to what has none, and references that named nothing and name
        /// something now resolved. Throws <see cref="FormatLossException"/> when the format can't hold it.
        /// </summary>
        public static MachineState Write(FsmModel model)
        {
            var w = Format.WriteMachine(model);
            if (w.Lost.Count > 0) throw new FormatLossException(w.Lost);
            return new MachineState { ModelText = w.Model, LayoutText = w.Layout, Model = model, Lines = w.Lines };
        }

        /// <summary>
        /// The old files (XMI) linked to <paramref name="path"/> by submachine references, transitively and both ways
        /// (the files it uses, the files using it), with their texts: they are converted together.
        /// <paramref name="others"/>: the workspace's old files (path and text), for the machines using this one;
        /// <paramref name="readText"/> reads a file by path (null when it can't).
        /// </summary>
        public static List<ConvertInput> LinkedOldFiles(string path, string text, IEnumerable<ConvertInput> others, Func<string, string> readText)
        {
            var found = new List<(string Path, string Text)> { (path, text) };
            bool Has(string p) => found.Any(f => f.Path == p);
            var known = others.Where(o => o.Path != path && Format.IsOldFormat(o.Text)).GroupBy(o => o.Path).Select(g => g.First()).ToList();
            List<string> Uses(string p, string t) => Load.SubmachineFilesOf(t).Select(f => SlashPaths.Join(SlashPaths.Dirname(p), f)).ToList();
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var (p, t) in found.ToList())
                {
                    foreach (var u in Uses(p, t))
                    {
                        if (Has(u)) continue;
                        var sub = known.FirstOrDefault(o => o.Path == u)?.Text ?? readText(u);
                        if (sub != null && Format.IsOldFormat(sub))
                        {
                            found.Add((u, sub));
                            changed = true;
                        }
                    }
                }
                foreach (var o in known)
                {
                    if (Has(o.Path)) continue;
                    if (Uses(o.Path, o.Text).Any(Has))
                    {
                        found.Add((o.Path, o.Text));
                        changed = true;
                    }
                }
            }
            return found.Select(f => new ConvertInput(f.Path, f.Text)).ToList();
        }

        /// <summary>The line of an element of an old file (XMI), found by its xmi:id; 1 when it isn't found.</summary>
        public static int OldLineOf(string text, string id)
        {
            var at = string.IsNullOrEmpty(id) ? -1 : text.IndexOf($"xmi:id=\"{id}\"", StringComparison.Ordinal);
            return at < 0 ? 1 : text.Substring(0, at).Count(c => c == '\n') + 1;
        }

        /// <summary>The message saying which files converting old files changed from XMI to JSON, and which it skipped.</summary>
        public static string ConversionMessage(IReadOnlyList<(string File, Converted Result)> converted, IReadOnlyList<string> skipped)
        {
            var message = converted.Count == 1
                ? $"{converted[0].File} is no longer saved in XMI but in JSON, in two separate files: {converted[0].File} and {converted[0].File}.layout."
                : $"{string.Join(", ", converted.Select(c => c.File))} are no longer saved in XMI but in JSON, each in two separate files: .fsm and .fsm.layout.";
            if (skipped.Count > 0) message += $" Still in XMI, as they have unsaved changes or can't be read: {string.Join(", ", skipped)}.";
            return message;
        }
    }
}
