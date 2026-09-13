using System.Diagnostics;

namespace GoWinUI.Core.Coding;

public static class CodingWorkspaceGit
{
    /// <summary>Initialize an unversioned workspace without staging, committing or replacing an existing repository.</summary>
    public static async Task EnsureRepositoryAsync(string workspace, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(workspace);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Der Coding-Projektordner existiert nicht.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var probe = await GitAsync(root, ["rev-parse", "--is-inside-work-tree"], timeout.Token).ConfigureAwait(false);
        if (probe.ExitCode == 0 && probe.Output.Trim() == "true") return;
        // Linked worktrees and existing repositories must never be reinitialized on a probe error.
        for (string? parent = root; parent is not null; parent = Path.GetDirectoryName(parent))
            if (File.Exists(Path.Combine(parent, ".git")) || Directory.Exists(Path.Combine(parent, ".git")))
                throw new IOException("Das vorhandene Git-Repository ist nicht zugänglich: " + probe.Error.Trim());
        var initialized = await GitAsync(root, ["init", "--quiet"], timeout.Token).ConfigureAwait(false);
        if (initialized.ExitCode != 0) throw new IOException("Der Coding-Projektordner konnte nicht als Git-Repository initialisiert werden: " + initialized.Error.Trim());
    }

    private static async Task<(int ExitCode, string Output, string Error)> GitAsync(string root, string[] arguments, CancellationToken cancellationToken)
    {
        var installedGit = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe");
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() && File.Exists(installedGit) ? installedGit : "git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY" }) start.Environment.Remove(name);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Git konnte nicht gestartet werden.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
