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

        /// <summary>Id of the offending vertex or transition ("" for the machine itself).</summary>
        public string Id { get; }
        public Severity Severity { get; }
        public string Message { get; }

        public override string ToString() => $"{Severity.ToString().ToLowerInvariant()} {Id}: {Message}";
    }

    /// <summary>Checks the UML 2.5.1 well-formedness rules that apply to state machines.</summary>
    public static class Validation
    {
        /// <param name="submachines">What is known about the machines referenced by submachine states, keyed by href.</param>
        public static List<Issue> Validate(FsmModel model, IReadOnlyDictionary<string, SubmachineInfo> submachines = null)
        {
            submachines = submachines ?? new Dictionary<string, SubmachineInfo>();
            var ix = new ModelIndex(model);
            var issues = new List<Issue>();
            void Add(string id, Severity severity, string message) => issues.Add(new Issue(id, severity, message));
            var protocol = model.Kind == MachineKind.Protocol;

            SubmachineInfo InfoOf(string href) => href != null && submachines.TryGetValue(href, out var i) ? i : null;
            string MachineName(string href)
            {
                var n = InfoOf(href)?.Name;
                return !string.IsNullOrEmpty(n) ? $"'{n}'" : $"'{href}'";
            }

            // Identity
            var seen = new HashSet<string>();
            foreach (var id in model.Vertices.Select(v => v.Id).Concat(model.Transitions.Select(t => t.Id)))
            {
                if (!seen.Add(id)) Add(id, Severity.Error, $"Duplicate id '{id}'.");
            }

            // Containment
            foreach (var v in model.Vertices)
            {
                if (v.Type == VertexType.EntryPoint || v.Type == VertexType.ExitPoint)
                {
                    // Either on the border of a state, or a connection point of the machine itself (top-level region).
                    var owner = ix.Vertex(v.Parent);
                    var machineLevel = model.Regions.Any(r => r.Id == v.Parent);
                    if (machineLevel)
                    {
                        if (v.Name.Length == 0) Add(v.Id, Severity.Warning, "Connection points of the state machine need a name so submachine states can reference them.");
                    }
                    else if (owner == null || owner.Type != VertexType.State)
                    {
                        Add(v.Id, Severity.Error, $"{Name(v)}: entry and exit points belong on the border of a state or at the top level of the state machine.");
                    }
                    else if (owner.Submachine.Length > 0)
                    {
                        Add(v.Id, Severity.Error, $"A submachine state cannot own entry/exit points; use a connection point reference to a point of {MachineName(owner.Submachine)} instead.");
                    }
                    else if (owner.Regions.Count == 0)
                    {
                        Add(v.Id, Severity.Warning, $"Only composite states can have entry/exit points; add a region to {Name(owner)}.");
                    }
                }
                else if (v.Type == VertexType.ConnectionPointRef)
                {
                    var owner = ix.Vertex(v.Parent);
                    if (owner == null || owner.Type != VertexType.State || owner.Submachine.Length == 0)
                    {
                        Add(v.Id, Severity.Error, "A connection point reference must be placed on the border of a submachine state.");
                    }
                }
                else if (!ix.IsRegion(v.Parent))
                {
                    Add(v.Id, Severity.Error, $"{Name(v)} is not contained in a known region.");
                }
            }

            // Regions
            var allRegions = model.Regions.Concat(model.Vertices.SelectMany(v => v.Regions)).ToList();
            foreach (var r in allRegions)
            {
                var kids = ix.ChildrenOf(r.Id).ToList();
                List<Vertex> Count(VertexType t) => kids.Where(k => k.Type == t).ToList();
                ix.RegionOwner.TryGetValue(r.Id, out var owner);
                var where = owner != null ? $"region of {Name(owner)}" : "top-level region";
                foreach (var (type, label) in new[]
                {
                    (VertexType.Initial, "initial pseudostate"),
                    (VertexType.ShallowHistory, "shallow history"),
                    (VertexType.DeepHistory, "deep history"),
                })
                {
                    var found = Count(type);
                    foreach (var k in found.Skip(1)) Add(k.Id, Severity.Error, $"A {where} can contain at most one {label}.");
                }
                if ((Count(VertexType.ShallowHistory).Count > 0 || Count(VertexType.DeepHistory).Count > 0) && owner == null)
                {
                    foreach (var k in kids.Where(x => x.Type == VertexType.ShallowHistory || x.Type == VertexType.DeepHistory))
                    {
                        Add(k.Id, Severity.Warning, "History pseudostates are only meaningful inside a composite state.");
                    }
                }
                // Default entry of a composite state targeted directly needs an initial pseudostate.
                if (owner != null && Count(VertexType.Initial).Count == 0)
                {
                    var entered = model.Transitions.Any(t =>
                        t.Target == owner.Id && t.Kind != TransitionKind.Internal && !ix.IsInside(ix.Vertex(t.Source) ?? owner, owner.Id));
                    if (entered && kids.Any(k => k.Type == VertexType.State))
                    {
                        var which = r.Name.Length > 0 ? $"region '{r.Name}'" : "region";
                        Add(owner.Id, Severity.Warning, $"{Name(owner)} is entered by default but its {which} has no initial pseudostate.");
                    }
                }
            }

            // Names unique within a region
            var byRegion = new Dictionary<string, HashSet<string>>();
            foreach (var v in model.Vertices)
            {
                if (v.Type != VertexType.State || v.Name.Length == 0) continue;
                if (!byRegion.TryGetValue(v.Parent, out var names)) byRegion[v.Parent] = names = new HashSet<string>();
                if (!names.Add(v.Name)) Add(v.Id, Severity.Warning, $"Another state named '{v.Name}' exists in the same region.");
            }

            // Vertices
            foreach (var v in model.Vertices)
            {
                var outgoing = ix.Outgoing(v.Id).Where(t => t.Kind != TransitionKind.Internal).ToList();
                var incoming = ix.Incoming(v.Id).Where(t => t.Kind != TransitionKind.Internal).ToList();
                switch (v.Type)
                {
                    case VertexType.Initial:
                        if (outgoing.Count != 1) Add(v.Id, Severity.Error, "An initial pseudostate must have exactly one outgoing transition.");
                        if (incoming.Count > 0) Add(v.Id, Severity.Error, "An initial pseudostate cannot have incoming transitions.");
                        foreach (var t in outgoing)
                        {
                            if (t.Guard.Length > 0) Add(t.Id, Severity.Error, "The transition leaving an initial pseudostate cannot have a guard.");
                        }
                        break;
                    case VertexType.Final:
                        if (outgoing.Count > 0) Add(v.Id, Severity.Error, "A final state cannot have outgoing transitions.");
                        if (v.Regions.Count > 0 || v.Entry.Length > 0 || v.Exit.Length > 0 || v.DoActivity.Length > 0)
                        {
                            Add(v.Id, Severity.Error, "A final state cannot have regions or entry/exit/do behaviors.");
                        }
                        break;
                    case VertexType.Terminate:
                        if (outgoing.Count > 0) Add(v.Id, Severity.Error, "A terminate pseudostate cannot have outgoing transitions.");
                        break;
                    case VertexType.ShallowHistory:
                    case VertexType.DeepHistory:
                        if (outgoing.Count > 1) Add(v.Id, Severity.Error, "A history pseudostate can have at most one outgoing (default) transition.");
                        break;
                    case VertexType.Fork:
                    {
                        if (incoming.Count != 1) Add(v.Id, Severity.Error, "A fork must have exactly one incoming transition.");
                        if (outgoing.Count < 2) Add(v.Id, Severity.Error, "A fork must have at least two outgoing transitions.");
                        foreach (var t in outgoing)
                        {
                            if (t.Guard.Length > 0 || t.Triggers.Count > 0) Add(t.Id, Severity.Error, "Transitions leaving a fork cannot have guards or triggers.");
                        }
                        var regions = outgoing.Select(t => ix.Vertex(t.Target)?.Parent).ToList();
                        if (regions.Distinct().Count() != regions.Count)
                        {
                            Add(v.Id, Severity.Warning, "The targets of a fork should be in different orthogonal regions.");
                        }
                        break;
                    }
                    case VertexType.Join:
                    {
                        if (outgoing.Count != 1) Add(v.Id, Severity.Error, "A join must have exactly one outgoing transition.");
                        if (incoming.Count < 2) Add(v.Id, Severity.Error, "A join must have at least two incoming transitions.");
                        foreach (var t in incoming)
                        {
                            if (t.Guard.Length > 0 || t.Triggers.Count > 0) Add(t.Id, Severity.Error, "Transitions entering a join cannot have guards or triggers.");
                        }
                        var regions = incoming.Select(t => ix.Vertex(t.Source)?.Parent).ToList();
                        if (regions.Distinct().Count() != regions.Count)
                        {
                            Add(v.Id, Severity.Warning, "The sources of a join should be in different orthogonal regions.");
                        }
                        break;
                    }
                    case VertexType.Choice:
                    case VertexType.Junction:
                    {
                        var kind = v.Type.Key();
                        if (incoming.Count == 0) Add(v.Id, Severity.Error, $"A {kind} must have at least one incoming transition.");
                        if (outgoing.Count == 0) Add(v.Id, Severity.Error, $"A {kind} must have at least one outgoing transition.");
                        if (outgoing.Count > 1)
                        {
                            if (outgoing.Any(t => t.Guard.Trim().Length == 0))
                            {
                                Add(v.Id, Severity.Warning, $"Every branch of a {kind} with several outgoing transitions should have a guard.");
                            }
                            if (!outgoing.Any(t => t.Guard.Trim() == "else"))
                            {
                                Add(v.Id, Severity.Info, $"Consider an [else] branch so the {kind} can always be left.");
                            }
                        }
                        break;
                    }
                    case VertexType.EntryPoint:
                    {
                        var owner = ix.Vertex(v.Parent);
                        if (owner == null && outgoing.Count == 0)
                        {
                            Add(v.Id, Severity.Warning, $"Entry point {Name(v)} of the state machine has no outgoing transition.");
                        }
                        if (owner != null)
                        {
                            foreach (var t in outgoing)
                            {
                                var target = ix.Vertex(t.Target);
                                if (target != null && !ix.IsInside(target, owner.Id))
                                {
                                    Add(t.Id, Severity.Error, $"Transitions leaving entry point {Name(v)} must target a vertex inside {Name(owner)}.");
                                }
                            }
                        }
                        break;
                    }
                    case VertexType.ExitPoint:
                    {
                        var owner = ix.Vertex(v.Parent);
                        if (owner == null)
                        {
                            foreach (var t in outgoing)
                            {
                                Add(t.Id, Severity.Error, $"Exit point {Name(v)} of the state machine cannot have outgoing transitions; the referencing submachine state continues from its connection point reference.");
                            }
                        }
                        else
                        {
                            foreach (var t in outgoing)
                            {
                                var target = ix.Vertex(t.Target);
                                if (target != null && (target.Id == owner.Id || ix.IsInside(target, owner.Id)))
                                {
                                    Add(t.Id, Severity.Error, $"Transitions leaving exit point {Name(v)} must target a vertex outside {Name(owner)}.");
                                }
                            }
                        }
                        break;
                    }
                    case VertexType.ConnectionPointRef:
                        CheckReference(v, outgoing, incoming);
                        break;
                    case VertexType.State:
                        if (v.Submachine.Length > 0)
                        {
                            var info = InfoOf(v.Submachine);
                            if (info != null && !info.Found) Add(v.Id, Severity.Error, $"The referenced state machine '{v.Submachine}' was not found.");
                        }
                        if (v.Submachine.Length > 0 && v.Regions.Count > 0)
                        {
                            Add(v.Id, Severity.Error, $"Submachine state {Name(v)} cannot also own regions.");
                        }
                        if (protocol && (v.Entry.Length > 0 || v.Exit.Length > 0 || v.DoActivity.Length > 0))
                        {
                            Add(v.Id, Severity.Error, $"States of a protocol state machine cannot have entry/exit/do behaviors ({Name(v)}).");
                        }
                        break;
                }
            }

            void CheckReference(Vertex v, List<Transition> outgoing, List<Transition> incoming)
            {
                var owner = ix.Vertex(v.Parent);
                var kind = v.PointKind;
                var kindText = kind == PointKind.Exit ? "exit" : "entry";
                var info = owner != null && owner.Submachine.Length > 0 ? InfoOf(owner.Submachine) : null;
                var target = info != null && info.Found ? info.Points.FirstOrDefault(p => p.Id == v.Ref) : null;
                var label = !string.IsNullOrEmpty(target?.Name) ? $"'{target.Name}'" : "connection point reference";
                if (v.Ref.Length == 0)
                {
                    Add(v.Id, Severity.Error, "The connection point reference does not refer to an entry or exit point of the submachine.");
                }
                else if (owner != null && owner.Submachine.Length > 0 && info != null && info.Found)
                {
                    if (target == null)
                    {
                        Add(v.Id, Severity.Error, $"{MachineName(owner.Submachine)} has no entry or exit point with id '{v.Ref}'.");
                    }
                    else if (target.Kind != kind)
                    {
                        var targetKind = target.Kind == PointKind.Exit ? "exit" : "entry";
                        Add(v.Id, Severity.Error, $"{label} is an {targetKind} point of {MachineName(owner.Submachine)}, but it is referenced as an {kindText} point.");
                    }
                }
                if (kind == PointKind.Entry)
                {
                    if (outgoing.Count > 0) Add(v.Id, Severity.Error, $"Entry reference {label} cannot have outgoing transitions; execution continues at the entry point inside the submachine.");
                    if (incoming.Count == 0) Add(v.Id, Severity.Warning, $"Entry reference {label} has no incoming transition.");
                    foreach (var t in incoming)
                    {
                        var src = ix.Vertex(t.Source);
                        if (src != null && owner != null && (src.Id == owner.Id || ix.IsInside(src, owner.Id)))
                        {
                            Add(t.Id, Severity.Error, $"Transitions into entry reference {label} must come from outside {Name(owner)}.");
                        }
                    }
                }
                else
                {
                    if (incoming.Count > 0) Add(v.Id, Severity.Error, $"Exit reference {label} cannot have incoming transitions; it is reached when the submachine leaves through its exit point.");
                    if (outgoing.Count == 0) Add(v.Id, Severity.Warning, $"Exit reference {label} has no outgoing transition.");
                }
                if (owner != null)
                {
                    var dup = model.Vertices.FirstOrDefault(o =>
                        o != v && o.Type == VertexType.ConnectionPointRef && o.Parent == owner.Id && o.Ref.Length > 0 && o.Ref == v.Ref);
                    if (dup != null && model.Vertices.IndexOf(dup) < model.Vertices.IndexOf(v))
                    {
                        Add(v.Id, Severity.Warning, $"{Name(owner)} already references {label}.");
                    }
                }
            }

            // Transitions
            foreach (var t in model.Transitions)
            {
                var s = ix.Vertex(t.Source);
                var g = ix.Vertex(t.Target);
                if (s == null) Add(t.Id, Severity.Error, $"Transition source '{t.Source}' does not exist.");
                if (g == null) Add(t.Id, Severity.Error, $"Transition target '{t.Target}' does not exist.");
                if (s == null || g == null) continue;
                if (s.Type == VertexType.Comment || g.Type == VertexType.Comment)
                {
                    Add(t.Id, Severity.Error, "Comments cannot be connected by transitions.");
                    continue;
                }
                if ((s.Type.IsPseudostate() || s.Type == VertexType.ConnectionPointRef) && t.Triggers.Count > 0)
                {
                    Add(t.Id, Severity.Error, $"Transitions leaving a pseudostate cannot have triggers (from {Name(s)}).");
                }
                if (t.Kind == TransitionKind.Internal && (s != g || s.Type != VertexType.State))
                {
                    Add(t.Id, Severity.Error, "An internal transition must start and end on the same state.");
                }
                if (t.Kind == TransitionKind.Local)
                {
                    var composite = s.Type == VertexType.State && s.Regions.Count > 0;
                    var container = s.Type == VertexType.EntryPoint ? s.Parent : s.Id;
                    if (!(composite || s.Type == VertexType.EntryPoint) || !(g == s || ix.IsInside(g, container)))
                    {
                        Add(t.Id, Severity.Error, "A local transition must go from a composite state to a vertex it contains.");
                    }
                }
                if (protocol)
                {
                    if (t.Effect.Length > 0) Add(t.Id, Severity.Error, "Protocol transitions cannot have effects.");
                    if (t.Guard.Length > 0) Add(t.Id, Severity.Warning, "Use a precondition instead of a guard in a protocol state machine.");
                }
                else if (t.Precondition.Length > 0 || t.Postcondition.Length > 0)
                {
                    Add(t.Id, Severity.Warning, "Pre/postconditions only apply to protocol state machines and are ignored.");
                }
                if (s.Type == VertexType.State && g.Type == VertexType.State && t.Triggers.Count == 0 && t.Guard.Length == 0 && s != g && s.Regions.Count == 0)
                {
                    if (ix.Outgoing(s.Id).Count(o => o.Kind != TransitionKind.Internal && o.Triggers.Count == 0) > 1)
                    {
                        Add(t.Id, Severity.Warning, $"{Name(s)} has several completion transitions without triggers or guards.");
                    }
                }
            }

            CheckText(model, ix, Add);
            CheckReachability(model, ix, Add);
            return issues;
        }

        /// <summary><c>'Name'</c>, or the kind of the vertex when it has no name.</summary>
        public static string Name(Vertex v)
        {
            if (v == null) return "?";
            return v.Name.Length > 0 ? $"'{v.Name}'" : v.Type.Label();
        }

        /// <summary>Behaviors and conditions are argument-less function calls; triggers are event names or after(...).</summary>
        private static void CheckText(FsmModel model, ModelIndex ix, Action<string, Severity, string> add)
        {
            void Report<T>(string id, string what, Check<T> r)
            {
                if (!r.Ok) add(id, Severity.Error, $"{what}: {r.Error}");
            }
            foreach (var v in model.Vertices)
            {
                if (v.Type != VertexType.State) continue;
                var n = Name(v);
                Report(v.Id, $"entry of {n}", Expressions.CheckActions(v.Entry));
                Report(v.Id, $"exit of {n}", Expressions.CheckActions(v.Exit));
                Report(v.Id, $"do of {n}", Expressions.CheckActions(v.DoActivity));
                Report(v.Id, $"Invariant of {n}", Expressions.CheckCondition(v.Invariant));
                if (v.Stereotype.Length > 0) Report(v.Id, $"Stereotype of {n}", Expressions.CheckName(v.Stereotype));
                foreach (var d in v.Deferrable) Report(v.Id, $"Deferrable event of {n}", Expressions.CheckEvent(d));
            }
            foreach (var t in model.Transitions)
            {
                var s = ix.Vertex(t.Source);
                foreach (var trig in t.Triggers) Report(t.Id, "Trigger", Expressions.CheckTrigger(trig));
                Report(t.Id, "Guard", Expressions.CheckGuard(t.Guard, Labels.AllowsElse(s)));
                Report(t.Id, "Effect", Expressions.CheckActions(t.Effect));
                Report(t.Id, "Precondition", Expressions.CheckCondition(t.Precondition));
                Report(t.Id, "Postcondition", Expressions.CheckCondition(t.Postcondition));
                if (t.Triggers.Any(x => Expressions.TimeTriggerMs(x) != null) && s != null && s.Type != VertexType.State)
                {
                    add(t.Id, Severity.Error, "Time triggers (after) can only be used on transitions leaving a state.");
                }
            }
        }

        private static void CheckReachability(FsmModel model, ModelIndex ix, Action<string, Severity, string> add)
        {
            var rootInitial = ix.InitialOf(model.Regions.Count > 0 ? model.Regions[0].Id : "");
            // The machine can also be entered through its own entry points (from a referencing submachine state).
            var machineEntries = model.Vertices
                .Where(v => v.Type == VertexType.EntryPoint && model.Regions.Any(r => r.Id == v.Parent))
                .ToList();
            if (rootInitial == null && machineEntries.Count == 0)
            {
                if (model.Vertices.Any(v => v.Type == VertexType.State))
                {
                    add("", Severity.Warning, "The top-level region has no initial pseudostate.");
                }
                return;
            }
            var reached = new HashSet<string>();
            var queue = new Queue<Vertex>();
            void Visit(Vertex v)
            {
                if (v == null || !reached.Add(v.Id)) return;
                queue.Enqueue(v);
            }
            Visit(rootInitial);
            machineEntries.ForEach(Visit);
            while (queue.Count > 0)
            {
                var v = queue.Dequeue();
                // Entering a vertex enters its enclosing states and their other orthogonal regions.
                foreach (var a in ix.Ancestors(v)) Visit(a);
                if (v.Type == VertexType.State)
                {
                    foreach (var r in v.Regions) Visit(ix.InitialOf(r.Id));
                    foreach (var cp in ix.ConnectionPoints(v))
                    {
                        if (cp.Type == VertexType.ExitPoint || (cp.Type == VertexType.ConnectionPointRef && cp.PointKind == PointKind.Exit)) Visit(cp);
                    }
                }
                foreach (var t in ix.Outgoing(v.Id)) Visit(ix.Vertex(t.Target));
            }
            foreach (var v in model.Vertices)
            {
                if (v.Type == VertexType.State && !reached.Contains(v.Id))
                {
                    add(v.Id, Severity.Warning, $"State {Name(v)} can never be entered.");
                }
            }
        }
    }
}
