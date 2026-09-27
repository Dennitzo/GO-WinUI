using System.Diagnostics;
using System.Text.Json;
using GoWinUI.App.Services.Extensions;
using GoWinUI.Core.Extensions;

namespace GoWinUI.Tests;

public sealed class ExtensionHostSecurityTests
{
    [Fact]
    public async Task WindowsJobBlocksEntrypointChildrenWithoutProcessPermission()
    {
        var fixture = CreateFixture();
        try
        {
            await using (var host = await StartHostAsync(fixture, []))
            {

                Assert.True(host.JobPolicy.KillsOnClose);
                Assert.False(ExtensionHostJobPolicy.AllowsBreakaway);
                Assert.Equal(2u, host.JobPolicy.ActiveProcessLimit);
                Assert.Equal(512L * 1024 * 1024, host.JobPolicy.MaximumProcessMemoryBytes);
                Assert.Equal(1024L * 1024 * 1024, host.JobPolicy.MaximumJobMemoryBytes);

                var result = await InvokeSpawnAsync(host, []);

                Assert.False(result.GetProperty("childStillRunning").GetBoolean());
                Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("childStartError").GetString()));
            }
        }
        finally
        {
            DeleteFixture(fixture.Root);
        }
    }

    [Fact]
    public async Task ProcessPermissionAllowsOnlyABoundedDescendantSetInsideKillOnCloseJob()
    {
        var fixture = CreateFixture();
        var permissions = new[] { ExtensionPermissionKind.Process };
        try
        {
            await using (var host = await StartHostAsync(fixture, permissions))
            {

                Assert.True(host.JobPolicy.KillsOnClose);
                Assert.False(ExtensionHostJobPolicy.AllowsBreakaway);
                Assert.Equal(18u, host.JobPolicy.ActiveProcessLimit);

                var result = await InvokeSpawnAsync(host, permissions);

                Assert.True(result.GetProperty("childStarted").GetBoolean());
                Assert.True(result.GetProperty("childStillRunning").GetBoolean());
                Assert.True(result.GetProperty("childProcessId").GetInt32() > 0);
                Assert.Equal(JsonValueKind.Null, result.GetProperty("childStartError").ValueKind);
            }
        }
        finally
        {
            DeleteFixture(fixture.Root);
        }
    }

    [Fact]
    public async Task ClosingJobHandleTerminatesAssignedProcess()
    {
        var fixtureAssembly = Path.Combine(
            AppContext.BaseDirectory, "ExtensionHost.TestExtension", "ExtensionHost.TestExtension.dll");
        Assert.True(File.Exists(fixtureAssembly), $"Test-Extension fehlt: {fixtureAssembly}");
        using var process = new Process
        {
            StartInfo = new()
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add(fixtureAssembly);
        process.StartInfo.ArgumentList.Add("--child-wait");
        Assert.True(process.Start());

        WindowsExtensionProcessJob? job = null;
        try
        {
            job = WindowsExtensionProcessJob.CreateAndAssign(process, new(
                fixtureAssembly,
                new("com.example.extension", new string('a', 64)),
                TimeSpan.FromSeconds(10)));
            Assert.False(process.HasExited);

            job.Dispose();
            job = null;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.HasExited);
        }
        finally
        {
            job?.Dispose();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static async Task<ExtensionHostConnection> StartHostAsync(
        ExtensionSecurityFixture fixture,
        ExtensionPermissionKind[] permissions)
    {
        var supervisor = new ExtensionHostSupervisor();
        return await supervisor.StartAsync(new(
            fixture.HostExecutable,
            new("com.example.extension", new string('a', 64)),
            TimeSpan.FromSeconds(10),
            fixture.ContentDirectory,
            new(ExtensionEntrypointKind.Desktop, "ExtensionHost.TestExtension.dll"),
            permissions,
            WorkspaceRoot: null,
            ActionTimeout: TimeSpan.FromSeconds(10),
            MaximumOutputBytes: 64 * 1024));
    }

    private static async Task<JsonElement> InvokeSpawnAsync(
        ExtensionHostConnection host,
        ExtensionPermissionKind[] permissions)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var invocation = new ExtensionActionInvocation(
            "com.example.extension/do-thing",
            JsonSerializer.SerializeToElement(new { spawnChild = true }),
            new(permissions, null));
        var request = ExtensionHostProtocol.Create(
            ExtensionHostMessageTypes.InvokeAction,
            Guid.NewGuid().ToString("N"),
            invocation);
        var response = await host.ExchangeAsync(request, timeout.Token);
        Assert.Equal(ExtensionHostMessageTypes.ActionResult, response.Type);
        return ExtensionHostProtocol.ReadPayload<ExtensionActionResult>(response).Result.Clone();
    }

    private static ExtensionSecurityFixture CreateFixture()
    {
        var hostExecutable = Path.Combine(AppContext.BaseDirectory, "ExtensionHost", "ExtensionHost.dll");
        var fixtureSource = Path.Combine(AppContext.BaseDirectory, "ExtensionHost.TestExtension");
        Assert.True(File.Exists(hostExecutable), $"ExtensionHost fehlt: {hostExecutable}");
        Assert.True(Directory.Exists(fixtureSource), $"Test-Extension fehlt: {fixtureSource}");

        var root = Path.Combine(Path.GetTempPath(), "extension-security-tests-" + Guid.NewGuid().ToString("N"));
        var contentDirectory = Path.Combine(root, "content");
        Directory.CreateDirectory(contentDirectory);
        foreach (var source in Directory.EnumerateFiles(fixtureSource))
            File.Copy(source, Path.Combine(contentDirectory, Path.GetFileName(source)));
        return new(root, hostExecutable, contentDirectory);
    }

    private static void DeleteFixture(string root)
    {
        if (!Directory.Exists(root)) return;
        var fullRoot = Path.GetFullPath(root);
        if (!Path.GetFileName(fullRoot).StartsWith("extension-security-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected extension security test directory.");
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Directory.Delete(fullRoot, recursive: true);
                return;
            }
            catch (Exception exception) when (
                attempt < 19 && exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private sealed record ExtensionSecurityFixture(
        string Root,
        string HostExecutable,
        string ContentDirectory);
}
