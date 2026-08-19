using GoAi.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GoWinUI.App.Services;

public sealed class ToolConfirmationService(MainWindow window) : IDisposable
{
    private static readonly System.Text.Json.JsonSerializerOptions DisplayJsonOptions = new()
    {
        WriteIndented = true,
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<bool> ConfirmAsync(ToolProposal proposal, CancellationToken cancellationToken = default)
    {
        // Die bewusste, dauerhaft gespeicherte Workspace-Freigabe und der
        // abgesendete Prompt autorisieren Datei- und Codeaktionen innerhalb
        // dieses Ordners. Die Pfadprüfung erfolgt weiterhin im Toolbroker.
        if (IsTrustedWorkspaceAction(proposal) || IsClientVerifiedReadOnly(proposal))
        {
            return true;
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunOnUiThreadAsync(async () =>
            {
                var json = System.Text.Json.JsonSerializer.Serialize(
                    proposal.Arguments,
                    DisplayJsonOptions);
                var content = new StackPanel { Spacing = 10, MaxWidth = 720 };
                content.Children.Add(new TextBlock
                {
                    Text = proposal.Summary,
                    TextWrapping = TextWrapping.Wrap,
                });
                content.Children.Add(new TextBlock
                {
                    Text = proposal.RiskClass switch
                    {
                        ToolRiskClass.LocalMutation => "Diese Aktion verändert lokale Dateien.",
                        ToolRiskClass.Process => "Diese Aktion startet ein freigegebenes lokales Prozess-Preset.",
                        ToolRiskClass.CadMutation => "Diese Aktion verändert die geöffnete BricsCAD-Zeichnung.",
                        _ => "Lokale Aktion",
                    },
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                });
                content.Children.Add(new TextBox
                {
                    Text = json,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.NoWrap,
                    MaxHeight = 320,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 12,
                });
                var dialog = new ContentDialog
                {
                    XamlRoot = (window.Content as FrameworkElement)?.XamlRoot,
                    Title = "Lokale AI-Aktion bestätigen",
                    Content = content,
                    PrimaryButtonText = "Einmal ausführen",
                    CloseButtonText = "Ablehnen",
                    DefaultButton = ContentDialogButton.Close,
                };
                return await dialog.ShowAsync() == ContentDialogResult.Primary;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsClientVerifiedReadOnly(ToolProposal proposal) =>
        proposal.RiskClass == ToolRiskClass.ReadOnly
        && proposal.Name is ClientToolNames.WorkspaceMap
            or ClientToolNames.DocumentsList
            or ClientToolNames.DocumentsSearch
            or ClientToolNames.DocumentsReadPages
            or ClientToolNames.FileSystemList
            or ClientToolNames.FileSystemStat
            or ClientToolNames.FileSystemFindFiles
            or ClientToolNames.FileSystemReadText
            or ClientToolNames.FileSystemReadMany
            or ClientToolNames.FileSystemSearch
            or ClientToolNames.BricsCadGeometryQuery
            or ClientToolNames.BricsCadMeasure;

    private static bool IsTrustedWorkspaceAction(ToolProposal proposal) =>
        proposal.Name is ClientToolNames.WorkspaceMap
            or ClientToolNames.FileSystemList
            or ClientToolNames.FileSystemStat
            or ClientToolNames.FileSystemFindFiles
            or ClientToolNames.FileSystemReadText
            or ClientToolNames.FileSystemReadMany
            or ClientToolNames.FileSystemSearch
            or ClientToolNames.FileSystemWriteText
            or ClientToolNames.FileSystemMove
            or ClientToolNames.FileSystemProposePatch
            or ClientToolNames.FileSystemProposeCreate
            or ClientToolNames.FileSystemProposeDelete
            or ClientToolNames.ProcessRunPreset
            or ClientToolNames.ProcessRun;

    private Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var dispatcher = window.DispatcherQueue;
        if (dispatcher.HasThreadAccess)
        {
            return action();
        }
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        if (!dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    completion.TrySetResult(await action());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("Das Bestätigungsfenster ist nicht mehr verfügbar."));
        }
        return completion.Task;
    }

    public void Dispose() => _gate.Dispose();
}
