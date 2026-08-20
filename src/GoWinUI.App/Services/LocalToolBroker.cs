using GoAi.Contracts;
using GoWinUI.BricsCad.Protocol;
using Microsoft.VisualBasic.FileIO;
using System.Diagnostics;
using System.Security.Cryptography;
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
    WorkspaceRepositoryIndex repositoryIndex,
    IDocumentIngestor documents)
{
    private const int MaximumResultCharacters = 4 * 1024 * 1024;
    private const int MaximumProcessStreamCharacters = 1_900_000;
    private const string EmptyContentSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private static readonly JsonSerializerOptions JsonOptions = GoAiProtocol.CreateJsonOptions();
    private readonly AsyncLocal<string?> _executionWorkspace = new();
    private readonly AsyncLocal<Guid?> _executionSession = new();

    public bool IsBricsCadAvailable => bricsCad.IsConnected;

    public string? ActiveWorkspacePath => TryGetWorkspace(null, out var workspace) ? workspace : null;

    public IReadOnlyList<string> GetAvailableCapabilities(string? workspacePath = null)
    {
        var result = new List<string>();
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

    public async Task<ClientToolResult> ExecuteAsync(
        ToolProposal proposal,
        string? workspacePath,
        Guid? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var previousWorkspace = _executionWorkspace.Value;
        try
        {
            var documentTool = proposal.Name.StartsWith("documents.", StringComparison.Ordinal);
            _executionWorkspace.Value = documentTool ? null : ResolveWorkspace(workspacePath);
            _executionSession.Value = sessionId;
            ValidateProposal(proposal);
            if (!await confirmation.ConfirmAsync(proposal, cancellationToken).ConfigureAwait(false))
            {
                return Result(proposal, "rejected", new { rejected = true }, message: "Vom Nutzer abgelehnt.");
            }

            var payload = proposal.Name switch
            {
                ClientToolNames.DocumentsList => await ListDocumentsAsync(cancellationToken).ConfigureAwait(false),
                ClientToolNames.DocumentsSearch => await SearchDocumentsAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.DocumentsReadPages => await ReadDocumentPagesAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.WorkspaceMap => await MapWorkspaceAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemList => await ListAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemStat => await StatAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemFindFiles => await FindFilesAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemReadText => await ReadTextAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemReadMany => await ReadManyAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemSearch => await SearchAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemWriteText => await WriteTextAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemReplaceText => await ReplaceTextAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemMove => await MoveAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemProposePatch => await ApplyPatchAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemProposeCreate => await CreateFileAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemProposeDelete => await DeleteFileAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.ProcessRunPreset => await RunPresetAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.ProcessRun => await RunArbitraryProcessAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.BricsCadGeometryQuery or ClientToolNames.BricsCadMeasure
                    or ClientToolNames.BricsCadMove or ClientToolNames.BricsCadAction =>
                    await RunBricsCadAsync(proposal.Name, proposal.Arguments, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Das Clientwerkzeug '{proposal.Name}' ist nicht implementiert."),
            };
            return Result(proposal, "completed", payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
            ClientToolNames.DocumentsList or ClientToolNames.DocumentsSearch or ClientToolNames.DocumentsReadPages
                or ClientToolNames.WorkspaceMap or ClientToolNames.FileSystemList or ClientToolNames.FileSystemStat
                or ClientToolNames.FileSystemFindFiles or ClientToolNames.FileSystemReadText
                or ClientToolNames.FileSystemReadMany or ClientToolNames.FileSystemSearch
                or ClientToolNames.BricsCadGeometryQuery or ClientToolNames.BricsCadMeasure => ToolRiskClass.ReadOnly,
            ClientToolNames.FileSystemWriteText or ClientToolNames.FileSystemReplaceText or ClientToolNames.FileSystemMove
                or ClientToolNames.FileSystemProposePatch or ClientToolNames.FileSystemProposeCreate
                or ClientToolNames.FileSystemProposeDelete => ToolRiskClass.LocalMutation,
            ClientToolNames.ProcessRunPreset or ClientToolNames.ProcessRun => ToolRiskClass.Process,
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
            case ClientToolNames.WorkspaceMap:
                ValidateProperties(arguments, [], ["maximumDepth", "maximumEntries"]);
                ValidateOptionalInteger(arguments, "maximumDepth", 1, 32);
                ValidateOptionalInteger(arguments, "maximumEntries", 1, 5_000);
                break;
            case ClientToolNames.FileSystemList:
            case ClientToolNames.FileSystemStat:
                ValidateProperties(arguments, ["path"], ["path"]);
                ValidateString(arguments, "path", 0, 1_024);
                break;
            case ClientToolNames.FileSystemProposeDelete:
                ValidateProperties(arguments, ["path"], ["path"]);
                ValidateString(arguments, "path", 1, 1_024);
                break;
            case ClientToolNames.FileSystemReadText:
                ValidateProperties(arguments, ["path"], ["path", "startLine", "endLine"]);
                ValidateString(arguments, "path", 1, 1_024);
                ValidateOptionalInteger(arguments, "startLine", 1, 10_000_000);
                ValidateOptionalInteger(arguments, "endLine", 1, 10_000_000);
                break;
            case ClientToolNames.FileSystemFindFiles:
                ValidateProperties(arguments, ["patterns"], ["path", "patterns", "maximumResults"]);
                ValidateOptionalString(arguments, "path", 0, 1_024);
                ValidateStringArray(arguments, "patterns", 1, 64, 256);
                ValidateOptionalInteger(arguments, "maximumResults", 1, 5_000);
                break;
            case ClientToolNames.FileSystemReadMany:
                ValidateProperties(arguments, ["items"], ["items", "maximumCharacters"]);
                ValidateReadManyItems(arguments);
                ValidateOptionalInteger(arguments, "maximumCharacters", 1_024, MaximumResultCharacters);
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
                ValidateOptionalInteger(arguments, "maximumResults", 1, 1_000);
                ValidateOptionalInteger(arguments, "contextLines", 0, 5);
                break;
            case ClientToolNames.FileSystemWriteText:
                ValidateProperties(arguments, ["path", "content"], ["path", "content", "expectedSha256"]);
                ValidateString(arguments, "path", 1, 1_024);
                ValidateString(arguments, "content", 0, MaximumResultCharacters);
                ValidateOptionalString(arguments, "expectedSha256", 64, 64);
                break;
            case ClientToolNames.FileSystemReplaceText:
                ValidateProperties(arguments, ["path", "oldText", "newText"], ["path", "oldText", "newText", "expectedSha256", "replaceAll"]);
                ValidateString(arguments, "path", 1, 1_024);
                ValidateString(arguments, "oldText", 1, MaximumResultCharacters / 2);
                ValidateString(arguments, "newText", 0, MaximumResultCharacters / 2);
                ValidateOptionalString(arguments, "expectedSha256", 64, 64);
                ValidateOptionalBoolean(arguments, "replaceAll");
                break;
            case ClientToolNames.FileSystemMove:
                ValidateProperties(arguments, ["source", "destination"], ["source", "destination", "overwrite"]);
                ValidateString(arguments, "source", 1, 1_024);
                ValidateString(arguments, "destination", 1, 1_024);
                ValidateOptionalBoolean(arguments, "overwrite");
                break;
            case ClientToolNames.FileSystemProposePatch:
                ValidateProperties(arguments, ["path", "patch"], ["path", "patch"]);
                ValidateString(arguments, "path", 1, 1_024);
                ValidateString(arguments, "patch", 1, MaximumResultCharacters);
                break;
            case ClientToolNames.FileSystemProposeCreate:
                ValidateProperties(arguments, ["path", "content"], ["path", "content"]);
                ValidateString(arguments, "path", 1, 1_024);
                ValidateString(arguments, "content", 0, MaximumResultCharacters);
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
                ValidateString(arguments, "executable", 1, 1_024);
                ValidateOptionalStringArray(arguments, "arguments", 128, 8_192);
                ValidateOptionalString(arguments, "workingDirectory", 1, 1_024);
                ValidateOptionalInteger(arguments, "timeoutSeconds", 1, 3_600);
                ValidateOptionalEnum(arguments, "purpose", ["inspect", "test", "build", "start"]);
                ValidateOptionalEnum(arguments, "startMode", ["wait", "smoke"]);
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

    private static void ValidateReadManyItems(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() is < 1 or > 128)
        {
            throw new InvalidDataException("fs.readMany benötigt 1 bis 128 Dateibereiche.");
        }
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Ein fs.readMany-Eintrag muss ein Objekt sein.");
            }
            ValidateProperties(item, ["path"], ["path", "startLine", "endLine"]);
            ValidateString(item, "path", 1, 1_024);
            ValidateOptionalInteger(item, "startLine", 1, 10_000_000);
            ValidateOptionalInteger(item, "endLine", 1, 10_000_000);
        }
    }

    private async Task<object> MapWorkspaceAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var maximumDepth = OptionalInteger(arguments, "maximumDepth") ?? 8;
        var maximumEntries = OptionalInteger(arguments, "maximumEntries") ?? 2_000;
        var snapshot = await repositoryIndex.GetSnapshotAsync(Workspace(), cancellationToken).ConfigureAwait(false);
        return Bounded(new
        {
            workspace = Path.GetFileName(snapshot.Root),
            fingerprint = snapshot.WorkspaceFingerprint,
            revision = snapshot.RevisionFingerprint,
            snapshot.IndexedAt,
            fileCount = snapshot.Entries.Count,
            snapshot.TextFileCount,
            snapshot.TextBytes,
            snapshot.IsTruncated,
            map = WorkspaceRepositoryIndex.BuildRepositoryMap(snapshot, maximumDepth, maximumEntries),
        });
    }

    private Task<object> ListAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(WorkspaceRootPath(arguments, "path"), requireExisting: true);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException("Der angeforderte Pfad ist kein Ordner.");
        }
        var entries = Directory.EnumerateFileSystemEntries(path)
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
        return Task.FromResult<object>(new { path = Relative(path), entries, truncated = entries.Length == 500 });
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

    private async Task<object> FindFilesAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var root = arguments.TryGetProperty("path", out var rootValue) && rootValue.ValueKind == JsonValueKind.String
            ? string.IsNullOrWhiteSpace(rootValue.GetString()) ? "." : rootValue.GetString()!
            : ".";
        _ = ResolvePath(root, requireExisting: true);
        var patterns = ReadStringArray(arguments, "patterns");
        var maximum = OptionalInteger(arguments, "maximumResults") ?? 500;
        var snapshot = await repositoryIndex.GetSnapshotAsync(Workspace(), cancellationToken).ConfigureAwait(false);
        var matches = WorkspaceRepositoryIndex.FindFiles(snapshot, patterns, root, maximum)
            .Select(static entry => new
            {
                path = entry.Path,
                entry.Length,
                entry.UpdatedAt,
                entry.Language,
                entry.IsBinary,
                entry.Sha256,
            })
            .ToArray();
        return Bounded(new
        {
            path = NormalizeWorkspaceAlias(root) ?? ".",
            patterns,
            matches,
            truncated = matches.Length >= maximum,
            repositoryRevision = snapshot.RevisionFingerprint,
        });
    }

    private async Task<object> ReadTextAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var path = ResolvePath(RequiredString(arguments, "path"), requireExisting: true);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Die angeforderte Textdatei wurde nicht gefunden.", path);
        }
        return Bounded(await ReadTextRangeAsync(
            path,
            OptionalInteger(arguments, "startLine") ?? 1,
            OptionalInteger(arguments, "endLine"),
            MaximumResultCharacters,
            cancellationToken).ConfigureAwait(false));
    }

    private async Task<object> ReadManyAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var maximumCharacters = OptionalInteger(arguments, "maximumCharacters") ?? 3_500_000;
        var remaining = maximumCharacters;
        var files = new List<TextReadResult>();
        foreach (var item in arguments.GetProperty("items").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining <= 0)
            {
                break;
            }
            var path = ResolvePath(RequiredString(item, "path"), requireExisting: true);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Ein angeforderter fs.readMany-Pfad ist keine Datei.", path);
            }
            var result = await ReadTextRangeAsync(
                path,
                OptionalInteger(item, "startLine") ?? 1,
                OptionalInteger(item, "endLine"),
                remaining,
                cancellationToken).ConfigureAwait(false);
            files.Add(result);
            remaining -= result.Text.Length;
        }
        return Bounded(new
        {
            files,
            characters = maximumCharacters - remaining,
            maximumCharacters,
            truncated = files.Count < arguments.GetProperty("items").GetArrayLength()
                || files.Any(static file => file.Truncated),
        });
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
        var maximum = OptionalInteger(arguments, "maximumResults") ?? 100;
        var contextLines = OptionalInteger(arguments, "contextLines") ?? 0;
        var expressions = mode == "regex"
            ? queries.Select(query => new Regex(
                query,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(500))).ToArray()
            : Array.Empty<Regex>();
        var snapshot = await repositoryIndex.GetSnapshotAsync(Workspace(), cancellationToken).ConfigureAwait(false);
        var relativeRoot = Relative(root).TrimEnd('/');
        var candidates = snapshot.Entries.Where(entry =>
            !entry.IsBinary
            && entry.Length <= WorkspaceRepositoryIndex.MaximumSearchableFileLength
            && (File.Exists(root)
                ? entry.Path.Equals(relativeRoot, StringComparison.OrdinalIgnoreCase)
                : relativeRoot is "." or ""
                    || entry.Path.StartsWith(relativeRoot + "/", StringComparison.OrdinalIgnoreCase))
            && WorkspaceRepositoryIndex.MatchesGlobs(entry.Path, includeGlobs, excludeGlobs));
        var matches = new List<object>();
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
                    matches.Add(new
                    {
                        query = queries[queryIndex],
                        path = entry.Path,
                        line = index + 1,
                        text = lines[index].Trim(),
                        contextStartLine = firstContextLine + 1,
                        context = contextLines == 0
                            ? null
                            : string.Join('\n', lines[firstContextLine..(lastContextLine + 1)]),
                    });
                }
            }
            if (matches.Count >= maximum)
            {
                break;
            }
        }
        return Bounded(new
        {
            queries,
            matchMode = mode,
            matches,
            truncated = matches.Count >= maximum,
            searchedFiles = snapshot.Entries.Count,
            repositoryRevision = snapshot.RevisionFingerprint,
        });
    }

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
        var expectsMissingTarget = false;
        if (arguments.TryGetProperty("expectedSha256", out var expectedValue)
            && expectedValue.ValueKind == JsonValueKind.String)
        {
            var expectedSha256 = expectedValue.GetString();
            if (!targetExisted)
            {
                if (!ExpectedHashRepresentsMissingTarget(expectedSha256, targetExists: false))
                {
                    throw new IOException("Die erwartete Zieldatei existiert nicht mehr.");
                }

                // Some coding models use SHA-256(empty) as an optimistic token for a
                // new file. Keep that creation race-safe by requiring the target to
                // remain absent until the atomic move below.
                expectsMissingTarget = true;
            }
            else
            {
                await using var existing = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81_920, true);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(existing, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
                if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Die Zieldatei wurde zwischenzeitlich geändert; fs.writeText wurde nicht ausgeführt.");
                }
            }
        }
        var content = RequiredString(arguments, "content", allowEmpty: true);
        ValidateSourceMutation(path, existingContent, content, isFullWrite: existingContent is not null);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".go-ai.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: !expectsMissingTarget);
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
            sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
        };
    }

    internal static bool ExpectedHashRepresentsMissingTarget(string? expectedSha256, bool targetExists) =>
        !targetExists
        && string.Equals(expectedSha256, EmptyContentSha256, StringComparison.OrdinalIgnoreCase);

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
        if (arguments.TryGetProperty("expectedSha256", out var expectedValue)
            && expectedValue.ValueKind == JsonValueKind.String)
        {
            var actual = await ComputeFileHashAsync(path, cancellationToken).ConfigureAwait(false);
            if (!actual.Equals(expectedValue.GetString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Die Zieldatei wurde zwischenzeitlich geändert; fs.replaceText wurde nicht ausgeführt.");
            }
        }

        var requestedOldText = RequiredString(arguments, "oldText");
        var requestedNewText = RequiredString(arguments, "newText", allowEmpty: true);
        var oldText = NormalizeReplacementLineEndings(requestedOldText, original);
        var newText = NormalizeReplacementLineEndings(requestedNewText, original);
        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            throw new InvalidDataException("fs.replaceText benötigt eine tatsächliche Textänderung.");
        }
        var occurrences = CountOrdinalOccurrences(original, oldText);
        var whitespaceNormalized = false;
        var copiedJsonUnicodeEscapesNormalized = false;
        var whitespaceMatch = occurrences == 0
            ? FindUniqueWhitespaceTolerantMatch(original, oldText, out _)
            : null;
        if (occurrences == 0 && whitespaceMatch is null)
        {
            var decodedOldText = NormalizeReplacementLineEndings(
                DecodeCopiedJsonUnicodeEscapes(requestedOldText),
                original);
            if (!string.Equals(decodedOldText, oldText, StringComparison.Ordinal))
            {
                var decodedOccurrences = CountOrdinalOccurrences(original, decodedOldText);
                var decodedWhitespaceMatch = decodedOccurrences == 0
                    ? FindUniqueWhitespaceTolerantMatch(original, decodedOldText, out _)
                    : null;
                if (decodedOccurrences > 0 || decodedWhitespaceMatch is not null)
                {
                    oldText = decodedOldText;
                    newText = NormalizeReplacementLineEndings(
                        DecodeCopiedJsonUnicodeEscapes(requestedNewText),
                        original);
                    occurrences = decodedOccurrences;
                    whitespaceMatch = decodedWhitespaceMatch;
                    copiedJsonUnicodeEscapesNormalized = true;
                }
            }
        }
        if (occurrences == 0 && whitespaceMatch is null)
        {
            var decodedOldText = NormalizeReplacementLineEndings(
                DecodeCopiedJsonTextEscapes(requestedOldText),
                original);
            if (!string.Equals(decodedOldText, oldText, StringComparison.Ordinal))
            {
                var decodedOccurrences = CountOrdinalOccurrences(original, decodedOldText);
                var decodedWhitespaceMatch = decodedOccurrences == 0
                    ? FindUniqueWhitespaceTolerantMatch(original, decodedOldText, out _)
                    : null;
                if (decodedOccurrences > 0 || decodedWhitespaceMatch is not null)
                {
                    oldText = decodedOldText;
                    newText = NormalizeReplacementLineEndings(
                        DecodeCopiedJsonTextEscapes(requestedNewText),
                        original);
                    occurrences = decodedOccurrences;
                    whitespaceMatch = decodedWhitespaceMatch;
                    copiedJsonUnicodeEscapesNormalized = true;
                }
            }
        }
        if (occurrences == 0 && whitespaceMatch is null)
        {
            throw new InvalidDataException("Der exakt angegebene oldText wurde nicht gefunden. Lies den aktuellen Dateibereich erneut und verwende dessen unveränderten Wortlaut.");
        }
        if (whitespaceMatch is not null)
        {
            oldText = whitespaceMatch;
            occurrences = 1;
            whitespaceNormalized = true;
        }
        var replaceAll = arguments.TryGetProperty("replaceAll", out var replaceAllValue)
            && replaceAllValue.ValueKind == JsonValueKind.True;
        if (!replaceAll && occurrences != 1)
        {
            throw new InvalidDataException($"oldText kommt {occurrences} Mal vor. Gib mehr eindeutigen Kontext an oder setze replaceAll ausdrücklich auf true.");
        }

        var firstOccurrence = original.IndexOf(oldText, StringComparison.Ordinal);
        var updated = replaceAll
            ? original.Replace(oldText, newText, StringComparison.Ordinal)
            : original.Remove(firstOccurrence, oldText.Length).Insert(firstOccurrence, newText);
        ValidateSourceMutation(path, original, updated, isFullWrite: false);
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
            replacements = replaceAll ? occurrences : 1,
            lineEndingsNormalized = !string.Equals(requestedOldText, oldText, StringComparison.Ordinal)
                || !string.Equals(requestedNewText, newText, StringComparison.Ordinal),
            whitespaceNormalized,
            copiedJsonUnicodeEscapesNormalized,
            length = Encoding.UTF8.GetByteCount(updated),
            sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(updated))).ToLowerInvariant(),
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

    internal static string? FindUniqueWhitespaceTolerantMatch(
        string source,
        string requestedText,
        out int occurrences)
    {
        occurrences = 0;
        if (requestedText.Count(static character => !char.IsWhiteSpace(character)) < 8)
        {
            return null;
        }

        var pattern = new StringBuilder();
        for (var index = 0; index < requestedText.Length;)
        {
            if (char.IsWhiteSpace(requestedText[index]))
            {
                while (index < requestedText.Length && char.IsWhiteSpace(requestedText[index])) index++;
                pattern.Append(@"\s+");
                continue;
            }

            var start = index;
            while (index < requestedText.Length && !char.IsWhiteSpace(requestedText[index])) index++;
            pattern.Append(Regex.Escape(requestedText[start..index]));
        }

        var regex = new Regex(
            pattern.ToString(),
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromSeconds(2));
        var matches = regex.Matches(source).Cast<Match>().ToArray();
        occurrences = matches.Length;
        return matches.Length == 1 ? matches[0].Value : null;
    }

    internal static string NormalizeReplacementLineEndings(string value, string existingContent)
    {
        string? lineEnding = existingContent.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : existingContent.Contains('\n')
                ? "\n"
                : existingContent.Contains('\r')
                    ? "\r"
                    : null;
        if (lineEnding is null)
        {
            return value;
        }

        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", lineEnding, StringComparison.Ordinal);
    }

    internal static string DecodeCopiedJsonUnicodeEscapes(string value) => Regex.Replace(
        value,
        @"\\u(?<code>[0-9a-fA-F]{4})",
        static match => ((char)Convert.ToUInt16(match.Groups["code"].Value, 16)).ToString(),
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(250));

    internal static string DecodeCopiedJsonTextEscapes(string value)
    {
        var decoded = DecodeCopiedJsonUnicodeEscapes(value)
            .Replace("\\r\\n", "\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\n", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal);
        return System.Net.WebUtility.HtmlDecode(decoded);
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
        // Atomic replacement and optional expectedSha256 still protect against torn or stale writes;
        // git diff plus mandatory verification expose unintended broad changes to the agent.
    }

    private Task<object> MoveAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ResolvePath(RequiredString(arguments, "source"), requireExisting: true);
        var destination = ResolvePath(RequiredString(arguments, "destination"), requireExisting: false);
        ValidateVerificationAssetMove(Relative(source), Relative(destination));
        var overwrite = arguments.TryGetProperty("overwrite", out var overwriteValue)
            && overwriteValue.ValueKind == JsonValueKind.True;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(source))
        {
            File.Move(source, destination, overwrite);
        }
        else if (Directory.Exists(source))
        {
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new IOException("Das Ziel für den Ordner existiert bereits.");
            }
            Directory.Move(source, destination);
        }
        else
        {
            throw new FileNotFoundException("Die zu verschiebende Workspace-Datei wurde nicht gefunden.", source);
        }
        return Task.FromResult<object>(new { moved = true, source = Relative(source), destination = Relative(destination) });
    }

    private async Task<object> CreateFileAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var path = ResolvePath(RequiredString(arguments, "path"), requireExisting: false);
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("Das Ziel existiert bereits; fs.proposeCreate überschreibt keine Daten.");
        }
        var content = RequiredString(arguments, "content", allowEmpty: true);
        ValidateSourceMutation(path, null, content, isFullWrite: false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return new { created = true, path = Relative(path), length = Encoding.UTF8.GetByteCount(content) };
    }

    private Task<object> DeleteFileAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(RequiredString(arguments, "path"), requireExisting: true);
        if (File.Exists(path))
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        else if (Directory.Exists(path))
        {
            if (string.Equals(path, Workspace(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Der freigegebene Workspace selbst darf nicht gelöscht werden.");
            }
            FileSystem.DeleteDirectory(
                path,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException);
        }
        else
        {
            throw new FileNotFoundException("Das zu löschende Workspace-Element wurde nicht gefunden.", path);
        }
        return Task.FromResult<object>(new { deleted = true, path = Relative(path), recoverable = true });
    }

    private async Task<object> ApplyPatchAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var target = ResolvePath(RequiredString(arguments, "path"), requireExisting: true);
        if (!File.Exists(target))
        {
            throw new FileNotFoundException("Das Patchziel wurde nicht gefunden.", target);
        }
        var original = await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false);
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
            ValidateSourceMutation(target, original, updated, isFullWrite: false);
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
                fileName = "git";
                commandArguments = ["-C", workspace, "diff", "--no-ext-diff"];
                timeout = TimeSpan.FromMinutes(2);
                break;
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
            segment.Equals(".venv", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("venv", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("__pycache__", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".vs", StringComparison.OrdinalIgnoreCase));
        return generatedSegment < 0 ? null : string.Join('/', segments.Take(generatedSegment + 1));
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
        var (testExecutable, testArguments) = requestedTarget
            ? ResolveCodeCommand(workspace, arguments, test: true)
            : ("dotnet", (IReadOnlyList<string>)["test", "--nologo"]);
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

        var buildScript = ResolveRepositoryBuildScript(workspace);
        var build = await RunProcessAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", buildScript],
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
        var normalizedPython = NormalizePythonProcessRequest(
            requestedExecutable,
            processArguments,
            Workspace());
        requestedExecutable = normalizedPython.Executable;
        processArguments = normalizedPython.Arguments;
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
        throw new FileNotFoundException("Im Workspace wurde kein automatischer Start- oder Test-Einstiegspunkt gefunden. Verwende process.run mit dem Repositorykommando.");
    }

    internal static string? NormalizeWorkspaceAlias(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }
        var normalized = target.Trim().Replace('\\', '/');
        if (string.Equals(normalized, "/workspace", StringComparison.OrdinalIgnoreCase)
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
        var combined = Path.IsPathFullyQualified(requested) ? requested : Path.Combine(root, requested);
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
        return Path.IsPathFullyQualified(normalized)
            || normalized.Contains(Path.DirectorySeparatorChar)
            || normalized.Contains(Path.AltDirectorySeparatorChar)
                ? ResolvePath(normalized, requireExisting: true)
                : normalized;
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
        var builder = new StringBuilder(Math.Min(maximumCharacters, 65_536));
        var currentLine = 0;
        var lastIncludedLine = startLine - 1;
        var truncated = false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81_920, true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 81_920, leaveOpen: false);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            currentLine++;
            if (currentLine < startLine)
            {
                continue;
            }
            if (requestedEndLine is { } endLine && currentLine > endLine)
            {
                break;
            }
            var required = line.Length + (builder.Length == 0 ? 0 : 1);
            if (required > maximumCharacters - builder.Length)
            {
                var remaining = maximumCharacters - builder.Length;
                if (remaining > 0)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append('\n');
                        remaining--;
                    }
                    if (remaining > 0)
                    {
                        builder.Append(line.AsSpan(0, Math.Min(line.Length, remaining)));
                    }
                }
                truncated = true;
                lastIncludedLine = currentLine;
                break;
            }
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }
            builder.Append(line);
            lastIncludedLine = currentLine;
        }
        return new TextReadResult(
            Relative(path),
            builder.ToString(),
            new FileInfo(path).Length,
            startLine,
            Math.Max(startLine, lastIncludedLine),
            truncated,
            await ComputeFileHashAsync(path, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81_920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
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
        var result = property.GetString() ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidDataException($"Das Werkzeugargument '{name}' ist leer.");
        }
        return result;
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
        string Sha256);
}
