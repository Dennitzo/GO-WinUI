using GoAi.Contracts;
using GoWinUI.App.ViewModels;

namespace GoWinUI.Tests;

public sealed class ShellViewModelTests
{
    [Fact]
    public void FooterAlwaysContainsEveryConfiguredServiceIncludingCoding()
    {
        var viewModel = new ShellViewModel();

        Assert.Equal(7, viewModel.AiServices.Count);
        Assert.Equal(
            ["General AI", "Coding", "Spracherkennung", "Sprachausgabe", "Vision / Medien", "Bildgenerierung", "Web / YouTube"],
            viewModel.AiServices.Select(static item => item.DisplayName));
        Assert.All(viewModel.AiServices, static item => Assert.False(string.IsNullOrWhiteSpace(item.Glyph)));
        Assert.Equal(viewModel.AiServices.Count, viewModel.AiServices.Select(static item => item.Glyph).Distinct().Count());
        Assert.Equal(
            "General AI - gpt-oss-20b",
            viewModel.AiServices.Single(static item => item.Key == "general").ToolTipText);
        Assert.All(viewModel.AiServices, static item => Assert.False(item.IsActive));
        Assert.All(viewModel.AiServices, static item => Assert.True(item.IsIdle));

        viewModel.SetAiServiceAvailability(true, ReadyModels(), ReadyServices());

        Assert.All(viewModel.AiServices, static item => Assert.True(item.IsReachable));
        Assert.All(viewModel.AiServices, static item => Assert.Equal("Bereit", item.StateLabel));
        Assert.Contains(viewModel.AiServices, static item =>
            item.DisplayName == "Coding" && item.Runtime.Contains("Laguna", StringComparison.Ordinal));
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
                new("lease-general", "llm-general", "gpt-oss-20b", "LM Studio", "run-1", now),
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

        Assert.Equal(7, viewModel.AiServices.Count);
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

        Assert.Equal(7, viewModel.AiServices.Count);
        Assert.True(viewModel.AiServices.Single(static item => item.Key == "speech-to-text").IsActive);

        viewModel.SetClientAiRun("microphone-stt", false, string.Empty, string.Empty);
        Assert.False(viewModel.AiServices.Single(static item => item.Key == "speech-to-text").IsActive);
    }

    [Fact]
    public void CodingWorkloadActivatesLagunaChipOnly()
    {
        var now = DateTimeOffset.UtcNow;
        var viewModel = new ShellViewModel();
        viewModel.SetAiServiceAvailability(true, ReadyModels(), ReadyServices());

        viewModel.SetActiveAiRuns(new GpuStatusSnapshot(
            true,
            0,
            "lease-code",
            [],
            now,
            ActiveWorkloads: [new("lease-code", "llm-code", "Laguna-S-2.1", "LM Studio", "run-code", now)]));

        Assert.True(viewModel.AiServices.Single(static item => item.Key == "coding").IsActive);
        Assert.False(viewModel.AiServices.Single(static item => item.Key == "general").IsActive);
    }

    private static ModelStatusSnapshot ReadyModels() => new(
        true,
        "http://127.0.0.1:1234",
        [
            new("gpt-oss-20b", "general", true, true, "loaded", 131_072),
            new("Laguna-S-2.1", "code", true, false, "available", 262_144),
            new("Qwen3-VL", "vision", true, false, "available", 65_536),
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
