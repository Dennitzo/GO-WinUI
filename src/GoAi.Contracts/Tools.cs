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
    public const string DocumentRead = "document.read";
    public const string DocumentCreate = "document.create";
    public const string DocumentsList = "documents.list";
    public const string DocumentsSearch = "documents.search";
    public const string DocumentsReadPages = "documents.readPages";
    public const string BricsCadGeometryQuery = "bricscad.geometryQuery";
    public const string BricsCadMeasure = "bricscad.measure";
    public const string BricsCadMove = "bricscad.move";
    public const string BricsCadAction = "bricscad.action";
}
