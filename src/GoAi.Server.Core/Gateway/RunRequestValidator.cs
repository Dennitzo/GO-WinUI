using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Runs;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GoAi.Server.Core.Gateway;

public static class RunRequestValidator
{
    private static readonly HashSet<string> ClientCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "screenCapture",
        "documents",
        "documentIo",
        "workspace", "visual-tools",
        "pdf",
        "coding",
        "coding.evidence",
        "coding.process",
        "workspace.open",
        "research.sandbox",
    };
    private static readonly HashSet<string> ServerTools = new(StringComparer.Ordinal)
    {
        "web.search", "web.fetch", "web.deepResearch", "media.inspect", "media.analyze",
        "image.generate", "speech.synthesize", "math.evaluate", "context.embed", "context.retrieve",
    };

    public static void Validate(RunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DeepResearch && (request.AllowedServerTools is not { } researchTools
            || !researchTools.Contains("web.search", StringComparer.Ordinal)
            || !researchTools.Contains("web.fetch", StringComparer.Ordinal)
            || request.Mode == RunMode.Coding && !researchTools.Contains("web.deepResearch", StringComparer.Ordinal)
            || request.Workload is { Kind: not RunWorkloadKind.Conversation }
            || request.ConversationProfile is ConversationProfile.Audiobook or ConversationProfile.ContextPreparation))
            throw new ArgumentException("Deep Research benötigt einen General-/Coding-Dialog mit ausdrücklich erlaubter Websuche und Quellenabruf.");
        ValidateResearchOptions(request);
        if (!string.Equals(request.ProtocolVersion, GoAiProtocol.Version, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unsupported protocolVersion. Expected {GoAiProtocol.Version}.");
        }
        if (!Enum.IsDefined(request.Mode))
        {
            throw new ArgumentException("Run mode is invalid.");
        }
        if (request.ConversationProfile is { } conversationProfile && !Enum.IsDefined(conversationProfile))
        {
            throw new ArgumentException("Conversation profile is invalid.");
        }
        if (request.Mode == RunMode.Coding
            && request.ClientCapabilities?.Contains("coding", StringComparer.OrdinalIgnoreCase) != true)
        {
            throw new ArgumentException("Coding runs require the coding capability and a selected local workspace.");
        }
        if (request.ConversationProfile == ConversationProfile.Audiobook
            && (request.Mode != RunMode.General || request.AllowedServerTools is not { Count: 0 }))
        {
            throw new ArgumentException("Audiobook runs require general mode and an explicit empty server-tool allow-list.");
        }
        if (request.CodingOptions is { } codingOptions)
        {
            if (codingOptions.WorkspacePath is { Length: > 4096 }
                || (codingOptions.ContinueSessionContext && (string.IsNullOrWhiteSpace(codingOptions.WorkspacePath) || string.IsNullOrWhiteSpace(request.SessionId))))
                throw new ArgumentException("Coding session continuation requires a session and workspace identity.");
            if (request.Mode != RunMode.Coding || codingOptions.ReasoningPolicy is not ("maximum" or "adaptive"))
                throw new ArgumentException("Coding options contain an unsupported policy.");
        }
        if (request.ConversationProfile == ConversationProfile.ContextPreparation
            && (request.Mode != RunMode.General
                || request.AllowedServerTools is not { Count: 0 }
                || request.ClientCapabilities is not { Count: 0 }
                || request.ClientTools is not null
                || request.DocumentContext is not null))
        {
            throw new ArgumentException(
                "Context-preparation runs require general mode without tools, capabilities, or document descriptors.");
        }
        if (request.Workload is not null)
        {
            throw new ArgumentException("Server workloads must use their dedicated protocol endpoint.");
        }
        if (request.Messages is null || request.Messages.Count is < 1 or > 500)
        {
            throw new ArgumentException("A run requires between 1 and 500 messages.");
        }

        long totalTextCharacters = 0;
        var hasUserContent = false;
        foreach (var message in request.Messages)
        {
            if (message is null
                || message.Role is null
                || (!string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("Each run message role must be user or assistant.");
            }
            if (message.Content is null || message.Content.Count is < 1 or > 100)
            {
                throw new ArgumentException("Each run message requires between 1 and 100 content parts.");
            }

            foreach (var part in message.Content)
            {
                if (part is null || string.IsNullOrWhiteSpace(part.Type) || part.Type.Length > 32)
                {
                    throw new ArgumentException("Each content part requires a bounded type.");
                }
                if (part.Text?.Length > 256_000
                    || part.FileName?.Length > 512
                    || part.MediaType?.Length > 128)
                {
                    throw new ArgumentException("A content part exceeds the protocol limits.");
                }
                if (part.UploadId is not null && !IsProtocolId(part.UploadId, "upload-"))
                {
                    throw new ArgumentException("A content part contains an invalid upload ID.");
                }
                if (part.ArtifactId is not null && !IsProtocolId(part.ArtifactId, "artifact-"))
                {
                    throw new ArgumentException("A content part contains an invalid artifact ID.");
                }
                if (string.IsNullOrWhiteSpace(part.Text)
                    && string.IsNullOrWhiteSpace(part.UploadId)
                    && string.IsNullOrWhiteSpace(part.ArtifactId))
                {
                    throw new ArgumentException("A content part must contain text, an upload, or an artifact.");
                }

                totalTextCharacters += part.Text?.Length ?? 0;
                if (totalTextCharacters > 1_500_000)
                {
                    throw new ArgumentException("Run text content exceeds the protocol limit.");
                }
                hasUserContent |= string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase);
            }
        }
        if (!hasUserContent)
        {
            throw new ArgumentException("A run requires at least one user content part.");
        }

        ValidateIds(request.UploadIds, "upload-");
        ValidateIds(request.ArtifactIds, "artifact-");
        if (request.ClientCapabilities is { Count: > 16 })
        {
            throw new ArgumentException("The run contains more than 16 client capabilities.");
        }
        var unknownCapability = request.ClientCapabilities?.FirstOrDefault(capability =>
            string.IsNullOrWhiteSpace(capability) || !ClientCapabilities.Contains(capability));
        if (unknownCapability is not null)
        {
            throw new ArgumentException(
                string.IsNullOrWhiteSpace(unknownCapability)
                    ? "The run contains an empty client capability."
                    : $"The run contains the unknown client capability '{unknownCapability}'.");
        }
        ValidateClientTools(request.ClientTools);
        if (request.AllowedServerTools is { Count: > 32 }
            || request.AllowedServerTools?.Any(tool =>
                string.IsNullOrWhiteSpace(tool) || !ServerTools.Contains(tool)) == true)
        {
            throw new ArgumentException("The run contains unknown or excessive allowed server tools.");
        }
        if (request.AllowedServerTools?.Contains("web.deepResearch", StringComparer.Ordinal) == true
            && (!request.AllowedServerTools.Contains("web.search", StringComparer.Ordinal)
                || !request.AllowedServerTools.Contains("web.fetch", StringComparer.Ordinal)))
            throw new ArgumentException("web.deepResearch requires explicit web.search/web.fetch permission.");
        if (request.SessionId?.Length > 128)
        {
            throw new ArgumentException("sessionId may contain at most 128 characters.");
        }
        if (request.DocumentContext is { } documentContext)
        {
            if (!Enum.IsDefined(documentContext.Mode)
                || documentContext.CorpusRevision is null
                || documentContext.CorpusRevision.Length != 64
                || !IsLowerHex(documentContext.CorpusRevision)
                || documentContext.DocumentCount is < 1 or > 10_000
                || documentContext.PageCount is < 1 or > 1_000_000
                || documentContext.EstimatedTokens is < 1 or > 20_000_000
                || documentContext.IncludedPageCount < 1
                || documentContext.IncludedPageCount > documentContext.PageCount
                || documentContext.Mode == DocumentContextMode.Full
                    && (documentContext.IncludedPageCount != documentContext.PageCount
                        || documentContext.PreparedByAi)
                || documentContext.Mode == DocumentContextMode.Prepared
                    && !documentContext.PreparedByAi)
            {
                throw new ArgumentException("The document context descriptor is invalid.");
            }
            if (request.ClientCapabilities?.Contains("documents", StringComparer.OrdinalIgnoreCase) != true
                && documentContext.Mode == DocumentContextMode.Prepared)
            {
                throw new ArgumentException("Prepared document context requires the documents client capability.");
            }
        }
        if (request.SessionContext is { } sessionContext
            && (sessionContext.HistoryRevision is null
                || sessionContext.HistoryRevision.Length != 64
                || !IsLowerHex(sessionContext.HistoryRevision)
                || sessionContext.OriginalMessageCount is < 0 or > 500
                || sessionContext.IncludedMessageCount is < 0 or > 500
                || sessionContext.IncludedMessageCount > sessionContext.OriginalMessageCount
                || sessionContext.EstimatedTokens is < 0 or > 20_000_000
                || sessionContext.PreparedByAi && sessionContext.OriginalMessageCount == 0))
        {
            throw new ArgumentException("The session context descriptor is invalid.");
        }
        if (request.PreferredGeneralModelId is { } preferredModel
            && (string.IsNullOrWhiteSpace(preferredModel)
                || preferredModel.Length > 512
                || preferredModel.Any(char.IsControl)))
        {
            throw new ArgumentException("preferredGeneralModelId must contain a bounded model ID.");
        }
        if (request.ReasoningEffort is { } reasoningEffort)
        {
            var reasoningModelId = request.Mode == RunMode.Coding ? request.PreferredCodingModelId : request.PreferredGeneralModelId;
            var reasoningRole = request.Mode == RunMode.Coding ? "coding" : "general";
            var reasoningProfile = ModelReasoningProfiles.Resolve(reasoningModelId, reasoningRole);
            if (string.IsNullOrWhiteSpace(reasoningModelId)
                || reasoningEffort.Length > 32
                || reasoningEffort.Any(char.IsControl)
                || (!reasoningProfile.Supports(reasoningEffort)
                    && !IsReasoningEffortName(reasoningEffort)))
            {
                var supported = reasoningProfile.SupportedEfforts.Count == 0
                    ? "keine steuerbare Stufe"
                    : string.Join(", ", reasoningProfile.SupportedEfforts);
                throw new ArgumentException(
                    $"reasoningEffort '{reasoningEffort}' wird vom ausgewählten Modell nicht unterstützt ({supported}).");
            }
        }
        if (request.PreferredCodingModelId is { } codingModel
            && (string.IsNullOrWhiteSpace(codingModel) || codingModel.Length > 512 || codingModel.Any(char.IsControl)))
        {
            throw new ArgumentException("preferredCodingModelId must contain a bounded model ID.");
        }
        if (request.Limits?.MaximumOutputTokens is { } maximumOutputTokens
            && maximumOutputTokens < 1)
        {
            throw new ArgumentException("maximumOutputTokens must be positive; the loaded model context bounds the effective limit.");
        }
        if (request.Limits?.MaximumContextTokens is { } maximumContextTokens
            && maximumContextTokens < 2_048)
        {
            throw new ArgumentException("maximumContextTokens must be at least 2048; the model catalog bounds the effective limit.");
        }
        var supportsUnlimitedDuration = request.Mode == RunMode.Coding;
        if (request.Limits?.TimeoutSeconds is { } timeoutSeconds
            && (timeoutSeconds == 0 ? !supportsUnlimitedDuration
                : timeoutSeconds < 30 || request.Mode != RunMode.Coding && timeoutSeconds > 14_400))
        {
            throw new ArgumentException("timeoutSeconds must be 0 for Coding or at least 30; finite non-Coding limits support up to 14400 seconds.");
        }
    }

    private static void ValidateResearchOptions(RunRequest request)
    {
        if (request.ResearchOptions is not { } options) return;
        if (!request.DeepResearch)
            throw new ArgumentException("researchOptions requires Deep Research.");
        if (!Enum.IsDefined(options.Profile)
            || !Enum.IsDefined(options.AutonomyLevel)
            || !Enum.IsDefined(options.VerificationLevel))
            throw new ArgumentException("Deep Research contains an unknown profile, autonomy, or verification level.");
        if (options.AutonomyLevel == ResearchAutonomyLevel.CodingWorkspaceResearch
            && (request.Mode != RunMode.Coding
                || string.IsNullOrWhiteSpace(request.CodingOptions?.WorkspacePath)))
            throw new ArgumentException("Executable Deep Research requires Coding mode and an assigned workspace.");
        if (options.AutonomyLevel == ResearchAutonomyLevel.SandboxResearch
            && (request.Mode is not (RunMode.General or RunMode.Auto)
                || !(request.ClientCapabilities ?? []).Contains("research.sandbox", StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Sandbox research requires a prepared, client-scoped research.sandbox runtime.");
        if (options.ProjectId is { } projectId
            && (projectId.Length is < 1 or > 128 || projectId.Any(char.IsControl)))
            throw new ArgumentException("researchOptions.projectId is invalid.");
        if (options.ResumeCheckpointId is { } checkpointId
            && (checkpointId.Length is < 1 or > 128 || checkpointId.Any(char.IsControl)))
            throw new ArgumentException("researchOptions.resumeCheckpointId is invalid.");
        if (options.ProtocolVersion is < 1
            || options.MaximumWorks is < 1 or > 10_000
            || options.MaximumFullTexts is < 1 or > 1_000
            || options.PreferredLanguages is { Count: > 16 }
            || options.PreferredLanguages?.Any(static language =>
                string.IsNullOrWhiteSpace(language) || language.Length > 16 || language.Any(char.IsControl)) == true)
            throw new ArgumentException("Deep Research options exceed their protocol limits.");
    }

    private static void ValidateClientTools(IReadOnlyList<ToolDescriptor>? tools)
    {
        if (tools is null) return;
        if (tools.Count is < 1 or > 16)
            throw new ArgumentException("Dynamic client tools must contain between one and 16 descriptors.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (tool is null
                || string.IsNullOrWhiteSpace(tool.Name)
                || tool.Name.Length > 160
                || !Regex.IsMatch(tool.Name, "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+$", RegexOptions.CultureInvariant)
                || tool.Name.StartsWith("go.", StringComparison.OrdinalIgnoreCase)
                || tool.Name.Contains("gowinui", StringComparison.OrdinalIgnoreCase)
                || !names.Add(tool.Name)
                || AgentToolCatalog.IsReservedToolName(tool.Name))
                throw new ArgumentException("A dynamic client tool has an invalid, duplicate, or reserved name.");
            if (string.IsNullOrWhiteSpace(tool.Description)
                || tool.Description.Length > 300
                || !string.Equals(tool.Description, tool.Description.Trim(), StringComparison.Ordinal))
                throw new ArgumentException($"Dynamic client tool '{tool.Name}' has an invalid description.");
            if (tool.RiskClass is not (ToolRiskClass.ReadOnly or ToolRiskClass.LocalMutation or ToolRiskClass.Process)
                || tool.TimeoutSeconds is < 1 or > 7_200
                || tool.MaximumOutputBytes is < 1_024 or > 64 * 1024 * 1024)
                throw new ArgumentException($"Dynamic client tool '{tool.Name}' has invalid execution limits.");
            ValidateClientToolSchema(tool.Name, tool.InputSchema);
        }
    }

    private static void ValidateClientToolSchema(string name, JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || schema.GetRawText().Length > 65_536)
            throw new ArgumentException($"Dynamic client tool '{name}' has an invalid input schema.");
        if (schema.EnumerateObject().Any(static property => property.Name is not
            ("type" or "properties" or "required" or "additionalProperties" or "description")))
            throw new ArgumentException($"Dynamic client tool '{name}' uses unsupported schema keywords.");
        if (!schema.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "object", StringComparison.Ordinal)
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object
            || properties.EnumerateObject().Count() > 64
            || !schema.TryGetProperty("additionalProperties", out var additional)
            || additional.ValueKind != JsonValueKind.False)
            throw new ArgumentException($"Dynamic client tool '{name}' must use a bounded object schema with additionalProperties=false.");
        var propertyNames = properties.EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (propertyNames.Any(static property => string.IsNullOrWhiteSpace(property) || property.Length > 80))
            throw new ArgumentException($"Dynamic client tool '{name}' has an invalid property name.");
        if (!schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array
            || required.GetArrayLength() > propertyNames.Count)
            throw new ArgumentException($"Dynamic client tool '{name}' must declare a bounded required array.");
        var requiredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in required.EnumerateArray())
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } requiredName
                || !propertyNames.Contains(requiredName) || !requiredNames.Add(requiredName))
                throw new ArgumentException($"Dynamic client tool '{name}' has an invalid required property.");
    }

    private static void ValidateIds(IReadOnlyList<string>? ids, string prefix)
    {
        if (ids is { Count: > 64 } || ids?.Any(id => !IsProtocolId(id, prefix)) == true)
        {
            throw new ArgumentException($"The run contains invalid or excessive {prefix.TrimEnd('-')} IDs.");
        }
    }

    private static bool IsProtocolId(string? value, string prefix)
    {
        if (value is null
            || value.Length != prefix.Length + 32
            || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        for (var index = prefix.Length; index < value.Length; index++)
        {
            if (!Uri.IsHexDigit(value[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsLowerHex(string value) => value.All(static character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsReasoningEffortName(string value) => value.Length is > 0 and <= 32
        && char.IsAsciiLetter(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
