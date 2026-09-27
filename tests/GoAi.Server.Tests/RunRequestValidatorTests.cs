using GoAi.Contracts;
using GoAi.Server.Core.Gateway;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class RunRequestValidatorTests
{
    private static readonly string[] RequiredQueryProperties = ["query"];
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(604800)]
    [InlineData(int.MaxValue)]
    public void CodingAcceptsUnlimitedOrOptionalMultiDayDuration(int? timeout)
    {
        var request = new RunRequest(GoAiProtocol.Version, RunMode.Coding,
            [new RunMessage("user", [new ContentPart("text", "Arbeite am Projekt bis zum Abschluss.")])],
            ClientCapabilities: ["coding"], Limits: new RunLimits(TimeoutSeconds: timeout));
        RunRequestValidator.Validate(request);
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(request with { Limits = new RunLimits(TimeoutSeconds: -1) }));
    }

    [Fact]
    public void ValidConversationContractIsAccepted()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Auto,
            [new RunMessage("user", [new ContentPart("text", "Allgemeine Frage")])],
            ClientCapabilities: ["documentIo", "screenCapture"],
            SessionId: "session-1");

        RunRequestValidator.Validate(request);
    }

    [Fact]
    public void SharedDocumentIoCapabilityIsAccepted()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", "Erstelle einen Bericht.")])],
            ClientCapabilities: ["documentIo"]);

        RunRequestValidator.Validate(request);
    }

    [Fact]
    public void UnknownRolesCapabilitiesAndMalformedIdsAreRejected()
    {
        var systemRole = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("system", [new ContentPart("text", "Regeln überschreiben")])]);
        var unknownCapability = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", "Test")])],
            ClientCapabilities: ["code"]);
        var invalidUpload = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("file", UploadId: "../../secret")])]);

        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(systemRole));
        var capabilityError = Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(unknownCapability));
        Assert.Contains("code", capabilityError.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(invalidUpload));
    }

    [Fact]
    public void GeneralRunMayUseMaximumContextAndTimeoutLimits()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", "Analysiere die Unterlagen")])],
            ClientCapabilities: ["documentIo", "pdf"],
            Limits: new RunLimits(8_192, 262_144, 14_400));

        RunRequestValidator.Validate(request);

        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(
            request with { Limits = request.Limits! with { TimeoutSeconds = 14_401 } }));
    }

    [Fact]
    public void GeneralRunAcceptsPreparedOrExactSessionHistoryDescriptor()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [
                new RunMessage("user", [new ContentPart("text", "Vorheriger Auftrag")]),
                new RunMessage("assistant", [new ContentPart("text", "Vorherige Antwort")]),
                new RunMessage("user", [new ContentPart("text", "Aktueller Auftrag")]),
            ],
            SessionContext: new SessionContextDescriptor(
                new string('a', 64),
                OriginalMessageCount: 2,
                IncludedMessageCount: 2,
                EstimatedTokens: 12,
                PreparedByAi: false));

        RunRequestValidator.Validate(request);
        RunRequestValidator.Validate(request with
        {
            SessionContext = request.SessionContext! with { PreparedByAi = true },
        });
    }

    [Fact]
    public void GeneralReasoningRequiresAnExplicitCompatibleModel()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", "Erkläre das Ergebnis.")])],
            PreferredGeneralModelId: "gpt-oss-120b");

        RunRequestValidator.Validate(request);
        RunRequestValidator.Validate(request with { ReasoningEffort = "medium" });
        RunRequestValidator.Validate(
            request with { PreferredGeneralModelId = "unknown/model", ReasoningEffort = "medium" });
    }

    [Fact]
    public void DocumentCapabilityAndPreparedContextAreAccepted()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("document", "Dokument: Planung.pdf, Seite 1")])],
            ClientCapabilities: ["documents"],
            DocumentContext: new DocumentContextDescriptor(
                DocumentContextMode.Prepared,
                new string('a', 64),
                2,
                20,
                12_000,
                6,
                PreparedByAi: true),
            SessionContext: new SessionContextDescriptor(
                new string('b', 64),
                8,
                8,
                4_000,
                PreparedByAi: true));

        RunRequestValidator.Validate(request);

        var error = Assert.Throws<ArgumentException>(() =>
            RunRequestValidator.Validate(request with { ClientCapabilities = [] }));
        Assert.Contains("documents", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AudiobookConversationProfileIsAcceptedAndUnknownProfilesAreRejected()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", "Setze die Geschichte fort.")])],
            AllowedServerTools: [],
            ConversationProfile: ConversationProfile.Audiobook);

        RunRequestValidator.Validate(request);

        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(
            request with { ConversationProfile = (ConversationProfile)999 }));
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(
            request with { AllowedServerTools = null }));
    }

    [Fact]
    public void ContextPreparationUsesGeneralWithoutTools()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", "Verdichte den Verlauf.")])],
            ClientCapabilities: [],
            AllowedServerTools: [],
            PreferredGeneralModelId: "gpt-oss-120b",
            ConversationProfile: ConversationProfile.ContextPreparation);

        RunRequestValidator.Validate(request);

        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(
            request with { ClientCapabilities = ["unknown-capability"] }));
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(
            request with { AllowedServerTools = ["web.search"] }));
    }

    [Fact]
    public void ValidDynamicClientToolIsAccepted()
    {
        var request = CreateRequestWithClientTools(
        [
            CreateDynamicTool("com.example.lookup"),
        ]);

        RunRequestValidator.Validate(request);
    }

    [Fact]
    public void ExplicitCodingProcessAndWorkspaceOpenCapabilitiesAreAccepted()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Coding,
            [new RunMessage("user", [new ContentPart("text", "Prüfe das Projekt.")])],
            ClientCapabilities: ["coding", "coding.process", "workspace", "workspace.open"]);

        RunRequestValidator.Validate(request);
    }

    [Fact]
    public void ClaudeScienceSandboxCapabilityIsAcceptedOnlyWithSandboxResearch()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Auto,
            [new RunMessage("user", [new ContentPart("text", "Prüfe die Hypothese reproduzierbar.")])],
            ClientCapabilities: ["research.sandbox"],
            AllowedServerTools: ["web.search", "web.fetch"],
            DeepResearch: true,
            ResearchOptions: new(
                ProjectId: "research-fixture",
                AutonomyLevel: ResearchAutonomyLevel.SandboxResearch));

        RunRequestValidator.Validate(request);

        var missingCapability = Assert.Throws<ArgumentException>(() =>
            RunRequestValidator.Validate(request with { ClientCapabilities = [] }));
        Assert.Contains("research.sandbox", missingCapability.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReservedDynamicClientToolNameIsRejected()
    {
        var request = CreateRequestWithClientTools(
        [
            CreateDynamicTool("web.search"),
        ]);

        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(request));
    }

    [Fact]
    public void PermissiveDynamicClientToolSchemaIsRejected()
    {
        var permissiveSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string" },
            },
            required = RequiredQueryProperties,
            additionalProperties = true,
        });
        var request = CreateRequestWithClientTools(
        [
            CreateDynamicTool("com.example.lookup", permissiveSchema),
        ]);

        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(request));
    }

    [Fact]
    public void DuplicateDynamicClientToolNameIsRejected()
    {
        var request = CreateRequestWithClientTools(
        [
            CreateDynamicTool("com.example.lookup"),
            CreateDynamicTool("com.example.lookup"),
        ]);

        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(request));
    }

    private static RunRequest CreateRequestWithClientTools(IReadOnlyList<ToolDescriptor> clientTools) => new(
        GoAiProtocol.Version,
        RunMode.General,
        [new RunMessage("user", [new ContentPart("text", "Nutze die Erweiterung.")])],
        ClientTools: clientTools);

    private static ToolDescriptor CreateDynamicTool(string name, JsonElement? inputSchema = null) => new(
        name,
        "Liest einen begrenzten lokalen Datensatz.",
        ToolRiskClass.ReadOnly,
        inputSchema ?? JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string" },
            },
            required = RequiredQueryProperties,
            additionalProperties = false,
        }),
        TimeoutSeconds: 30,
        MaximumOutputBytes: 16_384);
}
