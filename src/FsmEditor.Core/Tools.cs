using System;
using System.Collections.Generic;
using System.Linq;

namespace FsmEditor.Core
{
    /// <summary>An item of the diagram toolbox.</summary>
    public sealed class ToolDefinition
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        /// <summary>Single-letter shortcut, or null.</summary>
        public char? Key { get; set; }
        /// <summary>A mode (transition, region) rather than an element to place.</summary>
        public bool IsMode { get; set; }
        /// <summary>Builds the element placed by this tool (id, parent and position are set by the caller).</summary>
        public Func<DiagramSession, Vertex> Create { get; set; }
    }

    public sealed class ToolGroup
    {
        public string Title { get; set; } = "";
        public List<ToolDefinition> Items { get; } = new List<ToolDefinition>();
    }

    public static class Tools
    {
        private static Vertex Make(VertexType type, double w, double h, string name = "") =>
            new Vertex { Type = type, Name = name, W = w, H = h };

        public static readonly IReadOnlyList<ToolGroup> Groups = new List<ToolGroup>
        {
            new ToolGroup
            {
                Title = "States",
                Items =
                {
                    new ToolDefinition { Id = "state", Label = "State", Key = 's', Create = s => Make(VertexType.State, 140, 60, s.UniqueName("State")) },
                    new ToolDefinition
                    {
                        Id = "composite", Label = "Composite State",
                        Create = s =>
                        {
                            var v = Make(VertexType.State, 280, 190, s.UniqueName("Composite"));
                            v.Regions.Add(new Region { Id = s.NewId("r") });
                            return v;
                        },
                    },
                    new ToolDefinition
                    {
                        Id = "orthogonal", Label = "Orthogonal State",
                        Create = s =>
                        {
                            var v = Make(VertexType.State, 340, 250, s.UniqueName("Orthogonal"));
                            v.Regions.Add(new Region { Id = s.NewId("r") });
                            v.Regions.Add(new Region { Id = s.NewId("r") });
                            v.RegionLayout = RegionLayout.Vertical;
                            return v;
                        },
                    },
                    new ToolDefinition
                    {
                        Id = "submachine", Label = "Submachine State",
                        Create = s =>
                        {
                            var v = Make(VertexType.State, 170, 60, s.UniqueName("Sub"));
                            v.Submachine = s.Machines.Count > 0 ? s.Machines[0].Href : "";
                            return v;
                        },
                    },
                    new ToolDefinition { Id = "connectionPointRef", Label = "Connection Point Ref", Create = s => Make(VertexType.ConnectionPointRef, 16, 16) },
                    new ToolDefinition { Id = "final", Label = "Final State", Key = 'x', Create = s => Make(VertexType.Final, 26, 26) },
                },
            },
            new ToolGroup
            {
                Title = "Pseudostates",
                Items =
                {
                    new ToolDefinition { Id = "initial", Label = "Initial", Key = 'i', Create = s => Make(VertexType.Initial, 20, 20) },
                    new ToolDefinition { Id = "shallowHistory", Label = "Shallow History", Key = 'h', Create = s => Make(VertexType.ShallowHistory, 26, 26) },
                    new ToolDefinition { Id = "deepHistory", Label = "Deep History", Create = s => Make(VertexType.DeepHistory, 26, 26) },
                    new ToolDefinition { Id = "choice", Label = "Choice", Key = 'c', Create = s => Make(VertexType.Choice, 28, 28) },
                    new ToolDefinition { Id = "junction", Label = "Junction", Key = 'j', Create = s => Make(VertexType.Junction, 14, 14) },
                    new ToolDefinition { Id = "fork", Label = "Fork", Create = s => Make(VertexType.Fork, 90, 8) },
                    new ToolDefinition { Id = "join", Label = "Join", Create = s => Make(VertexType.Join, 90, 8) },
                    new ToolDefinition { Id = "entryPoint", Label = "Entry Point", Create = s => Make(VertexType.EntryPoint, 16, 16) },
                    new ToolDefinition { Id = "exitPoint", Label = "Exit Point", Create = s => Make(VertexType.ExitPoint, 16, 16) },
                    new ToolDefinition { Id = "terminate", Label = "Terminate", Create = s => Make(VertexType.Terminate, 20, 20) },
                },
            },
            new ToolGroup
            {
                Title = "Connections",
                Items =
                {
                    new ToolDefinition { Id = "transition", Label = "Transition", Key = 't', IsMode = true },
                    new ToolDefinition { Id = "region", Label = "Add Region", Key = 'r', IsMode = true },
                },
            },
            new ToolGroup
            {
                Title = "Annotations",
                Items =
                {
                    new ToolDefinition
                    {
                        Id = "comment", Label = "Comment", Key = 'n',
                        Create = s =>
                        {
                            var v = Make(VertexType.Comment, 160, 64);
                            v.Text = "Note";
                            return v;
                        },
                    },
                },
            },
        };

        public static IEnumerable<ToolDefinition> All => Groups.SelectMany(g => g.Items);

        public static ToolDefinition ById(string id) => All.FirstOrDefault(t => t.Id == id);

        public static ToolDefinition ByKey(char key) => All.FirstOrDefault(t => t.Key == char.ToLowerInvariant(key));

        /// <summary>Pseudostate kinds that can be swapped for one another from the properties panel.</summary>
        public static readonly IReadOnlyList<VertexType[]> Swappable = new[]
        {
            new[] { VertexType.Initial, VertexType.ShallowHistory, VertexType.DeepHistory, VertexType.Choice, VertexType.Junction, VertexType.Terminate },
            new[] { VertexType.Fork, VertexType.Join },
            new[] { VertexType.EntryPoint, VertexType.ExitPoint },
        };
    }
}
