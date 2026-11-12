using System.ComponentModel;
using System.Runtime.InteropServices;
using Community.VisualStudio.Toolkit;

namespace FsmEditor
{
    internal partial class OptionsProvider
    {
        [ComVisible(true)]
        public class GeneralOptions : BaseOptionPage<General> { }
    }

    /// <summary>Tools › Options › FSM Editor › General.</summary>
    public class General : BaseOptionModel<General>
    {
        [Category("Code generation")]
        [DisplayName("fsm executable")]
        [Description("Path of the fsm code generator (fsm.exe). Leave empty to use the one bundled with the extension, or else an fsm found on the PATH.")]
        [DefaultValue("")]
        public string CliPath { get; set; } = "";

        [Category("Code generation")]
        [DisplayName("fsm-generator executable")]
        [Description("Path of fsm-generator (fsm-generator.exe), the code generator that runs Jinja templates (.jinja). Leave empty to use the one bundled with the extension, or else one found on the PATH.")]
        [DefaultValue("")]
        public string GeneratorPath { get; set; } = "";

        [Category("Code generation")]
        [DisplayName("Save .fsm documents before generating")]
        [Description("The generator reads the files on disk: save open state machines with unsaved changes first.")]
        [DefaultValue(true)]
        public bool SaveBeforeGenerating { get; set; } = true;

        [Category("Trace Player")]
        [DisplayName("Trace formats")]
        [Description("Formats of the logs the Trace Player reads, besides its built-in format (… fsm <machine> <kind> <id> <name>, what an onTrace hook of the generated code can write): a JSON array of { \"name\", \"rules\": [{ \"kind\", \"match\" }], \"time\", \"machine\" }. Each rule is a regular expression whose named groups give the record (time, machine, id, name, and for transitions source and target) and its kind (event, enter, exit or transition, or a kind group). States and transitions are found by xmi:id, else by name; lines without a machine are about the format's machine, else a machine the Trace Player asks for.")]
        [DefaultValue("")]
        public string TraceFormats { get; set; } = "";
    }

    /// <summary>
    /// How the diagram editors show the toolbox and the properties panel
    /// (a <see cref="Editor.PaneMode"/> name, and whether the panel is closed).
    /// Saved with the other settings but not shown in Tools › Options.
    /// </summary>
    public class PanelLayout : BaseOptionModel<PanelLayout>
    {
        public string Toolbox { get; set; } = "Dock";
        public bool ToolboxClosed { get; set; }
        public string Properties { get; set; } = "Dock";
        public bool PropertiesClosed { get; set; }
    }
}
