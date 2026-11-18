using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FsmEditor.Core
{
    /// <summary>A text with conflict markers, split into its two sides.</summary>
    public sealed class ConflictSplit
    {
        /// <summary>The text with the current side of every conflict ("ours").</summary>
        public string Current { get; set; } = "";
        /// <summary>The text with the incoming side of every conflict ("theirs").</summary>
        public string Incoming { get; set; } = "";
        /// <summary>How many conflicts.</summary>
        public int Conflicts { get; set; }
    }

    public sealed class ConflictMarkerException : Exception
    {
        public ConflictMarkerException(string message) : base(message) { }
    }

    /// <summary>
    /// The diagram merge (three panes): splitting a file with conflict markers into its two sides, and taking one side's
    /// version of an element into the result, fsm-editor-common's (<see cref="Common"/>).
    /// </summary>
    public static class Merge
    {
        private static readonly Regex Start = new Regex(@"^<{7}(?: |\r?$)", RegexOptions.Multiline);
        private static readonly Regex End = new Regex(@"^>{7}(?: |\r?$)", RegexOptions.Multiline);

        /// <summary>Whether a text holds Git's conflict markers.</summary>
        public static bool HasConflictMarkers(string text) => text != null && Start.IsMatch(text) && End.IsMatch(text);

        /// <summary>
        /// Splits a text with conflict markers into its two sides: <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> current
        /// <c>=======</c> incoming <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>, and the three-part style, whose base section
        /// (<c>|||||||</c>) is left out. Throws <see cref="ConflictMarkerException"/> when the markers don't nest.
        /// </summary>
        public static ConflictSplit SplitConflicts(string text)
        {
            var r = (Dictionary<string, object>)Common.Call("splitConflicts", Common.Args(("text", Common.Str(text ?? ""))));
            if (r.TryGetValue("error", out var error)) throw new ConflictMarkerException(error as string ?? "");
            return new ConflictSplit { Current = ModelJson.Str(r, "current"), Incoming = ModelJson.Str(r, "incoming"), Conflicts = (int)ModelJson.Num(r, "conflicts") };
        }

        /// <summary>
        /// The result with the element of <paramref name="keys"/> (see <see cref="Compare"/>: its key in either version)
        /// as <paramref name="source"/> has it: updated, added (with what it contains), or removed.
        /// </summary>
        public static FsmModel Adopt(FsmModel result, FsmModel source, params string[] keys)
        {
            var list = "[" + string.Join(",", keys.Select(Common.Str)) + "]";
            return ModelJson.Read(Common.Call("adopt", Common.Args(("result", ModelJson.Write(result)), ("source", ModelJson.Write(source)), ("keys", list))));
        }
    }
}
