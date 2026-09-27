using System.Runtime.InteropServices;

namespace GoWinUI.Core.Memory;

public enum ProjectMemoryKind
{
    Requirement,
    Decision,
    Milestone,
    ErrorResolution,
    Verification,
}

public sealed record ProjectMemoryScope(string ProjectKey, string? ProjectId)
{
    public static ProjectMemoryScope FromWorkspace(string workspacePath, string? projectId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath.Trim()));
        var comparisonKey = ResolveWorkspaceIdentity(fullPath).Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
        {
            comparisonKey = comparisonKey.Replace(Path.AltDirectorySeparatorChar, '/');
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            comparisonKey = comparisonKey.ToUpperInvariant();
        }

        return new ProjectMemoryScope($"workspace:{comparisonKey}", NormalizeProjectId(projectId));
    }

    private static string ResolveWorkspaceIdentity(string fullPath)
    {
        try
        {
            var current = new DirectoryInfo(fullPath);
            while (current is not null)
            {
                var dotGitPath = Path.Combine(current.FullName, ".git");
                string? commonGitDirectory = null;
                if (Directory.Exists(dotGitPath))
                {
                    commonGitDirectory = Path.GetFullPath(dotGitPath);
                }
                else if (File.Exists(dotGitPath))
                {
                    var pointer = File.ReadLines(dotGitPath).FirstOrDefault()?.Trim();
                    const string prefix = "gitdir:";
                    if (pointer?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        var referenced = pointer[prefix.Length..].Trim();
                        var gitDirectory = Path.GetFullPath(
                            Path.IsPathRooted(referenced)
                                ? referenced
                                : Path.Combine(current.FullName, referenced));
                        var commonDirectoryFile = Path.Combine(gitDirectory, "commondir");
                        if (File.Exists(commonDirectoryFile))
                        {
                            var common = File.ReadLines(commonDirectoryFile).FirstOrDefault()?.Trim();
                            if (!string.IsNullOrWhiteSpace(common))
                            {
                                commonGitDirectory = Path.GetFullPath(
                                    Path.IsPathRooted(common)
                                        ? common
                                        : Path.Combine(gitDirectory, common));
                            }
                        }
                        commonGitDirectory ??= gitDirectory;
                    }
                }

                if (commonGitDirectory is not null)
                {
                    var relativeWorkspace = Path.GetRelativePath(current.FullName, fullPath);
                    return string.Equals(relativeWorkspace, ".", StringComparison.Ordinal)
                        ? $"git:{Path.TrimEndingDirectorySeparator(commonGitDirectory)}"
                        : $"git:{Path.TrimEndingDirectorySeparator(commonGitDirectory)}|{relativeWorkspace}";
                }

                current = current.Parent;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Non-Git workspaces and temporarily unavailable metadata retain the durable path identity.
        }

        return fullPath;
    }

    public static ProjectMemoryScope FromProjectId(string projectId)
    {
        var normalized = NormalizeProjectId(projectId)
            ?? throw new ArgumentException("Die Projekt-ID darf nicht leer sein.", nameof(projectId));
        return new ProjectMemoryScope($"project:{normalized.ToUpperInvariant()}", normalized);
    }

    public static ProjectMemoryScope Parse(string projectKey, string? projectId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        var normalizedKey = projectKey.Trim();
        if (normalizedKey.Length > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(projectKey), "Der Projektschlüssel ist zu lang.");
        }

        return new ProjectMemoryScope(normalizedKey, NormalizeProjectId(projectId));
    }

    private static string? NormalizeProjectId(string? projectId)
    {
        var normalized = projectId?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(projectId), "Die Projekt-ID ist zu lang.");
        }

        return normalized;
    }
}

public sealed record ProjectMemoryEntry(
    Guid Id,
    string ProjectKey,
    string? ProjectId,
    ProjectMemoryKind Kind,
    string Content,
    string Source,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastConfirmedAt,
    bool IsPinned);

public sealed record ProjectMemoryDraft(
    ProjectMemoryScope Scope,
    ProjectMemoryKind Kind,
    string Content,
    string Source,
    DateTimeOffset? LastConfirmedAt = null);

public sealed record ProjectMemoryUpdate(
    ProjectMemoryKind Kind,
    string Content,
    string Source,
    DateTimeOffset? LastConfirmedAt);

public sealed record ProjectMemorySettings(
    string ProjectKey,
    string? ProjectId,
    bool AutoCaptureEnabled,
    DateTimeOffset UpdatedAt);

public sealed class ProjectMemoryVersionConflictException(Guid id) : InvalidOperationException(
    $"Der Gedächtniseintrag '{id:D}' wurde zwischenzeitlich geändert oder gelöscht.")
{
    public Guid EntryId { get; } = id;
}
