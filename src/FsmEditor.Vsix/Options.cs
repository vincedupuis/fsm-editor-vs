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
        [DisplayName("Save .fsm documents before generating")]
        [Description("The generator reads the files on disk: save open state machines with unsaved changes first.")]
        [DefaultValue(true)]
        public bool SaveBeforeGenerating { get; set; } = true;
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
