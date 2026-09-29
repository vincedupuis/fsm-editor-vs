using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FsmEditor.Core
{
    /// <summary>The text shown on transitions and in state compartments, and parsing it back.</summary>
    public static class Labels
    {
        /// <summary><c>t1, t2 [guard] / effect</c>, or <c>[pre] t / [post]</c> for protocol machines.</summary>
        public static string TransitionLabel(FsmModel model, Transition t)
        {
            var trig = string.Join(", ", t.Triggers);
            string s;
            if (model.Kind == MachineKind.Protocol)
            {
                s = "";
                if (t.Precondition.Length > 0) s += $"[{t.Precondition}] ";
                s += trig;
                if (t.Postcondition.Length > 0) s += $" / [{t.Postcondition}]";
                return s.Trim();
            }
            s = trig;
            if (t.Guard.Length > 0) s += $" [{t.Guard}]";
            if (t.Effect.Length > 0) s += $" / {t.Effect}";
            return s.Trim();
        }

        /// <summary>May transitions leaving <paramref name="source"/> use the [else] guard?</summary>
        public static bool AllowsElse(Vertex source) =>
            source != null && (source.Type == VertexType.Choice || source.Type == VertexType.Junction);

        private static readonly Regex ProtocolLabel = new Regex(@"^\s*(?:\[([^\]]*)\])?([^/]*?)\s*(?:/\s*\[([^\]]*)\])?\s*$");
        private static readonly Regex TrailingGuard = new Regex(@"\[([^\]]*)\]\s*$");

        /// <summary>
        /// Parses a transition label into <paramref name="t"/>. Returns an error
        /// message and leaves <paramref name="t"/> untouched when any part breaks the grammar.
        /// </summary>
        public static string ApplyLabel(FsmModel model, Transition t, string text, Vertex source)
        {
            text = text ?? "";
            if (model.Kind == MachineKind.Protocol)
            {
                var m = ProtocolLabel.Match(text);
                if (!m.Success) return "Write the label as [precondition] event / [postcondition].";
                var pre = Expressions.CheckCondition(m.Groups[1].Value);
                if (!pre.Ok) return $"Precondition: {pre.Error}";
                var ptrig = Expressions.CheckList(m.Groups[2].Value, Expressions.CheckTrigger);
                if (!ptrig.Ok) return $"Trigger: {ptrig.Error}";
                var post = Expressions.CheckCondition(m.Groups[3].Value);
                if (!post.Ok) return $"Postcondition: {post.Error}";
                t.Precondition = pre.Value;
                t.Triggers = ptrig.Value;
                t.Postcondition = post.Value;
                return null;
            }
            var slash = IndexOutsideBrackets(text, '/');
            var head = slash >= 0 ? text.Substring(0, slash) : text;
            var g = TrailingGuard.Match(head);
            var trig = Expressions.CheckList(g.Success ? head.Substring(0, g.Index) : head, Expressions.CheckTrigger);
            if (!trig.Ok) return $"Trigger: {trig.Error}";
            var guard = Expressions.CheckGuard(g.Success ? g.Groups[1].Value : "", AllowsElse(source));
            if (!guard.Ok) return $"Guard: {guard.Error}";
            var effect = Expressions.CheckActions(slash >= 0 ? text.Substring(slash + 1) : "");
            if (!effect.Ok) return $"Effect: {effect.Error}";
            t.Triggers = trig.Value;
            t.Guard = guard.Value;
            t.Effect = effect.Value;
            return null;
        }

        private static int IndexOutsideBrackets(string s, char ch)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '[') depth++;
                else if (s[i] == ']') depth = depth > 0 ? depth - 1 : 0;
                else if (s[i] == ch && depth == 0) return i;
            }
            return -1;
        }

        public static IEnumerable<Transition> InternalTransitions(FsmModel model, Vertex v) =>
            model.Transitions.Where(t => t.Kind == TransitionKind.Internal && t.Source == v.Id && t.Target == v.Id);

        /// <summary>Lines of the internal activities compartment of a state.</summary>
        public static List<string> ActivityLines(FsmModel model, Vertex v)
        {
            var output = new List<string>();
            if (v.Entry.Length > 0) output.Add($"entry / {v.Entry}");
            if (v.Exit.Length > 0) output.Add($"exit / {v.Exit}");
            if (v.DoActivity.Length > 0) output.Add($"do / {v.DoActivity}");
            foreach (var d in v.Deferrable) output.Add($"{d} / defer");
            foreach (var t in InternalTransitions(model, v))
            {
                var label = TransitionLabel(model, t);
                output.Add(label.Length > 0 ? label : "(internal)");
            }
            return output;
        }
    }
}
