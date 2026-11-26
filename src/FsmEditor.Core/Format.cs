using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FsmEditor.Core
{
    /// <summary>A problem of a file's text or shape, with its line.</summary>
    public sealed class Problem
    {
        public Problem(int line, string message)
        {
            Line = line;
            Message = message;
        }

        public int Line { get; }
        public string Message { get; }

        public override string ToString() => $"line {Line}: {Message}";
    }

    /// <summary>A machine read from its model file (and its layout file).</summary>
    public sealed class ReadMachine
    {
        /// <summary>The model, or null when the model file can't be read (see <see cref="Problems"/>).</summary>
        public FsmModel Model { get; set; }
        /// <summary>Line of each element, region, point and transition, by id.</summary>
        public Dictionary<string, int> Lines { get; set; } = new Dictionary<string, int>();
        /// <summary>Why the model file can't be read (not JSON, or a wrong shape), or null.</summary>
        public List<Problem> Problems { get; set; }
        /// <summary>Why the layout file couldn't be used (it is then left out entirely), or null.</summary>
        public string LayoutProblem { get; set; }
        /// <summary>How many elements had no layout entry and were placed automatically.</summary>
        public int Placed { get; set; }

        public bool Failed => Problems != null;
    }

    /// <summary>What writing a machine gives.</summary>
    public sealed class Written
    {
        /// <summary>The model file.</summary>
        public string Model { get; set; } = "";
        /// <summary>The layout file.</summary>
        public string Layout { get; set; } = "";
        /// <summary>Line of each vertex and transition in the model file, by id.</summary>
        public Dictionary<string, int> Lines { get; set; } = new Dictionary<string, int>();
        /// <summary>What the format can't hold, which was left out (an editor's model holds none of it).</summary>
        public List<string> Lost { get; set; } = new List<string>();
    }


    /// <summary>
    /// The file format: a machine is two JSON files, <c>&lt;Name&gt;.fsm</c> (the model, without ids: elements are
    /// identified by their names) and <c>&lt;Name&gt;.fsm.layout</c> beside it (the drawing). Reading and writing them
    /// are fsm-editor-common's (<see cref="Common"/>): the same files, problems, lines and derived ids in every edition.
    /// The editor works on the flat <see cref="FsmModel"/> with absolute coordinates.
    ///
    /// A reference that names nothing is kept as it is: a transition's target is then <see cref="Unresolved"/> followed by
    /// the reference, a comment's anchor <see cref="Unresolved"/> (a reference) or <see cref="UnresolvedAnchor"/> (a
    /// transition, as JSON) followed by it.
    /// </summary>
    public static class Format
    {
        public const string Unresolved = "";
        public const string UnresolvedAnchor = "";

        /// <summary>The reference of a target that names nothing ("" for an <c>initial</c> without <c>target</c>), or null.</summary>
        public static string UnresolvedOf(string target) => target != null && target.StartsWith(Unresolved, StringComparison.Ordinal) ? target.Substring(1) : null;

        /// <summary>Whether a text is a file in the old format (XMI).</summary>
        public static bool IsOldFormat(string text)
        {
            foreach (var c in text ?? "")
            {
                if (c == '<') return true;
                if (c != '﻿' && c != ' ' && c != '\t' && c != '\r' && c != '\n') return false;
            }
            return false;
        }

        /// <summary>The size of each kind of vertex when the layout has none (the editor's defaults).</summary>
        public static (double W, double H) DefaultSize(VertexType t) => t.DefaultSize();

        /// <summary>Kinds the editor lets the user resize: the layout holds their width and height.</summary>
        public static bool IsResizable(VertexType t) => t == VertexType.State || t == VertexType.Comment || t == VertexType.Fork || t == VertexType.Join;

        /// <summary>Whether a name is a generated one, not shown on the diagram.</summary>
        public static bool IsGenerated(string name) => name != null && name.EndsWith("*", StringComparison.Ordinal);

        /// <summary>A name as the diagram shows it: nothing for a generated one.</summary>
        public static string Shown(string name) => IsGenerated(name) ? "" : name ?? "";

        /// <summary>Reads a machine's two files (no layout file: <paramref name="layoutText"/> null).</summary>
        public static ReadMachine ReadMachine(string modelText, string layoutText)
        {
            var r = (Dictionary<string, object>)Common.Call("read", Common.Args(("model", Common.Str(modelText ?? "")), ("layout", Common.Str(layoutText))));
            if (r.TryGetValue("problems", out var problems))
            {
                return new ReadMachine
                {
                    Problems = ((List<object>)problems).OfType<Dictionary<string, object>>()
                        .Select(p => new Problem((int)ModelJson.Num(p, "line"), ModelJson.Str(p, "message"))).ToList(),
                };
            }
            return new ReadMachine
            {
                Model = ModelJson.Read(r["model"]),
                Lines = Lines(r),
                LayoutProblem = r.TryGetValue("layoutProblem", out var lp) ? lp as string : null,
                Placed = (int)ModelJson.Num(r, "placed"),
            };
        }

        /// <summary>Reads a model file alone (every element placed automatically).</summary>
        public static ReadMachine ReadModel(string text) => ReadMachine(text, null);

        /// <summary>
        /// Writes a machine into its two files, giving generated names to what has none and resolving the references
        /// that name something now; <see cref="Written.Lost"/> lists what the format can't hold (nothing is written then).
        /// The model written (names given, references resolved) is <see cref="Written.Model"/>'s, read back into
        /// <paramref name="model"/> when it changed.
        /// </summary>
        public static Written WriteMachine(FsmModel model)
        {
            var r = (Dictionary<string, object>)Common.Call("write", Common.Args(("model", ModelJson.Write(model))));
            if (r.TryGetValue("lost", out var lost)) return new Written { Lost = ((List<object>)lost).OfType<string>().ToList() };
            if (r.TryGetValue("model", out var changed))
            {
                var m = ModelJson.Read(changed);
                model.Name = m.Name;
                model.Kind = m.Kind;
                model.Context = m.Context;
                model.Documentation = m.Documentation;
                model.Regions = m.Regions;
                model.Vertices = m.Vertices;
                model.Transitions = m.Transitions;
            }
            return new Written { Model = ModelJson.Str(r, "modelText"), Layout = ModelJson.Str(r, "layoutText"), Lines = Lines(r) };
        }

        /// <summary>
        /// The ids reading <paramref name="model"/> back from its file would give (what fsm-cli calls the elements, in
        /// generated code's traces and in its problems), by the model's own ids.
        /// </summary>
        public static Dictionary<string, string> DerivedIds(FsmModel model)
        {
            var r = Common.Call("derivedIds", Common.Args(("model", ModelJson.Write(model)))) as Dictionary<string, object>;
            return r?.ToDictionary(kv => kv.Key, kv => kv.Value as string ?? "") ?? new Dictionary<string, string>();
        }

        private static Dictionary<string, int> Lines(Dictionary<string, object> r) =>
            r.TryGetValue("lines", out var lines) && lines is Dictionary<string, object> d
                ? d.ToDictionary(kv => kv.Key, kv => kv.Value is double n ? (int)n : 0)
                : new Dictionary<string, int>();
    }
}
