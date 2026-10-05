using System.Windows;
using Microsoft.Win32;

namespace MadModStudio.App.Services;

public interface IDialogService
{
    string? PickFolder(string title, string? initial = null);
    string? PickFile(string title, string filter, string? initial = null);
    IReadOnlyList<string> PickFiles(string title, string filter);
    void Info(string title, string message);
    void Error(string title, string message);
    bool Confirm(string title, string message);
    void OpenFolder(string path);
    string? Prompt(string title, string message, string defaultValue = "");
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow;

    public string? PickFolder(string title, string? initial = null)
    {
        var d = new OpenFolderDialog { Title = title };
        if (initial != null && Directory.Exists(initial)) d.InitialDirectory = initial;
        return d.ShowDialog(Owner) == true ? d.FolderName : null;
    }

    public string? PickFile(string title, string filter, string? initial = null)
    {
        var d = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        if (initial != null && Directory.Exists(initial)) d.InitialDirectory = initial;
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public IReadOnlyList<string> PickFiles(string title, string filter)
    {
        var d = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = true };
        return d.ShowDialog(Owner) == true ? d.FileNames : Array.Empty<string>();
    }

    public void Info(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Error(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public string? Prompt(string title, string message, string defaultValue = "")
    {
        var res = Application.Current.Resources;
        var box = new System.Windows.Controls.TextBox { Text = defaultValue, Margin = new Thickness(0, 8, 0, 12), MinWidth = 420 };
        var ok = new System.Windows.Controls.Button { Content = "OK", IsDefault = true, Style = (Style)res["AccentButton"] };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)res["TextBrush"] });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var w = new Window
        {
            Title = title, Content = panel, Owner = Owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (System.Windows.Media.Brush)res["Bg1Brush"],
        };
        ok.Click += (_, _) => w.DialogResult = true;
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? box.Text : null;
    }

    public void OpenFolder(string path)
    {
        var target = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
        if (!Directory.Exists(target)) return;
        try
        {
            if (File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else
                System.Diagnostics.Process.Start("explorer.exe", $"\"{target}\"");
        }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
