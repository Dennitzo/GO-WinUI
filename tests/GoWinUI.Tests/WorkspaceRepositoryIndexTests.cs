using GoWinUI.App.Services;
using GoWinUI.Infrastructure;

namespace GoWinUI.Tests;

public sealed class WorkspaceRepositoryIndexTests
{
    [Fact]
    public async Task IndexKeepsDotDirectoriesAndArbitraryFileTypesButIgnoresGeneratedOutputs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"go-workspace-index-{Guid.NewGuid():N}");
        var data = Path.Combine(Path.GetTempPath(), $"go-workspace-data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".github", "workflows"));
        Directory.CreateDirectory(Path.Combine(root, ".lake", "packages", "generated-library"));
        Directory.CreateDirectory(Path.Combine(root, ".venv", "Lib", "site-packages"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "dependency"));
        Directory.CreateDirectory(Path.Combine(root, ".next", "server"));
        Directory.CreateDirectory(Path.Combine(root, "target", "debug"));
        Directory.CreateDirectory(Path.Combine(root, "vendor", "example.org", "dependency"));
        Directory.CreateDirectory(Path.Combine(root, "obj", "Debug"));
        Directory.CreateDirectory(Path.Combine(root, "AppPackages", "Generated"));
        Directory.CreateDirectory(Path.Combine(root, "firmware"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "ignored.tmp\n");
        await File.WriteAllTextAsync(Path.Combine(root, ".github", "workflows", "build.yml"), "name: Build\n");
        await File.WriteAllTextAsync(Path.Combine(root, "firmware", "main.zig"), "pub fn main() void {}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "system.customlang"), "arbitrary source\n");
        await File.WriteAllTextAsync(Path.Combine(root, "ignored.tmp"), "ignored\n");
        await File.WriteAllTextAsync(Path.Combine(root, ".lake", "packages", "generated-library", "Library.lean"), "theorem generated : True := by trivial\n");
        await File.WriteAllTextAsync(Path.Combine(root, ".venv", "Lib", "site-packages", "generated.py"), "generated = True\n");
        await File.WriteAllTextAsync(Path.Combine(root, "node_modules", "dependency", "index.js"), "module.exports = {};\n");
        await File.WriteAllTextAsync(Path.Combine(root, ".next", "server", "bundle.js"), "generated\n");
        await File.WriteAllTextAsync(Path.Combine(root, "target", "debug", "generated.rs"), "fn generated() {}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "vendor", "example.org", "dependency", "generated.go"), "package dependency\n");
        await File.WriteAllTextAsync(Path.Combine(root, "obj", "Debug", "Generated.cs"), "internal class Generated {}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "AppPackages", "Generated", "Package.appxmanifest"), "<Package />\n");
        await File.WriteAllTextAsync(Path.Combine(root, "bin", "generated.customlang"), "generated\n");
        try
        {
            using var index = new WorkspaceRepositoryIndex(new GoInfrastructureOptions { DataDirectory = data });

            var snapshot = await index.GetSnapshotAsync(root);

            Assert.Contains(snapshot.Entries, entry => entry.Path == ".gitignore");
            Assert.Contains(snapshot.Entries, entry => entry.Path == ".github/workflows/build.yml");
            Assert.Contains(snapshot.Entries, entry => entry.Path == "firmware/main.zig" && entry.Language == "ZIG");
            Assert.Contains(snapshot.Entries, entry => entry.Path == "system.customlang" && entry.Language == "CUSTOMLANG");
            Assert.DoesNotContain(snapshot.Entries, entry => entry.Path == "ignored.tmp");
            Assert.DoesNotContain(snapshot.Entries, entry => entry.Path.StartsWith(".lake/", StringComparison.Ordinal));
            Assert.DoesNotContain(snapshot.Entries, entry => entry.Path.StartsWith(".venv/", StringComparison.Ordinal));
            Assert.DoesNotContain(snapshot.Entries, entry => entry.Path.StartsWith("node_modules/", StringComparison.Ordinal));
            Assert.DoesNotContain(snapshot.Entries, entry => entry.Path.StartsWith(".next/", StringComparison.Ordinal));
            Assert.DoesNotContain(snapshot.Entries, entry => entry.Path.StartsWith("target/", StringComparison.Ordinal));
            Assert.DoesNotContain(snapshot.Entries, entry => entry.Path.StartsWith("vendor/", StringComparison.Ordinal));
            Assert.DoesNotContain(snapshot.Entries, entry => entry.Path.StartsWith("obj/", StringComparison.Ordinal));
            Assert.DoesNotContain(snapshot.Entries, entry => entry.Path.StartsWith("AppPackages/", StringComparison.Ordinal));
            Assert.DoesNotContain(snapshot.Entries, entry => entry.Path.StartsWith("bin/", StringComparison.Ordinal));
            Assert.True(WorkspaceRepositoryIndex.MatchesGlob("system.customlang", "**/*"));
            Assert.True(WorkspaceRepositoryIndex.IsAutomaticallyIgnoredPath("node_modules/dependency/index.js", isDirectory: false));
            Assert.True(WorkspaceRepositoryIndex.IsAutomaticallyIgnoredPath("rust/target", isDirectory: true));
            Assert.False(WorkspaceRepositoryIndex.IsAutomaticallyIgnoredPath("web/src/main.ts", isDirectory: false));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            try { Directory.Delete(data, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task RepositoryMapDetectsAndPrioritizesSupportedProjectProfiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"go-workspace-profiles-{Guid.NewGuid():N}");
        var data = Path.Combine(Path.GetTempPath(), $"go-workspace-data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "web", "src"));
        Directory.CreateDirectory(Path.Combine(root, "python"));
        Directory.CreateDirectory(Path.Combine(root, "rust", "src"));
        Directory.CreateDirectory(Path.Combine(root, "go"));
        Directory.CreateDirectory(Path.Combine(root, "winui"));
        Directory.CreateDirectory(Path.Combine(root, "lean"));
        await File.WriteAllTextAsync(Path.Combine(root, "web", "package.json"), "{\"name\":\"web\"}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "web", "package-lock.json"), "{\"lockfileVersion\":3}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "web", "src", "main.ts"), "export const ready = true;\n");
        await File.WriteAllTextAsync(Path.Combine(root, "python", "pyproject.toml"), "[project]\nname = \"sample\"\n");
        await File.WriteAllTextAsync(Path.Combine(root, "python", "main.py"), "print('ready')\n");
        await File.WriteAllTextAsync(Path.Combine(root, "rust", "Cargo.toml"), "[package]\nname = \"sample\"\nversion = \"0.1.0\"\n");
        await File.WriteAllTextAsync(Path.Combine(root, "rust", "src", "main.rs"), "fn main() {}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "go", "go.mod"), "module example.org/sample\n\ngo 1.24\n");
        await File.WriteAllTextAsync(Path.Combine(root, "go", "main.go"), "package main\nfunc main() {}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "winui", "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        await File.WriteAllTextAsync(Path.Combine(root, "winui", "App.xaml"), "<Application />\n");
        await File.WriteAllTextAsync(Path.Combine(root, "winui", "Package.appxmanifest"), "<Package />\n");
        await File.WriteAllTextAsync(Path.Combine(root, "lean", "lakefile.toml"), "name = \"Sample\"\n");
        await File.WriteAllTextAsync(Path.Combine(root, "lean", "lean-toolchain"), "leanprover/lean4:v4.24.0\n");
        try
        {
            using var index = new WorkspaceRepositoryIndex(new GoInfrastructureOptions { DataDirectory = data });

            var snapshot = await index.GetSnapshotAsync(root);
            var map = WorkspaceRepositoryIndex.BuildRepositoryMap(snapshot, maximumEntries: 12);

            Assert.Contains("Projektprofile: WinUI/.NET, Node.js/npm, Python, Rust/Cargo, Go, Lean/Lake", map, StringComparison.Ordinal);
            Assert.Contains("web/package.json", map, StringComparison.Ordinal);
            Assert.Contains("python/pyproject.toml", map, StringComparison.Ordinal);
            Assert.Contains("rust/Cargo.toml", map, StringComparison.Ordinal);
            Assert.Contains("go/go.mod", map, StringComparison.Ordinal);
            Assert.Contains("winui/Sample.csproj", map, StringComparison.Ordinal);
            Assert.Contains("winui/Package.appxmanifest", map, StringComparison.Ordinal);
            Assert.Contains("lean/lakefile.toml", map, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            try { Directory.Delete(data, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task PersistentIndexReusesUnchangedFileMetadataAfterServiceRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"go-workspace-cache-{Guid.NewGuid():N}");
        var data = Path.Combine(Path.GetTempPath(), $"go-workspace-data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "main.py");
        await File.WriteAllTextAsync(sourcePath, "print('cached')\n");
        try
        {
            WorkspaceIndexSnapshot initial;
            using (var index = new WorkspaceRepositoryIndex(new GoInfrastructureOptions { DataDirectory = data }))
            {
                initial = await index.GetSnapshotAsync(root);
            }

            await using var exclusiveFile = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.None);
            using var restoredIndex = new WorkspaceRepositoryIndex(new GoInfrastructureOptions { DataDirectory = data });
            var restored = await restoredIndex.GetSnapshotAsync(root);

            var initialEntry = Assert.Single(initial.Entries);
            var restoredEntry = Assert.Single(restored.Entries);
            Assert.Equal(initialEntry, restoredEntry);
            Assert.Equal(initial.RevisionFingerprint, restored.RevisionFingerprint);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            try { Directory.Delete(data, recursive: true); } catch (IOException) { }
        }
    }
}
