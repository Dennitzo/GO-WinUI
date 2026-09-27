using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.App.Services.Extensions;
using GoWinUI.Core.Extensions;
using GoWinUI.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GoWinUI.Tests;

public sealed class ExtensionFoundationTests : IDisposable
{
    private static readonly string[] RequiredPathProperties = ["path"];
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "assistant-extension-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExtensionHostRegistrationUsesTheSharedRuntimeAsHostedService()
    {
        var services = new ServiceCollection();

        services.AddExtensionHostSupervisor();

        var descriptors = services.Where(static descriptor => descriptor.ServiceType == typeof(IHostedService)).ToArray();
        var descriptor = Assert.Single(descriptors);
        Assert.NotNull(descriptor.ImplementationFactory);
    }

    [Fact]
    public void BuiltInIdentifiersAreNeutralAndCanonical()
    {
        Assert.All(BuiltInExtensionIds.All, id =>
        {
            Assert.True(ExtensionIdentifiers.IsValidExtensionId(id));
            Assert.True(ExtensionIdentifiers.IsProductNeutralPublicIdentifier(id));
        });
        Assert.Equal("builtin.workspace/attach-files-and-folders", BuiltInActionIds.AttachFilesAndFolders);
        Assert.Equal("builtin.documents/export-chat-pdf", BuiltInActionIds.ExportChatPdf);
        Assert.Equal("builtin.workflows/open-library", BuiltInActionIds.OpenWorkflowLibrary);
        Assert.Equal("builtin.memory/open-library", BuiltInActionIds.OpenMemoryLibrary);
        var planMode = Assert.Single(BuiltInExtensionCatalog.Actions, action =>
            action.ActionId == BuiltInActionIds.PlanMode);
        Assert.Equal([ExtensionChatMode.Coding], planMode.SupportedChatModes);
        Assert.Equal(ExtensionActionKind.SelectableTool, planMode.ActionKind);
        var publicCatalog = JsonSerializer.Serialize(BuiltInExtensionCatalog.Actions, ManifestJson);
        Assert.DoesNotContain("GoWinUI", publicCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"go.", publicCatalog, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("com.example.extension", true)]
    [InlineData("org.example.my-extension", true)]
    [InlineData("example.extension", false)]
    [InlineData("GO.example.extension", false)]
    [InlineData("go.example.extension", false)]
    [InlineData("builtin.Documents", false)]
    public void ExtensionIdsRequireReservedBuiltInOrReverseDns(string value, bool expected)
    {
        Assert.Equal(expected, ExtensionIdentifiers.IsValidExtensionId(value));
    }

    [Fact]
    public void ToolSelectorWritesCanonicalAndReadsLegacyAlias()
    {
        Assert.Equal(ToolSelectorNames.Canonical, ToolSelectorNames.NormalizeForRead(ToolSelectorNames.LegacyReadAlias));
        ToolSelectorNames.EnsureWritable(ToolSelectorNames.Canonical);
        Assert.Throws<ArgumentException>(() => ToolSelectorNames.EnsureWritable(ToolSelectorNames.LegacyReadAlias));
    }

    [Fact]
    public void ManifestValidatorRejectsCrossExtensionActionAndTraversal()
    {
        var manifest = CreateManifest() with
        {
            Entrypoints = [new(ExtensionEntrypointKind.Desktop, "../outside.dll")],
            Actions = [CreateAction("net.other.extension/do-thing")],
        };

        var result = ExtensionManifestValidator.Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Paketpfad", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("statt zur Extension", StringComparison.Ordinal));
    }

    [Fact]
    public void CatalogRejectsActionAndModelToolCollisionsAtomically()
    {
        var catalog = new ExtensionActionCatalog();
        catalog.Register(CreateManifest());
        var conflicting = CreateManifest("org.example.second") with
        {
            Tools =
            [
                new("org.example.second/do-thing", "sample.execute", "Konflikt",
                    JsonSerializer.SerializeToElement(new
                    {
                        type = "object",
                        properties = new { },
                        required = Array.Empty<string>(),
                        additionalProperties = false,
                    }), 30, 4_096),
            ],
        };

        Assert.Throws<InvalidDataException>(() => catalog.Register(conflicting));
        Assert.False(catalog.TryGetAction("org.example.second/do-thing", out _));
    }

    [Fact]
    public void ManifestRequiresPermissionsMatchingToolRisk()
    {
        var processTool = CreateManifest() with
        {
            Permissions = [ExtensionPermissionKind.WorkspaceRead],
            Tools = [CreateManifest().Tools.Single() with { RiskClass = ExtensionToolRiskClass.Process }],
        };
        var mutationTool = CreateManifest() with
        {
            Permissions = [ExtensionPermissionKind.WorkspaceRead],
            Tools = [CreateManifest().Tools.Single() with { RiskClass = ExtensionToolRiskClass.LocalMutation }],
        };

        Assert.Contains(ExtensionManifestValidator.Validate(processTool).Errors,
            error => error.Contains("Process-Permission", StringComparison.Ordinal));
        Assert.Contains(ExtensionManifestValidator.Validate(mutationTool).Errors,
            error => error.Contains("Schreib-Permission", StringComparison.Ordinal));
    }

    [Fact]
    public void BuiltInCatalogContainsImmediatePdfWorkflowAndMemoryActions()
    {
        var catalog = ExtensionActionCatalog.CreateWithBuiltIns();
        Assert.True(catalog.TryGetAction(BuiltInActionIds.ExportChatPdf, out var pdf));
        Assert.Equal(ExtensionActionKind.Immediate, pdf!.ActionKind);
        Assert.Equal(ExtensionSelectionBehavior.None, pdf.SelectionBehavior);
        Assert.True(catalog.TryGetAction(BuiltInActionIds.OpenWorkflowLibrary, out var workflows));
        Assert.Equal("workflows", workflows!.GroupId);
        Assert.True(catalog.TryGetAction(BuiltInActionIds.OpenMemoryLibrary, out var memory));
        Assert.Equal("extensions", memory!.GroupId);
        Assert.False(catalog.TryGetTool(BuiltInActionIds.ExportChatPdf, out _));
    }

    [Fact]
    public async Task InspectorRejectsTraversalAndCaseInsensitivePathCollisions()
    {
        Directory.CreateDirectory(_root);
        var inspector = new AextPackageInspector();
        var traversal = Path.Combine(_root, "traversal.aext");
        await CreateRawPackageAsync(traversal,
            ("../escape.dll", "escape"),
            (ExtensionPackageFormat.ManifestEntryName, SerializeManifest(CreateManifest())));
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.InspectAsync(traversal));

        var collision = Path.Combine(_root, "collision.aext");
        await CreateRawPackageAsync(collision,
            ("desktop/entry.dll", "entry"),
            ("desktop/ENTRY.dll", "collision"),
            (ExtensionPackageFormat.ManifestEntryName, SerializeManifest(CreateManifest())));
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.InspectAsync(collision));
        Assert.False(File.Exists(Path.Combine(_root, "escape.dll")));
    }

    [Fact]
    public async Task InspectorAndStoreVerifyStageAndActivateWithoutEscapingStore()
    {
        Directory.CreateDirectory(_root);
        var unsignedPath = Path.Combine(_root, "sample.aext");
        await CreatePackageAsync(unsignedPath, CreateManifest(), signature: null);
        var unsigned = await new AextPackageInspector().InspectAsync(unsignedPath);
        Assert.Equal(AextSignatureStatus.Missing, unsigned.SignatureVerification.Status);

        var signature = new AextSignatureEnvelope(
            "test-sha256", "com.example.test:key-1", unsigned.ContentSha256,
            Convert.ToBase64String(Enumerable.Repeat((byte)0x5a, 64).ToArray()));
        await CreatePackageAsync(unsignedPath, CreateManifest(), signature);
        var inspector = new AextPackageInspector(new AcceptingSignatureVerifier());
        var signed = await inspector.InspectAsync(unsignedPath);
        Assert.Equal(AextSignatureStatus.Verified, signed.SignatureVerification.Status);

        using var store = new FileSystemAextPackageStore(inspector,
            new(Path.Combine(_root, "store"), RequireVerifiedSignatures: true));
        var staged = await store.StageAsync(unsignedPath);
        Assert.True(File.Exists(staged.PackagePath));
        var candidates = await store.GetActivationCandidatesAsync();
        var activated = await store.ActivateAsync(Assert.Single(candidates));
        Assert.True(File.Exists(activated.PackagePath));
        Assert.True(File.Exists(Path.Combine(activated.ContentDirectory, "desktop", "entry.dll")));
        Assert.StartsWith(Path.GetFullPath(Path.Combine(_root, "store")), Path.GetFullPath(activated.PackagePath), StringComparison.OrdinalIgnoreCase);
        Assert.Single(await store.GetActivePackagesAsync());

        await File.WriteAllTextAsync(
            Path.Combine(activated.ContentDirectory, "desktop", "entry.dll"),
            "tampered-extension-entry");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetActivePackagesAsync());
    }

    [Fact]
    public void ExtensionHostHandshakeBindsIdentityHashAndChallenge()
    {
        var identity = new ExtensionHostIdentity("com.example.extension", new string('a', 64));
        var hello = ExtensionHostProtocol.CreateHello(identity, "request-1", new string('b', 64));
        var wire = ExtensionHostProtocol.SerializeLine(hello);
        var parsed = ExtensionHostProtocol.ParseLine(wire);
        var ready = ExtensionHostProtocol.AcceptHello(parsed, identity, 42, ["lifecycle"]);

        var accepted = ExtensionHostProtocol.ValidateReady(
            ExtensionHostProtocol.ParseLine(ExtensionHostProtocol.SerializeLine(ready)),
            identity, "request-1", new string('b', 64));

        Assert.Equal(42, accepted.ProcessId);
        Assert.Equal(["lifecycle"], accepted.Capabilities);
        Assert.Throws<InvalidDataException>(() => ExtensionHostProtocol.ValidateReady(
            ready, identity, "request-1", new string('c', 64)));
    }

    [Fact]
    public void InvocationPolicyEnforcesWorkspaceNetworkAndProcessPermissions()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var workspacePermissions = new[] { ExtensionPermissionKind.WorkspaceRead };
        var valid = new ExtensionActionInvocation(
            "com.example.extension/do-thing",
            JsonSerializer.SerializeToElement(new { path = "documents/input.txt" }),
            new(workspacePermissions, workspace));

        ExtensionInvocationPolicy.Validate(valid, workspacePermissions, workspace);

        var escaped = valid with
        {
            Arguments = JsonSerializer.SerializeToElement(new { path = "../outside.txt" }),
        };
        Assert.Throws<InvalidDataException>(() =>
            ExtensionInvocationPolicy.Validate(escaped, workspacePermissions, workspace));

        var noPermissions = Array.Empty<ExtensionPermissionKind>();
        var network = new ExtensionActionInvocation(
            "com.example.extension/do-thing",
            JsonSerializer.SerializeToElement(new { url = "https://example.invalid/resource" }),
            new(noPermissions, null));
        Assert.Throws<InvalidDataException>(() =>
            ExtensionInvocationPolicy.Validate(network, noPermissions, null));

        var process = network with
        {
            Arguments = JsonSerializer.SerializeToElement(new { command = "example.exe" }),
        };
        Assert.Throws<InvalidDataException>(() =>
            ExtensionInvocationPolicy.Validate(process, noPermissions, null));
    }

    [Fact]
    public async Task InspectorRejectsUnknownManifestFieldsAndForeignActionIds()
    {
        Directory.CreateDirectory(_root);
        var manifestJson = SerializeManifest(CreateManifest());
        var unknownField = manifestJson[..^1] + ",\"unexpectedField\":true}";
        var package = Path.Combine(_root, "unknown-field.aext");
        await CreateRawPackageAsync(
            package,
            (ExtensionPackageFormat.ManifestEntryName, unknownField),
            ("desktop/entry.dll", "extension-entry"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AextPackageInspector().InspectAsync(package));

        var foreign = CreateManifest() with
        {
            Actions = [CreateAction("org.other.publisher/do-thing")],
            Tools = [],
        };
        var validation = ExtensionManifestValidator.Validate(foreign);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error =>
            error.Contains("statt zur Extension", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ExtensionEntrypointKind.Desktop)]
    [InlineData(ExtensionEntrypointKind.Gateway)]
    public async Task ExtensionHostForwardsActionInvokeToIsolatedEntrypoint(ExtensionEntrypointKind entrypointKind)
    {
        var hostExecutable = Path.Combine(AppContext.BaseDirectory, "ExtensionHost", "ExtensionHost.dll");
        var fixtureSource = Path.Combine(AppContext.BaseDirectory, "ExtensionHost.TestExtension");
        Assert.True(File.Exists(hostExecutable), $"ExtensionHost fehlt: {hostExecutable}");
        Assert.True(Directory.Exists(fixtureSource), $"Test-Extension fehlt: {fixtureSource}");

        var contentDirectory = Path.Combine(_root, "roundtrip", "content");
        var workspace = Path.Combine(_root, "roundtrip", "workspace");
        Directory.CreateDirectory(contentDirectory);
        Directory.CreateDirectory(workspace);
        foreach (var source in Directory.EnumerateFiles(fixtureSource))
            File.Copy(source, Path.Combine(contentDirectory, Path.GetFileName(source)));

        var permissions = new[] { ExtensionPermissionKind.WorkspaceRead };
        var supervisor = new ExtensionHostSupervisor();
        await using var host = await supervisor.StartAsync(new(
            hostExecutable,
            new("com.example.extension", new string('a', 64)),
            TimeSpan.FromSeconds(10),
            contentDirectory,
            new(entrypointKind, "ExtensionHost.TestExtension.dll"),
            permissions,
            workspace,
            TimeSpan.FromSeconds(10),
            64 * 1024));
        var invocation = new ExtensionActionInvocation(
            "com.example.extension/do-thing",
            JsonSerializer.SerializeToElement(new { path = "input.txt", value = 42 }),
            new(permissions, workspace));
        var request = ExtensionHostProtocol.Create(
            ExtensionHostMessageTypes.InvokeAction,
            "roundtrip-1",
            invocation);

        var response = await host.ExchangeAsync(request);

        Assert.Equal(ExtensionHostMessageTypes.ActionResult, response.Type);
        var result = ExtensionHostProtocol.ReadPayload<ExtensionActionResult>(response).Result;
        Assert.Equal(invocation.ActionId, result.GetProperty("actionId").GetString());
        Assert.Equal(42, result.GetProperty("arguments").GetProperty("value").GetInt32());
        Assert.Equal(Path.GetFullPath(workspace), result.GetProperty("workspaceRoot").GetString());
    }

    [Fact]
    public async Task LocalToolBrokerRoutesVerifiedDynamicProposalToExtensionRuntime()
    {
        var catalog = new ExtensionActionCatalog();
        catalog.Register(CreateManifest());
        var runtime = new RecordingExtensionRuntime();
        var workspace = Path.Combine(_root, "broker-workspace");
        Directory.CreateDirectory(workspace);
        var broker = new LocalToolBroker(null!, null!, null!, null!, catalog, runtime);
        var proposal = new ToolProposal(
            "proposal-1",
            "run-1",
            "sample.execute",
            JsonSerializer.SerializeToElement(new { path = "input.txt", value = 7 }),
            ToolRiskClass.ReadOnly,
            "Beispieldaten lesen",
            DateTimeOffset.UtcNow.AddMinutes(1));

        var result = await broker.ExecuteAsync(proposal, Guid.NewGuid(), null, workspace);

        Assert.Equal("completed", result.Status);
        Assert.Equal("com.example.extension/do-thing", runtime.ActionId);
        Assert.Equal(Path.GetFullPath(workspace), runtime.WorkspaceRoot);
        Assert.Equal(7, runtime.Arguments.GetProperty("value").GetInt32());
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        var fullRoot = Path.GetFullPath(_root);
        if (!Path.GetFileName(fullRoot).StartsWith("assistant-extension-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test directory.");
        Directory.Delete(fullRoot, recursive: true);
    }

    private static ExtensionManifest CreateManifest(string extensionId = "com.example.extension")
    {
        var actionId = extensionId + "/do-thing";
        return new(
            ExtensionPackageFormat.CurrentSchemaVersion,
            extensionId,
            "1.2.3",
            "Example Extension",
            "Example Publisher",
            [new(ExtensionEntrypointKind.Desktop, "desktop/entry.dll")],
            [ExtensionPermissionKind.WorkspaceRead],
            [CreateAction(actionId)],
            [new(actionId, "sample.execute", "Beispiel ausführen",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string" },
                        value = new { type = "integer" },
                    },
                    required = RequiredPathProperties,
                    additionalProperties = false,
                }), 30, 4_096)]);
    }

    private static ExtensionActionDescriptor CreateAction(string actionId) =>
        new(actionId, "Beispiel", "Eine Beispielaktion ausführen", "extension", "extensions", 700, 100,
            [ExtensionChatMode.General, ExtensionChatMode.Coding], ExtensionActionKind.SelectableTool,
            ExtensionSelectionBehavior.Toggle);

    private static string SerializeManifest(ExtensionManifest manifest) =>
        JsonSerializer.Serialize(manifest, ManifestJson);

    private static async Task CreatePackageAsync(
        string path,
        ExtensionManifest manifest,
        AextSignatureEnvelope? signature)
    {
        var entries = new List<(string Path, string Contents)>
        {
            (ExtensionPackageFormat.ManifestEntryName, SerializeManifest(manifest)),
            ("desktop/entry.dll", "extension-entry"),
        };
        if (signature is not null)
            entries.Add((ExtensionPackageFormat.SignatureEntryName, JsonSerializer.Serialize(signature, ManifestJson)));
        await CreateRawPackageAsync(path, [.. entries]);
    }

    private static async Task CreateRawPackageAsync(
        string path,
        params (string Path, string Contents)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (entryPath, contents) in entries)
        {
            var entry = archive.CreateEntry(entryPath, CompressionLevel.Fastest);
            await using var target = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(contents);
            await target.WriteAsync(bytes);
        }
    }

    private sealed class AcceptingSignatureVerifier : IAextSignatureVerifier
    {
        public ValueTask<AextSignatureVerification> VerifyAsync(
            AextSignatureVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(context.ContentSha256, context.Signature.ContentSha256);
            return ValueTask.FromResult(new AextSignatureVerification(AextSignatureStatus.Verified));
        }
    }

    private sealed class RecordingExtensionRuntime : IExtensionRuntimeService
    {
        public string? ActionId { get; private set; }
        public string? WorkspaceRoot { get; private set; }
        public JsonElement Arguments { get; private set; }

        public string? GetDisabledReason(string actionId, string? workspaceRoot) => null;

        public Task<JsonElement> InvokeAsync(
            string actionId,
            JsonElement arguments,
            string? workspaceRoot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActionId = actionId;
            WorkspaceRoot = workspaceRoot;
            Arguments = arguments.Clone();
            return Task.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
        }
    }
}
