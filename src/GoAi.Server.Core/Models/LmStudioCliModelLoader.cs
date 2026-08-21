using System.Diagnostics;
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
    // Keep a finite safety boundary for a wedged CLI process, but do not cut off
    // legitimately slow multi-GPU loads at the former 30-minute boundary.
    internal static readonly TimeSpan LoadTimeout = TimeSpan.FromHours(4);
    private readonly ILogger<LmStudioCliModelLoader> _logger;

    public LmStudioCliModelLoader(
        IOptions<GoAiServerOptions> options,
        ILogger<LmStudioCliModelLoader> logger)
    {
        _ = options;
        _logger = logger;
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
            var detail = LastMeaningfulLine(error) ?? LastMeaningfulLine(output) ?? "Unbekannter LM-Studio-CLI-Fehler.";
            throw new InvalidOperationException(
                $"Das Coding-Modell '{modelId}' konnte nicht mit maximalem GPU-Offload geladen werden: {detail}");
        }

        LogCodingModelGpuLoaded(modelId, contextLength);
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

    [LoggerMessage(
        EventId = 3120,
        Level = LogLevel.Information,
        Message = "Coding model {modelId} was loaded through LM Studio with maximum GPU offload and {contextLength} context tokens.")]
    private partial void LogCodingModelGpuLoaded(string modelId, int contextLength);

}
