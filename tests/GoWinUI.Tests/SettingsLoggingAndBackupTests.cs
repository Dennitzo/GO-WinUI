using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging;

namespace GoWinUI.Tests;

public sealed class SettingsLoggingAndBackupTests
{
    private static readonly Action<ILogger, string, string, Exception?> SensitiveLog = LoggerMessage.Define<string, string>(
        LogLevel.Information, new EventId(9000, nameof(SensitiveLog)), "Prompt {Prompt} für Modell {Model}");
    [Fact]
    public async Task SettingsAreAtomicAndNormalized()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var settings = environment.Get<ISettingsStore>();
        await settings.SaveAsync(new AppSettings
        {
            LmStudioBaseUrl = "http://localhost:1234/v1/",
            NavigationPaneWidth = 999,
            Window = new(0, 0, 100, 100, SavedDpi: 1),
        });

        var restored = await settings.LoadAsync();
        Assert.Equal("http://localhost:1234/v1", restored.LmStudioBaseUrl);
        Assert.Equal(520, restored.NavigationPaneWidth);
        Assert.Equal(640, restored.Window.Width);
        Assert.Equal(480, restored.Window.Height);
        Assert.DoesNotContain(System.IO.Directory.GetFiles(environment.Directory), static path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SessionLogRedactsSensitiveStructuredValuesAndExports()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var factory = environment.Get<ILoggerFactory>();
        var log = environment.Get<ISessionLog>();
        var logger = factory.CreateLogger("LMStudio");
        SensitiveLog(logger, "streng geheim", "lokal", null);

        var entry = Assert.Single(log.Snapshot(category: "LMStudio"));
        Assert.DoesNotContain("streng geheim", entry.Message, StringComparison.Ordinal);
        Assert.Equal("[ausgelassen]", entry.Properties["Prompt"]);
        await using var export = new MemoryStream();
        await log.ExportAsync(export, asJson: true);
        Assert.NotEmpty(export.ToArray());
        log.Clear();
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public async Task BackupRestoreReturnsDatabaseAndSettingsToSnapshot()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var settings = environment.Get<ISettingsStore>();
        var backup = environment.Get<IBackupService>();
        _ = await chats.CreateSessionAsync("Vorher");
        await settings.SaveAsync(new AppSettings { Language = "en-US" });
        var path = Path.Combine(environment.Directory, "snapshot.gobackup");
        _ = await backup.CreateAsync(path);
        await backup.ValidateAsync(path);
        _ = await chats.CreateSessionAsync("Nachher");
        await settings.SaveAsync(new AppSettings { Language = "de-DE" });

        await backup.RestoreAsync(path);

        var sessions = await chats.ListSessionsAsync();
        Assert.Single(sessions);
        Assert.Equal("Vorher", sessions[0].Title);
        Assert.Equal("en-US", (await settings.LoadAsync()).Language);
    }
}
