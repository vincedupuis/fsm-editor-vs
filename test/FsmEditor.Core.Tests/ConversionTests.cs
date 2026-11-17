using System.Linq;
using FsmEditor.Core;
using Xunit;
using static FsmEditor.Core.Tests.TestFiles;

namespace FsmEditor.Core.Tests
{
    /// <summary>Old files (XMI): read, and converted (the VS Code edition's test/unit/xmi.test.ts).</summary>
    public class ConversionTests
    {
        /// <summary>A small old file: <paramref name="body"/> inside the machine's region.</summary>
        private static string Xmi(string body, string name = "M", string id = "sm") => $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<xmi:XMI xmi:version=""20131001"" xmlns:xmi=""http://www.omg.org/spec/XMI/20131001"" xmlns:uml=""http://www.omg.org/spec/UML/20161101"">
  <uml:Model xmi:id=""m"" name=""{name}"">
    <packagedElement xmi:type=""uml:StateMachine"" xmi:id=""{id}"" name=""{name}"">
      <region xmi:type=""uml:Region"" xmi:id=""r"">
{body}
      </region>
    </packagedElement>
  </uml:Model>
</xmi:XMI>
";

        [Fact]
        public void AreRecognizedAndReadAsTheyAre()
        {
            Assert.True(Format.IsOldFormat(Old("MediaPlayer")));
            Assert.False(Format.IsOldFormat(Example("MediaPlayer.fsm")));
            var m = Core.Xmi.Read(Old("MediaPlayer"));
            Assert.Equal("MediaPlayer", m.Context);
            Assert.Equal("updateVolume()", m.Model.Transitions.First(t => t.Id == "t22").Action);
        }

        [Fact]
        public void ConvertGivesNamesAndReportsEveryRenameAndLoss()
        {
            var r = Conversion.ConvertGroup(new[] { new ConvertInput("/w/M.fsm", Xmi(@"        <subvertex xmi:type=""uml:Pseudostate"" xmi:id=""i"" kind=""initial"" name=""start""/>
        <subvertex xmi:type=""uml:State"" xmi:id=""a"" name=""Idle""/>
        <subvertex xmi:type=""uml:State"" xmi:id=""b"" name=""Idle""/>
        <subvertex xmi:type=""uml:State"" xmi:id=""c"" name=""a.b*""/>
        <subvertex xmi:type=""uml:State"" xmi:id=""w"" name="" Waiting  for payment""/>
        <subvertex xmi:type=""uml:State"" xmi:id=""d""/>
        <subvertex xmi:type=""uml:Pseudostate"" xmi:id=""j"" kind=""junction""/>
        <transition xmi:type=""uml:Transition"" xmi:id=""t0"" source=""i"" target=""a""/>
        <transition xmi:type=""uml:Transition"" xmi:id=""t1"" source=""a"" target=""i""/>
        <transition xmi:type=""uml:Transition"" xmi:id=""t2"" source=""a"" target=""b""><effect xmi:type=""uml:OpaqueBehavior"" xmi:id=""e""><body>go()</body></effect></transition>")) })[0];
            Assert.Equal(new[] { "the state 'a.b*' is now 'a_b_'", "the state ' Waiting  for payment' is now 'Waiting_for_payment'", "the state 'Idle' is now 'Idle2' (its name was used twice)" }, r.Renames);
            Assert.Equal(2, r.Generated);
            Assert.Equal(new[] { "the name of the initial pseudostate 'start'", "the transition from state 'Idle' to an initial pseudostate" }, r.Losses);
            var read = Read(r.ModelText, r.LayoutText);
            Assert.Equal(new[] { "", "Idle", "Idle2", "a_b_", "Waiting_for_payment", "state1*", "junction1*" }, read.Model.Vertices.Select(v => v.Name));
            Assert.Contains("{ \"type\": \"external\", \"action\": \"go()\", \"target\": \"Idle2\" }", r.ModelText);
        }

        [Fact]
        public void ConvertsLinkedFilesTogether()
        {
            var results = Conversion.ConvertGroup(new[] { "Order", "Payment" }.Select(n => new ConvertInput($"/x/{n}.fsm", Old(n))).ToList());
            var order = Read(results[0].ModelText, results[0].LayoutText);
            Assert.Equal(new[] { ("retry", PointKind.Entry), ("failed", PointKind.Exit), ("cancelled", PointKind.Exit) },
                order.Model.Vertices.Where(v => v.Type == VertexType.ConnectionPointRef).Select(v => (v.Ref, v.PointKind)));
            // Alone, Order can't know Payment's points: the ids stay, and the loss is said.
            var alone = Conversion.ConvertGroup(new[] { new ConvertInput("/x/Order.fsm", Old("Order")) })[0];
            Assert.Equal(3, alone.Losses.Count);
            Assert.Matches("point 'p_retry' of Payment\\.fsm, which was not found", alone.Losses[0]);
        }

        [Fact]
        public void AFileThatIsNotXmiCanNotBeConverted()
        {
            var r = Conversion.ConvertGroup(new[] { new ConvertInput("/x/A.fsm", "<a/>") })[0];
            Assert.Contains("Not an XMI document", r.Error);
        }

        [Fact]
        public void FindsTheOldFilesLinkedBothWays()
        {
            var files = new[] { "Order", "Payment" }.Select(n => new ConvertInput($"/x/{n}.fsm", Old(n))).ToList();
            var others = files.Concat(new[] { new ConvertInput("/x/Other.fsm", Xmi("", "Other")), new ConvertInput("/x/New.fsm", Example("MediaPlayer.fsm")) }).ToList();
            // From the submachine: the machine using it.
            var group = MachineFiles.LinkedOldFiles("/x/Payment.fsm", Old("Payment"), others, _ => null);
            Assert.Equal(new[] { "/x/Payment.fsm", "/x/Order.fsm" }, group.Select(g => g.Path));
            // From the machine using it, without the workspace: read by path.
            group = MachineFiles.LinkedOldFiles("/x/Order.fsm", Old("Order"), new ConvertInput[0], p => p == "/x/Payment.fsm" ? Old("Payment") : null);
            Assert.Equal(new[] { "/x/Order.fsm", "/x/Payment.fsm" }, group.Select(g => g.Path));
        }

        [Fact]
        public void HrefsOfOldFilesDecodeAsDecodeUriDoes()
        {
            Assert.Equal("My Machine.fsm", Hrefs.DecodeUri("My%20Machine.fsm"));
            Assert.Equal("café.fsm", Hrefs.DecodeUri("caf%C3%A9.fsm"));
            Assert.Equal("a#b%2Fc?", Hrefs.DecodeUri("a%23b%2Fc%3F"));
            Assert.Equal("café%zz", Hrefs.DecodeUri("caf%C3%A9%zz"));
            Assert.Equal("a%E0.fsm", Hrefs.DecodeUri("a%E0.fsm"));
            Assert.Equal("a%E0A.fsm", Hrefs.DecodeUri("a%E0%41.fsm"));
            Assert.Equal("a%C0%80%", Hrefs.DecodeUri("a%C0%80%"));
        }
    }
}
