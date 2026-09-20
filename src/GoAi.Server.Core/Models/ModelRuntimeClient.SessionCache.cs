using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Core.Models;

public sealed partial class ModelRuntimeClient
{
    private Task<string?> SaveInterruptedSessionCacheAsync(string? preparedModelId, string? sessionCacheKey, int? promptTokens)
    {
        // The HTTP stream has already been disposed by the caller. The native
        // supervisor drains the brief cancellation/slot-idle race before saving.
        // Keep the turn gate until it replies so a follow-up cannot overwrite
        // the slot while its interrupted prefix is being snapshotted.
        return string.IsNullOrEmpty(preparedModelId) || string.IsNullOrEmpty(sessionCacheKey)
            ? Task.FromResult<string?>(null)
            : UpdateSessionCacheAsync("save", preparedModelId, sessionCacheKey, CancellationToken.None, promptTokens);
    }

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
