namespace GoWinUI.Infrastructure.Memory;

public sealed class ProjectMemoryOptions
{
    public string SharedDataDirectory { get; set; } = ProjectMemoryPathResolver.ResolveSharedDataDirectory();
    public string DatabaseFileName { get; set; } = "project-memory.db";
}

public static class ProjectMemoryPathResolver
{
    public const string SharedDataRootEnvironmentVariable = "ASSISTANT_SHARED_DATA_ROOT";

    public static string ResolveSharedDataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(SharedDataRootEnvironmentVariable)?.Trim();
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalAssistant",
                "Shared")
            : configured;
        return Path.GetFullPath(root);
    }
}
