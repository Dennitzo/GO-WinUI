using GoAi.Contracts;
using GoAi.Server.Core.Runtime;
using Microsoft.AspNetCore.Http;
using System.Net;
using System.Text.Json;

namespace GoAi.Server.Core.Gateway;

public sealed class ProblemDetailsMiddleware
{
    private readonly RequestDelegate _next;

    public ProblemDetailsMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ServerRuntimeState runtime)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client disconnected; no response can be delivered safely.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }

            var (status, code, title, detail) = MapException(exception);
            runtime.WriteLog("Error", code, $"API-Anfrage fehlgeschlagen ({exception.GetType().Name}, HTTP {status}).");
            context.Response.Clear();
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(
                new GoAiProblem(
                    $"https://go-ai.local/problems/{code}",
                    title,
                    status,
                    detail,
                    code,
                    context.TraceIdentifier),
                GoAiProtocol.CreateJsonOptions(),
                context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static (int Status, string Code, string Title, string Detail) MapException(Exception exception) => exception switch
    {
        JsonException => ((int)HttpStatusCode.BadRequest, "request.invalid_json", "Ungültige Anfrage", "Die JSON-Anfrage entspricht nicht dem GO-AI-Protokoll."),
        ArgumentException => ((int)HttpStatusCode.BadRequest, "request.invalid_argument", "Ungültige Anfrage", exception.Message),
        KeyNotFoundException => ((int)HttpStatusCode.NotFound, "resource.not_found", "Nicht gefunden", exception.Message),
        InvalidDataException => (422, "upload.integrity_failed", "Integritätsprüfung fehlgeschlagen", exception.Message),
        OperationCanceledException => ((int)HttpStatusCode.Conflict, "operation.cancelled", "Vorgang abgebrochen", "Die laufende Operation wurde abgebrochen."),
        InvalidOperationException => ((int)HttpStatusCode.Conflict, "operation.invalid_state", "Vorgang nicht möglich", exception.Message),
        HttpRequestException => ((int)HttpStatusCode.BadGateway, "upstream.failed", "Externer Dienst nicht erreichbar", "Ein interner AI-Dienst konnte die Anfrage nicht ausführen."),
        _ => ((int)HttpStatusCode.InternalServerError, "server.unhandled", "Interner Serverfehler", "Die Anfrage konnte nicht abgeschlossen werden."),
    };
}
