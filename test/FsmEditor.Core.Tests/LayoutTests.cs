using System.Collections.Generic;
using System.Linq;
using FsmEditor.Core;
using Xunit;

namespace FsmEditor.Core.Tests
{
    /// <summary>Elements stay inside their composite state's region, which grows for them; regions have their own sizes.</summary>
    public class LayoutTests
    {
        private static Vertex State(string id, string parent, double x, double y, double w = 100, double h = 50, params string[] regions)
        {
            var v = new Vertex { Id = id, Type = VertexType.State, Name = id, Parent = parent, X = x, Y = y, W = w, H = h };
            foreach (var r in regions) v.Regions.Add(new Region { Id = r });
            return v;
        }

        /// <summary>Q holds P, whose stacked regions r1 (A) and r2 (C, D, a transition with a bend point) share it equally; headers are 24 high.</summary>
        private static DiagramSession Nested()
        {
            var m = new FsmModel { Id = "sm", Name = "T", Regions = { new Region { Id = "r0" } } };
            m.Vertices.Add(State("Q", "r0", 50, 50, 600, 500, "rq"));
            m.Vertices.Add(State("P", "rq", 100, 100, 300, 260, "r1", "r2"));
            m.Vertices.Add(State("A", "r1", 130, 150));
            m.Vertices.Add(State("C", "r2", 130, 270));
            m.Vertices.Add(State("D", "r2", 260, 270));
            m.Vertices.Add(State("B", "r0", 700, 150));
            m.Transitions.Add(new Transition { Id = "t1", Source = "C", Target = "D", Points = { new PointD(245, 330) } });
            return new DiagramSession(m);
        }

        private static Vertex V(DiagramSession s, string id) => s.Index.Vertex(id);

        private static void Move(DiagramSession s, DiagramSession.LayoutSnapshot snap, Vertex v, double dx, double dy)
        {
            var items = new[] { v }.Concat(s.Index.Descendants(v)).Select(x => (x, x.X, x.Y)).ToList();
            s.Restore(snap);
            items = new[] { v }.Concat(s.Index.Descendants(v)).Select(x => (x, x.X, x.Y)).ToList();
            var boxes = new Dictionary<string, RectD> { [v.Id] = new RectD(v.X, v.Y, v.W, v.H) };
            s.MoveFrom(snap, items, new List<(Transition, List<PointD>)>(), new List<Vertex> { v }, boxes, dx, dy, v.Center, false);
        }

        [Fact]
        public void MovingPastARegionGrowsItAndMovingBackShrinksIt()
        {
            var s = Nested();
            var snap = s.Snapshot();
            Move(s, snap, V(s, "A"), 0, 60);
            Assert.Equal(148, V(s, "P").Regions[0].Size);
            Assert.Equal(290, V(s, "P").H);
            Assert.Equal(300, V(s, "C").Y);
            Assert.Equal(300, V(s, "D").Y);
            Assert.Equal(360, s.Index.Transition("t1").Points[0].Y);
            Move(s, snap, V(s, "A"), 0, 20);
            Assert.Equal(260, V(s, "P").H);
            Assert.Equal(270, V(s, "C").Y);
        }

        [Fact]
        public void GrowingCascadesToTheEnclosingStates()
        {
            var s = Nested();
            Move(s, s.Snapshot(), V(s, "A"), 0, 300);
            Assert.Equal(530, V(s, "P").H);
            Assert.Equal(590, V(s, "Q").H);
        }

        [Fact]
        public void MovingUpAndLeftGrowsThoseSides()
        {
            var s = Nested();
            Move(s, s.Snapshot(), V(s, "A"), -60, -60);
            var p = V(s, "P");
            var a = V(s, "A");
            Assert.True(p.X <= a.X - 10 && p.Y + 24 <= a.Y - 10);
            Assert.Equal((400.0, 360.0), (p.X + p.W, p.Y + p.H));
        }

        [Fact]
        public void DroppedElementsNestOnlyWhenWhollyInsideARegion()
        {
            var s = Nested();
            var b = V(s, "B");
            var moving = new HashSet<string> { "B" };
            (b.X, b.Y) = (280, 160);
            s.DropMoved(new[] { b }, moving);
            Assert.Equal("r1", b.Parent);

            s = Nested();
            b = V(s, "B");
            (b.X, b.Y) = (600, 160);
            s.DropMoved(new[] { b }, moving);
            Assert.Equal("r0", b.Parent);
            Assert.Contains(s.OverlapIssues(), i => i.Id == "B" && i.Message == "Overlaps 'Q'.");
            Assert.Contains(s.OverlapIssues(), i => i.Id == "Q" && i.Message == "Overlaps 'B'.");
            Assert.DoesNotContain(s.OverlapIssues(), i => i.Id == "A");
        }

        [Fact]
        public void LeavingTheParentKeepsThePlace()
        {
            var s = Nested();
            s.Selection = new HashSet<string> { "A" };
            Assert.True(s.CanLeaveParent);
            s.LeaveParent();
            var a = V(s, "A");
            Assert.Equal("rq", a.Parent);
            Assert.Equal((130.0, 150.0), (a.X, a.Y));
            Assert.Contains(s.OverlapIssues(), i => i.Id == "A" && i.Message == "Overlaps 'P'.");
            // Dropped back entirely inside its old state, it nests again.
            s.DropMoved(new[] { a }, new HashSet<string> { "A" });
            Assert.Equal("r1", a.Parent);
            s.Selection = new HashSet<string> { "B" };
            Assert.False(s.CanLeaveParent);
        }

        [Fact]
        public void TheLineBetweenRegionsResizesTheRegionBeforeIt()
        {
            var s = Nested();
            var p = V(s, "P");
            var snap = s.BeginLayoutDrag(p);
            s.ResizeRegionFrom(snap, p, 0, 50, false);
            Assert.Equal((168.0, 310.0, 320.0), (p.Regions[0].Size.Value, p.H, V(s, "C").Y));
            s.ResizeRegionFrom(snap, p, 0, -92, false);
            Assert.Equal((86.0, 228.0, 238.0), (p.Regions[0].Size.Value, p.H, V(s, "C").Y));
        }

        [Fact]
        public void ResizingStopsAtTheContentsAndTheTopEdgeResizesTheFirstRegion()
        {
            var s = Nested();
            var p = V(s, "P");
            var snap = s.BeginLayoutDrag(p);
            s.ResizeFrom(snap, p, "se", 100, 100, 300, 260, -200, -200, false);
            Assert.Equal((270.0, 230.0), (p.W, p.H));
            s.ResizeFrom(snap, p, "nw", 100, 100, 300, 260, 0, -20, false);
            Assert.Equal((80.0, 138.0, 270.0), (p.Y, p.Regions[0].Size.Value, V(s, "C").Y));
        }

        [Fact]
        public void ResizingANestedStatePastItsRegionGrowsIt()
        {
            var s = Nested();
            var a = V(s, "A");
            var snap = s.BeginLayoutDrag(a);
            s.ResizeFrom(snap, a, "se", 130, 150, 100, 50, 200, 0, false);
            Assert.Equal(440, V(s, "P").X + V(s, "P").W);
        }

        [Fact]
        public void ATallerHeaderMovesTheRegionsDown()
        {
            var s = Nested();
            V(s, "P").Entry = "doIt()";
            s.Commit();
            Assert.Equal((280.0, 170.0, 290.0), (V(s, "P").H, V(s, "A").Y, V(s, "C").Y));
        }

        [Fact]
        public void SwitchingTheLayoutKeepsTheContentsInTheirRegions()
        {
            var s = Nested();
            var p = V(s, "P");
            s.SetRegionLayout(p, RegionLayout.Horizontal);
            Assert.Equal((150.0, 270.0), (p.Regions[0].Size.Value, p.Regions[1].Size.Value));
            Assert.Equal((420.0, 260.0), (p.W, p.H));
            Assert.Equal((130.0, 150.0), (V(s, "A").X, V(s, "A").Y));
            Assert.Equal((280.0, 152.0), (V(s, "C").X, V(s, "C").Y));
        }

        [Fact]
        public void PastingAtAPointGoesIntoTheRegionThere()
        {
            var s = Nested();
            s.Selection = new HashSet<string> { "A" };
            var data = ClipboardJson.TryRead(ClipboardJson.Write(s.CollectSelection()));
            s.Paste(data, new PointD(350, 300));
            var copy = s.Model.Vertices.Single(v => v.Name == "A" && v.Id != "A");
            Assert.Equal(("r2", 350.0, 300.0), (copy.Parent, copy.X, copy.Y));
            Assert.Equal((360.0, 260.0), (V(s, "P").W, V(s, "P").H));
        }

        [Fact]
        public void AddedRegionsComeAfterAndRemovedOnesShrinkTheState()
        {
            var s = Nested();
            var p = V(s, "P");
            s.AddRegion(p);
            Assert.Equal(3, p.Regions.Count);
            Assert.Equal((360.0, 100.0), (p.H, p.Regions[2].Size.Value));
            s.RemoveRegion(p, "r1");
            Assert.Equal((242.0, 152.0), (p.H, V(s, "C").Y));
            Assert.Null(V(s, "A"));
        }

        [Fact]
        public void RegionSizesGoThroughTheClipboard()
        {
            var s = Nested();
            var p = V(s, "P");
            p.Regions[0].Size = 100;
            s.Selection = new HashSet<string> { "P" };
            var json = ClipboardJson.Write(s.CollectSelection());
            Assert.Contains("{\"id\":\"r1\",\"name\":\"\",\"size\":100}", json);
            Assert.Equal(100, ClipboardJson.TryRead(json).Vertices.Single(v => v.Id == "P").Regions[0].Size);
        }
    }
}
