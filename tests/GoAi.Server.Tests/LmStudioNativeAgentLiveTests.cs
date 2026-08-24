using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using GoAi.Server.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class LmStudioNativeAgentLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task QwenCoderGeneratesMultiRoundWorkspaceToolsThroughNativeSdk()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("GO_AI_RUN_LMSTUDIO_NATIVE_LIVE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var tokenPath = Environment.GetEnvironmentVariable("GO_AI_LM_STUDIO_TOKEN_FILE")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "GO-AI-Server",
                "Secrets",
                "lmstudio-token.dpapi");
        Assert.True(File.Exists(tokenPath), $"LM Studio token file is missing: {tokenPath}");
        var options = Options.Create(new GoAiServerOptions
        {
            LmStudioUri = new Uri("http://127.0.0.1:1234", UriKind.Absolute),
            LmStudioTokenFile = tokenPath,
        });
        using var native = new LmStudioNativeAgentClient(
            options,
            new DpapiSecretStore(options),
            NullLogger<LmStudioNativeAgentClient>.Instance);
        using var emptySchema = JsonDocument.Parse(
            """{"type":"object","properties":{},"additionalProperties":false}""");
        using var readSchema = JsonDocument.Parse(
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}""");
        var progress = new List<LmStudioNativeAgentProgress>();
        ValueTask Observe(LmStudioNativeAgentProgress item, CancellationToken _)
        {
            progress.Add(item);
            return ValueTask.CompletedTask;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var models = await native.GetModelsAsync(timeout.Token);
        Assert.Contains(models.Models, static model =>
            string.Equals(model.Key, "qwen3-coder-next", StringComparison.OrdinalIgnoreCase));
        var preparation = await native.LoadModelAsync(
            "qwen3-coder-next",
            32_768,
            isEmbedding: false,
            timeout.Token);
        Assert.False(string.IsNullOrWhiteSpace(preparation.InstanceId));

        LmChatResult map;
        try
        {
            map = await native.CompleteAsync(new LmStudioNativeAgentRequest(
                "qwen3-coder-next",
                [
                    new LmChatMessage(
                        "system",
                        "Du bist ein Coding-Agent. Erzeuge ausschliesslich einen nativen Toolaufruf."),
                    new LmChatMessage(
                        "user",
                        "Analysiere zuerst die Struktur des Workspace mit workspace.map."),
                ],
                [new LmToolDefinition("workspace.map", "Kartiert den Workspace.", emptySchema.RootElement.Clone())],
                2_048,
                RequireToolCall: true,
                RequiredToolName: "workspace.map",
                new LmStudioNativeSampling(Temperature: 0.1, TopP: 0.9, TopK: 20, RepeatPenalty: 1.05),
                Observe), timeout.Token);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Native SDK phases before timeout: "
                + string.Join(", ", progress.Select(static item => item.State)),
                exception);
        }
        var mapCall = Assert.Single(map.ToolCalls);
        Assert.Equal("workspace.map", mapCall.Name);

        var read = await native.CompleteAsync(new LmStudioNativeAgentRequest(
            "qwen3-coder-next",
            [
                new LmChatMessage(
                    "system",
                    "Du bist ein Coding-Agent. Erzeuge ausschliesslich einen nativen Toolaufruf."),
                new LmChatMessage("user", "Lies anschliessend die Projektbeschreibung."),
                new LmChatMessage("assistant", ToolCalls: [mapCall]),
                new LmChatMessage(
                    "tool",
                    "{\"root\":\"PhysikBuch\",\"files\":[\"README.md\"]}",
                    ToolCallId: mapCall.Id),
            ],
            [new LmToolDefinition("fs.readText", "Liest eine Textdatei.", readSchema.RootElement.Clone())],
            2_048,
            RequireToolCall: true,
            RequiredToolName: "fs.readText",
            new LmStudioNativeSampling(Temperature: 0.1, TopP: 0.9, TopK: 20, RepeatPenalty: 1.05),
            Observe), timeout.Token);
        var readCall = Assert.Single(read.ToolCalls);
        Assert.Equal("fs.readText", readCall.Name);
        Assert.False(string.IsNullOrWhiteSpace(readCall.Arguments.GetProperty("path").GetString()));

        var catalogRequest = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Code,
            [new RunMessage("user", [new ContentPart("text", "Untersuche zuerst die Workspace-Struktur.")])],
            ClientCapabilities: ["code", "filesystem", "process"],
            AllowedServerTools: ["math.evaluate"]);
        var completeCatalog = new AgentToolCatalog()
            .GetAvailableTools(catalogRequest)
            .Select(static tool => tool.ToLmDefinition())
            .ToArray();
        Assert.DoesNotContain(completeCatalog, static tool => tool.Name is "web.search" or "web.fetch");

        var selectedFromCatalog = await native.CompleteAsync(new LmStudioNativeAgentRequest(
            "qwen3-coder-next",
            [
                new LmChatMessage(
                    "system",
                    "Du bist ein Coding-Agent. Waehle genau ein passendes natives Werkzeug aus dem vollstaendigen Katalog."),
                new LmChatMessage(
                    "user",
                    "Untersuche als ersten Schritt die Struktur des gebundenen Workspace. Verwende das passende Workspace-Werkzeug."),
            ],
            completeCatalog,
            2_048,
            RequireToolCall: true,
            RequiredToolName: null,
            new LmStudioNativeSampling(Temperature: 0.1, TopP: 0.9, TopK: 20, RepeatPenalty: 1.05),
            Observe), timeout.Token);
        Assert.Equal("workspace.map", Assert.Single(selectedFromCatalog.ToolCalls).Name);

        Assert.Contains(progress, static item => item.State == "toolCallGenerationStart");
        Assert.Contains(progress, static item => item.State == "toolCallGenerationNameReceived");
        Assert.Contains(progress, static item => item.State == "toolCallGenerationEnd");
    }
}
