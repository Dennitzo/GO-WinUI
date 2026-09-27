using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using GoWinUI.Core.Extensions;

if (args is ["--child-wait"])
{
    await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
    return 0;
}

var line = await Console.In.ReadLineAsync().ConfigureAwait(false);
if (line is null) return 2;

try
{
    var request = ExtensionHostProtocol.ParseLine(line);
    if (!string.Equals(request.Type, ExtensionHostMessageTypes.InvokeAction, StringComparison.Ordinal))
        throw new InvalidDataException("Die Test-Extension erwartet action.invoke.");
    var invocation = ExtensionHostProtocol.ReadPayload<ExtensionActionInvocation>(request);
    var childStarted = false;
    var childStillRunning = false;
    int? childProcessId = null;
    string? childStartError = null;
    if (invocation.Arguments.TryGetProperty("spawnChild", out var spawnChild)
        && spawnChild.ValueKind == JsonValueKind.True)
    {
        try
        {
            var assemblyPath = typeof(ExtensionHostTestFixtureMarker).Assembly.Location;
            var childInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            childInfo.ArgumentList.Add(assemblyPath);
            childInfo.ArgumentList.Add("--child-wait");
            using var child = Process.Start(childInfo)
                ?? throw new InvalidOperationException("Der Test-Kindprozess konnte nicht gestartet werden.");
            childStarted = true;
            childProcessId = child.Id;
            if (child.WaitForExit(500))
                childStartError = $"Der Test-Kindprozess wurde sofort beendet (Exitcode {child.ExitCode}).";
            else
                childStillRunning = true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            childStartError = exception.Message;
        }
    }
    var response = ExtensionHostProtocol.Create(
        ExtensionHostMessageTypes.ActionResult,
        request.RequestId,
        new ExtensionActionResult(JsonSerializer.SerializeToElement(new
        {
            actionId = invocation.ActionId,
            arguments = invocation.Arguments,
            permissions = invocation.Grant.Permissions,
            workspaceRoot = invocation.Grant.WorkspaceRoot,
            childStarted,
            childStillRunning,
            childProcessId,
            childStartError,
        })));
    await Console.Out.WriteLineAsync(ExtensionHostProtocol.SerializeLine(response)).ConfigureAwait(false);
    await Console.Out.FlushAsync().ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is InvalidDataException or JsonException)
{
    var response = ExtensionHostProtocol.Create(
        ExtensionHostMessageTypes.Error,
        "fixture-error",
        new ExtensionHostError("fixture_error", exception.Message));
    await Console.Out.WriteLineAsync(ExtensionHostProtocol.SerializeLine(response)).ConfigureAwait(false);
    await Console.Out.FlushAsync().ConfigureAwait(false);
    return 3;
}

internal sealed class ExtensionHostTestFixtureMarker
{
}
