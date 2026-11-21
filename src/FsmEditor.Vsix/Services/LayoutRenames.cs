using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Community.VisualStudio.Toolkit;
using EnvDTE;
using EnvDTE80;
using FsmEditor.Core;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace FsmEditor.Services
{
    /// <summary>
    /// A model file renamed in Solution Explorer takes its layout file along (<c>X.fsm.layout</c>), unless that one was
    /// renamed too or the new name has one already. Projects report their renames (<see cref="OnAfterRenameFiles"/>);
    /// an open folder (Folder View) doesn't, so its files are watched instead.
    /// </summary>
    internal sealed class LayoutRenames : IVsTrackProjectDocumentsEvents2
    {
        private uint _cookie;
        private IVsTrackProjectDocuments2 _tracker;
        private FileSystemWatcher _folder;

        public void Advise()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _tracker = Package.GetGlobalService(typeof(SVsTrackProjectDocuments)) as IVsTrackProjectDocuments2;
            _tracker?.AdviseTrackProjectDocumentsEvents(this, out _cookie);
            VS.Events.SolutionEvents.OnAfterOpenFolder += Watch;
            VS.Events.SolutionEvents.OnAfterCloseFolder += _ => Watch(null);
            Watch(OpenFolder());
        }

        /// <summary>The folder open in Visual Studio (Open Folder), or null.</summary>
        private static string OpenFolder()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution solution)) return null;
            if (solution.GetProperty((int)__VSPROPID7.VSPROPID_IsInOpenFolderMode, out var open) != VSConstants.S_OK || !(open is bool b) || !b) return null;
            return solution.GetSolutionInfo(out var dir, out _, out _) == VSConstants.S_OK ? dir : null;
        }

        private void Watch(string folder)
        {
            _folder?.Dispose();
            _folder = null;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            _folder = new FileSystemWatcher(folder, "*.fsm") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName };
            // The watcher raises events on a thread pool thread; the package's task factory tracks the switch.
            _folder.Renamed += (s, e) =>
            {
                var jtf = FsmEditorPackage.Instance?.JoinableTaskFactory;
                jtf?.RunAsync(async () =>
                {
                    await jtf.SwitchToMainThreadAsync();
                    Renamed(e.OldFullPath, e.FullPath, new string[0]);
                }).FileAndForget("FsmEditor/LayoutRenames");
            };
            _folder.EnableRaisingEvents = true;
        }

        /// <summary>A model file renamed: its layout file follows, unless it is among <paramref name="renamedToo"/>.</summary>
        private static void Renamed(string oldName, string newName, string[] renamedToo)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!FsmDocuments.IsFsm(oldName) || !FsmDocuments.IsFsm(newName)) return;
            var from = MachineFiles.LayoutPathOf(oldName);
            var to = MachineFiles.LayoutPathOf(newName);
            if (renamedToo.Contains(from, StringComparer.OrdinalIgnoreCase)) return;
            if (!File.Exists(from) || File.Exists(to)) return;
            Rename(from, to);
        }

        public int OnAfterRenameFiles(int cProjects, int cFiles, IVsProject[] rgpProjects, int[] rgFirstIndices, string[] rgszMkOldNames, string[] rgszMkNewNames, VSRENAMEFILEFLAGS[] rgFlags)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var renamed = rgszMkOldNames.Take(cFiles).ToArray();
            for (var i = 0; i < cFiles; i++) Renamed(rgszMkOldNames[i], rgszMkNewNames[i], renamed);
            return VSConstants.S_OK;
        }

        /// <summary>Renames the project item of the file when a project lists it, else the file alone.</summary>
        private static void Rename(string from, string to)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var item = (Package.GetGlobalService(typeof(DTE)) as DTE2)?.Solution?.FindProjectItem(from);
                if (item != null && string.Equals(item.FileNames[1], from, StringComparison.OrdinalIgnoreCase))
                {
                    item.Name = Path.GetFileName(to);
                    return;
                }
            }
            catch (Exception e) when (e is COMException || e is ArgumentException || e is InvalidOperationException)
            {
                // moved as a file below
            }
            try
            {
                if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // left as it is: the renamed machine's elements are placed automatically
            }
        }

        public int OnAfterAddFilesEx(int cProjects, int cFiles, IVsProject[] rgpProjects, int[] rgFirstIndices, string[] rgpszMkDocuments, VSADDFILEFLAGS[] rgFlags) => VSConstants.S_OK;
        public int OnAfterAddDirectoriesEx(int cProjects, int cDirectories, IVsProject[] rgpProjects, int[] rgFirstIndices, string[] rgpszMkDocuments, VSADDDIRECTORYFLAGS[] rgFlags) => VSConstants.S_OK;
        public int OnAfterRemoveFiles(int cProjects, int cFiles, IVsProject[] rgpProjects, int[] rgFirstIndices, string[] rgpszMkDocuments, VSREMOVEFILEFLAGS[] rgFlags) => VSConstants.S_OK;
        public int OnAfterRemoveDirectories(int cProjects, int cDirectories, IVsProject[] rgpProjects, int[] rgFirstIndices, string[] rgpszMkDocuments, VSREMOVEDIRECTORYFLAGS[] rgFlags) => VSConstants.S_OK;
        public int OnQueryRenameFiles(IVsProject pProject, int cFiles, string[] rgszMkOldNames, string[] rgszMkNewNames, VSQUERYRENAMEFILEFLAGS[] rgFlags, VSQUERYRENAMEFILERESULTS[] pSummaryResult, VSQUERYRENAMEFILERESULTS[] rgResults) => VSConstants.S_OK;
        public int OnQueryRenameDirectories(IVsProject pProject, int cDirs, string[] rgszMkOldNames, string[] rgszMkNewNames, VSQUERYRENAMEDIRECTORYFLAGS[] rgFlags, VSQUERYRENAMEDIRECTORYRESULTS[] pSummaryResult, VSQUERYRENAMEDIRECTORYRESULTS[] rgResults) => VSConstants.S_OK;
        public int OnAfterRenameDirectories(int cProjects, int cDirs, IVsProject[] rgpProjects, int[] rgFirstIndices, string[] rgszMkOldNames, string[] rgszMkNewNames, VSRENAMEDIRECTORYFLAGS[] rgFlags) => VSConstants.S_OK;
        public int OnQueryAddFiles(IVsProject pProject, int cFiles, string[] rgpszMkDocuments, VSQUERYADDFILEFLAGS[] rgFlags, VSQUERYADDFILERESULTS[] pSummaryResult, VSQUERYADDFILERESULTS[] rgResults) => VSConstants.S_OK;
        public int OnQueryAddDirectories(IVsProject pProject, int cDirectories, string[] rgpszMkDocuments, VSQUERYADDDIRECTORYFLAGS[] rgFlags, VSQUERYADDDIRECTORYRESULTS[] pSummaryResult, VSQUERYADDDIRECTORYRESULTS[] rgResults) => VSConstants.S_OK;
        public int OnQueryRemoveFiles(IVsProject pProject, int cFiles, string[] rgpszMkDocuments, VSQUERYREMOVEFILEFLAGS[] rgFlags, VSQUERYREMOVEFILERESULTS[] pSummaryResult, VSQUERYREMOVEFILERESULTS[] rgResults) => VSConstants.S_OK;
        public int OnQueryRemoveDirectories(IVsProject pProject, int cDirectories, string[] rgpszMkDocuments, VSQUERYREMOVEDIRECTORYFLAGS[] rgFlags, VSQUERYREMOVEDIRECTORYRESULTS[] pSummaryResult, VSQUERYREMOVEDIRECTORYRESULTS[] rgResults) => VSConstants.S_OK;
        public int OnAfterSccStatusChanged(int cProjects, int cFiles, IVsProject[] rgpProjects, int[] rgFirstIndices, string[] rgpszMkDocuments, uint[] rgdwSccStatus) => VSConstants.S_OK;
    }
}
