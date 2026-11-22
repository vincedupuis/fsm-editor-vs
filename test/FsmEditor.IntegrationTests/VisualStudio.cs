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

        /// <summary>Whether <see cref="Dispose"/> deletes the folder (not when another instance opens it next).</summary>
        public bool KeepFolder { get; set; }

        public VisualStudio() : this(null) { }

        /// <summary>Visual Studio on a folder as it is (one an earlier instance had open).</summary>
        public static VisualStudio StartOn(string folder) => new VisualStudio(folder);

        // Screen coordinates in pixels, as UI Automation gives them and the mouse takes them (per-monitor DPI aware).
        static VisualStudio() => SetProcessDpiAwarenessContext(new IntPtr(-4));

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        /// <summary>On <paramref name="folder"/> as it is, or on a new folder holding copies of the examples (null).</summary>
        private VisualStudio(string folder)
        {
            // fsm-editor-common's sidecar, for the tests that read the machines as the diagram does (where it draws them).
            Core.Common.ExecutablePath = Environment.GetEnvironmentVariable("FSM_EDITOR_COMMON") ?? Path.Combine(AppContext.BaseDirectory, "cli", "fsm-editor-common.exe");
            if (folder == null)
            {
                folder = Path.Combine(Path.GetTempPath(), "fsm-vs-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(folder);
                foreach (var f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "examples")))
                    File.Copy(f, Path.Combine(folder, Path.GetFileName(f)));
            }
            Folder = folder;
            // The folder first: /log takes an optional file name, and would take the folder for it (no folder opened,
            // no ActivityLog.xml where integration.yml reads it).
            _process = Process.Start(new ProcessStartInfo(DevenvPath(), $"\"{Folder}\" /rootsuffix Exp /log") { UseShellExecute = false });
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while ((_dte = _sta.Invoke(() => FindDte(_process.Id))) == null)
            {
                if (_process.HasExited) throw new InvalidOperationException($"devenv exited ({_process.ExitCode}) before it could be reached");
                if (DateTime.UtcNow > deadline) throw new TimeoutException("devenv's DTE did not appear in the Running Object Table");
                Thread.Sleep(1000);
            }
            Until("Visual Studio ready", () => Do(d => d.MainWindow != null && d.MainWindow.Visible), TimeSpan.FromMinutes(3));
            MainWindow = Do(d => (IntPtr)d.MainWindow.HWnd);
        }

        /// <summary>Visual Studio's main window.</summary>
        public IntPtr MainWindow { get; }

        public int ProcessId => _process.Id;

        /// <summary>Whether Visual Studio is still running.</summary>
        public bool Running => !_process.HasExited;

        /// <summary>
        /// Starts a call on the DTE that doesn't return until a modal dialog it opens is closed (Visual Studio runs
        /// a command synchronously until it awaits): the test closes the dialog through UI Automation, then waits for
        /// the task. Calls through <see cref="Do{T}"/> wait behind it meanwhile.
        /// </summary>
        public System.Threading.Tasks.Task Begin(Action<EnvDTE.DTE> f) => _sta.Post(() => f(_dte));

        /// <summary>Ends Visual Studio as a crash would (no saving, no questions).</summary>
        public void Kill()
        {
            try
            {
                _process.Kill();
            }
            catch (InvalidOperationException)
            {
                // exited already
            }
            _process.WaitForExit(30000);
        }

        /// <summary>A call on the DTE, on its thread; when Visual Studio is gone, says so (and its exit code).</summary>
        public T Do<T>(Func<EnvDTE.DTE, T> f)
        {
            if (_process.HasExited) throw new InvalidOperationException($"Visual Studio exited (code {_process.ExitCode}); see its ActivityLog.xml");
            return _sta.Invoke(() => f(_dte));
        }

        public void Do(Action<EnvDTE.DTE> f) => Do(d =>
        {
            f(d);
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

        /// <summary>The name Visual Studio gives a command of the extension (VSCommandTable.vsct), by its id.</summary>
        public const string CommandSet = "{17a524b9-398e-41e9-a5c4-83d740321394}";

        public string CommandName(int id) => Do(d => d.Commands.Item(CommandSet, id).Name);

        /// <summary>Runs a command of the extension by its id (through its name, as the Command Window does).</summary>
        public void Raise(int id)
        {
            var name = CommandName(id);
            Do(d => d.ExecuteCommand(name));
        }

        /// <summary>Runs a command of Visual Studio by its name (Edit.Undo, File.SaveAll...).</summary>
        public void Execute(string name) => Do(d => d.ExecuteCommand(name));

        /// <summary>
        /// Closes every document without saving, after the dialogs a failed test may have left open (which would keep
        /// Visual Studio from answering).
        /// </summary>
        public void CloseAll()
        {
            CloseDialogs();
            Do(d => d.Documents.CloseAll(EnvDTE.vsSaveChanges.vsSaveChangesNo));
            CloseDialogs();
        }

        /// <summary>Closes the modal dialogs Visual Studio shows (as their Close button does); their titles.</summary>
        public System.Collections.Generic.List<string> CloseDialogs()
        {
            var closed = new System.Collections.Generic.List<string>();
            for (var round = 0; round < 5; round++)
            {
                var modal = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, _process.Id)).Cast<AutomationElement>()
                    .Where(w => w.Current.NativeWindowHandle != MainWindow.ToInt32())
                    .Concat(AutomationElement.FromHandle(MainWindow).FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)).Cast<AutomationElement>())
                    .Where(w => w.TryGetCurrentPattern(WindowPattern.Pattern, out var p) && ((WindowPattern)p).Current.IsModal)
                    .ToList();
                if (modal.Count == 0) break;
                foreach (var w in modal)
                {
                    try
                    {
                        closed.Add(w.Current.Name);
                        ((WindowPattern)w.GetCurrentPattern(WindowPattern.Pattern)).Close();
                    }
                    catch (ElementNotAvailableException)
                    {
                        // closed meanwhile
                    }
                }
                Thread.Sleep(500);
            }
            return closed;
        }

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

        /// <summary>
        /// Where the active diagram draws a vertex (by name; <paramref name="fx"/>, <paramref name="fy"/>: a fraction of
        /// its box), in screen pixels: its files on disk read as the diagram reads them, in the view the diagram fits
        /// them to (Fit: what it does when it opens, and on F, which this presses first, in a corner of the diagram).
        /// </summary>
        public System.Drawing.Point VertexOnScreen(string machine, string vertex, double fx = 0.5, double fy = 0.5)
        {
            var model = PathOf(machine);
            var layout = model + ".layout";
            var read = Core.Format.ReadMachine(File.ReadAllText(model), File.Exists(layout) ? File.ReadAllText(layout) : null);
            if (read.Failed) throw new InvalidOperationException($"{machine}: {string.Join("; ", read.Problems)}");
            var v = read.Model.Vertices.FirstOrDefault(x => x.Name == vertex) ?? throw new InvalidOperationException($"{machine} has no {vertex}");
            var r = Diagram().Current.BoundingRectangle;
            Input.Foreground(MainWindow);
            Input.Click((int)r.Left + 4, (int)r.Top + 4);
            Input.Key(0x46); // F: Fit
            var scale = Input.Scale(MainWindow);
            double w = r.Width / scale, h = r.Height / scale;
            var b = Core.Geometry.ContentBounds(new Core.ModelIndex(read.Model));
            const double pad = 40;
            var z = Core.Geometry.Clamp(Math.Min((w - pad * 2) / Math.Max(b.W, 1), (h - pad * 2) / Math.Max(b.H, 1)), 0.2, 1.5);
            double viewX = (w - b.W * z) / 2 - b.X * z, viewY = (h - b.H * z) / 2 - b.Y * z;
            return new System.Drawing.Point((int)Math.Round(r.Left + (viewX + (v.X + v.W * fx) * z) * scale), (int)Math.Round(r.Top + (viewY + (v.Y + v.H * fy) * z) * scale));
        }

        /// <summary>Whether a diagram's properties window is open (its title, automation id <c>FsmPropertiesWindow</c>).</summary>
        public bool PropertiesWindowOpen() =>
            AutomationElement.FromHandle(MainWindow)
                .FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "FsmPropertiesWindow"))
                .Cast<AutomationElement>().Any(e => !e.Current.IsOffscreen);

        /// <summary>The texts Visual Studio's main window shows (UI Automation: its Text elements), with their tooltips.</summary>
        public System.Collections.Generic.List<(string Text, string Tip)> Texts()
        {
            var main = AutomationElement.FromHandle(MainWindow);
            return main.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>().Select(e => (e.Current.Name ?? "", e.Current.HelpText ?? "")).ToList();
        }

        /// <summary>Waits for a text of the main window matching <paramref name="match"/> (a toast, a diff's bar...).</summary>
        public string WaitText(string what, Func<string, bool> match, TimeSpan? timeout = null)
        {
            string found = null;
            Until(what, () => (found = Texts().Select(t => t.Text).FirstOrDefault(match)) != null, timeout);
            return found;
        }

        /// <summary>A dialog of Visual Studio (a top-level window of its process, or one its main window holds), by its title.</summary>
        public AutomationElement WaitDialog(string title, TimeSpan? timeout = null)
        {
            AutomationElement found = null;
            var condition = new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, _process.Id), new PropertyCondition(AutomationElement.NameProperty, title),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
            Until($"the dialog \"{title}\"", () =>
            {
                found = AutomationElement.RootElement.FindFirst(TreeScope.Children, condition) ?? AutomationElement.FromHandle(MainWindow).FindFirst(TreeScope.Children, condition);
                return found != null;
            }, timeout);
            return found;
        }

        /// <summary>The texts of a dialog (a message box's message among them).</summary>
        public static string TextOf(AutomationElement dialog) => string.Join("\n", dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().Select(e => e.Current.Name));

        /// <summary>Presses a button of a dialog, by its name.</summary>
        public static void Press(AutomationElement dialog, string button)
        {
            AutomationElement b = null;
            Until($"the button {button}", () => (b = dialog.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, button)))) != null);
            ((InvokePattern)b.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        }

        /// <summary>Answers Windows' file dialog (Save As, Open) with a path.</summary>
        public static void AnswerFileDialog(AutomationElement dialog, string path, string button)
        {
            AutomationElement name = null;
            // The file name box: an edit (Open) or the edit of a combo box (Save As), automation id 1001 or 1148.
            Until("the file name box", () => (name = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
                .Cast<AutomationElement>().FirstOrDefault(e => e.Current.AutomationId == "1001" || e.Current.AutomationId == "1148")) != null);
            // Typed, as the dialog takes only what it is typed (a value set through UI Automation isn't used).
            Input.Foreground(new IntPtr(dialog.Current.NativeWindowHandle));
            name.SetFocus();
            Input.Ctrl(0x41);
            Input.Type(path);
            Thread.Sleep(300);
            Input.Key(0x0D);
        }

        /// <summary>The Error List's items, as (file, line, description).</summary>
        public System.Collections.Generic.List<(string File, int Line, string Description)> Errors() => Do(d =>
        {
            var list = new System.Collections.Generic.List<(string, int, string)>();
            var items = ((EnvDTE80.DTE2)d).ToolWindows.ErrorList.ErrorItems;
            for (var i = 1; i <= items.Count; i++)
            {
                var e = items.Item(i);
                list.Add((e.FileName ?? "", e.Line, e.Description ?? ""));
            }
            return list;
        });

        /// <summary>The Error List's items on a file of the folder.</summary>
        public System.Collections.Generic.List<(string File, int Line, string Description)> ErrorsOn(string name) =>
            Errors().Where(e => string.Equals(e.File, PathOf(name), StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>The document of a file, if open.</summary>
        public bool IsOpen(string name)
        {
            var path = PathOf(name);
            return Do(d => d.Documents.Cast<EnvDTE.Document>().Any(x => string.Equals(x.FullName, path, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>Whether an open document is unchanged (Visual Studio's dirty mark).</summary>
        public bool Saved(string path) => Do(d => d.Documents.Cast<EnvDTE.Document>().First(x => string.Equals(x.FullName, path, StringComparison.OrdinalIgnoreCase)).Saved);

        public void Dispose()
        {
            try
            {
                CloseDialogs();
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
            if (KeepFolder) return;
            try
            {
                // Git's objects are read-only.
                foreach (var f in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
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

        /// <summary>Runs <paramref name="a"/> on the thread without waiting for it.</summary>
        public System.Threading.Tasks.Task Post(Action a)
        {
            var done = new System.Threading.Tasks.TaskCompletionSource<bool>();
            _work.Add(() =>
            {
                try
                {
                    a();
                    done.SetResult(true);
                }
                catch (Exception e)
                {
                    done.SetException(e);
                }
            });
            return done.Task;
        }

        /// <summary>
        /// Runs <paramref name="f"/> on the thread and waits for it, at most two minutes: a call that Visual Studio
        /// doesn't answer (a modal dialog nobody closes, a call queued behind one) fails instead of waiting forever.
        /// </summary>
        public T Invoke<T>(Func<T> f)
        {
            T result = default;
            Exception error = null;
            // Not disposed: a call that times out still sets it when it ends.
            var done = new ManualResetEventSlim();
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
            if (!done.Wait(TimeSpan.FromMinutes(2))) throw new TimeoutException("Visual Studio didn't answer a call in 2 minutes (a dialog open?)");
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

        public static void Click(System.Drawing.Point p) => Click(p.X, p.Y);

        /// <summary>Types a text, character by character (Unicode input, whatever the keyboard layout).</summary>
        public static void Type(string text)
        {
            foreach (var c in text)
            {
                var inputs = new[]
                {
                    new INPUT { type = 1, ki = new KEYBDINPUT { wScan = (ushort)c, dwFlags = 0x0004 } },
                    new INPUT { type = 1, ki = new KEYBDINPUT { wScan = (ushort)c, dwFlags = 0x0004 | 0x0002 } },
                };
                SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
                Thread.Sleep(5);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        /// <summary>INPUT with its union sized for MOUSEINPUT, the largest member.</summary>
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        private struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public KEYBDINPUT ki;
        }

        [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);

        /// <summary>A double-click with Alt held (what opens a submachine state's machine).</summary>
        public static void AltDoubleClick(System.Drawing.Point p)
        {
            SetCursorPos(p.X, p.Y);
            Thread.Sleep(100);
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            for (var i = 0; i < 2; i++)
            {
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(60);
            }
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            Thread.Sleep(300);
        }

        /// <summary>A click with Alt held (what opens an element's properties window).</summary>
        public static void AltClick(System.Drawing.Point p)
        {
            SetCursorPos(p.X, p.Y);
            Thread.Sleep(100);
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(60);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            Thread.Sleep(300);
        }

        /// <summary>A click with the middle button.</summary>
        public static void MiddleClick(System.Drawing.Point p)
        {
            SetCursorPos(p.X, p.Y);
            Thread.Sleep(100);
            mouse_event(0x0020, 0, 0, 0, UIntPtr.Zero);
            mouse_event(0x0040, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(300);
        }

        /// <summary>A drag with the left button, in steps.</summary>
        public static void Drag(System.Drawing.Point from, System.Drawing.Point to)
        {
            SetCursorPos(from.X, from.Y);
            Thread.Sleep(100);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(100);
            for (var i = 1; i <= 10; i++)
            {
                SetCursorPos(from.X + (to.X - from.X) * i / 10, from.Y + (to.Y - from.Y) * i / 10);
                Thread.Sleep(30);
            }
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(300);
        }

        /// <summary>Screen pixels per WPF unit in a window (its monitor's scale).</summary>
        public static double Scale(IntPtr window)
        {
            var dpi = GetDpiForWindow(window);
            return dpi > 0 ? dpi / 96.0 : 1;
        }

        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    }
}
