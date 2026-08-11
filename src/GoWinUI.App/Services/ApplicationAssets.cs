using System.Diagnostics.CodeAnalysis;

namespace GoWinUI.App.Services;

internal static class ApplicationAssets
{
    [UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000",
        Justification = "The Portable configuration extracts all bundled content before startup; the assembly directory is the bundle extraction root.")]
    public static string ResolvePath(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var defaultPath = Path.Combine([AppContext.BaseDirectory, .. segments]);
        if (File.Exists(defaultPath) || Directory.Exists(defaultPath))
        {
            return defaultPath;
        }

        var assemblyDirectory = Path.GetDirectoryName(typeof(ApplicationAssets).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            var extractedPath = Path.Combine([assemblyDirectory, .. segments]);
            if (File.Exists(extractedPath) || Directory.Exists(extractedPath))
            {
                return extractedPath;
            }
        }

        return defaultPath;
    }
}
