using GoAi.Contracts;
using GoAi.Server.Core.Workers;

namespace GoAi.Server.Tests;

public sealed class WorkerOrchestratorStartupTests
{
    [Theory]
    [InlineData("qwen3-coder-next", "code")]
    [InlineData("ud", "code")]
    public void LoadedSpecialistModelIsPreservedAcrossServerRestart(string id, string role)
    {
        var status = CreateStatus(
            new ModelRuntimeStatus(id, role, true, true, "Geladen", 262_144),
            new ModelRuntimeStatus("openai/gpt-oss-120b", "general", true, false, "Bereit zum Laden", 131_072));

        var selected = WorkerOrchestrator.FindLoadedSpecialistModel(status);

        Assert.NotNull(selected);
        Assert.Equal(id, selected.Id);
    }

    [Theory]
    [InlineData("qwen3-vl", "vision")]
    [InlineData("bge-m3", "embedding")]
    public void OptionalModelsDoNotSuppressNormalStartupWarmup(string id, string role)
    {
        var status = CreateStatus(
            new ModelRuntimeStatus(id, role, true, true, "Geladen", 262_144));

        Assert.Null(WorkerOrchestrator.FindLoadedSpecialistModel(status));
    }

    [Fact]
    public void LoadedGeneralModelDoesNotSuppressNormalStartupWarmup()
    {
        var status = CreateStatus(
            new ModelRuntimeStatus("openai/gpt-oss-120b", "general", true, true, "Geladen", 131_072));

        Assert.Null(WorkerOrchestrator.FindLoadedSpecialistModel(status));
    }

    [Fact]
    public void UnreachableProviderDoesNotClaimAReusableSpecialistModel()
    {
        var status = new ModelStatusSnapshot(
            false,
            "http://127.0.0.1:1234",
            [new ModelRuntimeStatus("qwen3-coder-next", "code", true, true, "Geladen", 262_144)],
            DateTimeOffset.UtcNow,
            "lmstudio.unreachable");

        Assert.Null(WorkerOrchestrator.FindLoadedSpecialistModel(status));
    }

    private static ModelStatusSnapshot CreateStatus(params ModelRuntimeStatus[] models) => new(
        true,
        "http://127.0.0.1:1234",
        models,
        DateTimeOffset.UtcNow);
}
