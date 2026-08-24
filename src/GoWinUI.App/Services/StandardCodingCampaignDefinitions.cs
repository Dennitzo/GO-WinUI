using System.Text.Json;

namespace GoWinUI.App.Services;

public abstract class StandardCodingCampaignDefinition : ICodingCampaignDefinition
{
    protected StandardCodingCampaignDefinition(CodingCampaignDescriptor descriptor, string foundationFile, IReadOnlyList<string> challenges)
    {
        Descriptor = descriptor;
        FoundationFile = foundationFile;
        Challenges = challenges;
    }

    protected string FoundationFile { get; }
    protected IReadOnlyList<string> Challenges { get; }
    public CodingCampaignDescriptor Descriptor { get; }
    public bool HasFoundation(string workspacePath) => File.Exists(Path.Combine(workspacePath, FoundationFile));
    public int ReadIteration(string workspacePath)
    {
        var path = Path.Combine(workspacePath, ".go-campaign", Descriptor.Id + ".json");
        if (!File.Exists(path)) return 0;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("iteration", out var value) && value.TryGetInt32(out var result) ? result : 0;
        }
        catch (JsonException) { return 0; }
    }
    public string GetChallenge(int iteration) => Challenges[Math.Abs(iteration) % Challenges.Count];
    public abstract string BuildBootstrapPrompt();
    public virtual string BuildIterationPrompt(int iteration, string challenge) => $$"""
        Arbeite im bestehenden Coding-Workflow „{{Descriptor.Title}}“ in Iteration {{iteration}}.
        Aktueller Schwerpunkt: {{challenge}}
        Verwende vorhandenen Code, Daten, Tests und dokumentierte Fehlschläge. Führe eine fachlich sinnvolle
        Erweiterung aus, aktualisiere Plots und maschinenlesbare Daten atomar, starte alle Prüfungen und behebe Fehler
        selbstständig. Aktualisiere .go-campaign/{{Descriptor.Id}}.json mit Iteration, Phase und Zeitstempel.
        """;
    public virtual string BuildCorrectionPrompt(int iteration, string challenge, IReadOnlyList<string> issues) => $$"""
        Die unabhängige Abnahme des Workflows „{{Descriptor.Title}}“ in Iteration {{iteration}} meldet:
        {{string.Join(Environment.NewLine, issues.Select(static issue => "- " + issue))}}
        Behebe die Ursachen, regeneriere betroffene Daten und Plots und führe alle Tests erneut aus. Schwäche keine Prüfung.
        """;
    public virtual Task<CodingCampaignValidationResult> ValidateAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var issues = new List<string>();
        if (!HasFoundation(workspacePath)) issues.Add($"Pflichtartefakt fehlt: {FoundationFile}");
        if (!Directory.Exists(Path.Combine(workspacePath, "visualizations"))) issues.Add("Visualisierungsordner fehlt.");
        if (!Directory.Exists(Path.Combine(workspacePath, "simulation_data"))) issues.Add("Simulationsdatenordner fehlt.");
        return Task.FromResult(new CodingCampaignValidationResult(issues.Count == 0, issues, []));
    }
}

public sealed class TheoreticalPhysicsCodingCampaignDefinition() : StandardCodingCampaignDefinition(
    new("theoretical-physics", "Theoretische Physik", "Fortlaufende analytische und numerische Modellprüfung mit reproduzierbaren Simulationen.",
        "Physik und Mathematik", ["visualizations", "simulation_data", "solutions", "proofs"]),
    "physics_solver.py",
    ["Quantenharmonischer Oszillator", "Streutheorie", "Quantenfeld-Moden", "Nichtlineare Dynamik", "Variationsprinzipien"])
{
    public override string BuildBootstrapPrompt() => """
        Erstelle ein fortsetzbares Python-Forschungsprojekt für theoretische Physik. Implementiere symbolische und
        numerische Gegenprüfungen, Tests, reproduzierbare Simulationen, visualizations/, simulation_data/, solutions/
        und proofs/. Starte mit dem harmonischen Quantenoszillator und einem zweiten etablierten Modell. Lege
        physics_solver.py und test_physics_solver.py an. Numerische Evidenz ist nicht als mathematischer Beweis zu
        bezeichnen. Aktualisiere .go-campaign/theoretical-physics.json und alle Live-Artefakte atomar. Führe Tests,
        Generatoren und git diff aus und behebe Fehler selbstständig.
        """;
}

public sealed class TgaVentilationCodingCampaignDefinition() : StandardCodingCampaignDefinition(
    new("tga-ventilation", "TGA-Lüftungsplanung", "Fortlaufende Luftmengenberechnung, Prüfregeln und visuell aufbereitete Excel-Auswertung.",
        "TGA Planung", ["visualizations", "simulation_data", "solutions"]),
    "ventilation_calculation.py",
    ["Raumweise Außenluftvolumenströme", "Druckverlust und Kanalnetz", "Wärmerückgewinnung", "Plausibilitätsprüfung", "Excel-Bericht"])
{
    public override string BuildBootstrapPrompt() => """
        Erstelle ein fortsetzbares TGA-Lüftungsprojekt mit ventilation_calculation.py, Tests und einer visuell klaren
        Excel-Arbeitsmappe. Berechne und dokumentiere raumweise Zu-, Ab- und Außenluftvolumenströme, Summen,
        Plausibilitäten, Einheiten und Annahmen. Nutze Formeln statt fest eingetragener Ergebnisse. Erzeuge Diagramme,
        maschinenlesbare simulation_data/ und visualizations/. Aktualisiere .go-campaign/tga-ventilation.json atomar.
        Führe Berechnung, Tests, Excel-Validierung und git diff aus und behebe Fehler selbstständig.
        """;
}

public sealed class PromptDrivenCodingCampaignDefinition() : ICodingCampaignDefinition
{
    internal const string DescriptorId = "prompt-workflow";
    internal const string ContractRelativePath = ".go-campaign/prompt-workflow.json";

    public CodingCampaignDescriptor Descriptor { get; } = new(
        DescriptorId,
        "Prompt-Workflow",
        "Allgemeiner Coding-Workflow, der Ziel, Tests, Checker und Verifikation aus Nutzerprompt und Workspace ableitet.",
        "Allgemein",
        [".go-campaign", "tests", "solutions", "artifacts", "visualizations", "simulation_data"]);

    public bool HasFoundation(string workspacePath) =>
        File.Exists(Path.Combine(workspacePath, ContractRelativePath.Replace('/', Path.DirectorySeparatorChar)));

    public int ReadIteration(string workspacePath)
    {
        var path = Path.Combine(workspacePath, ContractRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!TryReadContract(path, out var document)) return 0;
        using (document)
        {
            return document.RootElement.TryGetProperty("iteration", out var value)
                   && value.TryGetInt32(out var result)
                   && result >= 0
                ? result
                : 0;
        }
    }

    public string GetChallenge(int iteration) =>
        "Nächster sinnvoller Schritt aus Nutzerziel, Workspace-Zustand und Prüfvertrag";

    public string BuildBootstrapPrompt() => $$"""
        Erstelle einen allgemeinen, promptgetriebenen Coding-Workflow im gebundenen Workspace. Nutze keine
        hartcodierte Test- oder Domänenlogik. Leite Ziel, Umfang, Akzeptanzkriterien und Verifikation aus der aktuellen
        Nutzeranweisung, vorhandenen Repositorydateien und tatsächlichem Workspace-Zustand ab.

        Lege den Workflow-Vertrag unter `{{ContractRelativePath}}` als striktes JSON an. Mindeststruktur:

        - `schema`: exakt `go.prompt-workflow.v1`;
        - `title`: kurzer fachlicher Titel;
        - `objective`: konkret messbares Ziel;
        - `iteration`: nichtnegative ganze Zahl;
        - `scope`: Objekt mit relevanten Dateien, Artefakten oder Bereichen;
        - `assumptions`: Liste der aktuell geltenden Annahmen;
        - `acceptanceCriteria`: Liste beobachtbarer, testbarer Kriterien;
        - `verificationCommands`: Liste geplanter oder ausgeführter Befehle mit Zweck `setup`, `test`, `build` oder `start`;
        - `artifacts`: Liste erwarteter oder erzeugter Artefakte;
        - `openQuestions`: Liste offener fachlicher oder technischer Punkte;
        - `lastRun`: Objekt mit Status, geänderten Dateien, ausgeführten Prüfungen und nächster Aktion.

        Schreibe fehlende Tests, Checker oder Smoke-Skripte selbst, wenn das Repository noch keine passenden besitzt.
        Diese Prüfungen müssen aus dem Nutzerziel ableitbare Invarianten, Grenzfälle oder Negativkontrollen enthalten.
        Behandle selbst geschriebene Tests nicht automatisch als Wahrheit: prüfe sie gegen bekannte gültige und ungültige
        Referenzfälle oder eine unabhängige Zweitberechnung. Führe danach die engsten passenden Tests, Build- oder
        Syntaxprüfung und einen begrenzten Start-/Smoke-Lauf aus. Aktualisiere den Vertrag atomar mit den realen
        Ergebnissen. Wenn der Workflow ein Buch, eine Lösung oder eine Lehrtext-PDF erzeugt, pflege die Quelle als
        Markdown/Text/TeX/JSON und überlasse die PDF-Erzeugung anschließend dem deterministischen GO-KaTeX-Exporter; baue
        dafür keine eigene HTML-, CDN-KaTeX- oder Direkt-PDF-Strecke und rufe kein PDF-Werkzeug auf. Wenn kein ausdrückliches Nutzerziel vorliegt, inspiziere README/Projektstruktur und wähle den
        kleinsten sicheren Qualitäts- oder Verifikationsschritt.
        """;

    public string BuildIterationPrompt(int iteration, string challenge) => $$"""
        Arbeite im allgemeinen Prompt-Workflow weiter. Lies zuerst `{{ContractRelativePath}}`, relevante Projektdateien
        und die letzten tatsächlichen Prüfergebnisse. Wähle den nächsten sinnvollen Schritt aus Ziel, offenen Punkten,
        fehlschlagenden Tests, unvalidierten Artefakten oder einer neuen Nutzeranweisung. Nutze keine versteckte
        Sonderlogik für einzelne Testnamen; die Entscheidung muss aus dem Vertrag und Workspace ableitbar sein.

        Wenn die aktuelle Nutzeranweisung Ziel, Umfang oder Priorität ändert, aktualisiere den Vertrag zuerst. Danach
        implementiere oder korrigiere Code, Tests, Checker, Generatoren oder Dokumentation wie nötig. Neue Tests müssen
        echte Assertions, Grenzfälle und wenn möglich Negativkontrollen enthalten. Ein grüner Testlauf reicht nur, wenn
        er die Akzeptanzkriterien wirklich abdeckt; andernfalls ergänze den Prüfvertrag und die Tests.

        Erhöhe `iteration` nur bei einer echten Verbesserung oder einer bewusst dokumentierten Erkenntnis. Führe die
        im Vertrag passenden setup/test/build/start-Schritte aus, behebe Fehler selbstständig und schreibe bei
        PDF-Artefakten den deterministischen GO-KaTeX-Exporter als Verifikations-/Buildschritt fest, damit KaTeX lokal durch GO gerendert
        und validiert wird; rufe dafür kein PDF-Werkzeug auf. Im Prozessbericht steht knapp, was aus dem Workflow-Vertrag abgeleitet wurde und welche
        Prüfungen tatsächlich liefen.
        """;

    public string BuildCorrectionPrompt(int iteration, string challenge, IReadOnlyList<string> issues) => $$"""
        Die allgemeine Workflow-Abnahme meldet:

        {{string.Join(Environment.NewLine, issues.Select(static issue => "- " + issue))}}

        Repariere nicht nur die Symptome. Aktualisiere `{{ContractRelativePath}}`, Tests, Checker, Generatoren,
        Artefakte und Dokumentation so, dass der Prüfvertrag wieder zum tatsächlichen Workspace passt. Schwäche keine
        Prüfung, entferne keine Akzeptanzkriterien ohne begründete Nutzer- oder Fachänderung und führe danach die
        im Vertrag dokumentierten Verifikationsbefehle erneut aus.
        """;

    public Task<CodingCampaignValidationResult> ValidateAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var issues = new List<string>();
        var path = Path.Combine(workspacePath, ContractRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!TryReadContract(path, out var document))
        {
            issues.Add($"Pflichtartefakt fehlt oder ist kein striktes JSON: {ContractRelativePath}");
            return Task.FromResult(new CodingCampaignValidationResult(false, issues, []));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add($"{ContractRelativePath} muss ein JSON-Objekt enthalten.");
                return Task.FromResult(new CodingCampaignValidationResult(false, issues, []));
            }

            RequireString(root, "schema", issues, expected: "go.prompt-workflow.v1");
            RequireString(root, "title", issues, minimumLength: 3);
            RequireString(root, "objective", issues, minimumLength: 20);
            RequireNonNegativeInteger(root, "iteration", issues);
            RequireObject(root, "scope", issues);
            RequireStringArray(root, "assumptions", issues, minimumCount: 1);
            RequireStringArray(root, "acceptanceCriteria", issues, minimumCount: 1);
            RequireArray(root, "verificationCommands", issues, minimumCount: 1);
            RequireArray(root, "artifacts", issues, minimumCount: 0);
            RequireArray(root, "openQuestions", issues, minimumCount: 0);
            RequireObject(root, "lastRun", issues);
        }

        return Task.FromResult(new CodingCampaignValidationResult(issues.Count == 0, issues, []));
    }

    private static bool TryReadContract(string path, out JsonDocument document)
    {
        document = null!;
        if (!File.Exists(path)) return false;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void RequireString(
        JsonElement root,
        string property,
        List<string> issues,
        int minimumLength = 1,
        string? expected = null)
    {
        if (!root.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
            || value.GetString()!.Trim().Length < minimumLength)
        {
            issues.Add($"{ContractRelativePath}.{property} fehlt oder ist zu knapp.");
            return;
        }
        if (expected is not null && !string.Equals(value.GetString()!.Trim(), expected, StringComparison.Ordinal))
        {
            issues.Add($"{ContractRelativePath}.{property} muss exakt '{expected}' sein.");
        }
    }

    private static void RequireNonNegativeInteger(JsonElement root, string property, List<string> issues)
    {
        if (!root.TryGetProperty(property, out var value)
            || !value.TryGetInt32(out var number)
            || number < 0)
        {
            issues.Add($"{ContractRelativePath}.{property} muss eine nichtnegative ganze Zahl sein.");
        }
    }

    private static void RequireObject(JsonElement root, string property, List<string> issues)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            issues.Add($"{ContractRelativePath}.{property} muss ein JSON-Objekt sein.");
        }
    }

    private static void RequireArray(JsonElement root, string property, List<string> issues, int minimumCount)
    {
        if (!root.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() < minimumCount)
        {
            issues.Add($"{ContractRelativePath}.{property} muss eine Liste mit mindestens {minimumCount} Einträgen sein.");
        }
    }

    private static void RequireStringArray(JsonElement root, string property, List<string> issues, int minimumCount)
    {
        if (!root.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            issues.Add($"{ContractRelativePath}.{property} muss eine Textliste sein.");
            return;
        }

        var count = value.EnumerateArray().Count(static item =>
            item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()));
        if (count < minimumCount)
        {
            issues.Add($"{ContractRelativePath}.{property} muss mindestens {minimumCount} nicht leere Texteinträge enthalten.");
        }
    }
}
