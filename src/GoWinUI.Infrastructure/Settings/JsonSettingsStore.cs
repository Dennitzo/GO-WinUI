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
        var window = settings.Window with
        {
            Width = Math.Clamp(settings.Window.Width, 640, 10_000),
            Height = Math.Clamp(settings.Window.Height, 480, 10_000),
            SavedDpi = Math.Clamp(settings.Window.SavedDpi, 48, 768),
        };
        return settings with
        {
            Version = 1,
            LmStudioBaseUrl = baseUrl.TrimEnd('/'),
            NavigationPaneWidth = Math.Clamp(settings.NavigationPaneWidth, 280, 520),
            Language = string.IsNullOrWhiteSpace(settings.Language) ? "de-DE" : settings.Language,
            LastRoute = string.IsNullOrWhiteSpace(settings.LastRoute) ? "assistant" : settings.LastRoute,
            Window = window,
        };
    }
}
