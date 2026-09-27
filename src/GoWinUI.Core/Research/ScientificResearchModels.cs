namespace GoWinUI.Core.Research;

public static class ResearchNodeStatuses
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "planned", "active", "supported", "provisionallySupported", "verified", "refuted", "blocked", "unresolved", "superseded",
    };
}

public sealed record ScientificResearchProject(
    string Id,
    Guid SessionId,
    string Profile,
    string OriginalQuestion,
    string InterpretedQuestion,
    string AutonomyLevel,
    string VerificationLevel,
    string Status,
    int ProtocolVersion,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? WorkspacePath = null,
    string? LatestCheckpointId = null);

public sealed record ResearchPlanNode(
    string Id,
    string ProjectId,
    string NodeType,
    string Title,
    string Status,
    int Priority,
    double Confidence,
    string EvidenceRequirementsJson,
    string VerificationRequirementsJson,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? CheckpointId = null,
    string? PayloadJson = null);

public sealed record ResearchPlanEdge(
    string Id,
    string ProjectId,
    string FromNodeId,
    string ToNodeId,
    string EdgeType,
    DateTimeOffset CreatedAt);

public sealed record ResearchCheckpoint(
    string Id,
    string ProjectId,
    string RunId,
    long Revision,
    string Stage,
    string StateJson,
    DateTimeOffset CreatedAt);

public sealed record ResearchHypothesis(
    string Id,
    string ProjectId,
    string Statement,
    string Classification,
    string Status,
    double Confidence,
    string PayloadJson,
    DateTimeOffset UpdatedAt,
    string? NodeId = null);

public sealed record ResearchExperiment(
    string Id,
    string ProjectId,
    string EnvironmentLock,
    string SourceFilesJson,
    string RandomSeedsJson,
    string InputHashesJson,
    string CommandText,
    string ResourceLimitsJson,
    string StdoutEvidence,
    string StderrEvidence,
    string ResultArtifactsJson,
    string VerificationStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? HypothesisId = null);

public sealed record ResearchVerification(
    string Id,
    string ProjectId,
    string TargetType,
    string TargetId,
    string Dimension,
    string Method,
    string Status,
    string EvidenceJson,
    DateTimeOffset CreatedAt);

public sealed record ResearchClaim(
    string Id,
    string ProjectId,
    string Statement,
    string ClaimClass,
    string ConclusionStatus,
    double Confidence,
    string PayloadJson,
    DateTimeOffset UpdatedAt);

public sealed record ResearchResultSnapshot(
    IReadOnlyList<ResearchHypothesis> Hypotheses,
    IReadOnlyList<ResearchExperiment> Experiments,
    IReadOnlyList<ResearchVerification> Verifications,
    IReadOnlyList<ResearchClaim> Claims);

public sealed record ResearchLiteratureEntry(
    string WorkId,
    string ProjectId,
    string Title,
    string CanonicalUrl,
    string VersionKind,
    string MetadataJson,
    string ScreeningStatus,
    string? EvidenceLevel,
    DateTimeOffset UpdatedAt,
    string? Doi = null,
    string? Pmid = null,
    string? Pmcid = null,
    string? ArxivId = null,
    string? DataCiteId = null,
    string? OpenAlexId = null);

public sealed record ResearchEvidenceRecord(
    string Id,
    string ProjectId,
    string WorkId,
    string ExactExcerpt,
    string NormalizedStatement,
    string ContentHash,
    string EvidenceLevel,
    string LocatorJson,
    DateTimeOffset RetrievedAt,
    string? Page = null,
    string? Section = null,
    string? TableOrFigure = null);

public sealed record ResearchArchiveSnapshot(
    int ProtocolVersion,
    string ProtocolJson,
    IReadOnlyList<ResearchLiteratureEntry> Works,
    IReadOnlyList<ResearchEvidenceRecord> Evidence,
    string ReportId,
    string ReportKind,
    string ConclusionStatus,
    string ContentMarkdown,
    string ManifestJson,
    string AuditEventType,
    string AuditPayloadJson,
    string RunId,
    long Revision,
    DateTimeOffset CreatedAt);

public sealed record ResearchStoredReport(string Id, string ProjectId, string ReportKind,
    string ConclusionStatus, string ContentMarkdown, string ManifestJson, DateTimeOffset CreatedAt);

public sealed record ScientificResearchExportBundle(string Directory, IReadOnlyList<string> Files,
    string ManifestPath, DateTimeOffset CreatedAt);

public sealed record ResearchSandboxLayout(
    string ProjectId,
    string RootPath,
    string InputsPath,
    string WorkPath,
    string ArtifactsPath,
    string NotebooksPath,
    string ManuscriptsPath,
    string EnvironmentPath,
    string RunsPath,
    string SnapshotsPath,
    DateTimeOffset CreatedAt,
    string RuntimeState);

public sealed record ResearchSandboxRunResult(
    string RunId,
    int ExitCode,
    bool TimedOut,
    string StandardOutput,
    string StandardError,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Command);

public sealed record ResearchSandboxRuntimeStatus(bool IsReady, string State, string? Detail = null);

public sealed record ResearchSandboxFileChange(string ChangeSetId, string RelativePath, string? BeforeSha256,
    string AfterSha256, bool IsNewFile);

public interface IResearchSandboxService
{
    Task<ResearchSandboxLayout> EnsureProjectAsync(string projectId, CancellationToken cancellationToken = default);
    Task<ResearchSandboxRuntimeStatus> PrepareRuntimeAsync(CancellationToken cancellationToken = default);
    Task<ResearchSandboxFileChange> WriteTextAsync(string projectId, string relativePath, string content,
        string? expectedSha256 = null, CancellationToken cancellationToken = default);
    Task RestoreChangeSetAsync(string projectId, string changeSetId, CancellationToken cancellationToken = default);
    Task<ResearchSandboxRunResult> RunPythonAsync(string projectId, string relativeScriptPath,
        IReadOnlyList<string>? arguments = null, int timeoutSeconds = 7200, string? relativeWorkingDirectory = null,
        CancellationToken cancellationToken = default);
    Task<string> ArchiveProjectAsync(string projectId, CancellationToken cancellationToken = default);
    Task RestoreProjectAsync(string projectId, CancellationToken cancellationToken = default);
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}

public interface IScientificResearchExportService
{
    Task<ScientificResearchExportBundle> ExportAllAsync(string projectId, string workspacePath,
        CancellationToken cancellationToken = default);
}

public interface IScientificResearchRepository
{
    Task<ScientificResearchProject?> GetProjectAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScientificResearchProject>> ListSessionProjectsAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task UpsertProjectAsync(ScientificResearchProject project, CancellationToken cancellationToken = default);
    Task SaveGraphAsync(string projectId, IReadOnlyList<ResearchPlanNode> nodes, IReadOnlyList<ResearchPlanEdge> edges,
        CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<ResearchPlanNode> Nodes, IReadOnlyList<ResearchPlanEdge> Edges)> LoadGraphAsync(
        string projectId, CancellationToken cancellationToken = default);
    Task SaveCheckpointAsync(ResearchCheckpoint checkpoint, CancellationToken cancellationToken = default);
    Task<ResearchCheckpoint?> GetLatestCheckpointAsync(string projectId, CancellationToken cancellationToken = default);
    Task SaveResultSnapshotAsync(string projectId, ResearchResultSnapshot snapshot,
        CancellationToken cancellationToken = default);
    Task SaveExperimentAsync(ResearchExperiment experiment, CancellationToken cancellationToken = default);
    Task<ResearchResultSnapshot> LoadResultSnapshotAsync(string projectId,
        CancellationToken cancellationToken = default);
    Task SaveArchiveSnapshotAsync(string projectId, ResearchArchiveSnapshot snapshot,
        CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<ResearchLiteratureEntry> Works, IReadOnlyList<ResearchEvidenceRecord> Evidence,
        ResearchStoredReport? Report)> LoadArchiveSnapshotAsync(string projectId,
        CancellationToken cancellationToken = default);
}
