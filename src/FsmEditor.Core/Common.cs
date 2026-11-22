using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace FsmEditor.Core
{
    /// <summary>What fsm-editor-common answered with an error (<c>&lt;fn&gt;: &lt;message&gt;</c>), or why it couldn't answer.</summary>
    public sealed class CommonException : Exception
    {
        public CommonException(string message) : base(message) { }
    }

    /// <summary>
    /// fsm-editor-common (github.com/vincedupuis/fsm-editor-common, Rust): the file services of the editions, written once.
    /// Reading and writing a machine's two files, converting old (XMI) files, validation, the grammar's checks,
    /// comparing, merging and code generation (fsm-cli's) are requests to its sidecar, <c>fsm-editor-common serve</c>,
    /// started on first use and kept running: one JSON request per line on its standard input, one JSON answer per line
    /// on its standard output (its <c>api.schema.json</c>). It holds no state: when it exits, the next request starts
    /// it again. Requests are answered one at a time, from any thread.
    /// </summary>
    public static class Common
    {
        private static readonly object Lock = new object();
        private static Process _process;
        private static StreamWriter _input;
        private static int _next;

        /// <summary>
        /// The sidecar's path: set by the extension (beside its assembly) and by the tests (<c>FSM_EDITOR_COMMON</c>,
        /// else <c>cli/fsm-editor-common</c> of the VSIX project, where <c>scripts/fetch-common.sh</c> puts it).
        /// </summary>
        public static string ExecutablePath { get; set; }

        /// <summary>The result of <paramref name="fn"/> with arguments <paramref name="argsJson"/> (a JSON object), parsed.</summary>
        public static object Call(string fn, string argsJson)
        {
            lock (Lock)
            {
                for (var attempt = 0; ; attempt++)
                {
                    string line;
                    var id = ++_next;
                    try
                    {
                        var p = Start();
                        _input.Write("{\"id\":" + id + ",\"fn\":" + ClipboardJson.Quote(fn) + ",\"args\":" + argsJson + "}\n");
                        _input.Flush();
                        line = p.StandardOutput.ReadLine();
                        if (line == null) throw new IOException("fsm-editor-common ended.");
                    }
                    catch (Exception e) when (e is IOException || e is InvalidOperationException)
                    {
                        // Ended (or never started): once more with a new one.
                        Stop();
                        if (attempt == 0) continue;
                        throw new CommonException($"fsm-editor-common doesn't answer: {e.Message}");
                    }
                    if (!(ClipboardJson.ParseJson(line) is Dictionary<string, object> answer)) throw new CommonException($"fsm-editor-common answered: {line}");
                    if (answer.TryGetValue("error", out var error)) throw new CommonException(error as string ?? "error");
                    answer.TryGetValue("result", out var result);
                    return result;
                }
            }
        }

        /// <summary>Arguments as a JSON object: values are JSON texts already (see <see cref="Str"/>).</summary>
        public static string Args(params (string Key, string Json)[] fields)
        {
            var sb = new StringBuilder("{");
            for (var i = 0; i < fields.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ClipboardJson.Quote(fields[i].Key)).Append(':').Append(fields[i].Json);
            }
            return sb.Append('}').ToString();
        }

        /// <summary>A text as JSON; null gives null.</summary>
        public static string Str(string s) => s == null ? "null" : ClipboardJson.Quote(s);

        private static Process Start()
        {
            if (_process != null && !_process.HasExited) return _process;
            var path = ExecutablePath ?? Environment.GetEnvironmentVariable("FSM_EDITOR_COMMON");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) throw new InvalidOperationException($"the sidecar is missing ({path ?? "no path"}).");
            var utf8 = new UTF8Encoding(false);
            _process = Process.Start(new ProcessStartInfo(path, "serve")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = utf8,
                StandardErrorEncoding = utf8,
            });
            // Written as UTF-8 without a byte order mark (StandardInputEncoding needs .NET Core).
            _input = new StreamWriter(_process.StandardInput.BaseStream, utf8) { AutoFlush = false, NewLine = "\n" };
            _process.ErrorDataReceived += (s, e) => { };
            _process.BeginErrorReadLine();
            return _process;
        }

        private static void Stop()
        {
            if (_process == null) return;
            try
            {
                _input = null;
                if (!_process.HasExited) _process.Kill();
            }
            catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
            {
                // already gone
            }
            _process.Dispose();
            _process = null;
        }

        /// <summary>Ends the sidecar (closing its input ends it), e.g. when the IDE closes.</summary>
        public static void Shutdown()
        {
            lock (Lock)
            {
                if (_process == null) return;
                try
                {
                    _input?.Close();
                    if (!_process.WaitForExit(2000)) _process.Kill();
                }
                catch (Exception e) when (e is IOException || e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
                {
                    // already gone
                }
                _input = null;
                _process.Dispose();
                _process = null;
            }
        }
    }
}
