using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace GoWinUI.Tests;

/// <summary>
/// Fortsetzbare Coding-Agent-Kampagne zu Einsteins Feldgleichungen und physikalisch
/// begründeten Erweiterungen. Der Dauermodus wird bewusst über eine Umgebungsvariable
/// aktiviert und läuft bis Ctrl+C. Das Modelltraining selbst wird nicht verändert;
/// Erkenntnisse bleiben über Quellcode, Herleitungen, Versuchshistorie, Tests und
/// Ergebnisartefakte persistent.
/// </summary>
public sealed class QwenCoderEinsteinTests
{
    private const string WorkspaceEnvironmentVariable = "GO_AI_LIVE_EINSTEIN_WORKSPACE";
    private const string IterationsEnvironmentVariable = "GO_AI_LIVE_EINSTEIN_ITERATIONS";
    private const string ContinuousEnvironmentVariable = "GO_AI_LIVE_EINSTEIN_CONTINUOUS";
    private const string ModelEnvironmentVariable = "GO_AI_LIVE_CODING_MODEL";

    private static readonly string[] Challenges =
    [
        "Minkowski-Raumzeit, linearisierte Gravitation und der kontrollierte Newtonsche Grenzfall",
        "Schwarzschild-Geometrie mit Krümmungsinvarianten, Geodäten, Horizontfläche und Oberflächengravitation",
        "Kerr- und Kerr-Newman-Geometrie mit Ergosphäre, Horizonten, Drehimpuls und ausgewählten Geodäten",
        "de-Sitter- und Anti-de-Sitter-Raumzeit mit kosmologischer Konstante, Kausalstruktur und bekannten Grenzfällen",
        "FLRW-Kosmologie mit physikalisch zulässiger Materie, Strahlung und dunkler Energie sowie numerischer Skalenfaktorentwicklung",
        "TOV-Gleichung mit kausaler, thermodynamisch stabiler Zustandsgleichung und Masse-Radius-Beziehung",
        "Quantenfeldtheorie in gekrümmter Raumzeit: skalares Feld auf Schwarzschild- oder FLRW-Hintergrund mit Modengleichung und Erhaltungssätzen",
        "semiklassische Einstein-Gleichung mit renormiertem Erwartungswert des Energie-Impuls-Tensors und klar dokumentiertem Näherungsbereich",
        "Thermodynamik schwarzer Löcher mit Hawking-Temperatur, Bekenstein-Hawking-Entropie, Horizontfläche und erstem Hauptsatz",
        "Niederenergie-Grenze der Stringtheorie als Einstein-Dilaton- oder Einstein-Maxwell-Dilaton-Modell einschließlich klassischem Grenzfall",
        "höhere Krümmungskorrekturen aus effektiver Stringtheorie, etwa Gauss-Bonnet in geeigneter Dimension, mit korrekter vierdimensionaler Topologiegrenze",
        "AdS-Schwarzschild- oder AdS-Black-Brane-Modell mit holografischer Thermodynamik und nachvollziehbarer Gültigkeitsdomäne",
        "Regge-Wheeler- oder Zerilli-Störungen eines schwarzen Lochs mit Potential, Stabilitätsprüfung und numerischer Modensimulation",
        "Bianchi-I-Kosmologie mit physikalisch zulässiger anisotroper Expansion, Zwangsbedingungen und numerischer Entwicklung",
    ];

    [Fact]
    [Trait("Category", "Live")]
    public async Task QwenCoderContinuouslyStudiesEinsteinFieldEquationsAndQuantumGravityModels()
    {
        var requestedWorkspace = Environment.GetEnvironmentVariable(WorkspaceEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(requestedWorkspace))
        {
            return;
        }

        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedWorkspace));
        Assert.True(Directory.Exists(workspace), $"Einstein-Live-Workspace fehlt: {workspace}");
        await EnsureGitRepositoryAsync(workspace);
        var modelId = Environment.GetEnvironmentVariable(ModelEnvironmentVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelId = "qwen3-coder-next";
        }
        var continuous = string.Equals(
            Environment.GetEnvironmentVariable(ContinuousEnvironmentVariable),
            "1",
            StringComparison.OrdinalIgnoreCase);
        var iterationLimit = ParseIterationLimit(
            Environment.GetEnvironmentVariable(IterationsEnvironmentVariable),
            continuous);
        using var timeout = new CancellationTokenSource(continuous ? TimeSpan.FromDays(30) : TimeSpan.FromHours(12));
        var sessionId = $"live-einstein-{Guid.NewGuid():N}";
        await using var harness = await CodingAgentLiveTestHarness.CreateAsync(
            "einstein-field-equations",
            workspace,
            modelId,
            sessionId,
            timeout.Token);
        await using var dashboard = CodingArtifactLiveDashboard.Start(
            workspace,
            harness.Record,
            timeout.Token);
        harness.Record("campaign.configuration", new
        {
            continuous,
            iterationLimit,
            challengeCount = Challenges.Length,
            liveDashboard = dashboard.Url.AbsoluteUri,
        });

        if (!HasFoundation(workspace))
        {
            var bootstrap = await harness.ExecuteAsync(
                sessionId,
                BuildBootstrapPrompt(),
                "live-einstein-bootstrap",
                timeout.Token);
            CodingAgentLiveTestHarness.AssertSuccessful(bootstrap, modelId);
        }

        var startingAttempt = ReadAttemptCount(workspace);
        for (var localIteration = 0; localIteration < iterationLimit; localIteration++)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var absoluteIteration = startingAttempt + localIteration;
            var challenge = Challenges[absoluteIteration % Challenges.Length];
            harness.Record("iteration.started", new { absoluteIteration, challenge });
            var observation = await harness.ExecuteAsync(
                sessionId,
                BuildIterationPrompt(absoluteIteration, challenge),
                $"live-einstein-{absoluteIteration}",
                timeout.Token);
            CodingAgentLiveTestHarness.AssertSuccessful(observation, modelId);

            var issues = CollectEinsteinIssues(workspace);
            harness.Record("iteration.validation", new
            {
                absoluteIteration,
                passed = issues.Count == 0,
                issues,
            });
            if (issues.Count > 0)
            {
                var correction = await harness.ExecuteAsync(
                    sessionId,
                    BuildCorrectionPrompt(absoluteIteration, issues),
                    $"live-einstein-correction-{absoluteIteration}",
                    timeout.Token);
                CodingAgentLiveTestHarness.AssertSuccessful(correction, modelId);
                issues = CollectEinsteinIssues(workspace);
                harness.Record("iteration.revalidation", new
                {
                    absoluteIteration,
                    passed = issues.Count == 0,
                    issues,
                });
            }

            if (!continuous)
            {
                Assert.True(
                    issues.Count == 0,
                    "Einstein-Abnahme fehlgeschlagen:\n- " + string.Join("\n- ", issues)
                    + $"\nLive-Protokoll: {harness.LogPath}");
            }
        }
    }

    [Fact]
    public void VerifiedResidualEvidenceRequiresMultipleRegularPointsAndSmallBianchiResidual()
    {
        using var document = JsonDocument.Parse("""
            {
              "classification": "verified",
              "verificationMethod": "python -m pytest",
              "maxEinsteinResidual": 1e-9,
              "maxBianchiResidual": 1e-2,
              "residualSamples": [
                { "einsteinResidual": 1e-9, "bianchiResidual": 1e-2 },
                {
                  "evaluationPoint": { "r": 8.0, "theta": 1.57079632679 },
                  "einsteinResidual": 1e-9,
                  "bianchiResidual": 1e-2
                }
              ]
            }
            """);
        var issues = new List<string>();

        ValidateResidualEvidence(document.RootElement, "candidate", issues);

        Assert.Contains(issues, static issue => issue.Contains("regulären evaluationPoint", StringComparison.Ordinal));
        Assert.Contains(issues, static issue => issue.Contains("mindestens zwei gültige Residuen", StringComparison.Ordinal));
        Assert.Contains(issues, static issue => issue.Contains("zu großes Bianchi-Residual", StringComparison.Ordinal));
    }

    private static string BuildBootstrapPrompt() => """
        Erstelle in diesem Workspace ein fortsetzbares, wissenschaftlich nachvollziehbares Python-Projekt zur
        symbolischen und numerischen Untersuchung der Einsteinschen Feldgleichungen

            G_{mu nu} + Lambda g_{mu nu} = 8 pi T_{mu nu}

        in geometrisierten Einheiten. Nutze vorzugsweise SymPy, NumPy, SciPy und Matplotlib mit einem headless
        Backend. Die Architektur muss neue Metriken, Koordinaten, Quellen und Annahmen aufnehmen können. Berechne
        Metrik und inverse Metrik, Christoffelsymbole, Riemann-, Ricci- und Einstein-Tensor, Ricci- und
        Kretschmann-Skalar sowie kovariante Divergenzen. Symbolische Aussagen sind durch unabhängige numerische
        Residuen an regulären Stichprobenpunkten zu prüfen.

        Lege mindestens diese stabilen Artefakte an:

        - einstein_engine.py mit CLI und wiederverwendbaren Tensor-/Simulationsfunktionen,
        - test_einstein_engine.py mit analytischen und numerischen Regressionstests,
        - einstein_cases.json mit schemaVersion und einer cases-Liste,
        - einstein_attempts.json mit schemaVersion und einer fortschreibbaren attempts-Liste,
        - einstein_analysis.md als fachliches Forschungsprotokoll,
        - visualize_einstein.py als reproduzierbarer Plot- und Simulationsgenerator,
        - solutions/ mit einem ausführlichen Markdown-Lösungsdokument je verifiziertem Fall,
        - visualizations/ mit erzeugten PNG- oder SVG-Grafiken,
        - simulation_data/ mit den zugehörigen maschinenlesbaren Daten,
        - simulation_data/live_progress.json als atomar aktualisierten Live-Fortschritt.

        Lange symbolische oder numerische Rechnungen müssen beobachtbar bleiben. Aktualisiere live_progress.json bei
        Beginn, bei jedem fachlich sinnvollen Zwischenstand und beim Abschluss. Das Objekt enthält mindestens status,
        caseId, phase, step, totalSteps, updatedAt und metrics. status ist running, completed oder failed. Schreibe die
        Datei atomar über eine temporäre Datei und Umbenennen, damit die Liveansicht nie halbes JSON liest. Aktualisiere
        während iterativer Simulationen zusätzlich visualizations/live_progress.png oder .svg mit den bisher
        berechneten Punkten. Verwende ein headless Backend für die Erzeugung; GO öffnet und aktualisiert die Dateien
        über eine separate Liveansicht. Berechnungen dürfen dafür nicht künstlich verlangsamt werden.

        Jeder Fall in einstein_cases.json enthält mindestens id, title, theoryDomain, approximationLevel,
        classification, equations, assumptions, validityDomain, maxEinsteinResidual, maxBianchiResidual,
        residualSamples, verificationMethod, independentChecks, conclusion, visualizations und simulationData.
        residualSamples enthält die tatsächlich berechneten, endlichen Einstein- und Bianchi-Residuen. Jede Stichprobe
        besitzt ein nicht leeres `evaluationPoint`-Objekt mit endlichen numerischen Koordinaten und Parametern. Verified-Fälle
        benötigen mindestens zwei reguläre Stichprobenpunkte; die beiden Maximalfelder werden daraus berechnet und sind keine
        vorgegebenen Toleranzen.
        classification ist exakt verified, approximation oder
        undetermined. approximationLevel kennzeichnet nachvollziehbar exact, effective, semiclassical, perturbative
        oder numerical. Verwende ausschließlich physikalisch etablierte Gleichungen, konsistente Quellen und offen
        dokumentierte Näherungen. Erfinde keine Messdaten, Randbedingungen, Energie-Impuls-Tensoren oder Lösungen.
        Ein Resultat darf nur verified heißen, wenn Gleichungen, Erhaltungssätze, Dimensionsanalyse, bekannte
        Grenzfälle und unabhängige numerische Residuen übereinstimmen.

        Sobald ein Fall diese Verifikation erfüllt, setze in seinem JSON-Eintrag `solutionDocument` auf einen
        relativen Pfad wie `solutions/schwarzschild.md` und verfasse dort die vollständige Lösung. Das Dokument muss
        Problemstellung, physikalisches Modell, sämtliche Annahmen, Gültigkeitsbereich, schrittweise mathematische
        Herleitung, umgeformte Feldgleichungen, Rand- und Anfangsbedingungen, analytische beziehungsweise numerische
        Lösung, unabhängige Prüfungen, Residuen und Fehlerschranken, Interpretation, Grenzen sowie exakt ausführbare
        Reproduktionsschritte enthalten. Verweise auf zugehörige Simulationsdaten und Grafiken. Erzeuge kein
        Lösungsdokument für ein bloß vermutetes oder noch undetermined Resultat und erkläre eine kontrollierte
        Näherung niemals stillschweigend zur exakten Lösung.

        Beginne mit Minkowski und Schwarzschild als exakten Referenzen und ergänze danach ein erstes gültiges Modell
        aus Quantenfeldtheorie in gekrümmter Raumzeit, schwarzer-Loch-Thermodynamik oder der Niederenergie-Grenze der
        Stringtheorie. Behandle offene Quantengravitationsfragen nicht als bereits gelöst, sondern rechne jeweils eine
        klar definierte effektive, semiklassische oder perturbative Gleichung innerhalb ihres Gültigkeitsbereichs
        durch. Erzeuge fachlich passende Grafiken oder Simulationen. Du entscheidest selbst, welche Darstellung den
        Erkenntnisgewinn am besten vermittelt, und darfst Generator, Datenformat und Visualisierung in späteren
        Iterationen verändern. Plots müssen aus gespeicherten Daten reproduzierbar sein und Achsen, Einheiten
        beziehungsweise dimensionslose Größen, Legende, Fall-ID und Gültigkeitsbereich nennen.

        Richte die lokale Python-3.11-Umgebung nach `py -0p` exakt mit `py -3.11 -m venv .venv` ein. Verwende den dort
        angezeigten absoluten Pythonpfad nicht als Prozesskommando und erfinde keinen Alias wie python311. Erst nach
        erfolgreichem Exit-Code der Umgebungserstellung darfst du `.venv\\Scripts\\python.exe -m pip ...` aufrufen.
        Halte .venv, Caches und generierte Zwischenstände aus Git und fixiere Abhängigkeiten. Führe Generator, Tests,
        Syntax-/Buildprüfung, einen begrenzten CLI-Smoke und git diff
        aus. Behebe alle gefundenen Fehler selbstständig. Speichere keine erfundenen Nullresiduen, vergleiche nie
        einen Wert mit sich selbst als Referenz und trenne analytische Referenz, numerische Rechnung und unabhängige
        Verifikation im Code nachvollziehbar.
        """;

    private static string BuildIterationPrompt(int iteration, string challenge) => $$"""
        Setze die bestehende, fortlaufende Untersuchung der Einsteinschen Feldgleichungen in Iteration
        {{iteration}} fort. Verwende die vorhandenen Implementierungen, Fälle, fehlgeschlagenen Ansätze,
        Simulationsdaten und Erkenntnisse; beginne kein paralleles Projekt.

        Aktueller Forschungsschwerpunkt:
        {{challenge}}

        Leite die Annahmen und Gleichungen aus einem anerkannten exakten, effektiven, semiklassischen oder
        perturbativen Modell nachvollziehbar her. Forme sie symbolisch um, löse eine klar definierte Reduktion
        analytisch oder numerisch und prüfe Einstein- und Bianchi-Residuen, Erhaltungssätze, Dimensionen und bekannte
        Grenzfälle unabhängig. Klassifiziere das Ergebnis ehrlich als verified, approximation oder undetermined und
        nenne den Gültigkeitsbereich. Speichere für einen verified-Fall mindestens zwei tatsächlich ausgewertete
        Residuen-Stichproben mit einem nicht leeren `evaluationPoint` aus endlichen numerischen Koordinaten/Parametern;
        Einstein- und Bianchi-Maximum müssen jeweils höchstens 1e-5 sein. Behaupte insbesondere keine vollständige Lösung der Stringtheorie,
        Quantenfeldtheorie oder Quantengravitation, wenn lediglich deren kontrollierte Niederenergie-, Hintergrund-
        oder Störungsnäherung berechnet wurde.

        Erweitere einstein_cases.json, einstein_attempts.json, die fachliche Analyse und Regressionstests. Wähle
        selbst eine aussagekräftige grafische oder simulierte Darstellung für diesen Fall. Du darfst bestehende
        Visualisierungs- und Simulationsverfahren umbauen, wenn eine andere Darstellung geeigneter ist. Erzeuge die
        Grafik und ihre Daten tatsächlich neu; Beispiele sind Tensorresiduen über dem Gitter, Krümmungsinvarianten,
        Horizont- oder Kausalstruktur, Geodäten, Phasenräume oder zeitliche kosmologische Entwicklung.

        Setze simulation_data/live_progress.json vor Beginn dieser Iteration auf running und aktualisiere phase, step,
        totalSteps, metrics und updatedAt bei aussagekräftigen Rechenschritten. Überschreibe parallel den Live-Plot
        visualizations/live_progress.png oder .svg mit dem jeweils vorhandenen Datenstand. Verwende atomare
        Dateiersetzung und setze den Status erst nach erfolgreicher Prüfung aller Ergebnisartefakte auf completed.
        Bei einem Fehler schreibe failed samt technischer Phase, bevor du den Fehler selbstständig behebst und den
        Lauf erneut ausführst.

        Wird der untersuchte Fall nach allen unabhängigen Prüfungen als verified klassifiziert, erstelle oder
        aktualisiere zusätzlich sein eigenes ausführliches Markdown-Dokument unter solutions/ und trage dessen
        relativen Pfad als solutionDocument im Fall ein. Die Datei muss die vollständige Herleitung, Annahmen,
        Gültigkeitsdomäne, Randbedingungen, Lösung, Residuen, Fehlerschranken, Interpretation und reproduzierbare
        Befehle einschließlich der zugehörigen Plot- und Simulationsartefakte enthalten.

        Führe anschließend alle Tests, Syntax-/Buildprüfung, Ergebnis- und Grafikgenerierung, einen begrenzten
        CLI-Smoke sowie git diff aus. Prüfe erzeugte JSON-, Daten- und Bildartefakte erneut und behebe Folgefehler
        selbstständig. Halte verifizierte Grenzfälle, Näherungsfehler und noch offene Fragen dauerhaft fest, damit
        spätere Iterationen auf belastbaren Ergebnissen aufbauen.
        """;

    private static string BuildCorrectionPrompt(int iteration, IReadOnlyList<string> issues) => """
        Die unabhängige Abnahme der Einstein-Kampagne hat nach Iteration
        """ + iteration + """
        folgende konkrete Mängel gefunden:

        """ + string.Join(Environment.NewLine, issues.Select(static issue => "- " + issue)) + """

        Behebe die Ursachen in Engine, Tests, Falldaten, Versuchshistorie, Analyse, Visualisierungs-/Simulationscode
        und erzeugten Artefakten. Schwäche keine Prüfung, erfinde keine Residuen oder physikalischen Eingabedaten und
        kennzeichne jede effektive, semiklassische, perturbative oder numerische Näherung samt Gültigkeitsbereich.
        Erzeuge beziehungsweise korrigiere für jeden verified-Fall auch das referenzierte detaillierte Dokument unter
        solutions/. Regeneriere Daten und Grafiken, führe alle Tests, Syntax-/Buildprüfung, CLI-Smoke und git diff aus
        und behebe Folgefehler selbstständig. Repariere ebenfalls den atomaren Live-Fortschritt und stelle sicher,
        dass simulation_data/live_progress.json sowie der zugehörige Live-Plot den korrigierten Abschlussstand zeigen.
        """;

    private static bool HasFoundation(string workspace) =>
        File.Exists(Path.Combine(workspace, "einstein_engine.py"))
        && File.Exists(Path.Combine(workspace, "test_einstein_engine.py"))
        && File.Exists(Path.Combine(workspace, "einstein_cases.json"))
        && File.Exists(Path.Combine(workspace, "einstein_attempts.json"));

    private static int ReadAttemptCount(string workspace)
    {
        var path = Path.Combine(workspace, "einstein_attempts.json");
        if (!File.Exists(path))
        {
            return 0;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("attempts", out var attempts)
                && attempts.ValueKind == JsonValueKind.Array
                    ? attempts.GetArrayLength()
                    : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static List<string> CollectEinsteinIssues(string workspace)
    {
        var issues = new List<string>();
        var enginePath = Path.Combine(workspace, "einstein_engine.py");
        var testsPath = Path.Combine(workspace, "test_einstein_engine.py");
        var casesPath = Path.Combine(workspace, "einstein_cases.json");
        var attemptsPath = Path.Combine(workspace, "einstein_attempts.json");
        var analysisPath = Path.Combine(workspace, "einstein_analysis.md");
        var visualizerPath = Path.Combine(workspace, "visualize_einstein.py");
        RequireFile(enginePath, "einstein_engine.py fehlt.");
        RequireFile(testsPath, "test_einstein_engine.py fehlt.");
        RequireFile(casesPath, "einstein_cases.json fehlt.");
        RequireFile(attemptsPath, "einstein_attempts.json fehlt.");
        RequireFile(analysisPath, "einstein_analysis.md fehlt.");
        RequireFile(visualizerPath, "visualize_einstein.py fehlt.");

        if (File.Exists(enginePath))
        {
            var source = File.ReadAllText(enginePath);
            AddIf(source.Length < 4_000, "Die Tensor-Engine ist auffällig kurz.");
            foreach (var term in new[] { "christoffel", "ricci", "einstein", "riemann", "bianchi" })
            {
                AddIf(!source.Contains(term, StringComparison.OrdinalIgnoreCase),
                    $"Die Tensor-Engine behandelt {term} nicht erkennbar.");
            }
            var divergenceFunction = ExtractPythonFunction(source, "compute_covariant_divergence");
            AddIf(string.IsNullOrWhiteSpace(divergenceFunction)
                || divergenceFunction.Contains("\n                pass", StringComparison.Ordinal)
                || !divergenceFunction.Contains("christoffel", StringComparison.OrdinalIgnoreCase),
                "Die kovariante Divergenz ist nur ein Platzhalter oder lässt die Verbindungskoeffizienten aus.");
            var verificationFunction = ExtractPythonFunction(source, "verify_einstein_equations");
            AddIf(string.IsNullOrWhiteSpace(verificationFunction)
                || CountOccurrences(verificationFunction, "stress_energy") < 2,
                "Die Feldgleichungsprüfung verwendet den übergebenen Energie-Impuls-Tensor nicht tatsächlich.");
            AddIf(verificationFunction.Contains("if not coords", StringComparison.Ordinal)
                && verificationFunction.Contains("return 0.0", StringComparison.Ordinal),
                "Die Feldgleichungsprüfung erklärt konstante, aber von null verschiedene Residuen fälschlich zu null.");
            var kretschmannFunction = ExtractPythonFunction(source, "compute_kretschmann_scalar");
            AddIf(kretschmannFunction.Contains("Komponentenquadrate", StringComparison.OrdinalIgnoreCase)
                || !kretschmannFunction.Contains("metric", StringComparison.OrdinalIgnoreCase),
                "Der Kretschmann-Skalar wird nicht als vollständige metrische Tensor-Kontraktion berechnet.");
        }
        if (File.Exists(testsPath))
        {
            var tests = File.ReadAllText(testsPath);
            AddIf(tests.Length < 1_500, "Die Einstein-Regressionstests sind nicht umfassend genug.");
            AddIf(!tests.Contains("residual", StringComparison.OrdinalIgnoreCase),
                "Die Tests prüfen keine unabhängigen Residuen.");
            AddIf(!tests.Contains("limit", StringComparison.OrdinalIgnoreCase)
                && !tests.Contains("boundary", StringComparison.OrdinalIgnoreCase),
                "Die Tests prüfen keinen bekannten Grenz- oder Randfall.");
            AddIf(!tests.Contains("conservation", StringComparison.OrdinalIgnoreCase)
                && !tests.Contains("bianchi", StringComparison.OrdinalIgnoreCase),
                "Die Tests prüfen keinen Erhaltungssatz beziehungsweise keine Bianchi-Identität.");
            AddIf(!tests.Contains("nonzero_constant", StringComparison.OrdinalIgnoreCase),
                "Ein Regressionstest gegen den falschen Null-Shortcut für konstante Nichtnulldaten fehlt.");
            AddIf(!tests.Contains("schwarzschild", StringComparison.OrdinalIgnoreCase)
                || !tests.Contains("48", StringComparison.Ordinal),
                "Eine unabhängige Schwarzschild-Prüfung einschließlich K = 48 M²/r⁶ fehlt.");
        }
        if (File.Exists(analysisPath))
        {
            var analysis = File.ReadAllText(analysisPath);
            AddIf(analysis.Length < 1_500, "Die fachliche Einstein-Auswertung ist zu knapp.");
        }

        ValidateCases(casesPath, issues);
        ValidateAttempts(attemptsPath, issues);
        var visualizationDirectory = Path.Combine(workspace, "visualizations");
        var visualizations = Directory.Exists(visualizationDirectory)
            ? Directory.EnumerateFiles(visualizationDirectory, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path) is ".png" or ".svg")
                .Where(path => new FileInfo(path).Length > 1_024)
                .ToArray()
            : [];
        AddIf(visualizations.Length == 0, "Es wurde keine belastbare PNG- oder SVG-Visualisierung erzeugt.");
        var simulationDirectory = Path.Combine(workspace, "simulation_data");
        var simulationFiles = Directory.Exists(simulationDirectory)
            ? Directory.EnumerateFiles(simulationDirectory, "*", SearchOption.AllDirectories)
                .Where(path => new FileInfo(path).Length > 32)
                .ToArray()
            : [];
        AddIf(simulationFiles.Length == 0, "Maschinenlesbare Simulations- oder Plotdaten fehlen.");
        ValidateLiveProgress(
            Path.Combine(simulationDirectory, "live_progress.json"),
            visualizationDirectory,
            issues);
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
    }

    private static void ValidateLiveProgress(
        string progressPath,
        string visualizationDirectory,
        List<string> issues)
    {
        if (!File.Exists(progressPath))
        {
            issues.Add("simulation_data/live_progress.json für die Liveansicht fehlt.");
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(progressPath));
            var root = document.RootElement;
            var status = ReadString(root, "status");
            if (status != "completed")
            {
                issues.Add("Der Live-Fortschritt besitzt nach dem Lauf nicht den Status completed.");
            }
            foreach (var propertyName in new[] { "caseId", "phase", "updatedAt" })
            {
                if (string.IsNullOrWhiteSpace(ReadString(root, propertyName)))
                {
                    issues.Add($"Der Live-Fortschritt enthält kein {propertyName}.");
                }
            }
            if (ReadString(root, "updatedAt") is { Length: > 0 } updatedAt
                && !DateTimeOffset.TryParse(
                    updatedAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out _))
            {
                issues.Add("updatedAt im Live-Fortschritt ist kein gültiger ISO-8601-Zeitpunkt.");
            }
            if (!root.TryGetProperty("step", out var stepValue)
                || !stepValue.TryGetInt32(out var step)
                || !root.TryGetProperty("totalSteps", out var totalValue)
                || !totalValue.TryGetInt32(out var totalSteps)
                || step < 0
                || totalSteps <= 0
                || step < totalSteps)
            {
                issues.Add("Der abgeschlossene Live-Fortschritt besitzt keine schlüssigen step/totalSteps-Werte.");
            }
            if (!root.TryGetProperty("metrics", out var metrics)
                || metrics.ValueKind != JsonValueKind.Object
                || !metrics.EnumerateObject().Any())
            {
                issues.Add("Der Live-Fortschritt enthält keine berechneten Zwischen- oder Abschlussmetriken.");
            }
        }
        catch (JsonException exception)
        {
            issues.Add($"simulation_data/live_progress.json ist ungültig: {exception.Message}");
        }

        var livePlot = new[]
        {
            Path.Combine(visualizationDirectory, "live_progress.png"),
            Path.Combine(visualizationDirectory, "live_progress.svg"),
        }.FirstOrDefault(path => File.Exists(path) && new FileInfo(path).Length > 1_024);
        if (livePlot is null)
        {
            issues.Add("Ein belastbarer visualizations/live_progress.png- oder SVG-Liveplot fehlt.");
        }
    }

    private static void ValidateCases(string path, List<string> issues)
    {
        if (!File.Exists(path))
        {
            return;
        }
        try
        {
            var workspace = Path.GetDirectoryName(Path.GetFullPath(path))
                ?? throw new InvalidOperationException("Der Einstein-Workspace konnte nicht bestimmt werden.");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("cases", out var cases)
                || cases.ValueKind != JsonValueKind.Array
                || cases.GetArrayLength() < 2)
            {
                issues.Add("einstein_cases.json muss mindestens zwei Fälle enthalten.");
                return;
            }
            var verifiedCount = 0;
            foreach (var item in cases.EnumerateArray())
            {
                var id = ReadString(item, "id") ?? "<ohne ID>";
                var classification = ReadString(item, "classification");
                if (classification is not ("verified" or "approximation" or "undetermined"))
                {
                    issues.Add($"Fall {id} besitzt keine gültige classification.");
                }
                var approximationLevel = ReadString(item, "approximationLevel");
                if (approximationLevel is not ("exact" or "effective" or "semiclassical" or "perturbative" or "numerical"))
                {
                    issues.Add($"Fall {id} besitzt kein gültiges approximationLevel.");
                }
                foreach (var propertyName in new[] { "theoryDomain", "validityDomain", "conclusion" })
                {
                    if (string.IsNullOrWhiteSpace(ReadString(item, propertyName)))
                    {
                        issues.Add($"Fall {id} dokumentiert {propertyName} nicht.");
                    }
                }
                foreach (var propertyName in new[] { "equations", "assumptions", "independentChecks" })
                {
                    if (!item.TryGetProperty(propertyName, out var values)
                        || values.ValueKind != JsonValueKind.Array
                        || values.GetArrayLength() == 0)
                    {
                        issues.Add($"Fall {id} besitzt keine Einträge in {propertyName}.");
                    }
                }
                CheckFiniteNonNegative(item, "maxEinsteinResidual", id, issues);
                CheckFiniteNonNegative(item, "maxBianchiResidual", id, issues);
                ValidateResidualEvidence(item, id, issues);
                if (string.Equals(id, "schwarzschild", StringComparison.OrdinalIgnoreCase))
                {
                    var serializedMetric = item.TryGetProperty("metric", out var metric)
                        ? metric.GetRawText()
                        : string.Empty;
                    if (!serializedMetric.Contains('r')
                        || !serializedMetric.Contains("theta", StringComparison.OrdinalIgnoreCase)
                        || !serializedMetric.Contains('M'))
                    {
                        issues.Add("Die gespeicherte Schwarzschild-Metrik besitzt keine Abhängigkeit von M, r und theta und ist keine Schwarzschild-Geometrie.");
                    }
                }
                if (classification == "verified"
                    && item.TryGetProperty("maxEinsteinResidual", out var residual)
                    && residual.TryGetDouble(out var value)
                    && value > 1e-5)
                {
                    issues.Add($"Der als verified markierte Fall {id} hat ein zu großes Einstein-Residual.");
                }
                if (classification == "verified")
                {
                    if (!item.TryGetProperty("independentChecks", out var independentChecks)
                        || independentChecks.ValueKind != JsonValueKind.Array
                        || independentChecks.GetArrayLength() < 2
                        || independentChecks.EnumerateArray().Any(static check =>
                            check.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(check.GetString())))
                    {
                        issues.Add($"Der verified-Fall {id} dokumentiert nicht mindestens zwei unabhängige Prüfungen.");
                    }
                    verifiedCount++;
                    ValidateSolutionDocument(workspace, item, id, issues);
                }
                if (!item.TryGetProperty("visualizations", out var plots)
                    || plots.ValueKind != JsonValueKind.Array
                    || plots.GetArrayLength() == 0)
                {
                    issues.Add($"Fall {id} referenziert keine Visualisierung.");
                }
                else
                {
                    ValidateReferencedArtifacts(workspace, plots, id, "Visualisierung", 1_024, issues);
                }
                if (!item.TryGetProperty("simulationData", out var simulationData)
                    || simulationData.ValueKind != JsonValueKind.Array
                    || simulationData.GetArrayLength() == 0)
                {
                    issues.Add($"Fall {id} referenziert keine maschinenlesbaren Simulations- oder Prüfdaten.");
                }
                else
                {
                    ValidateReferencedArtifacts(workspace, simulationData, id, "Simulationsdatei", 32, issues);
                }
            }
            if (verifiedCount == 0)
            {
                issues.Add("Es existiert noch keine unabhängig verifizierte Referenzlösung mit Lösungsdokument.");
            }
        }
        catch (JsonException exception)
        {
            issues.Add($"einstein_cases.json ist ungültig: {exception.Message}");
        }
    }

    private static void ValidateSolutionDocument(
        string workspace,
        JsonElement item,
        string id,
        List<string> issues)
    {
        var relativePath = ReadString(item, "solutionDocument");
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            issues.Add($"Der verified-Fall {id} referenziert kein solutionDocument.");
            return;
        }
        if (Path.IsPathRooted(relativePath))
        {
            issues.Add($"Das solutionDocument von Fall {id} muss ein relativer Workspacepfad sein.");
            return;
        }

        string documentPath;
        try
        {
            documentPath = Path.GetFullPath(Path.Combine(workspace, relativePath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            issues.Add($"Das solutionDocument von Fall {id} besitzt einen ungültigen Pfad.");
            return;
        }

        var solutionsRoot = Path.TrimEndingDirectorySeparator(Path.Combine(workspace, "solutions"))
            + Path.DirectorySeparatorChar;
        if (!documentPath.StartsWith(solutionsRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(documentPath), ".md", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add($"Das solutionDocument von Fall {id} muss eine Markdown-Datei unter solutions/ sein.");
            return;
        }
        if (!File.Exists(documentPath))
        {
            issues.Add($"Das solutionDocument von Fall {id} fehlt: {relativePath}.");
            return;
        }

        var text = File.ReadAllText(documentPath);
        if (text.Length < 2_500)
        {
            issues.Add($"Das Lösungsdokument von Fall {id} ist für eine detaillierte Herleitung zu knapp.");
        }
        if (!text.Contains(id, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add($"Das Lösungsdokument von Fall {id} nennt seine Fall-ID nicht.");
        }
        RequireConcept(["Herleitung", "derivation"], "mathematische Herleitung");
        RequireConcept(["Annahme", "assumption"], "Annahmen");
        RequireConcept(["Gültigkeit", "validity"], "Gültigkeitsbereich");
        RequireConcept(["Randbeding", "Anfangsbeding", "boundary", "initial condition"], "Rand- oder Anfangsbedingungen");
        RequireConcept(["Residual", "Fehlerschranke", "error bound"], "Residuen oder Fehlerschranken");
        RequireConcept(["Interpretation"], "physikalische Interpretation");
        RequireConcept(["Reprodu", "Ausführen", "execute"], "Reproduktionsschritte");
        RequireConcept(["Simulation", "Visualisierung", "Grafik", "plot"], "Simulations- oder Grafikbelege");

        void RequireConcept(IReadOnlyList<string> terms, string description)
        {
            if (!terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add($"Das Lösungsdokument von Fall {id} dokumentiert {description} nicht erkennbar.");
            }
        }
    }

    private static void ValidateResidualEvidence(JsonElement item, string id, List<string> issues)
    {
        if (string.IsNullOrWhiteSpace(ReadString(item, "verificationMethod")))
        {
            issues.Add($"Fall {id} dokumentiert keine ausführbare verificationMethod.");
        }
        if (!item.TryGetProperty("residualSamples", out var samples)
            || samples.ValueKind != JsonValueKind.Array
            || samples.GetArrayLength() == 0)
        {
            issues.Add($"Fall {id} enthält keine tatsächlich berechneten residualSamples.");
            return;
        }

        var maximumEinstein = 0d;
        var maximumBianchi = 0d;
        var validSamples = 0;
        foreach (var sample in samples.EnumerateArray())
        {
            if (sample.ValueKind != JsonValueKind.Object
                || !TryReadFiniteNonNegative(sample, "einsteinResidual", out var einstein)
                || !TryReadFiniteNonNegative(sample, "bianchiResidual", out var bianchi))
            {
                issues.Add($"Fall {id} besitzt eine ungültige Residuen-Stichprobe.");
                continue;
            }
            if (!sample.TryGetProperty("evaluationPoint", out var evaluationPoint)
                || evaluationPoint.ValueKind != JsonValueKind.Object
                || !evaluationPoint.EnumerateObject().Any()
                || evaluationPoint.EnumerateObject().Any(static coordinate =>
                    !coordinate.Value.TryGetDouble(out var number) || !double.IsFinite(number)))
            {
                issues.Add($"Fall {id} besitzt keine Residuen-Stichprobe mit einem regulären evaluationPoint aus endlichen numerischen Werten.");
                continue;
            }
            maximumEinstein = Math.Max(maximumEinstein, einstein);
            maximumBianchi = Math.Max(maximumBianchi, bianchi);
            validSamples++;
        }
        if (validSamples == 0)
        {
            return;
        }

        CompareDeclaredMaximum("maxEinsteinResidual", maximumEinstein);
        CompareDeclaredMaximum("maxBianchiResidual", maximumBianchi);
        if (string.Equals(ReadString(item, "classification"), "verified", StringComparison.Ordinal))
        {
            if (validSamples < 2)
            {
                issues.Add($"Der verified-Fall {id} besitzt nicht mindestens zwei gültige Residuen-Stichproben.");
            }
            if (maximumEinstein > 1e-5)
            {
                issues.Add($"Der als verified markierte Fall {id} hat ein zu großes Einstein-Residual in seinen Stichproben.");
            }
            if (maximumBianchi > 1e-5)
            {
                issues.Add($"Der als verified markierte Fall {id} hat ein zu großes Bianchi-Residual in seinen Stichproben.");
            }
        }

        void CompareDeclaredMaximum(string propertyName, double calculatedMaximum)
        {
            if (!item.TryGetProperty(propertyName, out var declaredValue)
                || !declaredValue.TryGetDouble(out var declared)
                || !double.IsFinite(declared))
            {
                return;
            }
            var tolerance = Math.Max(1e-14, Math.Max(Math.Abs(declared), calculatedMaximum) * 1e-8);
            if (Math.Abs(declared - calculatedMaximum) > tolerance)
            {
                issues.Add($"Fall {id}: {propertyName} stimmt nicht mit dem Maximum der residualSamples überein.");
            }
        }
    }

    private static void ValidateReferencedArtifacts(
        string workspace,
        JsonElement references,
        string id,
        string description,
        long minimumLength,
        List<string> issues)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)) + Path.DirectorySeparatorChar;
        foreach (var reference in references.EnumerateArray())
        {
            if (reference.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(reference.GetString()))
            {
                issues.Add($"Fall {id} besitzt eine ungültige {description}-Referenz.");
                continue;
            }
            var relativePath = reference.GetString()!;
            if (Path.IsPathRooted(relativePath))
            {
                issues.Add($"Fall {id}: {description} muss relativ zum Workspace angegeben werden.");
                continue;
            }
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(workspace, relativePath));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                issues.Add($"Fall {id} besitzt einen ungültigen {description}-Pfad.");
                continue;
            }
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(fullPath)
                || new FileInfo(fullPath).Length <= minimumLength)
            {
                issues.Add($"Fall {id}: Referenzierte {description} fehlt oder ist nicht belastbar: {relativePath}.");
            }
        }
    }

    private static bool TryReadFiniteNonNegative(JsonElement owner, string propertyName, out double value)
    {
        value = 0;
        return owner.TryGetProperty(propertyName, out var property)
            && property.TryGetDouble(out value)
            && double.IsFinite(value)
            && value >= 0;
    }

    private static string ExtractPythonFunction(string source, string functionName)
    {
        var marker = $"def {functionName}(";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }
        var next = source.IndexOf("\ndef ", start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static void ValidateAttempts(string path, List<string> issues)
    {
        if (!File.Exists(path))
        {
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("attempts", out var attempts)
                || attempts.ValueKind != JsonValueKind.Array
                || attempts.GetArrayLength() == 0)
            {
                issues.Add("einstein_attempts.json enthält keine persistente Versuchshistorie.");
            }
        }
        catch (JsonException exception)
        {
            issues.Add($"einstein_attempts.json ist ungültig: {exception.Message}");
        }
    }

    private static string? ReadString(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void CheckFiniteNonNegative(JsonElement owner, string name, string id, List<string> issues)
    {
        if (!owner.TryGetProperty(name, out var value)
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number)
            || number < 0)
        {
            issues.Add($"Fall {id} besitzt kein gültiges {name}.");
        }
    }

    private static int ParseIterationLimit(string? configured, bool continuous)
    {
        if (continuous)
        {
            return int.MaxValue;
        }
        return int.TryParse(configured, out var value) && value > 0
            ? Math.Min(value, 1_000)
            : 3;
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
        Assert.True(process.Start(), "Der Einstein-Testworkspace konnte nicht als Git-Repository initialisiert werden.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git init ist fehlgeschlagen: {output}\n{error}");
    }
}
