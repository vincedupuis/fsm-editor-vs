using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FsmEditor.Core;
using Xunit;

namespace FsmEditor.Core.Tests
{
    /// <summary>Builds an <see cref="FsmModel"/> in code (ModelBuilder of the VS Code edition's test/unit/harness.ts).</summary>
    internal sealed class ModelBuilder
    {
        public readonly FsmModel Model;
        private int _n;

        public ModelBuilder(string name, MachineKind kind = MachineKind.Behavioral)
        {
            Model = new FsmModel { Name = name, Kind = kind, Regions = { new Region { Id = "r_root" } } };
        }

        public string Vertex(VertexType type, string parent = "r_root", Action<Vertex> extra = null, string id = null)
        {
            var v = new Vertex { Id = id ?? $"v{++_n}", Type = type, Parent = parent, X = 0, Y = 0, W = 10, H = 10 };
            extra?.Invoke(v);
            Model.Vertices.Add(v);
            return v.Id;
        }

        /// <summary>A state; <paramref name="regions"/> &gt; 0 makes it composite, with region ids <c>&lt;id&gt;_r0</c>, <c>&lt;id&gt;_r1</c>, ...</summary>
        public string State(string name, string parent = "r_root", Action<Vertex> extra = null, int regions = 0, string id = null)
        {
            id = id ?? name;
            return Vertex(VertexType.State, parent, v =>
            {
                v.Name = name;
                v.Regions = Enumerable.Range(0, regions).Select(i => new Region { Id = $"{v.Id}_r{i}" }).ToList();
                extra?.Invoke(v);
            }, id);
        }

        public string T(string source, string target, Action<Transition> extra = null, string id = null)
        {
            var t = new Transition { Id = id ?? $"t{++_n}", Source = source, Target = target };
            extra?.Invoke(t);
            Model.Transitions.Add(t);
            return t.Id;
        }

        /// <summary>Initial pseudostate of <paramref name="region"/> targeting <paramref name="target"/>.</summary>
        public void Init(string target, string region = "r_root") => T(Vertex(VertexType.Initial, region), target);
    }

    internal static class TestFiles
    {
        public static string Examples(string file) => Path.Combine(AppContext.BaseDirectory, "examples", file);

        public static string Example(string file) => File.ReadAllText(Examples(file));

        public static string Old(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "old", name + ".fsm"));

        /// <summary>fsm-cli's test cases, copied by scripts/fetch-cli.sh.</summary>
        public static string Cases => Path.Combine(AppContext.BaseDirectory, "cases");

        public static string Schemas => Path.Combine(AppContext.BaseDirectory, "schemas");

        /// <summary>A machine's two files read; fails on problems.</summary>
        public static ReadMachine Read(string model, string layout = null)
        {
            var r = Format.ReadMachine(model, layout);
            Assert.False(r.Failed, r.Failed ? string.Join("; ", r.Problems) : "");
            return r;
        }

        /// <summary>An example's model, with its layout.</summary>
        public static FsmModel ExampleModel(string name) => Read(Example(name + ".fsm"), Example(name + ".fsm.layout")).Model;

        /// <summary>Writes a copy of a model (generated names given), as the editor saves it.</summary>
        public static Written Write(FsmModel model)
        {
            var m = model.Clone();
            Format.AssignNames(m);
            var w = Format.WriteMachine(m);
            Assert.Empty(w.Lost);
            return w;
        }

        /// <summary>A model's file text, as the editor writes it.</summary>
        public static string FileOf(FsmModel model) => Write(model).Model;
    }
}
