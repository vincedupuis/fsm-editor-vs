using System;

namespace FsmEditor
{
    /// <summary>GUIDs of the package, editor and commands (keep in sync with VSCommandTable.vsct).</summary>
    internal static class PackageGuids
    {
        public const string PackageString = "43c53194-4f12-42ec-8fcc-0bbca916caaf";
        public const string EditorFactoryString = "66c7b80a-5999-47c1-838f-fe34d6caeb45";
        public const string FsmFileSelectedRuleString = "ad807c01-9753-4922-8090-a020f6c165e2";
        public const string CommandSetString = "17a524b9-398e-41e9-a5c4-83d740321394";

        public static readonly Guid Package = new Guid(PackageString);
        public static readonly Guid EditorFactory = new Guid(EditorFactoryString);
        public static readonly Guid CommandSet = new Guid(CommandSetString);
    }

    internal static class PackageIds
    {
        public const int NewStateMachine = 0x0100;
        public const int GenerateCode = 0x0101;
        public const int ExportSvg = 0x0102;
        public const int OpenAsText = 0x0103;
        public const int OpenDiagram = 0x0104;
    }
}
