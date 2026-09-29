using System.IO;
using Community.VisualStudio.Toolkit;
using FsmEditor.Services;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace FsmEditor.Commands
{
    /// <summary>Where a command applies: the .fsm selected in Solution Explorer, or the active document.</summary>
    internal static class Targets
    {
        /// <summary>The .fsm file the command acts on, or null.</summary>
        public static async System.Threading.Tasks.Task<string> MachineAsync(bool preferSelection)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var active = FsmDocuments.ActiveDocumentPath();
            if (!preferSelection && FsmDocuments.IsFsm(active)) return active;
            var item = await VS.Solutions.GetActiveItemAsync();
            if (item?.Type == SolutionItemType.PhysicalFile && FsmDocuments.IsFsm(item.FullPath)) return item.FullPath;
            return FsmDocuments.IsFsm(active) ? active : null;
        }

        /// <summary>The folder selected in Solution Explorer (a folder, or a project's folder), or null.</summary>
        public static async System.Threading.Tasks.Task<string> FolderAsync()
        {
            var item = await VS.Solutions.GetActiveItemAsync();
            switch (item?.Type)
            {
                case SolutionItemType.PhysicalFolder:
                    return item.FullPath;
                case SolutionItemType.Project:
                case SolutionItemType.PhysicalFile:
                    return item.FullPath != null ? Path.GetDirectoryName(item.FullPath) : null;
                default:
                    return null;
            }
        }
    }

    [Command(PackageGuids.CommandSetString, PackageIds.NewStateMachine)]
    internal sealed class NewStateMachineCommand : BaseCommand<NewStateMachineCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            var folder = await Targets.FolderAsync();
            if (folder == null && await VS.Solutions.GetCurrentSolutionAsync() is Solution solution && solution.FullPath != null)
            {
                folder = Path.GetDirectoryName(solution.FullPath);
            }
            await FsmEditorPackage.Instance.NewStateMachineAsync(folder);
        }
    }

    [Command(PackageGuids.CommandSetString, PackageIds.GenerateCode)]
    internal sealed class GenerateCodeCommand : BaseCommand<GenerateCodeCommand>
    {
        protected override void BeforeQueryStatus(System.EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var fsm = FsmDocuments.IsFsm(FsmDocuments.ActiveDocumentPath()) || SelectedIsFsm();
            Command.Visible = fsm;
            Command.Enabled = fsm;
        }

        internal static bool SelectedIsFsm()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return FsmDocuments.IsFsm(FsmDocuments.SelectedItemPath());
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            // From Solution Explorer the selected file wins; from the editor, the active document.
            var fromSelection = !FsmDocuments.ActiveIsDiagram();
            var source = await Targets.MachineAsync(fromSelection);
            await FsmEditorPackage.Instance.GenerateCodeAsync(source, e.InValue as string);
        }
    }

    [Command(PackageGuids.CommandSetString, PackageIds.ExportSvg)]
    internal sealed class ExportSvgCommand : BaseCommand<ExportSvgCommand>
    {
        protected override void BeforeQueryStatus(System.EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Command.Visible = Command.Enabled = FsmDocuments.ActiveIsDiagram();
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var pane = FsmEditorPackage.Instance.PaneFor(FsmDocuments.ActiveDocumentPath());
            await FsmEditorPackage.Instance.ExportSvgAsync(pane);
        }
    }

    [Command(PackageGuids.CommandSetString, PackageIds.OpenAsText)]
    internal sealed class OpenAsTextCommand : BaseCommand<OpenAsTextCommand>
    {
        protected override void BeforeQueryStatus(System.EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Command.Visible = Command.Enabled = FsmDocuments.ActiveIsDiagram();
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var path = FsmDocuments.ActiveDocumentPath();
            if (FsmDocuments.IsFsm(path)) FsmDocuments.OpenAsText(path);
        }
    }

    [Command(PackageGuids.CommandSetString, PackageIds.OpenDiagram)]
    internal sealed class OpenDiagramCommand : BaseCommand<OpenDiagramCommand>
    {
        protected override void BeforeQueryStatus(System.EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // A .fsm open as text, or selected in Solution Explorer.
            var textEditor = FsmDocuments.IsFsm(FsmDocuments.ActiveDocumentPath()) && !FsmDocuments.ActiveIsDiagram();
            Command.Visible = Command.Enabled = textEditor || GenerateCodeCommand.SelectedIsFsm();
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var active = FsmDocuments.ActiveDocumentPath();
            var path = FsmDocuments.IsFsm(active) && !FsmDocuments.ActiveIsDiagram() ? active : await Targets.MachineAsync(true);
            if (FsmDocuments.IsFsm(path)) FsmDocuments.OpenInDiagram(path);
        }
    }
}
