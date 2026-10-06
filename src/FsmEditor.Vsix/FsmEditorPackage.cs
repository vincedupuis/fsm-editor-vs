using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Community.VisualStudio.Toolkit;
using FsmEditor.CodeGen;
using FsmEditor.Core;
using FsmEditor.Editor;
using FsmEditor.Services;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using Task = System.Threading.Tasks.Task;

namespace FsmEditor
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("#111", "#112", "0.3.7")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuids.PackageString)]
    [ProvideEditorFactory(typeof(FsmEditorFactory), 110, CommonPhysicalViewAttributes = (int)__VSPHYSICALVIEWATTRIBUTES.PVA_SupportsPreview, TrustLevel = __VSEDITORTRUSTLEVEL.ETL_AlwaysTrusted)]
    [ProvideEditorExtension(typeof(FsmEditorFactory), ".fsm", 50, NameResourceID = 110)]
    [ProvideEditorLogicalView(typeof(FsmEditorFactory), VSConstants.LOGVIEWID.Designer_string)]
    // The XMI text opens in the XML editor with its colorization.
    [ProvideLanguageExtension("{f6819a78-a205-47b5-be1c-675b3c7f0b8e}", ".fsm")]
    [ProvideUIContextRule(PackageGuids.FsmFileSelectedRuleString, "FSM file selected", "Fsm", new[] { "Fsm" }, new[] { "HierSingleSelectionName:.fsm$" })]
    [ProvideAutoLoad(PackageGuids.FsmFileSelectedRuleString, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.FolderOpened_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideOptionPage(typeof(OptionsProvider.GeneralOptions), "FSM Editor", "General", 0, 0, true, SupportsProfiles = true)]
    public sealed class FsmEditorPackage : ToolkitPackage
    {
        private const string SettingsCollection = @"FsmEditor\CodeGeneration";
        private readonly Dictionary<string, FsmEditorPane> _panes = new Dictionary<string, FsmEditorPane>(StringComparer.OrdinalIgnoreCase);
        private OutputWindowPane _output;

        internal static FsmEditorPackage Instance { get; private set; }

        /// <summary>Validation results of the open diagrams.</summary>
        internal Diagnostics Validation { get; private set; }

        /// <summary>Problems reported by the last code generation.</summary>
        internal Diagnostics CodeGeneration { get; private set; }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Instance = this;
            Validation = new Diagnostics(this, "FSM Editor", new Guid("b5c86f4e-5c2c-4b8a-9d38-2f4f5a6f1a01")) { Navigate = (file, id, line) => Reveal(file, id) };
            CodeGeneration = new Diagnostics(this, "FSM Code Generation", new Guid("b5c86f4e-5c2c-4b8a-9d38-2f4f5a6f1a02")) { Navigate = (file, id, line) => OpenAt(file, line) };
            RegisterEditorFactory(new FsmEditorFactory(this));
            await this.RegisterCommandsAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Validation?.Dispose();
                CodeGeneration?.Dispose();
                FsmWorkspace.Instance.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------------------------------------------------------------- open diagrams

        internal void RegisterPane(FsmEditorPane pane)
        {
            if (!string.IsNullOrEmpty(pane.FilePath)) _panes[FsmDocuments.SafeFullPath(pane.FilePath)] = pane;
        }

        internal void UnregisterPane(FsmEditorPane pane)
        {
            foreach (var key in _panes.Where(p => p.Value == pane).Select(p => p.Key).ToList()) _panes.Remove(key);
        }

        internal FsmEditorPane PaneFor(string path) =>
            path != null && _panes.TryGetValue(FsmDocuments.SafeFullPath(path), out var pane) ? pane : null;

        /// <summary>Opens the diagram and selects an element (from the Error List).</summary>
        private void Reveal(string file, string id)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            FsmDocuments.OpenInDiagram(file);
            PaneFor(file)?.Reveal(id ?? "");
        }

        private void OpenAt(string file, int line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!File.Exists(file)) return;
            if (FsmDocuments.IsFsm(file))
            {
                FsmDocuments.OpenInDiagram(file);
                return;
            }
            VsShellUtilities.OpenDocument(this, file, VSConstants.LOGVIEWID_Primary, out _, out _, out _, out var view);
            view?.SetCaretPos(Math.Max(0, line), 0);
            view?.CenterLines(Math.Max(0, line), 1);
        }

        // ---------------------------------------------------------------- new machine

        internal async Task NewStateMachineAsync(string folder)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var name = Dialogs.AskMachineName();
            if (name == null) return;
            if (folder == null)
            {
                folder = Dialogs.PickFolder("Create the state machine in", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                if (folder == null) return;
            }
            var path = Path.Combine(folder, name + ".fsm");
            if (File.Exists(path))
            {
                await VS.MessageBox.ShowErrorAsync("New State Machine", $"{name}.fsm already exists.");
                return;
            }
            File.WriteAllText(path, Xmi.ToXmi(FsmModel.CreateDefault(name)), new UTF8Encoding(false));
            // Add it to the project owning the folder (SDK-style projects pick it up on their own).
            try
            {
                var item = await VS.Solutions.GetActiveItemAsync();
                var project = item?.Type == SolutionItemType.Project ? item as Project : item?.FindParent(SolutionItemType.Project) as Project;
                if (project != null) await project.AddExistingFilesAsync(path);
            }
            catch (Exception e) when (e is COMException || e is InvalidOperationException || e is NotImplementedException)
            {
                // Folder views and some project types don't take new items.
            }
            FsmDocuments.OpenInDiagram(path);
        }

        // ---------------------------------------------------------------- SVG export

        internal async Task ExportSvgAsync(FsmEditorPane pane)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var svg = pane?.RenderSvg();
            if (svg == null)
            {
                await VS.MessageBox.ShowWarningAsync("Export as SVG", "Open a .fsm state machine in the FSM Editor first.");
                return;
            }
            var target = Dialogs.SaveFile("Export as SVG", "SVG image (*.svg)|*.svg", Path.GetDirectoryName(pane.FilePath),
                Path.GetFileNameWithoutExtension(pane.FilePath) + ".svg");
            if (target == null) return;
            try
            {
                File.WriteAllText(target, svg, new UTF8Encoding(false));
                await VS.StatusBar.ShowMessageAsync($"Exported to {Path.GetFileName(target)}.");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                await VS.MessageBox.ShowErrorAsync("SVG export failed", e.Message);
            }
        }

        // ---------------------------------------------------------------- code generation

        private WritableSettingsStore Settings()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var store = new ShellSettingsManager(this).GetWritableSettingsStore(SettingsScope.UserSettings);
            if (!store.CollectionExists(SettingsCollection)) store.CreateCollection(SettingsCollection);
            return store;
        }

        private static string Key(string prefix, string path)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
                return prefix + BitConverter.ToString(hash).Replace("-", "");
            }
        }

        /// <summary>Templates offered for a machine: the generator's own, then the *.hbs files of the solution.</summary>
        private static List<TemplateChoice> TemplateChoices(string cli, string root)
        {
            var choices = FsmCli.BundledTemplates(cli)
                .Select(f => new TemplateChoice { Path = f, Name = Path.GetFileNameWithoutExtension(f), Description = "bundled template" })
                .ToList();
            var found = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0 && found.Count < 50)
            {
                var dir = pending.Pop();
                try
                {
                    found.AddRange(Directory.EnumerateFiles(dir, "*.hbs").Take(50 - found.Count));
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        var name = Path.GetFileName(sub);
                        if (name != "node_modules" && name != "bin" && name != "obj" && !name.StartsWith(".", StringComparison.Ordinal)) pending.Push(sub);
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // skip
                }
            }
            choices.AddRange(found.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => new TemplateChoice { Path = f, Name = Path.GetFileNameWithoutExtension(f), Description = FsmWorkspace.Display(root, f) }));
            return choices;
        }

        private async System.Threading.Tasks.Task<OutputWindowPane> OutputAsync() =>
            _output ?? (_output = await VS.Windows.CreateOutputWindowPaneAsync("FSM Code Generation"));

        /// <summary>
        /// Generates code for <paramref name="source"/> with the <c>fsm</c> CLI.
        /// <paramref name="args"/> (from the Command Window: <c>-t &lt;template&gt; -o &lt;folder&gt;</c>) skip the dialog.
        /// </summary>
        internal async Task GenerateCodeAsync(string source, string args)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!FsmDocuments.IsFsm(source))
            {
                await VS.MessageBox.ShowWarningAsync("Generate Code", "Select or open a .fsm state machine to generate code from.");
                return;
            }
            source = FsmDocuments.SafeFullPath(source);
            var options = await General.GetLiveInstanceAsync();
            var cli = FsmCli.Locate(options.CliPath);
            if (cli == null)
            {
                await VS.MessageBox.ShowErrorAsync("Generate Code",
                    string.IsNullOrWhiteSpace(options.CliPath)
                        ? "The fsm code generator was not found. Set its path in Tools > Options > FSM Editor, or put fsm.exe on the PATH."
                        : $"The fsm code generator '{options.CliPath}' configured in Tools > Options > FSM Editor does not exist.");
                return;
            }
            var root = FsmWorkspace.Instance.RootFor(source);
            string ResolveArg(string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(root, p));

            var (argTemplate, argOut) = FsmCli.ParseCommandArgs(args);
            var settings = Settings();
            var outKey = Key("Out", source);
            var lastTemplate = settings.GetString(SettingsCollection, "Template", "");
            var lastOut = settings.GetString(SettingsCollection, outKey, "");
            string template, output;
            if (argTemplate != null && argOut != null)
            {
                // A bare name such as "ts" is a template of the generator, like `fsm -t ts`.
                template = argTemplate.IndexOfAny(new[] { '/', '\\', '.' }) < 0
                    ? FsmCli.BundledTemplates(cli).FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == argTemplate) ?? ResolveArg(argTemplate)
                    : ResolveArg(argTemplate);
                output = ResolveArg(argOut);
            }
            else
            {
                var chosen = GenerateCodeDialog.Show(Path.GetFileNameWithoutExtension(source), TemplateChoices(cli, root), lastTemplate,
                    lastOut.Length > 0 ? lastOut : Path.GetDirectoryName(source));
                if (chosen == null) return;
                (template, output) = chosen.Value;
            }
            settings.SetString(SettingsCollection, "Template", template);
            settings.SetString(SettingsCollection, outKey, output);

            if (options.SaveBeforeGenerating) FsmDocuments.SaveOpenMachines();
            var pane = await OutputAsync();
            var cliArgs = new[] { source, "--template", template, "--out", output };
            await pane.WriteLineAsync($"{DateTime.Now:T} {FsmCli.CommandLine(cli, cliArgs)}");
            await VS.StatusBar.ShowMessageAsync($"Generating code for {Path.GetFileName(source)}...");

            CliResult result;
            try
            {
                result = await FsmCli.RunAsync(cli, cliArgs, Path.GetDirectoryName(source));
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is IOException || e is InvalidOperationException)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                await pane.WriteLineAsync($"  {e.Message}");
                await VS.MessageBox.ShowErrorAsync("Code generation failed", $"Could not run {cli}: {e.Message}");
                return;
            }
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            foreach (var line in result.Output.Concat(result.Errors)) await pane.WriteLineAsync("  " + line);

            CodeGeneration.Clear();
            foreach (var group in result.Issues.GroupBy(i => i.File, StringComparer.OrdinalIgnoreCase))
            {
                CodeGeneration.Replace(group.Key, group.Select(i => ("", Math.Max(0, i.Line - 1), i.Severity, i.Message)));
            }

            var where = FsmWorkspace.Display(root, output);
            if (result.ExitCode == 0)
            {
                var n = result.Written.Count;
                await pane.WriteLineAsync($"  {n} file{(n == 1 ? "" : "s")} written in {where}");
                await VS.StatusBar.ShowMessageAsync($"Generated {n} file{(n == 1 ? "" : "s")} in {where}.");
                if (result.Issues.Count > 0) CodeGeneration.Show();
                return;
            }
            await VS.StatusBar.ShowMessageAsync("Code generation failed.");
            await pane.ActivateAsync();
            if (result.Issues.Count > 0) CodeGeneration.Show();
            var reason = result.ExitCode == 2 ? "The generator was called with bad arguments."
                : result.Issues.Any(i => i.Severity == Severity.Error) ? "The state machine has errors (see the Error List)."
                : result.Errors.LastOrDefault() ?? $"fsm exited with code {result.ExitCode}.";
            await VS.MessageBox.ShowErrorAsync("Code generation failed", reason + " Details are in the FSM Code Generation output.");
        }
    }
}
