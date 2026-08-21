using System.Net;
using System.Text;
using System.Text.Json;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runtime;
using GoAi.Server.Core.Security;
using GoAi.Server.Core.Storage;
using GoAi.Server.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoAi.Server.Tests;

public sealed class WorkerOrchestratorStartupTests
{
    [Fact]
    public void SpeechWorkerRemainsResidentAcrossLmStudioTransitions()
    {
        Assert.Equal("speech", WorkerOrchestrator.ResidentSpeechWorkerName);
    }

    [Fact]
    public async Task StartupWarmsOnlySpeechAndNeverTouchesLmStudioModels()
    {
        await using var fixture = await OrchestratorFixture.CreateAsync();

        await fixture.Orchestrator.WarmAllStartupResourcesAsync();

        Assert.Equal(0, fixture.LmStudioHandler.RequestCount);
        Assert.Equal(3, fixture.WorkerHandler.LoadedSpeechComponents.Count);
        Assert.Contains("stt", fixture.WorkerHandler.LoadedSpeechComponents);
        Assert.Contains("speaker", fixture.WorkerHandler.LoadedSpeechComponents);
        Assert.Contains("tts", fixture.WorkerHandler.LoadedSpeechComponents);
    }

    [Fact]
    public async Task MediaWorkerPreparationDoesNotLoadGeneralAi()
    {
        await using var fixture = await OrchestratorFixture.CreateAsync();

        var result = await fixture.Orchestrator.InspectMediaAsync(
            new WorkerMediaRequest("upload-test", "image/png"),
            "run-media-test");

        Assert.Equal("image", result.Kind);
        Assert.Equal(0, fixture.LmStudioHandler.RequestCount);
        Assert.Contains("/inspect", fixture.WorkerHandler.RequestPaths);
    }

    [Fact]
    public async Task VisionRequestTransitionsDirectlyFromCoderToVision()
    {
        await using var fixture = await OrchestratorFixture.CreateAsync(
            initiallyLoadedModelId: "qwen3-coder-next");

        _ = await fixture.Orchestrator.PrepareLmModelAsync(
            fixture.Context.Options.VisionModelId,
            65_536);

        Assert.Equal(1, fixture.LmStudioHandler.UnloadRequests);
        Assert.Equal([fixture.Context.Options.VisionModelId], fixture.LmStudioHandler.LoadTargets);
        Assert.DoesNotContain(
            fixture.Context.Options.GeneralModelId,
            fixture.LmStudioHandler.LoadTargets,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GeneralModelSelectionPersistsWithoutChangingTheLoadedModel()
    {
        await using var fixture = await OrchestratorFixture.CreateAsync(
            initiallyLoadedModelId: "qwen3-coder-next");
        using var selectionService = new GeneralModelSelectionService(
            fixture.Context.WrappedOptions,
            fixture.LmStudio);

        var selection = await selectionService.SelectAsync("alternate-general");

        Assert.Equal("alternate-general", selection.ModelId);
        Assert.False(selection.Loaded);
        Assert.Equal(0, fixture.LmStudioHandler.UnloadRequests);
        Assert.Empty(fixture.LmStudioHandler.LoadTargets);
    }

    private sealed class OrchestratorFixture : IAsyncDisposable
    {
        private readonly HttpClient _lmStudioHttp;
        private readonly HttpClient _workerHttp;
        private readonly LmStudioClient _lmStudio;
        private readonly WorkerKeyStore _workerKeys;
        private readonly GpuLeaseScheduler _scheduler;

        private OrchestratorFixture(
            TestServerContext context,
            RecordingLmStudioHandler lmStudioHandler,
            RecordingWorkerHandler workerHandler,
            HttpClient lmStudioHttp,
            HttpClient workerHttp,
            LmStudioClient lmStudio,
            WorkerKeyStore workerKeys,
            GpuLeaseScheduler scheduler,
            WorkerOrchestrator orchestrator)
        {
            Context = context;
            LmStudioHandler = lmStudioHandler;
            WorkerHandler = workerHandler;
            _lmStudioHttp = lmStudioHttp;
            _workerHttp = workerHttp;
            _lmStudio = lmStudio;
            _workerKeys = workerKeys;
            _scheduler = scheduler;
            Orchestrator = orchestrator;
        }

        public TestServerContext Context { get; }
        public RecordingLmStudioHandler LmStudioHandler { get; }
        public RecordingWorkerHandler WorkerHandler { get; }
        public LmStudioClient LmStudio => _lmStudio;
        public WorkerOrchestrator Orchestrator { get; }

        public static async Task<OrchestratorFixture> CreateAsync(string? initiallyLoadedModelId = null)
        {
            var context = new TestServerContext();
            await context.Database.InitializeAsync();
            var runtime = new ServerRuntimeState(context.WrappedOptions);
            var scheduler = new GpuLeaseScheduler(context.Database, runtime);
            var lmStudioHandler = new RecordingLmStudioHandler(context, initiallyLoadedModelId);
            var lmStudioHttp = new HttpClient(lmStudioHandler);
            var lmStudio = new LmStudioClient(
                lmStudioHttp,
                context.WrappedOptions,
                new DpapiSecretStore(context.WrappedOptions),
                NullLogger<LmStudioClient>.Instance);
            var workerHandler = new RecordingWorkerHandler();
            var workerHttp = new HttpClient(workerHandler);
            var workerKeys = new WorkerKeyStore(context.WrappedOptions);
            var workerClient = new WorkerApiClient(workerHttp, workerKeys, context.WrappedOptions);
            var artifacts = new ArtifactService(context.Database, context.WrappedOptions);
            var orchestrator = new WorkerOrchestrator(
                workerClient,
                lmStudio,
                scheduler,
                artifacts,
                context.WrappedOptions,
                runtime);
            return new(
                context,
                lmStudioHandler,
                workerHandler,
                lmStudioHttp,
                workerHttp,
                lmStudio,
                workerKeys,
                scheduler,
                orchestrator);
        }

        public ValueTask DisposeAsync()
        {
            Orchestrator.Dispose();
            _scheduler.Dispose();
            _workerKeys.Dispose();
            _lmStudio.Dispose();
            _workerHttp.Dispose();
            _lmStudioHttp.Dispose();
            Context.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingWorkerHandler : HttpMessageHandler
    {
        public List<string> LoadedSpeechComponents { get; } = [];
        public List<string> RequestPaths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            RequestPaths.Add(path);
            if (path == "/load" && request.Content is not null)
            {
                var content = await request.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.TryGetProperty("component", out var component))
                {
                    LoadedSpeechComponents.Add(component.GetString()!);
                }
            }

            var payload = path == "/inspect"
                ? "{\"kind\":\"image\",\"metadata\":{},\"artifacts\":[],\"frames\":[]}"
                : "{}";
            return JsonResponse(payload);
        }
    }

    private sealed class RecordingLmStudioHandler : HttpMessageHandler
    {
        private readonly TestServerContext _context;
        private readonly string? _initiallyLoadedModelId;

        public RecordingLmStudioHandler(TestServerContext context, string? initiallyLoadedModelId)
        {
            _context = context;
            _initiallyLoadedModelId = initiallyLoadedModelId;
        }

        public int RequestCount { get; private set; }
        public int UnloadRequests { get; private set; }
        public List<string> LoadTargets { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/models")
            {
                return JsonResponse(CreateModelList());
            }
            if (request.Method == HttpMethod.Post && path == "/api/v1/models/unload")
            {
                UnloadRequests++;
                return JsonResponse("{}");
            }
            if (request.Method == HttpMethod.Post && path == "/api/v1/models/load")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(body);
                var modelId = document.RootElement.GetProperty("model").GetString()!;
                var contextLength = document.RootElement.GetProperty("context_length").GetInt32();
                LoadTargets.Add(modelId);
                return JsonResponse($$"""
                    {
                      "instance_id": "requested-model",
                      "status": "loaded",
                      "load_config": {
                        "context_length": {{contextLength}},
                        "parallel": 1,
                        "flash_attention": true,
                        "offload_kv_cache_to_gpu": true
                      }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private string CreateModelList()
        {
            object Instances(string modelId, int contextLength) =>
                string.Equals(modelId, _initiallyLoadedModelId, StringComparison.OrdinalIgnoreCase)
                    ? new[]
                    {
                        new
                        {
                            id = "loaded-model",
                            model_instance_id = "loaded-model",
                            config = new
                            {
                                context_length = contextLength,
                                parallel = 1,
                                flash_attention = true,
                                offload_kv_cache_to_gpu = true,
                            },
                        },
                    }
                    : Array.Empty<object>();

            return JsonSerializer.Serialize(new
            {
                models = new object[]
                {
                    new
                    {
                        type = "llm",
                        key = _context.Options.GeneralModelId,
                        display_name = "General",
                        loaded_instances = Instances(_context.Options.GeneralModelId, _context.Options.GeneralContextLength),
                        max_context_length = _context.Options.GeneralContextLength,
                    },
                    new
                    {
                        type = "llm",
                        key = "qwen3-coder-next",
                        display_name = "Qwen Coder",
                        loaded_instances = Instances("qwen3-coder-next", 262_144),
                        max_context_length = 262_144,
                    },
                    new
                    {
                        type = "llm",
                        key = _context.Options.VisionModelId,
                        display_name = "Vision",
                        loaded_instances = Instances(_context.Options.VisionModelId, 65_536),
                        max_context_length = 65_536,
                    },
                    new
                    {
                        type = "embedding",
                        key = _context.Options.EmbeddingModelId,
                        display_name = "Embedding",
                        loaded_instances = Instances(_context.Options.EmbeddingModelId, 8_192),
                        max_context_length = 8_192,
                    },
                    new
                    {
                        type = "llm",
                        key = "alternate-general",
                        display_name = "Alternate General",
                        loaded_instances = Instances("alternate-general", 98_304),
                        max_context_length = 98_304,
                    },
                },
            });
        }
    }

    private static HttpResponseMessage JsonResponse(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };
}
