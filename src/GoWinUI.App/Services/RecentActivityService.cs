using GoWinUI.App.ViewModels;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace GoWinUI.App.Services;

public sealed class RecentActivityService(
    SettingsCoordinator settings,
    ShellViewModel shell,
    ILogger<RecentActivityService> logger)
{
    private DispatcherQueue? _dispatcherQueue;
    private static readonly Action<ILogger, Exception?> ActivityPersistenceFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(4100, nameof(ActivityPersistenceFailed)),
        "Die letzte Benutzeraktivität konnte nicht gespeichert werden.");

    public void Restore()
    {
        _dispatcherQueue ??= DispatcherQueue.GetForCurrentThread();
        shell.SetRecentActivity(settings.Current.LastActivityText, settings.Current.LastActivityAt);
    }

    public async Task RecordAsync(string description, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(description);
        var occurredAt = DateTimeOffset.UtcNow;

        try
        {
            await UpdateShellAsync(normalized, occurredAt);
            await settings.UpdateAsync(current => current with
            {
                LastActivityText = normalized,
                LastActivityAt = occurredAt,
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ActivityPersistenceFailed(logger, exception);
        }
    }

    private Task UpdateShellAsync(string description, DateTimeOffset occurredAt)
    {
        var dispatcherQueue = _dispatcherQueue;
        if (dispatcherQueue is null || dispatcherQueue.HasThreadAccess)
        {
            shell.SetRecentActivity(description, occurredAt);
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    shell.SetRecentActivity(description, occurredAt);
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            }))
        {
            // The app is shutting down. Persist the activity without touching a closed UI thread.
            completion.SetResult();
        }

        return completion.Task;
    }

    internal static string Normalize(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        var normalized = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= AppSettings.MaximumRecentActivityTextLength
            ? normalized
            : string.Concat(normalized.AsSpan(0, AppSettings.MaximumRecentActivityTextLength - 1), "…");
    }
}
