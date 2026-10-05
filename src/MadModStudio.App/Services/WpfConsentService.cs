using System.Windows;
using MadModStudio.AI;

namespace MadModStudio.App.Services;

/// <summary>Shows exactly what an AI operation may send externally and asks the user before anything is transmitted.</summary>
public sealed class WpfConsentService : IAIConsentService
{
    public Task<bool> ConfirmAsync(AIEgressNotice notice, CancellationToken ct = default)
    {
        var text = $"""
            {notice.Operation} will send data to {notice.ProviderName} ({notice.Destination}).

            Data that may be sent:
            {string.Join("\n", notice.DataCategories.Select(c => "  • " + c))}

            Nothing else from your game installation or projects is uploaded. Files are only sent when the AI explicitly requests them through Mad Mod Studio's tools.

            Continue?
            """;
        return Application.Current.Dispatcher.InvokeAsync(() =>
            MessageBox.Show(Application.Current.MainWindow!, text, "Send data to AI provider?", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes).Task;
    }
}
