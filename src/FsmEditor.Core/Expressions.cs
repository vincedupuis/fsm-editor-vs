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

    public enum ConditionOp { Call, Not, And, Or }

    /// <summary>A condition tree: a call, a negation, or a conjunction/disjunction.</summary>
    public sealed class Condition
    {
        public ConditionOp Op { get; set; }
        /// <summary>Function name, for <see cref="ConditionOp.Call"/>.</summary>
        public string Name { get; set; }
        /// <summary>Operand of <see cref="ConditionOp.Not"/>, or operands of And/Or.</summary>
        public List<Condition> Args { get; set; } = new List<Condition>();
        /// <summary>Parentheses written around this condition (kept in the normalized text only).</summary>
        public int Parens { get; set; }

        public static Condition Call(string name) => new Condition { Op = ConditionOp.Call, Name = name };
    }

    /// <summary>
    /// Grammar of the text a state machine may contain. The state machine holds
    /// no variables: behaviors and conditions are calls to functions without
    /// arguments; the parentheses only mark the call.
    /// <code>
    ///   actions   := call (';' call)*                      entry, exit, do, effect
    ///   condition := or                                     guard, invariant, pre/postcondition
    ///   or        := and ('||' and)*
    ///   and       := unary ('&amp;&amp;' unary)*
    ///   unary     := '!' unary | call | '(' or ')'
    ///   guard     := 'else' | condition                     'else' only on choice/junction branches
    ///   trigger   := event | 'after(' number unit ')'       unit: ms, s, m, h
    ///   event     := name                                   also used for deferrable events
    ///   call      := name '()'
    ///   stereotype:= name
    ///   name      := [A-Za-z_][A-Za-z0-9_]*
    /// </code>
    /// </summary>
    public static class Expressions
    {
        // JavaScript's \s, \b and . spelled out (see JsWhitespace), and \z for its $: .NET's $ also matches before a final \n.
        private const string Ws = JsWhitespace.Class;
        private static readonly Regex NameRe = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*\z");
        private static readonly Regex TimeRe = new Regex($@"^after{Ws}*\({Ws}*([0-9]+(?:\.[0-9]+)?){Ws}*(ms|s|m|h){Ws}*\)\z");
        private static readonly Regex AfterWord = new Regex(@"^after(?![A-Za-z0-9_])");
        private static readonly Regex WhenWord = new Regex(@"^when(?![A-Za-z0-9_])");
        private static readonly Regex TrailingSemicolon = new Regex($@";{Ws}*\z");
        private static readonly Regex CallTail = new Regex($@"{Ws}*\([^\n\r\u2028\u2029]*\z");

        private sealed class SyntaxProblem : Exception
        {
            public SyntaxProblem(string message) : base(message) { }
        }

        private struct Token
        {
            public string Kind; // "name", "(", ")", "!", ";", "&&", "||"
            public string Value;
            public string Display => Value ?? Kind;
        }

        private static bool IsNameStart(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
        private static bool IsNameChar(char c) => IsNameStart(c) || (c >= '0' && c <= '9');

        private static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (JsWhitespace.Contains(c))
                {
                    i++;
                }
                else if (IsNameStart(c))
                {
                    int j = i + 1;
                    while (j < text.Length && IsNameChar(text[j])) j++;
                    tokens.Add(new Token { Kind = "name", Value = text.Substring(i, j - i) });
                    i = j;
                }
                else if (string.CompareOrdinal(text, i, "&&", 0, 2) == 0 || string.CompareOrdinal(text, i, "||", 0, 2) == 0)
                {
                    tokens.Add(new Token { Kind = text.Substring(i, 2) });
                    i += 2;
                }
                else if ("()!;".IndexOf(c) >= 0)
                {
                    tokens.Add(new Token { Kind = c.ToString() });
                    i++;
                }
                else if (c == '.')
                {
                    throw new SyntaxProblem("Dotted names are not allowed: use a single function name like doThing().");
                }
                else if ("<>=+-*/%&|".IndexOf(c) >= 0 || (c >= '0' && c <= '9'))
                {
                    throw new SyntaxProblem(
                        $"'{c}' is not allowed: the state machine has no variables, so use function calls like isReady() combined with !, && and ||.");
                }
                else
                {
                    throw new SyntaxProblem($"Unexpected character '{c}'.");
                }
            }
            return tokens;
        }

        private sealed class Parser
        {
            private readonly List<Token> _tokens;
            private int _i;

            public Parser(List<Token> tokens) => _tokens = tokens;

            public Token? Peek() => _i < _tokens.Count ? _tokens[_i] : (Token?)null;

            public Token? Next() => _i < _tokens.Count ? _tokens[_i++] : (Token?)null;

            public bool PeekIs(string kind) => Peek() is Token t && t.Kind == kind;

            public string CallName()
            {
                var tok = Next();
                if (tok == null || tok.Value.Kind != "name")
                {
                    throw new SyntaxProblem(tok != null ? $"Expected a function call, found '{tok.Value.Kind}'." : "Expected a function call.");
                }
                var name = tok.Value.Value;
                var open = Next();
                if (open == null || open.Value.Kind != "(") throw new SyntaxProblem($"'{name}' must be a function call: write {name}().");
                var close = Next();
                if (close == null || close.Value.Kind != ")") throw new SyntaxProblem($"Functions take no arguments: write {name}().");
                return name;
            }

            public Condition Or()
            {
                var args = new List<Condition> { And() };
                while (PeekIs("||"))
                {
                    Next();
                    args.Add(And());
                }
                return args.Count == 1 ? args[0] : new Condition { Op = ConditionOp.Or, Args = args };
            }

            private Condition And()
            {
                var args = new List<Condition> { Unary() };
                while (PeekIs("&&"))
                {
                    Next();
                    args.Add(Unary());
                }
                return args.Count == 1 ? args[0] : new Condition { Op = ConditionOp.And, Args = args };
            }

            private Condition Unary()
            {
                if (PeekIs("!"))
                {
                    Next();
                    return new Condition { Op = ConditionOp.Not, Args = { Unary() } };
                }
                if (PeekIs("("))
                {
                    Next();
                    var inner = Or();
                    var close = Next();
                    if (close == null || close.Value.Kind != ")") throw new SyntaxProblem("Missing closing parenthesis.");
                    // Remember the grouping so the normalized text keeps its parentheses.
                    inner.Parens++;
                    return inner;
                }
                return Condition.Call(CallName());
            }

            public void End()
            {
                var tok = Peek();
                if (tok != null) throw new SyntaxProblem($"Unexpected '{tok.Value.Display}'.");
            }
        }

        private static Check<T> Guarded<T>(Func<T> fn)
        {
            try
            {
                return Check<T>.Success(fn());
            }
            catch (SyntaxProblem e)
            {
                return Check<T>.Fail(e.Message);
            }
        }

        /// <summary>Normalized text of a condition, keeping the parentheses the author wrote.</summary>
        public static string Format(Condition c)
        {
            string s;
            switch (c.Op)
            {
                case ConditionOp.Call: s = c.Name + "()"; break;
                case ConditionOp.Not: s = "!" + Format(c.Args[0]); break;
                case ConditionOp.And: s = string.Join(" && ", c.Args.Select(Format)); break;
                default: s = string.Join(" || ", c.Args.Select(Format)); break;
            }
            return new string('(', c.Parens) + s + new string(')', c.Parens);
        }

        private static string Trim(string text) => JsWhitespace.Trim(text);

        /// <summary>Condition built from calls, !, &amp;&amp; and ||. Empty text is allowed (no condition).</summary>
        public static Check<string> CheckCondition(string text)
        {
            var r = ParseCondition(text);
            if (!r.Ok) return Check<string>.Fail(r.Error);
            return Check<string>.Success(r.Value == null ? "" : Format(r.Value));
        }

        /// <summary>Condition as a tree. Empty text gives null (no condition).</summary>
        public static Check<Condition> ParseCondition(string text)
        {
            var s = Trim(text);
            if (s.Length == 0) return Check<Condition>.Success(null);
            return Guarded(() =>
            {
                var p = new Parser(Tokenize(s));
                var value = p.Or();
                p.End();
                return value;
            });
        }

        /// <summary>Transition guard: a condition, or 'else' when <paramref name="allowElse"/> is set.</summary>
        public static Check<string> CheckGuard(string text, bool allowElse)
        {
            var s = Trim(text);
            if (s == "else")
            {
                return allowElse
                    ? Check<string>.Success("else")
                    : Check<string>.Fail("[else] is only allowed on transitions leaving a choice or junction.");
            }
            return CheckCondition(s);
        }

        /// <summary>Behavior: calls separated by ';'. Empty text is allowed (no behavior).</summary>
        public static Check<string> CheckActions(string text)
        {
            var r = ParseActions(text);
            return r.Ok ? Check<string>.Success(string.Join("; ", r.Value.Select(n => n + "()"))) : Check<string>.Fail(r.Error);
        }

        /// <summary>Names of the functions a behavior calls, in order.</summary>
        public static Check<List<string>> ParseActions(string text)
        {
            var s = TrailingSemicolon.Replace(Trim(text), "");
            if (s.Length == 0) return Check<List<string>>.Success(new List<string>());
            return Guarded(() =>
            {
                var p = new Parser(Tokenize(s));
                var calls = new List<string> { p.CallName() };
                while (p.PeekIs(";"))
                {
                    p.Next();
                    calls.Add(p.CallName());
                }
                var tok = p.Peek();
                if (tok != null)
                {
                    var k = tok.Value.Kind;
                    throw new SyntaxProblem(k == "&&" || k == "||" || k == "!"
                        ? "Actions cannot use !, && or ||: separate several calls with ;"
                        : $"Unexpected '{tok.Value.Display}': separate several calls with ;");
                }
                return calls;
            });
        }

        /// <summary>Event name (used for triggers and deferrable events).</summary>
        public static Check<string> CheckEvent(string text)
        {
            var s = Trim(text);
            if (s.Length == 0) return Check<string>.Fail("Empty event name.");
            if (AfterWord.IsMatch(s)) return Check<string>.Fail("A time event cannot be deferred; use a named event.");
            if (s.Contains("(")) return Check<string>.Fail($"Events are plain names without (): write {StripCall(s)}.");
            if (!NameRe.IsMatch(s)) return Check<string>.Fail($"'{s}' is not a valid event name (letters, digits and _ only).");
            return Check<string>.Success(s);
        }

        /// <summary>A plain name (letters, digits and _), e.g. a stereotype. Empty text is allowed.</summary>
        public static Check<string> CheckName(string text)
        {
            var s = Trim(text);
            if (s.Length == 0) return Check<string>.Success("");
            if (!NameRe.IsMatch(s)) return Check<string>.Fail($"'{s}' is not a valid name: use letters, digits and _ only, not starting with a digit.");
            return Check<string>.Success(s);
        }

        /// <summary>One trigger: an event name, or after(&lt;number&gt;&lt;ms|s|m|h&gt;).</summary>
        public static Check<string> CheckTrigger(string text)
        {
            var s = Trim(text);
            if (AfterWord.IsMatch(s))
            {
                var m = TimeRe.Match(s);
                if (!m.Success)
                {
                    return Check<string>.Fail("Time triggers are written after(<number><unit>) with unit ms, s, m or h, e.g. after(500ms) or after(2s).");
                }
                if (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) <= 0)
                {
                    return Check<string>.Fail("The time of an after() trigger must be greater than zero.");
                }
                return Check<string>.Success($"after({m.Groups[1].Value}{m.Groups[2].Value})");
            }
            if (WhenWord.IsMatch(s)) return Check<string>.Fail("Change events (when) are not supported: use a named event or after(...).");
            if (s.Length == 0) return Check<string>.Fail("Empty trigger.");
            if (s.Contains("(")) return Check<string>.Fail($"Events are plain names without (): write {StripCall(s)}. The only exception is after(...).");
            if (!NameRe.IsMatch(s)) return Check<string>.Fail($"'{s}' is not a valid event name (letters, digits and _ only).");
            return Check<string>.Success(s);
        }

        /// <summary>Comma-separated list checked item by item; returns the normalized items.</summary>
        public static Check<List<string>> CheckList(string text, Func<string, Check<string>> check)
        {
            var output = new List<string>();
            foreach (var raw in (text ?? "").Split(','))
            {
                var item = JsWhitespace.Trim(raw);
                if (item.Length == 0) continue;
                var r = check(item);
                if (!r.Ok) return Check<List<string>>.Fail(r.Error);
                output.Add(r.Value);
            }
            return Check<List<string>>.Success(output);
        }

        /// <summary>Parses an after() trigger into milliseconds, or returns null.</summary>
        public static double? TimeTriggerMs(string trigger)
        {
            var m = TimeRe.Match(Trim(trigger));
            if (!m.Success) return null;
            double factor;
            switch (m.Groups[2].Value)
            {
                case "ms": factor = 1; break;
                case "s": factor = 1000; break;
                case "m": factor = 60000; break;
                default: factor = 3600000; break;
            }
            return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * factor;
        }

        private static string StripCall(string s) => CallTail.Replace(s, "");
    }
}
