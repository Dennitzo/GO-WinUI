using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging;

namespace GoWinUI.App.Services;

public sealed class ProjectAssetActivityService(
    IProjectAssetWorkingCopyService workingCopies,
    IProjectRepository projects,
    RecentActivityService recentActivity,
    ILogger<ProjectAssetActivityService> logger) : IDisposable
{
    private static readonly Action<ILogger, Guid, Exception?> ActivityUpdateFailed = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(4101, nameof(ActivityUpdateFailed)),
        "Die Bearbeitung der Projektdatei {AssetId} konnte nicht als letzte Aktivität erfasst werden.");
    private bool _started;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        workingCopies.AssetSynchronized += OnAssetSynchronized;
        _started = true;
    }

    private async void OnAssetSynchronized(object? sender, ProjectAssetSynchronizedEventArgs args)
    {
        try
        {
            var project = await projects.GetAsync(args.Asset.ProjectId, CancellationToken.None).ConfigureAwait(false);
            var projectName = project?.Name ?? "Unbekanntes Projekt";
            await recentActivity.RecordAsync(
                $"Datei „{args.Asset.FileName}“ in Projekt „{projectName}“ bearbeitet",
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ActivityUpdateFailed(logger, args.Asset.Id, exception);
        }
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        workingCopies.AssetSynchronized -= OnAssetSynchronized;
        _started = false;
    }
}
