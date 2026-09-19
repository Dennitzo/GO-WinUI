using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Core.Models;

public sealed partial class ModelRuntimeClient
{
    internal static string BuildSessionCacheKey(string sessionId, string role, string? workspacePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        // A UI tab, run ID, model selection or process lifetime must not change
        // this identity. The supervisor separately binds snapshots to the exact
        // native model, binary, template and cache configuration.
        var workspace = (workspacePath ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/').ToUpperInvariant();
        var scope = JsonSerializer.Serialize(new[] { sessionId, role.Trim().ToLowerInvariant(), workspace });
        return "go-session-v2-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))).ToLowerInvariant();
    }
}
