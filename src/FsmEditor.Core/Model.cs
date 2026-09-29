using System;
using System.Collections.Generic;
using System.Linq;

namespace FsmEditor.Core
{
    /// <summary>
    /// In-memory UML state machine model. Files store it as XMI with UML DI
    /// (see <see cref="Xmi"/>); the diagram editor works on this form.
    ///
    /// The hierarchy is flat: every vertex points to its owner through
    /// <see cref="Vertex.Parent"/>, which is either a region id (root region of
    /// the machine or a region of a composite state) or, for connection points,
    /// the id of the owning state.
    ///
    /// Entry/exit points whose parent is a top-level region are connection
    /// points of the state machine itself; a submachine state that references
    /// this machine exposes them through <see cref="VertexType.ConnectionPointRef"/>
    /// vertices placed on its border.
    /// </summary>
    public enum VertexType
    {
        State,
        Final,
        Initial,
        ShallowHistory,
        DeepHistory,
        Choice,
        Junction,
        Fork,
        Join,
        EntryPoint,
        ExitPoint,
        Terminate,
        ConnectionPointRef,
        Comment,
    }

    public enum PointKind { Entry, Exit }

    public enum TransitionKind { External, Internal, Local }

    public enum MachineKind { Behavioral, Protocol }

    public enum RegionLayout { Vertical, Horizontal }

    public struct PointD : IEquatable<PointD>
    {
        public double X;
        public double Y;

        public PointD(double x, double y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(PointD other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is PointD p && Equals(p);
        public override int GetHashCode() => X.GetHashCode() * 31 + Y.GetHashCode();
        public override string ToString() => $"{X},{Y}";
    }

    public sealed class Region
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";

        public Region Clone() => new Region { Id = Id, Name = Name };
    }

    public sealed class Vertex
    {
        public string Id { get; set; } = "";
        public VertexType Type { get; set; }
        public string Name { get; set; } = "";
        public string Parent { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }

        // state
        public List<Region> Regions { get; set; } = new List<Region>();
        public RegionLayout RegionLayout { get; set; }
        public string Entry { get; set; } = "";
        public string Exit { get; set; } = "";
        public string DoActivity { get; set; } = "";
        public List<string> Deferrable { get; set; } = new List<string>();
        public string Invariant { get; set; } = "";
        /// <summary>Submachine state: XMI href of the referenced state machine, e.g. <c>Payment.fsm#sm</c>.</summary>
        public string Submachine { get; set; } = "";
        public string Stereotype { get; set; } = "";

        // connectionPointRef: xmi:id of the entry/exit point, inside the submachine's file
        public string Ref { get; set; } = "";
        public PointKind PointKind { get; set; }

        // comment
        public string Text { get; set; } = "";
        public List<string> Anchors { get; set; } = new List<string>();

        public double CenterX => X + W / 2;
        public double CenterY => Y + H / 2;
        public PointD Center => new PointD(X + W / 2, Y + H / 2);

        public Vertex Clone()
        {
            var v = (Vertex)MemberwiseClone();
            v.Regions = Regions.Select(r => r.Clone()).ToList();
            v.Deferrable = new List<string>(Deferrable);
            v.Anchors = new List<string>(Anchors);
            return v;
        }
    }

    public sealed class Transition
    {
        public string Id { get; set; } = "";
        public string Source { get; set; } = "";
        public string Target { get; set; } = "";
        public TransitionKind Kind { get; set; }
        public List<string> Triggers { get; set; } = new List<string>();
        public string Guard { get; set; } = "";
        public string Effect { get; set; } = "";
        /// <summary>Protocol state machines only.</summary>
        public string Precondition { get; set; } = "";
        public string Postcondition { get; set; } = "";
        /// <summary>Bend points; empty for a straight line.</summary>
        public List<PointD> Points { get; set; } = new List<PointD>();
        /// <summary>Offset of the label from its default place, or null when it was never moved.</summary>
        public PointD? LabelOffset { get; set; }

        public Transition Clone()
        {
            var t = (Transition)MemberwiseClone();
            t.Triggers = new List<string>(Triggers);
            t.Points = new List<PointD>(Points);
            return t;
        }
    }

    public sealed class FsmModel
    {
        /// <summary>xmi:id of the state machine, used by other files to reference it.</summary>
        public string Id { get; set; } = "sm";
        public string Name { get; set; } = "";
        public MachineKind Kind { get; set; }
        public string Context { get; set; } = "";
        public string Documentation { get; set; } = "";
        public List<Region> Regions { get; set; } = new List<Region>();
        public List<Vertex> Vertices { get; set; } = new List<Vertex>();
        public List<Transition> Transitions { get; set; } = new List<Transition>();

        public FsmModel Clone()
        {
            var m = (FsmModel)MemberwiseClone();
            m.Regions = Regions.Select(r => r.Clone()).ToList();
            m.Vertices = Vertices.Select(v => v.Clone()).ToList();
            m.Transitions = Transitions.Select(t => t.Clone()).ToList();
            return m;
        }

        public static FsmModel CreateDefault(string name = "StateMachine") => new FsmModel
        {
            Id = "sm",
            Name = name,
            Regions = { new Region { Id = "r_root" } },
            Vertices =
            {
                new Vertex { Id = "v_init", Type = VertexType.Initial, Parent = "r_root", X = 60, Y = 80, W = 20, H = 20 },
                new Vertex { Id = "v_idle", Type = VertexType.State, Name = "Idle", Parent = "r_root", X = 160, Y = 60, W = 140, H = 60 },
            },
            Transitions = { new Transition { Id = "t_init", Source = "v_init", Target = "v_idle" } },
        };

        /// <summary>Fills in defaults so partially specified models are safe to use.</summary>
        public FsmModel Normalize()
        {
            if (string.IsNullOrEmpty(Id)) Id = "sm";
            Name = Name ?? "";
            Context = Context ?? "";
            Documentation = Documentation ?? "";
            Regions = Regions ?? new List<Region>();
            if (Regions.Count == 0) Regions.Add(new Region { Id = "r_root" });
            Vertices = Vertices ?? new List<Vertex>();
            Transitions = Transitions ?? new List<Transition>();
            foreach (var v in Vertices)
            {
                v.Name = v.Name ?? "";
                v.Regions = v.Regions ?? new List<Region>();
                v.Deferrable = v.Deferrable ?? new List<string>();
                v.Anchors = v.Anchors ?? new List<string>();
                v.Entry = v.Entry ?? "";
                v.Exit = v.Exit ?? "";
                v.DoActivity = v.DoActivity ?? "";
                v.Invariant = v.Invariant ?? "";
                v.Submachine = v.Submachine ?? "";
                v.Stereotype = v.Stereotype ?? "";
                v.Ref = v.Ref ?? "";
                v.Text = v.Text ?? "";
            }
            foreach (var t in Transitions)
            {
                t.Triggers = t.Triggers ?? new List<string>();
                t.Points = t.Points ?? new List<PointD>();
                t.Guard = t.Guard ?? "";
                t.Effect = t.Effect ?? "";
                t.Precondition = t.Precondition ?? "";
                t.Postcondition = t.Postcondition ?? "";
            }
            return this;
        }

        /// <summary>Entry/exit points owned by the state machine itself (placed in a top-level region).</summary>
        public List<ConnectionPointInfo> MachineConnectionPoints()
        {
            var top = new HashSet<string>(Regions.Select(r => r.Id));
            return Vertices
                .Where(v => (v.Type == VertexType.EntryPoint || v.Type == VertexType.ExitPoint) && top.Contains(v.Parent))
                .Select(v => new ConnectionPointInfo(v.Id, v.Name, v.Type == VertexType.EntryPoint ? PointKind.Entry : PointKind.Exit))
                .ToList();
        }
    }

    public static class VertexTypes
    {
        public static bool IsPseudostate(this VertexType t) =>
            t == VertexType.Initial || t == VertexType.ShallowHistory || t == VertexType.DeepHistory ||
            t == VertexType.Choice || t == VertexType.Junction || t == VertexType.Fork || t == VertexType.Join ||
            t == VertexType.EntryPoint || t == VertexType.ExitPoint || t == VertexType.Terminate;

        public static bool IsPointType(this VertexType t) =>
            t == VertexType.EntryPoint || t == VertexType.ExitPoint || t == VertexType.ConnectionPointRef;

        /// <summary>The name used in files and messages: <c>shallowHistory</c>, <c>connectionPointRef</c>…</summary>
        public static string Key(this VertexType t)
        {
            var s = t.ToString();
            return char.ToLowerInvariant(s[0]) + s.Substring(1);
        }

        public static bool TryParseKey(string key, out VertexType type)
        {
            foreach (VertexType t in Enum.GetValues(typeof(VertexType)))
            {
                if (t.Key() == key)
                {
                    type = t;
                    return true;
                }
            }
            type = VertexType.Initial;
            return false;
        }

        /// <summary>Lowercase label used in validation messages.</summary>
        public static string Label(this VertexType t)
        {
            switch (t)
            {
                case VertexType.State: return "state";
                case VertexType.Final: return "final state";
                case VertexType.Initial: return "initial pseudostate";
                case VertexType.ShallowHistory: return "shallow history";
                case VertexType.DeepHistory: return "deep history";
                case VertexType.Choice: return "choice";
                case VertexType.Junction: return "junction";
                case VertexType.Fork: return "fork";
                case VertexType.Join: return "join";
                case VertexType.EntryPoint: return "entry point";
                case VertexType.ExitPoint: return "exit point";
                case VertexType.ConnectionPointRef: return "connection point reference";
                case VertexType.Terminate: return "terminate";
                default: return "comment";
            }
        }

        /// <summary>Title-case label used in the editor.</summary>
        public static string Title(this VertexType t)
        {
            switch (t)
            {
                case VertexType.State: return "State";
                case VertexType.Final: return "Final State";
                case VertexType.Initial: return "Initial";
                case VertexType.ShallowHistory: return "Shallow History";
                case VertexType.DeepHistory: return "Deep History";
                case VertexType.Choice: return "Choice";
                case VertexType.Junction: return "Junction";
                case VertexType.Fork: return "Fork";
                case VertexType.Join: return "Join";
                case VertexType.EntryPoint: return "Entry Point";
                case VertexType.ExitPoint: return "Exit Point";
                case VertexType.Terminate: return "Terminate";
                case VertexType.ConnectionPointRef: return "Connection Point Reference";
                default: return "Comment";
            }
        }

        /// <summary>Default size of a new or unplaced vertex.</summary>
        public static (double W, double H) DefaultSize(this VertexType t)
        {
            switch (t)
            {
                case VertexType.State: return (140, 60);
                case VertexType.Final: return (26, 26);
                case VertexType.Initial: return (20, 20);
                case VertexType.ShallowHistory:
                case VertexType.DeepHistory: return (26, 26);
                case VertexType.Choice: return (28, 28);
                case VertexType.Junction: return (14, 14);
                case VertexType.Fork:
                case VertexType.Join: return (90, 8);
                case VertexType.EntryPoint:
                case VertexType.ExitPoint:
                case VertexType.ConnectionPointRef: return (16, 16);
                case VertexType.Terminate: return (20, 20);
                case VertexType.Comment: return (160, 64);
                default: return (40, 40);
            }
        }
    }

    public sealed class ConnectionPointInfo
    {
        public ConnectionPointInfo(string id, string name, PointKind kind)
        {
            Id = id;
            Name = name;
            Kind = kind;
        }

        public string Id { get; }
        public string Name { get; }
        public PointKind Kind { get; }
    }

    /// <summary>A state machine found in the workspace that submachine states can reference.</summary>
    public sealed class MachineInfo
    {
        /// <summary>href relative to the referencing file, e.g. <c>Payment.fsm#sm</c>.</summary>
        public string Href { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>Path shown to the user (relative to the solution when possible).</summary>
        public string File { get; set; } = "";
        public List<ConnectionPointInfo> Points { get; set; } = new List<ConnectionPointInfo>();
    }

    /// <summary>What is known about a machine referenced by submachine states.</summary>
    public sealed class SubmachineInfo
    {
        public bool Found { get; set; }
        public string Name { get; set; } = "";
        public string File { get; set; } = "";
        public List<ConnectionPointInfo> Points { get; set; } = new List<ConnectionPointInfo>();
    }

    /// <summary>Tree navigation over the flat model.</summary>
    public sealed class ModelIndex
    {
        public FsmModel Model { get; }
        public Dictionary<string, Vertex> Vertices { get; } = new Dictionary<string, Vertex>();
        public Dictionary<string, Transition> Transitions { get; } = new Dictionary<string, Transition>();
        /// <summary>Owner state of each region; null for top-level regions.</summary>
        public Dictionary<string, Vertex> RegionOwner { get; } = new Dictionary<string, Vertex>();

        public ModelIndex(FsmModel model)
        {
            Model = model;
            foreach (var r in model.Regions) RegionOwner[r.Id] = null;
            foreach (var v in model.Vertices)
            {
                Vertices[v.Id] = v;
                foreach (var r in v.Regions) RegionOwner[r.Id] = v;
            }
            foreach (var t in model.Transitions) Transitions[t.Id] = t;
        }

        public Vertex Vertex(string id) => id != null && Vertices.TryGetValue(id, out var v) ? v : null;

        public Transition Transition(string id) => id != null && Transitions.TryGetValue(id, out var t) ? t : null;

        public bool IsRegion(string id) => id != null && RegionOwner.ContainsKey(id);

        public bool IsTopRegion(string id) => Model.Regions.Any(r => r.Id == id);

        /// <summary>Vertices drawn on the border of a state (entry/exit points of a state and connection point references).</summary>
        public bool IsBorderVertex(Vertex v) =>
            v.Type == VertexType.ConnectionPointRef ||
            ((v.Type == VertexType.EntryPoint || v.Type == VertexType.ExitPoint) && Vertices.ContainsKey(v.Parent));

        public IEnumerable<Vertex> ChildrenOf(string regionOrStateId) => Model.Vertices.Where(v => v.Parent == regionOrStateId);

        public IEnumerable<Vertex> ConnectionPoints(Vertex state) =>
            Model.Vertices.Where(v => v.Parent == state.Id && v.Type.IsPointType());

        public IEnumerable<Transition> Outgoing(string id) => Model.Transitions.Where(t => t.Source == id);

        public IEnumerable<Transition> Incoming(string id) => Model.Transitions.Where(t => t.Target == id);

        /// <summary>The state that directly contains <paramref name="v"/> (through a region, or as a connection point).</summary>
        public Vertex OwnerState(Vertex v)
        {
            if (IsBorderVertex(v)) return Vertex(v.Parent);
            return RegionOwner.TryGetValue(v.Parent ?? "", out var o) ? o : null;
        }

        /// <summary>Enclosing states, innermost first.</summary>
        public List<Vertex> Ancestors(Vertex v)
        {
            var output = new List<Vertex>();
            var seen = new HashSet<string> { v.Id };
            var o = OwnerState(v);
            while (o != null && seen.Add(o.Id))
            {
                output.Add(o);
                o = OwnerState(o);
            }
            return output;
        }

        public int Depth(Vertex v) => Ancestors(v).Count;

        public bool IsInside(Vertex v, string stateId) => Ancestors(v).Any(a => a.Id == stateId);

        public List<Vertex> Descendants(Vertex v) => Model.Vertices.Where(x => x != v && IsInside(x, v.Id)).ToList();

        public Vertex InitialOf(string regionId) =>
            Model.Vertices.FirstOrDefault(v => v.Parent == regionId && v.Type == VertexType.Initial);
    }
}
