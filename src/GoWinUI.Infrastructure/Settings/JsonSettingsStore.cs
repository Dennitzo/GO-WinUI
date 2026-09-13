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
        Converters = { new GatewayProviderConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
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
        var goAiServerUrl = (settings.GoAiServerUrl ?? string.Empty).Trim();
        if (!Uri.TryCreate(goAiServerUrl, UriKind.Absolute, out var goAiUri)
            || goAiUri.Scheme is not ("http" or "https"))
        {
            goAiServerUrl = "http://192.168.0.67:8080";
        }
        else if (settings.Version < 13
                 && string.Equals(goAiUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                 && goAiUri.Port == 8443)
        {
            var dockerGateway = new UriBuilder(goAiUri)
            {
                Scheme = Uri.UriSchemeHttp,
                Port = 8080,
            };
            goAiServerUrl = dockerGateway.Uri.ToString();
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
            Version = AppSettings.CurrentVersion,
            AiProvider = AiProviderKind.GoAiServer,
            IsAiConnectionEnabled = settings.Version < 13
                || settings.IsAiConnectionEnabled,
            GoAiServerUrl = goAiServerUrl.TrimEnd('/'),
            GoAiProtocolVersion = string.IsNullOrWhiteSpace(settings.GoAiProtocolVersion)
                ? "1.0"
                : settings.GoAiProtocolVersion.Trim(),
            LiveCaptionLanguage = settings.Version < 5
                && string.Equals(settings.LiveCaptionLanguage, "de", StringComparison.OrdinalIgnoreCase)
                    ? "auto"
                    : string.IsNullOrWhiteSpace(settings.LiveCaptionLanguage)
                        ? "auto"
                        : settings.LiveCaptionLanguage.Trim(),
            SelectedModel = NormalizeGeneralModel(settings.SelectedModel),
            SelectedCodingModel = string.IsNullOrWhiteSpace(settings.SelectedCodingModel) ? null : settings.SelectedCodingModel.Trim(),
            CodingWorkspacePath = string.IsNullOrWhiteSpace(settings.CodingWorkspacePath) ? null : settings.CodingWorkspacePath.Trim(),
            ReasoningEffort = NormalizeReasoningEffort(settings.Version, settings.ReasoningEffort),
            ReasoningEffortsByModel = (settings.ReasoningEffortsByModel ?? [])
                .Where(item => item.Key.Length <= 1024 && !item.Key.Any(char.IsControl)
                    && item.Value is { Length: > 0 and <= 32 } && item.Value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                .GroupBy(item => item.Key.ToLowerInvariant()).ToDictionary(group => group.Key, group => group.Last().Value.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase),
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

    private static string NormalizeGeneralModel(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || string.Equals(normalized, "openai/gpt-oss-120b", StringComparison.OrdinalIgnoreCase))
        {
            return AppSettings.DefaultSelectedModel;
        }

        return NormalizeModelId(normalized, AppSettings.DefaultSelectedModel);
    }

    private static string NormalizeModelId(string? value, string fallback)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
               || normalized.Length > 512
               || normalized.Any(char.IsControl)
            ? fallback
            : normalized;
    }

    private static string NormalizeReasoningEffort(int settingsVersion, string? value)
    {
        _ = settingsVersion;
        _ = value;
        return "auto";
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

    private sealed class GatewayProviderConverter : JsonConverter<AiProviderKind>
    {
        public override AiProviderKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // Older files used LM Studio provider names or numeric enum values.
            // There is now one connection route; retain every other user setting.
            if (reader.TokenType is JsonTokenType.String or JsonTokenType.Number)
            {
                return AiProviderKind.GoAiServer;
            }
            throw new JsonException("Der AI-Provider muss ein Name oder ein numerischer Altwert sein.");
        }

        public override void Write(Utf8JsonWriter writer, AiProviderKind value, JsonSerializerOptions options) =>
            writer.WriteStringValue("goAiServer");
    }
}
