using GoAi.Client;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.BricsCad.Protocol;
using GoWinUI.Infrastructure;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class QwenCoderPhysicsLiveTests
{
    private const string WorkspaceEnvironmentVariable = "GO_AI_LIVE_PHYSICS_WORKSPACE";
    private const string ContinueEnvironmentVariable = "GO_AI_LIVE_PHYSICS_CONTINUE";
    private const string ModelEnvironmentVariable = "GO_AI_LIVE_CODING_MODEL";
    private static readonly JsonSerializerOptions JsonOptions = GoAiProtocol.CreateJsonOptions();
    private static readonly double[] ExpectedHarmonicEnergies = [0.5, 1.5, 2.5, 3.5, 4.5, 5.5];
    private static readonly double[] ExpectedFirstOrderEnergies = [0.5075, 1.5375, 2.5975, 3.6875];

    [Fact]
    [Trait("Category", "Live")]
    public async Task QwenCoderCanImplementAndVerifyAnalyticalAndNumericalQuantumMechanics()
    {
        var requestedWorkspace = Environment.GetEnvironmentVariable(WorkspaceEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(requestedWorkspace))
        {
            // Der echte Modelllauf wird ausschließlich mit einem explizit dafür
            // freigegebenen, leeren Testworkspace aktiviert.
            return;
        }

        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedWorkspace));
        Assert.True(Directory.Exists(workspace), $"Physik-Live-Workspace fehlt: {workspace}");
        var continueExisting = string.Equals(
            Environment.GetEnvironmentVariable(ContinueEnvironmentVariable),
            "1",
            StringComparison.OrdinalIgnoreCase);
        if (!continueExisting)
        {
            AssertWorkspaceIsEmpty(workspace);
        }
        await EnsureGitRepositoryAsync(workspace);
        var modelId = Environment.GetEnvironmentVariable(ModelEnvironmentVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelId = "qwen3-coder-next";
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(8));
        var sessionId = $"live-physics-{Guid.NewGuid():N}";
        await using var harness = await CodingAgentLiveTestHarness.CreateAsync(
            "quantum-physics",
            workspace,
            modelId,
            sessionId,
            timeout.Token);
        if (!continueExisting)
        {
            var creation = await harness.ExecuteAsync(
                sessionId,
                """
            Entwickle in diesem leeren Workspace ein reproduzierbares Python-Projekt für eine anspruchsvolle
            theoretisch-physikalische Berechnung. Untersuche den dimensionslosen eindimensionalen quantenmechanischen
            harmonischen Oszillator und zusätzlich die schwache quartische Störung
            H = -1/2 d²/dx² + 1/2 x² + Lambda x⁴ mit hbar = m = omega = 1 und Lambda = 0,01.

            Die analytische Seite muss für mindestens n = 0 bis 5 die exakten harmonischen Energien
            E_n = n + 1/2 und für n = 0 bis 3 die Störungstheorie erster Ordnung
            E_n^(1) = n + 1/2 + 3 Lambda/4 (2 n² + 2 n + 1) nachvollziehbar herleiten.

            Löse dieselbe stationäre Schrödingergleichung unabhängig davon numerisch. Numerische Energien dürfen nicht
            aus den analytischen Formeln übernommen oder nachträglich auf Sollwerte gesetzt werden. Verwende eine
            fachlich geeignete Diskretisierung beziehungsweise ein Shooting-/Numerov- oder Eigenwertverfahren auf
            einem ausreichend großen endlichen Gebiet. Prüfe mindestens:

            - Fehler der ersten sechs harmonischen Eigenenergien,
            - Normierung, Orthogonalität und Parität der Eigenfunktionen,
            - den Virialsatz für die harmonischen Zustände,
            - die ersten vier anharmonischen Energien gegen die Störungstheorie erster Ordnung,
            - echte Gitterkonvergenz mit mindestens einer groben und einer feineren Auflösung.

            Lege folgende stabile Artefakte an:

            - physics_solver.py mit CLI und wiederverwendbaren Rechenfunktionen,
            - test_physics_solver.py mit automatisierten analytischen und numerischen Tests,
            - physics_results.json als maschinenlesbaren Ergebnisbericht,
            - physics_analysis.md mit Gleichungen, Methode, Fehlern, Konvergenz und fachlicher Einordnung.

            physics_results.json muss exakt die folgenden obersten Felder besitzen:
            schemaVersion, system, harmonicLevels, orthogonalityMaxError, virialRelativeError,
            anharmonicLevels und convergence. system enthält hbar, mass, omega, quarticLambda, domain und gridPoints.
            Jeder Eintrag in harmonicLevels enthält n, analyticEnergy, numericEnergy, absoluteError,
            normalizationError, residualNorm und parityError. Jeder Eintrag in anharmonicLevels enthält n,
            firstOrderEnergy, numericEnergy und absoluteDifference. convergence enthält coarseGridPoints,
            fineGridPoints, coarseGroundStateError und fineGroundStateError.

            Nutze SI-freie dimensionslose Größen konsistent und dokumentiere, wie die Resultate auf physikalische
            Einheiten zurückskaliert werden. Verwende keine hardcodierten numerischen Ergebniswerte als Ersatz für die
            Berechnung. Externe Bibliotheken sind erlaubt, müssen aber reproduzierbar in requirements.txt festgelegt
            und ausschließlich in einer lokalen virtuellen Umgebung installiert werden. Prüfe zuerst `py -0p`, verwende auf
            diesem Host Python 3.11 für `.venv` und rufe Installation, Tests und Solver danach nur über
            `.venv\\Scripts\\python.exe` auf. Verändere keine globalen oder benutzerweiten Python-Pakete. Halte generierte
            Umgebungen und Caches aus Git.

            Führe die Ergebnisgenerierung, alle Tests, eine Syntax-/Buildprüfung, einen begrenzten CLI-Laufzeit-Smoke
            und abschließend die Änderungsprüfung aus. Behebe alle dabei gefundenen Fehler selbstständig. Der Lauf ist
            erst abgeschlossen, wenn analytische und numerische Resultate innerhalb begründeter Toleranzen
            übereinstimmen und die vier Ergebnisartefakte vorhanden sind.
            """,
                "live-physics-create",
                timeout.Token);

            CodingAgentLiveTestHarness.AssertSuccessful(creation, modelId);
        }
        var issues = CollectPhysicsIssues(workspace);
        if (issues.Count > 0)
        {
            var audit = await harness.ExecuteAsync(
                sessionId,
                """
                Eine unabhängige Abnahme der theoretisch-physikalischen Implementierung hat folgende konkrete Mängel
                gefunden. Behebe die Ursachen in der Implementierung, den Tests, der Dokumentation und dem bestehenden
                Ergebnisbericht. Schwäche keine Prüfungen und ersetze keine numerische Rechnung durch hardcodierte
                analytische Sollwerte.

                """ + string.Join(Environment.NewLine, issues.Select(static issue => "- " + issue)) + """


                Erzeuge physics_results.json danach vollständig neu. Führe alle Tests, die Syntax-/Buildprüfung,
                einen begrenzten CLI-Smoke und die abschließende Änderungsprüfung erneut aus. Behebe Folgefehler
                selbstständig und berichte die tatsächlich erreichten numerischen Fehler und Konvergenzwerte.
                """,
                "live-physics-audit",
                timeout.Token);
            CodingAgentLiveTestHarness.AssertSuccessful(audit, modelId);
        }

        var finalIssues = CollectPhysicsIssues(workspace);
        Assert.True(
            finalIssues.Count == 0,
            "Physik-Abnahme fehlgeschlagen:\n- " + string.Join("\n- ", finalIssues));
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
            "GO", "CodingPhysicsLiveTest");
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
            $"live-physics-{Guid.NewGuid():N}",
            cancellationToken);
        Console.WriteLine($"Coding run accepted: {accepted.RunId}");

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

    private static void AssertSuccessfulCodingRun(CodingRunObservation observation)
    {
        Assert.True(
            observation.Run.State == RunState.Completed,
            $"Qwen3-Coder-Next-Lauf endete als {observation.Run.State}: "
                + $"{observation.Failure?.ErrorCode ?? observation.Run.ErrorCode} – {observation.Failure?.Message}");
        Assert.Contains("qwen3-coder-next", observation.Run.SelectedModel ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(observation.MutationTools);
        Assert.Contains("test", observation.VerificationPurposes);
        Assert.Contains("build", observation.VerificationPurposes);
        Assert.Contains("start", observation.VerificationPurposes);
        Assert.Contains("review", observation.VerificationPurposes);
        Assert.False(string.IsNullOrWhiteSpace(observation.VisibleText));
        Assert.DoesNotContain("<tool_call", observation.VisibleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<function=", observation.VisibleText, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> CollectPhysicsIssues(string workspace)
    {
        var issues = new List<string>();
        var solverPath = Path.Combine(workspace, "physics_solver.py");
        var testsPath = Path.Combine(workspace, "test_physics_solver.py");
        var resultsPath = Path.Combine(workspace, "physics_results.json");
        var analysisPath = Path.Combine(workspace, "physics_analysis.md");
        RequireFile(solverPath, "physics_solver.py fehlt.");
        RequireFile(testsPath, "test_physics_solver.py fehlt.");
        RequireFile(resultsPath, "physics_results.json fehlt.");
        RequireFile(analysisPath, "physics_analysis.md fehlt.");

        if (File.Exists(solverPath))
        {
            var source = File.ReadAllText(solverPath);
            AddIf(source.Length < 3_000, "Die numerische Implementierung ist auffällig kurz und nicht nachvollziehbar vollständig.");
            AddIf(!ContainsAny(source, "numerov", "eigh", "eigen", "hamilton", "schrod", "finite_difference", "finite difference"),
                "Im Solver ist kein erkennbares unabhängiges Schrödinger-Eigenwertverfahren implementiert.");
            AddIf(!source.Contains("quart", StringComparison.OrdinalIgnoreCase),
                "Die quartische Störung ist im Solver nicht erkennbar implementiert.");
        }
        if (File.Exists(testsPath))
        {
            var tests = File.ReadAllText(testsPath);
            AddIf(tests.Length < 1_000, "Die automatisierten Physiktests sind nicht umfassend genug.");
            AddIf(!ContainsAny(tests, "orthogon", "virial", "parity", "convergence"),
                "Die Tests decken Orthogonalität, Virialsatz, Parität oder Konvergenz nicht erkennbar ab.");
        }
        if (File.Exists(analysisPath))
        {
            var analysis = File.ReadAllText(analysisPath);
            AddIf(analysis.Length < 1_200, "Die fachliche Auswertung ist zu knapp.");
            AddIf(!ContainsAny(analysis, "Schrödinger", "Schroedinger"),
                "Die Auswertung erläutert die Schrödingergleichung nicht.");
            AddIf(!ContainsAny(analysis, "analytisch", "analytical")
                    || !ContainsAny(analysis, "numerisch", "numerical")
                    || !ContainsAny(analysis, "Konvergenz", "convergence"),
                "Die Auswertung vergleicht analytische und numerische Resultate beziehungsweise Konvergenz nicht ausreichend.");
        }

        if (!File.Exists(resultsPath))
        {
            return issues;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(resultsPath));
            var root = document.RootElement;
            AddIf(root.ValueKind != JsonValueKind.Object, "physics_results.json muss ein JSON-Objekt enthalten.");
            if (root.ValueKind != JsonValueKind.Object)
            {
                return issues;
            }

            CheckNumber(root, "schemaVersion", 1, 0);
            if (TryObject(root, "system", out var system))
            {
                CheckNumber(system, "hbar", 1, 1e-12);
                CheckNumber(system, "mass", 1, 1e-12);
                CheckNumber(system, "omega", 1, 1e-12);
                CheckNumber(system, "quarticLambda", 0.01, 1e-12);
                if (!TryNumber(system, "gridPoints", out var gridPoints) || gridPoints < 100)
                {
                    issues.Add("system.gridPoints muss eine ausreichend feine numerische Diskretisierung beschreiben.");
                }
                if (!system.TryGetProperty("domain", out var domain)
                    || domain.ValueKind != JsonValueKind.Array
                    || domain.GetArrayLength() != 2
                    || !TryElementNumber(domain[0], out var lower)
                    || !TryElementNumber(domain[1], out var upper)
                    || lower > -5
                    || upper < 5)
                {
                    issues.Add("system.domain muss ein symmetrisches, ausreichend großes Rechengebiet abdecken.");
                }
            }
            else
            {
                issues.Add("Das Objekt system fehlt.");
            }

            CheckHarmonicLevels(root);
            CheckAnharmonicLevels(root);
            CheckUpperBound(root, "orthogonalityMaxError", 0.02);
            CheckUpperBound(root, "virialRelativeError", 0.05);
            CheckConvergence(root);
        }
        catch (JsonException exception)
        {
            issues.Add($"physics_results.json ist ungültig: {exception.Message}");
        }

        return issues;

        void RequireFile(string path, string message)
        {
            if (!File.Exists(path))
            {
                issues.Add(message);
            }
        }

        void AddIf(bool condition, string message)
        {
            if (condition)
            {
                issues.Add(message);
            }
        }

        void CheckNumber(JsonElement owner, string name, double expected, double tolerance)
        {
            if (!TryNumber(owner, name, out var value) || Math.Abs(value - expected) > tolerance)
            {
                issues.Add($"{name} muss {expected.ToString(CultureInfo.InvariantCulture)} sein.");
            }
        }

        void CheckUpperBound(JsonElement owner, string name, double maximum)
        {
            if (!TryNumber(owner, name, out var value) || value < 0 || value > maximum)
            {
                issues.Add($"{name} muss numerisch bestimmt und höchstens {maximum.ToString(CultureInfo.InvariantCulture)} sein.");
            }
        }

        void CheckHarmonicLevels(JsonElement owner)
        {
            if (!TryArray(owner, "harmonicLevels", out var levels) || levels.GetArrayLength() < ExpectedHarmonicEnergies.Length)
            {
                issues.Add("harmonicLevels muss mindestens die Zustände n = 0 bis 5 enthalten.");
                return;
            }
            for (var n = 0; n < ExpectedHarmonicEnergies.Length; n++)
            {
                if (!TryFindLevel(levels, n, out var level))
                {
                    issues.Add($"Der harmonische Zustand n = {n} fehlt.");
                    continue;
                }
                CheckLevelNumber(level, "analyticEnergy", ExpectedHarmonicEnergies[n], 1e-10, $"harmonicLevels[{n}].analyticEnergy");
                if (!TryNumber(level, "numericEnergy", out var numericEnergy)
                    || Math.Abs(numericEnergy - ExpectedHarmonicEnergies[n]) > 0.03)
                {
                    issues.Add($"Die unabhängig numerische Energie für n = {n} weicht um mehr als 0,03 vom analytischen Wert ab.");
                }
                CheckLevelUpperBound(level, "absoluteError", 0.03, n);
                CheckLevelUpperBound(level, "normalizationError", 0.005, n);
                CheckLevelUpperBound(level, "residualNorm", 0.2, n);
                CheckLevelUpperBound(level, "parityError", 0.05, n);
            }
        }

        void CheckAnharmonicLevels(JsonElement owner)
        {
            if (!TryArray(owner, "anharmonicLevels", out var levels) || levels.GetArrayLength() < ExpectedFirstOrderEnergies.Length)
            {
                issues.Add("anharmonicLevels muss mindestens die Zustände n = 0 bis 3 enthalten.");
                return;
            }
            for (var n = 0; n < ExpectedFirstOrderEnergies.Length; n++)
            {
                if (!TryFindLevel(levels, n, out var level))
                {
                    issues.Add($"Der anharmonische Zustand n = {n} fehlt.");
                    continue;
                }
                CheckLevelNumber(level, "firstOrderEnergy", ExpectedFirstOrderEnergies[n], 1e-10, $"anharmonicLevels[{n}].firstOrderEnergy");
                if (!TryNumber(level, "numericEnergy", out var numericEnergy)
                    || Math.Abs(numericEnergy - ExpectedFirstOrderEnergies[n]) > 0.05)
                {
                    issues.Add($"Die numerische anharmonische Energie für n = {n} stimmt nicht ausreichend mit der Störungstheorie überein.");
                }
                CheckLevelUpperBound(level, "absoluteDifference", 0.05, n);
            }
        }

        void CheckConvergence(JsonElement owner)
        {
            if (!TryObject(owner, "convergence", out var convergence))
            {
                issues.Add("Das Objekt convergence fehlt.");
                return;
            }
            if (!TryNumber(convergence, "coarseGridPoints", out var coarsePoints)
                || !TryNumber(convergence, "fineGridPoints", out var finePoints)
                || finePoints <= coarsePoints)
            {
                issues.Add("convergence muss eine feinere Gitterauflösung als coarseGridPoints nachweisen.");
            }
            if (!TryNumber(convergence, "coarseGroundStateError", out var coarseError)
                || !TryNumber(convergence, "fineGroundStateError", out var fineError)
                || coarseError <= 0
                || fineError < 0
                || fineError >= coarseError
                || fineError > 0.02)
            {
                issues.Add("Der Grundzustandsfehler muss auf dem feineren Gitter kleiner werden und unter 0,02 liegen.");
            }
        }

        void CheckLevelNumber(JsonElement level, string name, double expected, double tolerance, string label)
        {
            if (!TryNumber(level, name, out var value) || Math.Abs(value - expected) > tolerance)
            {
                issues.Add($"{label} besitzt nicht den analytisch erwarteten Wert {expected.ToString(CultureInfo.InvariantCulture)}.");
            }
        }

        void CheckLevelUpperBound(JsonElement level, string name, double maximum, int n)
        {
            if (!TryNumber(level, name, out var value) || value < 0 || value > maximum)
            {
                issues.Add($"{name} für n = {n} muss höchstens {maximum.ToString(CultureInfo.InvariantCulture)} sein.");
            }
        }
    }

    private static bool TryObject(JsonElement owner, string name, out JsonElement value)
    {
        if (owner.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }
        value = default;
        return false;
    }

    private static bool TryArray(JsonElement owner, string name, out JsonElement value)
    {
        if (owner.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Array)
        {
            return true;
        }
        value = default;
        return false;
    }

    private static bool TryNumber(JsonElement owner, string name, out double value)
    {
        if (owner.TryGetProperty(name, out var element) && TryElementNumber(element, out value))
        {
            return true;
        }
        value = default;
        return false;
    }

    private static bool TryElementNumber(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value)
            && double.IsFinite(value))
        {
            return true;
        }
        value = default;
        return false;
    }

    private static bool TryFindLevel(JsonElement levels, int n, out JsonElement level)
    {
        foreach (var candidate in levels.EnumerateArray())
        {
            if (candidate.ValueKind == JsonValueKind.Object
                && candidate.TryGetProperty("n", out var number)
                && number.TryGetInt32(out var value)
                && value == n)
            {
                level = candidate;
                return true;
            }
        }
        level = default;
        return false;
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static void AssertWorkspaceIsEmpty(string workspace)
    {
        var unexpected = Directory.EnumerateFileSystemEntries(workspace, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(
            unexpected.Length == 0,
            "Der Physik-Live-Test verlangt einen leeren Workspace. Vorhanden: "
                + string.Join(", ", unexpected.Select(Path.GetFileName)));
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
        Assert.True(process.Start(), "Der Physik-Testworkspace konnte nicht als lokales Git-Repository initialisiert werden.");
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
}
