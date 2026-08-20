using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GoAi.Client;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.BricsCad.Protocol;
using GoWinUI.Infrastructure;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GoWinUI.Tests;

public sealed class QwenCoderExcelLiveTests
{
    private const string WorkspaceEnvironmentVariable = "GO_AI_LIVE_EXCEL_WORKSPACE";
    private const string ModelEnvironmentVariable = "GO_AI_LIVE_CODING_MODEL";
    private static readonly JsonSerializerOptions JsonOptions = GoAiProtocol.CreateJsonOptions();

    [Fact]
    [Trait("Category", "Live")]
    public async Task QwenCoderCanCreateEditAndAnalyzeATgaVentilationWorkbook()
    {
        var requestedWorkspace = Environment.GetEnvironmentVariable(WorkspaceEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(requestedWorkspace))
        {
            // XLSX-Erzeugung und ein echter Coding-Serverlauf werden nur explizit
            // aktiviert. So bleibt der reguläre Testlauf lokal und deterministisch.
            return;
        }

        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedWorkspace));
        Assert.True(Directory.Exists(workspace), $"Excel-Live-Workspace fehlt: {workspace}");
        await EnsureGitRepositoryAsync(workspace);
        var modelId = Environment.GetEnvironmentVariable(ModelEnvironmentVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelId = "qwen3-coder-next";
        }
        var workbookExistedBefore = Directory.EnumerateFiles(workspace, "*.xlsx", SearchOption.AllDirectories)
            .Any(path => !Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal));

        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(8));
        var sessionId = $"live-excel-{Guid.NewGuid():N}";
        await using var harness = await CodingAgentLiveTestHarness.CreateAsync(
            "excel-tga",
            workspace,
            modelId,
            sessionId,
            timeout.Token);
        var creation = await harness.ExecuteAsync(
            sessionId,
            """
            Erstelle in diesem neuen Workspace eine reproduzierbare, professionell gestaltete Excel-Arbeitsmappe für
            die Luftmengenplanung einer TGA-Lüftungsanlage. Die Mappe soll mindestens acht unterschiedliche Räume
            enthalten und Eingaben, nachvollziehbare Berechnungen, eine kompakte Projektübersicht sowie dokumentierte
            Annahmen sauber trennen.

            Berechne je Raum mindestens Raumvolumen, personenbezogenen Außenluftvolumenstrom,
            Luftwechsel-Volumenstrom und den maßgebenden Auslegungsvolumenstrom mit echten Excel-Formeln. Dokumentiere
            Zu- und Abluft, spezifischen Volumenstrom, einen sinnvollen Auslegungsquerschnitt und einen äquivalenten
            runden Kanaldurchmesser. Verwende SI-Einheiten und kennzeichne Annahmen fachlich eindeutig. Summen und
            Kennwerte der Übersicht müssen aus den Berechnungsdaten referenziert werden und dürfen nicht hartcodiert sein.

            Gestalte die Arbeitsmappe wie ein nutzbares Planungsdokument: klarer Titelbereich, konsistente Farben,
            lesbare Zahlenformate, fixierte Kopfzeilen, Filter oder strukturierte Tabelle, bedingte Formatierung und
            mindestens ein aussagekräftiges Diagramm. Richte Druckbereich und Seitenlayout sinnvoll ein.

            Lege Generatorquellen und Abhängigkeiten im Workspace ab, sodass die XLSX-Datei jederzeit reproduziert
            werden kann. Ergänze automatisierte Tests, die die erzeugte Arbeitsmappe erneut öffnen und mindestens
            Tabellenstruktur, Formeln, Einheiten, Styles und Diagramm prüfen. Führe Generator, Tests, statische
            beziehungsweise Build-Prüfung und einen begrenzten Laufzeit-Smoke aus. Prüfe abschließend die erzeugten
            Dateien und behebe gefundene Fehler selbstständig.
            """,
            "live-excel-create",
            timeout.Token);

        CodingAgentLiveTestHarness.AssertSuccessful(
            creation,
            modelId,
            requireMutation: !workbookExistedBefore);
        var workbookPath = FindGeneratedWorkbook(workspace);
        var initialInspection = InspectWorkbook(workbookPath);
        var initialIssues = CollectWorkbookFoundationIssues(initialInspection);
        if (initialIssues.Count > 0)
        {
            var auditPrompt = """
                Eine unabhängige formatbewusste XLSX-Abnahme hat in der bestehenden Luftmengenplanung die folgenden
                konkreten Mängel gefunden. Behebe sie im Generator, in den Tests und in derselben erzeugten Arbeitsmappe.
                Entferne oder schwäche keine vorhandene Prüfung. Lies das Binärformat weiterhin nur über die verwendete
                Tabellenbibliothek ein.

                """ + string.Join(Environment.NewLine, initialIssues.Select(static issue => "- " + issue)) + """


                Regeneriere anschließend die XLSX-Datei, führe alle fachlichen Tests, die Build-/Generatorvalidierung,
                einen Laufzeit-Smoke und die abschließende Änderungsprüfung aus. Behebe Folgefehler selbstständig.
                """;
            var audit = await harness.ExecuteAsync(
                sessionId,
                auditPrompt,
                "live-excel-audit",
                timeout.Token);
            CodingAgentLiveTestHarness.AssertSuccessful(audit, modelId);
            initialInspection = InspectWorkbook(workbookPath);
        }
        AssertWorkbookFoundation(initialInspection);
        var initialWorkbookHash = ComputeSha256(workbookPath);

        var editing = await harness.ExecuteAsync(
            sessionId,
            """
            Bearbeite die bestehende Luftmengenplanung; erstelle keine zweite parallele Arbeitsmappe.

            Ergänze den Raum "Besprechungsraum Süd" mit sechzig Quadratmetern Fläche, drei Komma zwei Metern
            lichter Höhe, achtzehn Personen, fünfunddreißig Kubikmetern pro Stunde und Person, dreieinhalbfachem
            Luftwechsel und zweieinhalb Metern pro Sekunde Zielgeschwindigkeit. Führe in den Annahmen einen globalen,
            editierbaren Reservefaktor von zehn Prozent ein. Der Faktor muss über Zellbezüge in allen relevanten
            Auslegungs-, Summen- und Diagrammdaten wirken. Für den neuen Raum ergeben sich vor Reserve
            sechshundertzweiundsiebzig Kubikmeter pro Stunde und mit Reserve siebenhundertneununddreißig Komma zwei
            Kubikmeter pro Stunde; der rechnerische Rundkanaldurchmesser liegt ungefähr bei dreihundertdreiundzwanzig
            Millimetern.

            Aktualisiere Formatierung, bedingte Formatierung, Tabellenbereich, Diagramm und Druckbereich. Erzeuge außerdem
            eine separate Markdown-Auswertung mit Gesamtvolumenstrom vor und nach Reserve, Differenz, dem jeweils
            maßgebenden Kriterium und einer kurzen fachlichen Einordnung der auffälligsten Räume. Die Auswertung muss
            den Besprechungsraum Süd nachvollziehbar einbeziehen.

            Erweitere die bestehenden Tests um diese Änderung, regeneriere die Arbeitsmappe und führe danach Tests,
            Build- beziehungsweise statische Validierung, einen Laufzeit-Smoke und die abschließende Änderungsprüfung
            aus. Behebe alle durch die Anpassung verursachten Fehler selbstständig.
            """,
            "live-excel-edit",
            timeout.Token);

        CodingAgentLiveTestHarness.AssertSuccessful(editing, modelId);
        var editedWorkbookPath = FindGeneratedWorkbook(workspace);
        Assert.Equal(workbookPath, editedWorkbookPath, ignoreCase: true);
        Assert.NotEqual(initialWorkbookHash, ComputeSha256(editedWorkbookPath));

        var editedInspection = InspectWorkbook(editedWorkbookPath);
        AssertWorkbookFoundation(editedInspection);
        Assert.Contains(
            editedInspection.CellTexts,
            value => value.Contains("Besprechungsraum Süd", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            editedInspection.CellTexts,
            value => value.Contains("Reserve", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(editedInspection.CellTexts, value => NumericTextEquals(value, 60));
        Assert.Contains(editedInspection.CellTexts, value => NumericTextEquals(value, 3.2));
        Assert.Contains(editedInspection.CellTexts, value => NumericTextEquals(value, 18));
        Assert.Contains(editedInspection.CellTexts, value => NumericTextEquals(value, 35));
        Assert.Contains(editedInspection.CellTexts, value => NumericTextEquals(value, 3.5));
        Assert.Contains(editedInspection.CellTexts, value => NumericTextEquals(value, 2.5));
        Assert.Contains(
            editedInspection.Formulas,
            formula => formula.Contains("MAX(", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            editedInspection.Formulas,
            formula => formula.Contains('$')
                || formula.Contains("Reserve", StringComparison.OrdinalIgnoreCase));

        var analysisPath = Directory.EnumerateFiles(workspace, "*.md", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals("README.md", StringComparison.OrdinalIgnoreCase))
            .Select(path => new { Path = path, Content = File.ReadAllText(path) })
            .FirstOrDefault(item => item.Content.Contains("Besprechungsraum Süd", StringComparison.OrdinalIgnoreCase)
                && item.Content.Contains("Volumenstrom", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(analysisPath);
        Assert.Matches(new Regex(@"739(?:[,.]2)", RegexOptions.CultureInvariant), analysisPath.Content);
    }

    private static async Task<CodingRunObservation> ExecuteCodingRunAsync(
        GoAiClient client,
        string workspace,
        string sessionId,
        string prompt,
        CancellationToken cancellationToken)
    {
        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GO", "CodingExcelLiveTest");
        using var repositoryIndex = new WorkspaceRepositoryIndex(new GoInfrastructureOptions
        {
            DataDirectory = cacheRoot,
        });
        using var confirmation = new ToolConfirmationService(null!);
        await using var bricsCad = new BricsCadBridgeHost();
        var broker = new LocalToolBroker(
            connection: null!,
            settings: null!,
            confirmation,
            bricsCad,
            repositoryIndex,
            documents: null!);

        var index = await repositoryIndex.GetSnapshotAsync(workspace, cancellationToken);
        var descriptor = new WorkspaceDescriptor(
            Path.GetFileName(index.Root),
            index.WorkspaceFingerprint,
            index.RevisionFingerprint,
            WorkspaceRepositoryIndex.BuildRepositoryMap(index),
            index.Entries.Count,
            index.TextFileCount,
            index.TextBytes,
            index.IndexedAt,
            index.IsTruncated);
        var accepted = await client.CreateRunAsync(
            new RunRequest(
                GoAiProtocol.Version,
                RunMode.Code,
                [new RunMessage("user", [new ContentPart("text", prompt)])],
                ClientCapabilities: ["code", "filesystem", "process"],
                Limits: new RunLimits(8_192, 262_144, 14_400),
                SessionId: sessionId,
                AllowedServerTools: [],
                Workspace: descriptor,
                ConversationProfile: ConversationProfile.General,
                PreferredCodeModelId: "qwen3-coder-next"),
            $"live-excel-{Guid.NewGuid():N}",
            cancellationToken);

        var toolNames = new List<string>();
        var mutationTools = new List<string>();
        var verificationPurposes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visibleText = new StringBuilder();
        RunFailedEvent? failure = null;
        await foreach (var item in client.StreamRunEventsAsync(accepted.RunId, cancellationToken: cancellationToken))
        {
            switch (item.Type)
            {
                case RunEventTypes.ClientToolProposed:
                {
                    var proposal = item.Data.Deserialize<ToolProposal>(JsonOptions)
                        ?? throw new InvalidDataException("Der Server lieferte einen ungültigen Client-Toolvorschlag.");
                    toolNames.Add(proposal.Name);
                    Console.WriteLine($"Qwen3-Coder-Next -> {proposal.Name} {proposal.Arguments.GetRawText()}");
                    var result = await broker.ExecuteAsync(proposal, workspace, cancellationToken: cancellationToken);
                    Console.WriteLine($"GO <- {proposal.Name}: {result.Status} {result.ErrorCode} {result.Message}");
                    CodingLiveTestConsole.WriteProgramStart(proposal, result);
                    if (IsMutation(proposal.Name)
                        && string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase))
                    {
                        mutationTools.Add(proposal.Name);
                    }
                    ObserveVerification(proposal, result, verificationPurposes);
                    await client.SubmitClientToolResultAsync(accepted.RunId, result, cancellationToken);
                    break;
                }
                case RunEventTypes.TextDelta:
                    visibleText.Append(item.Data.Deserialize<TextDeltaEvent>(JsonOptions)?.Delta);
                    break;
                case RunEventTypes.RunFailed:
                    failure = item.Data.Deserialize<RunFailedEvent>(JsonOptions);
                    break;
            }
        }

        var completed = await client.GetRunAsync(accepted.RunId, cancellationToken);
        Console.WriteLine($"Run {accepted.RunId}: {completed.State}, Modell {completed.SelectedModel}, Tools {toolNames.Count}");
        Console.WriteLine(visibleText.ToString());
        return new CodingRunObservation(
            completed,
            failure,
            toolNames,
            mutationTools,
            verificationPurposes,
            visibleText.ToString());
    }

    private static void AssertSuccessfulCodingRun(
        CodingRunObservation observation,
        bool requireMutation = true)
    {
        Assert.True(
            observation.Run.State == RunState.Completed,
            $"Qwen3-Coder-Next-Lauf endete als {observation.Run.State}: "
                + $"{observation.Failure?.ErrorCode ?? observation.Run.ErrorCode} – {observation.Failure?.Message}");
        Assert.Contains("qwen3-coder-next", observation.Run.SelectedModel ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        if (requireMutation)
        {
            Assert.NotEmpty(observation.MutationTools);
        }
        Assert.Contains("test", observation.VerificationPurposes);
        Assert.Contains("build", observation.VerificationPurposes);
        Assert.Contains("start", observation.VerificationPurposes);
        Assert.Contains("review", observation.VerificationPurposes);
        Assert.False(string.IsNullOrWhiteSpace(observation.VisibleText));
        Assert.DoesNotContain("<tool_call", observation.VisibleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<function=", observation.VisibleText, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindGeneratedWorkbook(string workspace)
    {
        var candidates = Directory.EnumerateFiles(workspace, "*.xlsx", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
        return Assert.Single(candidates);
    }

    private static WorkbookInspection InspectWorkbook(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("Die XLSX-Datei enthält keinen WorkbookPart.");
        var workbook = workbookPart.Workbook
            ?? throw new InvalidDataException("Die XLSX-Datei enthält keine Arbeitsmappendefinition.");
        var sheets = workbook.Sheets?.Elements<Sheet>().ToArray() ?? [];
        var worksheetParts = workbookPart.WorksheetParts.ToArray();
        var worksheets = worksheetParts
            .Select(static part => part.Worksheet
                ?? throw new InvalidDataException("Die XLSX-Datei enthält ein leeres WorksheetPart."))
            .ToArray();
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>()
            .Select(static item => item.InnerText)
            .ToArray() ?? [];
        var cellTexts = worksheets
            .SelectMany(static worksheet => worksheet.Descendants<Cell>())
            .Select(cell => ReadCellText(cell, sharedStrings))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var formulaCells = worksheets
            .SelectMany(static worksheet => worksheet.Descendants<Cell>())
            .Where(static cell => cell.CellFormula is not null)
            .Select(static cell => new FormulaCell(
                cell.CellReference?.Value ?? string.Empty,
                cell.CellFormula?.Text ?? string.Empty))
            .Where(static cell => cell.Formula.Length > 0)
            .ToArray();
        var formulas = formulaCells.Select(static cell => cell.Formula).ToArray();
        var styleSheet = workbookPart.WorkbookStylesPart?.Stylesheet;
        return new WorkbookInspection(
            sheets.Select(static sheet => sheet.Name?.Value ?? string.Empty).ToArray(),
            cellTexts,
            formulas,
            formulaCells,
            styleSheet?.CellFormats?.ChildElements.Count ?? 0,
            styleSheet?.Fills?.ChildElements.Count ?? 0,
            worksheets.Any(static worksheet => worksheet.Descendants<Pane>()
                .Any(static pane => pane.State?.Value == PaneStateValues.Frozen)),
            worksheets.Any(static worksheet => worksheet.Descendants<AutoFilter>().Any())
                || worksheetParts.Any(static part => part.TableDefinitionParts.Any()),
            worksheets.Any(static worksheet => worksheet.Descendants<ConditionalFormatting>().Any()),
            worksheetParts.Any(static part => part.DrawingsPart?.ChartParts.Any() == true),
            worksheets.Any(static worksheet => worksheet.Descendants<MergeCells>().Any()),
            worksheets.Any(static worksheet => worksheet.Descendants<PageSetup>().Any()));
    }

    private static void AssertWorkbookFoundation(WorkbookInspection workbook)
    {
        var issues = CollectWorkbookFoundationIssues(workbook);
        Assert.True(issues.Count == 0, "XLSX-Abnahme fehlgeschlagen:\n- " + string.Join("\n- ", issues));
    }

    private static List<string> CollectWorkbookFoundationIssues(WorkbookInspection workbook)
    {
        var issues = new List<string>();
        AddIf(workbook.SheetNames.Count < 3, "Mindestens Berechnung, Übersicht und Annahmen als getrennte Tabellenblätter anlegen.");
        AddIf(!ContainsCell("Volumenstrom"), "Eine erkennbare Volumenstrom-Berechnung mit SI-Einheit fehlt.");
        AddIf(!ContainsCell("Luftwechselrate"), "Die Luftwechselrate [1/h] fehlt als separate Eingabe; der berechnete Luftwechsel darf sich nicht selbst referenzieren.");
        AddIf(!ContainsCell("Querschnitt"), "Der Auslegungsquerschnitt des Kanals fehlt als berechnete Größe.");
        AddIf(!ContainsCell("Geschwindigkeit"), "Die Zielgeschwindigkeit [m/s] fehlt als dokumentierte Eingabe oder Annahme.");
        AddIf(workbook.Formulas.Count < 24, "Zu wenige echte Excel-Formeln; abgeleitete Raum- und Summenwerte müssen referenziert werden.");
        AddIf(!workbook.Formulas.Any(formula => formula.Contains("MAX(", StringComparison.OrdinalIgnoreCase)),
            "Der maßgebende Volumenstrom muss per MAX-Formel aus den Kriterien bestimmt werden.");
        AddIf(workbook.Formulas.Any(static formula => formula.Contains(';')),
            "OOXML-Formeln verwenden invariante Komma-Trennzeichen statt lokalisierter Semikolons.");
        AddIf(workbook.FormulaCells.Any(FormulaReferencesOwnCell),
            "Mindestens eine Formel besitzt einen direkten Selbstbezug; Eingabespalte und Ergebnis müssen getrennt sein.");
        AddIf(!workbook.Formulas.Any(static formula => formula.Contains("SUM", StringComparison.OrdinalIgnoreCase)
                && formula.Contains(':')),
            "Summen müssen zusammenhängende Raumdatenbereiche mit Doppelpunkt referenzieren, nicht nur erste und letzte Zelle.");
        var hasDiameterFormula = workbook.Formulas.Any(formula =>
            formula.Contains("SQRT", StringComparison.OrdinalIgnoreCase));
        var hasHourlyToSecondConversion = workbook.Formulas.Any(formula =>
            formula.Contains("3600", StringComparison.Ordinal));
        AddIf(!hasDiameterFormula || !hasHourlyToSecondConversion,
            "Die Kanalberechnung muss m³/h durch 3600 in m³/s umrechnen und daraus den Rundkanaldurchmesser per Wurzelformel bestimmen.");
        AddIf(workbook.CellFormatCount < 6, "Zu wenige unterscheidbare Zellformate für Eingaben, Ergebnisse, Titel und Summen.");
        AddIf(workbook.FillCount < 3, "Die visuelle Farbstruktur ist nicht ausreichend differenziert.");
        AddIf(!workbook.HasFrozenPane, "Die Berechnungstabelle benötigt fixierte Kopfzeilen.");
        AddIf(!workbook.HasFilterOrTable, "Die Berechnungstabelle benötigt Filter oder eine strukturierte Tabelle.");
        AddIf(!workbook.HasConditionalFormatting, "Eine fachliche bedingte Formatierung fehlt.");
        AddIf(!workbook.HasChart, "Das geforderte Übersichtsdiagramm fehlt.");
        AddIf(!workbook.HasMergedCells, "Ein visuell abgesetzter Titelbereich fehlt.");
        AddIf(!workbook.HasPageSetup, "Das Druck- und Seitenlayout wurde nicht eingerichtet.");
        return issues;

        bool ContainsCell(string value) => workbook.CellTexts.Any(text =>
            text.Contains(value, StringComparison.OrdinalIgnoreCase));

        void AddIf(bool condition, string issue)
        {
            if (condition)
            {
                issues.Add(issue);
            }
        }
    }

    private static string ReadCellText(Cell cell, string[] sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(cell.CellValue?.Text, out var sharedStringIndex)
            && sharedStringIndex >= 0
            && sharedStringIndex < sharedStrings.Length)
        {
            return sharedStrings[sharedStringIndex];
        }
        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }
        return cell.CellValue?.Text ?? cell.InnerText;
    }

    private static bool NumericTextEquals(string value, double expected) =>
        double.TryParse(
            value.Replace(',', '.'),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
        && Math.Abs(parsed - expected) < 0.0001;

    private static bool FormulaReferencesOwnCell(FormulaCell cell)
    {
        var match = Regex.Match(cell.Reference, @"^\$?([A-Z]+)\$?([0-9]+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }
        var ownReference = @"(?<![A-Z0-9_!])\$?" + Regex.Escape(match.Groups[1].Value)
            + @"\$?" + Regex.Escape(match.Groups[2].Value) + @"(?![0-9])";
        return Regex.IsMatch(
            cell.Formula,
            ownReference,
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static async Task EnsureGitRepositoryAsync(string workspace)
    {
        if (Directory.Exists(Path.Combine(workspace, ".git")))
        {
            return;
        }
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workspace,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("init");
        Assert.True(process.Start(), "Der Excel-Testworkspace konnte nicht als lokales Git-Repository initialisiert werden.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git init ist fehlgeschlagen: {output}\n{error}");
    }

    private static bool IsMutation(string name) => name is
        ClientToolNames.FileSystemWriteText or
        ClientToolNames.FileSystemReplaceText or
        ClientToolNames.FileSystemMove or
        ClientToolNames.FileSystemProposePatch or
        ClientToolNames.FileSystemProposeCreate or
        ClientToolNames.FileSystemProposeDelete;

    private static void ObserveVerification(
        ToolProposal proposal,
        ClientToolResult result,
        HashSet<string> purposes)
    {
        if (!string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(result.ErrorCode)
            || result.Result.ValueKind != JsonValueKind.Object
            || !result.Result.TryGetProperty("exitCode", out var exitCode)
            || !exitCode.TryGetInt32(out var code)
            || code != 0)
        {
            return;
        }

        if (proposal.Name == ClientToolNames.ProcessRun
            && proposal.Arguments.TryGetProperty("purpose", out var purpose)
            && purpose.ValueKind == JsonValueKind.String
            && purpose.GetString() is { Length: > 0 } value)
        {
            var executable = proposal.Arguments.TryGetProperty("executable", out var executableValue)
                ? Path.GetFileName(executableValue.GetString() ?? string.Empty)
                : string.Empty;
            var commandArguments = proposal.Arguments.TryGetProperty("arguments", out var argumentValue)
                && argumentValue.ValueKind == JsonValueKind.Array
                    ? argumentValue.EnumerateArray()
                        .Where(static item => item.ValueKind == JsonValueKind.String)
                        .Select(static item => item.GetString() ?? string.Empty)
                        .ToArray()
                    : [];
            var isSmoke = proposal.Arguments.TryGetProperty("startMode", out var startMode)
                && string.Equals(startMode.GetString(), "smoke", StringComparison.OrdinalIgnoreCase);
            if (value.Equals("test", StringComparison.OrdinalIgnoreCase)
                && IsVerificationCommand(executable, commandArguments, TestCommands))
            {
                purposes.Add("test");
            }
            else if (value.Equals("build", StringComparison.OrdinalIgnoreCase)
                && (IsVerificationCommand(executable, commandArguments, BuildCommands)
                    || commandArguments.Any(IsGeneratorOrBuildScript)))
            {
                purposes.Add("build");
            }
            else if (value.Equals("start", StringComparison.OrdinalIgnoreCase) && isSmoke)
            {
                purposes.Add("start");
            }
        }
        else if (proposal.Name == ClientToolNames.ProcessRunPreset
            && proposal.Arguments.TryGetProperty("preset", out var preset)
            && preset.ValueKind == JsonValueKind.String)
        {
            switch (preset.GetString())
            {
                case "repository.verify":
                    purposes.UnionWith(["test", "build", "start"]);
                    break;
                case "repository.build":
                case "dotnet.build":
                    purposes.Add("build");
                    break;
                case "dotnet.test":
                case "code.test":
                    purposes.Add("test");
                    break;
                case "repository.start":
                case "code.run":
                    purposes.Add("start");
                    break;
                case "git.diff":
                    purposes.Add("review");
                    break;
            }
        }
    }

    private static readonly string[] TestCommands = ["test", "pytest", "ctest", "unittest"];
    private static readonly string[] BuildCommands =
        ["build", "publish", "package", "pack", "compile", "compileall", "check", "assemble", "dist", "bundle", "--build"];

    private static bool IsVerificationCommand(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> commandMarkers)
    {
        if (commandMarkers.Any(marker => executable.Equals(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return arguments.Any(argument => commandMarkers.Contains(argument, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsGeneratorOrBuildScript(string argument)
    {
        var name = Path.GetFileNameWithoutExtension(argument);
        return name.StartsWith("generate", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("build", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("package", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("compile", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CodingRunObservation(
        RunSnapshot Run,
        RunFailedEvent? Failure,
        IReadOnlyList<string> ToolNames,
        IReadOnlyList<string> MutationTools,
        IReadOnlySet<string> VerificationPurposes,
        string VisibleText);

    private sealed record WorkbookInspection(
        IReadOnlyList<string> SheetNames,
        IReadOnlyList<string> CellTexts,
        IReadOnlyList<string> Formulas,
        IReadOnlyList<FormulaCell> FormulaCells,
        int CellFormatCount,
        int FillCount,
        bool HasFrozenPane,
        bool HasFilterOrTable,
        bool HasConditionalFormatting,
        bool HasChart,
        bool HasMergedCells,
        bool HasPageSetup);

    private sealed record FormulaCell(string Reference, string Formula);
}
