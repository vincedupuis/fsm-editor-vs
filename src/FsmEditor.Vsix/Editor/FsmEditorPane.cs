using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using FsmEditor.Core;
using FsmEditor.Services;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace FsmEditor.Editor
{
    /// <summary>
    /// The document window of a state machine: the diagram of an <see cref="FsmDocument"/>. Diagram edits go to the
    /// document as new states (undoable), and every new state of the document (an undo, a reload, a change on disk) is
    /// shown on the diagram; the diagram's own edits aren't shown again, so its focus, typing and drags stay. After each
    /// change the machine is validated against the other machines of the solution and the results go to the diagram and
    /// the Error List. A pane can be a side of a diagram diff or merge (<see cref="IDiffPane"/>), which the package links.
    /// </summary>
    internal sealed class FsmEditorPane : WindowPane, IEditorHost, IOleCommandTarget, IDiffPane
    {
        private readonly FsmEditorPackage _package;
        private readonly DispatcherTimer _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        private EditorControl _control;
        private IDisposable _resyncSubscription;
        private int _generation;
        /// <summary>The state the diagram shows (the last one shown, or made by its own edit).</summary>
        private MachineState _shown;
        private FsmModel _shownDisplay;
        private string _pendingReveal;
        private readonly List<string> _notices = new List<string>();
        private Dictionary<string, string> _traceIds;
        private MachineState _traceIdsOf;
        private TraceView _trace;

        public FsmEditorPane(FsmEditorPackage package, FsmDocument document)
            : base(package)
        {
            _package = package;
            Document = document;
            _syncTimer.Tick += (s, e) =>
            {
                _syncTimer.Stop();
                SyncAsync().FileAndForget("FsmEditor/Sync");
            };
        }

        public FsmDocument Document { get; }

        public string FilePath => Document.Path;

        /// <summary>The diagram diff or merge this pane is a side of, or null.</summary>
        public object Session { get; set; }

        public EditorControl Control => _control;

        public bool IsReadOnly => Document.ReadOnly;

        protected override void Initialize()
        {
            base.Initialize();
            ThreadHelper.ThrowIfNotOnUIThread();
            _control = new EditorControl(this);
            Content = _control;
            Document.Changed += OnDocumentChanged;
            Document.SavedWithMessage += OnSaved;
            _resyncSubscription = FsmWorkspace.Instance.OnAnyMachineChanged(ScheduleSync);
            _package.RegisterPane(this);
            if (Document.IsLoaded) AfterLoad();
            else Document.Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Document.Loaded -= OnLoaded;
            AfterLoad();
        }

        /// <summary>Once the files are read: what opening found, said; a file with merge conflicts opens as a merge.</summary>
        private void AfterLoad()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Notices to show once the diagram is up: what opening found. Opening changes nothing on disk.
            var placed = Document.State.Placed;
            if (placed > 0) _notices.Add($"{placed} element{(placed == 1 ? " was" : "s were")} placed automatically: the layout file had no place for {(placed == 1 ? "it" : "them")}.");
            if (Document.UnreadableLayout != null) _notices.Add($"The layout file could not be read ({Document.UnreadableLayout.Problem}): the elements were placed automatically. It is replaced only if you confirm when saving.");
            if (Document.Old != null && !Document.ReadOnly) _notices.Add("This file is in the old format (XMI): saving it writes the new format, a .fsm model file and a .fsm.layout layout file, and converts the files linked to it by submachines.");
            if (Document.LayoutConflict && !Document.ReadOnly) _notices.Add("The layout file has merge conflicts: the diagram uses the current positions, and saving writes a clean layout file.");
            ScheduleSync();
            // A file with merge conflicts opens as the result of the diagram merge, its two sides beside it.
            if (Document.Merge != null && !Document.ReadOnly) OpenMergeSoonAsync().FileAndForget("FsmEditor/OpenMerge");
            else _package.Sessions.Opened(this);
        }

        /// <summary>The merge opens once the window is up: its sides open beside it.</summary>
        private async Task OpenMergeSoonAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
            if (_control != null) _package.Sessions.OpenMerge(this);
        }

        private void OnDocumentChanged(object sender, EventArgs e) => ScheduleSync();

        private void OnSaved(object sender, string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (message != null) VsShellUtilities.ShowMessageBox(this, message, "FSM Editor", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            FsmWorkspace.Instance.ResyncAll();
        }

        private void ScheduleSync()
        {
            _syncTimer.Stop();
            _syncTimer.Start();
        }

        // ---------------------------------------------------------------- document → diagram

        private async Task SyncAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_control == null) return;
            var generation = ++_generation;
            var state = Document.State;
            var display = Document.DisplayModel;
            var model = display ?? state.Model;
            string error = null;
            var issues = new List<Issue>();
            IReadOnlyDictionary<string, SubmachineInfo> submachines = null;
            IReadOnlyList<MachineInfo> machines = null;
            if (state.Problems != null || model == null)
            {
                error = state.Problems != null && state.Problems.Count > 0
                    ? string.Join("\n", state.Problems.Select(p => $"Line {p.Line}: {p.Message}"))
                    : "This file cannot be read.";
            }
            else
            {
                var workspace = FsmWorkspace.Instance;
                var root = workspace.RootFor(FilePath);
                workspace.Watch(root);
                var open = workspace.OpenTexts();
                var resolved = await workspace.ResolveAsync(FilePath, root, model, open);
                submachines = resolved.Submachines;
                machines = resolved.Machines;
                var toValidate = model;
                issues = await Task.Run(() => Core.Validation.Validate(toValidate, resolved.Submachines));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            }
            if (generation != _generation || _control == null) return; // a newer sync is on its way
            // The document changed meanwhile: showing this older state would take the diagram back to it.
            if (Document.State != state || Document.DisplayModel != display)
            {
                ScheduleSync();
                return;
            }
            PublishDiagnostics(state, issues, error);
            // The diagram's own edit coming back: it already shows it.
            var same = error == null && state == _shown && display == _shownDisplay;
            _shown = error == null ? state : null;
            _shownDisplay = display;
            _control.Update(same ? null : model?.Clone(), issues, error, submachines, machines, Document.MergeFallback);
            _control.SetReadOnly(Document.ReadOnly);
            if (_trace != null) _control.SetTrace(MapTrace(_trace));
            while (_notices.Count > 0 && error == null)
            {
                _control.Toast(_notices[0]);
                _notices.RemoveAt(0);
            }
            if (_pendingReveal != null && error == null)
            {
                var id = _pendingReveal;
                _pendingReveal = null;
                _control.Reveal(id);
            }
            (Session as DiffSession)?.Update();
            (Session as MergeSession)?.Update();
        }

        /// <summary>The Error List: validation issues at the lines of their elements (an old file's, by xmi:id), and the problems of a file that can't be read.</summary>
        private void PublishDiagnostics(MachineState state, List<Issue> issues, string error)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (CompareFiles.IsReadOnlySide(FilePath)) return;
            int LineOf(string id)
            {
                if (Document.Old != null) return MachineFiles.OldLineOf(Document.Old.Text, id) - 1;
                return state.Lines.TryGetValue(id ?? "", out var line) ? line - 1 : 0;
            }
            var entries = issues.Select(i => (i.Id, LineOf(i.Id), i.Severity, i.Message)).ToList();
            if (state.Problems != null) entries.AddRange(state.Problems.Select(p => ("", Math.Max(0, p.Line - 1), Severity.Error, p.Message)));
            else if (error != null) entries.Add(("", 0, Severity.Error, error));
            _package.Validation.Replace(FilePath, entries);
        }

        /// <summary>Selects and shows an element, once the diagram is loaded.</summary>
        public void Reveal(string id)
        {
            if (_control?.Session == null) _pendingReveal = id ?? "";
            else _control.Reveal(id ?? "");
        }

        // ---------------------------------------------------------------- diagram → document

        public string Write(FsmModel model)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Document.ReadOnly) return "This diagram is read-only.";
            if (!CanEdit()) return "The file is read-only; the change was not saved.";
            var problem = Document.Edit(model);
            if (problem == null)
            {
                // The new state is the diagram's own: not shown again.
                _shown = Document.State;
                _shownDisplay = null;
            }
            else
            {
                // Back to what the document holds.
                _shown = null;
                ScheduleSync();
            }
            return problem;
        }

        /// <summary>Asks Visual Studio whether the files may be edited (checking them out or making them writable); false when refused.</summary>
        private bool CanEdit()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(GetService(typeof(SVsQueryEditQuerySave)) is IVsQueryEditQuerySave2 queryEdit)) return true;
            var files = new[] { FilePath, Document.LayoutPath }.Where(File.Exists).ToArray();
            if (files.Length == 0) return true;
            // Flags 0: the default edit query, which may prompt to check the files out or make them writable.
            var hr = queryEdit.QueryEditFiles(0, files.Length, files, null, null, out var result, out _);
            return ErrorHandler.Succeeded(hr) && result == (uint)tagVSQueryEditResult.QER_EditOK;
        }

        public void Undo()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Document.Undo();
        }

        public void Redo()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Document.Redo();
        }

        public void ShowTracePlayer() => TracePlayer.TracePlayerWindow.ShowAsync().FileAndForget("FsmEditor/ShowTracePlayer");

        /// <summary>The Trace Player's highlights of this machine, or null.</summary>
        public void ShowTrace(TraceView view)
        {
            _trace = view;
            _control?.SetTrace(MapTrace(view));
        }

        /// <summary>
        /// The Trace Player's highlights with the ids of this diagram: the trace's ids are those reading the file gives
        /// (<see cref="Format.DerivedIds"/>), which elements created since the file was read, and an old file's (by their
        /// xmi:ids, until it is saved converted), don't have.
        /// </summary>
        private TraceView MapTrace(TraceView view)
        {
            var model = Document.State.Model;
            if (view == null || model == null) return view;
            if (_traceIdsOf != Document.State)
            {
                _traceIds = Format.DerivedIds(model).GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
                _traceIdsOf = Document.State;
            }
            var back = _traceIds;
            List<string> Map(List<string> ids) => ids.Select(id => back.TryGetValue(id, out var own) ? own : id).ToList();
            return new TraceView(view.Instances.Select(i => new TraceHighlight
            {
                Active = Map(i.Active),
                Taken = Map(i.Taken),
                Via = Map(i.Via),
                Current = Map(i.Current),
                Event = i.Event,
                Label = i.Label,
                Color = i.Color,
            }));
        }

        public void GenerateCode() => _package.GenerateCodeAsync(FilePath, null).FileAndForget("FsmEditor/GenerateCode");

        public void ExportSvg() => _package.ExportSvgAsync(this).FileAndForget("FsmEditor/ExportSvg");

        public string RenderSvg() => _control?.ExportSvg();

        public void ExportPng() => _package.ExportPngAsync(this).FileAndForget("FsmEditor/ExportPng");

        public System.Windows.Media.Imaging.BitmapSource RenderPng() => _control?.ExportPng();

        /// <summary>
        /// Opens the model file in the text editor. A text editor can't share the diagram's document: the diagram closes
        /// first, after asking what to do with its unsaved changes, which the text wouldn't show.
        /// </summary>
        public void OpenAsText()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Document.Dirty)
            {
                var answer = Dialogs.Choose("Open as Text",
                    $"{Path.GetFileName(FilePath)} has unsaved changes in the diagram, which the text editor won't show. Save them first?",
                    "Save and Open as Text", "Open Without Saving");
                if (answer < 0) return;
                if (answer == 0)
                {
                    try
                    {
                        if (!Document.Save()) return;
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        _control.Toast($"{Path.GetFileName(FilePath)} could not be saved: {e.Message}");
                        return;
                    }
                }
            }
            var path = FilePath;
            if (GetService(typeof(SVsWindowFrame)) is IVsWindowFrame frame) frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
            FsmDocuments.OpenAsText(path);
        }

        public void OpenSubmachine(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(path)) return;
            string file = null;
            try
            {
                file = Hrefs.ResolvePath(FilePath, path);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
            {
                // Not a file path: a file that doesn't exist.
            }
            if (!File.Exists(file))
            {
                _control.Toast($"The referenced state machine file {path} does not exist.");
                return;
            }
            FsmDocuments.OpenInDiagram(file);
        }

        public string AskSubmachineName(string proposed, ISet<string> namesInside)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var folder = Path.GetDirectoryName(FilePath);
            bool Exists(string name) => File.Exists(Path.Combine(folder, name + ".fsm"));
            var value = proposed;
            for (var n = 2; Exists(value); n++) value = proposed + n;
            return Dialogs.AskMachineName(value, "Extract to Submachine", "Name of the new state machine, saved next to this one:", "Extract",
                name => namesInside.Contains(name) ? $"An element of the new machine is named {name}: names are unique in a machine."
                    : Exists(name) ? $"{name}.fsm already exists." : null);
        }

        public bool CreateMachineFiles(string name, string modelText, string layoutText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var path = Path.Combine(Path.GetDirectoryName(FilePath), name + ".fsm");
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(modelText);
                }
                File.WriteAllText(MachineFiles.LayoutPathOf(path), layoutText, new System.Text.UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                VsShellUtilities.ShowMessageBox(this, $"{name}.fsm could not be written ({e.Message}). Nothing was changed.", "Extract to Submachine",
                    OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return false;
            }
            AddToProjectAsync(path).FileAndForget("FsmEditor/AddSubmachine");
            return true;
        }

        /// <summary>Adds <paramref name="path"/> to the project holding this document (SDK-style projects pick it up on their own).</summary>
        private async Task AddToProjectAsync(string path)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                var file = await Community.VisualStudio.Toolkit.PhysicalFile.FromFileAsync(FilePath);
                if (file?.FindParent(Community.VisualStudio.Toolkit.SolutionItemType.Project) is Community.VisualStudio.Toolkit.Project project) await project.AddExistingFilesAsync(path);
            }
            catch (Exception e) when (e is COMException || e is InvalidOperationException || e is NotImplementedException)
            {
                // Folder views and some project types don't take new items.
            }
        }

        // ---------------------------------------------------------------- diff and merge

        FsmModel IDiffPane.CurrentModel() => Document.DisplayModel ?? Document.State.Model;

        void IDiffPane.ShowDiff(PaneDiff diff) => _control?.ShowDiff(diff);

        void IDiffPane.Reveal(string id) => Reveal(id ?? "");

        void IDiffPane.Toast(string message) => _control?.Toast(message);

        public void DiffNavigate(int dir, string key) => _package.Sessions.Navigate(this, dir, key);

        public void DiffShowLayout(bool show) => _package.Sessions.ShowLayout(this, show);

        public void ViewChanged(double x, double y, double zoom) => _package.Sessions.ViewChanged(this, x, y, zoom);

        public void MergeChoose(string key, string side) => _package.Sessions.Choose(this, key, side);

        public void MergeComplete()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _package.Sessions.Complete(this);
        }

        /// <summary>Closes the pane's window (a side of a merge that ended).</summary>
        public void CloseWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (GetService(typeof(SVsWindowFrame)) is IVsWindowFrame frame) frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
        }

        public void OpenTextMerge()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Visual Studio's merge editor works on the text: the file opens as text, with its conflicts.
            var path = FilePath;
            if (GetService(typeof(SVsWindowFrame)) is IVsWindowFrame frame) frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
            FsmDocuments.OpenAsText(path);
        }

        // ---------------------------------------------------------------- edit commands

        /// <summary>The text box being typed in, which then gets the edit commands instead of the diagram.</summary>
        private static TextBox FocusedTextBox => Keyboard.FocusedElement as TextBox;

        private bool Handles(ref Guid group, uint id) =>
            group == VSConstants.GUID_VSStandardCommandSet97 && _control != null && Enum.IsDefined(typeof(VSConstants.VSStd97CmdID), (int)id) &&
            IsEditCommand((VSConstants.VSStd97CmdID)id);

        private static bool IsEditCommand(VSConstants.VSStd97CmdID id)
        {
            switch (id)
            {
                case VSConstants.VSStd97CmdID.Undo:
                case VSConstants.VSStd97CmdID.MultiLevelUndo:
                case VSConstants.VSStd97CmdID.Redo:
                case VSConstants.VSStd97CmdID.MultiLevelRedo:
                case VSConstants.VSStd97CmdID.Cut:
                case VSConstants.VSStd97CmdID.Copy:
                case VSConstants.VSStd97CmdID.Paste:
                case VSConstants.VSStd97CmdID.Delete:
                case VSConstants.VSStd97CmdID.SelectAll:
                    return true;
                default:
                    return false;
            }
        }

        private bool IsEnabled(VSConstants.VSStd97CmdID id)
        {
            var tb = FocusedTextBox;
            switch (id)
            {
                case VSConstants.VSStd97CmdID.Undo:
                case VSConstants.VSStd97CmdID.MultiLevelUndo:
                    return tb != null ? tb.CanUndo : Document.CanUndo;
                case VSConstants.VSStd97CmdID.Redo:
                case VSConstants.VSStd97CmdID.MultiLevelRedo:
                    return tb != null ? tb.CanRedo : Document.CanRedo;
                case VSConstants.VSStd97CmdID.Cut:
                    return tb != null ? tb.SelectionLength > 0 : _control.HasSelection && !Document.ReadOnly;
                case VSConstants.VSStd97CmdID.Copy:
                    return tb != null ? tb.SelectionLength > 0 : _control.HasSelection;
                case VSConstants.VSStd97CmdID.Delete:
                    return tb != null || (_control.HasSelection && !Document.ReadOnly);
                case VSConstants.VSStd97CmdID.Paste:
                    return tb != null || (_control.CanPaste && !Document.ReadOnly);
                default:
                    return true;
            }
        }

        private void Execute(VSConstants.VSStd97CmdID id)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var tb = FocusedTextBox;
            if (tb != null)
            {
                switch (id)
                {
                    case VSConstants.VSStd97CmdID.Undo:
                    case VSConstants.VSStd97CmdID.MultiLevelUndo: tb.Undo(); break;
                    case VSConstants.VSStd97CmdID.Redo:
                    case VSConstants.VSStd97CmdID.MultiLevelRedo: tb.Redo(); break;
                    case VSConstants.VSStd97CmdID.Cut: tb.Cut(); break;
                    case VSConstants.VSStd97CmdID.Copy: tb.Copy(); break;
                    case VSConstants.VSStd97CmdID.Paste: tb.Paste(); break;
                    case VSConstants.VSStd97CmdID.Delete: EditingCommands.Delete.Execute(null, tb); break;
                    case VSConstants.VSStd97CmdID.SelectAll: tb.SelectAll(); break;
                }
                return;
            }
            switch (id)
            {
                case VSConstants.VSStd97CmdID.Undo:
                case VSConstants.VSStd97CmdID.MultiLevelUndo: Undo(); break;
                case VSConstants.VSStd97CmdID.Redo:
                case VSConstants.VSStd97CmdID.MultiLevelRedo: Redo(); break;
                case VSConstants.VSStd97CmdID.Cut: _control.Copy(cut: true); break;
                case VSConstants.VSStd97CmdID.Copy: _control.Copy(cut: false); break;
                case VSConstants.VSStd97CmdID.Paste: _control.Paste(); break;
                case VSConstants.VSStd97CmdID.Delete: _control.DeleteSelection(); break;
                case VSConstants.VSStd97CmdID.SelectAll: _control.SelectAll(); break;
            }
        }

        int IOleCommandTarget.QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var group = pguidCmdGroup;
            if (!Handles(ref group, prgCmds[0].cmdID)) return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            var enabled = IsEnabled((VSConstants.VSStd97CmdID)prgCmds[0].cmdID);
            prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | (enabled ? OLECMDF.OLECMDF_ENABLED : 0));
            return VSConstants.S_OK;
        }

        int IOleCommandTarget.Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var group = pguidCmdGroup;
            if (!Handles(ref group, nCmdID)) return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            Execute((VSConstants.VSStd97CmdID)nCmdID);
            return VSConstants.S_OK;
        }

        // ---------------------------------------------------------------- cleanup

        protected override void Dispose(bool disposing)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposing)
            {
                _syncTimer.Stop();
                _resyncSubscription?.Dispose();
                Document.Changed -= OnDocumentChanged;
                Document.Loaded -= OnLoaded;
                Document.SavedWithMessage -= OnSaved;
                _package.Sessions.Closed(this);
                _package.UnregisterPane(this);
                _package.Validation.Remove(FilePath);
                _control = null;
            }
            base.Dispose(disposing);
        }
    }
}
