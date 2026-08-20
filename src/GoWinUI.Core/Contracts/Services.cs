using GoWinUI.Core.Models;

namespace GoWinUI.Core.Contracts;

public interface IGoDatabase
{
    string DatabasePath { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<bool> CheckIntegrityAsync(CancellationToken cancellationToken = default);
}

public interface IChatRepository
{
    Task<IReadOnlyList<ChatSession>> ListSessionsAsync(string? search = null, CancellationToken cancellationToken = default);
    Task<ChatSession?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ChatSession> CreateSessionAsync(string title, CancellationToken cancellationToken = default);
    Task RenameSessionAsync(Guid id, string title, CancellationToken cancellationToken = default);
    Task DeleteSessionAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveDraftAsync(Guid id, string draft, CancellationToken cancellationToken = default);
    Task SelectWorkflowAsync(Guid id, Guid? workflowId, CancellationToken cancellationToken = default);
    Task SetAssistantContextAsync(
        Guid id,
        AssistantMode mode,
        string? workspacePath,
        string? workspaceFingerprint,
        CancellationToken cancellationToken = default);
    Task SetPersistentToolActionAsync(
        Guid id,
        PersistentToolAction? action,
        CancellationToken cancellationToken = default);
    Task SetPinnedAsync(Guid id, bool isPinned, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<ChatMessage> AddMessageAsync(
        Guid sessionId,
        ChatRole role,
        string content,
        MessageStatus status,
        MessageContentProfile contentProfile = MessageContentProfile.General,
        CancellationToken cancellationToken = default);
    Task UpdateMessageAsync(Guid messageId, string content, MessageStatus status, string? errorMessage = null, CancellationToken cancellationToken = default);
    Task SetMessageContextSummaryAsync(Guid messageId, string contextSummary, CancellationToken cancellationToken = default);
    Task<SessionContextPreparation?> GetSessionContextPreparationAsync(string cacheKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionContextPreparation>> ListSessionContextPreparationsAsync(
        Guid sessionId,
        string modelId,
        int maximumMessageCount,
        SessionContextProfile profile = SessionContextProfile.General,
        CancellationToken cancellationToken = default);
    Task SaveSessionContextPreparationAsync(SessionContextPreparation preparation, CancellationToken cancellationToken = default);
    Task SetToolExecutionAsync(Guid messageId, ToolExecutionInfo execution, CancellationToken cancellationToken = default);
    Task SetCodeDiffAsync(Guid messageId, string? codeDiff, CancellationToken cancellationToken = default);
    Task<int> MarkStreamingMessagesInterruptedAsync(CancellationToken cancellationToken = default);
}

public interface IWorkflowRepository
{
    Task<IReadOnlyList<WorkflowDefinition>> ListAsync(string? search = null, CancellationToken cancellationToken = default);
    Task<WorkflowDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<WorkflowDefinition> CreateAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default);
    Task<WorkflowDefinition> UpdateAsync(WorkflowDefinition workflow, long expectedRevision, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default);
    Task<WorkflowDefinition> CloneAsync(Guid id, string title, CancellationToken cancellationToken = default);
}

public interface IPromptTriggerRepository
{
    Task<IReadOnlyList<PromptTrigger>> ListAsync(CancellationToken cancellationToken = default);
    Task<PromptTrigger?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PromptTrigger> CreateAsync(PromptTrigger trigger, CancellationToken cancellationToken = default);
    Task<PromptTrigger> UpdateAsync(PromptTrigger trigger, long expectedRevision, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PromptTriggerMatch?> MatchAsync(string prompt, CancellationToken cancellationToken = default);
}

public interface IAssistantAttachmentRepository
{
    Task<IReadOnlyList<AssistantAttachment>> ListAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<AssistantAttachment> ImportAsync(Guid sessionId, string fileName, string contentType, Stream content, CancellationToken cancellationToken = default);
    Task<AssistantAttachment?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IChatArtifactRepository
{
    Task<IReadOnlyList<ChatArtifact>> ListForMessageAsync(Guid messageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<ChatArtifact>>> ListForSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<ChatArtifact?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ChatArtifact> ImportAsync(
        Guid messageId,
        string serverArtifactId,
        string fileName,
        string contentType,
        string sha256,
        long length,
        string provider,
        IReadOnlyDictionary<string, string>? metadata,
        Stream content,
        CancellationToken cancellationToken = default);
}

public interface IGoAiRunRepository
{
    Task<GoAiRunRecord> CreateAsync(GoAiRunRecord run, CancellationToken cancellationToken = default);
    Task<GoAiRunRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<GoAiRunRecord?> GetByServerRunIdAsync(string serverRunId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GoAiRunRecord>> ListResumableAsync(CancellationToken cancellationToken = default);
    Task UpdateAsync(
        Guid id,
        string? serverRunId,
        long lastEventId,
        string state,
        string? selectedModel = null,
        string? errorCode = null,
        CancellationToken cancellationToken = default);
}

public interface IClientToolExecutionRepository
{
    Task<ClientToolExecutionRecord?> GetAsync(string proposalId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClientToolExecutionRecord>> ListPendingSubmissionsAsync(Guid localRunId, CancellationToken cancellationToken = default);
    Task<ClientToolExecutionRecord> BeginAsync(ClientToolExecutionRecord execution, CancellationToken cancellationToken = default);
    Task<ClientToolExecutionRecord> CompleteAsync(string proposalId, string resultJson, CancellationToken cancellationToken = default);
    Task MarkSubmittedAsync(string proposalId, CancellationToken cancellationToken = default);
}

public interface IAiSecretStore
{
    Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default);
    Task SetApiKeyAsync(string value, CancellationToken cancellationToken = default);
    Task DeleteApiKeyAsync(CancellationToken cancellationToken = default);
}

public interface IProjectRepository
{
    Task<IReadOnlyList<Project>> ListAsync(ProjectStatus? status = null, CancellationToken cancellationToken = default);
    Task<Project?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Project> CreateAsync(Project project, CancellationToken cancellationToken = default);
    Task<Project> UpdateAsync(Project project, long expectedRevision, CancellationToken cancellationToken = default);
    Task ArchiveAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default);
    Task RestoreAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChecklistItem>> ListChecklistAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ChecklistItem> SaveChecklistItemAsync(ChecklistItem item, long? expectedRevision = null, CancellationToken cancellationToken = default);
    Task MoveChecklistItemAsync(Guid projectId, Guid itemId, int direction, long expectedRevision, CancellationToken cancellationToken = default);
    Task DeleteChecklistItemAsync(Guid itemId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectAsset>> ListAssetsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ProjectAsset> AddAssetAsync(ProjectAsset asset, CancellationToken cancellationToken = default);
    Task<ProjectAsset> UpdateAssetAsync(ProjectAsset asset, long expectedRevision, CancellationToken cancellationToken = default);
    Task MoveAssetAsync(Guid projectId, Guid itemId, int direction, long expectedRevision, CancellationToken cancellationToken = default);
    Task DeleteAssetAsync(Guid assetId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<AssetThumbnail?> GetAssetThumbnailAsync(Guid assetId, CancellationToken cancellationToken = default);
    Task SaveAssetThumbnailAsync(AssetThumbnail thumbnail, CancellationToken cancellationToken = default);
    Task DeleteAssetThumbnailAsync(Guid assetId, CancellationToken cancellationToken = default);
}

public interface IBinaryObjectStore
{
    Task<BinaryObjectDescriptor> ImportAsync(Stream source, string contentType, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(Guid id, CancellationToken cancellationToken = default);
    Task ExportAsync(Guid id, Stream destination, CancellationToken cancellationToken = default);
    Task<bool> VerifyAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteIfUnreferencedAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IDocumentIngestor
{
    IReadOnlySet<string> SupportedExtensions { get; }
    Task<DocumentIngestResult> ImportAsync(Guid sessionId, string fileName, Stream content, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredDocument>> ListAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentPage>> ReadPagesAsync(Guid documentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentContextHit>> SearchAsync(Guid sessionId, string query, int maximumCharacters = 160_000, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentContextHit>> SearchHybridAsync(Guid sessionId, string query, string embeddingModelId, IReadOnlyList<double> queryEmbedding, int maximumCharacters = 160_000, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentIndexChunk>> ListIndexChunksAsync(Guid sessionId, string embeddingModelId, CancellationToken cancellationToken = default);
    Task SaveEmbeddingsAsync(IReadOnlyList<DocumentChunkEmbedding> embeddings, CancellationToken cancellationToken = default);
    Task<DocumentContextPreparation?> GetContextPreparationAsync(string cacheKey, CancellationToken cancellationToken = default);
    Task SaveContextPreparationAsync(DocumentContextPreparation preparation, CancellationToken cancellationToken = default);
    Task SetContextPreparationStateAsync(Guid sessionId, Guid messageId, DocumentPreparationStatus? status, int progress = 100, string? errorMessage = null, CancellationToken cancellationToken = default);
    Task SaveEvidenceAsync(Guid messageId, IReadOnlyList<DocumentContextHit> evidence, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetEvidenceCitationsAsync(Guid messageId, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid documentId, CancellationToken cancellationToken = default);
}

public interface ILmStudioClient
{
    Task<IReadOnlyList<LmModel>> ListModelsAsync(CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<LmDelta> StreamAsync(LmChatRequest request, CancellationToken cancellationToken = default);
}

public interface IContextAssembler
{
    ContextBuildResult Build(ContextBuildRequest request);
}

public interface IChatOrchestrator
{
    event EventHandler<ChatStreamUpdate>? StreamUpdated;
    bool IsRunning { get; }
    Task<ChatMessage> SendAsync(Guid sessionId, string prompt, string model, string systemPrompt, string? reasoningEffort = null, CancellationToken cancellationToken = default);
    void Cancel();
}

public interface ISettingsStore
{
    string SettingsPath { get; }
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface ISessionLog
{
    event EventHandler<SessionLogEntry>? EntryAdded;
    IReadOnlyList<SessionLogEntry> Snapshot(string? minimumLevel = null, string? category = null, string? search = null);
    void Clear();
    Task ExportAsync(Stream destination, bool asJson, CancellationToken cancellationToken = default);
}

public interface IBackupService
{
    Task<BackupResult> CreateAsync(string destinationPath, CancellationToken cancellationToken = default);
    Task ValidateAsync(string backupPath, CancellationToken cancellationToken = default);
    Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default);
}
