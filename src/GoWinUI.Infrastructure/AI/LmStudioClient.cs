using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoWinUI.Infrastructure.AI;

public sealed partial class LmStudioClient(
    HttpClient httpClient,
    ISettingsStore settingsStore,
    ILogger<LmStudioClient>? suppliedLogger = null) : ILmStudioClient
{
    private static readonly TimeSpan ModelDiscoveryTimeout = TimeSpan.FromSeconds(8);
    private readonly ILogger<LmStudioClient> _logger = suppliedLogger ?? NullLogger<LmStudioClient>.Instance;

    public async Task<IReadOnlyList<LmModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ModelDiscoveryTimeout);
        var requestToken = timeout.Token;
        var settings = await settingsStore.LoadAsync(requestToken).ConfigureAwait(false);
        try
        {
            using var nativeResponse = await httpClient.GetAsync(BuildNativeModelsUri(settings.LmStudioBaseUrl), requestToken).ConfigureAwait(false);
            if (nativeResponse.IsSuccessStatusCode)
            {
                await using var nativeStream = await nativeResponse.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
                using var nativeDocument = await JsonDocument.ParseAsync(nativeStream, cancellationToken: requestToken).ConfigureAwait(false);
                var nativeModels = ParseNativeModels(nativeDocument.RootElement);
                if (nativeModels.Count > 0) return nativeModels;
            }
        }
        catch (JsonException)
        {
            // Ältere LM-Studio-Versionen werden über den OpenAI-kompatiblen Endpoint erkannt.
        }

        using var response = await httpClient.GetAsync(BuildOpenAiUri(settings.LmStudioBaseUrl, "models"), requestToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: requestToken).ConfigureAwait(false);
        return ParseOpenAiModels(document.RootElement);
    }

    private static List<LmModel> ParseOpenAiModels(JsonElement root)
    {
        var result = new List<LmModel>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in data.EnumerateArray())
            {
                if (!model.TryGetProperty("id", out var idElement) || string.IsNullOrWhiteSpace(idElement.GetString())) continue;
                var context = GetPositiveInt(model, ContextKeys);
                result.Add(new(idElement.GetString()!, model.TryGetProperty("name", out var name) ? name.GetString() : null, context));
            }
        }

        return result;
    }

    private static List<LmModel> ParseNativeModels(JsonElement root)
    {
        var result = new List<LmModel>();
        var models = root.TryGetProperty("models", out var modelArray) ? modelArray
            : root.TryGetProperty("data", out var dataArray) ? dataArray : default;
        if (models.ValueKind != JsonValueKind.Array) return result;
        foreach (var model in models.EnumerateArray())
        {
            if (model.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && !string.Equals(type.GetString(), "llm", StringComparison.OrdinalIgnoreCase)) continue;
            if (!model.TryGetProperty("loaded_instances", out var instances) || instances.ValueKind != JsonValueKind.Array || instances.GetArrayLength() == 0) continue;
            var instance = instances[0];
            var id = GetString(model, "key") ?? GetString(model, "id") ?? GetString(instance, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var context = GetPositiveInt(instance, "config", ContextKeys)
                ?? GetPositiveInt(instance, "runtime", ContextKeys)
                ?? GetPositiveInt(instance, ContextKeys)
                ?? GetPositiveInt(model, "config", ContextKeys)
                ?? GetPositiveInt(model, "model_info", ContextKeys)
                ?? GetPositiveInt(model, ContextKeys);
            result.Add(new(id, GetString(model, "display_name") ?? GetString(model, "name"), context));
        }
        return result;
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await ListModelsAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    public async IAsyncEnumerable<LmDelta> StreamAsync(LmChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        using var responsesRequest = CreateRequest(settings.LmStudioBaseUrl, "responses", CreateResponsesPayload(request));
        StreamEndpointStarted(_logger, "responses", request.Model);
        using var responsesResponse = await httpClient.SendAsync(responsesRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (responsesResponse.IsSuccessStatusCode)
        {
            var receivedResponsesPayload = false;
            await using var responsesEvents = ReadStreamAsync(
                responsesResponse,
                isResponsesApi: true,
                cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await responsesEvents.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or IOException or InvalidDataException or JsonException)
                {
                    StreamEndpointFailed(_logger, exception, "responses", receivedResponsesPayload);
                    if (receivedResponsesPayload)
                    {
                        throw;
                    }

                    break;
                }

                if (!hasNext)
                {
                    break;
                }

                var delta = responsesEvents.Current;
                if (HasPayload(delta))
                {
                    receivedResponsesPayload = true;
                    yield return delta;
                }
                else if (delta.IsCompleted)
                {
                    if (receivedResponsesPayload)
                    {
                        yield return delta;
                    }

                    break;
                }
            }

            if (receivedResponsesPayload)
            {
                yield break;
            }

            StreamEndpointFallback(_logger, "responses", "chat/completions");
        }
        else if (!CanFallback(responsesResponse.StatusCode))
        {
            StreamHttpFailure(_logger, "responses", (int)responsesResponse.StatusCode);
            throw await CreateHttpExceptionAsync(responsesResponse, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            StreamEndpointFallback(_logger, "responses", "chat/completions");
        }

        using var chatRequest = CreateRequest(settings.LmStudioBaseUrl, "chat/completions", CreateChatPayload(request));
        StreamEndpointStarted(_logger, "chat/completions", request.Model);
        using var chatResponse = await httpClient.SendAsync(chatRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!chatResponse.IsSuccessStatusCode)
        {
            StreamHttpFailure(_logger, "chat/completions", (int)chatResponse.StatusCode);
            throw await CreateHttpExceptionAsync(chatResponse, cancellationToken).ConfigureAwait(false);
        }

        var receivedChatPayload = false;
        await foreach (var delta in ReadStreamAsync(chatResponse, isResponsesApi: false, cancellationToken).ConfigureAwait(false))
        {
            if (HasPayload(delta))
            {
                receivedChatPayload = true;
                yield return delta;
            }
            else if (delta.IsCompleted)
            {
                if (receivedChatPayload)
                {
                    yield return delta;
                }

                break;
            }
        }

        if (!receivedChatPayload)
        {
            StreamEndpointFailed(_logger, null, "chat/completions", false);
            throw new InvalidDataException("LM Studio returned no usable streaming content from either endpoint.");
        }
    }

    private static bool HasPayload(LmDelta delta) =>
        delta.Text.Length > 0 || !string.IsNullOrEmpty(delta.Reasoning);

    private static async IAsyncEnumerable<LmDelta> ReadStreamAsync(HttpResponseMessage response, bool isResponsesApi, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            if (item.Data == "[DONE]")
            {
                yield return new(string.Empty, IsCompleted: true);
                yield break;
            }

            LmDelta? parsed;
            try
            {
                parsed = isResponsesApi ? ParseResponsesEvent(item.EventType, item.Data) : ParseChatEvent(item.Data);
            }
            catch (JsonException)
            {
                continue;
            }

            if (parsed is not null)
            {
                yield return parsed;
                if (parsed.IsCompleted) yield break;
            }
        }
    }

    private static LmDelta? ParseResponsesEvent(string eventType, string data)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : eventType;
        if (type is "response.failed" or "error")
        {
            var message = root.TryGetProperty("error", out var error)
                ? error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : error.TryGetProperty("message", out var nestedMessage)
                        ? nestedMessage.GetString()
                        : null
                : null;
            throw new InvalidDataException(message ?? "LM Studio reported a Responses API stream failure.");
        }
        if (type is "response.completed" or "response.done") return new(string.Empty, IsCompleted: true);
        if (type is "response.output_text.delta" && root.TryGetProperty("delta", out var delta)) return new(delta.GetString() ?? string.Empty);
        if ((type is "response.reasoning.delta" or "response.reasoning_text.delta") && root.TryGetProperty("delta", out var reasoning))
            return new(string.Empty, reasoning.GetString());
        if (root.TryGetProperty("delta", out var generic) && generic.ValueKind == JsonValueKind.String) return new(generic.GetString() ?? string.Empty);
        return null;
    }

    private static LmDelta? ParseChatEvent(string data)
    {
        using var document = JsonDocument.Parse(data);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return null;
        var choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String && finish.GetString() is not null)
            return new(string.Empty, IsCompleted: true);
        if (!choice.TryGetProperty("delta", out var delta)) return null;
        var content = delta.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String ? contentElement.GetString() ?? string.Empty : string.Empty;
        var reasoning = delta.TryGetProperty("reasoning", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String ? reasonElement.GetString() : null;
        return content.Length > 0 || reasoning is not null ? new(content, reasoning) : null;
    }

    private static HttpRequestMessage CreateRequest(string baseUrl, string relative, object payload) => new(HttpMethod.Post, BuildOpenAiUri(baseUrl, relative))
    {
        Content = JsonContent.Create(payload),
        Headers = { Accept = { new("text/event-stream") } },
    };

    private static object CreateResponsesPayload(LmChatRequest request) => new
    {
        model = request.Model,
        input = request.Messages.Select(static message => new { role = message.Role.ToString().ToLowerInvariant(), content = message.Content }).ToArray(),
        stream = true,
        max_output_tokens = request.MaxOutputTokens,
        reasoning = string.IsNullOrWhiteSpace(request.ReasoningEffort) ? null : new { effort = request.ReasoningEffort },
    };

    private static object CreateChatPayload(LmChatRequest request) => new
    {
        model = request.Model,
        messages = request.Messages.Select(static message => new { role = message.Role.ToString().ToLowerInvariant(), content = message.Content }).ToArray(),
        stream = true,
        max_tokens = request.MaxOutputTokens,
        reasoning_effort = request.ReasoningEffort,
    };

    private static Uri BuildOpenAiUri(string baseUrl, string relative)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Die LM-Studio-Adresse muss eine absolute HTTP- oder HTTPS-Adresse sein.");
        var path = source.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)) path = path[..^7] + "/v1";
        else if (!path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path += "/v1";
        var root = new UriBuilder(source) { Path = path.TrimEnd('/') + "/", Query = string.Empty, Fragment = string.Empty }.Uri;
        return new(root, relative);
    }

    private static Uri BuildNativeModelsUri(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Die LM-Studio-Adresse muss eine absolute HTTP- oder HTTPS-Adresse sein.");
        var path = source.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)) path = path[..^7];
        else if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path = path[..^3];
        return new UriBuilder(source) { Path = path.TrimEnd('/') + "/api/v1/models", Query = string.Empty, Fragment = string.Empty }.Uri;
    }

    private static bool CanFallback(HttpStatusCode status) => status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.BadRequest or HttpStatusCode.NotImplemented;

    private static async Task<HttpRequestException> CreateHttpExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > 4_096) body = body[..4_096];
        return new HttpRequestException($"LM Studio antwortete mit {(int)response.StatusCode} ({response.ReasonPhrase}): {body}", null, response.StatusCode);
    }

    private static readonly string[] ContextKeys = ["context_length", "loaded_context_length", "max_context_length", "n_ctx", "num_ctx", "contextLength", "max_position_embeddings"];

    [LoggerMessage(EventId = 2100, Level = LogLevel.Information, Message = "LM Studio streaming endpoint {Endpoint} started for model {Model}")]
    private static partial void StreamEndpointStarted(ILogger logger, string endpoint, string model);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "LM Studio switched from {SourceEndpoint} to {TargetEndpoint} before receiving content")]
    private static partial void StreamEndpointFallback(ILogger logger, string sourceEndpoint, string targetEndpoint);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Warning, Message = "LM Studio endpoint {Endpoint} failed; content had been received: {ContentReceived}")]
    private static partial void StreamEndpointFailed(ILogger logger, Exception? exception, string endpoint, bool contentReceived);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Warning, Message = "LM Studio endpoint {Endpoint} returned HTTP {StatusCode}")]
    private static partial void StreamHttpFailure(ILogger logger, string endpoint, int statusCode);

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? GetPositiveInt(JsonElement element, string objectName, IReadOnlyList<string> keys) =>
        element.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object ? GetPositiveInt(nested, keys) : null;
    private static int? GetPositiveInt(JsonElement element, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value)) continue;
            if (value.TryGetInt32(out var number) && number > 0) return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out number) && number > 0) return number;
        }
        return null;
    }
}
