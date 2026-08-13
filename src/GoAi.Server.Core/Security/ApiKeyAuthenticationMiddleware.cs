using GoAi.Contracts;
using Microsoft.AspNetCore.Http;
using System.Net;

namespace GoAi.Server.Core.Security;

public sealed class ApiKeyAuthenticationMiddleware
{
    private static readonly PathString[] AnonymousPaths =
    [
        new("/v1/health/live"),
        new("/v1/health/ready"),
    ];
    private readonly RequestDelegate _next;

    public ApiKeyAuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ApiKeyStore keyStore)
    {
        if (AnonymousPaths.Any(path => context.Request.Path.Equals(path)))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var presented = context.Request.Headers[GoAiHeaders.ApiKey].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(presented)
            && context.Request.Headers.Authorization.FirstOrDefault() is { } authorization
            && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            presented = authorization[7..].Trim();
        }

        if (!await keyStore.ValidateAsync(presented, context.RequestAborted).ConfigureAwait(false))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(
                new GoAiProblem(
                    "https://go-ai.local/problems/authentication",
                    "Authentifizierung fehlgeschlagen",
                    (int)HttpStatusCode.Unauthorized,
                    "Ein gültiger GO-AI-API-Schlüssel ist erforderlich.",
                    "authentication.invalid_api_key",
                    context.TraceIdentifier),
                GoAiProtocol.CreateJsonOptions(),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}
