using System;
using System.Collections.Generic;
using System.Linq;

namespace FsmEditor.Core
{
    /// <summary>An old file to convert: its path (<c>/</c>-separated, absolute) and text.</summary>
    public sealed class ConvertInput
    {
        public ConvertInput(string path, string text)
        {
            Path = path;
            Text = text;
        }

        public string Path { get; }
        public string Text { get; }
    }

    public sealed class Converted
    {
        public string Path { get; set; } = "";
        /// <summary>The converted model (its ids are the old file's); null when the file couldn't be read.</summary>
        public FsmModel Model { get; set; }
        /// <summary>The texts of the two new files.</summary>
        public string ModelText { get; set; } = "";
        public string LayoutText { get; set; } = "";
        /// <summary>Names changed, as sentences.</summary>
        public List<string> Renames { get; set; } = new List<string>();
        /// <summary>How many unnamed elements got a generated name.</summary>
        public int Generated { get; set; }
        /// <summary>What was left out, as sentences.</summary>
        public List<string> Losses { get; set; } = new List<string>();
        /// <summary>Why the file could not be read (nothing else is set then).</summary>
        public string Error { get; set; }
    }

    /// <summary>
    /// Converting files of earlier versions (XMI) to the JSON format, fsm-editor-common's (<see cref="Common"/>). Machines
    /// linked by submachine references are converted together. Unnamed elements get generated names (<c>choice1*</c>),
    /// spaces, <c>*</c> and <c>.</c> in names become <c>_</c>, a name used twice gets a number (<c>Idle2</c>). What the
    /// JSON format can't hold is left out and reported.
    /// </summary>
    public static class Conversion
    {
        /// <summary>Converts old files together (a machine and the machines linked to it by submachine references).</summary>
        public static List<Converted> ConvertGroup(IReadOnlyList<ConvertInput> inputs)
        {
            var list = "[" + string.Join(",", inputs.Select(i => Common.Args(("path", Common.Str(i.Path)), ("text", Common.Str(i.Text))))) + "]";
            var r = (List<object>)Common.Call("convert", Common.Args(("inputs", list)));
            return r.OfType<Dictionary<string, object>>().Select(c => new Converted
            {
                Path = ModelJson.Str(c, "path"),
                Model = c.TryGetValue("model", out var m) ? ModelJson.Read(m) : null,
                ModelText = ModelJson.Str(c, "modelText"),
                LayoutText = ModelJson.Str(c, "layoutText"),
                Renames = ModelJson.Strings(c, "renames"),
                Generated = (int)ModelJson.Num(c, "generated"),
                Losses = ModelJson.Strings(c, "losses"),
                Error = c.TryGetValue("error", out var e) ? e as string : null,
            }).ToList();
        }
    }

    /// <summary>Paths as the VS Code edition handles them: absolute, <c>/</c>-separated.</summary>
    public static class SlashPaths
    {
        /// <summary>The directory of a path (<c>/</c> for one at the root).</summary>
        public static string Dirname(string p)
        {
            var i = p.LastIndexOf('/');
            return i <= 0 ? "/" : p.Substring(0, i);
        }

        /// <summary><paramref name="rel"/> resolved against the directory <paramref name="dir"/>, with <c>.</c> and <c>..</c> segments resolved.</summary>
        public static string Join(string dir, string rel)
        {
            var output = new List<string>();
            foreach (var s in (rel.StartsWith("/", StringComparison.Ordinal) ? rel : $"{dir}/{rel}").Split('/'))
            {
                if (s.Length == 0 || s == ".") continue;
                if (s == "..")
                {
                    if (output.Count > 0) output.RemoveAt(output.Count - 1);
                }
                else output.Add(s);
            }
            return "/" + string.Join("/", output);
        }
    }
}
