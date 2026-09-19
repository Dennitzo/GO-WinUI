namespace GoWinUI.Core.Coding;

public static class WorkspaceFilePath
{
    public static string Resolve(string root, string relative)
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new InvalidOperationException("Zuerst einen vorhandenen Workspace auswählen.");
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')
            || relative.Any(char.IsControl) || relative.Length > 1024)
            throw new UnauthorizedAccessException("Ein relativer Workspace-Pfad ist erforderlich.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Der Pfad verlässt den Workspace.");
        if (Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("Git-Interna sind kein Werkzeugziel.");
        for (var part = path; !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && File.GetAttributes(part).HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Verknüpfungen sind als Workspace-Ziel nicht erlaubt.");
        return path;
    }
}
