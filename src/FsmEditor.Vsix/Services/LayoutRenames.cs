using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    /// renamed too or the new name has one already.
    /// </summary>
    internal sealed class LayoutRenames : IVsTrackProjectDocumentsEvents2
    {
        private uint _cookie;
        private IVsTrackProjectDocuments2 _tracker;

        public void Advise()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _tracker = Package.GetGlobalService(typeof(SVsTrackProjectDocuments)) as IVsTrackProjectDocuments2;
            _tracker?.AdviseTrackProjectDocumentsEvents(this, out _cookie);
        }

        public int OnAfterRenameFiles(int cProjects, int cFiles, IVsProject[] rgpProjects, int[] rgFirstIndices, string[] rgszMkOldNames, string[] rgszMkNewNames, VSRENAMEFILEFLAGS[] rgFlags)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            for (var i = 0; i < cFiles; i++)
            {
                var oldName = rgszMkOldNames[i];
                var newName = rgszMkNewNames[i];
                if (!FsmDocuments.IsFsm(oldName) || !FsmDocuments.IsFsm(newName)) continue;
                var from = MachineFiles.LayoutPathOf(oldName);
                var to = MachineFiles.LayoutPathOf(newName);
                if (rgszMkOldNames.Take(cFiles).Contains(from, StringComparer.OrdinalIgnoreCase)) continue; // renamed too
                if (!File.Exists(from) || File.Exists(to)) continue;
                Rename(from, to);
            }
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
