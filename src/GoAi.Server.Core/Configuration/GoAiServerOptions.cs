using GoAi.Contracts;

namespace GoAi.Server.Core.Configuration;

public sealed class GoAiServerOptions
{
    public const string SectionName = "GoAiServer";

    public string DataDirectory { get; set; } = ResolveDefaultDataDirectory();

    public string ExpectedLanIp { get; set; } = "192.168.0.67";

    public string PublicUrl { get; set; } = "http://192.168.0.67:8080";

    public int GatewayPort { get; set; } = 8080;

    public Uri ModelRuntimeUri { get; set; } = new("http://llm:8080", UriKind.Absolute);

    public Uri SearxngUri { get; set; } = new("http://searxng:8080", UriKind.Absolute);

    public Uri SpeechWorkerUri { get; set; } = new("http://speech:8080", UriKind.Absolute);

    public Uri MediaWorkerUri { get; set; } = new("http://media:8080", UriKind.Absolute);

    public Uri ImageWorkerUri { get; set; } = new("http://image:8080", UriKind.Absolute);

    public string GeneralModelId { get; set; } = "gpt-oss-120b";

    public int GeneralContextLength { get; set; } = ModelContextProfiles.GptOss120BMaximum;

    public string CodeModelId { get; set; } = CodingModelCatalog.DefaultModelId;

    public int CodeContextLength { get; set; } = ModelContextProfiles.Qwen3CoderNextMaximum;

    public string VisionModelId { get; set; } = "qwen3-vl-30b-a3b-instruct";

    public int VisionContextLength { get; set; } = ModelContextProfiles.Qwen3VlMaximum;

    public string EmbeddingModelId { get; set; } = "text-embedding-bge-m3";

    public int EmbeddingContextLength { get; set; } = ModelContextProfiles.BgeM3Maximum;

    public string? YouTubeApiKey { get; set; }

    public int ModelTtlSeconds { get; set; } = 600;

    public int MaximumModelRounds { get; set; } = 12;

    public int MaximumToolCalls { get; set; } = 30;

    public int MaximumCodingModelRounds { get; set; } = 96;

    public int MaximumCodingToolCalls { get; set; } = 192;

    /// <summary>
    /// Optional shared data root mounted into the media workers. This is normally
    /// identical to <see cref="DataDirectory"/> and is overridden only by isolated
    /// smoke hosts whose database and worker data must remain temporary.
    /// </summary>
    public string? WorkerDataDirectory { get; set; }

    public string DatabasePath => Path.Combine(DataDirectory, "database", "go-ai-server.db");

    public string UploadDirectory => Path.Combine(GetWorkerDataDirectory(), "uploads");

    public string ResolvedWorkerDataDirectory => GetWorkerDataDirectory();

    public string ArtifactDirectory => Path.Combine(DataDirectory, "artifacts");

    public string WorkerArtifactDirectory => Path.Combine(GetWorkerDataDirectory(), "artifacts", "worker");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    private string GetWorkerDataDirectory() => string.IsNullOrWhiteSpace(WorkerDataDirectory)
        ? Path.GetFullPath(DataDirectory)
        : Path.GetFullPath(WorkerDataDirectory);

    public static string ResolveDefaultDataDirectory()
    {
        var requested = Environment.GetEnvironmentVariable("GO_AI_DATA_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return Path.GetFullPath(requested);
        }

        return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GO-AI-Stack")
            : "/data";
    }
}
