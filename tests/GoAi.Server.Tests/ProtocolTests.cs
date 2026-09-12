using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Policies;
using GoAi.Server.Core.Status;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void GeneralPoliciesRemainValidUtf8GermanText()
    {
        Assert.Contains("allgemeine KI-Assistent", GeneralAgentPolicies.GeneralCoordinator, StringComparison.Ordinal);
        Assert.Contains("setze kein bestimmtes Fachgebiet voraus", GeneralAgentPolicies.GeneralCoordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("TGA", GeneralAgentPolicies.GeneralCoordinator, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("keine technische Titelzeile", GeneralAgentPolicies.FinalResponseContract, StringComparison.Ordinal);
        Assert.DoesNotContain("Ã", GeneralAgentPolicies.GeneralCoordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("Ã", GeneralAgentPolicies.FinalResponseContract, StringComparison.Ordinal);
        Assert.Contains("eintausendfünfhundert bis", GeneralAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
        Assert.Contains("jede Zahl als natürlich ausgeschriebenes deutsches Wort", GeneralAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
        Assert.Contains("zwei Prozent", GeneralAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
        Assert.Contains("CONTINUATION_ANCHOR", GeneralAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
        Assert.Contains("unbegrenzt fortlaufende Serie", GeneralAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
        Assert.Contains("mindestens eine klar ausgearbeitete Hauptfigur", GeneralAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
        Assert.Contains("noch nicht eingetretenen Serienhandlungen", GeneralAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
        Assert.Contains("Der Beginn eines neuen AI-Laufs ist", GeneralAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
        Assert.Contains("tatsächlich ein neues Kapitel beginnt", GeneralAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
        Assert.Contains("# Kapitel eins – Titel", GeneralAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Erkläre Rekursion mit einem Python-Beispiel.")]
    [InlineData("Überarbeite diesen Romanabsatz.")]
    [InlineData("Plane eine Geburtstagsfeier.")]
    public void GeneralConversationPolicyTakesItsSubjectFromTheUserAndPreservesTheirText(string prompt)
    {
        var request = new RunRequest(GoAiProtocol.Version, RunMode.General, [new("user", [new("text", prompt)])]);
        var policy = GeneralAgentPolicies.ForConversation("general", request, []);
        Assert.Contains("aus dem aktuellen", policy, StringComparison.Ordinal);
        Assert.Contains("Nutzerauftrag", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("TGA", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Heizung, Lüftung", policy, StringComparison.Ordinal);
        Assert.Equal(prompt, request.Messages[0].Content[0].Text);
    }

    [Fact]
    public void DefaultMediaPromptsDescribeTheProvidedContentWithoutAnIndustryPreset()
    {
        Assert.Contains("anhand seines Inhalts", GeneralAgentPolicies.DefaultTranscriptAnalysis, StringComparison.Ordinal);
        Assert.Contains("tatsächlichen Inhalt", GeneralAgentPolicies.DefaultMediaAnalysis, StringComparison.Ordinal);
        Assert.Contains("sichtbaren Vorgänge", GeneralAgentPolicies.DefaultVideoAnalysis, StringComparison.Ordinal);
        Assert.DoesNotContain("Planung", GeneralAgentPolicies.DefaultMediaAnalysis, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunRequestUsesStrictCamelCaseAndStringEnums()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", "Build prüfen")])],
            ConversationProfile: ConversationProfile.General);

        var json = JsonSerializer.Serialize(request, GoAiProtocol.CreateJsonOptions());

        Assert.Contains("\"protocolVersion\":\"1.0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"general\"", json, StringComparison.Ordinal);
        Assert.Contains("\"conversationProfile\":\"general\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ProtocolVersion\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtocolConstantsKeepEightMebibyteChunks()
    {
        Assert.Equal(8 * 1024 * 1024, GoAiProtocol.UploadChunkSize);
        Assert.Equal("/v1", GoAiProtocol.ApiPrefix);
    }

    [Fact]
    public void CapabilitiesExposeOnlyThePrimaryVisionModel()
    {
        var snapshot = new CapabilityService(Options.Create(new GoAiServerOptions())).GetSnapshot();

        var vision = Assert.Single(snapshot.Models, static model => model.Role == "vision");
        Assert.Equal("qwen3-vl-30b-a3b-instruct", vision.Id);
        Assert.False(vision.IsFallback);
        Assert.DoesNotContain(snapshot.Models, static model => model.Role.Contains("fallback", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(snapshot.Models, static model => model.Id.Contains("qwen3-vl-8b", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CapabilitiesAdvertiseTheRealReasoningLevelsPerModel()
    {
        var snapshot = new CapabilityService(Options.Create(new GoAiServerOptions())).GetSnapshot();

        var general = Assert.Single(snapshot.Models, static model => model.Role == "general");
        Assert.Equal(["low", "medium", "high"], general.ReasoningEfforts);
        Assert.Equal("high", general.DefaultReasoningEffort);

        Assert.DoesNotContain(snapshot.Models, static model => model.Role == "code");

        var vision = Assert.Single(snapshot.Models, static model => model.Role == "vision");
        Assert.Equal(262_144, vision.ContextTokens);
    }

    [Fact]
    public void CapabilitiesAdvertiseSharedDocumentTools()
    {
        var snapshot = new CapabilityService(Options.Create(new GoAiServerOptions())).GetSnapshot();

        Assert.Contains(ClientToolNames.DocumentRead, snapshot.ClientTools);
        Assert.Contains(ClientToolNames.DocumentCreate, snapshot.ClientTools);
    }

    [Fact]
    public void UnknownContractPropertiesAreRejected()
    {
        const string json = """
            {"protocolVersion":"1.0","mode":"general","messages":[],"unexpected":true}
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RunRequest>(json, GoAiProtocol.CreateJsonOptions()));
    }

    [Fact]
    public void RemovedCodeModeIsRejectedByTheExternalContract()
    {
        const string json = """
            {"protocolVersion":"1.0","mode":"code","messages":[{"role":"user","content":[{"type":"text","text":"Test"}]}]}
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<RunRequest>(json, GoAiProtocol.CreateJsonOptions()));
    }

    [Fact]
    public void LiveCaptionContractUsesVersionedLimitsAndStringMode()
    {
        var request = new LiveCaptionSessionRequest(Mode: LiveCaptionMode.TranslateToEnglish);

        var json = JsonSerializer.Serialize(request, GoAiProtocol.CreateJsonOptions());

        Assert.Contains("\"mode\":\"translateToEnglish\"", json, StringComparison.Ordinal);
        Assert.Equal(512 * 1024, GoAiProtocol.MaximumLiveCaptionChunkBytes);
        Assert.Equal(16_000, GoAiProtocol.LiveCaptionSampleRate);
    }

    [Fact]
    public void DictationContractIsAdditiveAndCarriesRevisableText()
    {
        var request = new LiveCaptionSessionRequest(
            Language: null,
            WindowMilliseconds: 6_000,
            OverlapMilliseconds: 0,
            Profile: LiveCaptionProfile.Dictation);
        var response = new LiveCaptionChunkResponse(
            "caption-test",
            3,
            "Heizlast berechnen",
            "Heizlast berechnen",
            "de",
            0.98,
            [],
            false,
            "faster-whisper-large-v3-dictation",
            DateTimeOffset.UtcNow,
            "turn-1",
            7,
            "Heizlast",
            "berechnen");

        var requestJson = JsonSerializer.Serialize(request, GoAiProtocol.CreateJsonOptions());
        var responseJson = JsonSerializer.Serialize(response, GoAiProtocol.CreateJsonOptions());

        Assert.Contains("\"profile\":\"dictation\"", requestJson, StringComparison.Ordinal);
        Assert.Contains("\"stableText\":\"Heizlast\"", responseJson, StringComparison.Ordinal);
        Assert.Contains("\"provisionalText\":\"berechnen\"", responseJson, StringComparison.Ordinal);
        Assert.Contains("\"revision\":7", responseJson, StringComparison.Ordinal);
    }

    [Fact]
    public void GpuStatusRemainsCompatibleWithGatewayWithoutStructuredWorkloads()
    {
        const string json = """
            {"available":true,"queueLength":0,"activeLease":"lease-old","devices":[],"checkedAt":"2026-08-14T08:00:00+00:00"}
            """;

        var status = JsonSerializer.Deserialize<GpuStatusSnapshot>(json, GoAiProtocol.CreateJsonOptions());

        Assert.NotNull(status);
        Assert.Equal("lease-old", status.ActiveLease);
        Assert.Null(status.ActiveWorkloads);
    }

    [Fact]
    public void ContainerPathsStayUnderMountedDataDirectories()
    {
        var options = new GoAiServerOptions
        {
            DataDirectory = @"C:\GO-AI-Test\RunData",
            WorkerDataDirectory = @"C:\GO-AI-Test\WorkerData",
        };

        Assert.Equal(
            Path.GetFullPath(@"C:\GO-AI-Test\WorkerData\uploads"),
            options.UploadDirectory);
        Assert.Equal(
            Path.GetFullPath(@"C:\GO-AI-Test\WorkerData"),
            options.ResolvedWorkerDataDirectory);
        Assert.Equal(
            Path.GetFullPath(@"C:\GO-AI-Test\WorkerData\artifacts\worker"),
            options.WorkerArtifactDirectory);
        Assert.Equal(
            Path.GetFullPath(@"C:\GO-AI-Test\RunData\artifacts"),
            options.ArtifactDirectory);
    }
}
