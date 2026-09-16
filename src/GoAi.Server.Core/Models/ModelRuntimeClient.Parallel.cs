using System.Text.Json;

namespace GoAi.Server.Core.Models;

public sealed partial class ModelRuntimeClient
{
    private string? _pairMain;
    private string? _pairSecondary;
    internal static bool IsSecondaryInstance(string id) => id.EndsWith("~secondary", StringComparison.Ordinal);
    private bool IsPairPeer(string selected, string other) =>
        (selected == _pairMain && other == _pairSecondary) || (selected == _pairSecondary && other == _pairMain);

    private static bool IsPairInstance(string id) => IsSecondaryInstance(id) || id.EndsWith("~main", StringComparison.Ordinal);

    private sealed class TurnLease(SemaphoreSlim first, SemaphoreSlim? second = null)
    {
        public void Release() { second?.Release(); first.Release(); }
    }

    private async Task<TurnLease> AcquireTurnAsync(string? modelId, CancellationToken token)
    {
        if (modelId is not null && IsSecondaryInstance(modelId))
        {
            await _secondaryTurnGate.WaitAsync(token).ConfigureAwait(false);
            return new TurnLease(_secondaryTurnGate);
        }
        await _turnGate.WaitAsync(token).ConfigureAwait(false);
        if (modelId is not null && IsPairInstance(modelId)) return new TurnLease(_turnGate);
        try
        {
            await _secondaryTurnGate.WaitAsync(token).ConfigureAwait(false);
            return new TurnLease(_turnGate, _secondaryTurnGate);
        }
        catch { _turnGate.Release(); throw; }
    }

    // Caller holds both turn lanes and model gate. This also recovers native pair
    // state after a gateway restart, where the local pair fields are empty.
    private async Task DisableNativePairAsync(string mainModel, IReadOnlyList<RuntimeModel> models, CancellationToken token)
    {
        if (_pairMain is null && !models.Any(m => IsPairInstance(m.Id))) return;
        foreach (var model in models.Where(m => m.State is "loaded" or "loading" or "sleeping"))
            await UnloadRuntimeInstanceAsync(model.Id, token).ConfigureAwait(false);
        var uri = new UriBuilder(_options.ModelRuntimeUri) { Port = _options.ModelRuntimeUri.Port + 1, Path = "/models/configure-pair", Query = "" }.Uri.AbsoluteUri;
        using var response = await SendJsonAsync(HttpMethod.Post, uri, new { mainModel, secondaryModel = (string?)null }, token, bufferContent: true).ConfigureAwait(false);
        _pairMain = null; _pairSecondary = null;
        InvalidateStatus();
    }

    internal async Task DisablePairAsync(string mainModel, CancellationToken token)
    {
        var lease = await AcquireTurnAsync(null, token).ConfigureAwait(false);
        try
        {
            await _modelGate.WaitAsync(token).ConfigureAwait(false);
            try { await DisableNativePairAsync(mainModel, await GetRuntimeModelsAsync(token).ConfigureAwait(false), token).ConfigureAwait(false); }
            finally { _modelGate.Release(); }
        }
        finally { lease.Release(); }
    }

    internal async Task<(string Main, string Secondary)> ConfigurePairAsync(string main, string secondary, CancellationToken cancellationToken)
    {
        // A parent resumes after each client tool while its child may still be
        // generating. Validate residency without waiting on that child's lane.
        var live = await GetRuntimeModelsAsync(cancellationToken).ConfigureAwait(false);
        if (live.Any(m => m.Id == main + "~main") && live.Any(m => m.Id == secondary + "~secondary"))
        {
            _pairMain = main + "~main";
            _pairSecondary = secondary + "~secondary";
            return (_pairMain, _pairSecondary);
        }
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _secondaryTurnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var installed = await GetRuntimeModelsAsync(cancellationToken).ConfigureAwait(false);
                    var sameNativePair = installed.Any(m => m.Id == main + "~main") && installed.Any(m => m.Id == secondary + "~secondary");
                    if (!sameNativePair)
                        foreach (var model in installed)
                            if (model.State is "loaded" or "loading" or "sleeping")
                                await UnloadRuntimeInstanceAsync(model.Id, cancellationToken).ConfigureAwait(false);
                    var uri = new UriBuilder(_options.ModelRuntimeUri) { Port = _options.ModelRuntimeUri.Port + 1, Path = "/models/configure-pair", Query = "" }.Uri.AbsoluteUri;
                    using var response = await SendJsonAsync(HttpMethod.Post, uri, new { mainModel = main, secondaryModel = secondary }, cancellationToken, bufferContent: true).ConfigureAwait(false);
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                    _pairMain = json.RootElement.GetProperty("mainAlias").GetString()!;
                    _pairSecondary = json.RootElement.GetProperty("secondaryAlias").GetString()!;
                    InvalidateStatus();
                    return (_pairMain, _pairSecondary);
                }
                finally { _modelGate.Release(); }
            }
            finally { _secondaryTurnGate.Release(); }
        }
        finally { _turnGate.Release(); }
    }
}
