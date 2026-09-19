using GoAi.Contracts;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;

namespace GoAi.Server.Core.Runs;

public sealed partial class RunProcessor
{
    private static string ExtractOriginalTask(RunRequest request) => string.Join("\n",
        request.Messages.Last(message => message.Role == "user").Content.Where(part => part.Type == "text").Select(part => part.Text));

    private Task<RunEvent> PublishCodingMetricsAsync(string runId, long round, string phase, LmChatResult response,
        string? effort, double? queueMilliseconds, CancellationToken cancellationToken) =>
        _repository.AppendEventAsync(runId, RunEventTypes.CodingMetrics, new CodingTurnMetricsEvent(round, phase,
            response.Metrics ?? new ModelTurnMetrics(),
            effort, queueMilliseconds), cancellationToken);

    internal static LmToolCall WithOperationIdentity(string runId, string scope, long round, int ordinal, LmToolCall call) =>
        call with { Id = CreateServerToolOperationId(runId, scope, round, ordinal, call.Id) };

    internal static IReadOnlyList<LmChatMessage> WithWorkingState(IReadOnlyList<LmChatMessage> messages,
        CodingWorkingState? state, bool alreadyIncluded = false) =>
        state is null || alreadyIncluded ? messages : [.. messages, CodingEvidenceContext.Build(state)];

    private static void ApplyCodingBudgetInstruction(List<LmChatMessage> messages, CodingRunBudget budget,
        long rounds, long tools, bool preservePrefix)
    {
        if (!preservePrefix)
        {
            budget.ApplyInstruction(messages, rounds, tools);
            return;
        }
        if (budget.ModelRounds == 0 && budget.ToolCalls == 0) return;
        var instruction = budget.Instruction(rounds, tools);
        if (messages.LastOrDefault(message => message.Role == "system"
            && message.Content?.StartsWith(CodingRunBudget.PromptMarker, StringComparison.Ordinal) == true)?.Content != instruction)
            messages.Add(new LmChatMessage("system", instruction));
    }

    internal static bool HasUnresolvedCodingErrors(CodingWorkingState? state) => state is not null
        && state.Failures.Any(failure => failure.Count > 0 && failure.EnvironmentRevision == state.EnvironmentRevision);

}
