using System.Text.Json;

namespace GoAi.Contracts;

public enum ToolRiskClass
{
    ReadOnly,
    LocalMutation,
    Process,
    CadMutation,
}

public sealed record ToolProposal(
    string ProposalId,
    string RunId,
    string Name,
    JsonElement Arguments,
    ToolRiskClass RiskClass,
    string Summary,
    DateTimeOffset ExpiresAt);

public sealed record ClientToolResult(
    string ProposalId,
    string Status,
    JsonElement Result,
    string? ErrorCode = null,
    string? Message = null);

public sealed record ToolDescriptor(
    string Name,
    string Description,
    ToolRiskClass RiskClass,
    JsonElement InputSchema,
    int TimeoutSeconds,
    int MaximumOutputBytes);

public static class ClientToolNames
{
    public const string CodingList = "coding.list";
    public const string CodingSearch = "coding.search";
    public const string CodingRead = "coding.read";
    public const string CodingWrite = "coding.write";
    public const string CodingEdit = "coding.edit";
    public const string CodingCommand = "coding.command";
    public const string CodingGitDiff = "coding.gitDiff";
    public const string CodingUndo = "coding.undo";
    public const string CodingSearchHistory = "coding.searchHistory";
    public const string CodingSearchKnowledge = "coding.searchKnowledge";
    public const string CodingRenderHtml = "coding.renderHtml";
    public const string CodingReadOutput = "coding.readOutput";
    public const string CodingSearchRunEvidence = "coding.searchRunEvidence";
    public const string DocumentRead = "document.read";
    public const string DocumentCreate = "document.create";
    public const string DocumentsList = "documents.list";
    public const string DocumentsSearch = "documents.search";
    public const string DocumentsReadPages = "documents.readPages";
}
