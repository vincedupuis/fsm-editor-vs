using System;
using System.Collections.Generic;
using System.Linq;
using FsmEditor.Core;
using Microsoft.VisualStudio.Shell;

namespace FsmEditor.Services
{
    /// <summary>
    /// Publishes validation results and code generation problems to the Error
    /// List. Each document (or code generation run) replaces its own entries.
    /// </summary>
    internal sealed class Diagnostics : IDisposable
    {
        private readonly ErrorListProvider _provider;

        public Diagnostics(IServiceProvider services, string name, Guid id)
        {
            _provider = new ErrorListProvider(services) { ProviderName = name, ProviderGuid = id };
        }

        /// <summary>Called when an entry is double-clicked: file and element id (validation) or line (code generation).</summary>
        public Action<string, string, int> Navigate { get; set; }

        /// <summary>Zero-based line of the element <paramref name="id"/> in the XMI text.</summary>
        public static int LineOf(string text, string id)
        {
            if (string.IsNullOrEmpty(id)) return 0;
            var at = text.IndexOf($"xmi:id=\"{id}\"", StringComparison.Ordinal);
            if (at < 0) at = text.IndexOf($"\"{id}\"", StringComparison.Ordinal);
            if (at < 0) return 0;
            var line = 0;
            for (int i = 0; i < at; i++)
            {
                if (text[i] == '\n') line++;
            }
            return line;
        }

        public void Publish(string file, string text, IEnumerable<Issue> issues, string error)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var entries = issues.Select(i => (Id: i.Id, Line: LineOf(text, i.Id), i.Severity, i.Message)).ToList();
            if (error != null) entries.Add(("", 0, Severity.Error, error));
            Replace(file, entries);
        }

        /// <summary>Replaces the entries of <paramref name="file"/> (entries: element id, zero-based line, severity, message).</summary>
        public void Replace(string file, IEnumerable<(string Id, int Line, Severity Severity, string Message)> entries)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _provider.SuspendRefresh();
            try
            {
                Remove(file);
                foreach (var e in entries)
                {
                    var task = new ErrorTask
                    {
                        Document = file,
                        Line = e.Line,
                        Column = 0,
                        Text = e.Message,
                        ErrorCategory = e.Severity == Severity.Error ? TaskErrorCategory.Error : e.Severity == Severity.Warning ? TaskErrorCategory.Warning : TaskErrorCategory.Message,
                        Category = TaskCategory.BuildCompile,
                        HelpKeyword = e.Id,
                    };
                    var id = e.Id;
                    var line = e.Line;
                    task.Navigate += (s, a) => Navigate?.Invoke(file, id, line);
                    _provider.Tasks.Add(task);
                }
            }
            finally
            {
                _provider.ResumeRefresh();
            }
        }

        public void Remove(string file)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var t in _provider.Tasks.OfType<ErrorTask>().Where(t => string.Equals(t.Document, file, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _provider.Tasks.Remove(t);
            }
        }

        public void Clear()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _provider.Tasks.Clear();
        }

        public void Show()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _provider.Show();
        }

        public void Dispose() => _provider.Dispose();
    }
}
