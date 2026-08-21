using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GoWinUI.App.Services;

/// <summary>
/// Creates an A4 book-layout PDF next to a coding-workflow solution by using the
/// same Markdown, KaTeX and print styles as the assistant WebView.
/// </summary>
public sealed partial class CodingSolutionPdfExporter(ILogger<CodingSolutionPdfExporter> logger) : IDisposable
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".tex", ".json",
    };
    private readonly SemaphoreSlim _exportGate = new(1, 1);

    public async Task<string?> EnsureCurrentAsync(
        string sourcePath,
        bool sourceChanged,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var source = Path.GetFullPath(sourcePath);
        if (!SupportedExtensions.Contains(Path.GetExtension(source)) || !File.Exists(source))
        {
            return null;
        }

        var output = Path.ChangeExtension(source, ".pdf");
        if (!sourceChanged && File.Exists(output) && new FileInfo(output).Length >= 1024)
        {
            return output;
        }

        await _exportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!sourceChanged && File.Exists(output) && new FileInfo(output).Length >= 1024)
            {
                return output;
            }

            var script = Path.Combine(AppContext.BaseDirectory, "Assets", "Scripts", "export-coding-solution.ps1");
            var webAssets = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
            if (!File.Exists(script) || !Directory.Exists(webAssets))
            {
                throw new FileNotFoundException("Die lokalen GO-Ressourcen für den Lösungs-PDF-Export fehlen.", script);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            string[] arguments =
            [
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", script,
                "-SourcePath", source,
                "-WebAssetsPath", webAssets,
                "-OutputPath", output,
            ];
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("Der Lösungs-PDF-Export konnte nicht gestartet werden.");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException("Die PDF-Erzeugung hat das Zeitlimit von drei Minuten überschritten.");
            }

            var outputText = await standardOutput.ConfigureAwait(false);
            var errorText = await standardError.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length < 1024)
            {
                var detail = LastMeaningfulLine(errorText) ?? LastMeaningfulLine(outputText) ?? "Unbekannter Exportfehler.";
                throw new InvalidOperationException($"Die Lösungs-PDF konnte nicht erzeugt werden: {detail}");
            }

            LogSolutionPdfCreated(output);
            return output;
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private static string? LastMeaningfulLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    public void Dispose() => _exportGate.Dispose();

    [LoggerMessage(EventId = 5820, Level = LogLevel.Information, Message = "Coding solution PDF created at {Path}.")]
    private partial void LogSolutionPdfCreated(string path);
}
