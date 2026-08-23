using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GoWinUI.App.Services;

public sealed class PhyMaCodingCampaignDefinition(CodingProofVerifier proofVerifier) : ICodingCampaignDefinition
{
    internal const string BookRelativePath = "solutions/PhyMa.md";
    internal const string CatalogRelativePath = "phyma_catalog.json";
    private const string BookRevisionMarkerPrefix = "<!-- phyma-revision:";
    private const string UnitMarkerPrefix = "<!-- phyma-unit:";

    internal static readonly IReadOnlyList<string> RoadmapFieldIds =
    [
        "foundations-logic-set-theory",
        "algebra-number-theory",
        "linear-algebra",
        "geometry-topology",
        "analysis-calculus",
        "differential-equations-dynamical-systems",
        "probability-statistics",
        "discrete-mathematics-computation",
        "numerical-mathematics-optimization",
        "classical-mechanics",
        "continuum-fluid-mechanics",
        "thermodynamics-statistical-physics",
        "electromagnetism",
        "waves-optics",
        "relativity-gravitation",
        "quantum-mechanics",
        "quantum-field-particle-nuclear",
        "condensed-matter-materials",
        "astrophysics-cosmology",
    ];

    private static readonly HashSet<string> AllowedDisciplines = new(StringComparer.Ordinal)
    {
        "mathematics", "physics",
    };

    private static readonly HashSet<string> AllowedRoadmapStatuses = new(StringComparer.Ordinal)
    {
        "planned", "in-progress", "covered",
    };

    private static readonly HashSet<string> AllowedFormalLibraries = new(StringComparer.Ordinal)
    {
        "lean-core", "mathlib", "physlib", "leancert",
    };

    private static readonly HashSet<string> AllowedCrossChecks = new(StringComparer.Ordinal)
    {
        "alternative-derivation",
        "dimensional-analysis",
        "limit-case",
        "special-case",
        "symmetry",
        "conservation-law",
        "invariant",
        "independent-algebra",
    };

    private static readonly HashSet<string> RequiredFormalCoverage = new(StringComparer.Ordinal)
    {
        "definitions", "assumptions", "theorem", "derivation",
    };

    private static readonly HashSet<string> AllowedNumericalSampleKinds = new(StringComparer.Ordinal)
    {
        "reference", "boundary", "limit", "generic",
    };

    private static readonly string[] Challenges =
    [
        "Mathematische Grundlagen, Logik, Mengenlehre und Beweismethoden",
        "Algebra, Zahlentheorie und diskrete Strukturen",
        "Lineare Algebra, Tensorrechnung und Spektraltheorie",
        "Geometrie, Differentialgeometrie und Topologie",
        "Analysis, Maßtheorie, Funktionalanalysis und Variationsrechnung",
        "Differentialgleichungen, dynamische Systeme und Stabilität",
        "Wahrscheinlichkeit, Statistik und stochastische Prozesse",
        "Numerische Mathematik, Intervallarithmetik und Optimierung",
        "Klassische Mechanik, Kontinuums- und Strömungsmechanik",
        "Thermodynamik und statistische Physik",
        "Elektrodynamik, Wellen und Optik",
        "Relativität, Gravitation, Astrophysik und Kosmologie",
        "Quantenmechanik, Quantenfeldtheorie sowie Teilchen- und Kernphysik",
        "Festkörperphysik, Materialmodelle und Vielteilchensysteme",
    ];

    public CodingCampaignDescriptor Descriptor { get; } = new(
        "phyma",
        "PhyMa",
        "Ein fortlaufend wachsendes, dreifach validiertes Lehrbuch über Mathematik und Physik.",
        "Physik und Mathematik",
        ["solutions", "proofs", "simulation_data", "visualizations"]);

    public bool PublishSolutionsOnlyAfterValidation => true;

    public bool HasFoundation(string workspacePath) =>
        File.Exists(Path.Combine(workspacePath, CatalogRelativePath))
        && File.Exists(Path.Combine(workspacePath, BookRelativePath.Replace('/', Path.DirectorySeparatorChar)));

    public int ReadIteration(string workspacePath)
    {
        var path = Path.Combine(workspacePath, CatalogRelativePath);
        if (!TryReadJson(path, out var document, out _)) return 0;
        using (document)
        {
            return document.RootElement.TryGetProperty("revision", out var revision)
                   && revision.TryGetInt32(out var value)
                   && value >= 0
                ? value
                : 0;
        }
    }

    public string GetChallenge(int iteration) => Challenges[Math.Abs(iteration) % Challenges.Length];

    public string BuildBootstrapPrompt() => $$"""
        Erstelle im freigegebenen Workspace den fortsetzbaren Coding-Workflow „PhyMa“: ein deutschsprachiges,
        zusammenhängendes Lehrbuch, das Mathematik und Physik von den Grundlagen bis zu modernen Theorien zunehmend
        vollständig, korrekt und gut lesbar ausarbeitet. Das Projekt ist absichtlich ein Dauerprojekt. Behaupte nie,
        das gesamte Wissen sei abgeschlossen; führe einen transparenten Themenfahrplan und erweitere in jedem
        erfolgreichen Lauf die fachliche Substanz des Buches.

        Autoritative Dateien und Verzeichnisse:

        - `{{BookRelativePath}}`: einziges fortlaufendes Buchmanuskript in KaTeX-kompatiblem Markdown;
        - `{{CatalogRelativePath}}`: einziger maschinenlesbarer Katalog für Revision, Themenfahrplan und validierte Einheiten;
        - `test_phyma.py`: unabhängige Vertrags- und Konsistenztests für Katalog, Belege und numerische Daten;
        - `lean-toolchain` mit `{{LeanProofService.PinnedLeanToolchain}}` und ein Lake-Projekt mit der gepinnten
          Mathlib-Revision `{{LeanProofService.PinnedMathlibRevision}}`; `proof.lean` legt diese Dateien bei einem
          `import Mathlib` bei Bedarf automatisch an und lädt den vorkompilierten Cache;
        - `proofs/<unitId>/formal/proof.json` mit einer Lean-Datei;
        - `proofs/<unitId>/analytic/proof.json` mit einem ausführbaren symbolischen Python-Checker;
        - `proofs/<unitId>/numerical/proof.json` mit einer davon unabhängigen numerischen Implementierung;
        - `simulation_data/<unitId>.json` mit reproduzierbaren Stichproben und Fehlerwerten;
        - optional fachlich relevante Abbildungen unter `visualizations/`.

        Erzeuge und aktualisiere nach jeder bestandenen Abnahme eine für Menschen lesbare A4-PDF
        `solutions/PhyMa.pdf`, die `solutions/PhyMa.md` mit korrekt gerendertem KaTeX/LaTeX wiedergibt. Nutze dafür
        die GO-PDF-Exportlogik oder eine gleichwertige lokale HTML/Chromium-Renderstrecke; rohe LaTeX-Quellen,
        ungerenderte Formeln oder ein leeres Platzhalter-PDF gelten nicht als Ergebnis. Lege dabei kein zweites
        Buchmanuskript an. Beginne mit einem Vorwort, einer Inhaltsübersicht, einer konsistenten
        Notations- und Einheitenkonvention sowie mindestens einer belastbar validierten mathematischen und einer
        belastbar validierten physikalischen Lerneinheit. Formuliere wie in einem guten Fachbuch: Motivation,
        Definitionen, Voraussetzungen, Satz oder physikalisches Gesetz, schrittweise Herleitung, Beispiele,
        Gültigkeitsbereich, Interpretation, Verbindungen zu anderen Kapiteln und Grenzen der Aussage.

        `{{CatalogRelativePath}}` enthält exakt eine positive ganzzahlige `revision`, `book` mit dem Wert
        `{{BookRelativePath}}`, `roadmap` und `units`. Der Themenfahrplan enthält jeden der folgenden IDs genau einmal:

        {{string.Join(Environment.NewLine, RoadmapFieldIds.Select(static field => "- " + field))}}

        Ein Roadmap-Eintrag enthält `id`, `title`, `discipline` (`mathematics` oder `physics`) und `status`
        (`planned`, `in-progress` oder `covered`). Eine `units`-Einheit ist ausschließlich vollständig validierter
        Buchinhalt und enthält:

        - `id`, `title`, `discipline`, `field`, `status: validated`, `statement` und `validityDomain`;
        - nicht leere Listen `definitions`, `assumptions` und `formalCoverage`;
        - `formalCoverage` enthält `definitions`, `assumptions`, `theorem` und `derivation`;
        - `formalLibrary` ist `lean-core`, `mathlib`, `physlib` oder `leancert`;
        - `crossChecks` enthält `alternative-derivation` und mindestens einen weiteren unabhängigen Check aus
          `dimensional-analysis`, `limit-case`, `special-case`, `symmetry`, `conservation-law`, `invariant` oder
          `independent-algebra`;
        - `formalManifest`, `analyticManifest`, `numericalManifest` und `numericalData` als relative Workspacepfade.

        Das Buch enthält `<!-- phyma-revision:N -->` passend zur Katalogrevision und für jede Einheit unmittelbar vor
        ihrem Abschnitt `<!-- phyma-unit:<unitId> -->`. Jede Einheit und jede Kapitelüberschrift darf in
        `solutions/PhyMa.md` nur einmal vorkommen; erweitere vorhandene Abschnitte, statt sie erneut anzuhängen.
        Nutze gültige LaTeX-Begrenzer `$...$`, `$$...$$`, `\(...\)` oder `\[...\]`; Gleichungen stehen nie in
        Markdown-Codezäunen.

        Jeder Katalogeintrag muss vor Aufnahme in das Buch drei voneinander getrennte Gates bestehen:

        1. Formal: Formuliere Definitionen, Voraussetzungen, den zentralen Satz und die entscheidenden
           Herleitungsschritte in Lean. Nutze für Mengenlehre, Kardinalitäten, Analysis, Algebra und vergleichbar
           umfangreiche Gebiete ausdrücklich `import Mathlib`; GO stellt das gepinnte Lake-Projekt automatisch bereit.
           Ein gescheiterter Lean-Core-Versuch ist keine Begründung für eine Beweislücke. Verwende `proof.lean check`
           iterativ und abschließend zwingend `proof.lean verify` für den exakt benannten Theoremnamen. `sorry`, `admit`,
           `axiom`, `sorryAx`, `Lean.trustCompiler`, ungeprüfte eigene Axiome und vergleichbare Beweislücken sind verboten.
           Physlib darf für spezialisierte physikalische Formalisierungen zusätzlich revisionsgenau eingebunden werden.
        2. Analytisch: Prüfe dieselbe Aussage mit einem separaten symbolischen Checker über alternative Herleitung und
           passende Dimensions-, Grenzfall-, Symmetrie-, Erhaltungs-, Invarianten- oder Spezialfalltests.
        3. Numerisch: Implementiere dieselbe Aussage unabhängig vom symbolischen Checker. Prüfe mindestens drei echte
           Stichproben, darunter einen bekannten Referenzfall und einen Grenz- oder Randfall. Dokumentiere Methode,
           Eingaben, Soll- und Istwert, absolute und relative Fehler sowie begründete Toleranzen. Nutze nach Möglichkeit
           zertifizierte Intervallarithmetik; LeanCert darf nur revisionsgepinnt und ohne Compilervertrauen eingesetzt
           werden.

        Für jedes Gate gilt: Checker zuerst erstellen und erfolgreich ausführen, danach den echten SHA-256 bestimmen
        und zuletzt das Manifest schreiben. Der bestehende Manifestvertrag erlaubt nur `caseId`, `kind`, `statement`,
        `assumptions`, `validityDomain`, `artifact`, `sourceSha256` und bei `formal` zusätzlich `theoremName`.
        Der formale Typ ist `formal`, der analytische `symbolic`, der numerische `numerical-evidence` oder bei echter
        Zertifizierung `interval-certified`. `numerical-evidence` verwendet einen tatsächlich ausführbaren Python-
        Checker. `interval-certified` darf einen Python-Zertifikatschecker oder einen Lean-/LeanCert-Nachweis mit
        `theoremName` verwenden. Numerische Evidenz allein ist kein mathematischer Beweis.

        Führe alle Checker, `test_phyma.py`, Syntaxprüfungen und begrenzte Laufzeit-Smokes aus. Repariere Fehler ohne
        Prüfungen abzuschwächen. Halte Abhängigkeiten gepinnt, nutze bei Python eine lokale `.venv`, schreibe striktes
        RFC-8259-JSON ohne NaN oder Infinity und halte Caches sowie generierte Umgebungen aus Git.
        """;

    public string BuildIterationPrompt(int iteration, string challenge) => $$"""
        Arbeite in PhyMa-Revision {{iteration}} am vorhandenen einzigen Buch- und Katalogbestand weiter. Beginne kein
        Parallelprojekt und ersetze keine bereits bestandene Herleitung durch eine bloße Zusammenfassung.

        Möglicher Themenraum: {{challenge}}

        Wähle anhand von `phyma_catalog.json`, bestehenden Beweisen, numerischen Daten, Querverweisen und erkennbaren
        fachlichen Lücken selbst den sinnvollsten nächsten Schritt. Du darfst eine bestehende Einheit substanziell
        korrigieren oder vertiefen oder eine neue Einheit aus einem unterrepräsentierten Roadmap-Gebiet aufnehmen.
        Achte auf einen didaktisch schlüssigen Aufbau: neue Begriffe benötigen Voraussetzungen, Symboldefinitionen und
        Verweise auf frühere Kapitel. Verknüpfe Mathematik und Physik, ohne mathematische Sätze mit empirischen
        Naturgesetzen gleichzusetzen. Kennzeichne Näherungen, Konventionen, Einheiten und Gültigkeitsbereiche explizit.

        Eine neue oder geänderte Einheit darf erst als `validated` in Katalog und Buch erscheinen, wenn alle drei
        voneinander getrennten Gates für exakt dieselbe Kernaussage bestanden sind:

        - formaler Lean-Nachweis mit `proof.lean verify` und Axiomprüfung;
        - ausführbarer symbolisch-analytischer Cross-Check mit alternativer Herleitung und mindestens einem weiteren
          geeigneten Strukturtest;
        - unabhängige numerische Prüfung mit mindestens drei Stichproben einschließlich Referenz- und Grenzfall,
          nachvollziehbaren Toleranzen und neu erzeugten `simulation_data`.

        Nutze für geeignete Aussagen die von GO gepinnte Mathlib-Revision über `import Mathlib`; `proof.lean` bereitet
        Lake und den Binärcache automatisch vor. Behaupte niemals, Lean Core sei eine technische Grenze, bevor der
        Mathlib-Pfad mit `proof.lean check` und `proof.lean verify` tatsächlich geprüft wurde. Nutze Physlib oder LeanCert
        nur, wenn die Abhängigkeit revisionsgenau reproduzierbar ist. LeanCert-
        Zertifikate verwenden den Kernel-Vertrauenspfad; ein Nachweis mit `Lean.trustCompiler`, `sorry`, `admit`,
        eigenen Axiomen oder unvollständigen importierten Beweisen ist nicht bestanden. Bezeichne numerische
        Stichproben ohne Zertifikat ausschließlich als Evidenz.

        Aktualisiere nach erfolgreicher fachlicher Änderung `solutions/PhyMa.md`, `solutions/PhyMa.pdf`,
        `phyma_catalog.json`, die drei Beweismanifeste, Checker, numerischen Daten und betroffene Tests konsistent.
        Die PDF muss das Buch im A4-Buchformat mit gerendertem KaTeX darstellen; rohe Formelquellen oder ein
        Platzhalter-PDF reichen nicht. Schreibe neue Inhalte in den passenden bestehenden Abschnitt oder in genau
        einen neuen Abschnitt mit neuer `phyma-unit`; doppelte Einträge in `solutions/PhyMa.md` sind ungültig.
        Erhöhe die Revision und den
        Revisionsmarker nur bei echter inhaltlicher Verbesserung. Das Buch muss weiterhin flüssig lesbar sein und
        Definitionen, Voraussetzungen, Herleitung, Beispiele, Interpretation, Cross-Checks und Grenzen enthalten.
        Führe anschließend alle betroffenen Checker, Lean verify, `test_phyma.py`, Syntax-/Buildprüfung und einen
        begrenzten Smoke-Test aus. Der Dauerworkflow endet nach einer bestandenen Revision nicht, sondern sucht danach
        selbstständig die nächste fachlich wertvolle Lücke.
        """;

    public string BuildCorrectionPrompt(int iteration, string challenge, IReadOnlyList<string> issues) => $$"""
        Die unabhängige PhyMa-Abnahme der Revision {{iteration}} meldet folgende konkrete Vertrags-, Beweis- oder
        Rechenfehler:

        {{string.Join(Environment.NewLine, issues.Select(static issue => "- " + issue))}}

        Repariere die Ursachen im bestehenden Manuskript, Katalog, Lean-Nachweis, analytischen Checker, numerischen
        Checker, den Simulationsdaten und Tests. Schwäche kein Gate und entferne keine fachliche Aussage nur, um eine
        Prüfung zu umgehen. Stelle sicher, dass alle drei Manifeste exakt dieselbe Kernaussage und denselben `caseId`
        prüfen, ihre Artefakte tatsächlich ausgeführt wurden und ihre SHA-256-Werte aktuell sind. Verwende weder
        `sorry`, `admit`, eigene Axiome noch Compilervertrauen. Prüfe Grenzfälle und Fehlerwerte neu, aktualisiere das
        Buch didaktisch konsistent und führe danach sämtliche drei Gates sowie `test_phyma.py` erneut aus.
        """;

    public async Task<CodingCampaignValidationResult> ValidateAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var proofs = await proofVerifier.VerifyAllAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        return ValidateWorkspaceArtifacts(workspacePath, proofs);
    }

    internal static CodingCampaignValidationResult ValidateWorkspaceArtifacts(
        string workspacePath,
        IReadOnlyList<CodingProofVerificationResult> proofs)
    {
        var issues = new List<string>();
        var bookPath = Path.Combine(workspacePath, BookRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var catalogPath = Path.Combine(workspacePath, CatalogRelativePath);
        RequireNonEmptyFile(bookPath, BookRelativePath, issues);
        RequireNonEmptyFile(catalogPath, CatalogRelativePath, issues);
        RequireNonEmptyFile(Path.Combine(workspacePath, "test_phyma.py"), "test_phyma.py", issues);
        RequireDirectory(Path.Combine(workspacePath, "proofs"), "proofs/", issues);
        RequireDirectory(Path.Combine(workspacePath, "simulation_data"), "simulation_data/", issues);

        if (!TryReadJson(catalogPath, out var catalog, out var jsonError))
        {
            issues.Add($"phyma_catalog.json ist kein striktes RFC-8259-JSON: {jsonError}");
            return new(false, issues, proofs);
        }

        using (catalog)
        {
            var root = catalog.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add("phyma_catalog.json muss ein JSON-Objekt enthalten.");
                return new(false, issues, proofs);
            }

            var revision = ReadPositiveInteger(root, "revision", issues);
            if (!string.Equals(ReadString(root, "book"), BookRelativePath, StringComparison.Ordinal))
            {
                issues.Add($"phyma_catalog.json.book muss exakt '{BookRelativePath}' sein.");
            }

            ValidateRoadmap(root, issues);
            ValidateUnits(workspacePath, root, bookPath, revision, proofs, issues);
        }

        return new(issues.Count == 0, issues, proofs);
    }

    public IReadOnlySet<string>? GetPublishableSolutionDocuments(string workspacePath) =>
        File.Exists(Path.Combine(workspacePath, BookRelativePath.Replace('/', Path.DirectorySeparatorChar)))
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BookRelativePath }
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public string GetSolutionPublicationHeading(
        string workspacePath,
        string relativePath,
        string fallbackHeading) =>
        relativePath.Equals(BookRelativePath, StringComparison.OrdinalIgnoreCase)
            ? "PhyMa · validierte Buchrevision"
            : fallbackHeading;

    private static void ValidateRoadmap(JsonElement root, List<string> issues)
    {
        if (!root.TryGetProperty("roadmap", out var roadmap) || roadmap.ValueKind != JsonValueKind.Array)
        {
            issues.Add("phyma_catalog.json benötigt eine roadmap-Liste.");
            return;
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in roadmap.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                issues.Add("Ein Roadmap-Eintrag ist kein Objekt.");
                continue;
            }
            var id = ReadString(item, "id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id) || !found.Add(id))
            {
                issues.Add(string.IsNullOrWhiteSpace(id)
                    ? "Ein Roadmap-Eintrag besitzt keine id."
                    : $"Roadmap-ID {id} ist mehrfach vorhanden.");
            }
            RequireString(item, id, "title", issues);
            RequireEnum(item, id, "discipline", AllowedDisciplines, issues);
            RequireEnum(item, id, "status", AllowedRoadmapStatuses, issues);
        }

        foreach (var required in RoadmapFieldIds.Where(required => !found.Contains(required)))
        {
            issues.Add($"Pflichtgebiet fehlt im PhyMa-Themenfahrplan: {required}.");
        }
        foreach (var unexpected in found.Where(field => !RoadmapFieldIds.Contains(field, StringComparer.Ordinal)))
        {
            issues.Add($"Unbekannte Roadmap-ID: {unexpected}.");
        }
    }

    private static void ValidateUnits(
        string workspacePath,
        JsonElement root,
        string bookPath,
        int revision,
        IReadOnlyList<CodingProofVerificationResult> proofs,
        List<string> issues)
    {
        if (!root.TryGetProperty("units", out var units) || units.ValueKind != JsonValueKind.Array)
        {
            issues.Add("phyma_catalog.json benötigt eine units-Liste.");
            return;
        }
        if (units.GetArrayLength() < 2)
        {
            issues.Add("PhyMa benötigt mindestens eine validierte mathematische und eine validierte physikalische Starteinheit.");
        }

        var book = File.Exists(bookPath) ? File.ReadAllText(bookPath) : string.Empty;
        ValidateBookShell(book, revision, issues);
        ValidateNoDuplicateBookEntries(book, issues);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasMathematics = false;
        var hasPhysics = false;
        foreach (var unit in units.EnumerateArray())
        {
            if (unit.ValueKind != JsonValueKind.Object)
            {
                issues.Add("Eine PhyMa-Einheit ist kein JSON-Objekt.");
                continue;
            }
            var id = ReadString(unit, "id") ?? "unbekannt";
            if (id == "unbekannt" || !ids.Add(id))
            {
                issues.Add(id == "unbekannt" ? "Eine PhyMa-Einheit besitzt keine id." : $"PhyMa-Einheit {id} ist mehrfach vorhanden.");
                continue;
            }

            RequireString(unit, id, "title", issues);
            RequireString(unit, id, "statement", issues, minimumLength: 30);
            RequireString(unit, id, "validityDomain", issues, minimumLength: 15);
            var discipline = RequireEnum(unit, id, "discipline", AllowedDisciplines, issues);
            hasMathematics |= discipline == "mathematics";
            hasPhysics |= discipline == "physics";
            RequireEnum(unit, id, "status", new HashSet<string>(StringComparer.Ordinal) { "validated" }, issues);
            var field = RequireEnum(unit, id, "field", RoadmapFieldIds.ToHashSet(StringComparer.Ordinal), issues);
            _ = field;
            RequireEnum(unit, id, "formalLibrary", AllowedFormalLibraries, issues);
            RequireNonEmptyStringArray(unit, id, "definitions", issues);
            RequireNonEmptyStringArray(unit, id, "assumptions", issues);
            ValidateFormalCoverage(unit, id, issues);
            ValidateCrossChecks(unit, id, issues);

            var formalManifest = RequireRelativeFile(unit, id, "formalManifest", workspacePath, "proofs", issues);
            var analyticManifest = RequireRelativeFile(unit, id, "analyticManifest", workspacePath, "proofs", issues);
            var numericalManifest = RequireRelativeFile(unit, id, "numericalManifest", workspacePath, "proofs", issues);
            var numericalData = RequireRelativeFile(unit, id, "numericalData", workspacePath, "simulation_data", issues);
            if (new[] { formalManifest, analyticManifest, numericalManifest }
                .Where(static path => path is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            {
                issues.Add($"Einheit {id}: formaler, analytischer und numerischer Nachweis müssen getrennte Manifeste verwenden.");
            }

            ValidateProofGate(id, formalManifest, CodingProofKind.Formal, proofs, issues);
            ValidateProofGate(id, analyticManifest, CodingProofKind.Symbolic, proofs, issues);
            ValidateNumericalProofGate(id, numericalManifest, numericalData, workspacePath, proofs, issues);
            ValidateIndependentArtifacts(id, formalManifest, analyticManifest, numericalManifest, workspacePath, issues);
            ValidateBookUnit(book, id, ReadString(unit, "title") ?? id, issues);
        }

        if (!hasMathematics) issues.Add("PhyMa enthält noch keine validierte mathematische Lerneinheit.");
        if (!hasPhysics) issues.Add("PhyMa enthält noch keine validierte physikalische Lerneinheit.");
    }

    private static void ValidateBookShell(string book, int revision, List<string> issues)
    {
        if (book.Length < 4_000) issues.Add("Das PhyMa-Manuskript ist für eine lesbare erste Buchrevision zu knapp.");
        if (!book.TrimStart().StartsWith("# PhyMa", StringComparison.OrdinalIgnoreCase))
            issues.Add("Das PhyMa-Manuskript muss mit der Buchüberschrift '# PhyMa' beginnen.");
        RequireBookConcept(book, ["Vorwort", "Einleitung"], "Vorwort oder Einleitung", issues);
        RequireBookConcept(book, ["Inhaltsübersicht", "Inhaltsverzeichnis"], "Inhaltsübersicht", issues);
        RequireBookConcept(book, ["Notation", "Konvention"], "Notations- und Konventionsabschnitt", issues);
        if (revision > 0 && !book.Contains($"{BookRevisionMarkerPrefix}{revision} -->", StringComparison.OrdinalIgnoreCase))
            issues.Add($"Das Buch enthält nicht den zur Katalogrevision {revision} passenden Revisionsmarker.");
    }

    private static void ValidateNoDuplicateBookEntries(string book, List<string> issues)
    {
        var markers = Regex.Matches(
            book,
            @"<!--\s*phyma-unit\s*:\s*([^>\s]+)\s*-->",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var duplicate in markers
                     .Select(static match => match.Groups[1].Value.Trim())
                     .Where(static id => id.Length > 0)
                     .GroupBy(static id => id, StringComparer.OrdinalIgnoreCase)
                     .Where(static group => group.Count() > 1))
        {
            issues.Add($"solutions/PhyMa.md enthält den Einheitseintrag {duplicate.Key} mehrfach.");
        }

        var headings = Regex.Matches(
            book,
            @"(?m)^\s*##\s+(.+?)\s*$",
            RegexOptions.CultureInvariant);
        foreach (var duplicate in headings
                     .Select(static match => NormalizeBookHeading(match.Groups[1].Value))
                     .Where(static heading => heading.Length > 0)
                     .GroupBy(static heading => heading, StringComparer.OrdinalIgnoreCase)
                     .Where(static group => group.Count() > 1))
        {
            issues.Add($"solutions/PhyMa.md enthält die Kapitelüberschrift '{duplicate.Key}' mehrfach.");
        }
    }

    private static string NormalizeBookHeading(string value)
    {
        var withoutMarkup = Regex.Replace(value, @"[`*_#]+", string.Empty, RegexOptions.CultureInvariant);
        return Regex.Replace(withoutMarkup, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static void ValidateBookUnit(string book, string id, string title, List<string> issues)
    {
        var marker = $"{UnitMarkerPrefix}{id} -->";
        var start = book.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            issues.Add($"Einheit {id}: Buchabschnitt mit Marker '{marker}' fehlt.");
            return;
        }
        var next = book.IndexOf(UnitMarkerPrefix, start + marker.Length, StringComparison.OrdinalIgnoreCase);
        var section = next < 0 ? book[start..] : book[start..next];
        if (section.Length < 1_200) issues.Add($"Einheit {id}: Der Buchabschnitt ist für eine nachvollziehbare Herleitung zu knapp.");
        if (!section.Contains(title, StringComparison.OrdinalIgnoreCase)) issues.Add($"Einheit {id}: Der Buchabschnitt nennt seinen Titel nicht.");
        RequireBookConcept(section, ["Definition"], $"Definitionen in Einheit {id}", issues);
        RequireBookConcept(section, ["Voraussetzung", "Annahme"], $"Voraussetzungen in Einheit {id}", issues);
        RequireBookConcept(section, ["Satz", "Theorem", "Gesetz"], $"Satz oder Gesetz in Einheit {id}", issues);
        RequireBookConcept(section, ["Herleitung", "Beweis"], $"Herleitung in Einheit {id}", issues);
        RequireBookConcept(section, ["Analytischer Cross-Check", "Analytische Gegenprüfung"], $"analytischen Cross-Check in Einheit {id}", issues);
        RequireBookConcept(section, ["Numerische Validierung", "Numerische Gegenprüfung"], $"numerische Validierung in Einheit {id}", issues);
        RequireBookConcept(section, ["Gültigkeitsbereich", "Gültigkeit"], $"Gültigkeitsbereich in Einheit {id}", issues);
        if (!section.Contains('$') && !section.Contains("\\(", StringComparison.Ordinal))
            issues.Add($"Einheit {id}: Es fehlt eine KaTeX-kompatibel begrenzte mathematische Formel.");
    }

    private static void ValidateFormalCoverage(JsonElement unit, string id, List<string> issues)
    {
        var coverage = ReadStringArray(unit, "formalCoverage");
        foreach (var required in RequiredFormalCoverage.Where(required => !coverage.Contains(required, StringComparer.Ordinal)))
            issues.Add($"Einheit {id}: formale Abdeckung fehlt für {required}.");
    }

    private static void ValidateCrossChecks(JsonElement unit, string id, List<string> issues)
    {
        var values = ReadStringArray(unit, "crossChecks").Distinct(StringComparer.Ordinal).ToArray();
        if (!values.Contains("alternative-derivation", StringComparer.Ordinal))
            issues.Add($"Einheit {id}: Der analytische Cross-Check benötigt eine alternative Herleitung.");
        if (values.Count(value => AllowedCrossChecks.Contains(value)) < 2)
            issues.Add($"Einheit {id}: Mindestens zwei unterschiedliche analytische Cross-Checks fehlen.");
        foreach (var unexpected in values.Where(value => !AllowedCrossChecks.Contains(value)))
            issues.Add($"Einheit {id}: Unbekannter analytischer Cross-Check {unexpected}.");
    }

    private static void ValidateProofGate(
        string id,
        string? manifest,
        CodingProofKind expectedKind,
        IReadOnlyList<CodingProofVerificationResult> proofs,
        List<string> issues)
    {
        if (manifest is null) return;
        var result = proofs.FirstOrDefault(proof =>
            proof.ManifestPath.Equals(manifest, StringComparison.OrdinalIgnoreCase));
        if (result is null)
        {
            issues.Add($"Einheit {id}: Kein ausgeführtes Prüfergebnis für {manifest}.");
            return;
        }
        if (!result.CaseId.Equals(id, StringComparison.OrdinalIgnoreCase))
            issues.Add($"Einheit {id}: Manifest {manifest} prüft den abweichenden caseId {result.CaseId}.");
        if (result.Kind != expectedKind)
            issues.Add($"Einheit {id}: Manifest {manifest} muss vom Typ {expectedKind} sein.");
        if (!result.IsProof || !result.Passed)
            issues.Add($"Einheit {id}: Pflichtnachweis {manifest} ist nicht bestanden: {result.Detail}");
    }

    private static void ValidateNumericalProofGate(
        string id,
        string? manifest,
        string? data,
        string workspacePath,
        IReadOnlyList<CodingProofVerificationResult> proofs,
        List<string> issues)
    {
        CodingProofVerificationResult? result = null;
        if (manifest is not null)
        {
            result = proofs.FirstOrDefault(proof => proof.ManifestPath.Equals(manifest, StringComparison.OrdinalIgnoreCase));
            if (result is null)
                issues.Add($"Einheit {id}: Kein ausgeführtes numerisches Prüfergebnis für {manifest}.");
            else
            {
                if (!result.CaseId.Equals(id, StringComparison.OrdinalIgnoreCase))
                    issues.Add($"Einheit {id}: Numerisches Manifest prüft den abweichenden caseId {result.CaseId}.");
                if (result.Kind is not (CodingProofKind.NumericalEvidence or CodingProofKind.IntervalCertified))
                    issues.Add($"Einheit {id}: Numerisches Manifest muss numerical-evidence oder interval-certified sein.");
                if (!result.Passed)
                    issues.Add($"Einheit {id}: Numerische Validierung ist nicht bestanden: {result.Detail}");
            }
        }
        if (data is not null)
            ValidateNumericalData(workspacePath, data, id, result?.Kind == CodingProofKind.IntervalCertified, issues);
    }

    private static void ValidateNumericalData(
        string workspacePath,
        string relativePath,
        string id,
        bool intervalCertified,
        List<string> issues)
    {
        var fullPath = ResolveInside(workspacePath, relativePath);
        if (fullPath is null)
        {
            issues.Add($"Einheit {id}: numericalData verlässt den freigegebenen Workspace.");
            return;
        }
        if (!TryReadJson(fullPath, out var document, out var error))
        {
            issues.Add($"Einheit {id}: numerische Daten sind kein striktes JSON: {error}");
            return;
        }
        using (document)
        {
            var root = document.RootElement;
            if (!string.Equals(ReadString(root, "caseId"), id, StringComparison.OrdinalIgnoreCase))
                issues.Add($"Einheit {id}: numericalData besitzt einen abweichenden caseId.");
            RequireString(root, id, "method", issues, minimumLength: 10);
            RequireString(root, id, "independentImplementation", issues, minimumLength: 10);
            var absoluteTolerance = ReadFiniteNonNegative(root, "absoluteTolerance", id, issues);
            var relativeTolerance = ReadFiniteNonNegative(root, "relativeTolerance", id, issues);
            var declaredAbsolute = ReadFiniteNonNegative(root, "maxAbsoluteError", id, issues);
            var declaredRelative = ReadFiniteNonNegative(root, "maxRelativeError", id, issues);
            if (absoluteTolerance == 0 && relativeTolerance == 0)
                issues.Add($"Einheit {id}: Mindestens eine positive numerische Toleranz ist erforderlich.");
            if (intervalCertified
                && (!root.TryGetProperty("intervalCertified", out var certificate)
                    || certificate.ValueKind is not JsonValueKind.True))
                issues.Add($"Einheit {id}: interval-certified benötigt intervalCertified=true in den numerischen Daten.");

            if (!root.TryGetProperty("samples", out var samples)
                || samples.ValueKind != JsonValueKind.Array
                || samples.GetArrayLength() < 3)
            {
                issues.Add($"Einheit {id}: Mindestens drei numerische Stichproben fehlen.");
                return;
            }
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            var maximumAbsolute = 0d;
            var maximumRelative = 0d;
            var index = 0;
            foreach (var sample in samples.EnumerateArray())
            {
                index++;
                if (sample.ValueKind != JsonValueKind.Object)
                {
                    issues.Add($"Einheit {id}, Stichprobe {index}: Eintrag ist kein Objekt.");
                    continue;
                }
                RequireString(sample, id, "name", issues);
                var kind = ReadString(sample, "kind") ?? string.Empty;
                if (!AllowedNumericalSampleKinds.Contains(kind))
                    issues.Add($"Einheit {id}, Stichprobe {index}: kind muss reference, boundary, limit oder generic sein.");
                else
                    kinds.Add(kind);
                if (!sample.TryGetProperty("input", out var input)
                    || input.ValueKind != JsonValueKind.Object
                    || !input.EnumerateObject().Any())
                    issues.Add($"Einheit {id}, Stichprobe {index}: input muss ein nicht leeres Objekt sein.");
                var observed = ReadFinite(sample, "observed", id, index, issues);
                var reference = ReadFinite(sample, "reference", id, index, issues);
                var absolute = ReadFiniteNonNegative(sample, "absoluteError", id, issues);
                var relative = ReadFiniteNonNegative(sample, "relativeError", id, issues);
                if (observed is { } observedValue && reference is { } referenceValue)
                {
                    var calculated = Math.Abs(observedValue - referenceValue);
                    var tolerance = Math.Max(1e-12, calculated * 1e-8);
                    if (Math.Abs(absolute - calculated) > tolerance)
                        issues.Add($"Einheit {id}, Stichprobe {index}: absoluteError stimmt nicht mit observed und reference überein.");
                    var calculatedRelative = Math.Abs(referenceValue) > 1e-15
                        ? calculated / Math.Abs(referenceValue)
                        : calculated;
                    var relativeCheckTolerance = Math.Max(1e-12, calculatedRelative * 1e-8);
                    if (Math.Abs(relative - calculatedRelative) > relativeCheckTolerance)
                        issues.Add($"Einheit {id}, Stichprobe {index}: relativeError stimmt nicht mit observed und reference überein.");
                }
                maximumAbsolute = Math.Max(maximumAbsolute, absolute);
                maximumRelative = Math.Max(maximumRelative, relative);
            }
            if (!kinds.Contains("reference")) issues.Add($"Einheit {id}: Eine bekannte Referenzstichprobe fehlt.");
            if (!kinds.Contains("boundary") && !kinds.Contains("limit")) issues.Add($"Einheit {id}: Eine Grenz- oder Randfallstichprobe fehlt.");
            CompareMaximum(id, "maxAbsoluteError", declaredAbsolute, maximumAbsolute, issues);
            CompareMaximum(id, "maxRelativeError", declaredRelative, maximumRelative, issues);
            if (declaredAbsolute > absoluteTolerance && declaredRelative > relativeTolerance)
                issues.Add($"Einheit {id}: Numerische Fehler überschreiten beide deklarierten Toleranzen.");
        }
    }

    private static void ValidateIndependentArtifacts(
        string id,
        string? formalManifest,
        string? analyticManifest,
        string? numericalManifest,
        string workspacePath,
        List<string> issues)
    {
        var formal = ReadManifestArtifact(workspacePath, formalManifest);
        var analytic = ReadManifestArtifact(workspacePath, analyticManifest);
        var numerical = ReadManifestArtifact(workspacePath, numericalManifest);
        var artifacts = new[] { formal, analytic, numerical }.Where(static path => path is not null).Cast<string>().ToArray();
        if (artifacts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != artifacts.Length)
            issues.Add($"Einheit {id}: Die drei Gates müssen unterschiedliche Checker-Artefakte verwenden.");
        if (analytic is not null && numerical is not null && File.Exists(analytic) && File.Exists(numerical))
        {
            var analyticHash = HashFile(analytic);
            var numericalHash = HashFile(numerical);
            if (analyticHash.Equals(numericalHash, StringComparison.OrdinalIgnoreCase))
                issues.Add($"Einheit {id}: Analytischer und numerischer Checker sind inhaltsgleich und damit nicht unabhängig.");
        }
        ValidateFormalArtifactStructure(id, formal, issues);
    }

    private static void ValidateFormalArtifactStructure(string id, string? formalArtifact, List<string> issues)
    {
        if (formalArtifact is null || !File.Exists(formalArtifact)) return;
        var source = File.ReadAllText(formalArtifact);
        if (!Regex.IsMatch(
                source,
                @"(?m)^\s*(?:(?:private|protected|noncomputable)\s+)*(?:def|abbrev|structure|inductive)\s+[A-Za-z_]",
                RegexOptions.CultureInvariant))
        {
            issues.Add($"Einheit {id}: Der formale Lean-Nachweis enthält keine explizit geprüfte Definition oder Struktur.");
        }
        if (!Regex.IsMatch(
                source,
                @"(?m)^\s*(?:(?:private|protected)\s+)*(?:theorem|lemma)\s+[A-Za-z_]",
                RegexOptions.CultureInvariant))
        {
            issues.Add($"Einheit {id}: Der formale Lean-Nachweis enthält keinen benannten Satz oder kein Lemma.");
        }
    }

    private static string? ReadManifestArtifact(string workspacePath, string? manifestRelativePath)
    {
        if (manifestRelativePath is null) return null;
        var manifest = ResolveInside(workspacePath, manifestRelativePath);
        if (manifest is null || !TryReadJson(manifest, out var document, out _)) return null;
        using (document)
        {
            var artifact = ReadString(document.RootElement, "artifact");
            return artifact is null ? null : ResolveInside(workspacePath, artifact);
        }
    }

    private static string? RequireRelativeFile(
        JsonElement owner,
        string id,
        string property,
        string workspacePath,
        string requiredDirectory,
        List<string> issues)
    {
        var relative = ReadString(owner, property)?.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relative)
            || Path.IsPathRooted(relative)
            || !relative.StartsWith(requiredDirectory + "/", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add($"Einheit {id}: {property} muss ein relativer Pfad unter {requiredDirectory}/ sein.");
            return null;
        }
        var fullPath = ResolveInside(workspacePath, relative);
        if (fullPath is null || !File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
        {
            issues.Add($"Einheit {id}: Referenzierte Datei fehlt oder ist leer: {relative}.");
            return null;
        }
        return relative;
    }

    private static string? ResolveInside(string workspacePath, string relativePath)
    {
        try
        {
            if (Path.IsPathRooted(relativePath)) return null;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
            var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? candidate
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool TryReadJson(string path, out JsonDocument document, out string error)
    {
        document = null!;
        error = string.Empty;
        if (!File.Exists(path))
        {
            error = "Datei fehlt.";
            return false;
        }
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            return true;
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static int ReadPositiveInteger(JsonElement owner, string property, List<string> issues)
    {
        if (!owner.TryGetProperty(property, out var value)
            || !value.TryGetInt32(out var number)
            || number <= 0)
        {
            issues.Add($"phyma_catalog.json.{property} muss eine positive ganze Zahl sein.");
            return 0;
        }
        return number;
    }

    private static string? RequireEnum(
        JsonElement owner,
        string id,
        string property,
        HashSet<string> allowed,
        List<string> issues)
    {
        var value = ReadString(owner, property);
        if (value is null || !allowed.Contains(value))
        {
            issues.Add($"Eintrag {id}: {property} fehlt oder ist ungültig.");
            return null;
        }
        return value;
    }

    private static void RequireString(JsonElement owner, string id, string property, List<string> issues, int minimumLength = 1)
    {
        var value = ReadString(owner, property);
        if (value is null || value.Length < minimumLength)
            issues.Add($"Eintrag {id}: {property} fehlt oder ist zu knapp.");
    }

    private static void RequireNonEmptyStringArray(JsonElement owner, string id, string property, List<string> issues)
    {
        if (ReadStringArray(owner, property).Length == 0)
            issues.Add($"Einheit {id}: {property} muss eine nicht leere Textliste sein.");
    }

    private static string[] ReadStringArray(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                .Select(static item => item.GetString()!.Trim())
                .ToArray()
            : [];

    private static string? ReadString(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static double ReadFiniteNonNegative(JsonElement owner, string property, string id, List<string> issues)
    {
        if (!owner.TryGetProperty(property, out var value)
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number)
            || number < 0)
        {
            issues.Add($"Einheit {id}: {property} fehlt oder ist keine endliche nichtnegative Zahl.");
            return 0;
        }
        return number;
    }

    private static double? ReadFinite(
        JsonElement owner,
        string property,
        string id,
        int sampleIndex,
        List<string> issues)
    {
        if (!owner.TryGetProperty(property, out var value)
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number))
        {
            issues.Add($"Einheit {id}, Stichprobe {sampleIndex}: {property} fehlt oder ist nicht endlich.");
            return null;
        }
        return number;
    }

    private static void CompareMaximum(string id, string name, double declared, double calculated, List<string> issues)
    {
        var tolerance = Math.Max(1e-12, Math.Max(declared, calculated) * 1e-8);
        if (Math.Abs(declared - calculated) > tolerance)
            issues.Add($"Einheit {id}: {name} stimmt nicht mit den Stichproben überein.");
    }

    private static void RequireBookConcept(string text, IReadOnlyList<string> terms, string description, List<string> issues)
    {
        if (!terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)))
            issues.Add($"Das PhyMa-Manuskript dokumentiert {description} nicht erkennbar.");
    }

    private static void RequireNonEmptyFile(string path, string relativePath, List<string> issues)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            issues.Add($"Pflichtartefakt fehlt oder ist leer: {relativePath}");
    }

    private static void RequireDirectory(string path, string relativePath, List<string> issues)
    {
        if (!Directory.Exists(path)) issues.Add($"Pflichtverzeichnis fehlt: {relativePath}");
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
