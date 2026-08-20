using System.Text.Json;
using System.Text.Json.Serialization;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.Infrastructure.Settings;

public sealed class JsonSettingsStore : ISettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    public JsonSettingsStore(GoInfrastructureOptions options) => SettingsPath = Path.Combine(options.DataDirectory, options.SettingsFileName);
    public string SettingsPath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            try
            {
                await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                return Normalize(settings ?? new AppSettings());
            }
            catch (JsonException)
            {
                return new AppSettings();
            }
            catch (IOException)
            {
                return new AppSettings();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = SettingsPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath) ?? throw new InvalidOperationException("Ungültiger Einstellungspfad."));
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, Normalize(settings), JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            _gate.Release();
            try { File.Delete(temporaryPath); } catch (IOException) { }
        }
    }

    public void Dispose() => _gate.Dispose();

    private static AppSettings Normalize(AppSettings settings)
    {
        var baseUrl = settings.LmStudioBaseUrl.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            baseUrl = "http://127.0.0.1:1234/v1";
        var goAiServerUrl = settings.GoAiServerUrl.Trim();
        if (!Uri.TryCreate(goAiServerUrl, UriKind.Absolute, out var goAiUri)
            || goAiUri.Scheme is not ("http" or "https")
            || (goAiUri.Scheme == "http" && !goAiUri.IsLoopback))
        {
            goAiServerUrl = "https://192.168.0.67:8443";
        }
        var window = settings.Window with
        {
            Width = Math.Clamp(settings.Window.Width, 640, 10_000),
            Height = Math.Clamp(settings.Window.Height, 480, 10_000),
            SavedDpi = Math.Clamp(settings.Window.SavedDpi, 48, 768),
        };
        var accentColor = NormalizePaletteColor(settings.AccentColor, AppSettings.DefaultAccentColor);
        var backgroundColor = settings.Version < 2
            ? accentColor
            : NormalizePaletteColor(settings.BackgroundColor, AppSettings.DefaultBackgroundColor);
        var lastActivityText = NormalizeActivityText(settings.LastActivityText);
        var lastActivityAt = lastActivityText is null ? null : settings.LastActivityAt;
        return settings with
        {
            Version = 9,
            GoAiServerUrl = goAiServerUrl.TrimEnd('/'),
            GoAiProtocolVersion = string.IsNullOrWhiteSpace(settings.GoAiProtocolVersion)
                ? "1.0"
                : settings.GoAiProtocolVersion.Trim(),
            GoAiCaFingerprint = NormalizeFingerprint(settings.GoAiCaFingerprint),
            GoAiConnectionName = string.IsNullOrWhiteSpace(settings.GoAiConnectionName)
                ? "GO AI Server"
                : settings.GoAiConnectionName.Trim(),
            LocalToolWorkspacePath = NormalizeWorkspace(settings.LocalToolWorkspacePath),
            LiveCaptionLanguage = settings.Version < 5
                && string.Equals(settings.LiveCaptionLanguage, "de", StringComparison.OrdinalIgnoreCase)
                    ? "auto"
                    : string.IsNullOrWhiteSpace(settings.LiveCaptionLanguage)
                        ? "auto"
                        : settings.LiveCaptionLanguage.Trim(),
            LmStudioBaseUrl = baseUrl.TrimEnd('/'),
            SelectedModel = string.IsNullOrWhiteSpace(settings.SelectedModel)
                ? AppSettings.DefaultSelectedModel
                : settings.SelectedModel.Trim(),
            SelectedCodingModel = settings.Version < 9 || string.IsNullOrWhiteSpace(settings.SelectedCodingModel)
                ? AppSettings.DefaultSelectedCodingModel
                : settings.SelectedCodingModel.Trim(),
            AccentColor = accentColor,
            BackgroundColor = backgroundColor,
            NavigationPaneWidth = Math.Clamp(settings.NavigationPaneWidth, 280, 520),
            Language = string.IsNullOrWhiteSpace(settings.Language) ? "de-DE" : settings.Language,
            LastRoute = string.IsNullOrWhiteSpace(settings.LastRoute) ? "assistant" : settings.LastRoute,
            LastActivityText = lastActivityAt is null ? null : lastActivityText,
            LastActivityAt = lastActivityAt,
            Window = window,
        };
    }

    private static string? NormalizeFingerprint(string? value)
    {
        var normalized = new string((value ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        return normalized.Length == 64 ? normalized : null;
    }

    private static string? NormalizeWorkspace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        try
        {
            return Path.GetFullPath(value.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string NormalizePaletteColor(string? value, string fallback)
    {
        var candidate = value?.Trim();
        return candidate is { Length: 7 }
               && candidate[0] == '#'
               && candidate.Skip(1).All(Uri.IsHexDigit)
            ? candidate.ToUpperInvariant()
            : fallback;
    }

    private static string? NormalizeActivityText(string? value)
    {
        var normalized = string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
        {
            return null;
        }

        return normalized.Length <= AppSettings.MaximumRecentActivityTextLength
            ? normalized
            : string.Concat(normalized.AsSpan(0, AppSettings.MaximumRecentActivityTextLength - 1), "…");
    }
}
