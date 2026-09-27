using System.Diagnostics;
using System.Globalization;
using System.Text;
using GoWinUI.Core.Extensions;

var protocolEncoding = new UTF8Encoding(
    encoderShouldEmitUTF8Identifier: false,
    throwOnInvalidBytes: true);
Console.InputEncoding = protocolEncoding;
Console.OutputEncoding = protocolEncoding;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] arguments)
{
    try
    {
        var configuration = HostConfiguration.Parse(ParseArguments(arguments));
        var firstLine = await ReadBoundedLineAsync(
            Console.In, ExtensionHostProtocol.MaximumLineLength, CancellationToken.None).ConfigureAwait(false);
        var hello = ExtensionHostProtocol.ParseLine(firstLine);
        var capabilities = configuration.EntrypointPath is null
            ? new[] { "lifecycle" }
            : new[]
            {
                ExtensionHostMessageTypes.InvokeAction,
                configuration.EntrypointKind == ExtensionEntrypointKind.Gateway
                    ? "gateway-sidecar"
                    : "desktop-extension",
                "lifecycle",
            };
        var ready = ExtensionHostProtocol.AcceptHello(
            hello, configuration.Identity, Environment.ProcessId, capabilities);
        await WriteMessageAsync(ready).ConfigureAwait(false);

        while (await ReadBoundedLineAsync(
            Console.In, ExtensionHostProtocol.MaximumLineLength, CancellationToken.None).ConfigureAwait(false) is { } line)
        {
            ExtensionHostMessage message;
            try { message = ExtensionHostProtocol.ParseLine(line); }
            catch (InvalidDataException exception)
            {
                await WriteMessageAsync(Error("invalid_message", exception.Message, "invalid")).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(message.Type, ExtensionHostMessageTypes.Shutdown, StringComparison.Ordinal))
            {
                await WriteMessageAsync(ExtensionHostProtocol.Create(
                    ExtensionHostMessageTypes.Stopped, message.RequestId, new { reason = "shutdown" })).ConfigureAwait(false);
                return 0;
            }
            if (!string.Equals(message.Type, ExtensionHostMessageTypes.InvokeAction, StringComparison.Ordinal))
            {
                await WriteMessageAsync(Error(
                    "unsupported_message", $"Nachrichtentyp '{message.Type}' wird von diesem Host nicht unterstützt.", message.RequestId)).ConfigureAwait(false);
                continue;
            }

            try
            {
                if (configuration.EntrypointPath is null || configuration.ContentRoot is null)
                    throw new InvalidOperationException("Für diesen ExtensionHost ist kein Sidecar-Entrypoint konfiguriert.");
                var invocation = ExtensionHostProtocol.ReadPayload<ExtensionActionInvocation>(message);
                var separator = invocation.ActionId.IndexOf('/');
                if (separator <= 0
                    || !string.Equals(invocation.ActionId[..separator], configuration.Identity.ExtensionId, StringComparison.Ordinal))
                    throw new InvalidDataException("Die Action-ID gehört nicht zur gestarteten Extension.");
                ExtensionInvocationPolicy.Validate(
                    invocation, configuration.Permissions, configuration.WorkspaceRoot);
                var response = await InvokeEntrypointAsync(
                    configuration, message, CancellationToken.None).ConfigureAwait(false);
                await WriteMessageAsync(response).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException
                or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                await WriteMessageAsync(Error("action_failed", exception.Message, message.RequestId)).ConfigureAwait(false);
            }
        }
        return 0;
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException)
    {
        await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
        return 3;
    }
}

static async Task<ExtensionHostMessage> InvokeEntrypointAsync(
    HostConfiguration configuration,
    ExtensionHostMessage request,
    CancellationToken cancellationToken)
{
    var entrypoint = configuration.EntrypointPath
        ?? throw new InvalidOperationException("Extension-Entrypoint fehlt.");
    var contentRoot = configuration.ContentRoot
        ?? throw new InvalidOperationException("Extension-Inhaltsverzeichnis fehlt.");
    var isAssembly = string.Equals(Path.GetExtension(entrypoint), ".dll", StringComparison.OrdinalIgnoreCase);
    var protocolEncoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    var startInfo = new ProcessStartInfo
    {
        FileName = isAssembly ? "dotnet" : entrypoint,
        WorkingDirectory = contentRoot,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardInputEncoding = protocolEncoding,
        StandardOutputEncoding = protocolEncoding,
        StandardErrorEncoding = protocolEncoding,
    };
    if (isAssembly) startInfo.ArgumentList.Add(entrypoint);
    RestrictEnvironment(startInfo, configuration);
    using var process = new Process { StartInfo = startInfo };
    if (!process.Start()) throw new InvalidOperationException("Extension-Entrypoint konnte nicht gestartet werden.");

    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(configuration.ActionTimeout);
    var stderrTask = ReadBoundedToEndAsync(process.StandardError, 64 * 1024, timeout.Token);
    try
    {
        await process.StandardInput.WriteLineAsync(ExtensionHostProtocol.SerializeLine(request)).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
        var line = await ReadBoundedLineAsync(
            process.StandardOutput, configuration.MaximumOutputBytes, timeout.Token).ConfigureAwait(false);
        if (line is null)
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            throw new InvalidOperationException($"Extension-Entrypoint endete ohne Antwort (Exitcode {process.ExitCode}): {stderr}".Trim());
        }
        var response = ExtensionHostProtocol.ParseLine(line);
        if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal)
            || response.Type is not (ExtensionHostMessageTypes.ActionResult or ExtensionHostMessageTypes.Error))
            throw new InvalidDataException("Extension-Entrypoint lieferte keine passende action.result- oder host.error-Antwort.");
        return response;
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        throw new InvalidOperationException("Extension-Aktion hat das konfigurierte Zeitlimit überschritten.");
    }
    finally
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
    }
}

static void RestrictEnvironment(ProcessStartInfo startInfo, HostConfiguration configuration)
{
    var inherited = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "PATH", "DOTNET_ROOT", "DOTNET_HOST_PATH" })
        inherited[name] = Environment.GetEnvironmentVariable(name);
    startInfo.Environment.Clear();
    foreach (var (name, value) in inherited)
        if (!string.IsNullOrWhiteSpace(value)) startInfo.Environment[name] = value;
    startInfo.Environment["ASSISTANT_EXTENSION_ID"] = configuration.Identity.ExtensionId;
    startInfo.Environment["ASSISTANT_EXTENSION_PACKAGE_SHA256"] = configuration.Identity.PackageSha256;
    startInfo.Environment["ASSISTANT_EXTENSION_PERMISSIONS"] = string.Join(',', configuration.Permissions);
    if (configuration.WorkspaceRoot is not null)
        startInfo.Environment["ASSISTANT_EXTENSION_WORKSPACE_ROOT"] = configuration.WorkspaceRoot;
}

static async Task WriteMessageAsync(ExtensionHostMessage message)
{
    await Console.Out.WriteLineAsync(ExtensionHostProtocol.SerializeLine(message)).ConfigureAwait(false);
    await Console.Out.FlushAsync().ConfigureAwait(false);
}

static ExtensionHostMessage Error(string code, string message, string requestId) =>
    ExtensionHostProtocol.Create(
        ExtensionHostMessageTypes.Error,
        requestId,
        new ExtensionHostError(code, message));

static async Task<string?> ReadBoundedLineAsync(
    TextReader reader,
    int maximumBytes,
    CancellationToken cancellationToken)
{
    var builder = new StringBuilder(Math.Min(maximumBytes, 4_096));
    var buffer = new char[1];
    var byteCount = 0;
    while (true)
    {
        var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (count == 0) return builder.Length == 0 ? null : builder.ToString();
        if (buffer[0] == '\n') return builder.ToString().TrimEnd('\r');
        byteCount = checked(byteCount + Encoding.UTF8.GetByteCount(buffer));
        if (byteCount > maximumBytes) throw new InvalidDataException("ExtensionHost-Nachricht überschreitet das Ausgabelimit.");
        builder.Append(buffer[0]);
    }
}

static async Task<string> ReadBoundedToEndAsync(
    TextReader reader,
    int maximumCharacters,
    CancellationToken cancellationToken)
{
    var builder = new StringBuilder(Math.Min(maximumCharacters, 4_096));
    var buffer = new char[4_096];
    while (true)
    {
        var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (count == 0) return builder.ToString().Trim();
        var remaining = maximumCharacters - builder.Length;
        if (remaining > 0) builder.Append(buffer, 0, Math.Min(count, remaining));
    }
}

static Dictionary<string, string> ParseArguments(IReadOnlyList<string> arguments)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < arguments.Count; index += 2)
    {
        if (index + 1 >= arguments.Count || !arguments[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("ExtensionHost-Argumente müssen als --name wert übergeben werden.", nameof(arguments));
        var key = arguments[index][2..];
        if (!result.TryAdd(key, arguments[index + 1]))
            throw new ArgumentException($"ExtensionHost-Argument '{key}' wurde mehrfach angegeben.", nameof(arguments));
    }
    return result;
}

internal sealed record HostConfiguration(
    ExtensionHostIdentity Identity,
    string? ContentRoot,
    string? EntrypointPath,
    ExtensionEntrypointKind? EntrypointKind,
    ExtensionPermissionKind[] Permissions,
    string? WorkspaceRoot,
    TimeSpan ActionTimeout,
    int MaximumOutputBytes)
{
    internal static HostConfiguration Parse(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("extension-id", out var extensionId)
            || !ExtensionIdentifiers.TryValidateExtensionId(extensionId, out _)
            || !values.TryGetValue("package-sha256", out var packageSha256)
            || !ExtensionHashes.IsSha256(packageSha256))
            throw new ArgumentException("ExtensionHost benötigt gültige --extension-id und --package-sha256 Argumente.", nameof(values));

        values.TryGetValue("content-root", out var contentRootText);
        values.TryGetValue("entrypoint", out var entrypointText);
        if ((contentRootText is null) != (entrypointText is null))
            throw new ArgumentException("--content-root und --entrypoint müssen gemeinsam angegeben werden.", nameof(values));
        string? contentRoot = null;
        string? entrypointPath = null;
        ExtensionEntrypointKind? entrypointKind = null;
        if (contentRootText is not null && entrypointText is not null)
        {
            contentRoot = Path.GetFullPath(contentRootText);
            if (!Directory.Exists(contentRoot)) throw new DirectoryNotFoundException("Extension-Inhaltsverzeichnis fehlt.");
            if (!ExtensionPathRules.TryNormalizeRelativePath(entrypointText, out var relative, out var error)
                || relative.EndsWith('/')) throw new InvalidDataException(error);
            entrypointPath = ExtensionPathRules.ResolveInside(contentRoot, relative);
            if (!File.Exists(entrypointPath)) throw new FileNotFoundException("Extension-Entrypoint fehlt.", entrypointPath);
            if (!values.TryGetValue("entrypoint-kind", out var entrypointKindText)
                || !Enum.TryParse(entrypointKindText, ignoreCase: false, out ExtensionEntrypointKind parsedKind)
                || !Enum.IsDefined(parsedKind))
                throw new InvalidDataException("--entrypoint-kind fehlt oder ist ungültig.");
            entrypointKind = parsedKind;
        }

        var permissions = ParsePermissions(values.GetValueOrDefault("permissions"));
        var workspaceRoot = values.TryGetValue("workspace-root", out var workspaceText)
            ? Path.GetFullPath(workspaceText)
            : null;
        var timeoutMilliseconds = ParseInt64(values.GetValueOrDefault("action-timeout-ms"), 60_000, 1, 3_600_000, "action-timeout-ms");
        var maximumOutputBytes = checked((int)ParseInt64(values.GetValueOrDefault("maximum-output-bytes"),
            1024 * 1024, 1_024, 64L * 1024 * 1024, "maximum-output-bytes"));
        return new(
            new(extensionId, packageSha256), contentRoot, entrypointPath, entrypointKind, permissions, workspaceRoot,
            TimeSpan.FromMilliseconds(timeoutMilliseconds), maximumOutputBytes);
    }

    private static ExtensionPermissionKind[] ParsePermissions(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var permissions = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => Enum.TryParse<ExtensionPermissionKind>(item, ignoreCase: false, out var permission)
                && Enum.IsDefined(permission)
                    ? permission
                    : throw new InvalidDataException($"Unbekannte Extension-Berechtigung '{item}'."))
            .ToArray();
        if (permissions.Distinct().Count() != permissions.Length)
            throw new InvalidDataException("Extension-Berechtigungen enthalten Duplikate.");
        return permissions;
    }

    private static long ParseInt64(string? value, long fallback, long minimum, long maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum || parsed > maximum)
            throw new InvalidDataException($"--{name} liegt außerhalb des erlaubten Bereichs.");
        return parsed;
    }
}
