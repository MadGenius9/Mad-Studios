using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MadModStudio.App.ViewModels;
using MadModStudio.Core.Models;

namespace MadModStudio.App.Views;

public partial class ProjectView : UserControl
{
    private ProjectViewModel? _vm;

    public ProjectView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null) _vm.GoToLineRequested -= GoToLine;
        _vm = DataContext as ProjectViewModel;
        if (_vm != null) _vm.GoToLineRequested += GoToLine;
    }

    private void GoToLine(int line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (line < 1 || line > Editor.Document.LineCount) return;
            Editor.ScrollToLine(line);
            var docLine = Editor.Document.GetLineByNumber(line);
            Editor.TextArea.Caret.Offset = docLine.Offset;
            Editor.Select(docLine.Offset, docLine.Length);
            Editor.Focus();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_vm != null && e.NewValue is FileNode node && !node.IsFolder) _vm.SelectedFile = node;
    }

    private void DiagGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm != null && DiagGrid.SelectedItem is ModDiagnostic d) _vm.OpenDiagnosticCommand.Execute(d);
    }
}
