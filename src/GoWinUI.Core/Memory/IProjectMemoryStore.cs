namespace GoWinUI.Core.Memory;

public interface IProjectMemoryStore
{
    string DatabasePath { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<bool> CheckIntegrityAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectMemoryEntry>> ListAsync(ProjectMemoryScope scope, string? search = null, CancellationToken cancellationToken = default);
    Task<ProjectMemoryEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ProjectMemoryEntry> CreateAsync(ProjectMemoryDraft draft, CancellationToken cancellationToken = default);
    Task<ProjectMemoryEntry?> CreateIfAutoCaptureEnabledAsync(ProjectMemoryDraft draft, CancellationToken cancellationToken = default);
    Task<ProjectMemoryEntry> UpdateAsync(Guid id, long expectedVersion, ProjectMemoryUpdate update, CancellationToken cancellationToken = default);
    Task<ProjectMemoryEntry> SetPinnedAsync(Guid id, long expectedVersion, bool isPinned, CancellationToken cancellationToken = default);
    Task<ProjectMemoryEntry> ConfirmAsync(Guid id, long expectedVersion, DateTimeOffset? confirmedAt = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, long expectedVersion, CancellationToken cancellationToken = default);
    Task<ProjectMemorySettings> GetSettingsAsync(ProjectMemoryScope scope, CancellationToken cancellationToken = default);
    Task<ProjectMemorySettings> SetAutoCaptureAsync(ProjectMemoryScope scope, bool enabled, CancellationToken cancellationToken = default);
}
