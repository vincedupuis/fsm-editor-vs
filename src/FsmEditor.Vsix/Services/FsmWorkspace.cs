using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FsmEditor.Core;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;

namespace FsmEditor.Services
{
    /// <summary>
    /// The state machines of the solution: lists the *.fsm files submachine
    /// states can reference, reads their names and connection points (preferring
    /// unsaved text of open documents), and re-syncs every open diagram when any
    /// of them changes.
    /// </summary>
    internal sealed class FsmWorkspace : IDisposable
    {
        private const int MaxFiles = 500;
        private static readonly HashSet<string> SkippedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "node_modules", "packages", ".git", ".vs", ".vscode", "dist",
        };

        private readonly ConcurrentDictionary<string, (string Key, MachineSummary Summary)> _summaries =
            new ConcurrentDictionary<string, (string, MachineSummary)>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Action> _resyncs = new List<Action>();
        private readonly Dictionary<string, FileSystemWatcher> _watchers = new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);

        public static FsmWorkspace Instance { get; } = new FsmWorkspace();

        /// <summary>Registers a callback run (on the UI thread) whenever a state machine file changes.</summary>
        public IDisposable OnAnyMachineChanged(Action resync)
        {
            lock (_resyncs) _resyncs.Add(resync);
            return new Unsubscriber(() =>
            {
                lock (_resyncs) _resyncs.Remove(resync);
            });
        }

        public void ResyncAll()
        {
            Action[] all;
            lock (_resyncs) all = _resyncs.ToArray();
            foreach (var r in all) r();
        }

        private sealed class Unsubscriber : IDisposable
        {
            private Action _dispose;

            public Unsubscriber(Action dispose) => _dispose = dispose;

            public void Dispose()
            {
                _dispose?.Invoke();
                _dispose = null;
            }
        }

        /// <summary>Folder whose *.fsm files a document can use: the solution folder when it contains the document, else the document's folder.</summary>
        public string RootFor(string documentPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var docDir = Path.GetDirectoryName(documentPath);
            if (Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution solution &&
                solution.GetSolutionInfo(out var solutionDir, out _, out _) == 0 &&
                !string.IsNullOrEmpty(solutionDir) &&
                documentPath.StartsWith(Path.GetFullPath(solutionDir), StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(solutionDir).TrimEnd('\\', '/');
            }
            return docDir;
        }

        /// <summary>Path shown to the user: relative to the solution folder when possible.</summary>
        public static string Display(string root, string path)
        {
            var full = Path.GetFullPath(path);
            var prefix = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full.Substring(prefix.Length).Replace('\\', '/') : full;
        }

        /// <summary>Snapshots of the .fsm documents open in Visual Studio (they may have unsaved changes). UI thread.</summary>
        public Dictionary<string, ITextSnapshot> OpenSnapshots()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var output = new Dictionary<string, ITextSnapshot>(StringComparer.OrdinalIgnoreCase);
            var adapters = (Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel)?.GetService<IVsEditorAdaptersFactoryService>();
            if (adapters == null) return output;
            foreach (var info in new RunningDocumentTable(ServiceProvider.GlobalProvider))
            {
                if (info.Moniker == null || !info.Moniker.EndsWith(".fsm", StringComparison.OrdinalIgnoreCase)) continue;
                if (info.DocData is IVsTextBuffer vsBuffer && adapters.GetDocumentBuffer(vsBuffer) is ITextBuffer buffer)
                {
                    output[Path.GetFullPath(info.Moniker)] = buffer.CurrentSnapshot;
                }
            }
            return output;
        }

        /// <summary>The *.fsm files under <paramref name="root"/> (skipping build output and tool folders).</summary>
        public List<string> FindMachineFiles(string root)
        {
            var output = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0 && output.Count < MaxFiles)
            {
                var dir = pending.Pop();
                try
                {
                    output.AddRange(Directory.EnumerateFiles(dir, "*.fsm").Take(MaxFiles - output.Count));
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        var name = Path.GetFileName(sub);
                        if (!SkippedFolders.Contains(name) && !name.StartsWith(".", StringComparison.Ordinal)) pending.Push(sub);
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // unreadable folder
                }
            }
            output.Sort(StringComparer.OrdinalIgnoreCase);
            return output;
        }

        /// <summary>Summary of a machine file, cached by document version or modification time. Thread-safe.</summary>
        public MachineSummary SummaryOf(string path, IReadOnlyDictionary<string, ITextSnapshot> open)
        {
            var full = Path.GetFullPath(path);
            string key;
            ITextSnapshot snapshot = null;
            if (open.TryGetValue(full, out snapshot))
            {
                key = $"v{snapshot.TextBuffer.GetHashCode()}:{snapshot.Version.VersionNumber}";
            }
            else
            {
                try
                {
                    if (!File.Exists(full)) return null;
                    key = $"m{File.GetLastWriteTimeUtc(full).Ticks}";
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    return null;
                }
            }
            if (_summaries.TryGetValue(full, out var cached) && cached.Key == key) return cached.Summary;
            MachineSummary summary = null;
            try
            {
                summary = MachineSummary.TryRead(snapshot != null ? snapshot.GetText() : File.ReadAllText(full));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                summary = null;
            }
            _summaries[full] = (key, summary);
            return summary;
        }

        /// <summary>Everything validation needs to know about the other machines. Runs on a background thread.</summary>
        public Task<(List<MachineInfo> Machines, Dictionary<string, SubmachineInfo> Submachines)> ResolveAsync(
            string documentPath, string root, FsmModel model, IReadOnlyDictionary<string, ITextSnapshot> open)
        {
            return Task.Run(() =>
            {
                MachineSummary Summary(string p) => SummaryOf(p, open);
                string Show(string p) => Display(root, p);
                var machines = SubmachineResolver.ListMachines(documentPath, FindMachineFiles(root), Summary, Show);
                var submachines = SubmachineResolver.Resolve(documentPath, model, machines, Summary, Show);
                return (machines, submachines);
            });
        }

        /// <summary>Watches *.fsm files under <paramref name="root"/> so diagrams follow changes made outside them.</summary>
        public void Watch(string root)
        {
            lock (_watchers)
            {
                if (_watchers.ContainsKey(root) || !Directory.Exists(root)) return;
                try
                {
                    var w = new FileSystemWatcher(root, "*.fsm") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
                    // The watcher raises events on a thread pool thread; the package's task factory
                    // tracks the switch to the UI thread so it doesn't outlive Visual Studio's shutdown.
                    void Changed(object s, FileSystemEventArgs e)
                    {
                        var jtf = FsmEditorPackage.Instance?.JoinableTaskFactory;
                        jtf?.RunAsync(async () =>
                        {
                            await jtf.SwitchToMainThreadAsync();
                            ResyncAll();
                        }).FileAndForget("FsmEditor/Watch");
                    }
                    w.Changed += Changed;
                    w.Created += Changed;
                    w.Deleted += Changed;
                    w.Renamed += (s, e) => Changed(s, e);
                    w.EnableRaisingEvents = true;
                    _watchers[root] = w;
                }
                catch (Exception e) when (e is IOException || e is ArgumentException || e is PlatformNotSupportedException)
                {
                    // not watchable (network share limits...): changes are picked up on the next edit
                }
            }
        }

        public void Dispose()
        {
            lock (_watchers)
            {
                foreach (var w in _watchers.Values) w.Dispose();
                _watchers.Clear();
            }
        }
    }
}
