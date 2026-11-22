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
using FsmEditor.TracePlayer;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using Task = System.Threading.Tasks.Task;

namespace FsmEditor
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("#111", "#112", "0.8.2")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuids.PackageString)]
    [ProvideEditorFactory(typeof(FsmEditorFactory), 110, CommonPhysicalViewAttributes = (int)__VSPHYSICALVIEWATTRIBUTES.PVA_SupportsPreview, TrustLevel = __VSEDITORTRUSTLEVEL.ETL_AlwaysTrusted)]
    [ProvideEditorExtension(typeof(FsmEditorFactory), ".fsm", 50, NameResourceID = 110)]
    [ProvideEditorLogicalView(typeof(FsmEditorFactory), VSConstants.LOGVIEWID.Designer_string)]
    // Layout files (X.fsm.layout) as JSON in the text editor, whatever editor the installation gives .layout files.
    [ProvideEditorFactory(typeof(LayoutEditorFactory), 113, TrustLevel = __VSEDITORTRUSTLEVEL.ETL_AlwaysTrusted)]
    [ProvideEditorExtension(typeof(LayoutEditorFactory), ".layout", 100, NameResourceID = 113)]
    [ProvideEditorLogicalView(typeof(LayoutEditorFactory), VSConstants.LOGVIEWID.TextView_string)]
    [ProvideUIContextRule(PackageGuids.FsmFileSelectedRuleString, "FSM file selected", "Fsm", new[] { "Fsm" }, new[] { "HierSingleSelectionName:.fsm$" })]
    [ProvideAutoLoad(PackageGuids.FsmFileSelectedRuleString, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.FolderOpened_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideOptionPage(typeof(OptionsProvider.GeneralOptions), "FSM Editor", "General", 0, 0, true, SupportsProfiles = true)]
    [ProvideToolWindow(typeof(TracePlayerWindow.Pane), Style = VsDockStyle.Tabbed, Window = WindowGuids.OutputWindow)]
    public sealed class FsmEditorPackage : ToolkitPackage
    {
        private const string SettingsCollection = @"FsmEditor\CodeGeneration";
        private readonly Dictionary<string, FsmEditorPane> _panes = new Dictionary<string, FsmEditorPane>(StringComparer.OrdinalIgnoreCase);
        private OutputWindowPane _output;
        private readonly LayoutRenames _layoutRenames = new LayoutRenames();

        internal static FsmEditorPackage Instance { get; private set; }

        /// <summary>Validation results of the open diagrams.</summary>
        internal Diagnostics Validation { get; private set; }

        /// <summary>Problems reported by the last code generation.</summary>
        internal Diagnostics CodeGeneration { get; private set; }

        /// <summary>The diagram diffs and merges.</summary>
        internal DiagramSessions Sessions { get; private set; }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Instance = this;
            // fsm-editor-common's sidecar, started on first use: reading, writing, converting, validating, comparing,
            // merging and code generation go through it.
            Core.Common.ExecutablePath = CodeGenerator.CommonPath;
            Sessions = new DiagramSessions(this);
            CompareFiles.CleanUp();
            // Advised for the life of Visual Studio, as the package is.
            _layoutRenames.Advise();
            Validation = new Diagnostics(this, "FSM Editor", new Guid("b5c86f4e-5c2c-4b8a-9d38-2f4f5a6f1a01")) { Navigate = (file, id, line) => Reveal(file, id) };
            CodeGeneration = new Diagnostics(this, "FSM Code Generation", new Guid("b5c86f4e-5c2c-4b8a-9d38-2f4f5a6f1a02")) { Navigate = (file, id, line) => OpenAt(file, line) };
            RegisterEditorFactory(new FsmEditorFactory(this));
            RegisterEditorFactory(new LayoutEditorFactory());
            await this.RegisterCommandsAsync();
            this.RegisterToolWindows();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Validation?.Dispose();
                CodeGeneration?.Dispose();
                FsmWorkspace.Instance.Dispose();
                Core.Common.Shutdown();
            }
            base.Dispose(disposing);
        }

        // ---------------------------------------------------------------- open diagrams

        internal void RegisterPane(FsmEditorPane pane)
        {
            if (string.IsNullOrEmpty(pane.FilePath)) return;
            _panes[FsmDocuments.SafeFullPath(pane.FilePath)] = pane;
            pane.ShowTrace(TracePlayerService.Instance.ViewFor(FsmDocuments.SafeFullPath(pane.FilePath)));
        }

        /// <summary>Shows the Trace Player's highlights in the open diagrams (<paramref name="viewFor"/> by full path; null: none).</summary>
        internal void ShowTrace(Func<string, TraceView> viewFor)
        {
            foreach (var kv in _panes.ToList()) kv.Value.ShowTrace(viewFor(kv.Key));
        }

        internal void UnregisterPane(FsmEditorPane pane)
        {
            foreach (var key in _panes.Where(p => p.Value == pane).Select(p => p.Key).ToList()) _panes.Remove(key);
        }

        /// <summary>The document of a machine open in a diagram, or null.</summary>
        internal FsmDocument DocumentFor(string path) => PaneFor(path)?.Document;

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
            try
            {
                // Its two files: the model, then its layout.
                var files = MachineFiles.Write(FsmModel.CreateDefault(name));
                File.WriteAllText(path, files.ModelText, new UTF8Encoding(false));
                File.WriteAllText(MachineFiles.LayoutPathOf(path), files.LayoutText, new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                await VS.MessageBox.ShowErrorAsync("New State Machine", e.Message);
                return;
            }
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

        // ---------------------------------------------------------------- SVG and PNG export

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

        internal async Task ExportPngAsync(FsmEditorPane pane)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var image = pane?.RenderPng();
            if (image == null)
            {
                await VS.MessageBox.ShowWarningAsync("Export as PNG", "Open a .fsm state machine in the FSM Editor first.");
                return;
            }
            var target = Dialogs.SaveFile("Export as PNG", "PNG image (*.png)|*.png", Path.GetDirectoryName(pane.FilePath),
                Path.GetFileNameWithoutExtension(pane.FilePath) + ".png");
            if (target == null) return;
            try
            {
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                using (var stream = File.Create(target)) encoder.Save(stream);
                await VS.StatusBar.ShowMessageAsync($"Exported to {Path.GetFileName(target)}.");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                await VS.MessageBox.ShowErrorAsync("PNG export failed", e.Message);
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

        /// <summary>Templates offered for a machine: the bundled ones, then the *.jinja files of the solution.</summary>
        private static List<TemplateChoice> TemplateChoices(string root)
        {
            var choices = CodeGenerator.BundledTemplates()
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
                    found.AddRange(Directory.EnumerateFiles(dir, "*.jinja").Take(50 - found.Count));
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
        /// Generates code for <paramref name="source"/> (fsm-cli's generator, run by fsm-editor-common).
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
            var root = FsmWorkspace.Instance.RootFor(source);
            string ResolveArg(string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(root, p));

            var (argTemplate, argOut) = CodeGenerator.ParseCommandArgs(args);
            var settings = Settings();
            var outKey = Key("Out", source);
            var lastTemplate = settings.GetString(SettingsCollection, "Template", "");
            var lastOut = settings.GetString(SettingsCollection, outKey, "");
            string template, output;
            if (argTemplate != null && argOut != null)
            {
                // A bare name is a bundled template: "ts", like `fsm write -t ts`, and "ts.jinja"
                // unless the solution folder has that file.
                string bundled = null;
                if (argTemplate.IndexOfAny(new[] { '/', '\\', '.' }) < 0)
                    bundled = CodeGenerator.BundledTemplates().FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == argTemplate);
                else if (argTemplate.IndexOfAny(new[] { '/', '\\' }) < 0 && !File.Exists(ResolveArg(argTemplate)))
                    bundled = CodeGenerator.BundledTemplates().FirstOrDefault(f => string.Equals(Path.GetFileName(f), argTemplate, StringComparison.OrdinalIgnoreCase));
                template = bundled ?? ResolveArg(argTemplate);
                output = ResolveArg(argOut);
            }
            else
            {
                var chosen = GenerateCodeDialog.Show(Path.GetFileNameWithoutExtension(source), TemplateChoices(root), lastTemplate,
                    lastOut.Length > 0 ? lastOut : Path.GetDirectoryName(source));
                if (chosen == null) return;
                (template, output) = chosen.Value;
            }
            settings.SetString(SettingsCollection, "Template", template);
            settings.SetString(SettingsCollection, outKey, output);

            if (options.SaveBeforeGenerating) FsmDocuments.SaveOpenMachines();
            var pane = await OutputAsync();
            await pane.WriteLineAsync($"{DateTime.Now:T} {FsmWorkspace.Display(root, source)} with {Path.GetFileName(template)} into {FsmWorkspace.Display(root, output)}");
            await VS.StatusBar.ShowMessageAsync($"Generating code for {Path.GetFileName(source)}...");

            GenerateResult result;
            try
            {
                result = await System.Threading.Tasks.Task.Run(() => CodeGenerator.Generate(source, template, output));
            }
            catch (Exception e) when (e is CommonException || e is IOException || e is UnauthorizedAccessException)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                await pane.WriteLineAsync($"  {e.Message}");
                await VS.MessageBox.ShowErrorAsync("Code generation failed", e.Message);
                return;
            }
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            foreach (var i in result.Issues) await pane.WriteLineAsync($"  {FsmWorkspace.Display(root, i.File)}:{i.Line}: {i.Severity.ToString().ToLowerInvariant()}: {i.Message}");

            CodeGeneration.Clear();
            foreach (var group in result.Issues.GroupBy(i => i.File, StringComparer.OrdinalIgnoreCase))
            {
                CodeGeneration.Replace(group.Key, group.Select(i => ("", Math.Max(0, i.Line - 1), i.Severity, i.Message)));
            }

            var where = FsmWorkspace.Display(root, output);
            if (result.ExitCode == 0)
            {
                var n = result.Written.Count;
                foreach (var f in result.Written) await pane.WriteLineAsync($"  wrote {FsmWorkspace.Display(root, f)}");
                await pane.WriteLineAsync($"  {n} file{(n == 1 ? "" : "s")} written in {where}");
                await VS.StatusBar.ShowMessageAsync($"Generated {n} file{(n == 1 ? "" : "s")} in {where}.");
                if (result.Issues.Count > 0) CodeGeneration.Show();
                return;
            }
            await VS.StatusBar.ShowMessageAsync("Code generation failed.");
            await pane.ActivateAsync();
            if (result.Issues.Count > 0) CodeGeneration.Show();
            if (result.Error != null) await pane.WriteLineAsync($"  {result.Error}");
            // The generator's own message says why, also for a request it refuses, such as a template.
            var reason = result.Error ?? (result.Issues.Any(i => i.Severity == Severity.Error) ? "The state machine has errors (see the Error List)." : $"The generator stopped with exit code {result.ExitCode}.");
            await VS.MessageBox.ShowErrorAsync("Code generation failed", reason + " Details are in the FSM Code Generation output.");
        }
    }
}
