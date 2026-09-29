using System;
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
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Task = System.Threading.Tasks.Task;

namespace FsmEditor.Editor
{
    /// <summary>
    /// The document window of a state machine. The text buffer holds the XMI;
    /// the diagram works on the parsed model. Diagram edits are written back as
    /// one undoable text edit, and text changes (undo, the XMI text view, a
    /// reload) are parsed and shown on the diagram. After each change the
    /// machine is validated against the other machines of the solution and the
    /// results go to the diagram and the Error List.
    /// </summary>
    internal sealed class FsmEditorPane : WindowPane, IEditorHost, IOleCommandTarget, IVsTextBufferDataEvents
    {
        private readonly FsmEditorPackage _package;
        private readonly IVsTextLines _textLines;
        private readonly IVsEditorAdaptersFactoryService _adapters;
        private readonly ITextBufferUndoManagerProvider _undoProvider;
        private readonly DispatcherTimer _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        private EditorControl _control;
        private ITextBuffer _buffer;
        private ITextDocument _document;
        private IConnectionPoint _dataEvents;
        private uint _dataEventsCookie;
        private IDisposable _resyncSubscription;
        private int _generation;
        /// <summary>The last XMI this editor wrote, with the model it came from.</summary>
        private string _echoText;
        private FsmModel _echoModel;
        private string _pendingReveal;

        public FsmEditorPane(FsmEditorPackage package, string path, IVsTextLines textLines, IVsEditorAdaptersFactoryService adapters, IComponentModel componentModel)
            : base(package)
        {
            _package = package;
            FilePath = path;
            _textLines = textLines;
            _adapters = adapters;
            _undoProvider = componentModel.GetService<ITextBufferUndoManagerProvider>();
            _syncTimer.Tick += (s, e) =>
            {
                _syncTimer.Stop();
                SyncAsync().FileAndForget("FsmEditor/Sync");
            };
        }

        public string FilePath { get; private set; }

        protected override void Initialize()
        {
            base.Initialize();
            ThreadHelper.ThrowIfNotOnUIThread();
            _control = new EditorControl(this);
            Content = _control;
            if (_textLines is IConnectionPointContainer container)
            {
                var iid = typeof(IVsTextBufferDataEvents).GUID;
                container.FindConnectionPoint(ref iid, out _dataEvents);
                _dataEvents?.Advise(this, out _dataEventsCookie);
            }
            _resyncSubscription = FsmWorkspace.Instance.OnAnyMachineChanged(ScheduleSync);
            _package.RegisterPane(this);
            AttachBuffer();
        }

        /// <summary>Hooks the text buffer once the document is loaded.</summary>
        private void AttachBuffer()
        {
            if (_buffer != null) return;
            _buffer = _adapters.GetDocumentBuffer(_textLines);
            if (_buffer == null) return;
            _buffer.Changed += OnBufferChanged;
            if (_buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document))
            {
                _document = document;
                FilePath = document.FilePath;
                _document.FileActionOccurred += OnFileAction;
            }
            ScheduleSync();
        }

        private void OnFileAction(object sender, TextDocumentFileActionEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (e.FileActionType == FileActionTypes.DocumentRenamed)
            {
                _package.Validation.Remove(FilePath);
                _package.UnregisterPane(this);
                FilePath = e.FilePath;
                _package.RegisterPane(this);
                ScheduleSync();
            }
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            ScheduleSync();
            // Other diagrams may use this machine as a submachine.
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
            if (_buffer == null || _control == null) return;
            var generation = ++_generation;
            var text = _buffer.CurrentSnapshot.GetText();
            FsmModel model = null;
            string error = null;
            var echo = _echoText != null && _echoText == text;
            try
            {
                // Our own edit coming back: keep the model the diagram already has.
                model = echo ? _echoModel.Clone()
                    : text.Trim().Length == 0 ? FsmModel.CreateDefault(Path.GetFileNameWithoutExtension(FilePath))
                    : Xmi.FromXmi(text);
            }
            catch (Exception e) when (e is XmlParseException || e is XmiException)
            {
                error = e.Message;
            }

            var issues = new System.Collections.Generic.List<Issue>();
            System.Collections.Generic.IReadOnlyDictionary<string, SubmachineInfo> submachines = null;
            System.Collections.Generic.IReadOnlyList<MachineInfo> machines = null;
            if (model != null)
            {
                var workspace = FsmWorkspace.Instance;
                var root = workspace.RootFor(FilePath);
                workspace.Watch(root);
                var open = workspace.OpenSnapshots();
                var path = FilePath;
                var resolved = await workspace.ResolveAsync(path, root, model, open);
                submachines = resolved.Submachines;
                machines = resolved.Machines;
                var toValidate = model;
                issues = await Task.Run(() => Core.Validation.Validate(toValidate, resolved.Submachines));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            }
            if (generation != _generation || _control == null) return; // a newer sync is on its way
            _package.Validation.Publish(FilePath, text, issues, error);
            _control.Update(echo ? null : model, issues, error, submachines, machines);
            if (_pendingReveal != null && model != null)
            {
                var id = _pendingReveal;
                _pendingReveal = null;
                _control.Reveal(id);
            }
        }

        /// <summary>Selects and shows an element, once the diagram is loaded.</summary>
        public void Reveal(string id)
        {
            if (_control?.Session == null) _pendingReveal = id;
            else _control.Reveal(id);
        }

        // ---------------------------------------------------------------- diagram → document

        public void Write(FsmModel model)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_buffer == null) return;
            var snapshot = _buffer.CurrentSnapshot;
            var old = snapshot.GetText();
            var xmi = Xmi.ToXmi(model);
            // Keep the file's line ends (Git may check files out with CRLF).
            if (old.Contains("\r\n")) xmi = xmi.Replace("\n", "\r\n");
            _echoText = xmi;
            _echoModel = model.Clone();
            if (xmi == old) return;
            // Replace only what changed, as one undo unit.
            int prefix = 0;
            var max = Math.Min(old.Length, xmi.Length);
            while (prefix < max && old[prefix] == xmi[prefix]) prefix++;
            int suffix = 0;
            while (suffix < max - prefix && old[old.Length - 1 - suffix] == xmi[xmi.Length - 1 - suffix]) suffix++;
            try
            {
                var history = _undoProvider.GetTextBufferUndoManager(_buffer).TextBufferUndoHistory;
                using (var transaction = history.CreateTransaction("Edit State Machine"))
                {
                    using (var edit = _buffer.CreateEdit())
                    {
                        edit.Replace(prefix, old.Length - prefix - suffix, xmi.Substring(prefix, xmi.Length - prefix - suffix));
                        edit.Apply();
                    }
                    transaction.Complete();
                }
            }
            catch (InvalidOperationException e)
            {
                _control.Toast($"The change could not be saved: {e.Message}");
                ScheduleSync();
            }
        }

        private Microsoft.VisualStudio.Text.Operations.ITextUndoHistory History =>
            _buffer == null ? null : _undoProvider.GetTextBufferUndoManager(_buffer).TextBufferUndoHistory;

        public void Undo()
        {
            if (History?.CanUndo == true) History.Undo(1);
        }

        public void Redo()
        {
            if (History?.CanRedo == true) History.Redo(1);
        }

        public void GenerateCode() => _package.GenerateCodeAsync(FilePath, null).FileAndForget("FsmEditor/GenerateCode");

        public void ExportSvg() => _package.ExportSvgAsync(this).FileAndForget("FsmEditor/ExportSvg");

        public string RenderSvg() => _control?.ExportSvg();

        public void OpenAsText()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            FsmDocuments.OpenAsText(FilePath);
        }

        public void OpenSubmachine(string href)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(href)) return;
            var (file, _) = Hrefs.Resolve(FilePath, href);
            if (!File.Exists(file))
            {
                _control.Toast($"The referenced state machine file {href.Split('#')[0]} does not exist.");
                return;
            }
            FsmDocuments.OpenInDiagram(file);
        }

        // ---------------------------------------------------------------- IVsTextBufferDataEvents

        public void OnFileChanged(uint grfChange, uint dwFileAttrs)
        {
        }

        public int OnLoadCompleted(int fReload)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            AttachBuffer();
            return VSConstants.S_OK;
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
                    return tb != null ? tb.CanUndo : History?.CanUndo == true;
                case VSConstants.VSStd97CmdID.Redo:
                case VSConstants.VSStd97CmdID.MultiLevelRedo:
                    return tb != null ? tb.CanRedo : History?.CanRedo == true;
                case VSConstants.VSStd97CmdID.Cut:
                case VSConstants.VSStd97CmdID.Copy:
                    return tb != null ? tb.SelectionLength > 0 : _control.HasSelection;
                case VSConstants.VSStd97CmdID.Delete:
                    return tb != null || _control.HasSelection;
                case VSConstants.VSStd97CmdID.Paste:
                    return tb != null || _control.CanPaste;
                default:
                    return true;
            }
        }

        private void Execute(VSConstants.VSStd97CmdID id)
        {
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
            if (!Handles(ref group, prgCmds[0].cmdID)) return (int)Constants.OLECMDERR_E_NOTSUPPORTED;
            var enabled = IsEnabled((VSConstants.VSStd97CmdID)prgCmds[0].cmdID);
            prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | (enabled ? OLECMDF.OLECMDF_ENABLED : 0));
            return VSConstants.S_OK;
        }

        int IOleCommandTarget.Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var group = pguidCmdGroup;
            if (!Handles(ref group, nCmdID)) return (int)Constants.OLECMDERR_E_NOTSUPPORTED;
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
                if (_dataEvents != null && _dataEventsCookie != 0) _dataEvents.Unadvise(_dataEventsCookie);
                _dataEvents = null;
                if (_buffer != null) _buffer.Changed -= OnBufferChanged;
                if (_document != null) _document.FileActionOccurred -= OnFileAction;
                _package.UnregisterPane(this);
                _package.Validation.Remove(FilePath);
                _control = null;
            }
            base.Dispose(disposing);
        }
    }
}
