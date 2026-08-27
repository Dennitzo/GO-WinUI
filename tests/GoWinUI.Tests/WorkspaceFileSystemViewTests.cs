using GoWinUI.App.Services;

namespace GoWinUI.Tests;

public sealed class WorkspaceFileSystemViewTests
{
    [Fact]
    public async Task TreeReadsTheLiveWorkspaceAndExcludesGeneratedDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), $"go-workspace-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "package"));
        Directory.CreateDirectory(Path.Combine(root, "obj", "Debug"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "src", "Program.cs"), "internal static class Program { }\n");
            await File.WriteAllTextAsync(Path.Combine(root, "node_modules", "package", "index.js"), "generated\n");
            await File.WriteAllTextAsync(Path.Combine(root, "obj", "Debug", "Generated.cs"), "generated\n");

            var first = WorkspaceFileSystemView.BuildTree(root);
            Assert.Contains("src/", first.Tree, StringComparison.Ordinal);
            Assert.Contains("src/Program.cs", first.Tree, StringComparison.Ordinal);
            Assert.DoesNotContain("node_modules", first.Tree, StringComparison.Ordinal);
            Assert.DoesNotContain("obj/", first.Tree, StringComparison.Ordinal);

            await File.WriteAllTextAsync(Path.Combine(root, "src", "Added.py"), "print('live')\n");
            var second = WorkspaceFileSystemView.BuildTree(root);
            Assert.Contains("src/Added.py", second.Tree, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TreeIsBoundedAndMarksTruncation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"go-workspace-tree-limit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            for (var index = 0; index < 20; index++)
            {
                File.WriteAllText(
                    Path.Combine(root, $"file-{index:D2}.txt"),
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            var tree = WorkspaceFileSystemView.BuildTree(root, maximumEntries: 5, maximumCharacters: 4_096);

            Assert.True(tree.IsTruncated);
            Assert.Equal(5, tree.FileCount);
            Assert.Contains("Baum gekürzt", tree.Tree, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("src/Program.cs", "**/*.cs", true)]
    [InlineData("src/Program.cs", "*.cs", true)]
    [InlineData("src/Program.cs", "**/*.py", false)]
    public void GlobMatchingWorksWithoutAnIndex(string path, string glob, bool expected)
    {
        Assert.Equal(expected, WorkspaceFileSystemView.MatchesGlobs(path, [glob], []));
    }
}
