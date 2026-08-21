using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Gateway;

namespace GoAi.Server.Tests;

public sealed class RunRequestValidatorTests
{
    [Fact]
    public void ValidConversationContractIsAccepted()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Auto,
            [new RunMessage("user", [new ContentPart("text", "TGA-Frage")])],
            ClientCapabilities: ["filesystem", "screenCapture"],
            SessionId: "session-1");

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
            ClientCapabilities: ["shell"]);
        var invalidUpload = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("file", UploadId: "../../secret")])]);

        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(systemRole));
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(unknownCapability));
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(invalidUpload));
    }

    [Fact]
    public void PersistentCodingWorkspaceMayUse262KContextAndFourHourRepairWindow()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Code,
            [new RunMessage("user", [new ContentPart("text", "Analysiere und behebe das Repository")])],
            ClientCapabilities: ["filesystem", "code", "process"],
            Limits: new RunLimits(8_192, 262_144, 14_400),
            Workspace: new WorkspaceDescriptor(
                "GO-WinUI",
                new string('a', 64),
                new string('b', 64),
                "[GO_REPOSITORY_MAP_V1]\n- windows/build.ps1",
                42,
                40,
                123_456,
                DateTimeOffset.UtcNow));

        RunRequestValidator.Validate(request);

        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(
            request with { Limits = request.Limits! with { TimeoutSeconds = 14_401 } }));
    }

    [Theory]
    [InlineData(CodingModelCatalog.DeepSeekV4FlashId)]
    [InlineData(CodingModelCatalog.Qwen3CoderNextId)]
    [InlineData(CodingModelCatalog.GptOss120BId)]
    public void EveryCatalogCodingModelIsAccepted(string modelId)
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Code,
            [new RunMessage("user", [new ContentPart("text", "Behebe den Fehler und pr\u00fcfe die \u00c4nderung.")])],
            ClientCapabilities: ["filesystem", "code", "process"],
            PreferredCodeModelId: modelId);

        RunRequestValidator.Validate(request);
    }

    [Fact]
    public void UnknownCodingModelIsRejected()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Code,
            [new RunMessage("user", [new ContentPart("text", "Bearbeite das Projekt.")])],
            PreferredCodeModelId: "unknown-coding-model");

        var error = Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(request));

        Assert.Contains("supported coding model", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DocumentCapabilityAndPreparedContextAreAccepted()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("document", "Dokument: Planung.pdf, Seite 1")])],
            ClientCapabilities: ["documents", "documents"],
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

        var missingCapability = request with { ClientCapabilities = [] };
        var error = Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(missingCapability));
        Assert.Contains("documents", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownCapabilityErrorNamesTheRejectedCapability()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", "Test")])],
            ClientCapabilities: ["shell"]);

        var error = Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(request));

        Assert.Contains("shell", error.Message, StringComparison.Ordinal);
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
            request with { Mode = RunMode.Code }));
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(
            request with { AllowedServerTools = null }));
    }
}
