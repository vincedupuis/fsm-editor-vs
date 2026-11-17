using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FsmEditor.Core;
using FsmEditor.Services;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace FsmEditor.Editor
{
    /// <summary>
    /// The read-only files a diagram diff or merge opens: a version of a machine (Git's), or a side of a merge, written
    /// under <c>%LOCALAPPDATA%\FsmEditor\Compare\&lt;session&gt;\&lt;label&gt;\</c>, with the path the file has
    /// in its repository (its submachines beside it, so that its references resolve). A document there opens read-only,
    /// its tab captioned with the label.
    /// </summary>
    internal static class CompareFiles
    {
        private static int _seq;

        /// <summary>
        /// Under the local application data rather than the temporary folder, whose path Windows may give in its short
        /// (8.3) form while documents are known by their long one.
        /// </summary>
        public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FsmEditor", "Compare");

        public static bool IsReadOnlySide(string path)
        {
            try
            {
                return path != null && Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
            {
                return false;
            }
        }

        /// <summary>A new folder for a session's files (the process id keeps two instances of Visual Studio apart).</summary>
        public static string NewSession(string prefix) =>
            Path.Combine(Root, $"{prefix}{System.Diagnostics.Process.GetCurrentProcess().Id}-{++_seq}");

        /// <summary>The label of a side's folder (<c>HEAD</c>, <c>Index</c>, <c>Incoming</c>…), or null.</summary>
        public static string LabelOf(string path)
        {
            if (!IsReadOnlySide(path)) return null;
            var parts = Path.GetFullPath(path).Substring(Root.Length + 1).Split(Path.DirectorySeparatorChar);
            return parts.Length > 2 ? parts[1] : null;
        }

        /// <summary>The session folder of a side's file.</summary>
        public static string SessionOf(string path)
        {
            if (!IsReadOnlySide(path)) return null;
            var parts = Path.GetFullPath(path).Substring(Root.Length + 1).Split(Path.DirectorySeparatorChar);
            return Path.Combine(Root, parts[0]);
        }

        public static void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));
            File.SetAttributes(path, FileAttributes.ReadOnly);
        }

        /// <summary>Deletes the folders left by instances of Visual Studio that ended without closing their sessions.</summary>
        public static void CleanUp()
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                foreach (var dir in Directory.EnumerateDirectories(Root))
                {
                    var name = Path.GetFileName(dir);
                    var digits = new string(name.SkipWhile(char.IsLetter).TakeWhile(char.IsDigit).ToArray());
                    if (!int.TryParse(digits, out var pid) || Running(pid)) continue;
                    Drop(dir);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // next time
            }
        }

        private static bool Running(int pid)
        {
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        /// <summary>Deletes a session's folder (best effort: a file still open stays until the next one).</summary>
        public static void Drop(string session)
        {
            try
            {
                if (!Directory.Exists(session)) return;
                foreach (var f in Directory.EnumerateFiles(session, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(session, true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // in use
            }
        }
    }

    /// <summary>
    /// Links the diagram panes of diagram diffs (two panes: a version of a file and the file) and merges (three: a file
    /// with merge conflicts, the result, beside its two sides). The logic is Core's <see cref="DiffSession"/> and
    /// <see cref="MergeSession"/>; this opens the panes, passes their messages and closes them.
    /// </summary>
    internal sealed class DiagramSessions
    {
        private readonly FsmEditorPackage _package;
        /// <summary>The diffs asked for, by the paths of their two sides, until both are open.</summary>
        private readonly List<(string Older, string Newer, string OlderTitle, string NewerTitle, DateTime Asked)> _wanted = new List<(string, string, string, string, DateTime)>();
        /// <summary>The merges, by the session folder of their sides.</summary>
        private readonly Dictionary<string, (MergeSession Session, FsmDocument Document)> _merges = new Dictionary<string, (MergeSession, FsmDocument)>(StringComparer.OrdinalIgnoreCase);
        private readonly List<FsmEditorPane> _panes = new List<FsmEditorPane>();

        public DiagramSessions(FsmEditorPackage package) => _package = package;

        private static bool Same(string a, string b) => string.Equals(FsmDocuments.SafeFullPath(a), FsmDocuments.SafeFullPath(b), StringComparison.OrdinalIgnoreCase);

        // ---------------------------------------------------------------- panes

        /// <summary>A diagram opened: a side of a merge joins it; a diff asked for whose two sides are open starts.</summary>
        public void Opened(FsmEditorPane pane)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _panes.Add(pane);
            var session = CompareFiles.SessionOf(pane.FilePath);
            if (session != null && _merges.TryGetValue(session, out var merge))
            {
                var label = CompareFiles.LabelOf(pane.FilePath);
                pane.Session = merge.Session;
                merge.Session.Attach(pane, label == "Incoming" ? "incoming" : "current");
                return;
            }
            Pair();
        }

        public void Closed(FsmEditorPane pane)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _panes.Remove(pane);
            switch (pane.Session)
            {
                case DiffSession diff:
                    diff.Remove(pane);
                    if (diff.Other(pane) is FsmEditorPane other) other.Session = null;
                    break;
                case MergeSession merge:
                    merge.Remove(pane);
                    // The result closed: the merge ends (its sides close); the file keeps its conflicts until saved.
                    if (merge.Results.Count == 0 && _merges.TryGetValue(SessionFolder(merge), out var m) && m.Session == merge) EndMerge(merge);
                    break;
            }
            pane.Session = null;
            // A version's files go with its pane.
            var folder = CompareFiles.SessionOf(pane.FilePath);
            if (folder != null && !_merges.ContainsKey(folder) && !_panes.Any(p => CompareFiles.SessionOf(p.FilePath) == folder)) CompareFiles.Drop(folder);
        }

        private static string SessionFolder(MergeSession merge) => merge.Id;

        // ---------------------------------------------------------------- diff

        /// <summary>
        /// Compares a machine file with its version in Git, as diagrams: the file's changes not staged yet (the index's
        /// version, on the left) when it has some, else its changes since the last commit (<c>HEAD</c>'s version).
        /// </summary>
        public void CompareAsDiagrams(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var name = Path.GetFileName(path);
            var text = File.Exists(path) ? File.ReadAllText(path) : null;
            if (text != null && Core.Merge.HasConflictMarkers(text))
            {
                // A file with merge conflicts: its diagram is the merge.
                FsmDocuments.OpenInDiagram(path);
                return;
            }
            var root = Git.RootOf(path);
            if (root == null)
            {
                Message($"{name} is not in a Git repository: there is no earlier version to compare it with.");
                return;
            }
            var label = "Index";
            var older = Git.Show(path, "");
            var working = text ?? "";
            if (older != null && Normalize(older) == Normalize(working))
            {
                label = "HEAD";
                older = Git.Show(path, "HEAD");
            }
            if (older == null)
            {
                Message($"{name} is untracked: Git has no earlier version to compare it with.");
                return;
            }
            var rev = label == "HEAD" ? "HEAD" : "";
            var folder = CompareFiles.NewSession("diff");
            var relative = FsmDocuments.SafeFullPath(path).Substring(root.Length).TrimStart('\\', '/');
            var olderPath = Path.Combine(folder, label, relative);
            try
            {
                CompareFiles.Write(olderPath, older);
                var layout = Git.Show(MachineFiles.LayoutPathOf(path), rev);
                if (layout != null) CompareFiles.Write(MachineFiles.LayoutPathOf(olderPath), layout);
                WriteSubmachines(path, olderPath, older, rev, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FsmDocuments.SafeFullPath(path) });
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Message($"The version of {name} could not be written to {CompareFiles.Root}: {e.Message}");
                CompareFiles.Drop(folder);
                return;
            }
            _wanted.Add((olderPath, FsmDocuments.SafeFullPath(path), $"{name} ({label})", name, DateTime.UtcNow));
            // The older version on the left, the file on the right, in a group of its own.
            FsmDocuments.OpenInDiagram(olderPath);
            FsmDocuments.OpenInDiagram(path);
            FsmDocuments.NewVerticalTabGroup();
            Pair();
        }

        private static string Normalize(string text) => text.Replace("\r\n", "\n");

        /// <summary>The submachines of a version, at the same version, beside it (as far as Git has them): its references resolve.</summary>
        private static void WriteSubmachines(string file, string copy, string text, string rev, HashSet<string> done)
        {
            foreach (var sub in Load.SubmachineFilesOf(text))
            {
                string source, target;
                try
                {
                    source = Hrefs.ResolvePath(file, sub);
                    target = Hrefs.ResolvePath(copy, sub);
                }
                catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
                {
                    continue;
                }
                if (!done.Add(FsmDocuments.SafeFullPath(source)) || done.Count > 50 || !CompareFiles.IsReadOnlySide(target)) continue;
                var subText = Git.Show(source, rev);
                if (subText == null) continue;
                CompareFiles.Write(target, subText);
                WriteSubmachines(source, target, subText, rev, done);
            }
        }

        /// <summary>The diffs asked for whose two diagrams are open: linked, the older version on the left.</summary>
        private void Pair()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var w in _wanted.ToList())
            {
                // Both diagrams open at once: a side that didn't open as a diagram never will, and isn't paired later by surprise.
                if (DateTime.UtcNow - w.Asked > TimeSpan.FromSeconds(5))
                {
                    _wanted.Remove(w);
                    continue;
                }
                var older = _panes.LastOrDefault(p => p.Session == null && Same(p.FilePath, w.Older));
                var newer = _panes.LastOrDefault(p => p.Session == null && p != older && Same(p.FilePath, w.Newer));
                if (older == null || newer == null) continue;
                _wanted.Remove(w);
                StartDiff(older, newer, w.OlderTitle, w.NewerTitle);
            }
        }

        private void StartDiff(FsmEditorPane older, FsmEditorPane newer, string olderTitle, string newerTitle)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // An older version without its layout file borrows the newer one's, for the elements with the same names.
            var doc = older.Document;
            if (doc.LayoutMissing && doc.Old == null && newer.Document.State.LayoutText != null && doc.State.Problems == null)
            {
                var read = Format.ReadMachine(doc.State.ModelText, newer.Document.State.LayoutText);
                if (!read.Failed)
                {
                    doc.DisplayModel = read.Model;
                    doc.Refresh();
                    ((IDiffPane)older).Toast("This version has no layout file: its elements are drawn where the newer version has them.");
                }
            }
            var session = new DiffSession(older, newer, olderTitle, newerTitle);
            older.Session = session;
            newer.Session = session;
            session.Update();
        }

        // ---------------------------------------------------------------- merge

        /// <summary>
        /// Opens the diagram merge of a document with conflicts: its two sides beside it (incoming, then current),
        /// read-only, and the document as the result.
        /// </summary>
        public void OpenMerge(FsmEditorPane pane)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var doc = pane.Document;
            if (doc.Merge == null) return;
            var known = _merges.Values.FirstOrDefault(m => m.Document == doc);
            if (known.Session != null)
            {
                pane.Session = known.Session;
                known.Session.Attach(pane, "result");
                _panes.Add(pane);
                return;
            }
            var m0 = doc.Merge;
            var folder = CompareFiles.NewSession("merge");
            FsmModel Read(MachineTexts t)
            {
                var r = Format.ReadMachine(t.Model, t.Layout);
                return r.Failed ? null : r.Model;
            }
            MergeSession session = null;
            var host = new MergeHost(doc);
            session = new MergeSession(folder, m0.Current, m0.Incoming, m0.Base, Read, host, () => doc.State.Model?.Clone());
            _merges[folder] = (session, doc);
            var name = Path.GetFileName(doc.Path);
            var sides = new List<string>();
            try
            {
                foreach (var (role, t) in new[] { ("Incoming", m0.Incoming), ("Current", m0.Current) })
                {
                    var side = Path.Combine(folder, role, name);
                    CompareFiles.Write(side, t.Model);
                    if (t.Layout != null) CompareFiles.Write(MachineFiles.LayoutPathOf(side), t.Layout);
                    sides.Add(side);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                ((IDiffPane)pane).Toast($"The sides of the merge could not be written to {CompareFiles.Root}: {e.Message}");
            }
            pane.Session = session;
            session.Attach(pane, "result");
            if (!_panes.Contains(pane)) _panes.Add(pane);
            if (session.ConflictCount == 0) ((IDiffPane)pane).Toast("The two sides of the conflicts describe the same machine.");
            // The sides open to the right of the result, each in a group of its own.
            foreach (var side in sides)
            {
                FsmDocuments.OpenInDiagram(side);
                FsmDocuments.NewVerticalTabGroup();
            }
        }

        /// <summary>What the merge asks of the result's document.</summary>
        private sealed class MergeHost : IMergeHost
        {
            private readonly FsmDocument _doc;

            public MergeHost(FsmDocument doc) => _doc = doc;

            public string Edit(FsmModel model, string label)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return _doc.Edit(model);
            }

            public object State() => _doc.State;
        }

        /// <summary>Forgets a merge: its side panes close, and its files go.</summary>
        private void EndMerge(MergeSession merge)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _merges.Remove(merge.Id);
            foreach (var side in merge.Sides.Values.OfType<FsmEditorPane>().ToList())
            {
                side.Session = null;
                side.CloseWindow();
            }
            CompareFiles.Drop(merge.Id);
        }

        /// <summary>Complete Merge: saves the result, marks it resolved in Git, and closes the sides.</summary>
        public void Complete(FsmEditorPane pane)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(pane.Session is MergeSession merge)) return;
            var doc = pane.Document;
            var name = Path.GetFileName(doc.Path);
            try
            {
                if (!doc.Save()) return;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Message($"{name}: the merge could not be completed, {e.Message}. The file keeps its conflicts.");
                return;
            }
            if (Git.RootOf(doc.Path) != null && !Git.Add(doc.Path, doc.LayoutPath))
            {
                Message("The merge was saved, but could not be marked resolved in Git.");
            }
            doc.Merge = null;
            EndMerge(merge);
            foreach (var p in merge.Results.OfType<FsmEditorPane>().ToList())
            {
                p.Session = null;
                ((IDiffPane)p).ShowDiff(null);
            }
            ((IDiffPane)pane).Toast($"{name}: merge completed.");
        }

        // ---------------------------------------------------------------- messages of the panes

        public void Navigate(FsmEditorPane pane, int dir, string key)
        {
            if (pane.Session is DiffSession diff) diff.Navigate(dir);
            else if (pane.Session is MergeSession merge) merge.Navigate(dir, key);
        }

        public void ShowLayout(FsmEditorPane pane, bool show) => (pane.Session as DiffSession)?.SetShowLayout(show);

        public void Choose(FsmEditorPane pane, string key, string side) => (pane.Session as MergeSession)?.Choose(key, side);

        /// <summary>The panes of a diff or merge scroll and zoom together.</summary>
        public void ViewChanged(FsmEditorPane pane, double x, double y, double zoom)
        {
            IEnumerable<IDiffPane> others;
            if (pane.Session is DiffSession diff) others = new[] { diff.Other(pane) };
            else if (pane.Session is MergeSession merge) others = merge.Sides.Values.Concat(merge.Results);
            else return;
            foreach (var p in others.OfType<FsmEditorPane>()) if (p != pane) p.Control?.SetView(x, y, zoom);
        }

        private static void Message(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, text, "FSM Editor", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }
}
