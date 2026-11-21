#if JSON_SCHEMAS
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading.Tasks;
using Microsoft.WebTools.Languages.Json.Schema;

namespace FsmEditor.Services
{
    /// <summary>
    /// The JSON editor's schema of a model file (fsm-cli's <c>fsm.schema.json</c>) and of a layout file
    /// (fsm-editor-common's <c>fsm-layout.schema.json</c>), both bundled in <c>cli\schemas</c>. Compiled only where the
    /// build finds Visual Studio's JSON editor (<c>Microsoft.WebTools.Languages.Json</c>, see the project file).
    /// </summary>
    [Export(typeof(IJsonSchemaSelector))]
    internal sealed class JsonSchemas : IJsonSchemaSelector
    {
        private static readonly string Folder = Path.Combine(Path.GetDirectoryName(typeof(JsonSchemas).Assembly.Location) ?? "", "cli", "schemas");
        private static readonly string Model = new Uri(Path.Combine(Folder, "fsm.schema.json")).AbsoluteUri;
        private static readonly string Layout = new Uri(Path.Combine(Folder, "fsm-layout.schema.json")).AbsoluteUri;

        public event EventHandler AvailableSchemasChanged
        {
            add { }
            remove { }
        }

        public Task<IEnumerable<string>> GetAvailableSchemasAsync() => Task.FromResult<IEnumerable<string>>(new[] { Model, Layout });

        public string GetSchemaFor(string fileLocation)
        {
            if (fileLocation == null) return null;
            if (fileLocation.EndsWith(".fsm.layout", StringComparison.OrdinalIgnoreCase)) return Layout;
            return fileLocation.EndsWith(".fsm", StringComparison.OrdinalIgnoreCase) ? Model : null;
        }
    }
}
#endif
