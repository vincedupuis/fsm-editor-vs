using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace FsmEditor.Editor
{
    /// <summary>
    /// Opens *.fsm files in the diagram editor. The document data is Visual
    /// Studio's own text buffer holding the XMI, so saving, the dirty marker,
    /// undo/redo and a text view opened side by side (Open as XMI Text) all
    /// work on the same document.
    /// </summary>
    [Guid(PackageGuids.EditorFactoryString)]
    internal sealed class FsmEditorFactory : IVsEditorFactory, IDisposable
    {
        private readonly FsmEditorPackage _package;
        private ServiceProvider _services;
        private IOleServiceProvider _site;

        public FsmEditorFactory(FsmEditorPackage package) => _package = package;

        public int SetSite(IOleServiceProvider psp)
        {
            _site = psp;
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

            if (!(_services.GetService(typeof(SComponentModel)) is IComponentModel componentModel)) return VSConstants.E_FAIL;
            var adapters = componentModel.GetService<IVsEditorAdaptersFactoryService>();
            IVsTextLines textLines;
            if (punkDocDataExisting == IntPtr.Zero)
            {
                // A new buffer: the running document table loads the file into it.
                textLines = (IVsTextLines)adapters.CreateVsTextBufferAdapter(_site);
            }
            else
            {
                // Already open (for example as text): share its buffer.
                textLines = Marshal.GetObjectForIUnknown(punkDocDataExisting) as IVsTextLines;
                if (textLines == null) return VSConstants.VS_E_INCOMPATIBLEDOCDATA;
            }

            var pane = new FsmEditorPane(_package, pszMkDocument, textLines, adapters, componentModel);
            ppunkDocView = Marshal.GetIUnknownForObject(pane);
            ppunkDocData = Marshal.GetIUnknownForObject(textLines);
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
