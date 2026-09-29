using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace FsmEditor.Services
{
    /// <summary>Small themed dialogs built in code.</summary>
    internal static class Dialogs
    {
        public static DialogWindow Window(string title, double width)
        {
            var w = new DialogWindow
            {
                Title = title,
                Width = width,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                HasMinimizeButton = false,
                HasMaximizeButton = false,
                ShowInTaskbar = false,
            };
            w.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            w.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return w;
        }

        public static TextBlock Label(string text) => new TextBlock { Text = text, Margin = new Thickness(0, 8, 0, 4), TextWrapping = TextWrapping.Wrap };

        public static TextBox TextBox(string text)
        {
            var tb = new TextBox { Text = text ?? "", Padding = new Thickness(3, 2, 3, 2) };
            tb.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.TextBoxStyleKey);
            return tb;
        }

        public static Button Button(string text, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button { Content = text, IsDefault = isDefault, IsCancel = isCancel, MinWidth = 75, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
            b.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ButtonStyleKey);
            return b;
        }

        public static StackPanel Buttons(params Button[] buttons)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            foreach (var b in buttons) panel.Children.Add(b);
            return panel;
        }

        /// <summary>Asks for the name of a new state machine; null when cancelled.</summary>
        public static string AskMachineName(string initial = "StateMachine")
        {
            var w = Window("New State Machine", 380);
            var input = TextBox(initial);
            var error = new TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
            var ok = Button("Create", isDefault: true);
            var cancel = Button("Cancel", isCancel: true);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(Label("Name of the new state machine:"));
            panel.Children.Add(input);
            panel.Children.Add(error);
            panel.Children.Add(Buttons(ok, cancel));
            w.Content = panel;
            string result = null;
            ok.Click += (s, e) =>
            {
                var name = input.Text.Trim();
                if (!Regex.IsMatch(name, @"^[\w .-]+$"))
                {
                    error.Text = "Use letters, digits, spaces, dots, dashes or underscores.";
                    error.Visibility = Visibility.Visible;
                    return;
                }
                result = name;
                w.DialogResult = true;
            };
            w.Loaded += (s, e) =>
            {
                input.Focus();
                input.SelectAll();
            };
            w.ShowModal();
            return result;
        }

        /// <summary>Visual Studio's folder picker; null when cancelled.</summary>
        public static string PickFolder(string title, string initial)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var shell = (IVsUIShell)Package.GetGlobalService(typeof(SVsUIShell));
            shell.GetDialogOwnerHwnd(out var owner);
            const int max = 1024;
            var buffer = Marshal.AllocCoTaskMem(max * 2);
            try
            {
                var info = new[]
                {
                    new VSBROWSEINFOW
                    {
                        lStructSize = (uint)Marshal.SizeOf(typeof(VSBROWSEINFOW)),
                        hwndOwner = owner,
                        pwzDlgTitle = title,
                        pwzInitialDir = initial,
                        nMaxDirName = max,
                        pwzDirName = buffer,
                    },
                };
                return shell.GetDirectoryViaBrowseDlg(info) == VSConstants.S_OK ? Marshal.PtrToStringUni(buffer) : null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }

        /// <summary>A file picker; null when cancelled.</summary>
        public static string PickFile(string title, string filter, string initialDir)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, InitialDirectory = initialDir, CheckFileExists = true };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        public static string SaveFile(string title, string filter, string initialDir, string fileName)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, InitialDirectory = initialDir, FileName = fileName, OverwritePrompt = true };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }
    }
}
