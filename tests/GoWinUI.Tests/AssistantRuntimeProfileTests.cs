using GoWinUI.App.Services;
using GoWinUI.Core.Models;

namespace GoWinUI.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RuntimeEnvironmentTestGroup
{
    public const string Name = "Runtime environment";
}

[Collection(RuntimeEnvironmentTestGroup.Name)]
public sealed class AssistantRuntimeProfileTests
{
    private static readonly string[] Variables =
    [
        "ASSISTANT_PROFILE", "ASSISTANT_PRODUCT_NAME", "ASSISTANT_DATA_ROOT", "ASSISTANT_GATEWAY_URL",
        "ASSISTANT_GATEWAY_PORT", "ASSISTANT_NATIVE_PORT", "ASSISTANT_NATIVE_STATE_ROOT",
        "ASSISTANT_NATIVE_BINARY_PATH", "ASSISTANT_STACK_DATA_ROOT", "ASSISTANT_INSTANCE_KEY", "GO_DATA_DIRECTORY",
        "GO_SMOKE_INSTANCE_KEY",
    ];

    [Fact]
    public void StableDefaultRetainsPrimaryChatDatabaseDirectory()
    {
        using var environment = new EnvironmentScope();
        Environment.SetEnvironmentVariable("ASSISTANT_PROFILE", "stable");

        var profile = AssistantRuntimeProfile.Resolve();

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GO"),
            profile.DataDirectory);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".go-winui", "native-runtime"),
            profile.NativeStateDirectory);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GO-AI-Stack"),
            profile.StackDataRoot);
    }

    [Fact]
    public void PublishedManifestSelectsProfileWhenLauncherEnvironmentIsUnavailable()
    {
        using var environment = new EnvironmentScope();
        var directory = Path.Combine(Path.GetTempPath(), "assistant-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "assistant-profile.json"),
                "{\"schemaVersion\":1,\"profile\":\"stable\",\"appFile\":\"GoWinUI.App.exe\"}");

            Assert.Equal("stable", AssistantRuntimeProfile.ResolveProfileName(null, directory));
            Assert.Equal("stable", AssistantRuntimeProfile.ResolveProfileName("Stable", directory));

            var profile = AssistantRuntimeProfile.ResolveForPublishedDirectory(directory);
            var current = new AppSettings { GoAiServerUrl = "http://127.0.0.1:8080" };
            var applied = GoWinUI.App.App.ApplyRuntimeProfile(current, profile);

            Assert.True(profile.HasExplicitProfileSelection);
            Assert.Equal(8080, profile.GatewayUri.Port);
            Assert.Equal("http://127.0.0.1:8080", applied.GoAiServerUrl);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ImplicitStableFallbackPreservesStoredGateway()
    {
        using var environment = new EnvironmentScope();
        var directory = Path.Combine(Path.GetTempPath(), "assistant-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var profile = AssistantRuntimeProfile.ResolveForPublishedDirectory(directory);
            var current = new AppSettings { GoAiServerUrl = "http://127.0.0.1:9080" };
            var applied = GoWinUI.App.App.ApplyRuntimeProfile(current, profile);

            Assert.False(profile.HasExplicitProfileSelection);
            Assert.Same(current, applied);
            Assert.Equal("http://127.0.0.1:9080", applied.GoAiServerUrl);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ProductRenameDoesNotChangeTechnicalProfileIdentity()
    {
        using var environment = new EnvironmentScope();
        Environment.SetEnvironmentVariable("ASSISTANT_PROFILE", "stable");
        Environment.SetEnvironmentVariable("ASSISTANT_DATA_ROOT", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var original = AssistantRuntimeProfile.Resolve();

        Environment.SetEnvironmentVariable("ASSISTANT_PRODUCT_NAME", "Neuer Produktname");
        var renamed = AssistantRuntimeProfile.Resolve();

        Assert.Equal(original.InstanceKey, renamed.InstanceKey);
        Assert.Equal(original.DataDirectory, renamed.DataDirectory);
        Assert.Equal("Neuer Produktname", renamed.ProductName);
    }

    [Fact]
    public void RejectsControlPortOverflow()
    {
        using var environment = new EnvironmentScope();
        Environment.SetEnvironmentVariable("ASSISTANT_NATIVE_PORT", "65535");
        Assert.Throws<InvalidOperationException>(AssistantRuntimeProfile.Resolve);
    }

    [Fact]
    public void RejectsGatewayCollisionWithNativePorts()
    {
        using var environment = new EnvironmentScope();
        Environment.SetEnvironmentVariable("ASSISTANT_GATEWAY_URL", "http://127.0.0.1:8081");
        Assert.Throws<InvalidOperationException>(AssistantRuntimeProfile.Resolve);

        Environment.SetEnvironmentVariable("ASSISTANT_GATEWAY_URL", "http://127.0.0.1:8082");
        Assert.Throws<InvalidOperationException>(AssistantRuntimeProfile.Resolve);
    }

    [Fact]
    public void NativeBinaryResolutionUsesInstalledStackThenUnslothFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "native-binary-resolution-" + Guid.NewGuid().ToString("N"));
        var managed = Path.Combine(root, "stack", "bin", "llama-server.exe");
        var unsloth = Path.Combine(root, "user", ".unsloth", "llama.cpp", "build", "bin", "Release", "llama-server.exe");
        try
        {
            Assert.Equal(Path.GetFullPath(managed), AssistantRuntimeProfile.ResolveNativeBinaryPath(
                Path.Combine(root, "stack"), Path.Combine(root, "user"), path =>
                    string.Equals(path, Path.GetFullPath(managed), StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(Path.GetFullPath(unsloth), AssistantRuntimeProfile.ResolveNativeBinaryPath(
                Path.Combine(root, "stack"), Path.Combine(root, "user"), path =>
                    string.Equals(path, Path.GetFullPath(unsloth), StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(Path.GetFullPath(managed), AssistantRuntimeProfile.ResolveNativeBinaryPath(
                Path.Combine(root, "stack"), Path.Combine(root, "user"), static _ => false));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = Variables.ToDictionary(
            static name => name,
            static name => Environment.GetEnvironmentVariable(name),
            StringComparer.Ordinal);

        public EnvironmentScope()
        {
            foreach (var variable in Variables) Environment.SetEnvironmentVariable(variable, null);
        }

        public void Dispose()
        {
            foreach (var item in _previous) Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
    }
}
