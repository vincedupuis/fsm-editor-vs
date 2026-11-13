using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using Community.VisualStudio.Toolkit;
using FsmEditor.Core;
using FsmEditor.Services;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

// DispatcherTimer ticks run on the UI thread.
#pragma warning disable VSTHRD001, VSTHRD110

namespace FsmEditor.TracePlayer
{
    /// <summary>
    /// The Trace Player, as src/trace/player.ts of the VS Code edition: the <see cref="TraceSession"/> of the open logs,
    /// each followed by a file watcher as it grows; the machines of the solution and the open documents for it to
    /// resolve records; playing; the highlights it hands to the open diagrams; and the questions it asks (which machine
    /// the lines of a log naming none are about). UI thread only.
    /// </summary>
    internal sealed class TracePlayerService
    {
        public static TracePlayerService Instance { get; } = new TracePlayerService();

        private readonly Dictionary<TraceLog, FileSystemWatcher> _watchers = new Dictionary<TraceLog, FileSystemWatcher>();
        private readonly DispatcherTimer _play = new DispatcherTimer();
        private readonly DispatcherTimer _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        /// <summary>Parsed machine files, with the version they were read from.</summary>
        private readonly Dictionary<string, (string Key, TraceMachine Machine)> _parsed = new Dictionary<string, (string, TraceMachine)>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, TraceView> _views = new Dictionary<string, TraceView>(StringComparer.OrdinalIgnoreCase);
        private bool _machinesDirty = true;
        private bool _asking;
        private readonly Queue<TraceLog> _toAsk = new Queue<TraceLog>();
        private IDisposable _machineChanges;

        private TracePlayerService()
        {
            _play.Tick += (s, e) =>
            {
                _play.Stop();
                Session.Advance();
                Update();
                Schedule();
            };
            _refresh.Tick += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _refresh.Stop();
                Refresh();
            };
            General.Saved += options =>
            {
                Session.SetFormats(options.TraceFormats);
                Changed?.Invoke();
            };
        }

        public TraceSession Session { get; } = new TraceSession();

        /// <summary>Raised (on the UI thread) when the window should show the session again.</summary>
        public event Action Changed;

        /// <summary>The highlights of a machine file at the cursor, or null.</summary>
        public TraceView ViewFor(string file) => file != null && _views.TryGetValue(file, out var v) ? v : null;

        // ---------------------------------------------------------------- logs

        /// <summary>"Open Trace…": asks for log files (unless given), opens them and shows the window.</summary>
        public async Task OpenAsync(IEnumerable<string> files = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var list = files?.ToList();
            if (list == null)
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Open Trace: log files", Filter = "Logs (*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*", Multiselect = true, CheckFileExists = true };
                if (dialog.ShowDialog() != true) return;
                list = dialog.FileNames.ToList();
            }
            if (list.Count == 0) return;
            _machineChanges = _machineChanges ?? FsmWorkspace.Instance.OnAnyMachineChanged(MachinesChanged);
            Session.SetFormats(General.Instance.TraceFormats);
            var added = list.Select(f => Session.Open(f)).Where(t => t != null).ToList();
            await TracePlayerWindow.ShowAsync();
            if (added.Count == 0) return;
            foreach (var trace in added) Watch(trace);
            Refresh();
            foreach (var trace in added) Read(trace);
            Update();
        }

        private void Watch(TraceLog trace)
        {
            try
            {
                var w = new FileSystemWatcher(Path.GetDirectoryName(trace.Path), Path.GetFileName(trace.Path)) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                // The watcher raises events on a thread pool thread; the package's task factory tracks the switch.
                void Changed(object s, FileSystemEventArgs e)
                {
                    var jtf = FsmEditorPackage.Instance?.JoinableTaskFactory;
                    jtf?.RunAsync(async () =>
                    {
                        await jtf.SwitchToMainThreadAsync();
                        if (!Session.Traces.Contains(trace)) return;
                        if (e.ChangeType == WatcherChangeTypes.Deleted) Session.Missing(trace);
                        else Read(trace);
                        Update();
                    }).FileAndForget("FsmEditor/TraceWatch");
                }
                w.Changed += Changed;
                w.Created += Changed;
                w.Deleted += Changed;
                w.Renamed += (s, e) => Changed(s, e);
                w.EnableRaisingEvents = true;
                _watchers[trace] = w;
            }
            catch (Exception e) when (e is IOException || e is ArgumentException || e is PlatformNotSupportedException)
            {
                // Not watchable: read once.
            }
        }

        /// <summary>Reads what was added to a log, then asks for its machine when its lines name none.</summary>
        private void Read(TraceLog trace)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Session.ReadNew(trace);
            if (Session.ShouldAsk(trace)) AskMachine(trace);
        }

        public void Close(TraceLog trace)
        {
            if (_watchers.TryGetValue(trace, out var w))
            {
                w.Dispose();
                _watchers.Remove(trace);
            }
            Session.Close(trace);
            if (Session.Traces.Count == 0) _play.Stop();
            Update();
        }

        /// <summary>Reads a log again from the start with another format.</summary>
        public void SetFormat(TraceLog trace, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!Session.SetFormat(trace, name)) return;
            Read(trace);
            Session.Rebuild();
            Update();
        }

        /// <summary>Asks which machine the lines of <paramref name="trace"/> naming none are about (one question at a time).</summary>
        public void AskMachine(TraceLog trace)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _toAsk.Enqueue(trace);
            if (_asking) return;
            _asking = true;
            try
            {
                while (_toAsk.Count > 0)
                {
                    var t = _toAsk.Dequeue();
                    if (!Session.Traces.Contains(t)) continue;
                    if (_machinesDirty) Refresh();
                    var open = FsmWorkspace.Instance.OpenSnapshots();
                    var machines = Session.Machines
                        .OrderByDescending(m => open.ContainsKey(m.File))
                        .ThenBy(m => m.Model.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
                    if (machines.Count == 0)
                    {
                        VS.MessageBox.ShowWarning("Trace Player", $"{t.Name} has lines without a state machine, and the solution has none.");
                        continue;
                    }
                    var root = Roots(open).FirstOrDefault() ?? "";
                    var pick = MachinePicker.Show($"State machine of {t.Name}", "Its lines name no state machine: which one are they about?",
                        machines.Select(m => (m.Model.Name, root.Length > 0 ? FsmWorkspace.Display(root, m.File) : m.File, open.ContainsKey(m.File))).ToList(),
                        machines.FindIndex(m => string.Equals(m.File, t.MachineFile, StringComparison.OrdinalIgnoreCase)));
                    if (pick < 0 || !Session.Traces.Contains(t)) continue;
                    Session.ChooseMachine(t, machines[pick].File);
                    Update();
                }
            }
            finally
            {
                _asking = false;
            }
        }

        // ---------------------------------------------------------------- machines

        private void MachinesChanged()
        {
            _machinesDirty = true;
            if (Session.Traces.Count == 0) return;
            _refresh.Stop();
            _refresh.Start();
        }

        /// <summary>Reloads the machine files when they changed, then rebuilds the timeline.</summary>
        private void Refresh()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_machinesDirty)
            {
                _machinesDirty = false;
                LoadMachines();
            }
            Session.Rebuild();
            Update();
        }

        /// <summary>Folders whose machines records can be about: the solution's, else those of the open documents and logs.</summary>
        private IEnumerable<string> Roots(IReadOnlyDictionary<string, Microsoft.VisualStudio.Text.ITextSnapshot> open)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var roots = new List<string>();
            if (Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution solution &&
                solution.GetSolutionInfo(out var dir, out _, out _) == 0 && !string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                roots.Add(Path.GetFullPath(dir).TrimEnd('\\', '/'));
            }
            foreach (var d in open.Keys.Concat(Session.Traces.Select(t => t.Path)).Select(Path.GetDirectoryName))
            {
                if (!roots.Any(r => (d + "\\").StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))) roots.Add(d);
            }
            return roots;
        }

        /// <summary>The machines of the solution and the open ones (with their unsaved changes), the open ones first.</summary>
        private void LoadMachines()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var open = FsmWorkspace.Instance.OpenSnapshots();
            var files = new List<string>(open.Keys);
            foreach (var root in Roots(open))
            {
                foreach (var f in FsmWorkspace.Instance.FindMachineFiles(root))
                {
                    if (!files.Contains(f, StringComparer.OrdinalIgnoreCase)) files.Add(Path.GetFullPath(f));
                }
            }
            Session.SetMachines(files.Select(f => ReadMachine(f, open)).Where(m => m != null).ToList(), f => ReadMachine(f, open));
        }

        private TraceMachine ReadMachine(string file, IReadOnlyDictionary<string, Microsoft.VisualStudio.Text.ITextSnapshot> open)
        {
            try
            {
                string key;
                Func<string> text;
                if (open.TryGetValue(file, out var snapshot))
                {
                    key = $"v{snapshot.TextBuffer.GetHashCode()}:{snapshot.Version.VersionNumber}";
                    text = snapshot.GetText;
                }
                else
                {
                    if (!File.Exists(file)) return null;
                    key = $"m{File.GetLastWriteTimeUtc(file).Ticks}";
                    text = () => File.ReadAllText(file);
                }
                if (_parsed.TryGetValue(file, out var cached) && cached.Key == key) return cached.Machine;
                var machine = TraceMachine.Of(file, Xmi.FromXmi(text()));
                _parsed[file] = (key, machine);
                return machine;
            }
            catch (Exception e) when (e is XmlParseException || e is XmiException || e is IOException || e is UnauthorizedAccessException || e is FormatException || e is System.Xml.XmlException || e is InvalidOperationException)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- cursor

        public void Seek(int index)
        {
            Session.Seek(index);
            Update();
        }

        public void Step(int delta)
        {
            Session.Step(delta);
            Update();
        }

        public void First()
        {
            Session.First();
            Update();
        }

        public void Play()
        {
            Session.Play();
            Update();
            Schedule();
        }

        public void Pause()
        {
            Session.Pause();
            _play.Stop();
            Update();
        }

        public void GoLive()
        {
            _play.Stop();
            Session.GoLive();
            Update();
        }

        public void SetSpeed(double speed)
        {
            if (speed > 0) Session.Speed = speed;
            Changed?.Invoke();
        }

        private void Schedule()
        {
            _play.Stop();
            var delay = Session.NextDelay();
            if (delay == null)
            {
                Changed?.Invoke();
                return;
            }
            _play.Interval = TimeSpan.FromMilliseconds(Math.Min(delay.Value, int.MaxValue));
            _play.Start();
        }

        /// <summary>Highlights at the cursor for the diagrams, and the window.</summary>
        private void Update()
        {
            if (!Session.Playing) _play.Stop();
            var before = _views;
            _views = Session.Views();
            if (before.Count > 0 || _views.Count > 0) FsmEditorPackage.Instance?.ShowTrace(ViewFor);
            Changed?.Invoke();
        }
    }
}
