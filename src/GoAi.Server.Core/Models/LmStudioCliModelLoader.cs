using System.Diagnostics;
using System.Text.Json;
using GoAi.Server.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GoAi.Server.Core.Models;

/// <summary>
/// Loads exclusive coding models through the LM Studio CLI. The REST load API
/// does not expose a weight-offload setting, while <c>lms load --gpu max</c>
/// provides the deterministic full-GPU profile required by the coding lane.
/// </summary>
public sealed partial class LmStudioCliModelLoader
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(30);
    private readonly GoAiServerOptions _options;
    private readonly ILogger<LmStudioCliModelLoader> _logger;
    private string MarkerPath => Path.Combine(_options.DataDirectory, "Config", "coding-gpu-load.json");

    public LmStudioCliModelLoader(
        IOptions<GoAiServerOptions> options,
        ILogger<LmStudioCliModelLoader> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsKnownMaximumGpuLoad(string modelId, int minimumContextLength)
    {
        try
        {
            if (!File.Exists(MarkerPath))
            {
                return false;
            }

            var marker = JsonSerializer.Deserialize<GpuLoadMarker>(File.ReadAllText(MarkerPath));
            if (marker is null
                || !string.Equals(marker.ModelId, modelId, StringComparison.OrdinalIgnoreCase)
                || marker.ContextLength < minimumContextLength)
            {
                return false;
            }

            using var process = Process.GetProcessById(marker.LlamaServerProcessId);
            return !process.HasExited
                && process.StartTime.ToUniversalTime().Ticks == marker.LlamaServerStartTimeUtcTicks;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException
                or InvalidOperationException)
        {
            return false;
        }
    }

    public async Task LoadWithMaximumGpuOffloadAsync(
        string modelId,
        int contextLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentOutOfRangeException.ThrowIfLessThan(contextLength, 2_048);

        var executable = ResolveExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in CreateLoadArguments(modelId, contextLength))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Die LM-Studio-CLI konnte nicht gestartet werden.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LoadTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await AwaitOutputAsync(standardOutput, standardError).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"Das Coding-Modell '{modelId}' konnte innerhalb von {LoadTimeout.TotalMinutes:N0} Minuten nicht mit maximalem GPU-Offload geladen werden.");
        }

        var (output, error) = await AwaitOutputAsync(standardOutput, standardError).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            ClearMarker();
            var detail = LastMeaningfulLine(error) ?? LastMeaningfulLine(output) ?? "Unbekannter LM-Studio-CLI-Fehler.";
            throw new InvalidOperationException(
                $"Das Coding-Modell '{modelId}' konnte nicht mit maximalem GPU-Offload geladen werden: {detail}");
        }

        var llamaServer = FindNewestLlamaServerProcess()
            ?? throw new InvalidOperationException(
                $"LM Studio meldete das Coding-Modell '{modelId}' als geladen, aber es wurde kein llama-server-Prozess gefunden.");
        using (llamaServer)
        {
            await SaveMarkerAsync(
                new GpuLoadMarker(
                    modelId,
                    contextLength,
                    llamaServer.Id,
                    llamaServer.StartTime.ToUniversalTime().Ticks,
                    DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }

        LogCodingModelGpuLoaded(modelId, contextLength);
    }

    public void ClearMarker()
    {
        try
        {
            if (File.Exists(MarkerPath))
            {
                File.Delete(MarkerPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogGpuLoadMarkerRemovalFailed(exception);
        }
    }

    internal static IReadOnlyList<string> CreateLoadArguments(string modelId, int contextLength) =>
    [
        "load",
        modelId,
        "--gpu",
        "max",
        "--context-length",
        contextLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--parallel",
        "1",
        "--identifier",
        modelId,
        "--yes",
    ];

    private static string ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("GO_AI_LMS_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var userProfileCandidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".lmstudio",
            "bin",
            "lms.exe");
        if (File.Exists(userProfileCandidate))
        {
            return userProfileCandidate;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, "lms.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "Die LM-Studio-CLI wurde nicht gefunden. Erwartet wurde lms.exe unter %USERPROFILE%\\.lmstudio\\bin oder GO_AI_LMS_CLI_PATH.");
    }

    private async Task SaveMarkerAsync(GpuLoadMarker marker, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
        var temporary = MarkerPath + ".tmp";
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(marker),
            cancellationToken).ConfigureAwait(false);
        File.Move(temporary, MarkerPath, true);
    }

    private static Process? FindNewestLlamaServerProcess()
    {
        Process? newest = null;
        foreach (var process in Process.GetProcessesByName("llama-server"))
        {
            try
            {
                if (newest is null || process.StartTime > newest.StartTime)
                {
                    newest?.Dispose();
                    newest = process;
                }
                else
                {
                    process.Dispose();
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                process.Dispose();
            }
        }

        return newest;
    }

    private static async Task<(string Output, string Error)> AwaitOutputAsync(
        Task<string> standardOutput,
        Task<string> standardError)
    {
        await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        return (standardOutput.Result, standardError.Result);
    }

    private static string? LastMeaningfulLine(string value) => value
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .LastOrDefault(static line => !string.IsNullOrWhiteSpace(line));

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process may have exited between the state check and Kill().
        }
    }

    private sealed record GpuLoadMarker(
        string ModelId,
        int ContextLength,
        int LlamaServerProcessId,
        long LlamaServerStartTimeUtcTicks,
        DateTimeOffset LoadedAt);

    [LoggerMessage(
        EventId = 3120,
        Level = LogLevel.Information,
        Message = "Coding model {modelId} was loaded through LM Studio with maximum GPU offload and {contextLength} context tokens.")]
    private partial void LogCodingModelGpuLoaded(string modelId, int contextLength);

    [LoggerMessage(
        EventId = 3121,
        Level = LogLevel.Warning,
        Message = "The persisted coding GPU-load marker could not be removed.")]
    private partial void LogGpuLoadMarkerRemovalFailed(Exception exception);
}
