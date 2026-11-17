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
    /// Converting files of earlier versions (XMI, <see cref="Xmi"/>) to the JSON format (<see cref="Format"/>). Machines
    /// linked by submachine references are converted together: a connection point reference names its point by xmi:id in
    /// the other file, and becomes that point's name, which the conversion of the other file may give or change. A port
    /// of the VS Code edition's <c>src/convert.ts</c>.
    ///
    /// Names: unnamed elements get generated names (<c>choice1*</c>, hidden on the diagram), spaces, <c>*</c> and
    /// <c>.</c> in names become <c>_</c>, a name used twice gets a number (<c>Idle2</c>). What the JSON format can't hold
    /// is left out and reported.
    /// </summary>
    public static class Conversion
    {
        private static string Label(VertexType t)
        {
            switch (t)
            {
                case VertexType.Initial: return "initial pseudostate";
                case VertexType.ShallowHistory: return "shallow history";
                case VertexType.DeepHistory: return "deep history";
                case VertexType.Choice: return "choice";
                case VertexType.Junction: return "junction";
                case VertexType.Final: return "final state";
                case VertexType.Terminate: return "terminate pseudostate";
                case VertexType.EntryPoint: return "entry point";
                case VertexType.ExitPoint: return "exit point";
                default: return t.Key();
            }
        }

        /// <summary>Converts old files together (a machine and the machines linked to it by submachine references).</summary>
        public static List<Converted> ConvertGroup(IReadOnlyList<ConvertInput> inputs)
        {
            var read = new List<(string Path, XmiModel M)>();
            var results = new List<Converted>();
            foreach (var i in inputs)
            {
                try
                {
                    read.Add((i.Path, Xmi.Read(i.Text)));
                }
                catch (Exception e)
                {
                    results.Add(new Converted { Path = i.Path, Error = e.Message });
                }
            }
            XmiModel Find(string path) => read.FirstOrDefault(r => r.Path == path).M;
            // Names first, in every file: the points' new names are what the references become.
            var named = new Dictionary<string, (List<string> Renames, int Generated, List<string> Losses)>();
            foreach (var (path, m) in read)
            {
                var losses = OldOnly(m);
                var unnamed = CountUnnamed(m.Model);
                var renames = Format.AssignNames(m.Model, true).Select(r => $"{r.What} '{r.From}' is now '{r.To}'{(r.Why != null ? $" ({r.Why})" : "")}").ToList();
                named[path] = (renames, unnamed, losses);
            }
            foreach (var (path, m) in read)
            {
                var info = named[path];
                foreach (var v in m.Model.Vertices)
                {
                    if (v.Type != VertexType.State || v.Submachine.Length == 0) continue;
                    var hash = v.Submachine.IndexOf('#');
                    var file = hash >= 0 ? v.Submachine.Substring(0, hash) : v.Submachine;
                    var id = hash >= 0 ? v.Submachine.Substring(hash + 1).Split('#')[0] : "";
                    var subPath = SlashPaths.Join(SlashPaths.Dirname(path), Hrefs.DecodeUri(file));
                    var sub = Find(subPath);
                    var found = sub != null && (id.Length == 0 || sub.XmiId == id) ? sub : null;
                    v.Submachine = Hrefs.DecodeUri(file);
                    foreach (var r in m.Model.Vertices)
                    {
                        if (r.Type != VertexType.ConnectionPointRef || r.Parent != v.Id) continue;
                        var point = found?.Model.Vertices.FirstOrDefault(p => p.Id == r.Ref && (p.Type == VertexType.EntryPoint || p.Type == VertexType.ExitPoint));
                        if (point != null) r.Ref = point.Name;
                        else if (r.Ref.Length > 0) info.Losses.Add($"the connection point reference of state '{v.Name}' to point '{r.Ref}' of {file}, which was not found: it keeps that id as the point's name");
                    }
                }
            }
            foreach (var (path, m) in read)
            {
                var info = named[path];
                var w = Format.WriteMachine(m.Model);
                results.Add(new Converted
                {
                    Path = path, Model = m.Model, ModelText = w.Model, LayoutText = w.Layout, Renames = info.Renames, Generated = info.Generated,
                    Losses = info.Losses.Concat(w.Lost).ToList(),
                });
            }
            return inputs.Select(i => results.First(r => r.Path == i.Path)).ToList();
        }

        /// <summary>Elements that need a name and have none.</summary>
        private static int CountUnnamed(FsmModel m)
        {
            var namedTypes = new HashSet<VertexType>
            {
                VertexType.State, VertexType.Choice, VertexType.Junction, VertexType.Fork, VertexType.Join, VertexType.Final,
                VertexType.Terminate, VertexType.Comment, VertexType.EntryPoint, VertexType.ExitPoint,
            };
            var n = m.Vertices.Count(v => namedTypes.Contains(v.Type) && v.Name.Length == 0);
            foreach (var regions in new[] { m.Regions }.Concat(m.Vertices.Select(v => v.Regions)))
            {
                if (regions.Count > 1) n += regions.Count(r => r.Name.Length == 0);
            }
            return n;
        }

        /// <summary>What an old file holds that the JSON format has no place for, besides what the writer reports.</summary>
        private static List<string> OldOnly(XmiModel x)
        {
            var m = x.Model;
            var output = new List<string>();
            string Describe(Vertex v) => $"the {Label(v.Type)}{(v.Name.Length > 0 ? $" '{v.Name}'" : "")}";
            bool Pseudo(Vertex v) => v.Type == VertexType.Initial || v.Type == VertexType.ShallowHistory || v.Type == VertexType.DeepHistory;
            if (x.Context.Length > 0) output.Add($"the context '{x.Context}' of the state machine");
            if (m.Regions.Count == 1 && m.Regions[0].Name.Length > 0) output.Add($"the name '{m.Regions[0].Name}' of the state machine's region");
            foreach (var v in m.Vertices)
            {
                if (v.Type == VertexType.State && v.Regions.Count == 1 && v.Regions[0].Name.Length > 0) output.Add($"the name '{v.Regions[0].Name}' of the region of state '{v.Name}'");
                if (Pseudo(v) && v.Name.Length > 0) output.Add($"the name of {Describe(v)}");
                if (Pseudo(v) && (ElementColors.IsColor(v.Color) || v.ItemColors.Count > 0)) output.Add($"the color of {Describe(v)}");
                if (!Format.IsResizable(v.Type) && v.Type != VertexType.Comment)
                {
                    var (w, h) = Format.DefaultSize(v.Type);
                    if (Math.Abs(v.W - w) > 0.05 || Math.Abs(v.H - h) > 0.05)
                    {
                        output.Add($"the size of {Describe(v)} ({Num.Js(v.W)} × {Num.Js(v.H)}): it is drawn at its default size, around the same center");
                        v.X += (v.W - w) / 2;
                        v.Y += (v.H - h) / 2;
                        v.W = w;
                        v.H = h;
                    }
                }
            }
            return output;
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
