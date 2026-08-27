using GoAi.Contracts;
using GoWinUI.BricsCad.Protocol;
using Microsoft.VisualBasic.FileIO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GoWinUI.Core.Contracts;

namespace GoWinUI.App.Services;

public sealed class LocalToolBroker(
    GoAiConnectionService connection,
    SettingsCoordinator settings,
    ToolConfirmationService confirmation,
    IBricsCadBridgeHost bricsCad,
    IDocumentIngestor documents,
    LeanProofService? leanProof = null,
    LocalDocumentToolService? documentTools = null,
    CodingDiffService? codingDiffs = null)
{
    private const int MaximumResultCharacters = 4 * 1024 * 1024;
    private const int MaximumProcessStreamCharacters = 1_900_000;
    private const int DefaultTargetedReadCharacters = 8_000;
    private const int MaximumTargetedReadCharacters = 12_000;
    private static readonly JsonSerializerOptions JsonOptions = GoAiProtocol.CreateJsonOptions();
    private static readonly HashSet<string> ReadOnlyGitCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "blame", "cat-file", "check-attr", "check-ignore", "count-objects", "describe", "diff",
        "for-each-ref", "grep", "log", "ls-files", "ls-tree", "merge-base", "name-rev", "rev-parse",
        "shortlog", "show", "show-ref", "status",
    };
    private static readonly HashSet<string> GeneratedStatusDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lake", ".venv", "venv", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache",
        ".tox", ".nox", "site-packages", "node_modules", "bower_components", ".npm", ".pnpm-store",
        ".next", ".nuxt", ".svelte-kit", ".turbo", ".parcel-cache", "target", "vendor", "bin",
        "obj", "artifacts", "TestResults", "coverage", "AppPackages", "BundleArtifacts",
        "Generated Files", "build", "out", "render_tmp",
    };
    private readonly AsyncLocal<string?> _executionWorkspace = new();
    private readonly AsyncLocal<Guid?> _executionSession = new();
    private readonly LeanProofService _leanProof = leanProof ?? new LeanProofService();
    private readonly LocalDocumentToolService? _documentTools = documentTools;

    public bool IsBricsCadAvailable => bricsCad.IsConnected;

    public string? ActiveWorkspacePath => TryGetWorkspace(null, out var workspace) ? workspace : null;

    public IReadOnlyList<string> GetAvailableCapabilities(string? workspacePath = null)
    {
        var result = new List<string> { "documentIo" };
        if (TryGetWorkspace(workspacePath, out _))
        {
            result.Add("filesystem");
            result.Add("code");
            result.Add("process");
        }
        if (bricsCad.IsConnected)
        {
            result.Add("bricscad");
        }
        return result;
    }

    public Task<ClientToolResult> ExecuteAsync(ToolProposal proposal, CancellationToken cancellationToken = default) =>
        ExecuteAsync(proposal, null, null, cancellationToken);

    public Task<ClientToolResult> ExecuteAsync(
        ToolProposal proposal,
        string? workspacePath,
        Guid? sessionId = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(proposal, workspacePath, sessionId, null, false, null, cancellationToken);

    public async Task<ClientToolResult> ExecuteAsync(
        ToolProposal proposal,
        string? workspacePath,
        Guid? sessionId,
        Guid? assistantMessageId,
        bool codingMode,
        Guid? codingRunId,
        CancellationToken cancellationToken = default)
    {
        var previousWorkspace = _executionWorkspace.Value;
        try
        {
            var attachedDocumentTool = proposal.Name.StartsWith("documents.", StringComparison.Ordinal);
            _executionWorkspace.Value = attachedDocumentTool ? null : ResolveWorkspace(workspacePath);
            _executionSession.Value = sessionId;
            ValidateProposal(proposal);
            if (!await confirmation.ConfirmAsync(proposal, cancellationToken).ConfigureAwait(false))
            {
                return Result(proposal, "rejected", new { rejected = true }, message: "Vom Nutzer abgelehnt.");
            }

            var mutationBefore = codingMode && codingRunId.HasValue && codingDiffs is not null
                ? await CaptureMutationStateAsync(proposal, beforeExecution: true, cancellationToken).ConfigureAwait(false)
                : null;
            var payload = proposal.Name switch
            {
                ClientToolNames.DocumentRead => await RequireDocumentTools().ReadAsync(
                    proposal.Arguments,
                    _executionWorkspace.Value,
                    sessionId ?? throw new InvalidOperationException("Die Dokument-Sitzung fehlt."),
                    cancellationToken).ConfigureAwait(false),
                ClientToolNames.DocumentCreate => await RequireDocumentTools().CreateAsync(
                    proposal.Arguments,
                    _executionWorkspace.Value,
                    sessionId ?? throw new InvalidOperationException("Die Dokument-Sitzung fehlt."),
                    assistantMessageId ?? throw new InvalidOperationException("Die AI-Nachricht für das Dokumentartefakt fehlt."),
                    codingMode,
                    cancellationToken).ConfigureAwait(false),
                ClientToolNames.DocumentsList => await ListDocumentsAsync(cancellationToken).ConfigureAwait(false),
                ClientToolNames.DocumentsSearch => await SearchDocumentsAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.DocumentsReadPages => await ReadDocumentPagesAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemList => await ListAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemStat => await StatAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemFindFiles => await FindFilesAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemReadText => await ReadTextAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemSearch => await SearchAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemWriteText => await WriteTextAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemReplaceText => await ReplaceTextAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemMove => await MoveAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemProposePatch => await ApplyPatchAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemProposeCreate => await CreateFileAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemProposeDelete => await DeleteFileAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.ProcessRunPreset => await RunPresetAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.ProcessRun => await RunArbitraryProcessAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.LeanProof => await _leanProof.ExecuteAsync(Workspace(), proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.BricsCadGeometryQuery or ClientToolNames.BricsCadMeasure
                    or ClientToolNames.BricsCadMove or ClientToolNames.BricsCadAction =>
                    await RunBricsCadAsync(proposal.Name, proposal.Arguments, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Das Clientwerkzeug '{proposal.Name}' ist nicht implementiert."),
            };
            if (mutationBefore is not null && codingRunId.HasValue && codingDiffs is not null)
            {
                var mutationAfter = await CaptureMutationStateAsync(proposal, beforeExecution: false, cancellationToken).ConfigureAwait(false);
                if (mutationAfter is not null)
                {
                    await codingDiffs.RecordMutationAsync(
                        codingRunId.Value,
                        proposal.ProposalId,
                        mutationBefore,
                        mutationAfter,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            return Result(proposal, "completed", payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkspacePathNotFoundException exception)
        {
            return Result(
                proposal,
                "failed",
                exception.Recovery,
                "client.workspace_path_not_found",
                exception.Message);
        }
        catch (WorkspaceMutationRecoveryException exception)
        {
            return Result(
                proposal,
                "failed",
                exception.Recovery,
                exception.ErrorCode,
                exception.Message);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException
            && IsWorkspaceMutation(proposal.Name))
        {
            return Result(
                proposal,
                "failed",
                CreateGenericMutationRecovery(proposal, exception),
                "client.workspace_mutation_failed",
                exception.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Result(
                proposal,
                "failed",
                new { failed = true },
                "client.tool_failed",
                exception.Message);
        }
        finally
        {
            _executionWorkspace.Value = previousWorkspace;
            _executionSession.Value = null;
        }
    }

    private static bool IsWorkspaceMutation(string toolName) => toolName is
        ClientToolNames.DocumentCreate
        or ClientToolNames.FileSystemWriteText
        or ClientToolNames.FileSystemReplaceText
        or ClientToolNames.FileSystemMove
        or ClientToolNames.FileSystemProposePatch
        or ClientToolNames.FileSystemProposeCreate
        or ClientToolNames.FileSystemProposeDelete;

    private async Task<CodingMutationState?> CaptureMutationStateAsync(
        ToolProposal proposal,
        bool beforeExecution,
        CancellationToken cancellationToken)
    {
        string? requestedPath = proposal.Name switch
        {
            ClientToolNames.FileSystemWriteText
                or ClientToolNames.FileSystemReplaceText
                or ClientToolNames.FileSystemProposePatch
                or ClientToolNames.FileSystemProposeCreate
                or ClientToolNames.FileSystemProposeDelete => PropertyString(proposal.Arguments, "path"),
            ClientToolNames.FileSystemMove when beforeExecution => PropertyString(proposal.Arguments, "source"),
            ClientToolNames.FileSystemMove => PropertyString(proposal.Arguments, "destination"),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(requestedPath)) return null;

        var fullPath = ResolvePath(requestedPath, requireExisting: false);
        var relativePath = Relative(fullPath);
        if (!File.Exists(fullPath)) return new(relativePath, false, false, null, 0);

        var info = new FileInfo(fullPath);
        var binary = WorkspaceFileSystemView.IsProbablyBinary(fullPath);
        if (binary || info.Length > MaximumResultCharacters)
        {
            return new(relativePath, true, binary, null, info.Length);
        }
        var text = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return new(relativePath, true, false, text, info.Length);
    }

    private static string? PropertyString(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static object CreateGenericMutationRecovery(ToolProposal proposal, Exception exception)
    {
        var path = proposal.Arguments.ValueKind == JsonValueKind.Object
            && proposal.Arguments.TryGetProperty("path", out var pathValue)
            && pathValue.ValueKind == JsonValueKind.String
                ? pathValue.GetString()
                : null;
        return new
        {
            failed = true,
            reason = "mutation_rejected",
            path,
            tool = proposal.Name,
            diagnostic = exception.Message,
        };
    }

    private static WorkspaceMutationRecoveryException CreateContentConflict(
        string tool,
        string path)
    {
        const string message = "Die Zieldatei wurde zwischenzeitlich geändert; die Mutation wurde nicht ausgeführt. Lies die Datei unmittelbar erneut und bestätige danach die Änderung anhand des aktuellen Inhalts.";
        return new WorkspaceMutationRecoveryException(
            "client.workspace_content_conflict",
            message,
            new
            {
                failed = true,
                reason = "stale_file_content",
                path,
                tool,
            });
    }

    private static WorkspaceMutationRecoveryException CreateReplaceRecovery(
        string errorCode,
        string reason,
        string message,
        string path,
        string requestedOldText,
        int occurrences) => new(
            errorCode,
            message,
            new
            {
                failed = true,
                reason,
                path,
                tool = ClientToolNames.FileSystemReplaceText,
                requestedOldText,
                occurrences,
            });

    internal static void ValidateProposal(ToolProposal proposal, DateTimeOffset? currentTime = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ValidateIdentifier(proposal.ProposalId, "proposalId");
        ValidateIdentifier(proposal.RunId, "runId");
        if (string.IsNullOrWhiteSpace(proposal.Summary)
            || proposal.Summary.Length > 1_000
            || proposal.Summary.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            throw new InvalidDataException("Die Zusammenfassung des Client-Toolvorschlags ist ungültig.");
        }
        if (proposal.ExpiresAt <= (currentTime ?? DateTimeOffset.UtcNow))
        {
            throw new InvalidDataException("Der Client-Toolvorschlag ist abgelaufen.");
        }
        if (proposal.Arguments.ValueKind != JsonValueKind.Object
            || proposal.Arguments.GetRawText().Length > MaximumResultCharacters)
        {
            throw new InvalidDataException("Die Client-Toolargumente sind ungültig oder zu groß.");
        }

        var expectedRisk = proposal.Name switch
        {
            ClientToolNames.DocumentRead
                or ClientToolNames.DocumentsList or ClientToolNames.DocumentsSearch or ClientToolNames.DocumentsReadPages
                or ClientToolNames.FileSystemList or ClientToolNames.FileSystemStat
                or ClientToolNames.FileSystemFindFiles or ClientToolNames.FileSystemReadText
                or ClientToolNames.FileSystemSearch
                or ClientToolNames.BricsCadGeometryQuery or ClientToolNames.BricsCadMeasure => ToolRiskClass.ReadOnly,
            ClientToolNames.DocumentCreate
                or ClientToolNames.FileSystemWriteText or ClientToolNames.FileSystemReplaceText or ClientToolNames.FileSystemMove
                or ClientToolNames.FileSystemProposePatch or ClientToolNames.FileSystemProposeCreate
                or ClientToolNames.FileSystemProposeDelete => ToolRiskClass.LocalMutation,
            ClientToolNames.ProcessRunPreset or ClientToolNames.ProcessRun
                or ClientToolNames.LeanProof => ToolRiskClass.Process,
            ClientToolNames.BricsCadMove or ClientToolNames.BricsCadAction => ToolRiskClass.CadMutation,
            _ => throw new InvalidDataException($"Das Clientwerkzeug '{proposal.Name}' ist nicht freigegeben."),
        };
        if (proposal.RiskClass != expectedRisk)
        {
            throw new InvalidDataException("Die Risikoklasse des Client-Toolvorschlags stimmt nicht mit dem lokalen Vertrag überein.");
        }

        var arguments = proposal.Arguments;
        switch (proposal.Name)
        {
            case ClientToolNames.DocumentRead:
                ValidateProperties(
                    arguments,
                    ["scope", "mode"],
                    ["scope", "mode", "reference", "query", "startUnit", "characterOffset", "maximumUnits", "maximumCharacters"]);
                var documentScope = ValidateString(arguments, "scope", 1, 16);
                var documentReadMode = ValidateString(arguments, "mode", 1, 16);
                if (documentScope is not ("session" or "workspace")
                    || documentReadMode is not ("list" or "outline" or "read" or "search"))
                {
                    throw new InvalidDataException("Die document.read-Auswahl ist ungültig.");
                }
                ValidateOptionalString(arguments, "reference", 1, 1_024);
                ValidateOptionalString(arguments, "query", 1, 2_000);
                ValidateOptionalInteger(arguments, "startUnit", 1, 1_000_000);
                ValidateOptionalInteger(arguments, "characterOffset", 0, MaximumResultCharacters);
                ValidateOptionalInteger(arguments, "maximumUnits", 1, 30);
                ValidateOptionalInteger(arguments, "maximumCharacters", 1_000, 40_000);
                if (documentReadMode != "list" && !arguments.TryGetProperty("reference", out _))
                {
                    throw new InvalidDataException("document.read benötigt außerhalb des list-Modus eine reference.");
                }
                if (documentReadMode == "search" && !arguments.TryGetProperty("query", out _))
                {
                    throw new InvalidDataException("document.read search benötigt query.");
                }
                break;
            case ClientToolNames.DocumentCreate:
                ValidateProperties(
                    arguments,
                    ["operation", "reference", "format", "sectionId", "content"],
                    ["operation", "reference", "format", "sectionId", "heading", "content", "expectedSha256"]);
                var documentOperation = ValidateString(arguments, "operation", 1, 32);
                if (documentOperation is not ("create" or "appendSection" or "replaceSection"))
                {
                    throw new InvalidDataException("Die document.create-Operation ist ungültig.");
                }
                ValidateString(arguments, "reference", 1, 1_024);
                var documentFormat = ValidateString(arguments, "format", 1, 16);
                if (documentFormat is not ("markdown" or "text" or "docx" or "pdf"))
                {
                    throw new InvalidDataException("Das document.create-Format ist ungültig.");
                }
                ValidateString(arguments, "sectionId", 1, 128);
                ValidateOptionalString(arguments, "heading", 1, 500);
                ValidateString(arguments, "content", 0, 120_000);
                ValidateOptionalString(arguments, "expectedSha256", 64, 64);
                if (documentOperation != "create" && !arguments.TryGetProperty("expectedSha256", out _))
                {
                    throw new InvalidDataException("document.create-Bearbeitungen benötigen expectedSha256.");
                }
                break;
            case ClientToolNames.DocumentsList:
                ValidateProperties(arguments, [], []);
                break;
            case ClientToolNames.DocumentsSearch:
                ValidateProperties(arguments, ["query"], ["query", "maximumCharacters"]);
                ValidateString(arguments, "query", 1, 20_000);
                ValidateOptionalInteger(arguments, "maximumCharacters", 1_000, 200_000);
                break;
            case ClientToolNames.DocumentsReadPages:
                ValidateProperties(arguments, ["documentId", "startPage", "endPage"], ["documentId", "startPage", "endPage"]);
                ValidateString(arguments, "documentId", 36, 36);
                ValidateOptionalInteger(arguments, "startPage", 1, 1_000_000);
                ValidateOptionalInteger(arguments, "endPage", 1, 1_000_000);
                break;
            case ClientToolNames.FileSystemList:
            case ClientToolNames.FileSystemStat:
                ValidateProperties(arguments, ["path"], ["path"]);
                ValidateString(arguments, "path", 0, 1_024);
                break;
            case ClientToolNames.FileSystemProposeDelete:
                ValidateProperties(arguments, ["path", "expectedContent"], ["path", "expectedContent", "expectedContentMode"]);
                ValidateString(arguments, "path", 1, 1_024);
                ValidateString(arguments, "expectedContent", 0, MaximumResultCharacters);
                ValidateOptionalEnum(arguments, "expectedContentMode", ["complete", "fragment"]);
                break;
            case ClientToolNames.FileSystemReadText:
                ValidateProperties(arguments, ["path"], ["path", "startLine", "endLine", "maximumCharacters", "matchText"]);
                ValidateString(arguments, "path", 1, 1_024);
                ValidateOptionalInteger(arguments, "startLine", 1, 10_000_000);
                ValidateOptionalInteger(arguments, "endLine", 1, 10_000_000);
                ValidateOptionalInteger(arguments, "maximumCharacters", 1_024, MaximumTargetedReadCharacters);
                ValidateOptionalString(arguments, "matchText", 1, 4_096);
                break;
            case ClientToolNames.FileSystemFindFiles:
                ValidateProperties(arguments, ["patterns"], ["path", "patterns", "maximumResults"]);
                ValidateOptionalString(arguments, "path", 0, 1_024);
                ValidateStringArray(arguments, "patterns", 1, 64, 256);
                ValidateOptionalInteger(arguments, "maximumResults", 1, 5_000);
                break;
            case ClientToolNames.FileSystemSearch:
                ValidateProperties(
                    arguments,
                    ["path"],
                    ["path", "query", "queries", "matchMode", "includeGlobs", "excludeGlobs", "maximumResults", "contextLines"]);
                ValidateString(arguments, "path", 0, 1_024);
                var hasQuery = arguments.TryGetProperty("query", out _);
                var hasQueries = arguments.TryGetProperty("queries", out _);
                if (hasQuery == hasQueries)
                {
                    throw new InvalidDataException("fs.search benötigt entweder 'query' oder 'queries'.");
                }
                if (hasQuery) ValidateString(arguments, "query", 1, 1_024);
                if (hasQueries) ValidateStringArray(arguments, "queries", 1, 64, 1_024);
                ValidateOptionalEnum(arguments, "matchMode", ["literal", "regex"]);
                ValidateOptionalStringArray(arguments, "includeGlobs", 64, 256);
                ValidateOptionalStringArray(arguments, "excludeGlobs", 64, 256);
                ValidateOptionalInteger(arguments, "maximumResults", 1, 100);
                ValidateOptionalInteger(arguments, "contextLines", 0, 5);
                break;
            case ClientToolNames.FileSystemWriteText:
                ValidateProperties(arguments, ["path", "content", "expectedContent"], ["path", "content", "expectedContent", "expectedContentMode"]);
                ValidateString(arguments, "path", 1, 1_024);
                ValidateString(arguments, "content", 0, MaximumResultCharacters);
                ValidateString(arguments, "expectedContent", 0, MaximumResultCharacters);
                ValidateOptionalEnum(arguments, "expectedContentMode", ["complete", "fragment"]);
                break;
            case ClientToolNames.FileSystemReplaceText:
                ValidateProperties(arguments, ["path", "oldText", "newText", "expectedContent"], ["path", "oldText", "newText", "expectedContent", "expectedContentMode"]);
                ValidateString(arguments, "path", 1, 1_024);
                ValidateString(arguments, "oldText", 1, MaximumResultCharacters / 2);
                ValidateString(arguments, "newText", 0, MaximumResultCharacters / 2);
                ValidateString(arguments, "expectedContent", 0, MaximumResultCharacters);
                ValidateOptionalEnum(arguments, "expectedContentMode", ["complete", "fragment"]);
                break;
            case ClientToolNames.FileSystemMove:
                ValidateProperties(arguments, ["source", "destination"], ["source", "destination", "overwrite"]);
                ValidateString(arguments, "source", 1, 1_024);
                ValidateString(arguments, "destination", 1, 1_024);
                ValidateOptionalBoolean(arguments, "overwrite");
                break;
            case ClientToolNames.FileSystemProposePatch:
                ValidateProperties(arguments, ["path", "patch", "expectedContent"], ["path", "patch", "expectedContent", "expectedContentMode"]);
                ValidateString(arguments, "path", 1, 1_024);
                ValidateString(arguments, "patch", 1, MaximumResultCharacters);
                ValidateString(arguments, "expectedContent", 0, MaximumResultCharacters);
                ValidateOptionalEnum(arguments, "expectedContentMode", ["complete", "fragment"]);
                break;
            case ClientToolNames.FileSystemProposeCreate:
                ValidateProperties(arguments, ["path", "content"], ["path", "content"]);
                ValidateString(arguments, "path", 1, 1_024);
                ValidateString(arguments, "content", 0, 12_000);
                break;
            case ClientToolNames.ProcessRunPreset:
                ValidateProperties(arguments, ["preset"], ["preset", "workspace", "target"]);
                var preset = ValidateString(arguments, "preset", 1, 64);
                if (preset is not ("git.status" or "git.diff" or "dotnet.build" or "dotnet.test" or "repository.build" or "repository.verify" or "repository.start" or "code.run" or "code.test"))
                {
                    throw new InvalidDataException("Das angeforderte Prozess-Preset ist nicht freigegeben.");
                }
                ValidateOptionalString(arguments, "workspace", 1, 1_024);
                ValidateOptionalString(arguments, "target", 1, 1_024);
                break;
            case ClientToolNames.ProcessRun:
                ValidateProperties(
                    arguments,
                    ["executable", "purpose"],
                    ["executable", "arguments", "workingDirectory", "timeoutSeconds", "purpose", "startMode"]);
                var executable = ValidateString(arguments, "executable", 1, 1_024);
                if (IsLeanExecutable(executable))
                {
                    throw new InvalidDataException("Lean und Lake dürfen ausschließlich über das typisierte Werkzeug proof.lean ausgeführt werden.");
                }
                ValidateOptionalStringArray(arguments, "arguments", 128, 8_192);
                ValidateOptionalString(arguments, "workingDirectory", 1, 1_024);
                ValidateOptionalInteger(arguments, "timeoutSeconds", 1, 3_600);
                ValidateOptionalEnum(arguments, "purpose", ["inspect", "setup", "test", "build", "start"]);
                ValidateOptionalEnum(arguments, "startMode", ["wait", "smoke"]);
                break;
            case ClientToolNames.LeanProof:
                ValidateProperties(
                    arguments,
                    ["operation"],
                    ["operation", "path", "target", "theoremName", "timeoutSeconds"]);
                var leanOperation = ValidateString(arguments, "operation", 1, 16);
                if (leanOperation is not ("status" or "check" or "build" or "axioms" or "verify"))
                {
                    throw new InvalidDataException("Die proof.lean-Operation ist nicht freigegeben.");
                }
                ValidateOptionalString(arguments, "path", 1, 1_024);
                ValidateOptionalString(arguments, "target", 1, 256);
                ValidateOptionalString(arguments, "theoremName", 1, 512);
                ValidateOptionalInteger(arguments, "timeoutSeconds", 1, 1_800);
                if (leanOperation is "check" or "axioms" or "verify"
                    && !arguments.TryGetProperty("path", out _))
                {
                    throw new InvalidDataException($"proof.lean {leanOperation} benötigt 'path'.");
                }
                if (leanOperation is "axioms" or "verify"
                    && !arguments.TryGetProperty("theoremName", out _))
                {
                    throw new InvalidDataException($"proof.lean {leanOperation} benötigt 'theoremName'.");
                }
                break;
            case ClientToolNames.BricsCadGeometryQuery:
            case ClientToolNames.BricsCadMeasure:
            case ClientToolNames.BricsCadMove:
            case ClientToolNames.BricsCadAction:
                ValidateProperties(arguments, ["operation"], ["operation", "arguments"]);
                var operation = ValidateString(arguments, "operation", 1, 128);
                if (arguments.TryGetProperty("arguments", out var cadArguments)
                    && (cadArguments.ValueKind != JsonValueKind.Object
                        || cadArguments.GetRawText().Length > 1_048_576))
                {
                    throw new InvalidDataException("Die BricsCAD-Werkzeugargumente sind ungültig oder zu groß.");
                }
                ValidateCadOperation(proposal.Name, operation);
                break;
        }
    }

    private static bool IsLeanExecutable(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable.Trim());
        return name.Equals("lean", StringComparison.OrdinalIgnoreCase)
            || name.Equals("lake", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 200
            || value.Any(char.IsControl))
        {
            throw new InvalidDataException($"Der Client-Toolbezeichner '{name}' ist ungültig.");
        }
    }

    private static void ValidateProperties(JsonElement arguments, IReadOnlyList<string> required, IReadOnlyList<string> allowed)
    {
        foreach (var property in arguments.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"Das Werkzeugargument '{property.Name}' ist nicht erlaubt.");
            }
        }
        foreach (var name in required)
        {
            if (!arguments.TryGetProperty(name, out _))
            {
                throw new InvalidDataException($"Das Werkzeugargument '{name}' fehlt.");
            }
        }
    }

    private static string ValidateString(JsonElement arguments, string name, int minimum, int maximum)
    {
        if (!arguments.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text
            || text.Length < minimum
            || text.Length > maximum)
        {
            throw new InvalidDataException($"Das Werkzeugargument '{name}' verletzt die lokalen Textgrenzen.");
        }
        return text;
    }

    private static void ValidateOptionalString(JsonElement arguments, string name, int minimum, int maximum)
    {
        if (arguments.TryGetProperty(name, out _))
        {
            _ = ValidateString(arguments, name, minimum, maximum);
        }
    }

    private static void ValidateOptionalInteger(JsonElement arguments, string name, int minimum, int maximum)
    {
        if (arguments.TryGetProperty(name, out var value)
            && (!value.TryGetInt32(out var number) || number < minimum || number > maximum))
        {
            throw new InvalidDataException($"Das Werkzeugargument '{name}' verletzt die lokalen Zahlengrenzen.");
        }
    }

    private static void ValidateOptionalBoolean(JsonElement arguments, string name)
    {
        if (arguments.TryGetProperty(name, out var value)
            && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"Das Werkzeugargument '{name}' muss ein Boolean sein.");
        }
    }

    private static void ValidateOptionalEnum(JsonElement arguments, string name, IReadOnlyList<string> allowed)
    {
        if (arguments.TryGetProperty(name, out _))
        {
            var value = ValidateString(arguments, name, 1, 64);
            if (!allowed.Contains(value, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"Das Werkzeugargument '{name}' enthält einen unbekannten Wert.");
            }
        }
    }

    private static void ValidateOptionalStringArray(
        JsonElement arguments,
        string name,
        int maximumItems,
        int maximumItemLength)
    {
        if (arguments.TryGetProperty(name, out _))
        {
            ValidateStringArray(arguments, name, 0, maximumItems, maximumItemLength);
        }
    }

    private static void ValidateStringArray(
        JsonElement arguments,
        string name,
        int minimumItems,
        int maximumItems,
        int maximumItemLength)
    {
        if (!arguments.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() < minimumItems
            || value.GetArrayLength() > maximumItems
            || value.EnumerateArray().Any(item =>
                item.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(item.GetString())
                || item.GetString()!.Length > maximumItemLength))
        {
            throw new InvalidDataException($"Das Werkzeugargument '{name}' verletzt die lokalen Listengrenzen.");
        }
    }

    private Task<object> ListAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(WorkspaceRootPath(arguments, "path"), requireExisting: true);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException("Der angeforderte Pfad ist kein Ordner.");
        }
        var explicitlyListingGeneratedPath = WorkspaceFileSystemView.IsAutomaticallyIgnoredPath(Relative(path), isDirectory: true);
        var candidates = Directory.EnumerateFileSystemEntries(path)
            .Where(item => explicitlyListingGeneratedPath
                || !WorkspaceFileSystemView.IsAutomaticallyIgnoredPath(Relative(item), Directory.Exists(item)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(501)
            .ToArray();
        var entries = candidates
            .Take(500)
            .Select(item =>
            {
                var info = new FileInfo(item);
                var isDirectory = Directory.Exists(item);
                return new
                {
                    name = Path.GetFileName(item),
                    path = Relative(item),
                    type = isDirectory ? "directory" : "file",
                    length = isDirectory ? (long?)null : info.Length,
                    updatedAt = isDirectory ? Directory.GetLastWriteTimeUtc(item) : info.LastWriteTimeUtc,
                };
            })
            .ToArray();
        return Task.FromResult<object>(new { path = Relative(path), entries, truncated = candidates.Length > 500 });
    }

    private Task<object> StatAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(WorkspaceRootPath(arguments, "path"), requireExisting: true);
        var isDirectory = Directory.Exists(path);
        var info = isDirectory ? null : new FileInfo(path);
        return Task.FromResult<object>(new
        {
            path = Relative(path),
            type = isDirectory ? "directory" : "file",
            length = info?.Length,
            createdAt = isDirectory ? Directory.GetCreationTimeUtc(path) : info!.CreationTimeUtc,
            updatedAt = isDirectory ? Directory.GetLastWriteTimeUtc(path) : info!.LastWriteTimeUtc,
            readOnly = !isDirectory && info!.IsReadOnly,
        });
    }

    private Task<object> FindFilesAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = arguments.TryGetProperty("path", out var rootValue) && rootValue.ValueKind == JsonValueKind.String
            ? string.IsNullOrWhiteSpace(rootValue.GetString()) ? "." : rootValue.GetString()!
            : ".";
        var absoluteRoot = ResolvePath(root, requireExisting: true);
        if (!Directory.Exists(absoluteRoot))
        {
            throw new DirectoryNotFoundException("Der Suchpfad ist kein Ordner.");
        }
        var patterns = ReadStringArray(arguments, "patterns");
        var maximum = OptionalInteger(arguments, "maximumResults") ?? 500;
        var rootPrefix = Relative(absoluteRoot).TrimEnd('/');
        var matches = WorkspaceFileSystemView.EnumerateEntries(Workspace(), 20_000)
            .Where(static entry => !entry.IsDirectory)
            .Where(entry => rootPrefix is "." or ""
                || entry.Path.StartsWith(rootPrefix + "/", StringComparison.OrdinalIgnoreCase))
            .Where(entry => patterns.Any(pattern => WorkspaceFileSystemView.MatchesGlob(entry.Path, pattern)))
            .Take(maximum)
            .Select(static entry => new
            {
                path = entry.Path,
                entry.Length,
                entry.IsBinary,
            })
            .ToArray();
        return Task.FromResult<object>(Bounded(new
        {
            path = NormalizeWorkspaceAlias(root) ?? ".",
            patterns,
            matches,
            truncated = matches.Length >= maximum,
        }));
    }

    private async Task<object> ReadTextAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var requestedPath = RequiredString(arguments, "path");
        var path = ResolvePath(requestedPath, requireExisting: false);
        if (!File.Exists(path))
        {
            var pathIsDirectory = Directory.Exists(path);
            var liveEntries = WorkspaceFileSystemView.EnumerateEntries(Workspace(), 20_000).ToArray();
            var suggestedPaths = FindWorkspacePathSuggestions(requestedPath, liveEntries)
                .Where(candidate => File.Exists(ResolvePath(candidate, requireExisting: false)))
                .ToArray();
            throw new WorkspacePathNotFoundException(
                pathIsDirectory
                    ? "Der angeforderte fs.readText-Pfad ist ein Ordner. Verwende fs.list für diesen Ordner und lies danach eine tatsächlich vorhandene Textdatei."
                    : "Der angeforderte Workspace-Pfad wurde nicht gefunden. Verwende ausschließlich einen tatsächlich vorhandenen relativen Pfad aus suggestedPaths oder ermittle ihn mit fs.findFiles beziehungsweise fs.list.",
                new
                {
                    failed = true,
                    reason = pathIsDirectory ? "path_is_directory" : "path_not_found",
                    requestedPath = NormalizeWorkspaceAlias(requestedPath)?.Replace('\\', '/') ?? requestedPath.Replace('\\', '/'),
                    suggestedPaths,
                    recoveryTool = pathIsDirectory
                        ? ClientToolNames.FileSystemList
                        : suggestedPaths.Length == 0 ? ClientToolNames.FileSystemFindFiles : null,
                });
        }
        if (WorkspaceFileSystemView.IsProbablyBinary(path))
        {
            throw new InvalidDataException("Die angeforderte Datei ist binär und kann nicht als Quelltext gelesen werden.");
        }
        var hasStartLine = arguments.TryGetProperty("startLine", out _);
        var hasEndLine = arguments.TryGetProperty("endLine", out _);
        var hasMatchText = arguments.TryGetProperty("matchText", out _);
        var startLine = OptionalInteger(arguments, "startLine") ?? 1;
        var endLine = OptionalInteger(arguments, "endLine");
        var maximumCharacters = OptionalInteger(arguments, "maximumCharacters") ?? DefaultTargetedReadCharacters;
        var fileLength = new FileInfo(path).Length;
        var targeted = hasStartLine || hasEndLine || hasMatchText;
        if (!targeted && fileLength > maximumCharacters)
        {
            return Bounded(new
            {
                path = Relative(path),
                length = fileLength,
                contentReturned = false,
                completeFile = false,
                requiresTargetedRead = true,
                state = "search_required",
                message = "Die Datei ist für einen ungezielten Kontextabruf zu groß. Suche zuerst mit fs.search nach einer Funktion, einem Symbol oder einer Textphrase und lies danach nur den gelieferten Zeilenbereich.",
                recommendedTool = ClientToolNames.FileSystemSearch,
                maximumCharacters,
            });
        }
        if (!targeted && fileLength <= maximumCharacters)
        {
            var source = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (source.Length <= maximumCharacters)
            {
                return Bounded(new TextReadResult(
                    Relative(path), source, fileLength, 1,
                    source.Count(static character => character == '\n') + 1,
                    Truncated: false,
                    CompleteFile: true));
            }
        }
        if (arguments.TryGetProperty("matchText", out var matchValue)
            && matchValue.ValueKind == JsonValueKind.String
            && matchValue.GetString() is { Length: > 0 } matchText)
        {
            var source = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var occurrences = CountOrdinalOccurrences(source, matchText);
            if (occurrences != 1)
            {
                throw new InvalidDataException($"Der unmittelbar zu lesende Textblock kommt {occurrences} Mal in der Datei vor; die Mutation wurde nicht vorbereitet.");
            }
            var limit = maximumCharacters;
            if (source.Length <= limit)
            {
                return Bounded(new TextReadResult(
                    Relative(path), source, new FileInfo(path).Length, 1,
                    source.Count(static character => character == '\n') + 1,
                    Truncated: false,
                    CompleteFile: true));
            }
            var matchOffset = source.IndexOf(matchText, StringComparison.Ordinal);
            var availableContext = Math.Max(0, limit - matchText.Length);
            var fragmentStart = Math.Max(0, matchOffset - availableContext / 2);
            var fragmentEnd = Math.Min(source.Length, fragmentStart + limit);
            fragmentStart = Math.Max(0, fragmentEnd - limit);
            var fragment = source[fragmentStart..fragmentEnd];
            var firstLine = source.AsSpan(0, fragmentStart).Count('\n') + 1;
            var lastLine = firstLine + fragment.AsSpan().Count('\n');
            return Bounded(new TextReadResult(
                Relative(path), fragment, new FileInfo(path).Length,
                firstLine, lastLine,
                Truncated: true,
                CompleteFile: false));
        }
        return Bounded(await ReadTextRangeAsync(
            path,
            startLine,
            endLine,
            maximumCharacters,
            cancellationToken).ConfigureAwait(false));
    }

    internal static IReadOnlyList<string> FindWorkspacePathSuggestions(
        string requestedPath,
        IReadOnlyList<WorkspacePathEntry> entries,
        int maximumResults = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
        ArgumentNullException.ThrowIfNull(entries);
        maximumResults = Math.Clamp(maximumResults, 1, 32);
        var normalizedRequested = (NormalizeWorkspaceAlias(requestedPath) ?? requestedPath)
            .Replace('\\', '/')
            .TrimStart('/');
        var requestedName = Path.GetFileName(normalizedRequested);
        var requestedStem = Path.GetFileNameWithoutExtension(requestedName);
        var requestedExtension = Path.GetExtension(requestedName);
        var requestedDirectory = NormalizeRelativeDirectory(Path.GetDirectoryName(
            normalizedRequested.Replace('/', Path.DirectorySeparatorChar)));
        var requestedTokens = SplitPathTokens(requestedStem);

        return entries
            .Where(static entry => !entry.IsDirectory && !entry.IsBinary)
            .Select(entry => new
            {
                entry.Path,
                Score = ScoreWorkspacePathCandidate(
                    entry.Path,
                    requestedName,
                    requestedStem,
                    requestedExtension,
                    requestedDirectory,
                    requestedTokens),
            })
            .Where(static candidate => candidate.Score >= 25)
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Take(maximumResults)
            .Select(static candidate => candidate.Path)
            .ToArray();
    }

    private static int ScoreWorkspacePathCandidate(
        string candidatePath,
        string requestedName,
        string requestedStem,
        string requestedExtension,
        string requestedDirectory,
        IReadOnlySet<string> requestedTokens)
    {
        var normalizedCandidate = candidatePath.Replace('\\', '/');
        var candidateName = Path.GetFileName(normalizedCandidate);
        var candidateStem = Path.GetFileNameWithoutExtension(candidateName);
        var candidateExtension = Path.GetExtension(candidateName);
        var candidateDirectory = NormalizeRelativeDirectory(Path.GetDirectoryName(
            normalizedCandidate.Replace('/', Path.DirectorySeparatorChar)));
        var score = 0;
        if (candidateName.Equals(requestedName, StringComparison.OrdinalIgnoreCase))
        {
            score += 160;
        }
        if (candidateStem.Equals(requestedStem, StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }
        if (!string.IsNullOrWhiteSpace(requestedExtension)
            && candidateExtension.Equals(requestedExtension, StringComparison.OrdinalIgnoreCase))
        {
            score += 15;
        }
        if (candidateDirectory.Equals(requestedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
        }
        if (!string.IsNullOrWhiteSpace(requestedStem)
            && (candidateStem.Contains(requestedStem, StringComparison.OrdinalIgnoreCase)
                || requestedStem.Contains(candidateStem, StringComparison.OrdinalIgnoreCase)))
        {
            score += 35;
        }

        var candidateTokens = SplitPathTokens(candidateStem);
        score += Math.Min(45, requestedTokens.Count(candidateTokens.Contains) * 15);
        if (requestedStem.Length > 0 && candidateStem.Length > 0)
        {
            var maximumLength = Math.Max(requestedStem.Length, candidateStem.Length);
            var similarity = 1d - (double)LevenshteinDistance(
                requestedStem.ToLowerInvariant(),
                candidateStem.ToLowerInvariant()) / maximumLength;
            if (similarity >= 0.45d)
            {
                score += (int)Math.Round(similarity * 35d, MidpointRounding.AwayFromZero);
            }
        }
        return score;
    }

    private static HashSet<string> SplitPathTokens(string value) => Regex
        .Split(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
        .Where(static token => token.Length > 1)
        .ToHashSet(StringComparer.Ordinal);

    private static string NormalizeRelativeDirectory(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : value.Replace('\\', '/').Trim('/');

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution = previous[rightIndex - 1]
                    + (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                current[rightIndex] = Math.Min(
                    Math.Min(previous[rightIndex] + 1, current[rightIndex - 1] + 1),
                    substitution);
            }
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    private async Task<object> SearchAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var root = ResolvePath(WorkspaceRootPath(arguments, "path"), requireExisting: true);
        var mode = arguments.TryGetProperty("matchMode", out var modeValue)
            ? modeValue.GetString() ?? "literal"
            : "literal";
        var queries = arguments.TryGetProperty("queries", out _)
            ? ReadStringArray(arguments, "queries")
            : SplitLegacyQueries(RequiredString(arguments, "query"), mode);
        var includeGlobs = ReadOptionalStringArray(arguments, "includeGlobs");
        var excludeGlobs = ReadOptionalStringArray(arguments, "excludeGlobs");
        var maximum = OptionalInteger(arguments, "maximumResults") ?? 20;
        var contextLines = OptionalInteger(arguments, "contextLines") ?? 2;
        var expressions = mode == "regex"
            ? queries.Select(query => new Regex(
                query,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(500))).ToArray()
            : Array.Empty<Regex>();
        var relativeRoot = Relative(root).TrimEnd('/');
        var matches = new List<object>();
        var foundQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var searchedFiles = 0;

        void CollectMatches(string relativePath, string[] lines)
        {
            for (var index = 0; index < lines.Length && matches.Count < maximum; index++)
            {
                for (var queryIndex = 0; queryIndex < queries.Length && matches.Count < maximum; queryIndex++)
                {
                    var matched = mode == "regex"
                        ? expressions[queryIndex].IsMatch(lines[index])
                        : lines[index].Contains(queries[queryIndex], StringComparison.OrdinalIgnoreCase);
                    if (!matched)
                    {
                        continue;
                    }
                    var firstContextLine = Math.Max(0, index - contextLines);
                    var lastContextLine = Math.Min(lines.Length - 1, index + contextLines);
                    foundQueries.Add(queries[queryIndex]);
                    var lineText = LimitSearchSnippet(lines[index].Trim(), 1_000);
                    var context = contextLines == 0
                        ? null
                        : LimitSearchSnippet(
                            string.Join('\n', lines[firstContextLine..(lastContextLine + 1)]),
                            4_000);
                    matches.Add(new
                    {
                        query = queries[queryIndex],
                        path = relativePath,
                        line = index + 1,
                        text = lineText,
                        contextStartLine = firstContextLine + 1,
                        contextEndLine = lastContextLine + 1,
                        context,
                        readRequest = new
                        {
                            path = relativePath,
                            startLine = firstContextLine + 1,
                            endLine = lastContextLine + 1,
                            maximumCharacters = DefaultTargetedReadCharacters,
                        },
                    });
                }
            }
        }

        var candidates = WorkspaceFileSystemView.EnumerateEntries(Workspace(), 20_000)
            .Where(static entry => !entry.IsDirectory && !entry.IsBinary)
            .Where(entry => entry.Length <= WorkspaceFileSystemView.MaximumSearchableFileLength)
            .Where(entry => File.Exists(root)
                ? entry.Path.Equals(relativeRoot, StringComparison.OrdinalIgnoreCase)
                : relativeRoot is "." or ""
                    || entry.Path.StartsWith(relativeRoot + "/", StringComparison.OrdinalIgnoreCase))
            .Where(entry => WorkspaceFileSystemView.MatchesGlobs(entry.Path, includeGlobs, excludeGlobs));
        foreach (var entry in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = ResolvePath(entry.Path, requireExisting: true);
            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                continue;
            }
            searchedFiles++;
            CollectMatches(entry.Path, lines);
            if (matches.Count >= maximum)
            {
                break;
            }
        }
        var missingQueries = queries
            .Where(query => !foundQueries.Contains(query))
            .ToArray();
        var found = matches.Count > 0;
        return Bounded(new
        {
            queries,
            matchMode = mode,
            found,
            state = found ? "matches_found" : "not_present",
            matches,
            missingQueries,
            truncated = matches.Count >= maximum,
            searchedFiles,
            message = found
                ? "Treffer gefunden. Lade nur den benötigten readRequest-Zeilenbereich mit fs.readText und verwende den zurückgegebenen exakten Block für fs.replaceText."
                : "Kein Treffer im vollständig durchsuchten Bestand. Die gesuchte Funktion oder Textphrase existiert dort aktuell nicht. Wiederhole dieselbe Suche nicht; erstelle den Inhalt neu oder lies nur einen passenden Einfügebereich.",
        });
    }

    private static string LimitSearchSnippet(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters] + "…";

    private async Task<object> WriteTextAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var path = ResolvePath(RequiredString(arguments, "path"), requireExisting: false);
        var targetExisted = File.Exists(path);
        var existingContent = targetExisted
            ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
        if (Directory.Exists(path))
        {
            throw new IOException("fs.writeText kann keinen Ordner überschreiben.");
        }
        if (!targetExisted)
        {
            throw new FileNotFoundException("fs.writeText bearbeitet nur zuvor gelesene Bestandsdateien. Verwende fs.proposeCreate für neue Dateien.", path);
        }
        ValidateExpectedContent(path, existingContent!, arguments, ClientToolNames.FileSystemWriteText);
        var content = RequiredString(arguments, "content", allowEmpty: true);
        await ValidateSourceMutationAsync(path, existingContent, content, isFullWrite: existingContent is not null, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".go-ai.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
        return new
        {
            written = true,
            path = Relative(path),
            length = Encoding.UTF8.GetByteCount(content),
        };
    }

    private async Task<object> ReplaceTextAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var path = ResolvePath(RequiredString(arguments, "path"), requireExisting: true);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Das Ziel für fs.replaceText wurde nicht gefunden.", path);
        }
        if (IsProbablyBinary(path))
        {
            throw new InvalidDataException("fs.replaceText kann keine bekannte Binärdatei bearbeiten.");
        }

        var original = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (original.Contains('\0'))
        {
            throw new InvalidDataException("fs.replaceText kann keine Binärdatei bearbeiten.");
        }
        ValidateExpectedContent(path, original, arguments, ClientToolNames.FileSystemReplaceText);

        var oldText = RequiredString(arguments, "oldText");
        var newText = RequiredString(arguments, "newText", allowEmpty: true);
        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            throw new InvalidDataException("fs.replaceText benötigt eine tatsächliche Textänderung.");
        }
        var occurrences = CountOrdinalOccurrences(original, oldText);
        if (occurrences == 0)
        {
            const string message = "Der exakt angegebene oldText wurde nicht gefunden. Lies den aktuellen Dateibereich erneut und verwende dessen unveränderten Wortlaut.";
            throw CreateReplaceRecovery(
                "client.replace_text_not_found",
                "old_text_not_found",
                message,
                Relative(path),
                oldText,
                occurrences: 0);
        }
        if (occurrences != 1)
        {
            var message = $"oldText kommt {occurrences} Mal vor. Lies einen größeren, eindeutig vorkommenden Textblock und bestätige die Änderung erneut.";
            throw CreateReplaceRecovery(
                "client.replace_text_ambiguous",
                "old_text_ambiguous",
                message,
                Relative(path),
                oldText,
                occurrences);
        }

        var firstOccurrence = original.IndexOf(oldText, StringComparison.Ordinal);
        var updated = original.Remove(firstOccurrence, oldText.Length).Insert(firstOccurrence, newText);
        await ValidateSourceMutationAsync(path, original, updated, isFullWrite: false, cancellationToken).ConfigureAwait(false);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".go-ai.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, updated, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }

        return new
        {
            replaced = true,
            path = Relative(path),
            replacements = 1,
            length = Encoding.UTF8.GetByteCount(updated),
        };
    }

    private static int CountOrdinalOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    internal static void ValidateSourceMutation(
        string path,
        string? original,
        string updated,
        bool isFullWrite)
    {
        if (string.Equals(Path.GetExtension(path), ".xaml", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _ = XDocument.Parse(updated, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            }
            catch (Exception exception) when (exception is System.Xml.XmlException or ArgumentException)
            {
                throw new InvalidDataException(
                    $"Die erzeugte XAML-Datei ist nicht wohlgeformt: {exception.Message}",
                    exception);
            }

            if (updated.Contains("<FlyoutBase.AttachedFlyout", StringComparison.Ordinal)
                && original?.Contains("<FlyoutBase.AttachedFlyout", StringComparison.Ordinal) != true)
            {
                throw new InvalidDataException(
                    "FlyoutBase.AttachedFlyout öffnet sich bei einem normalen Button-Klick nicht automatisch. "
                    + "Verwende Button.Flyout mit einem normalen Flyout oder einen expliziten ShowAttachedFlyout-Aufruf.");
            }
        }

        // A coding prompt authorizes coherent full-file rewrites inside the bound workspace.
        // Atomic replacement and the immediately preceding content comparison
        // protect against torn or stale writes.
    }

    private async Task ValidateSourceMutationAsync(
        string path,
        string? original,
        string updated,
        bool isFullWrite,
        CancellationToken cancellationToken)
    {
        ValidateSourceMutation(path, original, updated, isFullWrite);
        if (!string.Equals(Path.GetExtension(path), ".py", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetExtension(path), ".pyw", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ProcessResult result;
        try
        {
            var python = ResolveProcessExecutable("python");
            result = await RunProcessAsync(
                python,
                ["-c", "import ast, sys; ast.parse(sys.stdin.read())"],
                Workspace(),
                updated,
                TimeSpan.FromSeconds(20),
                cancellationToken).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Python is optional. Where it is available, syntax errors are
            // rejected before the atomic write; its absence must not block other projects.
            return;
        }

        if (result.ExitCode != 0)
        {
            var diagnostic = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            diagnostic = diagnostic.Trim();
            if (diagnostic.Length > 2_000)
            {
                diagnostic = diagnostic[..2_000];
            }
            throw new InvalidDataException(
                $"Die erzeugte Python-Datei ist syntaktisch ungültig und wurde nicht geschrieben: {diagnostic}");
        }
    }

    private Task<object> MoveAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ResolvePath(RequiredString(arguments, "source"), requireExisting: true);
        var destination = ResolvePath(RequiredString(arguments, "destination"), requireExisting: false);
        ValidateVerificationAssetMove(Relative(source), Relative(destination));
        var overwrite = arguments.TryGetProperty("overwrite", out var overwriteValue)
            && overwriteValue.ValueKind == JsonValueKind.True;
        if (!File.Exists(source))
        {
            throw new InvalidOperationException("fs.move unterstützt nur Dateien, keine Ordner.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(source, destination, overwrite);
        return Task.FromResult<object>(new
        {
            moved = true,
            source = Relative(source),
            destination = Relative(destination),
        });
    }

    private async Task<object> CreateFileAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var path = ResolvePath(RequiredString(arguments, "path"), requireExisting: false);
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("Das Ziel existiert bereits; fs.proposeCreate überschreibt keine Daten.");
        }
        var content = RequiredString(arguments, "content", allowEmpty: true);
        await ValidateSourceMutationAsync(path, null, content, isFullWrite: false, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return new
        {
            created = true,
            path = Relative(path),
            length = Encoding.UTF8.GetByteCount(content),
        };
    }

    private async Task<object> DeleteFileAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(RequiredString(arguments, "path"), requireExisting: true);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("fs.proposeDelete unterstützt nur Dateien, keine Ordner.");
        }
        var original = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        ValidateExpectedContent(path, original, arguments, ClientToolNames.FileSystemProposeDelete);
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        return new { deleted = true, path = Relative(path), recoverable = true };
    }

    private async Task<object> ApplyPatchAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var target = ResolvePath(RequiredString(arguments, "path"), requireExisting: true);
        if (!File.Exists(target))
        {
            throw new FileNotFoundException("Das Patchziel wurde nicht gefunden.", target);
        }
        var original = await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false);
        ValidateExpectedContent(target, original, arguments, ClientToolNames.FileSystemProposePatch);
        var patch = NormalizeSingleFilePatch(
            RequiredString(arguments, "patch"),
            Relative(target));
        ValidatePatchTargets(patch, target);
        var result = await RunProcessAsync(
            "git",
            ["-C", Workspace(), "apply", "--recount", "--whitespace=nowarn", "-"],
            Workspace(),
            patch,
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git apply ist fehlgeschlagen: {result.StandardError}");
        }
        try
        {
            var updated = await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false);
            await ValidateSourceMutationAsync(target, original, updated, isFullWrite: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception validationException) when (validationException is InvalidDataException or IOException)
        {
            var rollback = await RunProcessAsync(
                "git",
                ["-C", Workspace(), "apply", "--reverse", "--recount", "--whitespace=nowarn", "-"],
                Workspace(),
                patch,
                TimeSpan.FromMinutes(2),
                CancellationToken.None).ConfigureAwait(false);
            if (rollback.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Die lokale Quellprüfung ist fehlgeschlagen und der Patch konnte nicht automatisch zurückgenommen werden: {rollback.StandardError}",
                    validationException);
            }
            throw;
        }
        return new { patched = true, path = Relative(target), result.ExitCode, result.StandardOutput };
    }

    private void ValidateExpectedContent(
        string path,
        string actualContent,
        JsonElement arguments,
        string tool)
    {
        if (!arguments.TryGetProperty("expectedContent", out var expectedValue)
            || expectedValue.ValueKind != JsonValueKind.String
            || expectedValue.GetString() is not { } expectedContent)
        {
            throw new IOException("Der unmittelbar gelesene Dateiinhalt fehlt. Die Mutation wurde nicht ausgeführt.");
        }
        var mode = arguments.TryGetProperty("expectedContentMode", out var modeValue)
            && modeValue.ValueKind == JsonValueKind.String
                ? modeValue.GetString()
                : "complete";
        var matches = string.Equals(mode, "fragment", StringComparison.Ordinal)
            ? string.Equals(tool, ClientToolNames.FileSystemReplaceText, StringComparison.Ordinal)
                && CountOrdinalOccurrences(actualContent, expectedContent) == 1
            : string.Equals(actualContent, expectedContent, StringComparison.Ordinal);
        if (!matches)
        {
            throw CreateContentConflict(tool, Relative(path));
        }
    }

    internal static string NormalizeSingleFilePatch(string patch, string relativeTarget)
    {
        var normalized = patch
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimStart('\uFEFF');
        if (!normalized.StartsWith("diff --git ", StringComparison.Ordinal))
        {
            var path = relativeTarget.Replace('\\', '/');
            normalized = $"diff --git a/{path} b/{path}\n{normalized}";
        }

        return normalized.TrimEnd('\n') + "\n";
    }

    private async Task<object> RunPresetAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var preset = RequiredString(arguments, "preset");
        // Der einmalig in GO freigegebene Ordner ist immer die verbindliche
        // Prozesswurzel. Modellinterne Aliase wie /workspace sind keine
        // physischen Clientpfade und werden daher nicht mehr aufgelöst.
        var workspace = Workspace();
        string fileName;
        IReadOnlyList<string> commandArguments;
        TimeSpan timeout;
        switch (preset)
        {
            case "git.status":
                fileName = "git";
                commandArguments = ["-C", workspace, "status", "--short"];
                timeout = TimeSpan.FromMinutes(2);
                break;
            case "git.diff":
                var gitDiff = await RunGitDiffAsync(workspace, cancellationToken).ConfigureAwait(false);
                return Bounded(new { preset, gitDiff.ExitCode, gitDiff.StandardOutput, gitDiff.StandardError });
            case "dotnet.build":
                fileName = "dotnet";
                commandArguments = BuildDotNetPresetArguments("build", ResolveOptionalPresetTarget(arguments));
                timeout = TimeSpan.FromMinutes(20);
                break;
            case "dotnet.test":
                fileName = "dotnet";
                commandArguments = BuildDotNetPresetArguments("test", ResolveOptionalPresetTarget(arguments));
                timeout = TimeSpan.FromMinutes(20);
                break;
            case "repository.build":
                var script = ResolveRepositoryBuildScript(workspace);
                fileName = "powershell.exe";
                commandArguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script];
                timeout = TimeSpan.FromMinutes(45);
                break;
            case "repository.verify":
                return await RunRepositoryVerificationAsync(workspace, arguments, cancellationToken).ConfigureAwait(false);
            case "repository.start":
                var startResult = await RunRepositoryStartProcessAsync(workspace, cancellationToken).ConfigureAwait(false);
                return Bounded(new { preset, startResult.ExitCode, startResult.StandardOutput, startResult.StandardError });
            case "code.run":
                (fileName, commandArguments) = ResolveCodeCommand(workspace, arguments, test: false);
                timeout = TimeSpan.FromMinutes(20);
                break;
            case "code.test":
                (fileName, commandArguments) = ResolveCodeCommand(workspace, arguments, test: true);
                timeout = TimeSpan.FromMinutes(20);
                break;
            default:
                throw new InvalidOperationException($"Das Prozess-Preset '{preset}' ist nicht freigegeben.");
        }
        var result = await RunProcessAsync(fileName, commandArguments, workspace, null, timeout, cancellationToken).ConfigureAwait(false);
        if (preset == "git.status" && result.ExitCode == 0)
        {
            result = result with { StandardOutput = SummarizeGitStatus(result.StandardOutput) };
        }
        return Bounded(new { preset, result.ExitCode, result.StandardOutput, result.StandardError });
    }

    private async Task<ProcessResult> RunGitDiffAsync(string workspace, CancellationToken cancellationToken)
    {
        var repository = await RunProcessAsync(
            "git",
            ["-C", workspace, "rev-parse", "--is-inside-work-tree"],
            workspace,
            null,
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        if (repository.ExitCode != 0)
        {
            return new ProcessResult(
                -1,
                string.Empty,
                "Der Workspace ist kein Git-Repository. Die GO-Codeänderungsanzeige verwendet unabhängig davon direkte Vorher-/Nachher-Diffs.");
        }
        var hasHead = await RunProcessAsync(
            "git",
            ["-C", workspace, "rev-parse", "--verify", "HEAD"],
            workspace,
            null,
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        var trackedArguments = hasHead.ExitCode == 0
            ? BuildGitDiffArguments(workspace, "HEAD")
            : BuildGitDiffArguments(workspace);
        var tracked = await RunProcessAsync(
            "git",
            trackedArguments,
            workspace,
            null,
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        if (tracked.ExitCode != 0)
        {
            return tracked;
        }

        var untracked = await RunProcessAsync(
            "git",
            ["-C", workspace, "ls-files", "--others", "--exclude-standard", "-z"],
            workspace,
            null,
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        if (untracked.ExitCode != 0)
        {
            return new ProcessResult(
                untracked.ExitCode,
                tracked.StandardOutput,
                string.Join(Environment.NewLine, new[] { tracked.StandardError, untracked.StandardError }
                    .Where(static value => !string.IsNullOrWhiteSpace(value))));
        }

        var result = new StringBuilder(Math.Min(MaximumProcessStreamCharacters, tracked.StandardOutput.Length + 16_384));
        AppendBounded(result, tracked.StandardOutput);
        var omitted = 0;
        foreach (var relativePath in untracked.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = relativePath.Replace('\\', '/');
            if (FindGeneratedStatusRoot(normalized) is not null)
            {
                continue;
            }

            var fullPath = ResolvePath(normalized, requireExisting: true);
            if (Directory.Exists(fullPath) || !File.Exists(fullPath))
            {
                continue;
            }

            var file = new FileInfo(fullPath);
            if (file.Length > 512 * 1024)
            {
                omitted++;
                AppendBounded(result, $"\n[Neue Datei '{normalized}' mit {file.Length:N0} Bytes nicht als Text-Diff eingebettet.]\n");
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (bytes.AsSpan().Contains((byte)0))
            {
                omitted++;
                AppendBounded(result, $"\n[Neue Binärdatei '{normalized}' nicht als Text-Diff eingebettet.]\n");
                continue;
            }

            var text = DecodeText(bytes);
            AppendBounded(result, FormatUntrackedTextDiff(normalized, text));
            if (result.Length >= MaximumProcessStreamCharacters)
            {
                omitted++;
                break;
            }
        }
        if (omitted > 0)
        {
            AppendBounded(result, $"\n[{omitted} neue Datei(en) ausgelassen oder zusammengefasst.]\n");
        }

        return new ProcessResult(
            0,
            result.ToString(),
            string.Join(Environment.NewLine, new[] { tracked.StandardError, untracked.StandardError }
                .Where(static value => !string.IsNullOrWhiteSpace(value))));
    }

    internal static string FormatUntrackedTextDiff(string relativePath, string content)
    {
        var path = relativePath.Replace('\\', '/');
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var hasTerminalNewline = normalized.EndsWith('\n');
        var lines = normalized.Split('\n');
        var lineCount = normalized.Length == 0
            ? 0
            : hasTerminalNewline ? Math.Max(0, lines.Length - 1) : lines.Length;
        var builder = new StringBuilder(normalized.Length + path.Length * 2 + 96);
        builder.Append("\ndiff --git a/").Append(path).Append(" b/").Append(path).Append('\n')
            .Append("new file mode 100644\n")
            .Append("--- /dev/null\n")
            .Append("+++ b/").Append(path).Append('\n')
            .Append("@@ -0,0 +1,").Append(lineCount).Append(" @@\n");
        for (var index = 0; index < lineCount; index++)
        {
            builder.Append('+').Append(lines[index]).Append('\n');
        }
        if (!hasTerminalNewline && lineCount > 0)
        {
            builder.Append("\\ No newline at end of file\n");
        }
        return builder.ToString();
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            return Encoding.UTF8.GetString(bytes, Encoding.UTF8.Preamble.Length, bytes.Length - Encoding.UTF8.Preamble.Length);
        }
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
        {
            return Encoding.Unicode.GetString(bytes, Encoding.Unicode.Preamble.Length, bytes.Length - Encoding.Unicode.Preamble.Length);
        }
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return Encoding.BigEndianUnicode.GetString(bytes, Encoding.BigEndianUnicode.Preamble.Length, bytes.Length - Encoding.BigEndianUnicode.Preamble.Length);
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static void AppendBounded(StringBuilder target, string value)
    {
        var available = MaximumProcessStreamCharacters - target.Length;
        if (available > 0)
        {
            target.Append(value.AsSpan(0, Math.Min(available, value.Length)));
        }
    }

    internal static string SummarizeGitStatus(string output, int maximumDetailedEntries = 240)
    {
        var detailed = new List<string>();
        var grouped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var omittedDetailed = 0;
        foreach (var rawLine in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            var path = line.Length > 3 ? line[3..].Trim(' ', '"') : line;
            var generatedRoot = FindGeneratedStatusRoot(path);
            if (generatedRoot is not null)
            {
                grouped[generatedRoot] = grouped.GetValueOrDefault(generatedRoot) + 1;
                continue;
            }

            if (detailed.Count < maximumDetailedEntries)
            {
                detailed.Add(line);
            }
            else
            {
                omittedDetailed++;
            }
        }

        foreach (var group in grouped.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            detailed.Add($"[{group.Value} Git-Status-Einträge unter '{group.Key}' zusammengefasst]");
        }
        if (omittedDetailed > 0)
        {
            detailed.Add($"[{omittedDetailed} weitere Git-Status-Einträge zusammengefasst]");
        }
        return detailed.Count == 0 ? string.Empty : string.Join('\n', detailed) + "\n";
    }

    private static string? FindGeneratedStatusRoot(string path)
    {
        var normalized = path.Replace('\\', '/');
        var renameTarget = normalized.LastIndexOf(" -> ", StringComparison.Ordinal);
        if (renameTarget >= 0)
        {
            normalized = normalized[(renameTarget + 4)..];
        }
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var generatedSegment = Array.FindIndex(segments, static segment =>
            GeneratedStatusDirectoryNames.Contains(segment) || segment.Equals(".vs", StringComparison.OrdinalIgnoreCase));
        return generatedSegment < 0 ? null : string.Join('/', segments.Take(generatedSegment + 1));
    }

    internal static string[] BuildGitDiffArguments(string workspace, string? reference = null)
    {
        var arguments = new List<string> { "-C", workspace, "diff", "--no-ext-diff" };
        if (!string.IsNullOrWhiteSpace(reference))
        {
            arguments.Add(reference);
        }

        arguments.Add("--");
        arguments.Add(".");
        foreach (var directory in GeneratedStatusDirectoryNames)
        {
            var escaped = directory.Replace('\\', '/');
            arguments.Add($":(exclude){escaped}/**");
            arguments.Add($":(exclude)**/{escaped}/**");
        }

        return [.. arguments];
    }

    private string? ResolveOptionalPresetTarget(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("target", out var target)
            || target.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(target.GetString()))
        {
            return null;
        }

        return Relative(ResolvePath(target.GetString()!, requireExisting: true));
    }

    internal static IReadOnlyList<string> BuildDotNetPresetArguments(string command, string? target)
    {
        var result = new List<string> { command };
        if (!string.IsNullOrWhiteSpace(target))
        {
            result.Add(target);
        }
        result.Add("--nologo");
        return result;
    }

    private string ResolveRepositoryBuildScript(string workspace)
    {
        var candidates = new[]
        {
            Path.Combine(workspace, "windows", "build.ps1"),
            Path.Combine(workspace, "windows", "Build-Portable.ps1"),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return ResolvePath(candidate, requireExisting: true);
            }
        }
        throw new FileNotFoundException(
            "Kein unterstütztes Repository-Buildskript gefunden. Erwartet wird windows/build.ps1 oder windows/Build-Portable.ps1.");
    }

    private async Task<object> RunRepositoryVerificationAsync(
        string workspace,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        const string preset = "repository.verify";
        var goBuildScript = Path.Combine(workspace, "windows", "build.ps1");
        if (File.Exists(goBuildScript))
        {
            var result = await RunProcessAsync(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ResolvePath(goBuildScript, requireExisting: true)],
                workspace,
                null,
                TimeSpan.FromMinutes(45),
                cancellationToken).ConfigureAwait(false);
            return Bounded(new { preset, result.ExitCode, result.StandardOutput, result.StandardError });
        }

        var requestedTarget = arguments.TryGetProperty("target", out var targetValue)
            && targetValue.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(targetValue.GetString());
        var (testExecutable, testArguments) = ResolveCodeCommand(workspace, arguments, test: true);
        var test = await RunProcessAsync(
            testExecutable,
            testArguments,
            workspace,
            null,
            TimeSpan.FromMinutes(20),
            cancellationToken).ConfigureAwait(false);
        if (test.ExitCode != 0)
        {
            return Bounded(new
            {
                preset,
                test.ExitCode,
                StandardOutput = "[Tests]\n" + test.StandardOutput,
                StandardError = "[Tests]\n" + test.StandardError,
            });
        }

        var buildScript = new[]
            {
                Path.Combine(workspace, "windows", "build.ps1"),
                Path.Combine(workspace, "windows", "Build-Portable.ps1"),
            }
            .FirstOrDefault(File.Exists);
        if (buildScript is null)
        {
            return Bounded(new
            {
                preset,
                test.ExitCode,
                StandardOutput = "[Tests]\n" + test.StandardOutput,
                StandardError = "[Tests]\n" + test.StandardError,
            });
        }

        var build = await RunProcessAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ResolvePath(buildScript, requireExisting: true)],
            workspace,
            null,
            TimeSpan.FromMinutes(45),
            cancellationToken).ConfigureAwait(false);
        if (build.ExitCode != 0)
        {
            return Bounded(new
            {
                preset,
                build.ExitCode,
                StandardOutput = "[Tests]\n" + test.StandardOutput + "\n[Build]\n" + build.StandardOutput,
                StandardError = "[Tests]\n" + test.StandardError + "\n[Build]\n" + build.StandardError,
            });
        }

        var start = await RunRepositoryStartProcessAsync(workspace, cancellationToken).ConfigureAwait(false);
        return Bounded(new
        {
            preset,
            start.ExitCode,
            StandardOutput = "[Tests]\n" + test.StandardOutput
                + "\n[Build]\n" + build.StandardOutput
                + "\n[App-Smoke]\n" + start.StandardOutput,
            StandardError = "[Tests]\n" + test.StandardError
                + "\n[Build]\n" + build.StandardError
                + "\n[App-Smoke]\n" + start.StandardError,
        });
    }

    private async Task<ProcessResult> RunRepositoryStartProcessAsync(
        string workspace,
        CancellationToken cancellationToken)
    {
        var smokeScript = Path.Combine(workspace, "windows", "smoke.ps1");
        if (File.Exists(smokeScript))
        {
            var publishDirectory = ResolvePath(Path.Combine(workspace, "artifacts", "portable", "win-x64"), requireExisting: true);
            var manifest = ResolvePath(Path.Combine(workspace, "artifacts", "portable", "win-x64.manifest.json"), requireExisting: true);
            return await RunProcessAsync(
                "powershell.exe",
                [
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                    ResolvePath(smokeScript, requireExisting: true),
                    "-PublishDirectory", publishDirectory, "-ManifestPath", manifest, "-Mode", "SingleFile",
                ],
                workspace,
                null,
                TimeSpan.FromMinutes(5),
                cancellationToken).ConfigureAwait(false);
        }

        var executable = ResolvePortableApplication(workspace);
        return await RunSmokeProcessAsync(
            executable,
            [],
            Path.GetDirectoryName(executable)!,
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
    }

    private string ResolvePortableApplication(string workspace)
    {
        var artifacts = ResolvePath(Path.Combine(workspace, "artifacts"), requireExisting: true);
        var ignoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "createdump.exe",
            "testhost.exe",
            "vstest.console.exe",
        };
        var executable = Directory.EnumerateFiles(artifacts, "*.exe", System.IO.SearchOption.AllDirectories)
            .Where(path => !ignoredNames.Contains(Path.GetFileName(path)))
            .OrderByDescending(path => path.Contains("portable", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? throw new FileNotFoundException("Nach dem Portable-Build wurde im Artefaktordner keine startbare Anwendung gefunden.");
        return ResolvePath(executable, requireExisting: true);
    }

    internal static void ValidateVerificationAssetMove(string source, string destination)
    {
        var normalizedSource = source.Replace('\\', '/').TrimStart('/');
        var normalizedDestination = destination.Replace('\\', '/').TrimStart('/');
        var sourceIsTest = normalizedSource.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
            || normalizedSource.Contains("/tests/", StringComparison.OrdinalIgnoreCase);
        var destinationIsTest = normalizedDestination.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
            || normalizedDestination.Contains("/tests/", StringComparison.OrdinalIgnoreCase);
        if (sourceIsTest
            && (!destinationIsTest
                || normalizedDestination.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "Testdateien dürfen nicht aus dem regulären Testbaum verschoben oder deaktiviert werden. Behebe die Implementierung oder den Test am ursprünglichen Testpfad.");
        }
    }

    private async Task<object> RunArbitraryProcessAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var requestedExecutable = RequiredString(arguments, "executable");
        var processArguments = ReadProcessArguments(arguments);
        var normalizedRequest = NormalizeInlineProcessRequest(requestedExecutable, processArguments);
        requestedExecutable = normalizedRequest.Executable;
        processArguments = normalizedRequest.Arguments;
        var normalizedPython = NormalizePythonProcessRequest(
            requestedExecutable,
            processArguments,
            Workspace());
        requestedExecutable = normalizedPython.Executable;
        processArguments = normalizedPython.Arguments;
        requestedExecutable = NormalizeSystemRuntimeAlias(requestedExecutable);
        var executable = ResolveProcessExecutable(requestedExecutable);
        var workingDirectory = arguments.TryGetProperty("workingDirectory", out var workingValue)
            && workingValue.ValueKind == JsonValueKind.String
            ? ResolvePath(workingValue.GetString()!, requireExisting: true)
            : Workspace();
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException("Das Arbeitsverzeichnis des Prozesses wurde nicht gefunden.");
        }
        var pythonCommand = ResolveWorkspacePythonCommand(
            requestedExecutable,
            executable,
            processArguments,
            Workspace());
        executable = pythonCommand.Executable;
        processArguments = pythonCommand.Arguments;
        ValidatePythonProcessHasEntryPoint(executable, processArguments);
        ValidateProcessBoundary(executable, processArguments);
        var timeoutSeconds = OptionalInteger(arguments, "timeoutSeconds") ?? 1_200;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var purpose = RequiredString(arguments, "purpose");
        var startMode = arguments.TryGetProperty("startMode", out var startModeValue)
            ? startModeValue.GetString() ?? "wait"
            : "wait";
        var result = startMode == "smoke"
            ? await RunSmokeProcessAsync(executable, processArguments, workingDirectory, timeout, cancellationToken).ConfigureAwait(false)
            : await RunProcessAsync(executable, processArguments, workingDirectory, null, timeout, cancellationToken).ConfigureAwait(false);
        return Bounded(new
        {
            purpose,
            executable = Path.GetFileName(executable),
            workingDirectory = Relative(workingDirectory),
            startMode,
            isolatedPythonEnvironment = pythonCommand.Isolated,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
        });
    }

    internal static (string Executable, string[] Arguments) NormalizeInlineProcessRequest(
        string requestedExecutable,
        string[] arguments)
    {
        var requested = requestedExecutable.Trim();
        if (requested.Length == 0
            || File.Exists(requested)
            || !ContainsCommandLineWhitespace(requested))
        {
            return (requested, arguments);
        }

        requested = RemoveCapturedOutputSuppression(requested);
        var tokens = TokenizeDirectProcessCommandLine(requested);
        if (tokens.Count <= 1)
        {
            return (requested, arguments);
        }

        if (IsCmdExecutable(tokens[0]))
        {
            var commandIndex = tokens.FindIndex(1, static token => token.Equals("/c", StringComparison.OrdinalIgnoreCase));
            if (commandIndex < 0 || commandIndex + 1 >= tokens.Count)
            {
                throw new InvalidOperationException(
                    "Eine zusammengefügte cmd-Anforderung muss genau einen sicheren Direktbefehl nach /c enthalten.");
            }

            var nestedCommand = string.Join(' ', tokens.Skip(commandIndex + 1));
            nestedCommand = RemoveCapturedOutputSuppression(nestedCommand);
            if (ContainsShellControlOperator(nestedCommand))
            {
                throw new InvalidOperationException(
                    "Shell-Verkettungen, Pipes und Datei-Umleitungen sind in process.run nicht erlaubt. "
                    + "Übermittle genau ein Programm und dessen Argumente getrennt.");
            }

            return NormalizeInlineProcessRequest(nestedCommand, arguments);
        }

        var executable = tokens[0];
        var parsedArguments = tokens.Skip(1).ToList();
        CollapsePythonCodeArgument(executable, parsedArguments);
        parsedArguments.AddRange(arguments);
        return (executable, parsedArguments.ToArray());
    }

    private static bool ContainsCommandLineWhitespace(string value) =>
        value.Any(char.IsWhiteSpace);

    private static bool IsCmdExecutable(string value)
    {
        var name = Path.GetFileName(value);
        return name.Equals("cmd", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveCapturedOutputSuppression(string command)
    {
        var result = command.Trim();
        while (true)
        {
            var updated = Regex.Replace(
                result,
                @"\s+(?:(?:1?>)\s*nul(?:\s+2>\s*&1)?|2>\s*&1)\s*$",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (updated.Length == result.Length)
            {
                return result;
            }
            result = updated.TrimEnd();
        }
    }

    private static List<string> TokenizeDirectProcessCommandLine(string command)
    {
        var result = new List<string>();
        var token = new StringBuilder(command.Length);
        var inDoubleQuotes = false;
        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];
            if (character == '\\'
                && inDoubleQuotes
                && index + 1 < command.Length
                && command[index + 1] == '"')
            {
                token.Append('"');
                index++;
                continue;
            }
            if (character == '"')
            {
                inDoubleQuotes = !inDoubleQuotes;
                continue;
            }
            if (char.IsWhiteSpace(character) && !inDoubleQuotes)
            {
                if (token.Length > 0)
                {
                    result.Add(token.ToString());
                    token.Clear();
                }
                continue;
            }
            token.Append(character);
        }

        if (inDoubleQuotes)
        {
            throw new InvalidOperationException(
                "Die zusammengefügte process.run-Anforderung enthält nicht geschlossene Anführungszeichen.");
        }
        if (token.Length > 0)
        {
            result.Add(token.ToString());
        }
        return result;
    }

    private static void CollapsePythonCodeArgument(string executable, List<string> arguments)
    {
        var name = Path.GetFileName(executable);
        var isPython = IsPythonExecutableName(name)
            || name.Equals("py", StringComparison.OrdinalIgnoreCase)
            || name.Equals("py.exe", StringComparison.OrdinalIgnoreCase);
        if (!isPython)
        {
            return;
        }

        var codeIndex = arguments.FindIndex(static argument => argument.Equals("-c", StringComparison.OrdinalIgnoreCase));
        if (codeIndex < 0 || codeIndex + 2 >= arguments.Count)
        {
            return;
        }

        var code = string.Join(' ', arguments.Skip(codeIndex + 1));
        arguments.RemoveRange(codeIndex + 1, arguments.Count - codeIndex - 1);
        arguments.Add(code);
    }

    private static bool ContainsShellControlOperator(string command)
    {
        var inDoubleQuotes = false;
        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];
            if (character == '\\'
                && inDoubleQuotes
                && index + 1 < command.Length
                && command[index + 1] == '"')
            {
                index++;
                continue;
            }
            if (character == '"')
            {
                inDoubleQuotes = !inDoubleQuotes;
                continue;
            }
            if (!inDoubleQuotes && character is '&' or '|' or '<' or '>')
            {
                return true;
            }
        }
        return false;
    }

    internal static (string Executable, string[] Arguments) NormalizePythonProcessRequest(
        string requestedExecutable,
        string[] arguments,
        string workspace)
    {
        var requestedName = Path.GetFileName(requestedExecutable);
        var workspacePython = Path.GetFullPath(Path.Combine(workspace, ".venv", "Scripts", "python.exe"));
        var workspaceEnvironmentExists = File.Exists(workspacePython);
        var isPython = IsPythonExecutableName(requestedName);
        var isLauncher = requestedName.Equals("py", StringComparison.OrdinalIgnoreCase)
            || requestedName.Equals("py.exe", StringComparison.OrdinalIgnoreCase);
        var isPip = requestedName.Equals("pip", StringComparison.OrdinalIgnoreCase)
            || requestedName.Equals("pip.exe", StringComparison.OrdinalIgnoreCase)
            || requestedName.Equals("pip3", StringComparison.OrdinalIgnoreCase)
            || requestedName.Equals("pip3.exe", StringComparison.OrdinalIgnoreCase)
            || requestedName.StartsWith("pip3.", StringComparison.OrdinalIgnoreCase);
        var isPytest = requestedName.Equals("pytest", StringComparison.OrdinalIgnoreCase)
            || requestedName.Equals("pytest.exe", StringComparison.OrdinalIgnoreCase);

        if ((isPython || isLauncher)
            && IsBareUnittestInvocation(arguments)
            && TryFindPythonTestRoot(workspace, out var testRoot))
        {
            arguments = BuildPythonUnittestDiscoveryArguments(arguments, testRoot);
        }

        if (workspaceEnvironmentExists)
        {
            if (isPip)
            {
                return (workspacePython, ["-m", "pip", .. RemovePipPythonSelector(arguments)]);
            }
            if (isPytest)
            {
                return (workspacePython, ["-m", "pytest", .. arguments]);
            }
            if (isPython)
            {
                return (workspacePython, arguments);
            }
            if (isLauncher && !IsPythonLauncherInspection(arguments) && !InvokesVenv(arguments))
            {
                return (workspacePython, RemovePythonLauncherVersion(arguments));
            }
            return (requestedExecutable, arguments);
        }

        if (isPip)
        {
            return ("python", ["-m", "pip", .. RemovePipPythonSelector(arguments)]);
        }
        if (isPytest)
        {
            return ("python", ["-m", "pytest", .. arguments]);
        }
        if (isPython
            && (requestedName.Equals("python3", StringComparison.OrdinalIgnoreCase)
                || requestedName.Equals("python3.exe", StringComparison.OrdinalIgnoreCase)))
        {
            return ("python", arguments);
        }

        if (!InvokesVenv(arguments))
        {
            return (requestedExecutable, arguments);
        }
        if (isLauncher)
        {
            return (requestedExecutable, arguments);
        }
        if (!isPython)
        {
            return (requestedExecutable, arguments);
        }

        var selector = InferPythonLauncherSelector(requestedExecutable) ?? "-3.11";
        return ("py", [selector, .. arguments]);
    }

    internal static bool IsBareUnittestInvocation(IReadOnlyList<string> arguments)
    {
        var moduleIndex = -1;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Equals("-m", StringComparison.OrdinalIgnoreCase))
            {
                moduleIndex = index;
                break;
            }
        }
        if (moduleIndex < 0
            || moduleIndex + 1 >= arguments.Count
            || !arguments[moduleIndex + 1].Equals("unittest", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return arguments
            .Skip(moduleIndex + 2)
            .All(static argument => argument is "-v" or "--verbose" or "-q" or "--quiet" or "-f" or "--failfast" or "-b" or "--buffer" or "-c" or "--catch");
    }

    internal static string[] BuildPythonUnittestDiscoveryArguments(
        IReadOnlyList<string> originalArguments,
        string relativeTestRoot)
    {
        var moduleIndex = -1;
        for (var index = 0; index < originalArguments.Count; index++)
        {
            if (originalArguments[index].Equals("-m", StringComparison.OrdinalIgnoreCase))
            {
                moduleIndex = index;
                break;
            }
        }
        if (moduleIndex < 0 || moduleIndex + 1 >= originalArguments.Count)
        {
            throw new ArgumentException("The Python unittest module selector is missing.", nameof(originalArguments));
        }

        var prefix = originalArguments.Take(moduleIndex + 2).ToArray();
        var options = originalArguments.Skip(moduleIndex + 2).ToArray();
        return [.. prefix, "discover", "-s", relativeTestRoot, .. options];
    }

    private static bool TryFindPythonTestRoot(string workspace, out string relativeTestRoot)
    {
        var testsDirectory = Path.Combine(workspace, "tests");
        if (Directory.Exists(testsDirectory)
            && Directory.EnumerateFiles(testsDirectory, "test*.py", System.IO.SearchOption.AllDirectories).Any())
        {
            relativeTestRoot = "tests";
            return true;
        }
        if (Directory.EnumerateFiles(workspace, "test*.py", System.IO.SearchOption.TopDirectoryOnly).Any())
        {
            relativeTestRoot = ".";
            return true;
        }

        relativeTestRoot = string.Empty;
        return false;
    }

    internal static void ValidatePythonProcessHasEntryPoint(
        string executable,
        IReadOnlyList<string> arguments)
    {
        if (IsPythonExecutableName(Path.GetFileName(executable)) && arguments.Count == 0)
        {
            throw new InvalidOperationException(
                "Ein Python-Prozess ohne Skript, -m-Modul oder -c-Ausdruck ist keine ausführbare Test-, Build- oder Startprüfung. "
                + "Verwende beispielsweise -m pytest, -m py_compile <Datei> oder einen konkreten Programmeinstiegspunkt.");
        }
    }

    private static bool IsPythonExecutableName(string name) =>
        name.Equals("python", StringComparison.OrdinalIgnoreCase)
        || name.Equals("python.exe", StringComparison.OrdinalIgnoreCase)
        || name.Equals("python3", StringComparison.OrdinalIgnoreCase)
        || name.Equals("python3.exe", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(name, "^python\\d+(?:\\.\\d+)?(?:\\.exe)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool InvokesVenv(string[] arguments)
    {
        for (var index = 0; index + 1 < arguments.Length; index++)
        {
            if (arguments[index].Equals("-m", StringComparison.OrdinalIgnoreCase)
                && arguments[index + 1].Equals("venv", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsPythonLauncherInspection(IReadOnlyList<string> arguments) =>
        arguments.Any(argument => argument.Equals("-0", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("-0p", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--list", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--list-paths", StringComparison.OrdinalIgnoreCase));

    private static string[] RemovePythonLauncherVersion(string[] arguments) =>
        arguments.Length > 0 && Regex.IsMatch(arguments[0], "^-\\d+(?:\\.\\d+)?$", RegexOptions.CultureInvariant)
            ? arguments.Skip(1).ToArray()
            : arguments.ToArray();

    private static string[] RemovePipPythonSelector(string[] arguments)
    {
        var result = new List<string>(arguments.Length);
        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index].Equals("--python", StringComparison.OrdinalIgnoreCase)
                && index + 1 < arguments.Length)
            {
                index++;
                continue;
            }
            result.Add(arguments[index]);
        }
        return result.ToArray();
    }

    private static string? InferPythonLauncherSelector(string executable)
    {
        var match = Regex.Match(
            executable,
            "python(?<version>\\d{2,3})(?:\\\\|/|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            match = Regex.Match(
                Path.GetFileNameWithoutExtension(executable),
                "^python(?<version>\\d{2,3})$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        if (!match.Success)
        {
            return null;
        }
        var digits = match.Groups["version"].Value;
        return digits.Length >= 2 ? $"-{digits[0]}.{digits[1..]}" : null;
    }

    private (string FileName, IReadOnlyList<string> Arguments) ResolveCodeCommand(
        string workspace,
        JsonElement arguments,
        bool test)
    {
        var requestedTarget = arguments.TryGetProperty("target", out var targetValue)
            && targetValue.ValueKind == JsonValueKind.String
            ? targetValue.GetString()
            : null;
        var normalizedTarget = NormalizeWorkspaceAlias(requestedTarget);
        var target = string.IsNullOrWhiteSpace(normalizedTarget)
            ? FindCodeTarget(workspace, test)
            : ResolveCodeTarget(workspace, normalizedTarget, test);
        var extension = Path.GetExtension(target).ToLowerInvariant();

        if (extension == ".py")
        {
            var workspacePython = Path.Combine(workspace, ".venv", "Scripts", "python.exe");
            var python = File.Exists(workspacePython)
                ? ResolvePath(workspacePython, requireExisting: true)
                : "python";
            return test
                ? (python, BuildPythonTestArguments(target))
                : (python, [target]);
        }
        if (extension == ".js")
        {
            return test
                ? ("npm.cmd", ["test"])
                : ("node", [target]);
        }
        if (extension is ".ts" or ".tsx" or ".mts" or ".cts")
        {
            return test
                ? ("npm.cmd", ["test"])
                : ("npx.cmd", ["--no-install", "tsx", target]);
        }
        if (extension == ".ps1")
        {
            return ("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", target]);
        }
        if (extension is ".bat" or ".cmd")
        {
            return ("cmd.exe", ["/d", "/c", target]);
        }
        if (extension == ".sh")
        {
            return ("bash", [target]);
        }
        if (extension == ".go")
        {
            return test ? ("go", ["test", "./..."]) : ("go", ["run", target]);
        }
        if (extension == ".rs")
        {
            var manifest = Directory.EnumerateFiles(workspace, "Cargo.toml", System.IO.SearchOption.TopDirectoryOnly).FirstOrDefault();
            return manifest is not null
                ? test ? ("cargo", ["test"]) : ("cargo", ["run"])
                : test ? ("rustc", ["--test", target]) : ("rustc", [target]);
        }
        if (extension == ".java")
        {
            return ("java", [target]);
        }
        if (extension == ".kts")
        {
            return ("kotlinc", ["-script", target]);
        }
        if (extension == ".rb")
        {
            return ("ruby", [target]);
        }
        if (extension == ".php")
        {
            return ("php", [target]);
        }
        if (extension == ".lua")
        {
            return ("lua", [target]);
        }
        if (extension == ".pl")
        {
            return ("perl", [target]);
        }
        if (extension == ".dart")
        {
            return test ? ("dart", ["test"]) : ("dart", ["run", target]);
        }
        if (extension is ".csproj" or ".fsproj" or ".vbproj" or ".sln" or ".slnx")
        {
            return test
                ? ("dotnet", ["test", target, "--nologo"])
                : extension is ".sln" or ".slnx"
                    ? throw new InvalidOperationException("Zum Starten muss ein konkretes .NET-Projekt statt einer Solution angegeben werden.")
                    : ("dotnet", ["run", "--project", target]);
        }
        if (extension is ".cs" or ".fs" or ".vb")
        {
            var project = FindNearestDotNetProject(workspace, target);
            return test
                ? ("dotnet", ["test", project, "--nologo", "--filter", $"FullyQualifiedName~{Path.GetFileNameWithoutExtension(target)}"])
                : ("dotnet", ["run", "--project", project]);
        }
        if (Path.GetFileName(target).Equals("package.json", StringComparison.OrdinalIgnoreCase))
        {
            return test
                ? ("npm.cmd", ["test"])
                : ("npm.cmd", ["start"]);
        }
        if (extension == ".exe")
        {
            return (target, []);
        }
        throw new InvalidOperationException(
            "Für diesen Dateityp gibt es kein universelles code.run/code.test-Kommando. Verwende process.run mit dem im Repository vorgesehenen Compiler, Interpreter oder Buildwerkzeug.");
    }

    internal static IReadOnlyList<string> BuildPythonTestArguments(string target)
    {
        var content = File.ReadAllText(target);
        var usesUnittest = content.Contains("import unittest", StringComparison.Ordinal)
            || content.Contains("from unittest", StringComparison.Ordinal);
        return usesUnittest
            ? ["-m", "unittest", target]
            : ["-m", "pytest", target];
    }

    private string ResolveCodeTarget(string workspace, string requestedTarget, bool test)
    {
        try
        {
            return ResolvePath(requestedTarget, requireExisting: true);
        }
        catch (FileNotFoundException) when (test && !Path.IsPathFullyQualified(requestedTarget))
        {
            var normalizedSuffix = requestedTarget.Replace('\\', '/').TrimStart('/');
            var fileName = Path.GetFileName(normalizedSuffix);
            var suffixMatches = EnumerateFilesSafe(workspace)
                .Where(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                .Where(path => Relative(path).EndsWith(normalizedSuffix, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (suffixMatches.Length == 1)
            {
                return ResolvePath(suffixMatches[0], requireExisting: true);
            }

            var nameMatches = EnumerateFilesSafe(workspace)
                .Where(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (nameMatches.Length == 1)
            {
                return ResolvePath(nameMatches[0], requireExisting: true);
            }
            throw new FileNotFoundException(
                "Das Testziel wurde nicht eindeutig gefunden. Nutze fs.findFiles und übergib danach einen vorhandenen relativen Pfad.",
                requestedTarget);
        }
    }

    private string FindNearestDotNetProject(string workspace, string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath);
        while (!string.IsNullOrWhiteSpace(directory) && IsWithin(workspace, directory))
        {
            var projects = Directory.EnumerateFiles(directory, "*.*proj", System.IO.SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(path).Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(path).Equals(".vbproj", StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (projects.Length == 1)
            {
                return ResolvePath(projects[0], requireExisting: true);
            }
            directory = Path.GetDirectoryName(directory);
        }
        throw new FileNotFoundException(
            "Für die Quelldatei wurde im übergeordneten Workspace kein eindeutiges .NET-Projekt gefunden.",
            sourcePath);
    }

    private string FindCodeTarget(string workspace, bool test)
    {
        var patterns = test
            ? new[] { "*.slnx", "*.sln", "*.csproj", "package.json", "pyproject.toml", "Cargo.toml", "go.mod", "test_*.py", "*_test.py", "*.py", "*.js", "*.ts", "*.ps1", "*.go", "*.rs" }
            : new[] { "*.csproj", "package.json", "Cargo.toml", "go.mod", "main.py", "app.py", "*.py", "index.js", "*.js", "*.ts", "*.ps1", "*.bat", "*.cmd", "*.go", "*.rs", "*.java", "*.rb", "*.php" };
        foreach (var pattern in patterns)
        {
            var candidate = Directory.EnumerateFiles(workspace, pattern, System.IO.SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (candidate is not null)
            {
                return ResolvePath(candidate, requireExisting: true);
            }
        }
        if (test)
        {
            foreach (var pattern in new[] { "test_*.py", "*_test.py" })
            {
                var candidate = EnumerateFilesSafe(workspace)
                    .FirstOrDefault(path => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                        pattern,
                        Path.GetFileName(path),
                        ignoreCase: true));
                if (candidate is not null)
                {
                    return ResolvePath(candidate, requireExisting: true);
                }
            }
        }
        throw new FileNotFoundException("Im Workspace wurde kein automatischer Start- oder Test-Einstiegspunkt gefunden. Verwende process.run mit dem Repositorykommando.");
    }

    internal static string? NormalizeWorkspaceAlias(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }
        var normalized = target.Trim().Replace('\\', '/');
        if (normalized is "/" or "." or "./"
            || string.Equals(normalized, "/workspace", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "workspace", StringComparison.OrdinalIgnoreCase))
        {
            return ".";
        }
        const string rootedAlias = "/workspace/";
        if (normalized.StartsWith(rootedAlias, StringComparison.OrdinalIgnoreCase))
        {
            return normalized[rootedAlias.Length..];
        }
        const string relativeAlias = "workspace/";
        return normalized.StartsWith(relativeAlias, StringComparison.OrdinalIgnoreCase)
            ? normalized[relativeAlias.Length..]
            : target.Trim();
    }

    private async Task<object> ListDocumentsAsync(CancellationToken cancellationToken)
    {
        var sessionId = _executionSession.Value ?? throw new InvalidOperationException("Die Dokument-Sitzung fehlt.");
        var items = await documents.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new
        {
            documents = items.Where(static item => item.PreparationStatus == GoWinUI.Core.Models.DocumentPreparationStatus.Ready)
                .Select(static item => new { documentId = item.Id, item.FileName, item.PageCount, item.Sha256 }),
        };
    }

    private LocalDocumentToolService RequireDocumentTools() => _documentTools
        ?? throw new InvalidOperationException("Die lokalen Dokumentwerkzeuge sind nicht initialisiert.");

    private async Task<object> SearchDocumentsAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var sessionId = _executionSession.Value ?? throw new InvalidOperationException("Die Dokument-Sitzung fehlt.");
        var query = arguments.GetProperty("query").GetString()!;
        var maximum = arguments.TryGetProperty("maximumCharacters", out var value) ? value.GetInt32() : 120_000;
        IReadOnlyList<GoWinUI.Core.Models.DocumentContextHit> hits;
        var searchMode = "fulltext";
        try
        {
            using var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
            var modelStatus = await client.GetModelStatusAsync(cancellationToken).ConfigureAwait(false);
            var embeddingModel = modelStatus.Models.FirstOrDefault(static item => item.Downloaded && item.Role == "embedding");
            var indexed = embeddingModel is null
                ? []
                : await documents.ListIndexChunksAsync(sessionId, embeddingModel.Id, cancellationToken).ConfigureAwait(false);
            if (embeddingModel is not null
                && indexed.Count > 0
                && indexed.All(static item => item.Embedding is not null))
            {
                var response = await client.CreateEmbeddingsAsync(
                    new EmbeddingBatchRequest([new EmbeddingInput("query", query)]),
                    cancellationToken).ConfigureAwait(false);
                var vector = response.Vectors.Single(static item => item.Id == "query").Values;
                hits = await documents.SearchHybridAsync(
                    sessionId,
                    query,
                    embeddingModel.Id,
                    vector,
                    maximum,
                    cancellationToken).ConfigureAwait(false);
                searchMode = "hybrid";
            }
            else
            {
                hits = await documents.SearchAsync(sessionId, query, maximum, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            hits = await documents.SearchAsync(sessionId, query, maximum, cancellationToken).ConfigureAwait(false);
            searchMode = $"fulltext (semantisch nicht verfügbar: {exception.GetType().Name})";
        }
        return new
        {
            searchMode,
            evidence = hits.Select(static hit => new
            {
                hit.DocumentId,
                hit.FileName,
                hit.PageNumber,
                hit.Score,
                hit.Text,
                citation = $"[{hit.FileName}, S. {hit.PageNumber}]",
            }),
        };
    }

    private async Task<object> ReadDocumentPagesAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var sessionId = _executionSession.Value ?? throw new InvalidOperationException("Die Dokument-Sitzung fehlt.");
        var documentId = Guid.Parse(arguments.GetProperty("documentId").GetString()!);
        var start = arguments.GetProperty("startPage").GetInt32();
        var end = arguments.GetProperty("endPage").GetInt32();
        if (end < start || end - start > 100) throw new InvalidDataException("Der angeforderte Seitenbereich ist ungültig oder zu groß.");
        var document = (await documents.ListAsync(sessionId, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == documentId && item.PreparationStatus == GoWinUI.Core.Models.DocumentPreparationStatus.Ready)
            ?? throw new FileNotFoundException("Das Dokument ist in dieser Sitzung nicht fertig aufbereitet.");
        var pages = (await documents.ReadPagesAsync(documentId, cancellationToken).ConfigureAwait(false))
            .Where(page => page.PageNumber >= start && page.PageNumber <= end)
            .Select(page => new { documentId, document.FileName, page.PageNumber, page.Text, citation = $"[{document.FileName}, S. {page.PageNumber}]" })
            .ToArray();
        return new { pages };
    }

    private async Task<object> RunBricsCadAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!bricsCad.IsConnected)
        {
            throw new InvalidOperationException("Das GO-BricsCAD-Plugin ist nicht verbunden.");
        }
        var operation = RequiredString(arguments, "operation");
        ValidateCadOperation(toolName, operation);
        var parameters = arguments.TryGetProperty("arguments", out var value) && value.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(value.GetRawText())?.AsObject() ?? new JsonObject()
            : new JsonObject();
        var response = await bricsCad.RequestAsync(operation, parameters, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!response.Ok)
        {
            throw new BridgeRemoteException(response);
        }
        return new
        {
            operation,
            provider = response.Provider ?? BridgeProtocol.Provider,
            result = response.Result,
        };
    }

    private static void ValidateCadOperation(string toolName, string operation)
    {
        var valid = toolName switch
        {
            ClientToolNames.BricsCadGeometryQuery => operation is "geometry.query" or "selection.describe" or "entity.describe" or "layers.list" or "bim.objects.query" or "bim.components.query",
            ClientToolNames.BricsCadMeasure => operation is "measurement.bbox" or "measurement.length" or "measurement.area",
            ClientToolNames.BricsCadMove => operation is "geometry.move" or "bim.move",
            ClientToolNames.BricsCadAction => operation is
                "pipes.validateNetwork" or "bim.host.point.resolve"
                or "layers.create" or "layers.rename" or "layers.setColor" or "layers.batch"
                or "entity.setLayer" or "entity.setName" or "selection.set" or "bim.selection.set"
                or "geometry.create" or "geometry.copy" or "geometry.rotate" or "geometry.scale" or "geometry.delete"
                or "profile.extrude" or "circle.extrude" or "rectangles.extrude"
                or "pipes.createNetworkSolids" or "annotations.createRoomDimensions"
                or "document.save" or "undo.last" or "undo.redo"
                or "bim.classify" or "bim.create" or "bricscad.assoc.evaluate",
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidOperationException($"Die BricsCAD-Operation '{operation}' passt nicht zu {toolName}.");
        }
    }

    private string ResolvePath(string requested, bool requireExisting)
    {
        var root = Workspace();
        var normalizedRequested = NormalizeWorkspaceAlias(requested) ?? requested;
        var combined = Path.IsPathFullyQualified(normalizedRequested)
            ? normalizedRequested
            : Path.Combine(root, normalizedRequested);
        var full = Path.GetFullPath(combined);
        if (!IsWithin(root, full))
        {
            throw new UnauthorizedAccessException("Der angeforderte Pfad liegt außerhalb des freigegebenen Workspace.");
        }
        RejectReparsePoints(root, full);
        if (requireExisting && !File.Exists(full) && !Directory.Exists(full))
        {
            throw new FileNotFoundException("Der angeforderte Workspace-Pfad wurde nicht gefunden.", full);
        }
        return full;
    }

    private string Relative(string path) => Path.GetRelativePath(Workspace(), path).Replace('\\', '/');

    private string Workspace() => !string.IsNullOrWhiteSpace(_executionWorkspace.Value)
        ? _executionWorkspace.Value
        : TryGetWorkspace(null, out var workspace)
            ? workspace
            : throw new InvalidOperationException("Für diese AI-Sitzung ist kein gültiger lokaler Workspace freigegeben.");

    private string? ResolveWorkspace(string? requested) => TryGetWorkspace(requested, out var workspace)
        ? workspace
        : null;

    private bool TryGetWorkspace(string? requested, out string workspace)
    {
        workspace = string.Empty;
        var configured = string.IsNullOrWhiteSpace(requested)
            ? settings.Current.LocalToolWorkspacePath
            : requested;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }
        try
        {
            workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
            return Directory.Exists(workspace);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalizedRoot, Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectReparsePoints(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException("Reparse- und Symlink-Pfade sind für AI-Tools gesperrt.");
            }
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(directory);
                directories = Directory.GetDirectories(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                yield return file;
            }
            foreach (var child in directories)
            {
                if (!File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static bool IsProbablyBinary(string path)
    {
        var extension = Path.GetExtension(path);
        return extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".pdf" or ".zip" or ".7z"
            or ".dll" or ".exe" or ".pdb" or ".db" or ".sqlite" or ".wav" or ".mp3" or ".mp4";
    }

    private void ValidatePatchTargets(string patch, string target)
    {
        var expected = Relative(target);
        var targetHeaders = 0;
        foreach (var rawLine in patch.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("rename from ", StringComparison.Ordinal)
                || line.StartsWith("rename to ", StringComparison.Ordinal)
                || line.StartsWith("copy from ", StringComparison.Ordinal)
                || line.StartsWith("copy to ", StringComparison.Ordinal)
                || line.StartsWith("old mode ", StringComparison.Ordinal)
                || line.StartsWith("new mode ", StringComparison.Ordinal)
                || line.StartsWith("new file mode ", StringComparison.Ordinal)
                || line.StartsWith("deleted file mode ", StringComparison.Ordinal)
                || line.StartsWith("similarity index ", StringComparison.Ordinal)
                || line.Equals("GIT binary patch", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Umbenennungen, Kopien, Modusänderungen und Binärpatches sind über fs.proposePatch nicht erlaubt.");
            }
            if (!line.StartsWith("+++ ", StringComparison.Ordinal)
                && !line.StartsWith("--- ", StringComparison.Ordinal))
            {
                continue;
            }
            targetHeaders++;
            var value = line[4..].Trim().Replace('\\', '/');
            if (value == "/dev/null")
            {
                throw new InvalidDataException("Dateien müssen mit den getrennten Erstellen-/Löschen-Werkzeugen geändert werden.");
            }
            if (value.StartsWith("a/", StringComparison.Ordinal) || value.StartsWith("b/", StringComparison.Ordinal))
            {
                value = value[2..];
            }
            if (!string.Equals(value, expected, StringComparison.OrdinalIgnoreCase)
                || value.Contains("../", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Der Patch darf ausschließlich den bestätigten Workspace-Pfad ändern.");
            }
        }
        if (targetHeaders != 2)
        {
            throw new InvalidDataException("Der Patch muss genau ein vorhandenes Ziel mit ---/+++-Headern beschreiben.");
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? standardInput,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput is not null,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };
        if (standardInput is not null)
        {
            process.StartInfo.StandardInputEncoding = new UTF8Encoding(false, true);
        }
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        ConfigureCodingProcessEnvironment(process.StartInfo);
        if (!process.Start())
        {
            throw new InvalidOperationException($"Der freigegebene Prozess '{fileName}' konnte nicht gestartet werden.");
        }
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        using var streamCancellation = new CancellationTokenSource();
        var outputTask = ReadBoundedProcessStreamAsync(process.StandardOutput, streamCancellation.Token);
        var errorTask = ReadBoundedProcessStreamAsync(process.StandardError, streamCancellation.Token);
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception drainException) when (drainException is TimeoutException or InvalidOperationException) { }
            CancelProcessStreamReads(process, streamCancellation);
            _ = await ObserveProcessStreamAsync(outputTask).ConfigureAwait(false);
            _ = await ObserveProcessStreamAsync(errorTask).ConfigureAwait(false);
            if (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Das freigegebene Prozess-Preset hat das Zeitlimit von {timeout.TotalMinutes:N0} Minuten überschritten.", exception);
            }
            throw;
        }
        var streams = await DrainProcessStreamsAsync(process, outputTask, errorTask, streamCancellation).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, streams.StandardOutput, streams.StandardError);
    }

    private static async Task<ProcessResult> RunSmokeProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        ConfigureCodingProcessEnvironment(process.StartInfo);
        if (!process.Start())
        {
            throw new InvalidOperationException($"Der Smoke-Start '{fileName}' konnte nicht gestartet werden.");
        }
        using var streamCancellation = new CancellationTokenSource();
        var outputTask = ReadBoundedProcessStreamAsync(process.StandardOutput, streamCancellation.Token);
        var errorTask = ReadBoundedProcessStreamAsync(process.StandardError, streamCancellation.Token);
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        try
        {
            var exited = await Task.WhenAny(
                process.WaitForExitAsync(linked.Token),
                Task.Delay(TimeSpan.FromSeconds(8), linked.Token)).ConfigureAwait(false);
            if (process.HasExited)
            {
                var completedStreams = await DrainProcessStreamsAsync(process, outputTask, errorTask, streamCancellation).ConfigureAwait(false);
                return new ProcessResult(
                    process.ExitCode,
                    completedStreams.StandardOutput,
                    completedStreams.StandardError);
            }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            var streams = await DrainProcessStreamsAsync(process, outputTask, errorTask, streamCancellation).ConfigureAwait(false);
            return new ProcessResult(0, streams.StandardOutput + "\n[App-Smoke-Start war 8 Sekunden stabil.]", streams.StandardError);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            CancelProcessStreamReads(process, streamCancellation);
            throw;
        }
    }

    private static void ConfigureCodingProcessEnvironment(ProcessStartInfo startInfo)
    {
        // MSBuild- und Compiler-Server dürfen die umgeleiteten Pipe-Handles des
        // bereits beendeten Elternprozesses nicht offenhalten. Andernfalls wartet
        // der Toolbroker trotz abgeschlossenem Build endlos auf EOF.
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        var pathEntries = (startInfo.Environment.TryGetValue("PATH", out var currentPath)
                ? currentPath
                : Environment.GetEnvironmentVariable("PATH"))
            ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList()
            ?? [];
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var directory in new[]
        {
            Path.Combine(userProfile, ".cargo", "bin"),
            Path.Combine(userProfile, ".elan", "bin"),
            Path.Combine(programFiles, "nodejs"),
            Path.Combine(programFiles, "Go", "bin"),
            Path.Combine(programFiles, "dotnet"),
        })
        {
            if (Directory.Exists(directory)
                && !pathEntries.Contains(directory, StringComparer.OrdinalIgnoreCase))
            {
                pathEntries.Insert(0, directory);
            }
        }
        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, pathEntries);
    }

    internal static string NormalizeSystemRuntimeAlias(string requestedExecutable)
    {
        var name = Path.GetFileName(requestedExecutable);
        if (!string.Equals(name, requestedExecutable, StringComparison.OrdinalIgnoreCase))
        {
            return requestedExecutable;
        }

        return name.ToLowerInvariant() switch
        {
            "nodejs" or "nodejs.exe" => "node",
            _ => requestedExecutable,
        };
    }

    private static async Task<(string StandardOutput, string StandardError)> DrainProcessStreamsAsync(
        Process process,
        Task<string> outputTask,
        Task<string> errorTask,
        CancellationTokenSource streamCancellation)
    {
        try
        {
            await Task.WhenAll(outputTask, errorTask)
                .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            CancelProcessStreamReads(process, streamCancellation);
        }

        var output = await ObserveProcessStreamAsync(outputTask).ConfigureAwait(false) ?? string.Empty;
        var error = await ObserveProcessStreamAsync(errorTask).ConfigureAwait(false) ?? string.Empty;
        return (output, error);
    }

    private static void CancelProcessStreamReads(Process process, CancellationTokenSource streamCancellation)
    {
        if (!streamCancellation.IsCancellationRequested)
        {
            streamCancellation.Cancel();
        }
        try { process.StandardOutput.Dispose(); } catch (InvalidOperationException) { }
        try { process.StandardError.Dispose(); } catch (InvalidOperationException) { }
    }

    private string ResolveProcessExecutable(string requested)
    {
        var normalized = NormalizeWorkspaceAlias(requested) ?? requested;
        if (!Path.IsPathFullyQualified(normalized)
            && !normalized.Contains(Path.DirectorySeparatorChar)
            && !normalized.Contains(Path.AltDirectorySeparatorChar)
            && TryResolveSystemExecutable(normalized, out var systemExecutable))
        {
            return systemExecutable;
        }

        return Path.IsPathFullyQualified(normalized)
            || normalized.Contains(Path.DirectorySeparatorChar)
            || normalized.Contains(Path.AltDirectorySeparatorChar)
                ? ResolvePath(normalized, requireExisting: true)
                : normalized;
    }

    internal static bool TryResolveSystemExecutable(string executable, out string resolved)
    {
        var candidates = new List<string>();
        var extension = Path.GetExtension(executable);
        if (extension.Length > 0)
        {
            candidates.Add(executable);
        }
        else
        {
            candidates.Add(executable + ".exe");
            candidates.Add(executable + ".cmd");
            candidates.Add(executable + ".bat");
            candidates.Add(executable);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var searchDirectories = new List<string>
        {
            Path.Combine(userProfile, ".cargo", "bin"),
            Path.Combine(userProfile, ".elan", "bin"),
            Path.Combine(programFiles, "nodejs"),
            Path.Combine(programFiles, "Go", "bin"),
            Path.Combine(programFiles, "dotnet"),
        };
        searchDirectories.AddRange(
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var directory in searchDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path))
                {
                    resolved = Path.GetFullPath(path);
                    return true;
                }
            }
        }

        resolved = executable;
        return false;
    }

    internal static (string Executable, string[] Arguments, bool Isolated) ResolveWorkspacePythonCommand(
        string requestedExecutable,
        string resolvedExecutable,
        string[] arguments,
        string workspace)
    {
        var requestedName = Path.GetFileName(requestedExecutable);
        var isBareExecutable = string.Equals(requestedExecutable, requestedName, StringComparison.OrdinalIgnoreCase);
        var workspacePython = Path.GetFullPath(Path.Combine(workspace, ".venv", "Scripts", "python.exe"));
        if (!isBareExecutable || !File.Exists(workspacePython))
        {
            return (resolvedExecutable, arguments, false);
        }

        if (requestedName.Equals("python", StringComparison.OrdinalIgnoreCase)
            || requestedName.Equals("python.exe", StringComparison.OrdinalIgnoreCase)
            || requestedName.Equals("python3", StringComparison.OrdinalIgnoreCase)
            || requestedName.Equals("python3.exe", StringComparison.OrdinalIgnoreCase))
        {
            return (workspacePython, arguments, true);
        }

        if (requestedName.Equals("pip", StringComparison.OrdinalIgnoreCase)
            || requestedName.Equals("pip.exe", StringComparison.OrdinalIgnoreCase)
            || requestedName.Equals("pip3", StringComparison.OrdinalIgnoreCase)
            || requestedName.Equals("pip3.exe", StringComparison.OrdinalIgnoreCase))
        {
            return (workspacePython, ["-m", "pip", .. arguments], true);
        }

        if (requestedName.Equals("pytest", StringComparison.OrdinalIgnoreCase)
            || requestedName.Equals("pytest.exe", StringComparison.OrdinalIgnoreCase))
        {
            return (workspacePython, ["-m", "pytest", .. arguments], true);
        }

        return (resolvedExecutable, arguments, false);
    }

    private void ValidateProcessBoundary(string executable, string[] arguments)
    {
        var name = Path.GetFileName(executable);
        var executableIsInsideWorkspace = Path.IsPathFullyQualified(executable)
            && IsWithin(Workspace(), executable);
        ValidatePythonEnvironmentBoundary(executable, arguments, executableIsInsideWorkspace);
        ValidateGitProcessBoundary(executable, arguments);
        if (name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Any(argument => argument.Equals("-Command", StringComparison.OrdinalIgnoreCase)
                || argument.Equals("-EncodedCommand", StringComparison.OrdinalIgnoreCase)
                || argument.Equals("-EncodedArguments", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("PowerShell darf nur eine Skriptdatei aus dem Workspace über -File starten.");
            }
            var fileIndex = Array.FindIndex(arguments, argument => argument.Equals("-File", StringComparison.OrdinalIgnoreCase));
            if (fileIndex < 0 || fileIndex + 1 >= arguments.Length)
            {
                throw new InvalidOperationException("PowerShell process.run benötigt -File und ein Workspace-Skript.");
            }
            _ = ResolvePath(arguments[fileIndex + 1], requireExisting: true);
        }
        if (name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cmd", StringComparison.OrdinalIgnoreCase))
        {
            var commandIndex = Array.FindIndex(arguments, argument => argument.Equals("/c", StringComparison.OrdinalIgnoreCase));
            if (commandIndex < 0 || commandIndex + 1 >= arguments.Length)
            {
                throw new InvalidOperationException("cmd process.run benötigt /c und eine Batchdatei aus dem Workspace.");
            }
            var script = ResolvePath(arguments[commandIndex + 1], requireExisting: true);
            if (Path.GetExtension(script) is not (".bat" or ".cmd"))
            {
                throw new InvalidOperationException("cmd darf ausschließlich eine .bat- oder .cmd-Datei aus dem Workspace starten.");
            }
        }
    }

    internal static void ValidateGitProcessBoundary(string executable, IReadOnlyList<string> arguments)
    {
        var name = Path.GetFileName(executable);
        if (!name.Equals("git", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("git.exe", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? command = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index].Trim();
            if (argument.Length == 0) continue;

            if (argument is "--version" or "-v" or "--help" or "-h"
                or "--no-pager" or "--paginate" or "--no-replace-objects"
                or "--literal-pathspecs" or "--glob-pathspecs" or "--noglob-pathspecs" or "--icase-pathspecs")
            {
                continue;
            }

            if (argument.StartsWith('-'))
            {
                throw new InvalidOperationException(
                    $"Die globale Git-Option '{argument}' ist in process.run nicht erlaubt. "
                    + "Verwende workingDirectory sowie die typisierten Git-Status- und Diff-Presets.");
            }

            command = argument;
            break;
        }

        if (command is null) return;
        if (!ReadOnlyGitCommands.Contains(command))
        {
            throw new InvalidOperationException(
                $"Der Git-Unterbefehl '{command}' ist im autonomen Coding-Modus nicht erlaubt. "
                + "Der Agent darf den echten Git-Index, Referenzen und den Worktree nicht über Git verändern. "
                + "Dateiänderungen erfolgen über die Dateitools; für Git stehen ausschließlich lesende Befehle zur Verfügung.");
        }

        if (arguments.Any(static argument =>
                argument.Equals("--output", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--output=", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Git-Ausgaben dürfen in process.run keine Dateien schreiben. Verwende die erfasste Prozessausgabe oder das Git-Diff-Preset.");
        }
    }

    internal static void ValidatePythonEnvironmentBoundary(
        string executable,
        IReadOnlyList<string> arguments,
        bool executableIsInsideWorkspace)
    {
        var name = Path.GetFileName(executable);
        var isPipExecutable = name.Equals("pip", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pip.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pip3", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pip3.exe", StringComparison.OrdinalIgnoreCase);
        var isPythonExecutable = name.Equals("python", StringComparison.OrdinalIgnoreCase)
            || name.Equals("python.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("python3", StringComparison.OrdinalIgnoreCase)
            || name.Equals("python3.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("py", StringComparison.OrdinalIgnoreCase)
            || name.Equals("py.exe", StringComparison.OrdinalIgnoreCase);
        var moduleIndex = -1;
        if (isPythonExecutable)
        {
            for (var index = 0; index + 1 < arguments.Count; index++)
            {
                if (arguments[index].Equals("-m", StringComparison.OrdinalIgnoreCase))
                {
                    moduleIndex = index;
                    break;
                }
            }
        }

        var invokesPip = isPipExecutable
            || (moduleIndex >= 0 && arguments[moduleIndex + 1].Equals("pip", StringComparison.OrdinalIgnoreCase));
        var invokesEnsurePip = moduleIndex >= 0
            && arguments[moduleIndex + 1].Equals("ensurepip", StringComparison.OrdinalIgnoreCase);
        if ((!invokesPip && !invokesEnsurePip) || executableIsInsideWorkspace)
        {
            return;
        }

        var commandOffset = isPipExecutable ? 0 : moduleIndex + 2;
        var command = arguments
            .Skip(commandOffset)
            .FirstOrDefault(static argument => argument.Length == 0 || argument[0] != '-');
        var mutatesEnvironment = invokesEnsurePip
            || command is null
            || command.Equals("install", StringComparison.OrdinalIgnoreCase)
            || command.Equals("uninstall", StringComparison.OrdinalIgnoreCase)
            || command.Equals("cache", StringComparison.OrdinalIgnoreCase)
            || command.Equals("config", StringComparison.OrdinalIgnoreCase);
        if (!mutatesEnvironment)
        {
            return;
        }

        throw new InvalidOperationException(
            "Globale Python-Paketänderungen sind im Coding-Modus gesperrt. Erzeuge .venv im Workspace "
            + "(unter Windows bevorzugt mit 'py -3.11 -m venv .venv') und verwende anschließend "
            + "'.venv\\Scripts\\python.exe -m pip ...'. Bare python-, pip- und pytest-Aufrufe werden danach "
            + "automatisch auf diese Workspace-Umgebung umgeleitet.");
    }

    private static async Task<string> ReadBoundedProcessStreamAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(MaximumProcessStreamCharacters, 65_536));
        var buffer = new char[8_192];
        var truncated = false;
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                var available = MaximumProcessStreamCharacters - result.Length;
                if (available > 0)
                {
                    result.Append(buffer, 0, Math.Min(available, read));
                }
                if (read > available)
                {
                    truncated = true;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            truncated = true;
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested
            && exception is IOException or ObjectDisposedException)
        {
            truncated = true;
        }
        if (truncated)
        {
            result.Append("\n[Ausgabe gekürzt oder Pipe nach Prozessende geschlossen]");
        }
        return result.ToString();
    }

    private static async Task<string?> ObserveProcessStreamAsync(Task<string> streamTask)
    {
        try
        {
            return await streamTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or TimeoutException or OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<TextReadResult> ReadTextRangeAsync(
        string path,
        int startLine,
        int? requestedEndLine,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        if (requestedEndLine is { } invalidEnd && invalidEnd < startLine)
        {
            throw new InvalidDataException("endLine darf nicht vor startLine liegen.");
        }
        var source = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var lineStarts = new List<int> { 0 };
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '\r')
            {
                if (index + 1 < source.Length && source[index + 1] == '\n')
                {
                    index++;
                }
                lineStarts.Add(index + 1);
            }
            else if (source[index] == '\n')
            {
                lineStarts.Add(index + 1);
            }
        }

        if (startLine > lineStarts.Count)
        {
            return new TextReadResult(
                Relative(path),
                string.Empty,
                new FileInfo(path).Length,
                startLine,
                startLine,
                Truncated: false,
                CompleteFile: false);
        }

        var startOffset = lineStarts[startLine - 1];
        var requestedEndIndex = requestedEndLine is { } endLine
            ? Math.Min(endLine, lineStarts.Count)
            : lineStarts.Count;
        var endOffset = requestedEndIndex < lineStarts.Count
            ? lineStarts[requestedEndIndex]
            : source.Length;
        var availableLength = Math.Max(0, endOffset - startOffset);
        var selectedLength = Math.Min(availableLength, maximumCharacters);
        if (selectedLength > 0
            && startOffset + selectedLength < source.Length
            && char.IsHighSurrogate(source[startOffset + selectedLength - 1]))
        {
            selectedLength--;
        }
        var text = source.Substring(startOffset, selectedLength);
        var truncated = selectedLength < availableLength;
        var lastIncludedLine = startLine + CountLogicalLineBreaks(text);
        if (text.EndsWith("\r\n", StringComparison.Ordinal)
            || text.EndsWith('\r')
            || text.EndsWith('\n'))
        {
            lastIncludedLine--;
        }
        return new TextReadResult(
            Relative(path),
            text,
            new FileInfo(path).Length,
            startLine,
            Math.Max(startLine, lastIncludedLine),
            truncated,
            CompleteFile: startOffset == 0 && selectedLength == source.Length);
    }

    private static int CountLogicalLineBreaks(string value)
    {
        var count = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\r')
            {
                if (index + 1 < value.Length && value[index + 1] == '\n')
                {
                    index++;
                }
                count++;
            }
            else if (value[index] == '\n')
            {
                count++;
            }
        }
        return count;
    }

    private static int? OptionalInteger(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt32(out var result)
            ? result
            : null;

    private static string[] ReadStringArray(JsonElement value, string name) =>
        value.GetProperty(name).EnumerateArray()
            .Select(static item => item.GetString()!.Trim())
            .Where(static item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] ReadOptionalStringArray(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray()
                .Select(static item => item.GetString()!.Trim())
                .Where(static item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();

    private static string[] ReadProcessArguments(JsonElement value) =>
        value.TryGetProperty("arguments", out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().Select(static item => item.GetString() ?? string.Empty).ToArray()
            : [];

    private static string[] SplitLegacyQueries(string query, string mode)
    {
        if (!string.Equals(mode, "literal", StringComparison.Ordinal)
            || !query.Contains('|', StringComparison.Ordinal))
        {
            return [query];
        }
        var values = query.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return values.Length == 0 ? [query] : values;
    }

    private static string RequiredString(JsonElement value, string name, bool allowEmpty = false)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Das Werkzeugargument '{name}' fehlt.");
        }
        var result = NormalizeUnicodeScalarText(property.GetString() ?? string.Empty);
        if (!allowEmpty && string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidDataException($"Das Werkzeugargument '{name}' ist leer.");
        }
        return result;
    }

    internal static string NormalizeUnicodeScalarText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        StringBuilder? builder = null;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsHighSurrogate(current)
                && index + 1 < value.Length
                && char.IsLowSurrogate(value[index + 1]))
            {
                if (builder is not null)
                {
                    builder.Append(current);
                    builder.Append(value[++index]);
                }
                else
                {
                    index++;
                }
                continue;
            }
            if (!char.IsSurrogate(current))
            {
                builder?.Append(current);
                continue;
            }

            builder ??= new StringBuilder(value.Length)
                .Append(value, 0, index);
        }
        return builder?.ToString() ?? value;
    }

    private static string WorkspaceRootPath(JsonElement value, string name)
    {
        var requested = RequiredString(value, name, allowEmpty: true);
        return string.IsNullOrWhiteSpace(requested) ? "." : requested;
    }

    private static object Bounded(object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        if (json.Length > MaximumResultCharacters)
        {
            throw new InvalidOperationException("Das lokale Toolergebnis überschreitet das Größenlimit.");
        }
        return value;
    }

    private static ClientToolResult Result(
        ToolProposal proposal,
        string status,
        object value,
        string? errorCode = null,
        string? message = null) =>
        new(proposal.ProposalId, status, JsonSerializer.SerializeToElement(value, JsonOptions), errorCode, message);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record TextReadResult(
        string Path,
        string Text,
        long Length,
        int StartLine,
        int EndLine,
        bool Truncated,
        bool CompleteFile);

    private sealed class WorkspacePathNotFoundException(string message, object recovery) : FileNotFoundException(message)
    {
        public object Recovery { get; } = recovery;
    }

    private sealed class WorkspaceMutationRecoveryException(
        string errorCode,
        string message,
        object recovery) : IOException(message)
    {
        public string ErrorCode { get; } = errorCode;
        public object Recovery { get; } = recovery;
    }
}
