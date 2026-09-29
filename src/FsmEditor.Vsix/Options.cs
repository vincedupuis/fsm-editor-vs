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
}
