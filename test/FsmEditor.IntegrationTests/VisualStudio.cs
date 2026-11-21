using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Windows.Automation;

namespace FsmEditor.IntegrationTests
{
    /// <summary>
    /// A Visual Studio started for the tests: the latest installed (vswhere), its experimental instance
    /// (<c>/rootsuffix Exp</c>), on a folder holding copies of the examples. Its DTE is reached through the Running
    /// Object Table and called on a thread of its own (STA, with a message filter retrying the calls Visual Studio
    /// rejects while busy).
    /// </summary>
    public sealed class VisualStudio : IDisposable
    {
        public string Folder { get; }
        private readonly Process _process;
        private readonly StaThread _sta = new StaThread();
        private EnvDTE.DTE _dte;

        public VisualStudio()
        {
            Folder = Path.Combine(Path.GetTempPath(), "fsm-vs-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Folder);
            foreach (var f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "examples")))
                File.Copy(f, Path.Combine(Folder, Path.GetFileName(f)));
            _process = Process.Start(new ProcessStartInfo(DevenvPath(), $"/rootsuffix Exp \"{Folder}\"") { UseShellExecute = false });
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while ((_dte = _sta.Invoke(() => FindDte(_process.Id))) == null)
            {
                if (_process.HasExited) throw new InvalidOperationException($"devenv exited ({_process.ExitCode}) before it could be reached");
                if (DateTime.UtcNow > deadline) throw new TimeoutException("devenv's DTE did not appear in the Running Object Table");
                Thread.Sleep(1000);
            }
            Until("Visual Studio ready", () => Do(d => d.MainWindow != null && d.MainWindow.Visible), TimeSpan.FromMinutes(3));
        }

        /// <summary>A call on the DTE, on its thread.</summary>
        public T Do<T>(Func<EnvDTE.DTE, T> f) => _sta.Invoke(() => f(_dte));

        public void Do(Action<EnvDTE.DTE> f) => _sta.Invoke(() =>
        {
            f(_dte);
            return 0;
        });

        /// <summary>Waits until <paramref name="probe"/> holds, polling it; a probe that throws is false.</summary>
        public static void Until(string what, Func<bool> probe, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
            Exception last = null;
            for (;;)
            {
                try
                {
                    if (probe()) return;
                }
                catch (Exception e)
                {
                    last = e;
                }
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out: {what}" + (last != null ? $" (last: {last.Message})" : ""));
                Thread.Sleep(250);
            }
        }

        public string PathOf(string name) => Path.Combine(Folder, name);

        /// <summary>Opens a file as Visual Studio does on a double-click, and waits until it is the active document.</summary>
        public void Open(string name)
        {
            var path = PathOf(name);
            Do(d => d.ItemOperations.OpenFile(path));
            Until($"{name} active", () => Do(d => string.Equals(d.ActiveDocument?.FullName, path, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>Whether the active document is shown by a text editor (rather than the diagram).</summary>
        public bool ActiveIsText() => Do(d => d.ActiveDocument?.Object("TextDocument") != null);

        /// <summary>Runs a command of the extension (VSCommandTable.vsct) by its id.</summary>
        public void Raise(int id)
        {
            Do(d =>
            {
                object input = null, output = null;
                d.Commands.Raise("{17a524b9-398e-41e9-a5c4-83d740321394}", id, ref input, ref output);
            });
        }

        /// <summary>Runs a command of Visual Studio by its name (Edit.Undo, File.SaveAll...).</summary>
        public void Execute(string name) => Do(d => d.ExecuteCommand(name));

        public void CloseAll() => Do(d => d.Documents.CloseAll(EnvDTE.vsSaveChanges.vsSaveChangesNo));

        /// <summary>The diagram of the active document, as UI Automation sees it.</summary>
        public AutomationElement Diagram()
        {
            var main = AutomationElement.FromHandle(Do(d => d.MainWindow.HWnd));
            AutomationElement found = null;
            Until("the diagram in UI Automation", () =>
            {
                found = main.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "FsmDiagramCanvas"))
                    .Cast<AutomationElement>().FirstOrDefault(e => !e.Current.IsOffscreen && e.Current.BoundingRectangle.Width > 0);
                return found != null;
            });
            return found;
        }

        /// <summary>A click on the diagram, at a fraction of its width and height, Visual Studio in front.</summary>
        public void ClickDiagram(double fx = 0.5, double fy = 0.5)
        {
            var r = Diagram().Current.BoundingRectangle;
            Input.Foreground(Do(d => d.MainWindow.HWnd));
            Input.Click((int)(r.Left + r.Width * fx), (int)(r.Top + r.Height * fy));
        }

        public void Dispose()
        {
            try
            {
                Do(d =>
                {
                    d.Documents.CloseAll(EnvDTE.vsSaveChanges.vsSaveChangesNo);
                    d.Quit();
                });
            }
            catch
            {
                // gone already
            }
            if (!_process.WaitForExit(30000))
            {
                try
                {
                    _process.Kill();
                }
                catch
                {
                    // exited meanwhile
                }
            }
            _sta.Dispose();
            try
            {
                Directory.Delete(Folder, true);
            }
            catch
            {
                // a file still open: left in the temp folder
            }
        }

        private static string DevenvPath()
        {
            var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
            var p = Process.Start(new ProcessStartInfo(vswhere, "-latest -prerelease -products * -property productPath") { UseShellExecute = false, RedirectStandardOutput = true });
            var path = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (!File.Exists(path)) throw new FileNotFoundException("no Visual Studio found by vswhere", path);
            return path;
        }

        /// <summary>The DTE that process <paramref name="pid"/> registered (<c>!VisualStudio.DTE.&lt;version&gt;:&lt;pid&gt;</c>).</summary>
        private static EnvDTE.DTE FindDte(int pid)
        {
            if (GetRunningObjectTable(0, out var rot) != 0) return null;
            rot.EnumRunning(out var monikers);
            var one = new IMoniker[1];
            CreateBindCtx(0, out var ctx);
            while (monikers.Next(1, one, IntPtr.Zero) == 0)
            {
                one[0].GetDisplayName(ctx, null, out var name);
                if (name.StartsWith("!VisualStudio.DTE.", StringComparison.Ordinal) && name.EndsWith(":" + pid, StringComparison.Ordinal))
                {
                    rot.GetObject(one[0], out var dte);
                    return dte as EnvDTE.DTE;
                }
            }
            return null;
        }

        [DllImport("ole32.dll")]
        private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);

        [DllImport("ole32.dll")]
        private static extern int CreateBindCtx(int reserved, out IBindCtx ctx);
    }

    /// <summary>A thread of its own for COM calls to Visual Studio: STA, with <see cref="MessageFilter"/> registered.</summary>
    internal sealed class StaThread : IDisposable
    {
        private readonly BlockingCollection<Action> _work = new BlockingCollection<Action>();
        private readonly Thread _thread;

        public StaThread()
        {
            _thread = new Thread(() =>
            {
                MessageFilter.Register();
                foreach (var a in _work.GetConsumingEnumerable()) a();
                MessageFilter.Revoke();
            }) { IsBackground = true, Name = "DTE" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public T Invoke<T>(Func<T> f)
        {
            T result = default;
            Exception error = null;
            using (var done = new ManualResetEventSlim())
            {
                _work.Add(() =>
                {
                    try
                    {
                        result = f();
                    }
                    catch (Exception e)
                    {
                        error = e;
                    }
                    done.Set();
                });
                done.Wait();
            }
            if (error != null) throw new InvalidOperationException(error.Message, error);
            return result;
        }

        public void Dispose() => _work.CompleteAdding();
    }

    /// <summary>Retries the calls Visual Studio rejects while it is busy (RPC_E_CALL_REJECTED), instead of failing them.</summary>
    [ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);
        [PreserveSig] int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
        [PreserveSig] int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
    }

    internal sealed class MessageFilter : IOleMessageFilter
    {
        public static void Register() => CoRegisterMessageFilter(new MessageFilter(), out _);

        public static void Revoke() => CoRegisterMessageFilter(null, out _);

        int IOleMessageFilter.HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo) => 0;

        // SERVERCALL_RETRYLATER: try again after 200 ms, for up to a minute.
        int IOleMessageFilter.RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType) => dwRejectType == 2 && dwTickCount < 60000 ? 200 : -1;

        int IOleMessageFilter.MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType) => 2;

        [DllImport("ole32.dll")]
        private static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);
    }

    /// <summary>Real mouse and keyboard input (the runner runs on a logged-in desktop).</summary>
    internal static class Input
    {
        public static void Foreground(IntPtr window)
        {
            SetForegroundWindow(window);
            Thread.Sleep(300);
        }

        public static void Click(int x, int y)
        {
            SetCursorPos(x, y);
            Thread.Sleep(50);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); // left down
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); // left up
            Thread.Sleep(300);
        }

        /// <summary>A key with Ctrl held (virtual key code, e.g. 'A').</summary>
        public static void Ctrl(byte key)
        {
            keybd_event(0x11, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, 2, UIntPtr.Zero);
            keybd_event(0x11, 0, 2, UIntPtr.Zero);
            Thread.Sleep(300);
        }

        public static void Key(byte key)
        {
            keybd_event(key, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, 2, UIntPtr.Zero);
            Thread.Sleep(300);
        }

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    }
}
