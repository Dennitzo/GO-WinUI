using System.Buffers;

namespace GoWinUI.Core.Models;

public static class ProjectAssetFileName
{
    private static readonly SearchValues<char> InvalidCharacters = SearchValues.Create(['<', '>', ':', '"', '/', '\\', '|', '?', '*']);

    public static string Normalize(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var normalized = fileName.Trim();
        if (normalized is "." or ".."
            || normalized.EndsWith(' ')
            || normalized.EndsWith('.')
            || normalized.Any(static character => character < ' ')
            || normalized.AsSpan().IndexOfAny(InvalidCharacters) >= 0
            || IsReservedWindowsName(normalized))
        {
            throw new ArgumentException("Der Dateiname enthält unter Windows nicht zulässige Zeichen oder Namen.", nameof(fileName));
        }

        return normalized;
    }

    private static bool IsReservedWindowsName(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        return baseName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || IsNumberedDevice(baseName, "COM")
            || IsNumberedDevice(baseName, "LPT");
    }

    private static bool IsNumberedDevice(string value, string prefix) =>
        value.Length == prefix.Length + 1
        && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && value[^1] is >= '1' and <= '9';
}
