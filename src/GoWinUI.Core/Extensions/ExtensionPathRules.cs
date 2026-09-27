namespace GoWinUI.Core.Extensions;

public static class ExtensionPathRules
{
    public static bool TryNormalizeRelativePath(string? path, out string normalized, out string error)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Der Paketpfad fehlt.";
            return false;
        }

        if (path.Length > 512 || path.Contains('\0'))
        {
            error = "Der Paketpfad ist zu lang oder enthält ein Nullzeichen.";
            return false;
        }

        var portable = path.Replace('\\', '/');
        if (portable.StartsWith('/')
            || portable.Contains(':', StringComparison.Ordinal)
            || Path.IsPathRooted(path))
        {
            error = "Absolute Paketpfade sind nicht zulässig.";
            return false;
        }

        var isDirectory = portable.EndsWith('/');
        var segments = portable.Split('/', StringSplitOptions.None);
        var count = isDirectory ? segments.Length - 1 : segments.Length;
        if (count == 0)
        {
            error = "Ein leerer Paketpfad ist nicht zulässig.";
            return false;
        }

        var safeSegments = new string[count];
        for (var index = 0; index < count; index++)
        {
            var segment = segments[index];
            if (segment.Length == 0 || segment is "." or "..")
            {
                error = "Leere Segmente sowie '.' und '..' sind in Paketpfaden nicht zulässig.";
                return false;
            }

            if (segment.Length > 128
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || segment.EndsWith(' ')
                || segment.EndsWith('.'))
            {
                error = $"Das Pfadsegment '{segment}' ist nicht portabel.";
                return false;
            }

            safeSegments[index] = segment;
        }

        normalized = string.Join('/', safeSegments) + (isDirectory ? "/" : string.Empty);
        error = string.Empty;
        return true;
    }

    public static string ResolveInside(string root, string normalizedRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!TryNormalizeRelativePath(normalizedRelativePath, out var normalized, out var error))
            throw new InvalidDataException(error);

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Der Paketpfad verlässt das Zielverzeichnis.");
        return candidate;
    }
}
