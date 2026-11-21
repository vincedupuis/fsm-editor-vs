using System;
using System.IO;
using System.Linq;
using Xunit;

namespace FsmEditor.IntegrationTests
{
    /// <summary>A test inside Visual Studio: skipped unless FSM_VS_INTEGRATION=1 (integration.yml, on Windows).</summary>
    public sealed class VsFactAttribute : FactAttribute
    {
        public VsFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FSM_VS_INTEGRATION") != "1") Skip = "inside Visual Studio only: FSM_VS_INTEGRATION=1 (integration.yml)";
        }
    }

    [CollectionDefinition("Visual Studio")]
    public sealed class VisualStudioCollection : ICollectionFixture<VisualStudio> { }

    /// <summary>
    /// The extension in Visual Studio's experimental instance, where integration.yml installs the VSIX built from this
    /// commit: the diagram editor, the Error List, the commands, editing through Visual Studio's own commands, undo and
    /// save of both files, old files, the Trace Player.
    /// </summary>
    [Collection("Visual Studio")]
    public sealed class VsTests : IDisposable
    {
        private const int GenerateCode = 0x0101, ExportSvg = 0x0102, OpenAsText = 0x0103, OpenDiagram = 0x0104,
            ExportPng = 0x0105, OpenTrace = 0x0106, ShowTracePlayer = 0x0107, CompareAsDiagrams = 0x0108, NewStateMachine = 0x0100;

        private readonly VisualStudio _vs;

        public VsTests(VisualStudio vs)
        {
            _vs = vs;
        }

        public void Dispose() => _vs.CloseAll();

        [VsFact]
        public void OpensAMachineAsADiagram()
        {
            _vs.Open("MediaPlayer.fsm");
            Assert.False(_vs.ActiveIsText(), "a .fsm file opens in the diagram, not a text editor");
            var diagram = _vs.Diagram();
            Assert.True(diagram.Current.BoundingRectangle.Width > 200, "the diagram is shown");
            Assert.True(_vs.Do(d => d.ActiveDocument.Saved), "opening changes nothing");
        }

        [VsFact]
        public void ReportsValidationProblemsInTheErrorListAtTheirLines()
        {
            const string play = "{ \"type\": \"external\", \"trigger\": \"play\", \"target\": \"Playing\" }";
            var media = File.ReadAllText(_vs.PathOf("MediaPlayer.fsm"));
            Assert.Contains(play, media);
            File.WriteAllText(_vs.PathOf("Broken.fsm"), media.Replace(play, "{ \"type\": \"external\", \"trigger\": \"play\", \"guard\": \"count > 0\", \"target\": \"Playing\" }"));
            File.Copy(_vs.PathOf("MediaPlayer.fsm.layout"), _vs.PathOf("Broken.fsm.layout"), true);
            _vs.Open("Broken.fsm");
            var line = media.Split('\n').ToList().FindIndex(l => l.Contains(play)) + 1;
            string found = null;
            VisualStudio.Until("the guard's problem in the Error List", () =>
            {
                found = _vs.Do(d =>
                {
                    var items = ((EnvDTE80.DTE2)d).ToolWindows.ErrorList.ErrorItems;
                    for (var i = 1; i <= items.Count; i++)
                    {
                        var e = items.Item(i);
                        if (e.FileName.EndsWith("Broken.fsm", StringComparison.OrdinalIgnoreCase) && e.Description.StartsWith("Guard:")) return $"{e.Line}|{e.Description}";
                    }
                    return null;
                });
                return found != null;
            });
            Assert.Equal(line, int.Parse(found.Split('|')[0]));
            Assert.Contains("no variables", found);
        }

        [VsFact]
        public void RegistersItsCommands()
        {
            foreach (var id in new[] { NewStateMachine, GenerateCode, ExportSvg, OpenAsText, OpenDiagram, ExportPng, OpenTrace, ShowTracePlayer, CompareAsDiagrams })
            {
                var name = _vs.CommandName(id);
                Assert.False(string.IsNullOrEmpty(name), $"command 0x{id:x4} has a name");
            }
        }

        [VsFact]
        public void OpensTheMachineAsTextAndBack()
        {
            _vs.Open("Payment.fsm");
            _vs.Raise(OpenAsText);
            VisualStudio.Until("the text editor", () => _vs.ActiveIsText() && _vs.Do(d => d.ActiveDocument.FullName.EndsWith("Payment.fsm", StringComparison.OrdinalIgnoreCase)));
            _vs.Raise(OpenDiagram);
            VisualStudio.Until("the diagram again", () => !_vs.ActiveIsText() && _vs.Do(d => d.ActiveDocument.FullName.EndsWith("Payment.fsm", StringComparison.OrdinalIgnoreCase)));
        }

        [VsFact]
        public void EditsThroughVisualStudiosCommandsUndoesAndSavesBothFiles()
        {
            var model = _vs.PathOf("Order.fsm");
            var layout = model + ".layout";
            var before = File.ReadAllText(model);
            _vs.Open("Order.fsm");
            // The diagram has the focus; Visual Studio's Select All, Copy and Paste (its keys) reach it.
            _vs.ClickDiagram(0.97, 0.97);
            Input.Ctrl((byte)'A');
            Input.Ctrl((byte)'C');
            Input.Ctrl((byte)'V');
            VisualStudio.Until("the document changed", () => !_vs.Do(d => d.ActiveDocument.Saved));
            _vs.Execute("Edit.Undo");
            VisualStudio.Until("undone", () => _vs.Do(d => d.ActiveDocument.Saved));
            _vs.Execute("Edit.Redo");
            VisualStudio.Until("redone", () => !_vs.Do(d => d.ActiveDocument.Saved));
            var layoutBefore = File.ReadAllText(layout);
            _vs.Do(d => d.ActiveDocument.Save());
            VisualStudio.Until("saved", () => _vs.Do(d => d.ActiveDocument.Saved));
            var after = File.ReadAllText(model);
            Assert.NotEqual(before, after);
            Assert.Contains("\"name\": \"Cart2\"", after);
            Assert.NotEqual(layoutBefore, File.ReadAllText(layout));
        }

        [VsFact]
        public void OpensAnOldFileUnchanged()
        {
            File.WriteAllText(_vs.PathOf("Old.fsm"), Old);
            _vs.Open("Old.fsm");
            Assert.False(_vs.ActiveIsText(), "an old file opens in the diagram");
            _vs.Diagram();
            Assert.True(_vs.Do(d => d.ActiveDocument.Saved), "opening it converts nothing");
            Assert.Equal(Old, File.ReadAllText(_vs.PathOf("Old.fsm")));
        }

        [VsFact]
        public void ShowsTheTracePlayer()
        {
            _vs.Raise(ShowTracePlayer);
            VisualStudio.Until("the Trace Player window", () => _vs.Do(d =>
            {
                foreach (EnvDTE.Window w in d.Windows)
                {
                    if (w.Caption.Contains("Trace Player") && w.Visible) return true;
                }
                return false;
            }));
        }

        /// <summary>A machine in the old format: an initial pseudostate, an unnamed state and a choice.</summary>
        private const string Old = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<xmi:XMI xmi:version=""20131001"" xmlns:xmi=""http://www.omg.org/spec/XMI/20131001"" xmlns:uml=""http://www.omg.org/spec/UML/20161101"">
  <uml:Model xmi:id=""m"" name=""Old"">
    <packagedElement xmi:type=""uml:StateMachine"" xmi:id=""sm"" name=""Old"">
      <region xmi:type=""uml:Region"" xmi:id=""r"">
        <subvertex xmi:type=""uml:Pseudostate"" xmi:id=""i"" kind=""initial""/>
        <subvertex xmi:type=""uml:State"" xmi:id=""a"" name=""Idle""/>
        <subvertex xmi:type=""uml:State"" xmi:id=""b""/>
        <transition xmi:type=""uml:Transition"" xmi:id=""t0"" source=""i"" target=""a""/>
        <transition xmi:type=""uml:Transition"" xmi:id=""t1"" source=""a"" target=""b""><trigger xmi:id=""g"" name=""go""/></transition>
      </region>
    </packagedElement>
  </uml:Model>
</xmi:XMI>
";
    }
}
