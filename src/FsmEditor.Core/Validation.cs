using System;
using System.Collections.Generic;
using System.Linq;

namespace FsmEditor.Core
{
    public enum Severity { Error, Warning, Info }

    public sealed class Issue
    {
        public Issue(string id, Severity severity, string message)
        {
            Id = id;
            Severity = severity;
            Message = message;
        }

        /// <summary>Id of the offending vertex, transition or region ("" for the machine itself).</summary>
        public string Id { get; }
        public Severity Severity { get; }
        public string Message { get; }

        public override string ToString() => $"{Severity.ToString().ToLowerInvariant()} {Id}: {Message}";
    }

    /// <summary>
    /// The UML 2.5.1 well-formedness rules that apply to state machines, names and references, the text grammar and
    /// reachability: fsm-cli's rules and messages, run by fsm-editor-common (<see cref="Common"/>).
    /// </summary>
    public static class Validation
    {
        public static List<Issue> Validate(FsmModel model, IReadOnlyDictionary<string, SubmachineInfo> submachines = null)
        {
            var subs = string.Join(",", (submachines ?? new Dictionary<string, SubmachineInfo>()).Select(kv =>
                ClipboardJson.Quote(kv.Key) + ":" + Common.Args(
                    ("found", kv.Value.Found ? "true" : "false"),
                    ("name", Common.Str(kv.Value.Name ?? "")),
                    ("points", "[" + string.Join(",", kv.Value.Points.Select(p => Common.Args(
                        ("id", Common.Str(p.Id)), ("name", Common.Str(p.Name)), ("kind", Common.Str(p.Kind == PointKind.Exit ? "exit" : "entry"))))) + "]"))));
            var r = (List<object>)Common.Call("validate", Common.Args(("model", ModelJson.Write(model)), ("submachines", "{" + subs + "}")));
            return r.OfType<Dictionary<string, object>>().Select(i => new Issue(
                ModelJson.Str(i, "id"),
                ModelJson.Str(i, "severity") == "error" ? Severity.Error : ModelJson.Str(i, "severity") == "warning" ? Severity.Warning : Severity.Info,
                ModelJson.Str(i, "message"))).ToList();
        }
    }
}
