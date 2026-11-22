using System;
using System.Runtime.InteropServices;
using FsmEditor.Services;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace FsmEditor.Editor
{
    /// <summary>
    /// Opens a machine's layout file (<c>X.fsm.layout</c>) in the text editor, as JSON (<see cref="ContentTypes"/>).
    /// Visual Studio picks an editor by a file's last suffix only, <c>.layout</c>, which an installation may give to
    /// the XML editor. Other <c>.layout</c> files are refused (<c>VS_E_UNSUPPORTEDFORMAT</c>), so they open in the
    /// editor they would have had.
    /// </summary>
    [Guid(PackageGuids.LayoutEditorFactoryString)]
    internal sealed class LayoutEditorFactory : IVsEditorFactory, IDisposable
    {
        private ServiceProvider _services;
        private IOleServiceProvider _site;

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
            return rguidLogicalView == VSConstants.LOGVIEWID_Primary || rguidLogicalView == VSConstants.LOGVIEWID_TextView
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
            pguidCmdUI = VSConstants.GUID_TextEditorFactory;
            pgrfCDW = 0;
            if ((grfCreateDoc & (VSConstants.CEF_OPENFILE | VSConstants.CEF_SILENT)) == 0) return VSConstants.E_INVALIDARG;
            if (pszMkDocument == null || !pszMkDocument.EndsWith(".fsm.layout", StringComparison.OrdinalIgnoreCase)) return VSConstants.VS_E_UNSUPPORTEDFORMAT;

            var model = _services.GetService(typeof(SComponentModel)) as IComponentModel;
            var adapters = model?.GetService<IVsEditorAdaptersFactoryService>();
            if (adapters == null) return VSConstants.E_FAIL;
            IVsTextLines lines;
            if (punkDocDataExisting == IntPtr.Zero)
            {
                // A new buffer, JSON from the start; the running document table loads the file into it.
                lines = ContentTypes.TryJson(model.GetService<IContentTypeRegistryService>(), out var json)
                    ? adapters.CreateVsTextBufferAdapter(_site, json) as IVsTextLines
                    : adapters.CreateVsTextBufferAdapter(_site) as IVsTextLines;
                if (lines == null) return VSConstants.E_FAIL;
            }
            else
            {
                // Already open as text: share its buffer; open in another editor: incompatible.
                lines = Marshal.GetObjectForIUnknown(punkDocDataExisting) as IVsTextLines;
                if (lines == null) return VSConstants.VS_E_INCOMPATIBLEDOCDATA;
            }
            var window = adapters.CreateVsCodeWindowAdapter(_site);
            ErrorHandler.ThrowOnFailure(window.SetBuffer(lines));
            ppunkDocView = Marshal.GetIUnknownForObject(window);
            ppunkDocData = Marshal.GetIUnknownForObject(lines);
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
