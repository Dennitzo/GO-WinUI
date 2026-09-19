using GoAi.Contracts;
using GoWinUI.Core.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoWinUI.App.Services;

public sealed partial class GoAiAssistantService
{
    public async Task<RunSteeringAccepted> SteerAsync(Guid sessionId, string prompt, string inputId,
        string? expectedRunId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputId);
        if (prompt.Length > 100_000 || inputId.Length > 128) throw new ArgumentException("Die Umlenkung ist zu lang.");
        if (!IsRunning || ActiveSessionId != sessionId)
        {
            // A lost HTTP acknowledgement can be retried after completion. The
            // gateway only returns an existing idempotent receipt for a terminal
            // run; an unknown input is rejected, never turned into a new run.
            var previous = expectedRunId is null ? null
                : await runs.GetByServerRunIdAsync(expectedRunId, cancellationToken).ConfigureAwait(false);
            if (previous?.SessionId != sessionId || previous.State is not ("completed" or "failed" or "cancelled" or "interrupted"))
                throw new InvalidOperationException("In dieser Sitzung läuft kein Auftrag zum Umlenken.");
            return await SubmitAsync(expectedRunId!).ConfigureAwait(false);
        }
        var activeAttempt = _activeCancellation;
        // A prompt may arrive during local preparation, before POST /runs returns.
        // Await only that identity; never cancel, acquire the send gate or start a run.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (ActiveRunId is null && IsRunning && ActiveSessionId == sessionId && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        var runId = ActiveRunId;
        if (!IsRunning || ActiveSessionId != sessionId || string.IsNullOrWhiteSpace(runId)
            || !ReferenceEquals(activeAttempt, _activeCancellation))
            throw new InvalidOperationException("Der Auftrag ist noch nicht bereit oder bereits beendet. Die Eingabe bleibt erhalten.");
        if (expectedRunId is not null && !string.Equals(runId, expectedRunId, StringComparison.Ordinal))
            throw new InvalidOperationException("Der laufende Auftrag hat sich geändert. Die Eingabe wurde nicht an einen anderen Lauf gesendet.");
        return await SubmitAsync(runId).ConfigureAwait(false);

        async Task<RunSteeringAccepted> SubmitAsync(string targetRunId)
        {
            using var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
            var capabilities = await client.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (!capabilities.SupportsRunSteering)
                throw new InvalidOperationException("Der AI-Server unterstützt Umlenken noch nicht. Aktualisiere den Gateway.");
            var receipt = await client.SteerRunAsync(targetRunId, new(sessionId.ToString("D"), inputId, prompt.Trim()), cancellationToken).ConfigureAwait(false);
            await chats.ClearDraftIfMatchesAsync(sessionId, prompt, CancellationToken.None).ConfigureAwait(false);
            return receipt;
        }
    }

    // Steering lives inside the same durable run message so in-flight tool receipts
    // retain their owner. For model context it expands to ordinary chronological
    // assistant/user messages, exactly as the gateway's session checkpoint does.
    internal static IReadOnlyList<ChatMessage> ExpandSteeringHistory(IReadOnlyList<ChatMessage> source)
    {
        var expanded = new List<ChatMessage>();
        foreach (var message in source)
        {
            var inputs = (message.ToolSteps ?? []).Where(step => step.Tool == "assistant.steering")
                .OrderBy(step => step.ContentOffset ?? message.Content.Length)
                .ThenBy(SteeringSequence).ToArray();
            if (message.Role != ChatRole.Assistant || inputs.Length == 0) { expanded.Add(message); continue; }
            var offset = 0;
            var ordinal = 0;
            foreach (var input in inputs)
            {
                var end = Math.Clamp(input.ContentOffset ?? message.Content.Length, offset, message.Content.Length);
                if (end > offset)
                    expanded.Add(Segment(message.Content[offset..end], ChatRole.Assistant, "before-" + input.Id));
                if (!string.IsNullOrWhiteSpace(input.Detail))
                    expanded.Add(Segment(input.Detail, ChatRole.User, input.Id));
                offset = end;
            }
            expanded.Add(message with { Content = message.Content[offset..],
                ToolSteps = message.ToolSteps?.Where(step => step.Tool != "assistant.steering").ToArray(),
                CreatedAt = message.CreatedAt.AddTicks(ordinal) });

            ChatMessage Segment(string text, ChatRole role, string suffix)
            {
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(message.Id.ToString("D") + "/" + suffix));
                return message with { Id = new Guid(hash.AsSpan(0, 16)), Content = text, Role = role,
                    Status = role == ChatRole.User ? MessageStatus.Completed : message.Status,
                    ToolSteps = [], ContextSummary = null, ToolExecution = null,
                    CreatedAt = message.CreatedAt.AddTicks(ordinal++) };
            }
        }
        return expanded;
    }

    private static long SteeringSequence(AssistantToolStep step)
    {
        try
        {
            using var json = JsonDocument.Parse(step.InputJson ?? "{}");
            return json.RootElement.TryGetProperty("sequence", out var sequence) ? sequence.GetInt64() : 0;
        }
        catch (JsonException) { return 0; }
    }
}
