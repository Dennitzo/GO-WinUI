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
    public const string DocumentsList = "documents.list";
    public const string DocumentsSearch = "documents.search";
    public const string DocumentsReadPages = "documents.readPages";
    public const string WorkspaceMap = "workspace.map";
    public const string FileSystemList = "fs.list";
    public const string FileSystemStat = "fs.stat";
    public const string FileSystemFindFiles = "fs.findFiles";
    public const string FileSystemReadText = "fs.readText";
    public const string FileSystemReadMany = "fs.readMany";
    public const string FileSystemSearch = "fs.search";
    public const string FileSystemWriteText = "fs.writeText";
    public const string FileSystemReplaceText = "fs.replaceText";
    public const string FileSystemMove = "fs.move";
    public const string FileSystemProposePatch = "fs.proposePatch";
    public const string FileSystemProposeCreate = "fs.proposeCreate";
    public const string FileSystemProposeDelete = "fs.proposeDelete";
    public const string ProcessRunPreset = "process.runPreset";
    public const string ProcessRun = "process.run";
    public const string LeanProof = "proof.lean";
    public const string BricsCadGeometryQuery = "bricscad.geometryQuery";
    public const string BricsCadMeasure = "bricscad.measure";
    public const string BricsCadMove = "bricscad.move";
    public const string BricsCadAction = "bricscad.action";
}
