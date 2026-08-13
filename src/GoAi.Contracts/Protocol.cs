using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoAi.Contracts;

public static class GoAiProtocol
{
    public const string Version = "1.0";
    public const string ApiPrefix = "/v1";
    public const int UploadChunkSize = 8 * 1024 * 1024;
    public const long MaximumJsonBytes = 2L * 1024 * 1024;
    public const long MaximumToolResultTextBytes = 4L * 1024 * 1024;
    public const int MaximumLiveCaptionChunkBytes = 512 * 1024;
    public const int LiveCaptionSampleRate = 16_000;

    public static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public static class GoAiHeaders
{
    public const string ApiKey = "X-GO-AI-Key";
    public const string LastEventId = "Last-Event-ID";
    public const string IdempotencyKey = "Idempotency-Key";
    public const string WorkerKey = "X-GO-AI-Worker-Key";
}

public sealed record GoAiProblem(
    string Type,
    string Title,
    int Status,
    string Detail,
    string? ErrorCode = null,
    string? TraceId = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);
