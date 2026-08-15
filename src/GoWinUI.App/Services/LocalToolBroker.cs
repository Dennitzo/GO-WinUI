using GoAi.Contracts;
using GoWinUI.BricsCad.Protocol;
using Microsoft.VisualBasic.FileIO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GoWinUI.App.Services;

public sealed class LocalToolBroker(
    SettingsCoordinator settings,
    ToolConfirmationService confirmation,
    IBricsCadBridgeHost bricsCad,
    WorkspaceRepositoryIndex repositoryIndex)
{
    private const int MaximumResultCharacters = 4 * 1024 * 1024;
    private const int MaximumProcessStreamCharacters = 1_900_000;
    private static readonly JsonSerializerOptions JsonOptions = GoAiProtocol.CreateJsonOptions();
    private readonly AsyncLocal<string?> _executionWorkspace = new();

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
        ExecuteAsync(proposal, null, cancellationToken);

    public async Task<ClientToolResult> ExecuteAsync(
        ToolProposal proposal,
        string? workspacePath,
        CancellationToken cancellationToken = default)
    {
        var previousWorkspace = _executionWorkspace.Value;
        try
        {
            _executionWorkspace.Value = ResolveWorkspace(workspacePath);
            ValidateProposal(proposal);
            if (!await confirmation.ConfirmAsync(proposal, cancellationToken).ConfigureAwait(false))
            {
                return Result(proposal, "rejected", new { rejected = true }, message: "Vom Nutzer abgelehnt.");
            }

            var payload = proposal.Name switch
            {
                ClientToolNames.WorkspaceMap => await MapWorkspaceAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemList => await ListAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemStat => await StatAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemFindFiles => await FindFilesAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemReadText => await ReadTextAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemReadMany => await ReadManyAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemSearch => await SearchAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
                ClientToolNames.FileSystemWriteText => await WriteTextAsync(proposal.Arguments, cancellationToken).ConfigureAwait(false),
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
            ClientToolNames.WorkspaceMap or ClientToolNames.FileSystemList or ClientToolNames.FileSystemStat
                or ClientToolNames.FileSystemFindFiles or ClientToolNames.FileSystemReadText
                or ClientToolNames.FileSystemReadMany or ClientToolNames.FileSystemSearch
                or ClientToolNames.BricsCadGeometryQuery or ClientToolNames.BricsCadMeasure => ToolRiskClass.ReadOnly,
            ClientToolNames.FileSystemWriteText or ClientToolNames.FileSystemMove
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
            case ClientToolNames.WorkspaceMap:
                ValidateProperties(arguments, [], ["maximumDepth", "maximumEntries"]);
                ValidateOptionalInteger(arguments, "maximumDepth", 1, 32);
                ValidateOptionalInteger(arguments, "maximumEntries", 1, 5_000);
                break;
            case ClientToolNames.FileSystemList:
            case ClientToolNames.FileSystemStat:
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
                ValidateOptionalString(arguments, "path", 1, 1_024);
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
                ValidateString(arguments, "path", 1, 1_024);
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
        var path = ResolvePath(RequiredString(arguments, "path"), requireExisting: true);
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
        var path = ResolvePath(RequiredString(arguments, "path"), requireExisting: true);
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
            ? rootValue.GetString() ?? "."
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
        var root = ResolvePath(RequiredString(arguments, "path"), requireExisting: true);
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
        if (Directory.Exists(path))
        {
            throw new IOException("fs.writeText kann keinen Ordner überschreiben.");
        }
        if (arguments.TryGetProperty("expectedSha256", out var expectedValue)
            && expectedValue.ValueKind == JsonValueKind.String)
        {
            if (!File.Exists(path))
            {
                throw new IOException("Die erwartete Zieldatei existiert nicht mehr.");
            }
            await using var existing = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81_920, true);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(existing, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (!actual.Equals(expectedValue.GetString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Die Zieldatei wurde zwischenzeitlich geändert; fs.writeText wurde nicht ausgeführt.");
            }
        }
        var content = RequiredString(arguments, "content", allowEmpty: true);
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
            sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
        };
    }

    private Task<object> MoveAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ResolvePath(RequiredString(arguments, "source"), requireExisting: true);
        var destination = ResolvePath(RequiredString(arguments, "destination"), requireExisting: false);
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
        var patch = RequiredString(arguments, "patch");
        ValidatePatchTargets(patch, target);
        var result = await RunProcessAsync(
            "git",
            ["-C", Workspace(), "apply", "--whitespace=nowarn", "-"],
            Workspace(),
            patch,
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git apply ist fehlgeschlagen: {result.StandardError}");
        }
        return new { patched = true, path = Relative(target), result.ExitCode, result.StandardOutput };
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
                commandArguments = ["build", "--nologo"];
                timeout = TimeSpan.FromMinutes(20);
                break;
            case "dotnet.test":
                fileName = "dotnet";
                commandArguments = ["test", "--nologo"];
                timeout = TimeSpan.FromMinutes(20);
                break;
            case "repository.build":
            case "repository.verify":
                var script = ResolvePath(Path.Combine(workspace, "windows", "build.ps1"), requireExisting: true);
                fileName = "powershell.exe";
                commandArguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script];
                timeout = TimeSpan.FromMinutes(45);
                break;
            case "repository.start":
                var smokeScript = ResolvePath(Path.Combine(workspace, "windows", "smoke.ps1"), requireExisting: true);
                var publishDirectory = ResolvePath(Path.Combine(workspace, "artifacts", "portable", "win-x64"), requireExisting: true);
                var manifest = ResolvePath(Path.Combine(workspace, "artifacts", "portable", "win-x64.manifest.json"), requireExisting: true);
                fileName = "powershell.exe";
                commandArguments =
                [
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", smokeScript,
                    "-PublishDirectory", publishDirectory, "-ManifestPath", manifest, "-Mode", "SingleFile",
                ];
                timeout = TimeSpan.FromMinutes(5);
                break;
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
        return Bounded(new { preset, result.ExitCode, result.StandardOutput, result.StandardError });
    }

    private async Task<object> RunArbitraryProcessAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var requestedExecutable = RequiredString(arguments, "executable");
        var executable = ResolveProcessExecutable(requestedExecutable);
        var processArguments = ReadProcessArguments(arguments);
        var workingDirectory = arguments.TryGetProperty("workingDirectory", out var workingValue)
            && workingValue.ValueKind == JsonValueKind.String
            ? ResolvePath(workingValue.GetString()!, requireExisting: true)
            : Workspace();
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException("Das Arbeitsverzeichnis des Prozesses wurde nicht gefunden.");
        }
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
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
        });
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
            : ResolvePath(normalizedTarget, requireExisting: true);
        var extension = Path.GetExtension(target).ToLowerInvariant();

        if (extension == ".py")
        {
            return test
                ? ("python", ["-m", "pytest", target])
                : ("python", [target]);
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
        if (!process.Start())
        {
            throw new InvalidOperationException($"Der freigegebene Prozess '{fileName}' konnte nicht gestartet werden.");
        }
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        var outputTask = ReadBoundedProcessStreamAsync(process.StandardOutput);
        var errorTask = ReadBoundedProcessStreamAsync(process.StandardError);
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
            _ = await ObserveProcessStreamAsync(outputTask).ConfigureAwait(false);
            _ = await ObserveProcessStreamAsync(errorTask).ConfigureAwait(false);
            if (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Das freigegebene Prozess-Preset hat das Zeitlimit von {timeout.TotalMinutes:N0} Minuten überschritten.", exception);
            }
            throw;
        }
        return new ProcessResult(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
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
        if (!process.Start())
        {
            throw new InvalidOperationException($"Der Smoke-Start '{fileName}' konnte nicht gestartet werden.");
        }
        var outputTask = ReadBoundedProcessStreamAsync(process.StandardOutput);
        var errorTask = ReadBoundedProcessStreamAsync(process.StandardError);
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        try
        {
            var exited = await Task.WhenAny(
                process.WaitForExitAsync(linked.Token),
                Task.Delay(TimeSpan.FromSeconds(8), linked.Token)).ConfigureAwait(false);
            if (process.HasExited)
            {
                return new ProcessResult(
                    process.ExitCode,
                    await outputTask.ConfigureAwait(false),
                    await errorTask.ConfigureAwait(false));
            }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return new ProcessResult(0, output + "\n[App-Smoke-Start war 8 Sekunden stabil.]", error);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
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

    private void ValidateProcessBoundary(string executable, string[] arguments)
    {
        var name = Path.GetFileName(executable);
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

    private static async Task<string> ReadBoundedProcessStreamAsync(StreamReader reader)
    {
        var result = new StringBuilder(Math.Min(MaximumProcessStreamCharacters, 65_536));
        var buffer = new char[8_192];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
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
        if (truncated)
        {
            result.Append("\n[Ausgabe gekürzt]");
        }
        return result.ToString();
    }

    private static async Task<string?> ObserveProcessStreamAsync(Task<string> streamTask)
    {
        try
        {
            return await streamTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or TimeoutException)
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
