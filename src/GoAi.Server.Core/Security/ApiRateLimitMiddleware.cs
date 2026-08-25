using GoAi.Contracts;
using Microsoft.AspNetCore.Http;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace GoAi.Server.Core.Security;

public sealed class ApiRateLimitMiddleware
{
    private const int GeneralRequestsPerMinute = 240;
    private const int StatusRequestsPerMinute = 1_200;
    private const int RunControlRequestsPerMinute = 1_200;
    private const int SpeechRequestsPerMinute = 1_200;
    private const int ArtifactRequestsPerMinute = 1_200;
    private static readonly PathString[] AnonymousPaths =
    [
        new("/v1/health/live"),
        new("/v1/health/ready"),
    ];
    private readonly RequestDelegate _next;
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private long _lastCleanupTicks;

    public ApiRateLimitMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (AnonymousPaths.Any(path => context.Request.Path.Equals(path)))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var clientId = context.Request.Headers[GoAiHeaders.ClientId].FirstOrDefault();
        var sourceIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var presented = string.IsNullOrWhiteSpace(clientId)
            ? sourceIp
            : $"{sourceIp}:{clientId[..Math.Min(clientId.Length, 128)]}";
        var policy = ResolvePolicy(context.Request);
        var credentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(presented)));
        var partition = $"{credentialHash}:{policy.Name}";
        var now = DateTimeOffset.UtcNow;
        var window = _windows.GetOrAdd(partition, static _ => new Window());
        int count;
        DateTimeOffset resetAt;
        lock (window)
        {
            if (now - window.StartedAt >= TimeSpan.FromMinutes(1))
            {
                window.StartedAt = now;
                window.Count = 0;
            }

            count = ++window.Count;
            resetAt = window.StartedAt.AddMinutes(1);
        }

        Cleanup(now);
        context.Response.Headers["X-RateLimit-Limit"] = policy.RequestsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers["X-RateLimit-Remaining"] = Math.Max(0, policy.RequestsPerMinute - count).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (count <= policy.RequestsPerMinute)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var retryAfter = Math.Max(1, (int)Math.Ceiling((resetAt - now).TotalSeconds));
        context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await context.Response.WriteAsJsonAsync(
            new GoAiProblem(
                "https://go-ai.local/problems/rate-limit",
                "Zu viele Anfragen",
                (int)HttpStatusCode.TooManyRequests,
                "Das API-Limit wurde erreicht. Bitte nach kurzer Pause erneut versuchen.",
                "rate_limit.exceeded",
                context.TraceIdentifier),
            GoAiProtocol.CreateJsonOptions(),
            context.RequestAborted).ConfigureAwait(false);
    }

    private static RateLimitPolicy ResolvePolicy(HttpRequest request)
    {
        var path = request.Path;
        if (path.StartsWithSegments("/v1/audio/speech")
            || path.StartsWithSegments("/v1/audio/live-captions"))
        {
            return new("speech", SpeechRequestsPerMinute);
        }

        if (path.StartsWithSegments("/v1/artifacts"))
        {
            return new("artifact", ArtifactRequestsPerMinute);
        }

        if (path.Equals("/v1/capabilities")
            || path.Equals("/v1/models/status")
            || path.Equals("/v1/gpu/status")
            || path.Equals("/v1/services/status"))
        {
            return new("status", StatusRequestsPerMinute);
        }

        // Creating a run remains part of the normal request budget. Polling,
        // SSE reconnects, cancellation and idempotent client-tool results use a
        // separate control window so health/status traffic cannot interrupt an
        // already accepted agent run.
        if ((path.Value ?? string.Empty).StartsWith("/v1/runs/", StringComparison.OrdinalIgnoreCase))
        {
            return new("run-control", RunControlRequestsPerMinute);
        }

        return new("general", GeneralRequestsPerMinute);
    }

    private void Cleanup(DateTimeOffset now)
    {
        var nowTicks = now.UtcTicks;
        var previous = Interlocked.Read(ref _lastCleanupTicks);
        if (nowTicks - previous < TimeSpan.FromMinutes(5).Ticks
            || Interlocked.CompareExchange(ref _lastCleanupTicks, nowTicks, previous) != previous)
        {
            return;
        }

        foreach (var pair in _windows)
        {
            if (now - pair.Value.StartedAt > TimeSpan.FromMinutes(5))
            {
                _ = _windows.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed class Window
    {
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

        public int Count { get; set; }
    }

    private sealed record RateLimitPolicy(string Name, int RequestsPerMinute);
}
