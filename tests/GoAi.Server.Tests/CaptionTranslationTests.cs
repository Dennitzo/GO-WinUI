using GoAi.Contracts;
using GoAi.Server.Core.Audio;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runtime;
using GoAi.Server.Core.Storage;
using GoAi.Server.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class CaptionTranslationTests
{
    [Fact]
    public void TranslationValidationRejectsModelCommentaryButAcceptsFaithfulText()
    {
        WorkerOrchestrator.ValidateCaptionTranslation("No! No! ooo", "Nein! Nein! Ooo");
        Assert.Throws<JsonException>(() => WorkerOrchestrator.ValidateCaptionTranslation(
            "No! No! ooo",
            "Nein! Nein! Die Eingabe besteht aus einem einzelnen Element. Ich analysiere die Struktur und Bedeutung dieses Textes und formatiere die Ausgabe entsprechend der Vorgabe."));
    }

    [Fact]
    public async Task CaptionSessionUsesSelectedGeneralModelWithoutReasoningAndPreservesSpeaker()
    {
        const bool supportsNoReasoning = true;
        using var context = new TestServerContext();
        using var handler = new CaptionHandler(supportsNoReasoning);
        using var modelHttp = new HttpClient(handler);
        using var workerHttp = new HttpClient(handler);
        using var model = new ModelRuntimeClient(modelHttp, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        var runtime = new ServerRuntimeState();
        using var scheduler = new GpuLeaseScheduler(context.Database, runtime);
        using var workers = new WorkerOrchestrator(new WorkerApiClient(workerHttp, context.WrappedOptions), model,
            scheduler, new ArtifactService(context.Database, context.WrappedOptions), context.WrappedOptions, runtime);
        using var captions = new LiveCaptionService(workers, runtime);
        var request = JsonSerializer.Deserialize<LiveCaptionSessionRequest>(
            JsonSerializer.Serialize(new LiveCaptionSessionRequest(Language: null, PreferredGeneralModelId: CaptionHandler.ModelId), GoAiProtocol.CreateJsonOptions()),
            GoAiProtocol.CreateJsonOptions())!;
        Assert.Null(request.Language);
        var session = await captions.CreateAsync(request);
        using var wave = new MemoryStream();
        using (var writer = new BinaryWriter(wave, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(32036); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(32000); writer.Write(new byte[32000]);
        }
        var response = await captions.ProcessChunkAsync(session.SessionId, 0, wave.ToArray());
        Assert.Equal("Person 2: Hallo Welt.", response.Text);
        Assert.Contains(CaptionHandler.ModelId, response.Provider);
        Assert.Equal(CaptionHandler.ModelId, handler.ChatBody.GetProperty("model").GetString());
        Assert.DoesNotContain("gpt-oss", handler.ChatBody.ToString());
        var format = handler.ChatBody.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var schema = format.GetProperty("json_schema").GetProperty("schema");
        Assert.Equal(1, schema.GetProperty("minItems").GetInt32());
        Assert.Equal(1, schema.GetProperty("maxItems").GetInt32());
        Assert.False(handler.ChatBody.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        await captions.StopSessionAsync(session.SessionId);
    }

    [Fact]
    public async Task CaptionTranslationRejectsAModelThatCannotDisableReasoning()
    {
        using var context = new TestServerContext();
        using var handler = new CaptionHandler(supportsNoReasoning: false);
        using var modelHttp = new HttpClient(handler);
        using var workerHttp = new HttpClient(handler);
        using var model = new ModelRuntimeClient(modelHttp, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        var runtime = new ServerRuntimeState();
        using var scheduler = new GpuLeaseScheduler(context.Database, runtime);
        using var workers = new WorkerOrchestrator(new WorkerApiClient(workerHttp, context.WrappedOptions), model,
            scheduler, new ArtifactService(context.Database, context.WrappedOptions), context.WrappedOptions, runtime);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workers.TranslateCaptionSegmentsAsync(
            [new TranscriptionSegment(0, 1, "Hello world.", "Person 1")], "caption-test", CaptionHandler.ModelId));
        Assert.Contains("reasoning-freien Modus", error.Message);
        Assert.Equal(JsonValueKind.Undefined, handler.ChatBody.ValueKind);
    }

    private sealed class CaptionHandler(bool supportsNoReasoning) : HttpMessageHandler
    {
        public const string ModelId = "coding/DeepSeek-caption-test~1234";
        public JsonElement ChatBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object result = new { success = true };
            if (path == "/v1/models") result = new { data = new[] { new {
                id = ModelId, status = new { value = "loaded" },
                tags = supportsNoReasoning
                    ? new[] { "go-context-train:4096", "go-reasoning-mode:llama-toggle", "go-reasoning-levels:none|on", "go-reasoning-default:on" }
                    : new[] { "go-context-train:4096", "go-reasoning-mode:automatic" }
            } } };
            if (path == "/props") result = new { default_generation_settings = new { n_ctx = 4096 } };
            if (path == "/v1/chat/completions/input_tokens") result = new { input_tokens = 100 };
            if (path == "/live-captions")
            {
                Assert.False(request.Headers.Contains("X-GO-AI-Caption-Language"));
                result = new {
                text = "Hello world.", language = "en", languageProbability = 0.99,
                segments = new[] { new { start = 0, end = 1, text = "Hello world.", speaker = "Person 2" } }, provider = "test-whisper"
                };
            }
            if (path == "/v1/chat/completions")
            {
                ChatBody = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
                result = new { choices = new[] { new { message = new { role = "assistant", content = "[\"Hallo Welt.\"]" }, finish_reason = "stop" } } };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") };
        }
    }
}
