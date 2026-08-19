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
    public void TgaPoliciesRemainValidUtf8GermanText()
    {
        Assert.Contains("für die TGA-Fachplanung", TgaAgentPolicies.GeneralCoordinator, StringComparison.Ordinal);
        Assert.Contains("höchstens sechs Wörtern", TgaAgentPolicies.FinalResponseContract, StringComparison.Ordinal);
        Assert.DoesNotContain("Ã", TgaAgentPolicies.GeneralCoordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("Ã", TgaAgentPolicies.FinalResponseContract, StringComparison.Ordinal);
        Assert.Contains("eintausendfünfhundert bis", TgaAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
        Assert.Contains("jede Zahl als natürlich ausgeschriebenes deutsches Wort", TgaAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
        Assert.Contains("zwei Prozent", TgaAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
        Assert.Contains("CONTINUATION_ANCHOR", TgaAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
        Assert.Contains("unbegrenzt fortlaufende Serie", TgaAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
        Assert.Contains("mindestens eine klar ausgearbeitete Hauptfigur", TgaAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
        Assert.Contains("noch nicht eingetretenen Serienhandlungen", TgaAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
        Assert.Contains("Der Beginn eines neuen AI-Laufs ist", TgaAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
        Assert.Contains("tatsächlich ein neues Kapitel beginnt", TgaAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
        Assert.Contains("# Kapitel eins – Titel", TgaAgentPolicies.AudiobookAuthor, StringComparison.Ordinal);
    }

    [Fact]
    public void RunRequestUsesStrictCamelCaseAndStringEnums()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Code,
            [new RunMessage("user", [new ContentPart("text", "Build prüfen")])],
            ConversationProfile: ConversationProfile.General);

        var json = JsonSerializer.Serialize(request, GoAiProtocol.CreateJsonOptions());

        Assert.Contains("\"protocolVersion\":\"1.0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"code\"", json, StringComparison.Ordinal);
        Assert.Contains("\"conversationProfile\":\"general\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtocolVersion", json, StringComparison.Ordinal);
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
    public void UnknownContractPropertiesAreRejected()
    {
        const string json = """
            {"protocolVersion":"1.0","mode":"general","messages":[],"unexpected":true}
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RunRequest>(json, GoAiProtocol.CreateJsonOptions()));
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
    public void ProviderSecretOverridesKeepClientSecretsInTheRunDataDirectory()
    {
        var options = new GoAiServerOptions
        {
            DataDirectory = @"C:\GO-AI-Test\RunData",
            ProviderDataDirectory = @"C:\GO-AI-Test\ProviderData",
            WorkerDataDirectory = @"C:\GO-AI-Test\WorkerData",
        };

        Assert.Equal(
            Path.GetFullPath(@"C:\GO-AI-Test\ProviderData\Secrets\lmstudio-token.dpapi"),
            options.LmStudioTokenPath);
        Assert.Equal(
            Path.GetFullPath(@"C:\GO-AI-Test\ProviderData\Secrets\speech-worker.key"),
            options.GetWorkerKeyPath("speech"));
        Assert.Equal(
            Path.GetFullPath(@"C:\GO-AI-Test\RunData\Secrets\bootstrap-client-key.once"),
            options.BootstrapKeyExportPath);
        Assert.Equal(
            Path.GetFullPath(@"C:\GO-AI-Test\WorkerData\Uploads"),
            options.UploadDirectory);
        Assert.Equal(
            Path.GetFullPath(@"C:\GO-AI-Test\WorkerData"),
            options.ResolvedWorkerDataDirectory);
        Assert.Equal(
            Path.GetFullPath(@"C:\GO-AI-Test\WorkerData\Artifacts\worker"),
            options.WorkerArtifactDirectory);
        Assert.Equal(
            Path.GetFullPath(@"C:\GO-AI-Test\RunData\Artifacts"),
            options.ArtifactDirectory);
    }
}
