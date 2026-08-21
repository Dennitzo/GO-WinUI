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
