using GoAi.Contracts;
using GoAi.Server.Core.Configuration;

namespace GoAi.Server.Core.Gateway;

public static class RunRequestValidator
{
    private static readonly HashSet<string> ClientCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "filesystem",
        "code",
        "process",
        "bricscad",
        "screenCapture",
        "documents",
    };
    private static readonly HashSet<string> ServerTools = new(StringComparer.Ordinal)
    {
        "web.search", "web.fetch", "youtube.search", "media.inspect", "media.analyze",
        "image.generate", "math.evaluate", "context.embed", "context.retrieve",
    };

    public static void Validate(RunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
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
        if (request.ConversationProfile == ConversationProfile.Audiobook
            && (request.Mode != RunMode.General || request.AllowedServerTools is not { Count: 0 }))
        {
            throw new ArgumentException("Audiobook runs require general mode and an explicit empty server-tool allow-list.");
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
        if (request.AllowedServerTools is { Count: > 32 }
            || request.AllowedServerTools?.Any(tool =>
                string.IsNullOrWhiteSpace(tool) || !ServerTools.Contains(tool)) == true)
        {
            throw new ArgumentException("The run contains unknown or excessive allowed server tools.");
        }
        if (request.SessionId?.Length > 128)
        {
            throw new ArgumentException("sessionId may contain at most 128 characters.");
        }
        if (request.Workspace is { } workspace)
        {
            if (request.Mode != RunMode.Code
                || string.IsNullOrWhiteSpace(workspace.Name)
                || workspace.Name.Length > 256
                || workspace.Fingerprint.Length != 64
                || workspace.Revision.Length != 64
                || workspace.RepositoryMap.Length is < 1 or > 256_000
                || workspace.FileCount is < 0 or > 100_000
                || workspace.TextFileCount < 0
                || workspace.TextFileCount > workspace.FileCount
                || workspace.TextBytes < 0)
            {
                throw new ArgumentException("The coding workspace descriptor is invalid.");
            }
            if (!IsLowerHex(workspace.Fingerprint) || !IsLowerHex(workspace.Revision))
            {
                throw new ArgumentException("Workspace fingerprints must be lowercase SHA-256 values.");
            }
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
        if (request.PreferredCodeModelId is { } preferredCodeModel
            && (request.Mode != RunMode.Code
                || !CodingModelCatalog.TryGet(preferredCodeModel, out _)))
        {
            throw new ArgumentException(
                "preferredCodeModelId is only valid in code mode and must name a supported coding model.");
        }
        if (request.Limits?.MaximumOutputTokens is { } maximumOutputTokens
            && maximumOutputTokens is < 1 or > 65_536)
        {
            throw new ArgumentException("maximumOutputTokens must be between 1 and 65536.");
        }
        if (request.Limits?.MaximumContextTokens is { } maximumContextTokens
            && maximumContextTokens is < 1_024 or > 262_144)
        {
            throw new ArgumentException("maximumContextTokens must be between 1024 and 262144.");
        }
        if (request.Limits?.TimeoutSeconds is { } timeoutSeconds
            && timeoutSeconds is < 30 or > 14_400)
        {
            throw new ArgumentException("timeoutSeconds must be between 30 and 14400.");
        }
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
}
