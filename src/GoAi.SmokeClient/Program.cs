using GoAi.Client;
using GoAi.Contracts;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

var options = Arguments.Parse(args);
using var handler = new HttpClientHandler();
if (!string.IsNullOrWhiteSpace(options.RootCertificatePath))
{
    var root = X509CertificateLoader.LoadCertificateFromFile(options.RootCertificatePath);
    var validator = GoAiClientFactory.CreatePinnedChainValidator(root);
    handler.ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
        certificate is not null && validator(certificate, chain, errors);
}

using var http = new HttpClient(handler)
{
    BaseAddress = new Uri(options.ServerUrl.EndsWith('/') ? options.ServerUrl : options.ServerUrl + "/"),
    Timeout = TimeSpan.FromHours(3),
};
using var client = new GoAiClient(http, options.ApiKey);
var json = GoAiProtocol.CreateJsonOptions();
json.WriteIndented = true;

switch (options.Command)
{
    case "status":
        await WriteAsync(new
        {
            live = await client.GetLiveHealthAsync(),
            ready = await client.GetReadyHealthAsync(),
            capabilities = await client.GetCapabilitiesAsync(),
            models = await client.GetModelStatusAsync(),
            gpu = await client.GetGpuStatusAsync(),
            services = await client.GetServiceStatusAsync(),
        });
        break;
    case "run":
        var run = await client.CreateRunAsync(
            new RunRequest(
                GoAiProtocol.Version,
                options.Mode,
                [new RunMessage("user", [new ContentPart("text", options.Prompt)])]),
            $"smoke-{Guid.NewGuid():N}");
        await WriteAsync(await CompleteRunAsync(client, run.RunId));
        break;
    case "upload":
        RequireFile(options.FilePath, "--file");
        await WriteAsync(await client.UploadFileAsync(options.FilePath!, options.MediaType));
        break;
    case "smoke":
        await WriteAsync(await RunBasicSmokeAsync());
        break;
    case "live-smoke":
        await WriteAsync(await RunLiveSmokeAsync());
        break;
    default:
        throw new ArgumentException($"Unknown command: {options.Command}");
}

async Task<object> RunBasicSmokeAsync()
{
    var live = await client.GetLiveHealthAsync();
    var ready = await client.GetReadyHealthAsync();
    var capabilities = await client.GetCapabilitiesAsync();
    var models = await client.GetModelStatusAsync();
    var gpu = await client.GetGpuStatusAsync();
    Ensure(live.Status == "live", "Live health failed.");
    Ensure(ready.Status == "ready", $"Readiness failed: {ready.Reason}");
    Ensure(capabilities.ProtocolVersion == GoAiProtocol.Version, "Protocol version mismatch.");
    Ensure(capabilities.LiveCaptions?.Available == true, "Live system-audio captions are not advertised.");
    Ensure(models.ProviderReachable, "LM Studio is not reachable.");
    Ensure(gpu.Available && gpu.Devices.Count > 0, "No GPU was detected.");
    return new
    {
        passed = true,
        live = live.Status,
        ready = ready.Status,
        capabilities.ProtocolVersion,
        configuredModels = models.Models.Count,
        gpuCount = gpu.Devices.Count,
    };
}

async Task<object> RunLiveSmokeAsync()
{
    RequireFile(options.ImagePath, "--image");
    RequireFile(options.AudioPath, "--audio");
    RequireFile(options.VideoPath, "--video");
    Directory.CreateDirectory(options.OutputDirectory);

    _ = await RunBasicSmokeAsync();
    var services = await client.GetServiceStatusAsync();
    Ensure(services.All(static service => service.Reachable),
        "Not all Docker services are reachable: " + string.Join(", ", services.Where(static service => !service.Reachable).Select(static service => service.Name)));

    var general = await CreateAndCompleteRunAsync(
        RunMode.General,
        "Dies ist ein Text-Smoke-Test. Antworte in genau einem kurzen deutschen Satz zum Thema TGA-Planung.");
    EnsureEvent(general, RunEventTypes.TextDelta);
    EnsureCompletedWithModel(general, "gpt-oss-20b");
    await AssertSseResumeAsync(general);

    var math = await CreateAndCompleteRunAsync(
        RunMode.General,
        "Dies ist ein strukturierter Smoke-Test. Rufe zwingend genau einmal math.evaluate mit operation add, left [2] und right [3] auf. Antworte danach kurz mit dem Ergebnis.");
    EnsureToolEvent(math, "math.evaluate");

    var code = await CreateAndCompleteRunAsync(
        RunMode.Code,
        "Dies ist ein strukturierter Code-Smoke-Test. Rufe zwingend genau einmal fs.readText für path README.md auf und fasse das simulierte Ergebnis in einem Satz zusammen.",
        ["filesystem", "code"],
        respondToClientTools: true);
    EnsureToolEvent(code, ClientToolNames.FileSystemReadText, clientSide: true);
    EnsureCompletedWithModel(code, "ud");

    var embedding = await CreateAndCompleteRunAsync(
        RunMode.General,
        "Dies ist ein strukturierter Embedding-Smoke-Test. Rufe zwingend genau einmal context.embed mit den Inputs [\"Heizung\", \"Lüftung\"] auf und nenne danach ausschließlich die Vektordimension.");
    EnsureToolEvent(embedding, "context.embed");

    var web = await client.SearchWebAsync(new WebSearchRequest(options.SearchQuery, 3, "de-DE"));
    Ensure(web.Results.Count > 0, "SearXNG returned no web results.");
    Ensure(string.Equals(web.Provider, "searxng", StringComparison.OrdinalIgnoreCase), "Unexpected web provider.");
    var youtube = await client.SearchYouTubeAsync(new WebSearchRequest("TGA Planung", 3, "de-DE"));
    Ensure(youtube.Results.Count > 0, "YouTube fallback returned no results.");
    Ensure(youtube.IsFallback, "YouTube search did not report the configured SearXNG fallback.");
    var fetched = await client.FetchWebAsync(new WebFetchRequest("https://example.com/"));
    Ensure(fetched.IsUntrusted && fetched.Content.Length > 0, "Protected web fetch did not return untrusted content.");

    var imageUpload = await UploadWithResumeAsync(options.ImagePath!, "image/png");
    var vision = await client.AnalyzeMediaAsync(
        new MediaJobRequest(imageUpload.UploadId, "Beschreibe die sichtbaren Inhalte knapp und erfinde keine Details."),
        $"vision-smoke-{Guid.NewGuid():N}");
    var visionRun = await CompleteRunAsync(client, vision.RunId);
    EnsureEvent(visionRun, RunEventTypes.ArtifactCreated);
    EnsureCompletedWithModel(visionRun, "qwen3-vl");

    var audioUpload = await client.UploadFileAsync(options.AudioPath!, "audio/wav");
    var captionSession = await client.CreateLiveCaptionSessionAsync(
        new LiveCaptionSessionRequest(
            "de",
            LiveCaptionMode.TranslateToEnglish,
            WindowMilliseconds: 10_000,
            OverlapMilliseconds: 500));
    var captionAudio = await File.ReadAllBytesAsync(options.AudioPath!);
    var parallelGeneralAccepted = await client.CreateRunAsync(
        new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", "Antworte kurz: Sprachsteuerung und Live-Übersetzung laufen parallel.")])]),
        $"parallel-speech-smoke-{Guid.NewGuid():N}");
    var parallelGeneralTask = CompleteRunAsync(client, parallelGeneralAccepted.RunId);
    var transcriptionTask = client.TranscribeAsync(new TranscriptionRequest(audioUpload.UploadId, "de"));
    var captionTask = client.SendLiveCaptionChunkAsync(captionSession.SessionId, 0, captionAudio);
    await Task.WhenAll(parallelGeneralTask, transcriptionTask, captionTask);
    var parallelGeneral = await parallelGeneralTask;
    var transcription = await transcriptionTask;
    var caption = await captionTask;
    var completedCaptions = await client.StopLiveCaptionSessionAsync(captionSession.SessionId);
    EnsureCompletedWithModel(parallelGeneral, "gpt-oss-20b");
    Ensure(transcription.Segments.Count > 0 && !string.IsNullOrWhiteSpace(transcription.Text), "Parallel Whisper voice-control transcription returned no text.");
    Ensure(transcription.Provider.Contains("whisper", StringComparison.OrdinalIgnoreCase), "Unexpected parallel STT provider.");
    Ensure(caption.IsFinal && !string.IsNullOrWhiteSpace(caption.Text), "Live system-audio caption returned no final text.");
    Ensure(caption.Provider.Contains("whisper", StringComparison.OrdinalIgnoreCase), "Unexpected live-caption provider.");
    Ensure(captionSession.Mode == LiveCaptionMode.TranslateToEnglish, "Live-caption session did not enable real-time translation.");
    Ensure(completedCaptions.State == "completed" && completedCaptions.Transcript.Contains(caption.Text, StringComparison.Ordinal),
        "Live-caption session did not preserve the confirmed transcript.");

    var speech = await client.SynthesizeSpeechAsync(new SpeechRequest("GO AI Sprachtest für die technische Gebäudeausrüstung."));
    Ensure(string.Equals(speech.Provider, SpeechProviderIds.SupertonicF5Cuda, StringComparison.Ordinal),
        $"Speech used unexpected provider {speech.Provider}.");
    Ensure(speech.Artifact.MediaType == "audio/wav", "TTS did not create WAV audio.");
    await AssertArtifactRangeAsync(speech.Artifact);

    var videoUpload = await client.UploadFileAsync(options.VideoPath!, "video/mp4");
    var video = await client.AnalyzeMediaAsync(
        new MediaJobRequest(videoUpload.UploadId, "Fasse Bildinhalt und gesprochenen Inhalt mit Timecodes knapp zusammen."),
        $"video-smoke-{Guid.NewGuid():N}");
    var videoRun = await CompleteRunAsync(client, video.RunId);
    EnsureEvent(videoRun, RunEventTypes.ArtifactCreated);
    EnsureCompletedWithModel(videoRun, "qwen3-vl");

    var generated = await client.GenerateImageAsync(
        new ImageGenerationRequest("Technische schematische Darstellung eines Lüftungskanals auf neutralem Hintergrund", 512, 512, 424242, 1),
        $"image-smoke-{Guid.NewGuid():N}");
    var imageRun = await CompleteRunAsync(client, generated.RunId);
    var generatedArtifact = GetArtifacts(imageRun).Single();
    Ensure(generatedArtifact.Metadata?.TryGetValue("seed", out var seed) == true && seed == "424242", "Generated image seed metadata is not deterministic.");
    await AssertArtifactRangeAsync(generatedArtifact);

    return new
    {
        passed = true,
        runs = new
        {
            general = general.Snapshot.RunId,
            math = math.Snapshot.RunId,
            code = code.Snapshot.RunId,
            embedding = embedding.Snapshot.RunId,
            vision = visionRun.Snapshot.RunId,
            video = videoRun.Snapshot.RunId,
            image = imageRun.Snapshot.RunId,
        },
        webResults = web.Results.Count,
        youtubeFallback = youtube.IsFallback,
        transcriptionSegments = transcription.Segments.Count,
        liveCaption = caption.Text,
        ttsProvider = speech.Provider,
        generatedArtifact = generatedArtifact.ArtifactId,
    };
}

async Task<UploadCompleted> UploadWithResumeAsync(string filePath, string mediaType)
{
    var info = new FileInfo(filePath);
    await using var hashStream = info.OpenRead();
    var sha = Convert.ToHexString(await SHA256.HashDataAsync(hashStream)).ToLowerInvariant();
    var chunkCount = checked((int)Math.Ceiling(info.Length / (double)GoAiProtocol.UploadChunkSize));
    var created = await client.CreateUploadAsync(
        new UploadManifest(info.Name, mediaType, info.Length, sha, GoAiProtocol.UploadChunkSize, chunkCount));
    var firstLength = (int)Math.Min(info.Length, created.ChunkSize);
    var first = new byte[firstLength];
    await using (var stream = info.OpenRead())
    {
        await stream.ReadExactlyAsync(first);
    }
    var firstHash = Convert.ToHexString(SHA256.HashData(first)).ToLowerInvariant();
    await using (var chunk = new MemoryStream(first, writable: false))
    {
        _ = await client.PutUploadChunkAsync(created.UploadId, 0, chunk, firstHash);
    }
    var resumable = await client.GetUploadAsync(created.UploadId);
    Ensure(resumable.ReceivedChunks.Contains(0), "Server did not persist the resumable upload chunk.");
    return await client.ResumeUploadFileAsync(created.UploadId, filePath);
}

async Task<RunResult> CreateAndCompleteRunAsync(
    RunMode mode,
    string prompt,
    IReadOnlyList<string>? capabilities = null,
    bool respondToClientTools = false)
{
    var accepted = await client.CreateRunAsync(
        new RunRequest(
            GoAiProtocol.Version,
            mode,
            [new RunMessage("user", [new ContentPart("text", prompt)])],
            ClientCapabilities: capabilities),
        $"live-smoke-{Guid.NewGuid():N}");
    return await CompleteRunAsync(client, accepted.RunId, respondToClientTools);
}

async Task<RunResult> CompleteRunAsync(GoAiClient api, string runId, bool respondToClientTools = false)
{
    var events = new List<RunEvent>();
    long previous = 0;
    await foreach (var item in api.StreamRunEventsAsync(runId))
    {
        Ensure(item.Id > previous, $"SSE event IDs are not monotonic for {runId}.");
        previous = item.Id;
        events.Add(item);
        if (respondToClientTools && item.Type == RunEventTypes.ClientToolProposed)
        {
            var proposal = item.Data.Deserialize<ToolProposal>(GoAiProtocol.CreateJsonOptions())
                ?? throw new InvalidDataException("Client tool proposal event is invalid.");
            var result = JsonSerializer.SerializeToElement(new
            {
                path = "README.md",
                content = "# GO AI Smoke Fixture\nDer simulierte Client-Toolbroker ist erreichbar.",
            }, GoAiProtocol.CreateJsonOptions());
            await api.SubmitClientToolResultAsync(
                runId,
                new ClientToolResult(proposal.ProposalId, "completed", result));
        }
    }

    var snapshot = await api.GetRunAsync(runId);
    Ensure(snapshot.State == RunState.Completed, $"Run {runId} ended as {snapshot.State} ({snapshot.ErrorCode}).");
    Ensure(events.Any(static item => item.Type == RunEventTypes.RunCompleted), $"Run {runId} has no completion event.");
    return new RunResult(snapshot, events);
}

async Task AssertSseResumeAsync(RunResult run)
{
    var pivot = run.Events[Math.Max(0, run.Events.Count / 2)].Id;
    var resumed = new List<RunEvent>();
    await foreach (var item in client.StreamRunEventsAsync(run.Snapshot.RunId, pivot))
    {
        resumed.Add(item);
    }
    Ensure(resumed.Count > 0 && resumed.All(item => item.Id > pivot), "SSE Last-Event-ID resume failed.");
    var expected = run.Events.Where(item => item.Id > pivot).Select(item => item.Id);
    Ensure(expected.SequenceEqual(resumed.Select(static item => item.Id)), "SSE resumed event sequence differs from persisted events.");
}

async Task AssertArtifactRangeAsync(ArtifactDescriptor artifact)
{
    var path = Path.Combine(options.OutputDirectory, artifact.ArtifactId + Path.GetExtension(artifact.FileName));
    await client.DownloadArtifactAsync(artifact.ArtifactId, path);
    var full = await File.ReadAllBytesAsync(path);
    Ensure(full.LongLength == artifact.Length, "Artifact length mismatch.");
    Ensure(Convert.ToHexString(SHA256.HashData(full)).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase), "Artifact SHA-256 mismatch.");
    var partialLength = Math.Max(1, full.Length / 3);
    await File.WriteAllBytesAsync(path, full[..partialLength]);
    await client.DownloadArtifactAsync(artifact.ArtifactId, path, partialLength);
    var resumed = await File.ReadAllBytesAsync(path);
    Ensure(resumed.AsSpan().SequenceEqual(full), "HTTP Range artifact resume failed.");
}

static IReadOnlyList<ArtifactDescriptor> GetArtifacts(RunResult run) => run.Events
    .Where(static item => item.Type == RunEventTypes.ArtifactCreated)
    .Select(static item => item.Data.Deserialize<ArtifactDescriptor>(GoAiProtocol.CreateJsonOptions())
        ?? throw new InvalidDataException("Artifact event is invalid."))
    .ToArray();

static void EnsureEvent(RunResult run, string eventType) =>
    Ensure(run.Events.Any(item => item.Type == eventType), $"Run {run.Snapshot.RunId} has no {eventType} event.");

static void EnsureToolEvent(RunResult run, string toolName, bool clientSide = false)
{
    var eventType = clientSide ? RunEventTypes.ClientToolProposed : RunEventTypes.ServerToolStarted;
    var found = run.Events.Where(item => item.Type == eventType).Any(item =>
        item.Data.TryGetProperty(clientSide ? "name" : "tool", out var tool)
        && string.Equals(tool.GetString(), toolName, StringComparison.Ordinal));
    Ensure(found, $"Run {run.Snapshot.RunId} did not invoke {toolName}.");
}

static void EnsureCompletedWithModel(RunResult run, string modelFragment)
{
    Ensure(run.Snapshot.SelectedModel?.Contains(modelFragment, StringComparison.OrdinalIgnoreCase) == true,
        $"Run {run.Snapshot.RunId} selected unexpected model {run.Snapshot.SelectedModel}.");
}

static void RequireFile(string? path, string argument)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
    {
        throw new ArgumentException($"{argument} must reference an existing file.");
    }
}

static void Ensure(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

Task WriteAsync<T>(T value)
{
    Console.WriteLine(JsonSerializer.Serialize(value, json));
    return Task.CompletedTask;
}

internal sealed record RunResult(RunSnapshot Snapshot, IReadOnlyList<RunEvent> Events);

internal sealed record Arguments(
    string Command,
    string ServerUrl,
    string ApiKey,
    string? RootCertificatePath,
    string Prompt,
    RunMode Mode,
    string? FilePath,
    string MediaType,
    string? ImagePath,
    string? AudioPath,
    string? VideoPath,
    string OutputDirectory,
    string SearchQuery)
{
    public static Arguments Parse(string[] args)
    {
        var command = args.FirstOrDefault(static value => !value.StartsWith("--", StringComparison.Ordinal)) ?? "smoke";
        string? Read(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        var server = Read("--server") ?? Environment.GetEnvironmentVariable("GO_AI_SERVER_URL") ?? "http://127.0.0.1:7080";
        var key = Read("--key") ?? Environment.GetEnvironmentVariable("GO_AI_API_KEY")
            ?? throw new ArgumentException("Pass --key or set GO_AI_API_KEY.");
        var mode = Enum.TryParse<RunMode>(Read("--mode"), ignoreCase: true, out var parsedMode) ? parsedMode : RunMode.General;
        var output = Read("--output") ?? Path.Combine(Path.GetTempPath(), "go-ai-smoke-artifacts");
        return new Arguments(
            command,
            server,
            key,
            Read("--ca") ?? Environment.GetEnvironmentVariable("GO_AI_ROOT_CERTIFICATE"),
            Read("--prompt") ?? "Antworte nur mit: GO AI bereit.",
            mode,
            Read("--file"),
            Read("--media-type") ?? "application/octet-stream",
            Read("--image"),
            Read("--audio"),
            Read("--video"),
            Path.GetFullPath(output),
            Read("--search-query") ?? "VDI Richtlinie technische Gebäudeausrüstung");
    }
}
