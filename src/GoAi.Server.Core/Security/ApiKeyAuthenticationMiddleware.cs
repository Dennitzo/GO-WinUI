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

    public async Task InvokeAsync(
        HttpContext context,
        ApiKeyStore keyStore,
        DpapiSecretStore secretStore)
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

        var authenticated = await keyStore.ValidateAsync(presented, context.RequestAborted).ConfigureAwait(false)
            || await secretStore.ValidateLmStudioTokenAsync(presented, context.RequestAborted).ConfigureAwait(false);
        if (!authenticated)
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(
                new GoAiProblem(
                    "https://go-ai.local/problems/authentication",
                    "Authentifizierung fehlgeschlagen",
                    (int)HttpStatusCode.Unauthorized,
                    "Der in GO AI Server gespeicherte LM-Studio-API-Schlüssel ist erforderlich.",
                    "authentication.invalid_api_key",
                    context.TraceIdentifier),
                GoAiProtocol.CreateJsonOptions(),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}
