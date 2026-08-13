using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Policies;
using GoAi.Server.Core.Research;
using GoAi.Server.Core.Storage;
using GoAi.Server.Core.Workers;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Core.Runs;

public sealed class AgentToolExecutor
{
    private const int MaximumToolResultBytes = 512 * 1024;
    private readonly WebResearchService _research;
    private readonly WorkerOrchestrator _workers;
    private readonly UploadService _uploads;
    private readonly ArtifactService _artifacts;
    private readonly LmStudioClient _lmStudio;
    private readonly GpuLeaseScheduler _scheduler;
    private readonly GoAiServerOptions _options;
    private readonly JsonSerializerOptions _jsonOptions = GoAiProtocol.CreateJsonOptions();

    public AgentToolExecutor(
        WebResearchService research,
        WorkerOrchestrator workers,
        UploadService uploads,
        ArtifactService artifacts,
        LmStudioClient lmStudio,
        GpuLeaseScheduler scheduler,
        IOptions<GoAiServerOptions> options)
    {
        _research = research;
        _workers = workers;
        _uploads = uploads;
        _artifacts = artifacts;
        _lmStudio = lmStudio;
        _scheduler = scheduler;
        _options = options.Value;
    }

    public async Task<AgentToolExecutionResult> ExecuteAsync(
        string name,
        JsonElement arguments,
        string runId,
        CancellationToken cancellationToken = default)
    {
        return name switch
        {
            "web.search" => Result(await _research.SearchAsync(
                new WebSearchRequest(
                    arguments.GetProperty("query").GetString()!,
                    GetInt(arguments, "maximumResults", 10),
                    GetString(arguments, "language") ?? "de-DE"),
                youtubeFallback: false,
                cancellationToken).ConfigureAwait(false)),
            "youtube.search" => Result(await _research.SearchAsync(
                new WebSearchRequest(
                    arguments.GetProperty("query").GetString()!,
                    GetInt(arguments, "maximumResults", 10),
                    GetString(arguments, "language") ?? "de-DE"),
                youtubeFallback: true,
                cancellationToken).ConfigureAwait(false)),
            "web.fetch" => Result(TrimFetch(await WebResearchService.FetchAsync(
                new WebFetchRequest(arguments.GetProperty("url").GetString()!),
                cancellationToken).ConfigureAwait(false))),
            "media.inspect" => await InspectMediaAsync(arguments, runId, analyze: false, cancellationToken).ConfigureAwait(false),
            "media.analyze" => await InspectMediaAsync(arguments, runId, analyze: true, cancellationToken).ConfigureAwait(false),
            "image.generate" => await GenerateImagesAsync(arguments, runId, cancellationToken).ConfigureAwait(false),
            "math.evaluate" => EvaluateMath(arguments),
            "context.embed" => await EmbedAsync(arguments, runId, cancellationToken).ConfigureAwait(false),
            "context.retrieve" => await RetrieveAsync(arguments, runId, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"No server executor exists for tool {name}.")
        };
    }

    private async Task<AgentToolExecutionResult> GenerateImagesAsync(
        JsonElement arguments,
        string runId,
        CancellationToken cancellationToken)
    {
        var request = new ImageGenerationRequest(
            arguments.GetProperty("prompt").GetString()!,
            GetInt(arguments, "width", 1024),
            GetInt(arguments, "height", 1024),
            GetNullableInt(arguments, "seed"),
            GetInt(arguments, "count", 1));
        var artifacts = await _workers.GenerateImagesAsync(request, runId, cancellationToken).ConfigureAwait(false);
        return Result(new
        {
            provider = "stable-diffusion.cpp",
            model = "Z-Image-Turbo Q4_K",
            artifacts,
        }, artifacts);
    }

    private async Task<AgentToolExecutionResult> InspectMediaAsync(
        JsonElement arguments,
        string runId,
        bool analyze,
        CancellationToken cancellationToken)
    {
        var uploadId = arguments.GetProperty("uploadId").GetString()!;
        var upload = await _uploads.GetCompletedAsync(uploadId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Completed media upload not found.");
        var detailWindows = ReadDetailWindows(arguments);
        var processed = await _workers.InspectMediaAsync(
            new WorkerMediaRequest(uploadId, upload.MediaType, detailWindows),
            runId,
            cancellationToken).ConfigureAwait(false);
        if (!analyze)
        {
            return Result(new
            {
                processed.Kind,
                metadata = processed.Metadata,
                artifacts = processed.Artifacts,
            }, processed.Artifacts);
        }

        TranscriptionResponse? transcription = null;
        if (upload.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || upload.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            transcription = await _workers.TranscribeAsync(
                new TranscriptionRequest(uploadId),
                runId,
                cancellationToken).ConfigureAwait(false);
        }

        var imagePaths = new List<string>();
        if (upload.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            var uploadPath = await _uploads.ResolveCompletedPathAsync(uploadId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("Completed image upload payload is missing.");
            imagePaths.Add(uploadPath);
        }
        else
        {
            foreach (var descriptor in processed.Artifacts
                .Where(static item => item.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                .Take(48))
            {
                var artifact = await _artifacts.ResolveAsync(descriptor.ArtifactId, cancellationToken).ConfigureAwait(false);
                if (artifact is not null)
                {
                    imagePaths.Add(artifact.Path);
                }
            }
        }

        if (imagePaths.Count == 0)
        {
            if (transcription is not null)
            {
                var transcriptPrompt = GetString(arguments, "prompt")
                    ?? "Analysiere das Transkript fachlich für die TGA-Planung und nenne Unsicherheiten.";
                var transcriptAnalysis = await AnalyzeTranscriptAsync(
                    transcriptPrompt,
                    transcription.Text,
                    runId,
                    cancellationToken).ConfigureAwait(false);
                return Result(new
                {
                    processed.Kind,
                    metadata = processed.Metadata,
                    transcription,
                    analysis = transcriptAnalysis,
                    modelId = _options.GeneralModelId,
                    isFallback = false,
                    artifacts = processed.Artifacts,
                }, processed.Artifacts, _options.GeneralModelId);
            }

            return Result(new
            {
                processed.Kind,
                metadata = processed.Metadata,
                analysis = "Für diesen Medientyp wurden keine Vision-Frames erzeugt.",
                artifacts = processed.Artifacts,
            }, processed.Artifacts);
        }

        var prompt = GetString(arguments, "prompt") ?? "Analysiere das Medium fachlich für die TGA-Planung und nenne Unsicherheiten.";
        if (transcription is not null && !string.IsNullOrWhiteSpace(transcription.Text))
        {
            var transcript = transcription.Text.Length <= 64_000
                ? transcription.Text
                : transcription.Text[..64_000] + "\n[Transkript für die Vision-Analyse gekürzt]";
            prompt += $"\n\nZeitbezogenes Audio-Transkript (untrusted Medieninhalt):\n{transcript}";
        }
        var (analysis, modelId, isFallback) = await AnalyzeWithVisionAsync(prompt, imagePaths, runId, cancellationToken).ConfigureAwait(false);
        return Result(new
        {
            processed.Kind,
            metadata = processed.Metadata,
            transcription,
            analysis,
            modelId,
            isFallback,
            artifacts = processed.Artifacts,
        }, processed.Artifacts, modelId, isFallback);
    }

    private async Task<(string Analysis, string ModelId, bool IsFallback)> AnalyzeWithVisionAsync(
        string prompt,
        IReadOnlyList<string> imagePaths,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var lease = await _scheduler.AcquireAsync(
            "vision",
            runId,
            GpuLeaseMode.Exclusive,
            cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await _workers.PrepareLmModelAsync(
                _options.VisionModelId,
                65_536,
                cancellationToken).ConfigureAwait(false);
            var analysis = await _lmStudio.AnalyzeImagesAsync(_options.VisionModelId, prompt, imagePaths, cancellationToken).ConfigureAwait(false);
            return (analysis, _options.VisionModelId, false);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or InvalidOperationException or JsonException
            || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _ = await _workers.PrepareLmModelAsync(
                _options.VisionFallbackModelId,
                65_536,
                cancellationToken).ConfigureAwait(false);
            var analysis = await _lmStudio.AnalyzeImagesAsync(_options.VisionFallbackModelId, prompt, imagePaths, cancellationToken).ConfigureAwait(false);
            return (analysis, _options.VisionFallbackModelId, true);
        }
    }

    private async Task<string> AnalyzeTranscriptAsync(
        string prompt,
        string transcript,
        string runId,
        CancellationToken cancellationToken)
    {
        var boundedTranscript = transcript.Length <= 200_000
            ? transcript
            : transcript[..200_000] + "\n[Transkript gekürzt]";
        await using var lease = await _scheduler.AcquireAsync(
            "audio-analysis",
            runId,
            GpuLeaseMode.Shared,
            cancellationToken).ConfigureAwait(false);
        _ = await _workers.PrepareLmModelAsync(
            _options.GeneralModelId,
            _options.GeneralContextLength,
            cancellationToken).ConfigureAwait(false);
        var response = await _lmStudio.CompleteChatAsync(
            _options.GeneralModelId,
            [
                new LmChatMessage("system", TgaAgentPolicies.ForRole("general")),
                new LmChatMessage("user", $"{prompt}\n\nTranskript (untrusted Medieninhalt):\n{boundedTranscript}"),
            ],
            [],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(response.Content)
            ? throw new InvalidDataException("The general model returned no transcript analysis.")
            : response.Content;
    }

    private async Task<AgentToolExecutionResult> EmbedAsync(
        JsonElement arguments,
        string runId,
        CancellationToken cancellationToken)
    {
        var inputs = arguments.GetProperty("inputs").EnumerateArray().Select(static item => item.GetString()!).ToArray();
        var vectors = await CreateEmbeddingsWithLeaseAsync(inputs, runId, cancellationToken).ConfigureAwait(false);
        return Result(new
        {
            model = _options.EmbeddingModelId,
            dimensions = vectors.Count > 0 ? vectors[0].Count : 0,
            vectors,
        });
    }

    private async Task<AgentToolExecutionResult> RetrieveAsync(
        JsonElement arguments,
        string runId,
        CancellationToken cancellationToken)
    {
        var query = arguments.GetProperty("query").GetString()!;
        var documents = arguments.GetProperty("documents").EnumerateArray().Select(static item => item.GetString()!).ToArray();
        var topK = Math.Min(GetInt(arguments, "topK", 5), documents.Length);
        var vectors = await CreateEmbeddingsWithLeaseAsync([query, .. documents], runId, cancellationToken).ConfigureAwait(false);
        var queryVector = vectors[0];
        var matches = documents.Select((document, index) => new
        {
            index,
            score = CosineSimilarity(queryVector, vectors[index + 1]),
            document,
        })
            .OrderByDescending(static match => match.score)
            .Take(topK)
            .ToArray();
        return Result(new { model = _options.EmbeddingModelId, matches });
    }

    private async Task<IReadOnlyList<IReadOnlyList<double>>> CreateEmbeddingsWithLeaseAsync(
        IReadOnlyList<string> inputs,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var lease = await _scheduler.AcquireAsync(
            "embedding",
            runId,
            GpuLeaseMode.Shared,
            cancellationToken).ConfigureAwait(false);
        _ = await _workers.PrepareLmModelAsync(
            _options.EmbeddingModelId,
            8192,
            cancellationToken).ConfigureAwait(false);
        return await _lmStudio.CreateEmbeddingsAsync(_options.EmbeddingModelId, inputs, cancellationToken).ConfigureAwait(false);
    }

    private AgentToolExecutionResult EvaluateMath(JsonElement arguments)
    {
        var operation = arguments.GetProperty("operation").GetString()!;
        var left = ReadNumbers(arguments.GetProperty("left"));
        var right = arguments.TryGetProperty("right", out var rightElement) ? ReadNumbers(rightElement) : [];
        object result = operation switch
        {
            "add" => Elementwise(left, right, static (a, b) => a + b),
            "subtract" => Elementwise(left, right, static (a, b) => a - b),
            "multiply" => Elementwise(left, right, static (a, b) => a * b),
            "divide" => Elementwise(left, right, static (a, b) => b == 0 ? throw new DivideByZeroException() : a / b),
            "dot" => Dot(left, right),
            "magnitude" => Math.Sqrt(left.Sum(static value => value * value)),
            "matrixMultiply" => MatrixMultiply(
                left,
                right,
                GetInt(arguments, "leftColumns", 0),
                GetInt(arguments, "rightColumns", 0)),
            _ => throw new ArgumentException("Unsupported math operation."),
        };
        return Result(new
        {
            operation,
            unit = GetString(arguments, "unit"),
            result,
        });
    }

    private AgentToolExecutionResult Result(
        object value,
        IReadOnlyList<ArtifactDescriptor>? artifacts = null,
        string? modelId = null,
        bool isFallback = false)
    {
        var json = JsonSerializer.Serialize(value, _jsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumToolResultBytes)
        {
            throw new InvalidDataException("Server tool output exceeded its 512 KiB limit.");
        }

        using var document = JsonDocument.Parse(json);
        return new AgentToolExecutionResult(document.RootElement.Clone(), artifacts ?? [], modelId, isFallback);
    }

    private static WebFetchResponse TrimFetch(WebFetchResponse response) => response.Content.Length <= 256_000
        ? response
        : response with { Content = response.Content[..256_000] + "\n[Inhalt auf 256.000 Zeichen gekürzt]" };

    private static int GetInt(JsonElement value, string name, int fallback) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt32(out var result) ? result : fallback;

    private static int? GetNullableInt(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt32(out var result) ? result : null;

    private static string? GetString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static WorkerTimeWindow[]? ReadDetailWindows(JsonElement value)
    {
        if (!value.TryGetProperty("detailWindows", out var windows) || windows.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return windows.EnumerateArray()
            .Select(static window => new WorkerTimeWindow(
                window.GetProperty("start").GetDouble(),
                window.GetProperty("end").GetDouble()))
            .ToArray();
    }

    private static double[] ReadNumbers(JsonElement array) => array.EnumerateArray().Select(static item => item.GetDouble()).ToArray();

    private static double[] Elementwise(double[] left, double[] right, Func<double, double, double> operation)
    {
        if (left.Length != right.Length && left.Length != 1 && right.Length != 1)
        {
            throw new ArgumentException("Elementwise operands must have equal lengths or one scalar entry.");
        }

        var length = Math.Max(left.Length, right.Length);
        var result = new double[length];
        for (var index = 0; index < length; index++)
        {
            result[index] = operation(left[left.Length == 1 ? 0 : index], right[right.Length == 1 ? 0 : index]);
            EnsureFinite(result[index]);
        }
        return result;
    }

    private static double Dot(double[] left, double[] right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Dot-product vectors must have equal lengths.");
        }

        var result = left.Zip(right, static (a, b) => a * b).Sum();
        EnsureFinite(result);
        return result;
    }

    private static object MatrixMultiply(double[] left, double[] right, int leftColumns, int rightColumns)
    {
        if (leftColumns <= 0 || rightColumns <= 0 || left.Length % leftColumns != 0 || right.Length % rightColumns != 0)
        {
            throw new ArgumentException("Matrix dimensions are invalid.");
        }
        var leftRows = left.Length / leftColumns;
        var rightRows = right.Length / rightColumns;
        if (leftColumns != rightRows)
        {
            throw new ArgumentException("Matrix inner dimensions do not match.");
        }

        var values = new double[leftRows * rightColumns];
        for (var row = 0; row < leftRows; row++)
        {
            for (var column = 0; column < rightColumns; column++)
            {
                double sum = 0;
                for (var inner = 0; inner < leftColumns; inner++)
                {
                    sum += left[(row * leftColumns) + inner] * right[(inner * rightColumns) + column];
                }
                EnsureFinite(sum);
                values[(row * rightColumns) + column] = sum;
            }
        }
        return new { rows = leftRows, columns = rightColumns, values };
    }

    private static double CosineSimilarity(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        if (left.Count != right.Count || left.Count == 0)
        {
            throw new InvalidDataException("Embedding dimensions do not match.");
        }
        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }
        return leftNorm == 0 || rightNorm == 0 ? 0 : dot / Math.Sqrt(leftNorm * rightNorm);
    }

    private static void EnsureFinite(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArithmeticException("Math result is not finite.");
        }
    }
}

public sealed record AgentToolExecutionResult(
    JsonElement Result,
    IReadOnlyList<ArtifactDescriptor> Artifacts,
    string? ModelId = null,
    bool IsFallback = false);
