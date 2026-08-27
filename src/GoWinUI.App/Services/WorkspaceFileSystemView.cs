using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GoWinUI.App.Services;

/// <summary>
/// Stateless view of the currently bound coding workspace. Every operation reads
/// the live filesystem; no file contents, symbols or search results are cached.
/// </summary>
public static class WorkspaceFileSystemView
{
    public const int MaximumTreeEntries = 2_000;
    public const int MaximumTreeCharacters = 65_536;
    public const long MaximumSearchableFileLength = 4L * 1024 * 1024;

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".vs", ".idea",
        ".lake", ".venv", "venv", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache",
        ".tox", ".nox", ".eggs", "site-packages", "htmlcov",
        "node_modules", "bower_components", ".npm", ".pnpm-store", ".next", ".nuxt", ".svelte-kit",
        ".turbo", ".parcel-cache",
        "target", "vendor", "bin", "obj", "artifacts", "TestResults", "coverage", "AppPackages",
        "BundleArtifacts", "Generated Files",
    };

    private static readonly HashSet<string> IgnoredFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pnp.cjs", ".pnp.loader.mjs", ".DS_Store", "Thumbs.db",
    };

    private static readonly HashSet<string> KnownBinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".avi", ".bmp", ".class", ".db", ".dll", ".doc", ".docx", ".eot", ".exe",
        ".gif", ".gz", ".ico", ".jar", ".jpeg", ".jpg", ".lockb", ".m4a", ".mov", ".mp3",
        ".mp4", ".msi", ".odt", ".pdb", ".pdf", ".png", ".ppt", ".pptx", ".pyc", ".so",
        ".sqlite", ".tar", ".tiff", ".ttf", ".wav", ".webm", ".webp", ".woff", ".woff2",
        ".xls", ".xlsx", ".zip",
    };

    public static string CreateWorkspaceIdentity(string workspace)
    {
        var normalized = NormalizeRoot(workspace).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    public static WorkspaceTreeSnapshot BuildTree(
        string workspace,
        int maximumEntries = MaximumTreeEntries,
        int maximumCharacters = MaximumTreeCharacters,
        int maximumDepth = 16)
    {
        var root = NormalizeRoot(workspace);
        maximumEntries = Math.Clamp(maximumEntries, 1, MaximumTreeEntries);
        maximumCharacters = Math.Clamp(maximumCharacters, 1_024, MaximumTreeCharacters);
        maximumDepth = Math.Clamp(maximumDepth, 1, 32);
        var entries = EnumerateEntries(root, maximumEntries + 1, maximumDepth).ToArray();
        var truncated = entries.Length > maximumEntries;
        var selected = entries.Take(maximumEntries).ToArray();
        var builder = new StringBuilder(Math.Min(maximumCharacters, Math.Max(256, selected.Length * 48)));
        builder.AppendLine("[GO_WORKSPACE_TREE]");
        builder.Append("Workspace: ").AppendLine(Path.GetFileName(root));
        builder.AppendLine("Der Baum enthält relative Pfade, aber keine Dateiinhalte.");
        foreach (var entry in selected)
        {
            var line = $"- {entry.Path}{(entry.IsDirectory ? "/" : string.Empty)}";
            if (builder.Length + line.Length + Environment.NewLine.Length > maximumCharacters)
            {
                truncated = true;
                break;
            }
            builder.AppendLine(line);
        }
        if (truncated)
        {
            builder.AppendLine("[Baum gekürzt.]");
        }
        return new WorkspaceTreeSnapshot(
            root,
            builder.ToString().TrimEnd(),
            selected.Count(static entry => !entry.IsDirectory),
            selected.Count(static entry => !entry.IsDirectory && !entry.IsBinary),
            truncated);
    }

    public static IEnumerable<WorkspacePathEntry> EnumerateEntries(
        string workspace,
        int maximumEntries = 20_000,
        int maximumDepth = 32)
    {
        var root = NormalizeRoot(workspace);
        var pending = new Stack<string>();
        pending.Push(root);
        var yielded = 0;
        while (pending.Count > 0 && yielded < maximumEntries)
        {
            var directory = pending.Pop();
            string[] children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory)
                    .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            for (var index = children.Length - 1; index >= 0; index--)
            {
                var child = children[index];
                var isDirectory = Directory.Exists(child);
                var relative = NormalizeRelative(Path.GetRelativePath(root, child));
                if (relative.Length == 0 || IsAutomaticallyIgnoredPath(relative, isDirectory))
                {
                    continue;
                }
                var depth = relative.Count(static character => character == '/') + 1;
                if (depth > maximumDepth)
                {
                    continue;
                }
                var entry = new WorkspacePathEntry(
                    relative,
                    isDirectory,
                    !isDirectory && IsProbablyBinary(child),
                    !isDirectory ? SafeLength(child) : 0);
                yield return entry;
                yielded++;
                if (yielded >= maximumEntries)
                {
                    yield break;
                }
                if (isDirectory)
                {
                    pending.Push(child);
                }
            }
        }
    }

    public static bool MatchesGlobs(
        string relativePath,
        IReadOnlyList<string> includeGlobs,
        IReadOnlyList<string> excludeGlobs)
    {
        var included = includeGlobs.Count == 0 || includeGlobs.Any(pattern => MatchesGlob(relativePath, pattern));
        return included && !excludeGlobs.Any(pattern => MatchesGlob(relativePath, pattern));
    }

    internal static bool MatchesGlob(string relativePath, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }
        var normalizedPath = NormalizeRelative(relativePath);
        var normalizedPattern = NormalizeRelative(pattern.Trim()).TrimStart('/');
        var builder = new StringBuilder("^");
        if (!normalizedPattern.Contains('/'))
        {
            builder.Append("(?:.*/)?");
        }
        for (var index = 0; index < normalizedPattern.Length; index++)
        {
            var character = normalizedPattern[index];
            if (character == '*')
            {
                var recursive = index + 1 < normalizedPattern.Length && normalizedPattern[index + 1] == '*';
                if (recursive)
                {
                    index++;
                    if (index + 1 < normalizedPattern.Length && normalizedPattern[index + 1] == '/')
                    {
                        index++;
                        builder.Append("(?:.*/)?");
                    }
                    else
                    {
                        builder.Append(".*");
                    }
                }
                else
                {
                    builder.Append("[^/]*");
                }
            }
            else if (character == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(character.ToString()));
            }
        }
        builder.Append('$');
        return Regex.IsMatch(
            normalizedPath,
            builder.ToString(),
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(250));
    }

    internal static bool IsAutomaticallyIgnoredPath(string relativePath, bool isDirectory)
    {
        var normalized = NormalizeRelative(relativePath);
        var name = Path.GetFileName(normalized.TrimEnd('/'));
        if (isDirectory && (IgnoredDirectoryNames.Contains(name)
            || name.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        if (!isDirectory && (IgnoredFileNames.Contains(name)
            || name.EndsWith(".user", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".suo", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("npm-debug.log", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("yarn-error.log", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var directorySegments = isDirectory ? segments.Length : Math.Max(0, segments.Length - 1);
        for (var index = 0; index < directorySegments; index++)
        {
            var directory = string.Join('/', segments.Take(index + 1));
            var directoryName = Path.GetFileName(directory);
            if (IgnoredDirectoryNames.Contains(directoryName)
                || directoryName.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        var surrounded = "/" + normalized.Trim('/') + "/";
        return surrounded.Contains("/.yarn/cache/", StringComparison.OrdinalIgnoreCase)
            || surrounded.Contains("/.yarn/unplugged/", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsProbablyBinary(string path)
    {
        if (KnownBinaryExtensions.Contains(Path.GetExtension(path)))
        {
            return true;
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> buffer = stackalloc byte[4_096];
            var read = stream.Read(buffer);
            return buffer[..read].Contains((byte)0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static string NormalizeRoot(string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Der freigegebene Workspace wurde nicht gefunden: {root}");
        }
        return root;
    }

    private static string NormalizeRelative(string value) => value
        .Replace('\\', '/')
        .TrimStart('.', '/');
}

public sealed record WorkspaceTreeSnapshot(
    string Root,
    string Tree,
    int FileCount,
    int TextFileCount,
    bool IsTruncated);

public sealed record WorkspacePathEntry(
    string Path,
    bool IsDirectory,
    bool IsBinary,
    long Length);
