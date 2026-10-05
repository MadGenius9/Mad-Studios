using CommunityToolkit.Mvvm.ComponentModel;
using MadModStudio.Core.Security;

namespace MadModStudio.App.ViewModels;

/// <summary>Base for pages: busy state plus a safe runner that turns exceptions into visible messages instead of crashes.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyText;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _errorMessage;

    public abstract string Title { get; }

    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;

    protected async Task RunAsync(string busyText, Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = busyText;
        ErrorMessage = null;
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            ErrorMessage = SecretRedactor.Redact(ex.Message);
            App.Log("UI operation failed: " + busyText, ex);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }
}
