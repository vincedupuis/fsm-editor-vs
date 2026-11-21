using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FsmEditor.Core;
using FsmEditor.Services;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace FsmEditor.Editor
{
    /// <summary>An old file (XMI), converted when it was opened: what saving it will write and say.</summary>
    internal sealed class OldFile
    {
        /// <summary>Its text on disk, for the lines of problems.</summary>
        public string Text;
        public Converted Result;
    }

    /// <summary>The layout file at the open, when it couldn't be read: not overwritten without asking.</summary>
    internal sealed class UnreadableLayout
    {
        public string Text;
        public string Problem;
    }

    /// <summary>A file with merge conflicts: its two sides (the layout file's too when it has conflicts), and Git's common version when known.</summary>
    internal sealed class MergeInfo
    {
        public MachineTexts Current;
        public MachineTexts Incoming;
        public MachineTexts Base;
    }

    /// <summary>
    /// The document data of a state machine: its two files, the model (<c>X.fsm</c>) and its layout (<c>X.fsm.layout</c>),
    /// edited together. Visual Studio's save, Save As, dirty mark, reload and close act on it through
    /// <see cref="IVsPersistDocData2"/> and <see cref="IPersistFileFormat"/>; its undo and redo are its own history of
    /// states (<see cref="MachineState"/>: the texts of the two files and the model), which the diagram's Undo and Redo
    /// commands walk. A move changes only the layout file, and is undone and saved like any other change.
    ///
    /// Opening a file changes nothing on disk and doesn't mark it changed: an old file (XMI) is converted, a file with
    /// merge conflicts split into its sides, in memory; saving is what writes them. Without changes of its own, the
    /// document follows its files when they change on disk.
    /// </summary>
    internal sealed class FsmDocument : IVsPersistDocData2, IPersistFileFormat, IVsFileChangeEvents, Microsoft.VisualStudio.TextManager.Interop.IVsFileBackup
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private readonly List<MachineState> _undo = new List<MachineState>();
        private readonly List<MachineState> _redo = new List<MachineState>();
        private uint _cookie;
        private readonly List<uint> _fileWatches = new List<uint>();
        private bool _readOnly;

        public FsmDocument(string path, bool readOnly = false)
        {
            Path = path;
            _readOnly = readOnly;
        }

        /// <summary>Full path of the model file.</summary>
        public string Path { get; private set; }
        public string LayoutPath => MachineFiles.LayoutPathOf(Path);

        /// <summary>The state the diagram shows, and the state on disk (last loaded or saved).</summary>
        public MachineState State { get; private set; } = new MachineState();
        public MachineState Saved { get; private set; } = new MachineState();
        public bool Dirty => State != Saved;
        public bool ReadOnly => _readOnly;

        public OldFile Old { get; private set; }
        public UnreadableLayout UnreadableLayout { get; private set; }
        /// <summary>No layout file was found: in a diff, this older version borrows the newer one's layout (<see cref="DisplayModel"/>).</summary>
        public bool LayoutMissing { get; private set; }
        /// <summary>What the diagram shows when it isn't the state's model (a borrowed layout), or null.</summary>
        public FsmModel DisplayModel { get; set; }
        /// <summary>The model file has merge conflicts: its two sides, opened as the diagram merge (the document is the result).</summary>
        public MergeInfo Merge { get; set; }
        /// <summary>The model file has merge conflicts the diagram merge can't open (a side isn't a valid machine file).</summary>
        public bool MergeFallback { get; private set; }
        /// <summary>Only the layout file has merge conflicts: the diagram uses its current side, and saving writes it clean.</summary>
        public bool LayoutConflict { get; private set; }

        /// <summary>The files were read (<see cref="LoadDocData"/>): the shell may load the document after its window is created.</summary>
        public bool IsLoaded { get; private set; }
        public event EventHandler Loaded;

        public bool CanUndo => _undo.Count > 0 && !_readOnly;
        public bool CanRedo => _redo.Count > 0 && !_readOnly;

        /// <summary>Raised when the state changes: an edit, undo, redo, reload, a change on disk.</summary>
        public event EventHandler Changed;
        /// <summary>Raised once a save wrote the files, with the message to show (a conversion), or null.</summary>
        public event EventHandler<string> SavedWithMessage;

        // ---------------------------------------------------------------- reading

        private static string ReadText(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>A full path as the <c>/</c>-separated path <see cref="MachineFiles"/> and <see cref="Conversion"/> work with.</summary>
        internal static string ToSlash(string path)
        {
            var p = path.Replace('\\', '/');
            return p.StartsWith("/", StringComparison.Ordinal) ? p : "/" + p;
        }

        internal static string FromSlash(string path) =>
            path.Length > 2 && path[2] == ':' ? path.Substring(1).Replace('/', System.IO.Path.DirectorySeparatorChar) : path.Replace('/', System.IO.Path.DirectorySeparatorChar);

        /// <summary>The old files of the workspace of <paramref name="path"/>, for the machines using it (converted with it).</summary>
        private static List<ConvertInput> WorkspaceOldFiles(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var root = FsmWorkspace.Instance.RootFor(path);
            var output = new List<ConvertInput>();
            foreach (var f in FsmWorkspace.Instance.FindMachineFiles(root))
            {
                var text = ReadText(f);
                if (text != null && Format.IsOldFormat(text)) output.Add(new ConvertInput(ToSlash(System.IO.Path.GetFullPath(f)), text));
            }
            return output;
        }

        /// <summary>The old files linked to <paramref name="path"/> by submachine references, both ways, with their texts.</summary>
        internal static List<ConvertInput> LinkedOldFiles(string path, string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return MachineFiles.LinkedOldFiles(ToSlash(path), text, WorkspaceOldFiles(path), p => ReadText(FromSlash(p)));
        }

        /// <summary>Reads the files: converts an old file (with the files linked to it), splits a file with merge conflicts.</summary>
        private void Read()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Old = null;
            UnreadableLayout = null;
            LayoutMissing = false;
            Merge = null;
            MergeFallback = false;
            LayoutConflict = false;
            DisplayModel = null;
            var modelText = ReadText(Path) ?? "";
            if (Core.Merge.HasConflictMarkers(modelText))
            {
                ReadConflicted(modelText);
                return;
            }
            if (Format.IsOldFormat(modelText))
            {
                var result = Conversion.ConvertGroup(LinkedOldFiles(Path, modelText)).First(r => r.Path == ToSlash(Path));
                if (result.Error != null)
                {
                    State = new MachineState { ModelText = modelText, Problems = new List<Problem> { new Problem(1, result.Error) } };
                    return;
                }
                // The converted model, with the old ids: the diagram's problems are found in the old text by them.
                State = new MachineState { ModelText = result.ModelText, LayoutText = result.LayoutText, Model = result.Model };
                Old = new OldFile { Text = modelText, Result = result };
                return;
            }
            var layoutText = ReadText(LayoutPath);
            // Only the layout file has merge conflicts: its current side (saving writes it clean).
            if (layoutText != null && Core.Merge.HasConflictMarkers(layoutText))
            {
                try
                {
                    layoutText = Core.Merge.SplitConflicts(layoutText).Current;
                    LayoutConflict = true;
                }
                catch (ConflictMarkerException)
                {
                    // unreadable: placed automatically
                }
            }
            var s = MachineFiles.StateOf(modelText, layoutText);
            if (s.LayoutProblem != null && layoutText != null && !LayoutConflict) UnreadableLayout = new UnreadableLayout { Text = layoutText, Problem = s.LayoutProblem };
            // An unreadable layout file is left for automatic places: writing the state as it is keeps that file.
            if (UnreadableLayout != null) s.LayoutText = null;
            LayoutMissing = layoutText == null;
            State = s;
        }

        /// <summary>
        /// A model file with merge conflicts: its two sides (the layout file's too when it has conflicts) are the diagram
        /// merge's, and the result starts as the current side. When a side isn't a valid machine file, no merge: its problems.
        /// </summary>
        private void ReadConflicted(string modelText)
        {
            void Fail(string message)
            {
                State = new MachineState { ModelText = modelText, Problems = new List<Problem> { new Problem(1, message) } };
                MergeFallback = true;
            }
            ConflictSplit model;
            try
            {
                model = Core.Merge.SplitConflicts(modelText);
            }
            catch (ConflictMarkerException e)
            {
                Fail($"The file has merge conflicts whose markers can't be read: {e.Message}");
                return;
            }
            var layoutRaw = ReadText(LayoutPath);
            string current = layoutRaw, incoming = layoutRaw;
            if (layoutRaw != null && Core.Merge.HasConflictMarkers(layoutRaw))
            {
                try
                {
                    var split = Core.Merge.SplitConflicts(layoutRaw);
                    current = split.Current;
                    incoming = split.Incoming;
                }
                catch (ConflictMarkerException)
                {
                    current = incoming = null;
                }
            }
            foreach (var (side, text) in new[] { ("current", model.Current), ("incoming", model.Incoming) })
            {
                var r = Format.ReadModel(text);
                if (r.Failed)
                {
                    Fail($"The file has merge conflicts, and its {side} side is not a valid state machine file: line {r.Problems[0].Line}: {r.Problems[0].Message}");
                    return;
                }
            }
            var s = MachineFiles.StateOf(model.Current, current);
            s.LayoutText = current;
            State = s;
            Merge = new MergeInfo
            {
                Current = new MachineTexts(model.Current, current),
                Incoming = new MachineTexts(model.Incoming, incoming),
                Base = Git.CommonVersion(Path),
            };
        }

        /// <summary>Reads the files again: the state on disk, its history dropped.</summary>
        public void Reload()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Read();
            Saved = State;
            _undo.Clear();
            _redo.Clear();
            OnStateChanged();
        }

        /// <summary>The files changed on disk: a document without changes of its own follows them.</summary>
        private void FollowDisk()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Dirty || Merge != null) return;
            var modelText = ReadText(Path);
            if (modelText == null) return; // deleted: the editor keeps what it has
            var layoutText = ReadText(LayoutPath);
            if (modelText == State.ModelText && (layoutText == State.LayoutText || UnreadableLayout?.Text == layoutText)) return;
            if (Old != null && modelText == Old.Text) return;
            Reload();
        }

        // ---------------------------------------------------------------- editing

        /// <summary>An edit of the diagram: the document's new state, undoable. A message when it can't be made, else null.</summary>
        public string Edit(FsmModel model)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_readOnly) return "This diagram is read-only.";
            MachineState next;
            try
            {
                next = MachineFiles.Write(model);
            }
            catch (FormatLossException e)
            {
                return $"The change can't be saved: {e.Message}";
            }
            // The state keeps its own copy: the diagram goes on editing its model.
            next.Model = model.Clone();
            if (next.SameFiles(State))
            {
                // Nothing that the files hold changed (a selection, an equal value): no edit to undo.
                State.Model = next.Model;
                State.Lines = next.Lines;
                return null;
            }
            _undo.Add(State);
            _redo.Clear();
            State = next;
            OnStateChanged();
            return null;
        }

        public void Undo()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!CanUndo) return;
            _redo.Add(State);
            State = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            OnStateChanged();
        }

        public void Redo()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!CanRedo) return;
            _undo.Add(State);
            State = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            OnStateChanged();
        }

        private void OnStateChanged()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // A borrowed layout is only what the diagram showed before: an edit made on it holds its places.
            DisplayModel = null;
            Changed?.Invoke(this, EventArgs.Empty);
            NotifyDirty();
        }

        /// <summary>Tells Visual Studio the dirty state changed (the document tab's mark).</summary>
        private void NotifyDirty()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_cookie == 0) return;
            if (Package.GetGlobalService(typeof(SVsRunningDocumentTable)) is IVsRunningDocumentTable rdt)
            {
                rdt.NotifyDocumentChanged(_cookie, (uint)__VSRDTATTRIB.RDTA_DocDataIsDirty);
            }
        }

        /// <summary>Raises <see cref="Changed"/> without a new state (the diagram resyncs).</summary>
        public void Refresh() => Changed?.Invoke(this, EventArgs.Empty);

        // ---------------------------------------------------------------- saving

        private static void WriteFile(string path, string text) => File.WriteAllText(path, text, Utf8);

        /// <summary>
        /// Writes the state's two files at <paramref name="target"/> (and, for an old file saved in place, converts the
        /// files linked to it). False when the user cancelled (the layout file couldn't be read: replace it?).
        /// </summary>
        private bool WriteTo(string target, MachineState s, out string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            message = null;
            var inPlace = string.Equals(System.IO.Path.GetFullPath(target), System.IO.Path.GetFullPath(Path), StringComparison.OrdinalIgnoreCase);
            if (s.Problems != null || s.Model == null)
            {
                // A model file that can't be read: written as it is.
                WriteFile(target, s.ModelText);
                return true;
            }
            var layoutTarget = MachineFiles.LayoutPathOf(target);
            var writeLayout = true;
            if (UnreadableLayout != null && inPlace && ReadText(layoutTarget) == UnreadableLayout.Text)
            {
                var answer = Dialogs.AskUnreadableLayout(System.IO.Path.GetFileName(layoutTarget), UnreadableLayout.Problem);
                if (answer == null) return false;
                writeLayout = answer.Value;
            }
            var layoutText = s.LayoutText ?? MachineFiles.Write(s.Model.Clone()).LayoutText;
            WriteFile(target, s.ModelText);
            if (writeLayout) WriteFile(layoutTarget, layoutText);
            if (Old != null && inPlace) message = ConvertLinked();
            return true;
        }

        /// <summary>After an old file was saved in the new format: converts the old files linked to it; what happened, said.</summary>
        private string ConvertLinked()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var converted = new List<(string, Converted)> { (System.IO.Path.GetFileName(Path), Old.Result) };
            var skipped = new List<string>();
            foreach (var r in Conversion.ConvertGroup(LinkedOldFiles(Path, Old.Text)))
            {
                if (r.Path == ToSlash(Path)) continue;
                var file = FromSlash(r.Path);
                var open = FsmEditorPackage.Instance?.DocumentFor(file);
                if (r.Error != null || (open != null && open.Dirty))
                {
                    skipped.Add(System.IO.Path.GetFileName(file));
                    continue;
                }
                try
                {
                    WriteFile(file, r.ModelText);
                    WriteFile(MachineFiles.LayoutPathOf(file), r.LayoutText);
                    converted.Add((System.IO.Path.GetFileName(file), r));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    skipped.Add(System.IO.Path.GetFileName(file));
                }
            }
            return MachineFiles.ConversionMessage(converted, skipped);
        }

        /// <summary>Saves in place; false when cancelled. Throws IOException when the files can't be written.</summary>
        public bool Save()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var s = State;
            if (!WriteTo(Path, s, out var message)) return false;
            Old = null;
            UnreadableLayout = null;
            LayoutConflict = false;
            Saved = s;
            NotifyDirty();
            Changed?.Invoke(this, EventArgs.Empty);
            SavedWithMessage?.Invoke(this, message);
            return true;
        }

        // ---------------------------------------------------------------- IVsPersistDocData2

        public int GetGuidEditorType(out Guid pClassID)
        {
            pClassID = PackageGuids.EditorFactory;
            return VSConstants.S_OK;
        }

        public int IsDocDataDirty(out int pfDirty)
        {
            pfDirty = Dirty ? 1 : 0;
            return VSConstants.S_OK;
        }

        public int SetUntitledDocPath(string pszDocDataPath) => VSConstants.E_NOTIMPL;

        public int LoadDocData(string pszMkDocument)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Path = pszMkDocument;
            try
            {
                _readOnly |= File.Exists(Path) && new FileInfo(Path).IsReadOnly;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // checked again when saving
            }
            Read();
            Saved = State;
            Watch();
            IsLoaded = true;
            Loaded?.Invoke(this, EventArgs.Empty);
            return VSConstants.S_OK;
        }

        public int SaveDocData(VSSAVEFLAGS dwSave, out string pbstrMkDocumentNew, out int pfSaveCanceled)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            pbstrMkDocumentNew = null;
            pfSaveCanceled = 0;
            if (dwSave == VSSAVEFLAGS.VSSAVE_Save || dwSave == VSSAVEFLAGS.VSSAVE_SilentSave)
            {
                try
                {
                    if (!Save()) pfSaveCanceled = 1;
                    pbstrMkDocumentNew = Path;
                    return VSConstants.S_OK;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, e.Message, $"{System.IO.Path.GetFileName(Path)} could not be saved",
                        OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                    return VSConstants.E_FAIL;
                }
            }
            // Save As and Save Copy As: the shell asks for the file and calls IPersistFileFormat.Save.
            if (!(Package.GetGlobalService(typeof(SVsUIShell)) is IVsUIShell shell)) return VSConstants.E_FAIL;
            return shell.SaveDocDataToFile(dwSave, this, Path, out pbstrMkDocumentNew, out pfSaveCanceled);
        }

        public int Close()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Unwatch();
            return VSConstants.S_OK;
        }

        public int OnRegisterDocData(uint docCookie, IVsHierarchy pHierNew, uint itemidNew)
        {
            _cookie = docCookie;
            return VSConstants.S_OK;
        }

        public int RenameDocData(uint grfAttribs, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Unwatch();
            Path = pszMkDocumentNew;
            Watch();
            Changed?.Invoke(this, EventArgs.Empty);
            return VSConstants.S_OK;
        }

        public int IsDocDataReloadable(out int pfReloadable)
        {
            pfReloadable = 1;
            return VSConstants.S_OK;
        }

        public int ReloadDocData(uint grfFlags)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Reload();
            return VSConstants.S_OK;
        }

        public int SetDocDataDirty(int fDirty) => VSConstants.E_NOTIMPL;

        public int IsDocDataReadOnly(out int pfReadOnly)
        {
            pfReadOnly = _readOnly ? 1 : 0;
            return VSConstants.S_OK;
        }

        public int SetDocDataReadOnly(int fReadOnly)
        {
            _readOnly = fReadOnly != 0;
            return VSConstants.S_OK;
        }

        // ---------------------------------------------------------------- IPersistFileFormat (Save As)

        public int GetClassID(out Guid pClassID)
        {
            pClassID = PackageGuids.EditorFactory;
            return VSConstants.S_OK;
        }

        public int IsDirty(out int pfIsDirty)
        {
            pfIsDirty = Dirty ? 1 : 0;
            return VSConstants.S_OK;
        }

        public int InitNew(uint nFormatIndex) => VSConstants.S_OK;

        public int Load(string pszFilename, uint grfMode, int fReadOnly)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return LoadDocData(pszFilename);
        }

        public int Save(string pszFilename, int fRemember, uint nFormatIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var target = pszFilename ?? Path;
            try
            {
                if (fRemember == 0 || string.Equals(target, Path, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(target, Path, StringComparison.OrdinalIgnoreCase)) return Save() ? VSConstants.S_OK : VSConstants.E_ABORT;
                    // Save a copy: the document stays as it is.
                    return WriteTo(target, State, out _) ? VSConstants.S_OK : VSConstants.E_ABORT;
                }
                // Save As: the document is now the new file.
                if (!WriteTo(target, State, out _)) return VSConstants.E_ABORT;
                Unwatch();
                Path = target;
                Old = null;
                UnreadableLayout = null;
                Saved = State;
                Watch();
                NotifyDirty();
                Changed?.Invoke(this, EventArgs.Empty);
                return VSConstants.S_OK;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return Marshal.GetHRForException(e);
            }
        }

        public int SaveCompleted(string pszFilename) => VSConstants.S_OK;

        public int GetCurFile(out string ppszFilename, out uint pnFormatIndex)
        {
            ppszFilename = Path;
            pnFormatIndex = 0;
            return VSConstants.S_OK;
        }

        public int GetFormatList(out string ppszFormatList)
        {
            ppszFormatList = "FSM State Machine (*.fsm)\n*.fsm\n";
            return VSConstants.S_OK;
        }

        // ---------------------------------------------------------------- IVsFileBackup (AutoRecover)

        /// <summary>The state last backed up, or null.</summary>
        private MachineState _backedUp;

        /// <summary>
        /// AutoRecover's backup: the model file's text of the unsaved state, which Visual Studio writes over the file when
        /// it recovers it after a crash. The layout file isn't recovered: the machine's elements keep the places it has.
        /// </summary>
        public int BackupFile(string pszBackupFileName)
        {
            try
            {
                File.WriteAllText(pszBackupFileName, State.ModelText, Utf8);
                _backedUp = State;
                return VSConstants.S_OK;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return VSConstants.E_FAIL;
            }
        }

        public int IsBackupFileObsolete(out int pbObsolete)
        {
            pbObsolete = Dirty && State != _backedUp ? 1 : 0;
            return VSConstants.S_OK;
        }

        // ---------------------------------------------------------------- file changes

        private void Watch()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(Package.GetGlobalService(typeof(SVsFileChangeEx)) is IVsFileChangeEx changes)) return;
            foreach (var file in new[] { Path, LayoutPath })
            {
                var flags = (uint)(_VSFILECHANGEFLAGS.VSFILECHG_Time | _VSFILECHANGEFLAGS.VSFILECHG_Size | _VSFILECHANGEFLAGS.VSFILECHG_Add | _VSFILECHANGEFLAGS.VSFILECHG_Del);
                if (ErrorHandler.Succeeded(changes.AdviseFileChange(file, flags, this, out var cookie))) _fileWatches.Add(cookie);
            }
        }

        private void Unwatch()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsFileChangeEx)) is IVsFileChangeEx changes)
            {
                foreach (var cookie in _fileWatches) changes.UnadviseFileChange(cookie);
            }
            _fileWatches.Clear();
        }

        public int FilesChanged(uint cChanges, string[] rgpszFile, uint[] rggrfChange)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            FollowDisk();
            return VSConstants.S_OK;
        }

        public int DirectoryChanged(string pszDirectory) => VSConstants.S_OK;
    }
}
