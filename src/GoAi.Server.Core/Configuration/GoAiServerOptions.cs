namespace GoAi.Server.Core.Configuration;

public sealed class GoAiServerOptions
{
    public const string SectionName = "GoAiServer";

    public string DataDirectory { get; set; } = ResolveDefaultDataDirectory();

    public string ExpectedLanIp { get; set; } = "192.168.0.67";

    public string PublicUrl { get; set; } = "https://192.168.0.67:8443";

    public int GatewayPort { get; set; } = 7080;

    public Uri LmStudioUri { get; set; } = new("http://127.0.0.1:1234", UriKind.Absolute);

    public Uri SearxngUri { get; set; } = new("http://127.0.0.1:7081", UriKind.Absolute);

    public Uri SpeechWorkerUri { get; set; } = new("http://127.0.0.1:7082", UriKind.Absolute);

    public Uri MediaWorkerUri { get; set; } = new("http://127.0.0.1:7083", UriKind.Absolute);

    public Uri ImageWorkerUri { get; set; } = new("http://127.0.0.1:7084", UriKind.Absolute);

    public string GeneralModelId { get; set; } = "openai/gpt-oss-20b";

    public int GeneralContextLength { get; set; } = 131_072;

    public string CodeModelId { get; set; } = "poolside/laguna-s-2.1";

    // Laguna always owns the exclusive GPU lane, so its full context profile is safe.
    public int CodeContextLength { get; set; } = 262_144;

    public string VisionModelId { get; set; } = "qwen3-vl-30b-a3b-instruct";

    public string VisionFallbackModelId { get; set; } = "qwen3-vl-8b-instruct";

    public string EmbeddingModelId { get; set; } = "text-embedding-bge-m3";

    public string? YouTubeApiKey { get; set; }

    public int ModelTtlSeconds { get; set; } = 600;

    public int MaximumModelRounds { get; set; } = 12;

    public int MaximumToolCalls { get; set; } = 30;

    public bool RequireLmStudioAuthentication { get; set; } = true;

    public string? ProviderDataDirectory { get; set; }

    public string? LmStudioTokenFile { get; set; }

    public string? WorkerKeyDirectory { get; set; }

    /// <summary>
    /// Optional shared data root mounted into the media workers. This is normally
    /// identical to <see cref="DataDirectory"/> and is overridden only by isolated
    /// live-smoke hosts whose database and client keys must remain temporary.
    /// </summary>
    public string? WorkerDataDirectory { get; set; }

    public string DatabasePath => Path.Combine(DataDirectory, "Data", "go-ai-server.db");

    public string UploadDirectory => Path.Combine(GetWorkerDataDirectory(), "Uploads");

    public string ResolvedWorkerDataDirectory => GetWorkerDataDirectory();

    public string ArtifactDirectory => Path.Combine(DataDirectory, "Artifacts");

    public string WorkerArtifactDirectory => Path.Combine(GetWorkerDataDirectory(), "Artifacts", "worker");

    public string SecretDirectory => Path.Combine(DataDirectory, "Secrets");

    public string LogDirectory => Path.Combine(DataDirectory, "Logs");

    public string LmStudioTokenPath => !string.IsNullOrWhiteSpace(LmStudioTokenFile)
        ? Path.GetFullPath(LmStudioTokenFile)
        : Path.Combine(GetProviderSecretDirectory(), "lmstudio-token.dpapi");

    public string BootstrapKeyExportPath => Path.Combine(SecretDirectory, "bootstrap-client-key.once");

    public string GetWorkerKeyPath(string workerName)
    {
        var fileName = workerName switch
        {
            "speech" => "speech-worker.key",
            "media" => "media-worker.key",
            "image" => "image-worker.key",
            _ => throw new ArgumentOutOfRangeException(nameof(workerName), workerName, "Unknown worker."),
        };
        var directory = string.IsNullOrWhiteSpace(WorkerKeyDirectory)
            ? GetProviderSecretDirectory()
            : Path.GetFullPath(WorkerKeyDirectory);
        return Path.Combine(directory, fileName);
    }

    private string GetProviderSecretDirectory() => string.IsNullOrWhiteSpace(ProviderDataDirectory)
        ? SecretDirectory
        : Path.Combine(Path.GetFullPath(ProviderDataDirectory), "Secrets");

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

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GO-AI-Server");
    }
}
