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
            SelectedModel = null,
            AccentColor = "#f4b860",
            BackgroundColor = "#34313b",
            NavigationPaneWidth = 999,
            IsAssistantSessionPaneOpen = false,
            Window = new(0, 0, 100, 100, SavedDpi: 1),
        });

        var restored = await settings.LoadAsync();
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
        Assert.Equal(AppSettings.CurrentVersion, restored.Version);
        Assert.Equal("#8FBD45", restored.AccentColor);
        Assert.Equal("#8FBD45", restored.BackgroundColor);
    }

    [Fact]
    public async Task VersionThreeSettingsMigrateToTheShared120BDefault()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var settings = environment.Get<ISettingsStore>();
        await settings.SaveAsync(new AppSettings
        {
            Version = 3,
            SelectedModel = "openai/gpt-oss-120b",
        });

        var restored = await settings.LoadAsync();
        Assert.Equal(AppSettings.CurrentVersion, restored.Version);
        Assert.Equal(AppSettings.DefaultSelectedModel, restored.SelectedModel);
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
    public async Task NewAndLegacySettingsStartOnlineAndMigrateTheDockerGateway()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var settings = environment.Get<ISettingsStore>();

        var initial = await settings.LoadAsync();
        Assert.True(initial.IsAiConnectionEnabled);
        Assert.Equal("http://192.168.0.67:8080", initial.GoAiServerUrl);

        await settings.SaveAsync(new AppSettings
        {
            Version = 12,
            IsAiConnectionEnabled = false,
            GoAiServerUrl = "https://192.168.0.67:8443",
        });

        var restored = await settings.LoadAsync();
        Assert.Equal(AppSettings.CurrentVersion, restored.Version);
        Assert.True(restored.IsAiConnectionEnabled);
        Assert.Equal("http://192.168.0.67:8080", restored.GoAiServerUrl);

        await settings.SaveAsync(restored with { IsAiConnectionEnabled = false });
        Assert.False((await settings.LoadAsync()).IsAiConnectionEnabled);
    }

    [Fact]
    public async Task CurrentSettingsPreserveDynamicallyDiscoveredModelIds()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var settings = environment.Get<ISettingsStore>();
        const string dynamicGeneralModel = "vendor/new-general-model:q8_0";

        await settings.SaveAsync(new AppSettings
        {
            Version = AppSettings.CurrentVersion,
            SelectedModel = dynamicGeneralModel,
        });

        var restored = await settings.LoadAsync();
        Assert.Equal(dynamicGeneralModel, restored.SelectedModel);
    }

    [Fact]
    public async Task TerminalOnlyModelIsNotRestoredAsTheGeneralUiModel()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var settings = environment.Get<ISettingsStore>();

        await settings.SaveAsync(new AppSettings
        {
            Version = AppSettings.CurrentVersion,
            SelectedModel = "qwen3-coder-next",
        });

        Assert.Equal(AppSettings.DefaultSelectedModel, (await settings.LoadAsync()).SelectedModel);
    }

    [Fact]
    public async Task GeneralModelSelectionSurvivesCoordinatorRestart()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var store = environment.Get<ISettingsStore>();

        using (var first = new SettingsCoordinator(store))
        {
            await first.InitializeAsync();
            await first.UpdateAsync(current => current with
            {
                SelectedModel = "general-after-restart",
            });
        }

        using var second = new SettingsCoordinator(store);
        await second.InitializeAsync();
        Assert.Equal("general-after-restart", second.Current.SelectedModel);
    }

    [Fact]
    public async Task EveryLegacyReasoningSettingIsResetToLmStudioAutomatic()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var settings = environment.Get<ISettingsStore>();

        await settings.SaveAsync(new AppSettings { Version = 10, ReasoningEffort = "high" });
        Assert.Equal("auto", (await settings.LoadAsync()).ReasoningEffort);

        await settings.SaveAsync(new AppSettings { Version = 11, ReasoningEffort = "on" });
        Assert.Equal("auto", (await settings.LoadAsync()).ReasoningEffort);

        await settings.SaveAsync(new AppSettings { Version = 11, ReasoningEffort = "off" });
        Assert.Equal("auto", (await settings.LoadAsync()).ReasoningEffort);

        await settings.SaveAsync(new AppSettings { Version = 11, ReasoningEffort = "xhigh" });
        Assert.Equal("auto", (await settings.LoadAsync()).ReasoningEffort);
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
        var logger = factory.CreateLogger("AiGateway");
        SensitiveLog(logger, "streng geheim", "lokal", null);

        var entry = Assert.Single(log.Snapshot(category: "AiGateway"));
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
