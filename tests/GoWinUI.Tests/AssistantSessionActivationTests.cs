using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.Tests;

public sealed class AssistantSessionActivationTests
{
    [Theory]
    [InlineData("stable", "local-assistant-stable")]
    public void SessionUriUsesTheValidatedProfileIdentity(
        string profileName,
        string expectedScheme)
    {
        var sessionId = Guid.NewGuid();

        var uri = AssistantSessionUri.Create(profileName, sessionId);

        Assert.Equal(expectedScheme, uri.Scheme);
        Assert.Equal($"{expectedScheme}://session/{sessionId:D}", uri.AbsoluteUri);
        Assert.True(AssistantSessionUri.TryParse(uri, profileName, out var parsed));
        Assert.Equal(sessionId, parsed);
    }

    [Fact]
    public void SessionUriRejectsAmbiguousOrExpandedTargets()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        string[] invalidUris =
        [
            $"go://session/{sessionId}",
            $"local-assistant-development://session/{sessionId}",
            $"local-assistant-stable://sessions/{sessionId}",
            $"local-assistant-stable://session/{sessionId}/",
            $"local-assistant-stable://session/{sessionId}?source=notification",
            $"local-assistant-stable://session/{sessionId}#message",
            $"local-assistant-stable://session:42/{sessionId}",
            $"local-assistant-stable://user@session/{sessionId}",
            "local-assistant-stable://session/not-a-guid",
            $"local-assistant-stable://session/{Guid.Empty:D}",
            "local-assistant-stable://session/%3000000000-0000-0000-0000-000000000001",
        ];

        foreach (var value in invalidUris)
        {
            Assert.False(
                AssistantSessionUri.TryParse(new Uri(value, UriKind.Absolute), "stable", out var parsed),
                value);
            Assert.Equal(Guid.Empty, parsed);
        }

        Assert.False(AssistantSessionUri.TryParse(
            new Uri($"local-assistant-stable://session/{sessionId}"),
            "Stable",
            out _));
        Assert.Throws<ArgumentException>(() => AssistantSessionUri.ProtocolScheme("invalid/profile"));
    }

    [Theory]
    [InlineData(ChatMode.General)]
    [InlineData(ChatMode.Coding)]
    [InlineData(ChatMode.ClaudeScience)]
    public async Task TargetSessionSelectsItsModeAndBothActivePointersBeforeNavigation(ChatMode targetMode)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var target = await chats.CreateSessionAsync("Ziel", targetMode);
        var otherMode = targetMode == ChatMode.General ? ChatMode.Coding : ChatMode.General;
        var retained = await chats.CreateSessionAsync("Andere Ansicht", otherMode);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with
        {
            SelectedChatMode = otherMode,
            ActiveGeneralSessionId = otherMode == ChatMode.General ? retained.Id : null,
            ActiveCodingSessionId = otherMode == ChatMode.Coding ? retained.Id : null,
            ActiveClaudeScienceSessionId = otherMode == ChatMode.ClaudeScience ? retained.Id : null,
            ActiveSessionId = retained.Id,
            LastRoute = "settings",
        });
        var service = new AssistantSessionActivationService(chats, settings);

        var activation = await service.ActivateAsync(target.Id);

        Assert.NotNull(activation);
        Assert.True(activation.SessionChanged);
        Assert.Equal(target.Id, activation.SessionId);
        Assert.Equal(targetMode, activation.ChatMode);
        Assert.Equal(targetMode, settings.Current.SelectedChatMode);
        Assert.Equal(target.Id, settings.Current.ActiveSessionId);
        Assert.Equal("assistant", settings.Current.LastRoute);
        Assert.Equal(
            (Guid?)(targetMode == ChatMode.General ? target.Id : otherMode == ChatMode.General ? retained.Id : null),
            settings.Current.ActiveGeneralSessionId);
        Assert.Equal(
            (Guid?)(targetMode == ChatMode.Coding ? target.Id : otherMode == ChatMode.Coding ? retained.Id : null),
            settings.Current.ActiveCodingSessionId);
        Assert.Equal(
            (Guid?)(targetMode == ChatMode.ClaudeScience ? target.Id : null),
            settings.Current.ActiveClaudeScienceSessionId);

        var repeated = await service.ActivateAsync(target.Id);
        Assert.NotNull(repeated);
        Assert.False(repeated.SessionChanged);
    }

    [Fact]
    public async Task MissingTargetDoesNotChangePersistedSelection()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var retained = await chats.CreateSessionAsync("Behalten", ChatMode.General);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with
        {
            ActiveGeneralSessionId = retained.Id,
            ActiveSessionId = retained.Id,
            LastRoute = "logs",
        });
        var before = settings.Current;
        var service = new AssistantSessionActivationService(chats, settings);

        var activation = await service.ActivateAsync(Guid.NewGuid());

        Assert.Null(activation);
        Assert.Equal(before, settings.Current);
    }
}
