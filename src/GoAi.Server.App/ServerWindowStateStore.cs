using GoAi.Server.Core.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace GoAi.Server.App;

public sealed class ServerWindowStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _statePath;
    private readonly string _legacyStatePath;

    public ServerWindowStateStore(IOptions<GoAiServerOptions> options)
    {
        _statePath = Path.Combine(options.Value.DataDirectory, "Config", "window-state.json");
        _legacyStatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GO-AI-Server",
            "window-state.json");
    }

    internal async Task<ServerWindowState?> LoadAsync()
    {
        try
        {
            var sourcePath = File.Exists(_statePath)
                ? _statePath
                : File.Exists(_legacyStatePath)
                    ? _legacyStatePath
                    : null;
            if (sourcePath is null)
            {
                return null;
            }

            ServerWindowState? state;
            await using (var stream = File.OpenRead(sourcePath))
            {
                state = await JsonSerializer.DeserializeAsync<ServerWindowState>(stream, SerializerOptions);
            }

            if (state is not null && string.Equals(sourcePath, _legacyStatePath, StringComparison.OrdinalIgnoreCase))
            {
                await SaveAsync(state);
            }

            return state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal async Task SaveAsync(ServerWindowState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _statePath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, SerializerOptions);
            }

            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Window state is optional. A locked profile must never prevent the dashboard from closing.
        }
    }
}

internal sealed class ServerWindowState
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool IsMaximized { get; set; }

    public bool IsPaneOpen { get; set; } = true;

    public double PaneWidth { get; set; } = 250;
}
