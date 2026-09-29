using System;
using System.IO;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace FsmEditor.Services
{
    /// <summary>Opening, finding and saving state machine documents.</summary>
    internal static class FsmDocuments
    {
        /// <summary>The XML editor, which gives the XMI text colorization and folding.</summary>
        private static readonly Guid XmlEditorFactory = new Guid("FA3CD31E-987B-443A-9B81-186104E8DAC1");

        public static bool IsFsm(string path) => path != null && path.EndsWith(".fsm", StringComparison.OrdinalIgnoreCase);

        public static void OpenInDiagram(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            OpenWith(path, PackageGuids.EditorFactory);
        }

        public static void OpenAsText(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!OpenWith(path, XmlEditorFactory)) OpenWith(path, VSConstants.VsEditorFactoryGuid.TextEditor_guid);
        }

        private static bool OpenWith(string path, Guid editor)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                VsShellUtilities.OpenDocumentWithSpecificEditor(ServiceProvider.GlobalProvider, path, editor, VSConstants.LOGVIEWID_Primary,
                    out _, out _, out IVsWindowFrame frame);
                frame?.Show();
                return frame != null;
            }
            catch (COMException)
            {
                return false;
            }
        }

        /// <summary>Path of the document in the active window, or null.</summary>
        public static string ActiveDocumentPath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var monitor = Package.GetGlobalService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            if (monitor == null || monitor.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out var value) != VSConstants.S_OK) return null;
            if (value is IVsWindowFrame frame && frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out var moniker) == VSConstants.S_OK)
            {
                return moniker as string;
            }
            return null;
        }

        /// <summary>Path of the single item selected in Solution Explorer, or null.</summary>
        public static string SelectedItemPath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(Package.GetGlobalService(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection monitor)) return null;
            IntPtr hierarchyPtr = IntPtr.Zero, containerPtr = IntPtr.Zero;
            try
            {
                if (monitor.GetCurrentSelection(out hierarchyPtr, out var itemId, out var multi, out containerPtr) != VSConstants.S_OK ||
                    hierarchyPtr == IntPtr.Zero || multi != null || itemId == VSConstants.VSITEMID_SELECTION)
                {
                    return null;
                }
                var hierarchy = Marshal.GetObjectForIUnknown(hierarchyPtr) as IVsHierarchy;
                return hierarchy != null && hierarchy.GetCanonicalName(itemId, out var name) == VSConstants.S_OK ? name : null;
            }
            finally
            {
                if (hierarchyPtr != IntPtr.Zero) Marshal.Release(hierarchyPtr);
                if (containerPtr != IntPtr.Zero) Marshal.Release(containerPtr);
            }
        }

        /// <summary>The editor of the active document window is the diagram editor.</summary>
        public static bool ActiveIsDiagram()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var monitor = Package.GetGlobalService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            if (monitor == null || monitor.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out var value) != VSConstants.S_OK) return false;
            return value is IVsWindowFrame frame &&
                frame.GetGuidProperty((int)__VSFPROPID.VSFPROPID_guidEditorType, out var editor) == VSConstants.S_OK &&
                editor == PackageGuids.EditorFactory;
        }

        /// <summary>Saves the open .fsm documents that have unsaved changes (the code generator reads the files).</summary>
        public static void SaveOpenMachines()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(Package.GetGlobalService(typeof(DTE)) is DTE2 dte)) return;
            foreach (Document doc in dte.Documents)
            {
                try
                {
                    if (IsFsm(doc.FullName) && !doc.Saved) doc.Save();
                }
                catch (COMException)
                {
                    // read-only or being closed: the file on disk is used
                }
            }
        }

        public static string SafeFullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
            {
                return path;
            }
        }
    }
}
