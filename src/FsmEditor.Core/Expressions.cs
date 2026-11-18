using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace FsmEditor.Core
{
    /// <summary>Result of checking a piece of text: the normalized value, or an error message.</summary>
    public readonly struct Check<T>
    {
        private Check(bool ok, T value, string error)
        {
            Ok = ok;
            Value = value;
            Error = error;
        }

        public bool Ok { get; }
        public T Value { get; }
        public string Error { get; }

        public static Check<T> Success(T value) => new Check<T>(true, value, null);
        public static Check<T> Fail(string error) => new Check<T>(false, default, error);
    }

    /// <summary>
    /// White space as JavaScript's <c>trim()</c> and regex <c>\s</c> see it (the reference
    /// edition's grammar): U+0009–U+000D, U+0020, U+00A0, U+1680, U+2000–U+200A, U+2028,
    /// U+2029, U+202F, U+205F, U+3000 and U+FEFF. <see cref="char.IsWhiteSpace(char)"/>
    /// differs (U+0085 in, U+FEFF out).
    /// </summary>
    public static class JsWhitespace
    {
        /// <summary>The set as a regex character class, in place of <c>\s</c>.</summary>
        public const string Class = @"[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]";

        /// <summary>Is <paramref name="c"/> white space for JavaScript?</summary>
        public static bool Contains(char c) =>
            (c >= '\u0009' && c <= '\u000D') || c == ' ' || c == '\u00A0' || c == '\u1680' || (c >= '\u2000' && c <= '\u200A')
            || c == '\u2028' || c == '\u2029' || c == '\u202F' || c == '\u205F' || c == '\u3000' || c == '\uFEFF';

        /// <summary>Like JavaScript's <c>String.prototype.trim()</c>; null gives "".</summary>
        public static string Trim(string text)
        {
            if (text == null) return "";
            int start = 0, end = text.Length;
            while (start < end && Contains(text[start])) start++;
            while (end > start && Contains(text[end - 1])) end--;
            return start == 0 && end == text.Length ? text : text.Substring(start, end - start);
        }

        /// <summary>Splits on runs of white space, dropping empty items, like <c>split(/\s+/).filter(Boolean)</c>.</summary>
        public static List<string> Split(string text)
        {
            var output = new List<string>();
            text = text ?? "";
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && Contains(text[i])) i++;
                int j = i;
                while (j < text.Length && !Contains(text[j])) j++;
                if (j > i) output.Add(text.Substring(i, j - i));
                i = j;
            }
            return output;
        }
    }

    /// <summary>
    /// The text grammar's checks (behaviors, conditions, triggers, events, names): fsm-cli's rules and messages, with the
    /// text an editor keeps for valid input, normalized; fsm-editor-common's <c>checkText</c> (<see cref="Common"/>).
    /// </summary>
    public static class Expressions
    {
        private static Check<string> Text(string kind, string text, bool allowElse = false)
        {
            var r = (Dictionary<string, object>)Common.Call("checkText", Common.Args(
                ("kind", Common.Str(kind)), ("text", Common.Str(text ?? "")), ("allowElse", allowElse ? "true" : "false")));
            return r.TryGetValue("ok", out var ok) && ok is bool b && b
                ? Check<string>.Success(r.TryGetValue("value", out var v) ? v as string ?? "" : "")
                : Check<string>.Fail(ModelJson.Str(r, "error"));
        }

        /// <summary>A condition built from calls, !, &amp;&amp; and ||. Empty text is allowed (no condition).</summary>
        public static Check<string> CheckCondition(string text) => Text("condition", text);

        /// <summary>A transition's guard: a condition, or <c>else</c> when <paramref name="allowElse"/> is set.</summary>
        public static Check<string> CheckGuard(string text, bool allowElse) => Text("guard", text, allowElse);

        /// <summary>A behavior: calls separated by <c>;</c>. Empty text is allowed (no behavior).</summary>
        public static Check<string> CheckActions(string text) => Text("actions", text);

        /// <summary>An event name (triggers and deferrable events).</summary>
        public static Check<string> CheckEvent(string text) => Text("event", text);

        /// <summary>A plain name (letters, digits and _), e.g. a stereotype.</summary>
        public static Check<string> CheckName(string text) => Text("name", text);

        /// <summary>One trigger: an event name, or after(&lt;number&gt;&lt;ms|s|m|h&gt;).</summary>
        public static Check<string> CheckTrigger(string text) => Text("trigger", text);

        /// <summary>A comma-separated list checked item by item; the normalized items.</summary>
        public static Check<List<string>> CheckList(string text, Func<string, Check<string>> check)
        {
            var output = new List<string>();
            foreach (var item in (text ?? "").Split(',').Select(JsWhitespace.Trim).Where(x => x.Length > 0))
            {
                var r = check(item);
                if (!r.Ok) return Check<List<string>>.Fail(r.Error);
                output.Add(r.Value);
            }
            return Check<List<string>>.Success(output);
        }
    }
}
