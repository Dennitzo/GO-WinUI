using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
            SelectedModel = null,
            AccentColor = "#f4b860",
            BackgroundColor = "#34313b",
            NavigationPaneWidth = 999,
            IsAssistantSessionPaneOpen = false,
            Window = new(0, 0, 100, 100, SavedDpi: 1),
        });

        var restored = await settings.LoadAsync();
        Assert.Equal("http://localhost:1234/v1", restored.LmStudioBaseUrl);
        Assert.Equal(AppSettings.DefaultSelectedModel, restored.SelectedModel);
        Assert.Equal("#F4B860", restored.AccentColor);
        Assert.Equal("#34313B", restored.BackgroundColor);
        Assert.Equal(520, restored.NavigationPaneWidth);
        Assert.False(restored.IsAssistantSessionPaneOpen);
        Assert.Equal(640, restored.Window.Width);
        Assert.Equal(480, restored.Window.Height);
        Assert.DoesNotContain(System.IO.Directory.GetFiles(environment.Directory), static path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VersionOneSettingsUseTheirAccentAsInitialBackgroundColor()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var settings = environment.Get<ISettingsStore>();
        await settings.SaveAsync(new AppSettings
        {
            Version = 1,
            AccentColor = "#8fbd45",
        });

        var restored = await settings.LoadAsync();
        Assert.Equal(6, restored.Version);
        Assert.Equal("#8FBD45", restored.AccentColor);
        Assert.Equal("#8FBD45", restored.BackgroundColor);
    }

    [Fact]
    public async Task VersionThreeSettingsMigrateTheFormer120BDefaultTo20B()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var settings = environment.Get<ISettingsStore>();
        await settings.SaveAsync(new AppSettings
        {
            Version = 3,
            SelectedModel = "openai/gpt-oss-120b",
        });

        var restored = await settings.LoadAsync();
        Assert.Equal(6, restored.Version);
        Assert.Equal("openai/gpt-oss-20b", restored.SelectedModel);
    }

    [Fact]
    public async Task VersionFourGermanCaptionsMigrateToAutomaticLanguageDetection()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var settings = environment.Get<ISettingsStore>();
        await settings.SaveAsync(new AppSettings { Version = 4, LiveCaptionLanguage = "de" });

        Assert.Equal("auto", (await settings.LoadAsync()).LiveCaptionLanguage);
    }

    [Fact]
    public async Task NewAndExistingSettingsDefaultToOfflineMode()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var settings = environment.Get<ISettingsStore>();

        Assert.False((await settings.LoadAsync()).IsAiConnectionEnabled);

        await settings.SaveAsync(new AppSettings { Version = 5 });

        var restored = await settings.LoadAsync();
        Assert.Equal(6, restored.Version);
        Assert.False(restored.IsAiConnectionEnabled);
    }

    [Fact]
    public async Task RecentActivitySurvivesASettingsReload()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var store = environment.Get<ISettingsStore>();
        var firstShell = new ShellViewModel();
        using (var firstSettings = new SettingsCoordinator(store))
        {
            await firstSettings.InitializeAsync();
            var activity = new RecentActivityService(
                firstSettings,
                firstShell,
                NullLogger<RecentActivityService>.Instance);

            await activity.RecordAsync("  Projekt   „Haus A“\r\n erstellt  ");
            Assert.Equal("Projekt „Haus A“ erstellt", firstShell.RecentActivityText);
            Assert.Contains("Uhr", firstShell.RecentActivityTimeText, StringComparison.Ordinal);
        }

        var secondShell = new ShellViewModel();
        using var secondSettings = new SettingsCoordinator(store);
        await secondSettings.InitializeAsync();
        var restoredActivity = new RecentActivityService(
            secondSettings,
            secondShell,
            NullLogger<RecentActivityService>.Instance);
        restoredActivity.Restore();

        Assert.Equal("Projekt „Haus A“ erstellt", secondSettings.Current.LastActivityText);
        Assert.NotNull(secondSettings.Current.LastActivityAt);
        Assert.Equal("Projekt „Haus A“ erstellt", secondShell.RecentActivityText);
        Assert.Contains("Uhr", secondShell.RecentActivityTimeText, StringComparison.Ordinal);
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
