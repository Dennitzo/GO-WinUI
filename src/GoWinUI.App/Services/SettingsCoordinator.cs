using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.App.Services;

public sealed class SettingsCoordinator(ISettingsStore store) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettings Current { get; private set; } = new();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Current = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(
        Func<AppSettings, AppSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Current = update(Current);
            await store.SaveAsync(Current, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
