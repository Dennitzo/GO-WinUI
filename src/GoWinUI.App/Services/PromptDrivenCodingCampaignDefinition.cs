using System.Text.Json;

namespace GoWinUI.App.Services;

/// <summary>
/// The only built-in campaign definition. Concrete workflows are persisted
/// user data and derive their contract from the prompt and workspace.
/// </summary>
public sealed class PromptDrivenCodingCampaignDefinition : ICodingCampaignDefinition
{
    internal const string DescriptorId = "prompt-workflow";
    internal const string ContractRelativePath = ".go-campaign/prompt-workflow.json";

    public CodingCampaignDescriptor Descriptor { get; } = new(
        DescriptorId,
        "Prompt-Workflow",
        "Allgemeiner Coding-Workflow, der Ziel, Artefakte und Verifikation aus Nutzerprompt und Workspace ableitet.",
        "Allgemein",
        [".go-campaign", "tests", "solutions", "artifacts", "visualizations", "simulation_data", "proofs"]);

    public bool HasFoundation(string workspacePath) => File.Exists(GetContractPath(workspacePath));

    public int ReadIteration(string workspacePath)
    {
        if (!TryReadContract(GetContractPath(workspacePath), out var document)) return 0;
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
        Erstelle aus der aktuellen Nutzeranweisung und dem tatsächlichen Workspace einen allgemeinen Coding-Workflow.
        Verwende keine fest codierte Test-, Projekt- oder Domänenvorlage. Leite Ziel, Umfang, Artefakte,
        Akzeptanzkriterien und Prüfverfahren fachlich aus dem Prompt und vorhandenen Repositorydateien ab.

        Lege `{{ContractRelativePath}}` als striktes JSON mit folgender Mindeststruktur an:
        - `schema`: exakt `go.prompt-workflow.v1`;
        - `title`: kurzer fachlicher Titel;
        - `objective`: konkret messbares Ziel;
        - `iteration`: nichtnegative ganze Zahl;
        - `scope`: Objekt mit relevanten Dateien, Artefakten und Grenzen;
        - `assumptions`: nicht leere Liste aktueller Annahmen;
        - `acceptanceCriteria`: nicht leere Liste beobachtbarer Kriterien;
        - `verificationCommands`: Liste realer setup/test/build/start-Schritte;
        - `artifacts`: erwartete oder erzeugte Artefakte;
        - `openQuestions`: fachlich oder technisch offene Punkte;
        - `lastRun`: Status, Änderungen, tatsächlich ausgeführte Prüfungen und nächste Aktion.

        Implementiere den kleinsten belastbaren ersten Schritt. Schreibe fehlende Tests oder Checker im Stil des
        Projekts. Prüfe selbst erstellte Orakel gegen bekannte gültige und ungültige Referenzfälle. Binärartefakte
        entstehen reproduzierbar aus diffbaren Quellen und werden nach dem Erzeugen erneut geöffnet und validiert.
        Bücher oder Berichte pflegen eine kanonische Markdown-/TeX-Quelle; die PDF wird ausschließlich durch den
        deterministischen GO-KaTeX-Exporter erzeugt. Aktualisiere den Vertrag nur mit tatsächlich beobachteten Ergebnissen.
        """;

    public string BuildIterationPrompt(int iteration, string challenge) => $$"""
        Setze den promptabgeleiteten Workflow anhand von `{{ContractRelativePath}}`, der neuesten Nutzeranweisung und
        des aktuellen Workspace-Zustands fort. Es gibt keine vorgegebene Themenfolge. Wähle selbst den fachlich
        stärksten noch offenen, nicht redundanten Schritt aus Akzeptanzkriterien, Fehlern, offenen Fragen oder
        unvalidierten Artefakten.

        Aktualisiere Ziel, Umfang oder Priorität zuerst im Vertrag, wenn die neue Nutzeranweisung dies verlangt.
        Implementiere danach notwendige Änderungen an Code, Tests, Checkern, Generatoren oder Dokumentation. Ein grüner
        Test ist nur belastbar, wenn seine Assertions die fachliche Anforderung abdecken und mindestens ein geeigneter
        Grenz-, Negativ- oder unabhängiger Referenzfall geprüft wurde. Erhöhe `iteration` nur bei echter Verbesserung
        oder einer begründeten neuen Erkenntnis. Führe die dokumentierten Prüfungen wirklich aus und speichere deren
        beobachtete Ergebnisse. Ein angeforderter Dauerlauf bleibt aktiv, bis der Nutzer stoppt oder einen neuen Prompt sendet.
        """;

    public string BuildCorrectionPrompt(int iteration, string challenge, IReadOnlyList<string> issues) => $$"""
        Die unabhängige Abnahme des allgemeinen Workflow-Vertrags meldet:

        {{string.Join(Environment.NewLine, issues.Select(static issue => "- " + issue))}}

        Behebe die Ursachen statt nur die Symptome. Aktualisiere `{{ContractRelativePath}}`, Implementierung, Tests,
        Checker und abgeleitete Artefakte konsistent. Schwäche keine Prüfung und entferne kein Akzeptanzkriterium ohne
        eine belegbare Änderung des Nutzerziels. Führe anschließend die betroffenen Prüfungen erneut aus.
        """;

    public Task<CodingCampaignValidationResult> ValidateAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var issues = new List<string>();
        if (!TryReadContract(GetContractPath(workspacePath), out var document))
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

    private static string GetContractPath(string workspacePath) => Path.Combine(
        workspacePath,
        ContractRelativePath.Replace('/', Path.DirectorySeparatorChar));

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
        if (!root.TryGetProperty(property, out var value) || !value.TryGetInt32(out var number) || number < 0)
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
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
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
