using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace FsmEditor.Editor
{
    /// <summary>
    /// Opens *.fsm files in the diagram editor. The document data is an
    /// <see cref="FsmDocument"/>, which holds the machine's two files (the model
    /// and its layout) and their history: Visual Studio's save, dirty marker and
    /// reload act on it, and the diagram's undo and redo walk its history. A text
    /// editor can't share it: Open as Text closes the diagram first.
    /// </summary>
    [Guid(PackageGuids.EditorFactoryString)]
    internal sealed class FsmEditorFactory : IVsEditorFactory, IDisposable
    {
        private readonly FsmEditorPackage _package;
        private ServiceProvider _services;

        public FsmEditorFactory(FsmEditorPackage package) => _package = package;

        public int SetSite(IOleServiceProvider psp)
        {
            _services = new ServiceProvider(psp);
            return VSConstants.S_OK;
        }

        public int Close() => VSConstants.S_OK;

        public int MapLogicalView(ref Guid rguidLogicalView, out string pbstrPhysicalView)
        {
            pbstrPhysicalView = null;
            return rguidLogicalView == VSConstants.LOGVIEWID_Primary || rguidLogicalView == VSConstants.LOGVIEWID_Designer
                ? VSConstants.S_OK
                : VSConstants.E_NOTIMPL;
        }

        public int CreateEditorInstance(uint grfCreateDoc, string pszMkDocument, string pszPhysicalView, IVsHierarchy pvHier, uint itemid,
            IntPtr punkDocDataExisting, out IntPtr ppunkDocView, out IntPtr ppunkDocData, out string pbstrEditorCaption, out Guid pguidCmdUI, out int pgrfCDW)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ppunkDocView = IntPtr.Zero;
            ppunkDocData = IntPtr.Zero;
            pbstrEditorCaption = null;
            pguidCmdUI = PackageGuids.EditorFactory;
            pgrfCDW = 0;
            if ((grfCreateDoc & (VSConstants.CEF_OPENFILE | VSConstants.CEF_SILENT)) == 0) return VSConstants.E_INVALIDARG;

            FsmDocument document;
            if (punkDocDataExisting == IntPtr.Zero)
            {
                // A new document: the running document table loads the files into it (LoadDocData).
                document = new FsmDocument(pszMkDocument, CompareFiles.IsReadOnlySide(pszMkDocument));
            }
            else
            {
                // Already open in the diagram editor: share it; open in another editor (as text): incompatible.
                document = Marshal.GetObjectForIUnknown(punkDocDataExisting) as FsmDocument;
                if (document == null) return VSConstants.VS_E_INCOMPATIBLEDOCDATA;
            }

            var pane = new FsmEditorPane(_package, document);
            ppunkDocView = Marshal.GetIUnknownForObject(pane);
            ppunkDocData = Marshal.GetIUnknownForObject(document);
            pbstrEditorCaption = "";
            return VSConstants.S_OK;
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _services?.Dispose();
        }
    }
}
