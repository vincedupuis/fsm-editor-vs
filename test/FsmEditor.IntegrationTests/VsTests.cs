using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
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
    /// commit: the scenarios of scenarios.md (a copy of the VS Code edition's test/scenarios.md), each test naming the
    /// ones it implements (a trait <c>Scenario</c> whose value is the id, which ScenarioTests of FsmEditor.Core.Tests checks
    /// against the list).
    /// </summary>
    [Collection("Visual Studio")]
    public sealed class VsTests : IDisposable
    {
        private const int GenerateCode = 0x0101, ExportSvg = 0x0102, OpenAsText = 0x0103, OpenDiagram = 0x0104,
            ExportPng = 0x0105, OpenTrace = 0x0106, ShowTracePlayer = 0x0107, CompareAsDiagrams = 0x0108, NewStateMachine = 0x0100;

        /// <summary><c>Stopped -play-&gt; Playing</c> of MediaPlayer.fsm, and the guard Broken.fsm gives it.</summary>
        private const string Play = "{ \"type\": \"external\", \"trigger\": \"play\", \"target\": \"Playing\" }";
        private const string PlayGuarded = "{ \"type\": \"external\", \"trigger\": \"play\", \"guard\": \"count > 0\", \"target\": \"Playing\" }";

        private readonly VisualStudio _vs;

        public VsTests(VisualStudio vs)
        {
            _vs = vs;
        }

        public void Dispose() => _vs.CloseAll();

        private static readonly TimeSpan Dialog = TimeSpan.FromSeconds(60);

        private string Read(string name) => File.ReadAllText(_vs.PathOf(name));

        private void Write(string name, string text)
        {
            var path = _vs.PathOf(name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        /// <summary>A machine of the examples copied under another name (its two files).</summary>
        private void CopyMachine(string from, string to, Func<string, string> change = null)
        {
            Write(to, (change ?? (t => t))(Read(from)));
            Write(to + ".layout", Read(from + ".layout"));
        }

        /// <summary>Broken.fsm: MediaPlayer.fsm whose <c>Stopped -play-&gt; Playing</c> has the guard <c>count &gt; 0</c>.</summary>
        private void WriteBroken()
        {
            Assert.Contains(Play, Read("MediaPlayer.fsm"));
            // The first of the two (Paused has the same transition).
            CopyMachine("MediaPlayer.fsm", "Broken.fsm", t => t.Substring(0, t.IndexOf(Play, StringComparison.Ordinal)) + PlayGuarded + t.Substring(t.IndexOf(Play, StringComparison.Ordinal) + Play.Length));
        }

        /// <summary>The Error List's problem about the guard on Broken.fsm (<c>Guard: …</c>), or null.</summary>
        private (string File, int Line, string Description)? GuardProblem(string name = "Broken.fsm") =>
            _vs.ErrorsOn(name).Where(e => e.Description.StartsWith("Guard:", StringComparison.Ordinal)).Cast<(string, int, string)?>().FirstOrDefault();

        private bool ActiveIs(string name) => _vs.Do(d => string.Equals(d.ActiveDocument?.FullName, _vs.PathOf(name), StringComparison.OrdinalIgnoreCase));

        /// <summary>Opens a machine in the diagram, Visual Studio in front.</summary>
        private void OpenDiagramOf(string name)
        {
            _vs.Open(name);
            Assert.False(_vs.ActiveIsText(), $"{name} opens in the diagram");
            _vs.Diagram();
            _vs.Do(d => d.ActiveDocument.Activate());
        }

        /// <summary>Saves the active document (and waits until it is no longer marked changed).</summary>
        private void Save()
        {
            _vs.Do(d => d.ActiveDocument.Save());
            VisualStudio.Until("saved", () => _vs.Do(d => d.ActiveDocument.Saved));
        }

        /// <summary>Runs a command of the extension that shows a modal dialog, which <paramref name="answer"/> answers.</summary>
        private void RaiseWithDialog(int id, string args, string title, Action<AutomationElement> answer)
        {
            var name = _vs.CommandName(id);
            var run = _vs.Begin(d => d.ExecuteCommand(name, args ?? ""));
            answer(_vs.WaitDialog(title, Dialog));
            Assert.True(run.Wait(Dialog), $"{name} returned");
        }

        /// <summary>Validation has run for what was opened before: Broken.fsm, opened last, has its guard's problem.</summary>
        private void OpenBrokenAndWaitForItsProblem()
        {
            WriteBroken();
            _vs.Open("Broken.fsm");
            VisualStudio.Until("the guard's problem in the Error List", () => GuardProblem() != null);
        }

        // ---------------------------------------------------------------- documents

        [VsFact]
        [Trait("Scenario", "open-diagram")]
        public void OpensAMachineAsADiagram()
        {
            _vs.Open("MediaPlayer.fsm");
            Assert.False(_vs.ActiveIsText(), "a .fsm file opens in the diagram, not a text editor");
            var diagram = _vs.Diagram();
            Assert.True(diagram.Current.BoundingRectangle.Width > 200, "the diagram is shown");
            Assert.True(_vs.Do(d => d.ActiveDocument.Saved), "opening changes nothing");
        }

        [VsFact]
        [Trait("Scenario", "open-as-text-and-back")]
        public void OpensTheMachineAsTextAndBack()
        {
            _vs.Open("Payment.fsm");
            _vs.Raise(OpenAsText);
            VisualStudio.Until("the text editor", () => _vs.ActiveIsText() && ActiveIs("Payment.fsm"));
            _vs.Raise(OpenDiagram);
            VisualStudio.Until("the diagram again", () => !_vs.ActiveIsText() && ActiveIs("Payment.fsm"));
            _vs.Diagram();
        }

        /// <summary>The JSON editor's schema box (Schema Address Bar) of the active text editor: the schema it applies.</summary>
        private string SchemaOfActiveEditor()
        {
            string schema = null;
            VisualStudio.Until("the JSON editor's schema box", () =>
            {
                var box = AutomationElement.FromHandle(_vs.MainWindow).FindAll(TreeScope.Descendants, new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox), new PropertyCondition(AutomationElement.NameProperty, "Schema Address Bar (F4)")))
                    .Cast<AutomationElement>().FirstOrDefault(e => !e.Current.IsOffscreen);
                if (box == null) return false;
                schema = ((ValuePattern)box.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
                return true;
            }, null, () => "combo boxes shown: " + string.Join(", ", AutomationElement.FromHandle(_vs.MainWindow)
                .FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox))
                .Cast<AutomationElement>().Where(e => !e.Current.IsOffscreen).Select(e => $"'{e.Current.Name}'")));
            return schema;
        }

        [VsFact]
        [Trait("Scenario", "text-editor-json")]
        public void OpensTheFilesAsJsonWithTheirSchemas()
        {
            _vs.Open("Payment.fsm");
            _vs.Raise(OpenAsText);
            VisualStudio.Until("the model file in the text editor", () => _vs.ActiveIsText() && ActiveIs("Payment.fsm"));
            var model = SchemaOfActiveEditor();
            // With the model file's text still open: Visual Studio's JSON editor, its last view closed, shuts down for a
            // while, and a view opened meanwhile has no schema bar (Globals.Initialize can't be called during shutdown).
            _vs.Open("Payment.fsm.layout");
            Assert.True(_vs.ActiveIsText(), "the layout file opens in the text editor");
            var layout = SchemaOfActiveEditor();
            Assert.True(model.EndsWith("fsm.schema.json", StringComparison.OrdinalIgnoreCase), $"the model file's schema is fsm-cli's fsm.schema.json, not {model}");
            Assert.True(layout.EndsWith("fsm-layout.schema.json", StringComparison.OrdinalIgnoreCase), $"the layout file's schema is fsm-editor-common's fsm-layout.schema.json, not {layout}");
        }

        /// <summary>Replaces a text in the active text editor (unsaved), through its TextDocument.</summary>
        private void EditText(string from, string to)
        {
            _vs.Do(d =>
            {
                var doc = (EnvDTE.TextDocument)d.ActiveDocument.Object("TextDocument");
                var text = doc.StartPoint.CreateEditPoint().GetText(doc.EndPoint).Replace("\r\n", "\n");
                var at = text.IndexOf(from, StringComparison.Ordinal);
                Assert.True(at >= 0, $"the text holds {from}");
                var line = text.Substring(0, at).Count(c => c == '\n') + 1;
                var column = at - (text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1) + 1;
                var point = doc.StartPoint.CreateEditPoint();
                point.MoveToLineAndOffset(line, column);
                point.Delete(from.Length);
                point.Insert(to);
            });
        }

        [VsFact]
        [Trait("Scenario", "text-edit-reaches-diagram")]
        public void ATextEditReachesTheDiagram()
        {
            // Open as Text closes the diagram first (a text editor can't share its document): the edit is made and
            // saved in the text editor, then the diagram is opened again.
            WriteBroken();
            _vs.Open("Broken.fsm");
            VisualStudio.Until("the guard's problem in the Error List", () => GuardProblem() != null);
            _vs.Raise(OpenAsText);
            VisualStudio.Until("the text editor", () => _vs.ActiveIsText() && ActiveIs("Broken.fsm"));
            EditText("count > 0", "isReady()");
            Save();
            _vs.Raise(OpenDiagram);
            VisualStudio.Until("the diagram again", () => !_vs.ActiveIsText() && ActiveIs("Broken.fsm"));
            _vs.Diagram();
            VisualStudio.Until("the guard's problem cleared", () => GuardProblem() == null);
            Assert.Contains("\"guard\": \"isReady()\"", Read("Broken.fsm"));
        }

        [VsFact]
        [Trait("Scenario", "text-closed-unsaved")]
        public void TheTextClosedUnsavedLeavesTheFile()
        {
            CopyMachine("MediaPlayer.fsm", "Unsaved.fsm");
            var before = Read("Unsaved.fsm");
            _vs.Open("Unsaved.fsm");
            _vs.Diagram();
            _vs.Raise(OpenAsText);
            VisualStudio.Until("the text editor", () => _vs.ActiveIsText() && ActiveIs("Unsaved.fsm"));
            EditText(Play, PlayGuarded);
            Assert.False(_vs.Do(d => d.ActiveDocument.Saved), "the text editor has the edit");
            _vs.Do(d => d.ActiveDocument.Close(EnvDTE.vsSaveChanges.vsSaveChangesNo));
            // The diagram shows the file again: not the edit (no guard, so no problem about it), not marked changed.
            _vs.Open("Unsaved.fsm");
            Assert.False(_vs.ActiveIsText(), "the file opens in the diagram again");
            _vs.Diagram();
            OpenBrokenAndWaitForItsProblem();
            Assert.Null(GuardProblem("Unsaved.fsm"));
            Assert.True(_vs.Saved(_vs.PathOf("Unsaved.fsm")), "not marked changed");
            Assert.Equal(before, Read("Unsaved.fsm"));
        }

        [VsFact]
        [Trait("Scenario", "old-file-opens-unchanged")]
        public void OpensAnOldFileUnchanged()
        {
            File.Delete(_vs.PathOf("Old.fsm.layout"));
            Write("Old.fsm", OldText);
            _vs.Open("Old.fsm");
            Assert.False(_vs.ActiveIsText(), "an old file opens in the diagram");
            _vs.Diagram();
            Assert.True(_vs.Do(d => d.ActiveDocument.Saved), "opening it converts nothing");
            Assert.Equal(OldText, Read("Old.fsm"));
            Assert.False(File.Exists(_vs.PathOf("Old.fsm.layout")), "no layout file is written");
        }

        [VsFact]
        [Trait("Scenario", "old-file-saved-converts")]
        public void SavingAnOldFileConvertsIt()
        {
            File.Delete(_vs.PathOf("Old.fsm.layout"));
            Write("Old.fsm", OldText);
            OpenDiagramOf("Old.fsm");
            // Saving says what it converted, in a message box.
            var save = _vs.Begin(d => d.ActiveDocument.Save());
            var message = _vs.WaitDialog("Microsoft Visual Studio", Dialog);
            Assert.Contains("no longer saved in XMI but in JSON", VisualStudio.TextOf(message));
            VisualStudio.Press(message, "OK");
            Assert.True(save.Wait(Dialog), "saved");
            var model = Read("Old.fsm");
            Assert.StartsWith("{", model);
            Assert.Contains("\"name\": \"state1*\"", model);
            Assert.Contains("\"name\": \"Idle\"", model);
            Assert.True(File.Exists(_vs.PathOf("Old.fsm.layout")), "its layout file is written");
        }

        /// <summary>Solution Explorer's item of a file of the folder (Folder View).</summary>
        private AutomationElement SolutionExplorerItem(string name)
        {
            _vs.Execute("View.SolutionExplorer");
            AutomationElement item = null;
            VisualStudio.Until($"{name} in Solution Explorer", () => (item = AutomationElement.FromHandle(_vs.MainWindow).FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem), new PropertyCondition(AutomationElement.NameProperty, name)))) != null);
            return item;
        }

        [VsFact]
        [Trait("Scenario", "layout-follows-rename")]
        public void TheLayoutFileFollowsARename()
        {
            CopyMachine("Payment.fsm", "Rename.fsm");
            // Renamed in Solution Explorer: selected, F2, the new name, Enter.
            var item = SolutionExplorerItem("Rename.fsm");
            ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Input.Foreground(_vs.MainWindow);
            item.SetFocus();
            Input.Key(0x71); // F2
            AutomationElement box = null;
            VisualStudio.Until("the name's box", () => (box = AutomationElement.FromHandle(_vs.MainWindow).FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
                .Cast<AutomationElement>().FirstOrDefault(e => e.Current.HasKeyboardFocus)) != null);
            ((ValuePattern)box.GetCurrentPattern(ValuePattern.Pattern)).SetValue("Renamed.fsm");
            Input.Key(0x0D);
            VisualStudio.Until("Renamed.fsm", () => File.Exists(_vs.PathOf("Renamed.fsm")));
            VisualStudio.Until("the layout file renamed with it", () => File.Exists(_vs.PathOf("Renamed.fsm.layout")), TimeSpan.FromSeconds(15));
            Assert.False(File.Exists(_vs.PathOf("Rename.fsm.layout")), "not left under its old name");
        }

        [VsFact]
        [Trait("Scenario", "new-state-machine")]
        public void NewFileStateMachineOpensInTheDiagram()
        {
            // File › New › File… › State Machine (the item template for General).
            var before = _vs.Do(d => d.Documents.Count);
            _vs.Do(d => d.ItemOperations.NewFile("General\\State Machine", "Traffic"));
            string path = null;
            VisualStudio.Until("the new machine open", () => (path = _vs.Do(d => d.ActiveDocument?.FullName)) != null && path.EndsWith(".fsm", StringComparison.OrdinalIgnoreCase));
            Assert.False(_vs.ActiveIsText(), "it opens in the diagram");
            _vs.Diagram();
            Assert.Contains("\"name\": \"Idle\"", File.ReadAllText(path));
            // The New File dialog names each file of a template on its own: the template gives it the model file only,
            // whose layout file the editor writes at the first save.
            var open = _vs.Do(d => d.Documents.Cast<EnvDTE.Document>().Select(x => x.FullName).ToList());
            Assert.DoesNotContain(open, f => f.EndsWith(".layout", StringComparison.OrdinalIgnoreCase));
        }

        // ---------------------------------------------------------------- problems

        [VsFact]
        [Trait("Scenario", "validation-at-line")]
        public void ReportsValidationProblemsInTheErrorListAtTheirLines()
        {
            OpenBrokenAndWaitForItsProblem();
            var line = Read("Broken.fsm").Split('\n').ToList().FindIndex(l => l.Contains(PlayGuarded)) + 1;
            var problem = GuardProblem().Value;
            Assert.Equal(line, problem.Line);
            Assert.Contains("no variables", problem.Description);
        }

        [VsFact]
        [Trait("Scenario", "valid-machines-no-problems")]
        public void TheExamplesHaveNoProblems()
        {
            foreach (var name in new[] { "MediaPlayer.fsm", "Order.fsm", "Payment.fsm" }) OpenDiagramOf(name);
            OpenBrokenAndWaitForItsProblem();
            foreach (var name in new[] { "MediaPlayer.fsm", "Order.fsm", "Payment.fsm" })
                Assert.True(_vs.ErrorsOn(name).Count == 0, $"{name}: {string.Join("; ", _vs.ErrorsOn(name).Select(e => e.Description))}");
        }

        [VsFact]
        [Trait("Scenario", "submachine-references-resolve")]
        public void TheSubmachineReferencesResolve()
        {
            OpenDiagramOf("Order.fsm");
            OpenBrokenAndWaitForItsProblem();
            var problems = _vs.ErrorsOn("Order.fsm");
            Assert.True(problems.Count == 0, string.Join("; ", problems.Select(e => e.Description)));
        }

        [VsFact]
        [Trait("Scenario", "follow-reference")]
        public void FollowsASubmachineReference()
        {
            OpenDiagramOf("Order.fsm");
            // A double-click with Alt held on a submachine state opens its machine (a plain one edits its name).
            Input.AltDoubleClick(_vs.VertexOnScreen("Order.fsm", "Payment"));
            VisualStudio.Until("Payment.fsm open", () => ActiveIs("Payment.fsm"));
            Assert.False(_vs.ActiveIsText(), "in the diagram");
            _vs.Diagram();
        }

        [VsFact]
        [Trait("Scenario", "missing-submachine")]
        public void AMissingSubmachineOpensNothing()
        {
            CopyMachine("Order.fsm", "Missing.fsm", t => t.Replace("\"submachine\": \"Payment.fsm\"", "\"submachine\": \"Nowhere.fsm\""));
            OpenDiagramOf("Missing.fsm");
            Input.AltDoubleClick(_vs.VertexOnScreen("Missing.fsm", "Payment"));
            _vs.WaitText("the toast saying the file doesn't exist", t => t.Contains("Nowhere.fsm does not exist"));
            Assert.True(ActiveIs("Missing.fsm"), "nothing else opens");
            Assert.False(File.Exists(_vs.PathOf("Nowhere.fsm")));
        }

        // ---------------------------------------------------------------- the diagram's own gestures (not scenarios)

        [VsFact]
        public void AltClickAndAMiddleClickOpenThePropertiesWindow()
        {
            OpenDiagramOf("MediaPlayer.fsm");
            Input.AltClick(_vs.VertexOnScreen("MediaPlayer.fsm", "Off"));
            VisualStudio.Until("the properties window, after Alt+click", _vs.PropertiesWindowOpen);
            Input.Key(0x1B); // Esc
            VisualStudio.Until("the window closed", () => !_vs.PropertiesWindowOpen());
            Input.MiddleClick(_vs.VertexOnScreen("MediaPlayer.fsm", "Off"));
            VisualStudio.Until("the properties window, after a middle click", _vs.PropertiesWindowOpen);
            Input.Key(0x1B);
            VisualStudio.Until("the window closed", () => !_vs.PropertiesWindowOpen());
        }

        [VsFact]
        public void AltClickOnASubmachineStateOpensItsPropertiesWindowAfterTheDoubleClickTime()
        {
            OpenDiagramOf("Order.fsm");
            Input.AltClick(_vs.VertexOnScreen("Order.fsm", "Payment"));
            VisualStudio.Until("the properties window", _vs.PropertiesWindowOpen);
            Assert.True(ActiveIs("Order.fsm"), "Payment.fsm not opened");
            Input.Key(0x1B);
        }

        [VsFact]
        public void AClickInACompositeStatesAreaSelectsIt()
        {
            CopyMachine("MediaPlayer.fsm", "Area.fsm");
            OpenDiagramOf("Area.fsm");
            // Inside On's region, near its bottom right corner, away from its states and its border.
            Input.Click(_vs.VertexOnScreen("Area.fsm", "On", 0.96, 0.96));
            _vs.Execute("Edit.Delete");
            VisualStudio.Until("On deleted", () => !_vs.Do(d => d.ActiveDocument.Saved));
            Save();
            Assert.False(Has(Read("Area.fsm"), "On"), "On deleted: it was the selection");
            Assert.True(Has(Read("Area.fsm"), "Off"), "Off kept");
        }

        [VsFact]
        public void ACompositeStateMovesByItsBorder()
        {
            CopyMachine("MediaPlayer.fsm", "Border.fsm");
            var model = Read("Border.fsm");
            var layout = Read("Border.fsm.layout");
            OpenDiagramOf("Border.fsm");
            // On's left border, low in its region (not its title bar).
            var edge = _vs.VertexOnScreen("Border.fsm", "On", 0, 0.9);
            var from = new System.Drawing.Point(edge.X + 2, edge.Y);
            Input.Drag(from, new System.Drawing.Point(from.X + 40, from.Y + 30));
            VisualStudio.Until("On moved", () => !_vs.Do(d => d.ActiveDocument.Saved));
            Save();
            Assert.Equal(model, Read("Border.fsm"));
            Assert.NotEqual(layout, Read("Border.fsm.layout"));
        }

        // ---------------------------------------------------------------- editing through Visual Studio

        /// <summary>
        /// Visual Studio's Select All and Copy in Order, then its Paste in Empty.fsm (a machine with no elements: pasted
        /// into Order itself, its initial pseudostate would be a second one in its region, which the file can't hold).
        /// </summary>
        private void PasteOrderIntoEmpty()
        {
            Write("Empty.fsm", "{\n  \"version\": 1,\n  \"name\": \"Empty\",\n  \"type\": \"behavioral\"\n}\n");
            Write("Empty.fsm.layout", EmptyLayout);
            OpenDiagramOf("Order.fsm");
            _vs.Execute("Edit.SelectAll");
            _vs.Execute("Edit.Copy");
            OpenDiagramOf("Empty.fsm");
            _vs.Execute("Edit.Paste");
            VisualStudio.Until("the document changed", () => !_vs.Do(d => d.ActiveDocument.Saved));
        }

        private const string EmptyLayout = "{\n  \"version\": 1\n}\n";

        private static bool Has(string model, string state) => model.Contains($"\"name\": \"{state}\"");

        [VsFact]
        [Trait("Scenario", "ide-edit-commands")]
        public void EditsThroughVisualStudiosCommands()
        {
            var order = Read("Order.fsm");
            PasteOrderIntoEmpty();
            Save();
            var pasted = Read("Empty.fsm");
            foreach (var state in new[] { "Cart", "Payment", "Shipped", "PaymentFailed" }) Assert.True(Has(pasted, state), $"{state} pasted");
            Assert.Equal(order, Read("Order.fsm"));
            // In Order (put back afterwards): Cut on a selected state removes it; Delete removes another.
            var orderLayout = Read("Order.fsm.layout");
            try
            {
                OpenDiagramOf("Order.fsm");
                Input.Click(_vs.VertexOnScreen("Order.fsm", "Shipped"));
                _vs.Execute("Edit.Cut");
                VisualStudio.Until("cut", () => !_vs.Do(d => d.ActiveDocument.Saved));
                Save();
                Assert.False(Has(Read("Order.fsm"), "Shipped"), "Cut removes the state");
                Input.Click(_vs.VertexOnScreen("Order.fsm", "Cart"));
                _vs.Execute("Edit.Delete");
                VisualStudio.Until("deleted", () => !_vs.Do(d => d.ActiveDocument.Saved));
                Save();
                var after = Read("Order.fsm");
                Assert.False(Has(after, "Cart"), "Delete removes the state");
                Assert.True(Has(after, "PaymentFailed"), "and only it");
            }
            finally
            {
                _vs.CloseAll();
                Write("Order.fsm", order);
                Write("Order.fsm.layout", orderLayout);
            }
        }

        [VsFact]
        [Trait("Scenario", "undo-redo-save-both-files")]
        public void UndoesRedoesAndSavesBothFiles()
        {
            const string layoutBefore = EmptyLayout;
            PasteOrderIntoEmpty();
            _vs.Execute("Edit.Undo");
            VisualStudio.Until("undone", () => _vs.Do(d => d.ActiveDocument.Saved));
            _vs.Execute("Edit.Redo");
            VisualStudio.Until("redone", () => !_vs.Do(d => d.ActiveDocument.Saved));
            Save();
            Assert.True(Has(Read("Empty.fsm"), "Cart"), "the model file holds Cart");
            Assert.NotEqual(layoutBefore, Read("Empty.fsm.layout"));
        }

        [VsFact]
        [Trait("Scenario", "move-changes-layout-only")]
        public void MovingAStateChangesTheLayoutOnly()
        {
            var model = Read("MediaPlayer.fsm");
            var layout = Read("MediaPlayer.fsm.layout");
            OpenDiagramOf("MediaPlayer.fsm");
            var from = _vs.VertexOnScreen("MediaPlayer.fsm", "Off", 0.5, 0.75);
            Input.Drag(from, new System.Drawing.Point(from.X + 30, from.Y + 20));
            VisualStudio.Until("moved", () => !_vs.Do(d => d.ActiveDocument.Saved));
            Save();
            Assert.Equal(model, Read("MediaPlayer.fsm"));
            Assert.NotEqual(layout, Read("MediaPlayer.fsm.layout"));
        }

        // ---------------------------------------------------------------- commands and tool windows

        /// <summary>The captions of a command bar's controls (a menu's items), without accelerators and ellipses.</summary>
        private static string[] Captions(dynamic bar)
        {
            int n = bar.Controls.Count;
            var list = new string[n];
            for (var i = 1; i <= n; i++) list[i - 1] = ((string)bar.Controls[i].Caption).Replace("&", "").Replace("...", "").Replace("…", "");
            return list;
        }

        [VsFact]
        [Trait("Scenario", "commands-registered")]
        public void RegistersItsCommandsInItsMenus()
        {
            foreach (var id in new[] { NewStateMachine, GenerateCode, ExportSvg, OpenAsText, OpenDiagram, ExportPng, OpenTrace, ShowTracePlayer, CompareAsDiagrams })
            {
                var name = _vs.CommandName(id);
                Assert.False(string.IsNullOrEmpty(name), $"command 0x{id:x4} has a name");
            }
            // Tools › FSM Editor, and View › Other Windows › Trace Player.
            string[] tools, windows;
            try
            {
                (tools, windows) = _vs.Do(d =>
                {
                    dynamic bars = d.CommandBars;
                    dynamic menu = bars["Tools"].Controls["FSM Editor"].CommandBar;
                    return ((string[])Captions(menu), (string[])Captions(bars["Other Windows"]));
                });
            }
            catch (Exception e) when (e.ToString().Contains("disposed"))
            {
                // Visual Studio 2026's CommandBars can be disposed altogether: the menus read as they show, opened.
                tools = _vs.MenuItems("Tools", "FSM Editor");
                windows = _vs.MenuItems("View", "Other Windows");
            }
            foreach (var caption in new[] { "New State Machine", "Generate Code", "Export as SVG", "Export as PNG", "Open as Text", "Open in FSM Editor", "Open Trace", "Compare as Diagrams" })
                Assert.True(tools.Contains(caption), $"Tools › FSM Editor › {caption}: {string.Join(", ", tools)}");
            Assert.Contains("Trace Player", windows);
        }

        [VsFact]
        [Trait("Scenario", "export-svg-png")]
        public void ExportsTheDiagramAsSvgAndPng()
        {
            OpenDiagramOf("MediaPlayer.fsm");
            var svgPath = _vs.PathOf("export.svg");
            var pngPath = _vs.PathOf("export.png");
            RaiseWithDialog(ExportSvg, null, "Export as SVG", d => VisualStudio.AnswerFileDialog(d, svgPath, "Save"));
            VisualStudio.Until("export.svg", () => File.Exists(svgPath));
            var svg = File.ReadAllText(svgPath);
            Assert.Contains("<svg ", svg);
            Assert.Contains("Stopped", svg);
            RaiseWithDialog(ExportPng, null, "Export as PNG", d => VisualStudio.AnswerFileDialog(d, pngPath, "Save"));
            VisualStudio.Until("export.png", () => File.Exists(pngPath) && new FileInfo(pngPath).Length > 24);
            var png = File.ReadAllBytes(pngPath);
            Assert.Equal("PNG", Encoding.ASCII.GetString(png, 1, 3));
            int Be(int i) => (png[i] << 24) | (png[i + 1] << 16) | (png[i + 2] << 8) | png[i + 3];
            double Size(string attr) => double.Parse(Regex.Match(svg, $"<svg [^>]*{attr}=\"([\\d.]+)\"").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal((int)Math.Round(Size("width") * 2), Be(16));
            Assert.Equal((int)Math.Round(Size("height") * 2), Be(20));
        }

        /// <summary>
        /// Generate Code with the arguments it takes from the Command Window (<c>-t &lt;template&gt; -o &lt;folder&gt;</c>:
        /// no dialog), raised through DTE's Commands.Raise (ExecuteCommand("Tools.GenerateCode", args) is refused: "does not
        /// accept arguments or switches").
        /// </summary>
        private System.Threading.Tasks.Task Generate(string template)
        {
            OpenDiagramOf("Order.fsm");
            return _vs.Begin(d =>
            {
                object args = $"-t {template} -o generated", output = null;
                d.Commands.Raise(VisualStudio.CommandSet, GenerateCode, ref args, ref output);
            });
        }

        [VsFact]
        [Trait("Scenario", "codegen-bundled")]
        public void GeneratesCodeWithABundledTemplate()
        {
            Assert.True(Generate("ts").Wait(Dialog));
            VisualStudio.Until("Order.ts", () => File.Exists(_vs.PathOf(@"generated\Order.ts")));
            Assert.Contains("export class Order", Read(@"generated\Order.ts"));
            Assert.False(File.Exists(_vs.PathOf(@"generated\Payment.ts")), "the submachine is read, not generated");
        }

        [VsFact]
        [Trait("Scenario", "codegen-workspace-template")]
        public void GeneratesCodeWithATemplateOfTheFolder()
        {
            Write("states.jinja", "{% filter file(machine.name ~ \".txt\") %}{{ machine.name }}: {{ submachines | map(attribute=\"name\") | join(\", \") }}{% endfilter %}");
            Assert.True(Generate("states.jinja").Wait(Dialog));
            VisualStudio.Until("Order.txt", () => File.Exists(_vs.PathOf(@"generated\Order.txt")));
            Assert.Equal("Order: Payment", Read(@"generated\Order.txt"));
        }

        [VsFact]
        [Trait("Scenario", "codegen-refused-template")]
        public void RefusesAHandlebarsTemplate()
        {
            Write("states.hbs", "{{machine.name}}");
            var run = Generate("states.hbs");
            var message = _vs.WaitDialog("Microsoft Visual Studio", Dialog);
            var text = VisualStudio.TextOf(message);
            VisualStudio.Press(message, "OK");
            Assert.True(run.Wait(Dialog));
            // fsm-cli's message, as it is.
            Assert.Contains("states.hbs is a Handlebars template: they are no longer supported. Convert it to Jinja (.jinja), see https://github.com/vincedupuis/fsm-cli#coming-from-the-handlebars-templates", text);
        }

        private static void Git(string dir, string args)
        {
            var p = Process.Start(new ProcessStartInfo("git", "-c user.name=test -c user.email=test@example.com " + args)
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"git {args}: {output}");
        }

        [VsFact]
        [Trait("Scenario", "compare-as-diagrams")]
        public void ComparesWithItsLastCommitAsDiagrams()
        {
            // A repository of its own (under the folder), MediaPlayer committed, then changed.
            CopyMachine("MediaPlayer.fsm", @"git\MediaPlayer.fsm");
            var dir = _vs.PathOf("git");
            Git(dir, "init -q");
            Git(dir, "add .");
            Git(dir, "commit -q -m MediaPlayer");
            Write(@"git\MediaPlayer.fsm", Read(@"git\MediaPlayer.fsm").Replace("\"entry\": \"rewind()\"", "\"entry\": \"rewind(); beep()\""));
            OpenDiagramOf(@"git\MediaPlayer.fsm");
            _vs.Raise(CompareAsDiagrams);
            // Two diagrams, linked: the older version and the file, each with the bar of the diff, the change counted.
            VisualStudio.Until("the diagram diff's two panes", () =>
            {
                var texts = _vs.Texts().Select(t => t.Text).ToList();
                return texts.Contains("Old") && texts.Contains("New") && texts.Count(t => t == "1 change") == 2;
            });
            Assert.True(_vs.Do(d => d.Documents.Cast<EnvDTE.Document>().Count(x => x.FullName.EndsWith("MediaPlayer.fsm", StringComparison.OrdinalIgnoreCase))) >= 2, "the two versions are open");
        }

        [VsFact]
        [Trait("Scenario", "merge-conflicts")]
        public void OpensAConflictedFileAsTheDiagramMerge()
        {
            const string captured = "      \"entry\": \"capture()\",\n";
            Assert.Contains(captured, Read("Payment.fsm"));
            CopyMachine("Payment.fsm", @"merge\Payment.fsm", t => t.Replace(captured, "<<<<<<< HEAD\n      \"entry\": \"capture(); log()\",\n=======\n      \"entry\": \"settle()\",\n>>>>>>> feature\n"));
            _vs.Open(@"merge\Payment.fsm");
            Assert.False(_vs.ActiveIsText(), "it opens in the diagram");
            // The result, with one conflict, beside its incoming and current sides.
            VisualStudio.Until("the diagram merge", () =>
            {
                var texts = _vs.Texts().Select(t => t.Text).ToList();
                return texts.Contains("Result") && texts.Contains("1 conflict") && texts.Contains("Incoming") && texts.Contains("Current");
            });
            Assert.True(_vs.Saved(_vs.PathOf(@"merge\Payment.fsm")), "not marked changed");
        }

        /// <summary>Open Trace, answered with a log file.</summary>
        private void OpenLog(string name)
        {
            var path = _vs.PathOf(name);
            RaiseWithDialog(OpenTrace, null, "Open Trace: log files", d => VisualStudio.AnswerFileDialog(d, path, "Open"));
        }

        /// <summary>The Trace Player's count of a log's records (<c>N records</c> beside its name).</summary>
        private bool Records(int count) => _vs.Texts().Any(t => t.Text == $"{count} records");

        [VsFact]
        [Trait("Scenario", "trace-player")]
        public void ReplaysALogOnTheDiagram()
        {
            _vs.Raise(ShowTracePlayer);
            VisualStudio.Until("the Trace Player window", () => _vs.Do(d => d.Windows.Cast<EnvDTE.Window>().Any(w => w.Caption.Contains("Trace Player") && w.Visible)));
            OpenDiagramOf("MediaPlayer.fsm");
            Write("run.log", "fsm MediaPlayer transition MediaPlayer.initial#1 initial -> Off\nfsm MediaPlayer enter Off Off\n");
            OpenLog("run.log");
            VisualStudio.Until("the log's 2 records", () => Records(2));
            // On MediaPlayer's diagram: its status bar shows the Trace Player's step.
            VisualStudio.Until("the step on the diagram", () => _vs.Texts().Any(t => t.Tip == "Trace Player" && t.Text.Length > 0));
            // Followed as it grows.
            File.AppendAllText(_vs.PathOf("run.log"), "fsm MediaPlayer exit Off Off\nfsm MediaPlayer transition Off#1 powerOn -> On\nfsm MediaPlayer enter On On\n");
            VisualStudio.Until("the lines appended", () => Records(5), TimeSpan.FromSeconds(20));
            // A log in a format of Tools › Options › FSM Editor › Trace formats (set in the page, as a user does: DTE's
            // Properties("FSM Editor", "General") fails, "Specified cast is not valid").
            const string formats = "[{ \"name\": \"plain\", \"machine\": \"MediaPlayer\", \"rules\": [{ \"kind\": \"enter\", \"match\": \"^ENTER (?<name>\\\\w+)$\" }] }]"; // \\w in the JSON: the regex's \w
            SetTraceFormats(formats);
            try
            {
                Write("plain.log", "ENTER Off\nENTER On\n");
                OpenLog("plain.log");
                try
                {
                    VisualStudio.Until("plain.log read with the format", () => _vs.Texts().Any(t => t.Text == "plain.log") && Records(2));
                }
                catch (TimeoutException e)
                {
                    // What the Trace Player shows instead.
                    var formatBoxes = AutomationElement.FromHandle(_vs.MainWindow).FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.HelpTextProperty, "Format of the lines"))
                        .Cast<AutomationElement>().Select(b =>
                        {
                            ((ExpandCollapsePattern)b.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
                            var items = string.Join("/", b.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>().Select(i => i.Current.Name));
                            ((ExpandCollapsePattern)b.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
                            System.Threading.Thread.Sleep(300);
                            object sel;
                            return items + (b.TryGetCurrentPattern(SelectionPattern.Pattern, out sel) ? " selected " + string.Join(",", ((SelectionPattern)sel).Current.GetSelection().Select(x => x.Current.Name)) : "");
                        }).ToList();
                    throw new TimeoutException(e.Message + ": " + string.Join(" | ", _vs.Texts().Select(t => t.Text).Where(t => t.Length > 0).SkipWhile(t => t != "Trace Player")) + "; formats offered: " + string.Join("; ", formatBoxes), e);
                }
            }
            finally
            {
                SetTraceFormats("");
            }
        }

        /// <summary>
        /// Sets Tools › Options › FSM Editor › General › Trace formats: Visual Studio's Options, FSM Editor, its General
        /// page (the property grid of a page of the extension), the field, OK.
        /// </summary>
        private void SetTraceFormats(string value)
        {
            var (dialog, field) = TraceFormatsField();
            // Typed, as the grid takes only what it is typed (a value set through UI Automation isn't kept).
            var box = field.Current.BoundingRectangle;
            Input.Click((int)(box.Left + box.Width / 2), (int)(box.Top + box.Height / 2));
            Input.Ctrl(0x41);
            Input.Key(0x2E);
            Input.Type(value);
            // Enter commits the field (and presses OK, the default button, when the field is done).
            Input.Key(0x0D);
            System.Threading.Thread.Sleep(1000);
            CloseOptionsPage(dialog, "OK");
            // Saved: the page shows it again.
            var (again, saved) = TraceFormatsField();
            var shown = ((ValuePattern)saved.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
            CloseOptionsPage(again, "Cancel");
            Assert.Equal(value, shown);
        }

        private static void CloseOptionsPage(AutomationElement dialog, string button)
        {
            bool Open()
            {
                try
                {
                    return dialog.Current.Name != null && !dialog.Current.IsOffscreen;
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
            }
            if (Open()) VisualStudio.Press(dialog, button);
            VisualStudio.Until("the Options page closed", () => !Open(), TimeSpan.FromSeconds(10));
        }

        /// <summary>The page Tools › Options › FSM Editor › General (modal), and its Trace formats field.</summary>
        private (AutomationElement Dialog, AutomationElement Field) TraceFormatsField()
        {
            _vs.Execute("Tools.Options");
            var main = AutomationElement.FromHandle(_vs.MainWindow);
            AutomationElement item = null, link = null;
            VisualStudio.Until("FSM Editor in Options", () => (item = main.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem), new PropertyCondition(AutomationElement.NameProperty, "FSM Editor")))) != null);
            ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            VisualStudio.Until("its General page", () => (link = main.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink), new PropertyCondition(AutomationElement.NameProperty, "General")))) != null);
            // The page opens modal: invoked off this thread.
            System.Threading.Tasks.Task.Run(() => ((InvokePattern)link.GetCurrentPattern(InvokePattern.Pattern)).Invoke());
            var dialog = _vs.WaitDialog("Options", Dialog);
            // The first row, Save .fsm documents before generating, at its default (the codegen tests rely on it).
            VisualStudio.Until("the page's fields", () => dialog.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)) != null);
            var save = dialog.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit), new PropertyCondition(AutomationElement.NameProperty, "Save .fsm documents before generating")));
            if (save != null && ((ValuePattern)save.GetCurrentPattern(ValuePattern.Pattern)).Current.Value != "True") ((ValuePattern)save.GetCurrentPattern(ValuePattern.Pattern)).SetValue("True");
            AutomationElement field = null;
            VisualStudio.Until("the Trace formats field", () =>
            {
                field = dialog.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit), new PropertyCondition(AutomationElement.NameProperty, "Trace formats")));
                if (field != null) return true;
                // The grid edits its selected row: its next row selected, from the grid itself (Down in a field
                // changes its value).
                dialog.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Table))?.SetFocus();
                Input.Key(0x28);
                return false;
            });
            return (dialog, field);
        }

        /// <summary>A machine in the old format: an initial pseudostate, a state <c>Idle</c> and an unnamed state.</summary>
        internal const string OldText = @"<?xml version=""1.0"" encoding=""UTF-8""?>
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
