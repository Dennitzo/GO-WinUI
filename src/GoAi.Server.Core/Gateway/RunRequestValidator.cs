using GoAi.Contracts;

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
        if (request.ClientCapabilities is { Count: > 16 }
            || request.ClientCapabilities?.Any(capability =>
                string.IsNullOrWhiteSpace(capability) || !ClientCapabilities.Contains(capability)) == true)
        {
            throw new ArgumentException("The run contains unknown or excessive client capabilities.");
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
