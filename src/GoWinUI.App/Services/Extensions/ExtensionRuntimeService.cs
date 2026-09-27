using System.Collections.Concurrent;
using System.Text.Json;
using GoWinUI.Core.Extensions;
using Microsoft.Extensions.Hosting;

namespace GoWinUI.App.Services.Extensions;

public interface IExtensionRuntimeService
{
    string? GetDisabledReason(string actionId, string? workspaceRoot);

    Task<JsonElement> InvokeAsync(
        string actionId,
        JsonElement arguments,
        string? workspaceRoot,
        CancellationToken cancellationToken = default);
}

public sealed class ExtensionRuntimeService(
    IAextPackageStore packageStore,
    IExtensionActionCatalog actionCatalog,
    IExtensionHostSupervisor hostSupervisor) : IExtensionRuntimeService, IHostedService
{
    private readonly ConcurrentDictionary<string, ActiveExtension> _actions = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _startupErrors = new();
    private string? _hostExecutable;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _hostExecutable = ResolveHostExecutable();
        try
        {
            var pending = await packageStore.GetActivationCandidatesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var candidate in pending)
            {
                try { _ = await packageStore.ActivateAsync(candidate, cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    _startupErrors.Enqueue($"{candidate.Manifest.Id}: {exception.Message}");
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _startupErrors.Enqueue(exception.Message);
        }

        IReadOnlyList<AextPackageInspection> active;
        try { active = await packageStore.GetActivePackagesAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _startupErrors.Enqueue(exception.Message);
            return;
        }

        foreach (var package in active
            .GroupBy(static item => item.Manifest.Id, StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(
                static item => ParseVersion(item.Manifest.Version)).First()))
        {
            try { Register(package); }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
            {
                _startupErrors.Enqueue($"{package.Manifest.Id}: {exception.Message}");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public string? GetDisabledReason(string actionId, string? workspaceRoot)
    {
        if (!_actions.TryGetValue(actionId, out var extension))
            return "Die Erweiterung ist nicht aktiv oder wurde beim Start abgelehnt.";
        if (_hostExecutable is null)
            return "ExtensionHost ist in dieser Installation nicht verfügbar.";
        if (!actionCatalog.TryGetAction(actionId, out var action) || action is null)
            return "Die Erweiterungsaktion ist nicht im Action-Katalog registriert.";
        if (SelectEntrypoint(extension, action) is null)
            return action.ActionKind == ExtensionActionKind.SelectableTool
                ? "Das Modellwerkzeug besitzt keinen Gateway- oder Desktop-Sidecar-Entrypoint."
                : "Die Aktion besitzt keinen Desktop-Entrypoint.";
        if (RequiresWorkspace(extension.Manifest.Permissions) && !IsExistingDirectory(workspaceRoot))
            return "Die Erweiterung benötigt einen geöffneten Workspace als Zugriffsgrenze.";
        return null;
    }

    public async Task<JsonElement> InvokeAsync(
        string actionId,
        JsonElement arguments,
        string? workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var disabledReason = GetDisabledReason(actionId, workspaceRoot);
        if (disabledReason is not null) throw new InvalidOperationException(disabledReason);
        var extension = _actions[actionId];
        var hostExecutable = _hostExecutable!;
        var action = actionCatalog.TryGetAction(actionId, out var registeredAction) && registeredAction is not null
            ? registeredAction
            : throw new InvalidOperationException("Die Erweiterungsaktion ist nicht registriert.");
        var entrypoint = SelectEntrypoint(extension, action)
            ?? throw new InvalidOperationException("Für die Erweiterungsaktion fehlt ein passender Sidecar-Entrypoint.");
        var normalizedWorkspace = string.IsNullOrWhiteSpace(workspaceRoot) ? null : Path.GetFullPath(workspaceRoot);
        var grant = new ExtensionPermissionGrant([.. extension.Manifest.Permissions], normalizedWorkspace);
        var invocation = new ExtensionActionInvocation(actionId, arguments.Clone(), grant);
        ExtensionInvocationPolicy.Validate(invocation, extension.Manifest.Permissions, normalizedWorkspace);
        var timeout = TimeSpan.FromSeconds(60);
        var maximumOutputBytes = 1024 * 1024;
        if (actionCatalog.TryGetTool(actionId, out var tool) && tool is not null)
        {
            timeout = TimeSpan.FromSeconds(tool.TimeoutSeconds);
            maximumOutputBytes = tool.MaximumOutputBytes;
        }

        await using var host = await hostSupervisor.StartAsync(new(
            hostExecutable,
            new(extension.Manifest.Id, extension.Package.PackageSha256),
            TimeSpan.FromSeconds(10),
            extension.ContentDirectory,
            entrypoint,
            [.. extension.Manifest.Permissions],
            normalizedWorkspace,
            timeout,
            maximumOutputBytes), cancellationToken).ConfigureAwait(false);
        using var actionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        actionTimeout.CancelAfter(timeout + TimeSpan.FromSeconds(5));
        var request = ExtensionHostProtocol.Create(
            ExtensionHostMessageTypes.InvokeAction, Guid.NewGuid().ToString("N"), invocation);
        var response = await host.ExchangeAsync(request, actionTimeout.Token).ConfigureAwait(false);
        if (string.Equals(response.Type, ExtensionHostMessageTypes.Error, StringComparison.Ordinal))
        {
            var error = ExtensionHostProtocol.ReadPayload<ExtensionHostError>(response);
            throw new InvalidOperationException($"Extension-Aktion fehlgeschlagen ({error.Code}): {error.Message}");
        }
        if (!string.Equals(response.Type, ExtensionHostMessageTypes.ActionResult, StringComparison.Ordinal))
            throw new InvalidDataException("ExtensionHost lieferte keine action.result-Antwort.");
        return ExtensionHostProtocol.ReadPayload<ExtensionActionResult>(response).Result.Clone();
    }

    private void Register(AextPackageInspection package)
    {
        if (package.SignatureVerification.Status != AextSignatureStatus.Verified)
            throw new InvalidDataException("Nur verifizierte Extension-Pakete dürfen registriert werden.");
        if (package.Manifest.Id.StartsWith("builtin.", StringComparison.Ordinal))
            throw new InvalidDataException("Installierbare Pakete dürfen den reservierten builtin-Namensraum nicht verwenden.");
        var desktopEntrypoint = package.Manifest.Entrypoints
            .SingleOrDefault(static item => item.Kind == ExtensionEntrypointKind.Desktop);
        var gatewayEntrypoint = package.Manifest.Entrypoints
            .SingleOrDefault(static item => item.Kind == ExtensionEntrypointKind.Gateway);
        var contentDirectory = Path.Combine(Path.GetDirectoryName(package.PackagePath)
            ?? throw new InvalidDataException("Aktiver Extension-Paketpfad besitzt kein Verzeichnis."), "content");
        var registration = new ActiveExtension(
            package, package.Manifest, contentDirectory, desktopEntrypoint, gatewayEntrypoint);
        actionCatalog.Register(package.Manifest);
        foreach (var action in package.Manifest.Actions)
            if (!_actions.TryAdd(action.ActionId, registration))
                throw new InvalidDataException($"Extension-Action '{action.ActionId}' ist bereits aktiv.");
    }

    private static Version ParseVersion(string semanticVersion)
    {
        var core = semanticVersion.Split(['-', '+'], 2)[0];
        return Version.TryParse(core, out var version) ? version : new Version(0, 0, 0);
    }

    private static bool RequiresWorkspace(IEnumerable<ExtensionPermissionKind> permissions) =>
        permissions.Any(static permission => permission is ExtensionPermissionKind.FileRead
            or ExtensionPermissionKind.FileWrite
            or ExtensionPermissionKind.WorkspaceRead
            or ExtensionPermissionKind.WorkspaceWrite);

    private static bool IsExistingDirectory(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(Path.GetFullPath(path));

    private static ExtensionEntrypointDescriptor? SelectEntrypoint(
        ActiveExtension extension,
        ExtensionActionDescriptor action) =>
        action.ActionKind == ExtensionActionKind.SelectableTool
            ? extension.GatewayEntrypoint ?? extension.DesktopEntrypoint
            : extension.DesktopEntrypoint;

    private static string? ResolveHostExecutable()
    {
        foreach (var path in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ExtensionHost", "ExtensionHost.exe"),
            Path.Combine(AppContext.BaseDirectory, "ExtensionHost", "ExtensionHost.dll"),
            Path.Combine(AppContext.BaseDirectory, "ExtensionHost.exe"),
            Path.Combine(AppContext.BaseDirectory, "ExtensionHost.dll"),
        })
            if (File.Exists(path)) return path;
        return null;
    }

    private sealed record ActiveExtension(
        AextPackageInspection Package,
        ExtensionManifest Manifest,
        string ContentDirectory,
        ExtensionEntrypointDescriptor? DesktopEntrypoint,
        ExtensionEntrypointDescriptor? GatewayEntrypoint);
}
