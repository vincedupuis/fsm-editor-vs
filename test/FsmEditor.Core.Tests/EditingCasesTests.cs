using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace FsmEditor.Core.Tests
{
    /// <summary>
    /// Editing parity with the VS Code edition, the reference: <c>editing/</c> is a copy of its
    /// <c>test/unit/fixtures/editing</c>, each case a machine of <c>geometry/</c>, steps (editing operations in the
    /// diagram's coordinates, run there with the mouse and the keyboard) and the two files its last edit wrote. Here the
    /// steps run on <see cref="DiagramSession"/> as <c>DiagramCanvas</c> and <c>PropertiesPanel</c> call it (the drags
    /// as the canvas does them), and must write the same files. After a change there, copy the folder here.
    /// </summary>
    public class EditingCasesTests
    {
        private static string Dir => Path.Combine(AppContext.BaseDirectory, "editing");
        private static string Geometry => Path.Combine(AppContext.BaseDirectory, "geometry");

        public static IEnumerable<object[]> Cases() =>
            Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "editing"), "*.json")
                .Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.Ordinal).Select(n => new object[] { n });

        [Fact]
        public void HasTheCases() => Assert.True(Cases().Count() >= 15, $"{Cases().Count()} cases in {Dir}");

        [Theory]
        [MemberData(nameof(Cases))]
        public void EditsAsTheVsCodeEdition(string name)
        {
            var c = (Dictionary<string, object>)ClipboardJson.ParseJson(File.ReadAllText(Path.Combine(Dir, name + ".json")));
            var machine = Path.Combine(Geometry, (string)c["machine"]);
            var s = new DiagramSession(TestFiles.Read(File.ReadAllText(machine + ".fsm"), File.ReadAllText(machine + ".fsm.layout")).Model);
            var selected = new List<string>();
            var i = 0;
            foreach (Dictionary<string, object> step in (List<object>)c["steps"])
            {
                i++;
                var op = (string)step["op"];
                Run(s, step, selected);
                if (op == "select") continue;
                // As the VS Code edition shows each edit: written, and read back (ids derived from names).
                var w = TestFiles.Write(s.Model);
                s.ReplaceModel(TestFiles.Read(w.Model, w.Layout).Model);
            }
            var written = TestFiles.Write(s.Model);
            Assert.Equal((string)c["fsm"], written.Model);
            Assert.Equal((string)c["layout"], PlainGroups(written.Layout));
        }

        /// <summary>A layout file with its group ids, which a new group takes at random, renamed group1, group2… in order of appearance.</summary>
        private static string PlainGroups(string layout)
        {
            var names = new Dictionary<string, string>();
            return System.Text.RegularExpressions.Regex.Replace(layout, "\"group\": \"([^\"]*)\"", m =>
            {
                var g = m.Groups[1].Value;
                if (!names.ContainsKey(g)) names[g] = "group" + (names.Count + 1);
                return "\"group\": \"" + names[g] + "\"";
            });
        }

        private static string Str(Dictionary<string, object> o, string k) => (string)o[k];
        private static double Num(Dictionary<string, object> o, string k) => (double)o[k];

        /// <summary>Where the VS Code edition grabs a vertex: its center, or the middle of a composite state's title bar.</summary>
        private static PointD Grab(Vertex v) => new PointD(v.X + v.W / 2, v.Regions.Count > 0 ? v.Y + 10 : v.Y + v.H / 2);

        private static void Run(DiagramSession s, Dictionary<string, object> step, List<string> selected)
        {
            Vertex V(string id) => s.Index.Vertex(id) ?? throw new InvalidOperationException("no vertex " + id);
            Transition T(string id) => s.Index.Transition(id) ?? throw new InvalidOperationException("no transition " + id);
            switch (Str(step, "op"))
            {
                case "select":
                    selected.Clear();
                    selected.AddRange(((List<object>)step["ids"]).Cast<string>());
                    s.Selection = new HashSet<string>(selected);
                    break;
                case "create":
                    s.CreateVertex(Str(step, "tool"), new PointD(Num(step, "x"), Num(step, "y")));
                    break;
                case "move":
                    Move(s, Grab(V(selected[0])), Num(step, "dx"), Num(step, "dy"));
                    break;
                case "resize":
                {
                    var v = V(selected[0]);
                    var snap = s.BeginLayoutDrag(v);
                    s.ResizeFrom(snap, v, Str(step, "corner"), v.X, v.Y, v.W, v.H, Num(step, "dx"), Num(step, "dy"), false);
                    s.Commit();
                    break;
                }
                case "divider":
                {
                    var v = V(Str(step, "state"));
                    var snap = s.BeginLayoutDrag(v);
                    s.ResizeRegionFrom(snap, v, (int)Num(step, "index"), Num(step, "delta"), false);
                    s.Commit();
                    break;
                }
                case "connect":
                    s.Connect(V(Str(step, "source")), Str(step, "target"));
                    break;
                case "delete":
                    s.DeleteSelection();
                    break;
                case "waypoint":
                    s.AddWaypoint(T(Str(step, "transition")), new PointD(Num(step, "x"), Num(step, "y")));
                    break;
                case "unwaypoint":
                    s.RemoveWaypoint(T(Str(step, "transition")), (int)Num(step, "index"));
                    break;
                case "label":
                    Assert.Null(s.ApplyLabel(T(Str(step, "transition")), Str(step, "text")));
                    break;
                case "kind":
                    s.SetKind(T(Str(step, "transition")), Str(step, "kind") == "local" ? TransitionKind.Local : Str(step, "kind") == "internal" ? TransitionKind.Internal : TransitionKind.External);
                    break;
                case "straighten":
                    s.Straighten(T(Str(step, "transition")));
                    break;
                case "reverse":
                    s.Reverse(T(Str(step, "transition")));
                    break;
                case "addRegion":
                    s.AddRegion(V(Str(step, "state")));
                    break;
                case "internal":
                    s.AddInternalTransition(V(Str(step, "state")));
                    break;
                case "removeRegion":
                {
                    var v = V(Str(step, "state"));
                    s.RemoveRegion(v, v.Regions[(int)Num(step, "index")].Id);
                    break;
                }
                case "layout":
                    s.SetRegionLayout(V(Str(step, "state")), Str(step, "layout") == "horizontal" ? RegionLayout.Horizontal : RegionLayout.Vertical);
                    break;
                case "type":
                    Assert.True(VertexTypes.TryParseKey(Str(step, "type"), out var type));
                    s.ChangeType(V(Str(step, "id")), type);
                    break;
                case "group":
                    s.ToggleGroup();
                    break;
                case "duplicate":
                    s.ResetPasteOffset();
                    s.Paste(s.CollectSelection());
                    break;
                case "leave":
                    s.LeaveParent();
                    break;
                case "align":
                    s.Align(Str(step, "anchor"), (bool)step["horizontally"]);
                    break;
                case "color":
                {
                    var id = Str(step, "id");
                    if (s.Index.Vertex(id) is Vertex v) s.SetColor(v, null, Str(step, "color"));
                    else s.SetColor(T(id), null, Str(step, "color"));
                    break;
                }
                default:
                    throw new InvalidOperationException("unknown step " + Str(step, "op"));
            }
        }

        /// <summary>A drag of the selection from <paramref name="start"/>, as <c>DiagramCanvas</c> does it (StartMove, a move, the drop).</summary>
        private static void Move(DiagramSession s, PointD start, double dx, double dy)
        {
            var ix = s.Index;
            var moving = new Dictionary<string, Vertex>();
            var roots = new List<Vertex>();
            var picked = s.WithGroupMates(s.Selection.Select(ix.Vertex).Where(v => v != null));
            var pickedIds = new HashSet<string>(picked.Select(v => v.Id));
            foreach (var v in picked)
            {
                if (ix.Ancestors(v).Any(a => pickedIds.Contains(a.Id))) continue;
                roots.Add(v);
                moving[v.Id] = v;
                foreach (var d in ix.Descendants(v)) moving[d.Id] = d;
            }
            var items = moving.Values.Select(v => (v, v.X, v.Y)).ToList();
            var transitions = s.Model.Transitions
                .Where(t => t.Points.Count > 0 && moving.ContainsKey(t.Source) && moving.ContainsKey(t.Target))
                .Select(t => (t, t.Points.ToList()))
                .ToList();
            var snap = s.Snapshot();
            var boxes = roots.ToDictionary(v => v.Id, v => new RectD(v.X, v.Y, v.W, v.H));
            var lead = roots.FirstOrDefault();
            if (lead != null && !ix.IsBorderVertex(lead))
            {
                var it = items.First(x => x.v == lead);
                dx = Core.Geometry.Snap(it.X + dx) - it.X;
                dy = Core.Geometry.Snap(it.Y + dy) - it.Y;
            }
            s.MoveFrom(snap, items, transitions, roots, boxes, dx, dy, new PointD(start.X + dx, start.Y + dy), false);
            s.DropMoved(roots, new HashSet<string>(moving.Keys));
        }
    }
}
