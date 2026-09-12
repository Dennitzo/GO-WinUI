using GoAi.Contracts;
using GoWinUI.App.ViewModels;

namespace GoWinUI.Tests;

public sealed class ShellViewModelTests
{
    [Fact]
    public void ConnectionStateTracksOfflineReachableAndUnavailableModesWithoutATitleBarLabel()
    {
        var viewModel = new ShellViewModel();

        Assert.False(viewModel.IsAiConnectionEnabled);

        viewModel.IsAiConnectionEnabled = true;
        Assert.False(viewModel.IsAiAvailabilityKnown);

        viewModel.ApplyAiConnectionState(false, false);
        Assert.True(viewModel.IsAiAvailabilityKnown);
        Assert.False(viewModel.IsAiAvailable);
        Assert.False(viewModel.IsAiServerReady);

        viewModel.ApplyAiConnectionState(true, false);
        Assert.True(viewModel.IsAiAvailable);
        Assert.False(viewModel.IsAiServerReady);

        viewModel.ApplyAiConnectionState(true, true);
        Assert.True(viewModel.IsAiAvailable);
        Assert.True(viewModel.IsAiServerReady);

        viewModel.ApplyAiConnectionState(false, false);
        Assert.False(viewModel.IsAiAvailable);
        Assert.False(viewModel.IsAiServerReady);

        viewModel.IsAiConnectionEnabled = false;
        Assert.False(viewModel.IsAiConnectionEnabled);
    }

    [Fact]
    public void ActiveAiRunKeepsAConfirmedOnlineStateAcrossATransientFailedProbe()
    {
        var viewModel = new ShellViewModel
        {
            IsAiConnectionEnabled = true,
        };
        viewModel.ApplyAiAvailabilitySnapshot(true, null, ReadyModels(), ReadyServices());
        viewModel.IsAiRunning = true;

        viewModel.ApplyAiAvailabilitySnapshot(false, null, null, null);

        Assert.True(viewModel.IsAiAvailable);
        Assert.True(viewModel.IsAiServerReady);

        viewModel.IsAiRunning = false;
        viewModel.ApplyAiAvailabilitySnapshot(false, null, null, null);

        Assert.False(viewModel.IsAiAvailable);
        Assert.False(viewModel.IsAiServerReady);
    }

    [Fact]
    public void FooterAlwaysContainsEveryConfiguredService()
    {
        var viewModel = new ShellViewModel();

        Assert.Equal(6, viewModel.AiServices.Count);
        Assert.Equal(
            ["General AI", "Spracherkennung", "Sprachausgabe", "Vision / Medien", "Bildgenerierung", "Web / YouTube"],
            viewModel.AiServices.Select(static item => item.DisplayName));
        Assert.All(viewModel.AiServices, static item => Assert.False(string.IsNullOrWhiteSpace(item.Glyph)));
        Assert.Equal(viewModel.AiServices.Count, viewModel.AiServices.Select(static item => item.Glyph).Distinct().Count());
        Assert.Equal(
            "General AI - lokale Modelle",
            viewModel.AiServices.Single(static item => item.Key == "general").ToolTipText);
        Assert.All(viewModel.AiServices, static item => Assert.False(item.IsActive));
        Assert.All(viewModel.AiServices, static item => Assert.True(item.IsIdle));

        viewModel.SetAiServiceAvailability(true, ReadyModels(), ReadyServices());

        Assert.All(viewModel.AiServices, static item => Assert.True(item.IsReachable));
        Assert.All(viewModel.AiServices, static item => Assert.Equal("Bereit", item.StateLabel));
    }

    [Fact]
    public void FooterUpdatesGeneralAndSpeechChipsWithoutChangingTheirOrder()
    {
        var now = DateTimeOffset.UtcNow;
        var viewModel = new ShellViewModel();
        viewModel.SetAiServiceAvailability(true, ReadyModels(), ReadyServices());
        var order = viewModel.AiServices.Select(static item => item.Key).ToArray();

        viewModel.SetActiveAiRuns(new GpuStatusSnapshot(
            true,
            0,
            "lease-general,lease-speech",
            [],
            now,
            ActiveWorkloads:
            [
                new("lease-general", "llm-general", "gpt-oss-120b", "llama.cpp", "run-1", now),
                new("lease-speech", "live-caption", "Sprache wird live transkribiert", "Docker · Whisper STT", "caption-1", now),
            ]));

        Assert.Equal(order, viewModel.AiServices.Select(static item => item.Key));
        Assert.True(viewModel.AiServices.Single(static item => item.Key == "general").IsBusy);
        Assert.True(viewModel.AiServices.Single(static item => item.Key == "speech-to-text").IsBusy);
        Assert.Equal(2, viewModel.AiServices.Count(static item => item.IsActive));
    }

    [Fact]
    public void LegacyLeaseUsesGeneralChipInsteadOfCreatingAnotherChip()
    {
        var viewModel = new ShellViewModel();

        viewModel.SetActiveAiRuns(new GpuStatusSnapshot(
            true,
            0,
            "lease-old",
            [],
            DateTimeOffset.UtcNow));

        Assert.Equal(6, viewModel.AiServices.Count);
        Assert.True(viewModel.AiServices.Single(static item => item.Key == "general").IsActive);
    }

    [Fact]
    public void ClientSpeechObservationUpdatesExistingStaticChip()
    {
        var viewModel = new ShellViewModel();

        viewModel.SetClientAiRun(
            "microphone-stt",
            true,
            "Sprache wird live transkribiert",
            "Docker · Whisper STT");

        Assert.Equal(6, viewModel.AiServices.Count);
        Assert.True(viewModel.AiServices.Single(static item => item.Key == "speech-to-text").IsActive);

        viewModel.SetClientAiRun("microphone-stt", false, string.Empty, string.Empty);
        Assert.False(viewModel.AiServices.Single(static item => item.Key == "speech-to-text").IsActive);
    }

    private static ModelStatusSnapshot ReadyModels() => new(
        true,
        "http://host.docker.internal:8081",
        [
            new("gpt-oss-120b", "general", true, true, "loaded", 32_768),
            new("Qwen3-VL", "vision", true, false, "available", 32_768),
        ],
        DateTimeOffset.UtcNow);

    private static IReadOnlyList<ServiceStatusSnapshot> ReadyServices() =>
    [
        new("SearXNG", "Bereit", "http://127.0.0.1:7081", true, DateTimeOffset.UtcNow),
        new("Speech / Live-Untertitel", "Bereit", "http://127.0.0.1:7082", true, DateTimeOffset.UtcNow),
        new("Media Worker", "Bereit", "http://127.0.0.1:7083", true, DateTimeOffset.UtcNow),
        new("Image Worker", "Bereit", "http://127.0.0.1:7084", true, DateTimeOffset.UtcNow),
    ];
}
