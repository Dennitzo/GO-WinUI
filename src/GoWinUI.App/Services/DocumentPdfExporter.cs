using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GoWinUI.App.Services;

/// <summary>
/// Creates an A4 book-layout PDF for a generated session document by using the
/// same Markdown, KaTeX and print styles as the assistant WebView.
/// </summary>
public sealed partial class DocumentPdfExporter(ILogger<DocumentPdfExporter> logger) : IDisposable
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

            var script = ApplicationAssets.ResolvePath("Assets", "Scripts", "export-document.ps1");
            var webAssets = ApplicationAssets.ResolvePath("Assets", "Web");
            if (!File.Exists(script) || !Directory.Exists(webAssets))
            {
                throw new FileNotFoundException("Die lokalen GO-Ressourcen für den Dokument-PDF-Export fehlen.", script);
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
                throw new InvalidOperationException("Der Dokument-PDF-Export konnte nicht gestartet werden.");
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
                if (!detail.Contains("KaTeX", StringComparison.OrdinalIgnoreCase)
                    && SourceLikelyContainsMathematics(source))
                {
                    detail += " KaTeX-Pruefung oder mathematisches Rendering wurde nicht erfolgreich abgeschlossen.";
                }
                throw new InvalidOperationException($"Die Dokument-PDF konnte nicht erzeugt werden: {detail}");
            }

            LogDocumentPdfCreated(output);
            return output;
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private static string? LastMeaningfulLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

    private static bool SourceLikelyContainsMathematics(string sourcePath)
    {
        try
        {
            var text = File.ReadAllText(sourcePath);
            return text.Contains('$', StringComparison.Ordinal)
                || text.Contains("\\(", StringComparison.Ordinal)
                || text.Contains("\\[", StringComparison.Ordinal)
                || text.Contains("\\begin{", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

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

    [LoggerMessage(EventId = 5820, Level = LogLevel.Information, Message = "Document PDF created at {Path}.")]
    private partial void LogDocumentPdfCreated(string path);
}
