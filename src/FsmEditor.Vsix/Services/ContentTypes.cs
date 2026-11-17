using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Utilities;

namespace FsmEditor.Services
{
    /// <summary>
    /// The text editor shows a model file (<c>.fsm</c>) and its layout file (<c>.fsm.layout</c>) as JSON, with the JSON
    /// editor's colorization, folding and syntax errors, when Visual Studio has it. Old files (XMI) open as text in the
    /// XML editor instead (<see cref="FsmDocuments.OpenAsText"/>).
    /// </summary>
    internal static class ContentTypes
    {
        public const string Json = "JSON";

        internal static bool TryJson(IContentTypeRegistryService registry, out IContentType contentType)
        {
            contentType = registry?.GetContentType(Json);
            return contentType != null;
        }
    }

    [Export(typeof(IFilePathToContentTypeProvider))]
    [Name("FsmEditor.ModelFile")]
    [FileExtension(".fsm")]
    internal sealed class ModelFileContentType : IFilePathToContentTypeProvider
    {
        [Import]
        internal IContentTypeRegistryService Registry { get; set; }

        public bool TryGetContentTypeForFilePath(string filePath, out IContentType contentType) => ContentTypes.TryJson(Registry, out contentType);
    }

    [Export(typeof(IFilePathToContentTypeProvider))]
    [Name("FsmEditor.LayoutFile")]
    [FileExtension(".layout")]
    internal sealed class LayoutFileContentType : IFilePathToContentTypeProvider
    {
        [Import]
        internal IContentTypeRegistryService Registry { get; set; }

        /// <summary>Only <c>X.fsm.layout</c>: other <c>.layout</c> files keep their own.</summary>
        public bool TryGetContentTypeForFilePath(string filePath, out IContentType contentType)
        {
            contentType = null;
            return filePath != null && filePath.EndsWith(".fsm.layout", StringComparison.OrdinalIgnoreCase) && ContentTypes.TryJson(Registry, out contentType);
        }
    }
}
