using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FsmEditor.Services;
using Microsoft.VisualStudio.PlatformUI;

namespace FsmEditor.CodeGen
{
    /// <summary>A template the user can pick.</summary>
    internal sealed class TemplateChoice
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";

        public override string ToString() => $"{Name}    ({Description})";
    }

    /// <summary>Asks for the template and output folder of a code generation.</summary>
    internal static class GenerateCodeDialog
    {
        /// <returns>The template file and output folder, or null when cancelled.</returns>
        public static (string Template, string Out)? Show(string machineName, IList<TemplateChoice> templates, string lastTemplate, string defaultOut)
        {
            var w = Dialogs.Window($"Generate Code: {machineName}", 520);
            var list = new ListBox { Height = 150, DisplayMemberPath = null };
            list.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            list.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            foreach (var t in templates) list.Items.Add(t);
            list.SelectedItem = templates.FirstOrDefault(t => string.Equals(t.Path, lastTemplate, System.StringComparison.OrdinalIgnoreCase)) ?? templates.FirstOrDefault();
            var browseTemplate = Dialogs.Button("Browse...");
            browseTemplate.HorizontalAlignment = HorizontalAlignment.Left;
            browseTemplate.Margin = new Thickness(0, 6, 0, 0);
            browseTemplate.Click += (s, e) =>
            {
                var file = Dialogs.PickFile("Template", "Handlebars template (*.hbs)|*.hbs", Path.GetDirectoryName(lastTemplate ?? defaultOut));
                if (file == null) return;
                var choice = new TemplateChoice { Path = file, Name = Path.GetFileNameWithoutExtension(file), Description = file };
                list.Items.Add(choice);
                list.SelectedItem = choice;
            };

            var output = Dialogs.TextBox(defaultOut);
            var browseOut = Dialogs.Button("Browse...");
            browseOut.Margin = new Thickness(6, 0, 0, 0);
            browseOut.Click += (s, e) =>
            {
                Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
                var folder = Dialogs.PickFolder("Generate Code: output folder", Directory.Exists(output.Text) ? output.Text : defaultOut);
                if (folder != null) output.Text = folder;
            };
            var outRow = new DockPanel();
            DockPanel.SetDock(browseOut, Dock.Right);
            outRow.Children.Add(browseOut);
            outRow.Children.Add(output);

            var error = new TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
            var ok = Dialogs.Button("Generate", isDefault: true);
            var cancel = Dialogs.Button("Cancel", isCancel: true);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(Dialogs.Label("Template (bundled templates, *.hbs files of the solution, or Browse):"));
            panel.Children.Add(list);
            panel.Children.Add(browseTemplate);
            panel.Children.Add(Dialogs.Label("Output folder (generated files are overwritten):"));
            panel.Children.Add(outRow);
            panel.Children.Add(error);
            panel.Children.Add(Dialogs.Buttons(ok, cancel));
            w.Content = panel;

            (string, string)? result = null;
            ok.Click += (s, e) =>
            {
                if (!(list.SelectedItem is TemplateChoice template))
                {
                    error.Text = "Pick a template.";
                    error.Visibility = Visibility.Visible;
                    return;
                }
                if (string.IsNullOrWhiteSpace(output.Text))
                {
                    error.Text = "Pick an output folder.";
                    error.Visibility = Visibility.Visible;
                    return;
                }
                result = (template.Path, output.Text.Trim());
                w.DialogResult = true;
            };
            w.ShowModal();
            return result;
        }
    }
}
