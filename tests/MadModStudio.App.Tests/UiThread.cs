using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MadModStudio.App.Tests;

/// <summary>
/// One STA thread with a running WPF dispatcher and the real <see cref="App"/> resources (theme, converters), shared by
/// all UI tests. Async work started on it resumes on it, like in the running app.
/// </summary>
internal static class UiThread
{
    private static readonly Lazy<Dispatcher> Dispatcher = new(Start);
    public static readonly BindingErrorListener BindingErrors = new();

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(BindingErrors);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true, Name = "WPF test UI thread" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    public static Task RunAsync(Func<Task> action) => Dispatcher.Value.InvokeAsync(action).Task.Unwrap();

    /// <summary>Measures and arranges like a 1400×900 window, then selects every tab (recursively) so all tab content is created.</summary>
    public static void Render(FrameworkElement root)
    {
        void Layout()
        {
            root.Measure(new Size(1400, 900));
            root.Arrange(new Rect(0, 0, 1400, 900));
            root.UpdateLayout();
        }
        Layout();
        var visited = new HashSet<TabControl>();
        for (var pass = 0; pass < 5; pass++)
        {
            var tabs = Descendants<TabControl>(root).Where(visited.Add).ToList();
            if (tabs.Count == 0) break;
            foreach (var tab in tabs)
            {
                var original = tab.SelectedIndex;
                for (var i = 0; i < tab.Items.Count; i++) { tab.SelectedIndex = i; Layout(); }
                tab.SelectedIndex = original;
                Layout();
            }
        }
    }

    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }
}

/// <summary>Collects WPF data-binding errors (e.g. "BindingExpression path error").</summary>
internal sealed class BindingErrorListener : TraceListener
{
    private readonly List<string> _errors = new();
    private string _partial = "";

    public override void Write(string? message) { lock (_errors) _partial += message; }

    public override void WriteLine(string? message)
    {
        lock (_errors)
        {
            _errors.Add(_partial + message);
            _partial = "";
        }
    }

    public List<string> Take()
    {
        lock (_errors)
        {
            var copy = _errors.ToList();
            _errors.Clear();
            return copy;
        }
    }
}
