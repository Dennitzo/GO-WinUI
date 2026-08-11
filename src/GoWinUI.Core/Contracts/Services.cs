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
    Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<ChatMessage> AddMessageAsync(Guid sessionId, ChatRole role, string content, MessageStatus status, CancellationToken cancellationToken = default);
    Task UpdateMessageAsync(Guid messageId, string content, MessageStatus status, string? errorMessage = null, CancellationToken cancellationToken = default);
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
