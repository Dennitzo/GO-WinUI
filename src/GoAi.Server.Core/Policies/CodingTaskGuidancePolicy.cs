using System.Text;
using GoAi.Contracts;

namespace GoAi.Server.Core.Policies;

/// <summary>
/// Adds reusable, task-derived quality rules to the coding agent. The rules are
/// deliberately based on capabilities and artifact types, never on a named test
/// or a built-in workflow.
/// </summary>
public static class CodingTaskGuidancePolicy
{
    private static readonly string[] WorkflowTerms =
    [
        "workflow", "dauerlauf", "fortlaufend", "wiederholt", "iteration", "kampagne",
        "continuous", "loop", "fortsetzen", "laufend verbessern",
    ];

    private static readonly string[] SpreadsheetTerms =
    [
        "excel", "xlsx", "xlsm", "arbeitsmappe", "tabellenkalkulation", "spreadsheet", "workbook",
    ];

    private static readonly string[] ScientificTerms =
    [
        "mathemat", "physik", "gleichung", "theorem", "beweis", "herleitung", "simulation",
        "numerisch", "symbolisch", "intervallarithmetik", "lean", "mathlib", "tensor", "metrik",
        "mechanik", "thermodynamik", "quanten", "relativit", "field equation", "differential equation",
    ];

    private static readonly string[] RelativityTerms =
    [
        "einstein", "raumzeit", "relativit", "schwarzschild", "kerr", "ricci", "riemann",
        "energie-impuls", "feldgleichung", "spacetime", "general relativity", "lorentz",
    ];

    private static readonly string[] PublicationTerms =
    [
        "pdf", "katex", "latex", "lehrbuch", "fachbuch", "buch", "manuskript", "publikation",
        "book", "report", "bericht",
    ];

    public static string Build(RunRequest request)
    {
        var taskText = ExtractTaskText(request);
        if (taskText.Length == 0)
        {
            return string.Empty;
        }

        var sections = new List<string>();
        if (ContainsAny(taskText, WorkflowTerms)) sections.Add(WorkflowGuidance);
        if (ContainsAny(taskText, SpreadsheetTerms)) sections.Add(SpreadsheetGuidance);
        if (ContainsAny(taskText, ScientificTerms)) sections.Add(ScientificGuidance);
        if (ContainsAny(taskText, RelativityTerms)) sections.Add(RelativityGuidance);
        if (ContainsAny(taskText, PublicationTerms)) sections.Add(PublicationGuidance);
        return string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private static string ExtractTaskText(RunRequest request)
    {
        var result = new StringBuilder();
        foreach (var message in request.Messages)
        {
            if (!string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var part in message.Content)
            {
                if (!string.IsNullOrWhiteSpace(part.Text)) result.AppendLine(part.Text);
                if (!string.IsNullOrWhiteSpace(part.MediaType)) result.AppendLine(part.MediaType);
            }
        }

        if (request.Workspace is not null)
        {
            result.AppendLine(request.Workspace.Name);
            result.AppendLine(request.Workspace.RepositoryMap);
        }
        return result.ToString().ToLowerInvariant();
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> terms) =>
        terms.Any(term => text.Contains(term, StringComparison.Ordinal));

    private const string WorkflowGuidance = """
        Promptabgeleiteter Workflow:
        - Erzeuge keine fest verdrahtete Themenfolge. Leite Ziel, Teilziele, Artefakte, Annahmen, Akzeptanzkriterien,
          Verifikationsbefehle und den naechsten Schritt aus der aktuellen Nutzeranweisung und dem Workspace ab.
        - Speichere bei einem mehrstufigen oder wiederholbaren Auftrag einen strikten, workspace-lokalen Vertrag unter
          `.go-campaign/prompt-workflow.json`. Eine neue Nutzeranweisung aktualisiert diesen Vertrag und hat Vorrang vor
          einem frueheren naechsten Schritt.
        - Waehle in jeder Iteration selbst den staerksten noch offenen, nicht redundanten Arbeitsschritt. Ein einzelnes
          erfolgreiches Ergebnis beendet einen angeforderten Dauerlauf nicht. Reine Wiederholungen ohne neue Evidenz,
          Quelltextaenderung oder begruendete Erkenntnis gelten nicht als Fortschritt.
        - Halte den Vertrag als Arbeitszustand, nicht als Erfolgsbeleg. Ergebnisse gelten erst nach den im Vertrag
          festgelegten und tatsaechlich ausgefuehrten Pruefungen.
        """;

    private const string SpreadsheetGuidance = """
        Tabellen- und Excel-Artefakte:
        - Modell und Eingabedaten werden zuerst in einer reproduzierbaren Textquelle oder einem Generator beschrieben.
          Erzeuge oder bearbeite XLSX/XLSM ausschliesslich mit einer formatbewussten Bibliothek der vorhandenen Toolchain.
        - Trenne Eingaben, abgeleitete Werte, Einheiten, Annahmen und Ergebnisse sichtbar. Verwende echte Tabellenformeln
          statt fest eingetragener Resultate; OOXML-Formeln nutzen englische Funktionsnamen und Kommas.
        - Gestalte Arbeitsblaetter lesbar: eindeutige Titel, Einheiten in Ueberschriften, geeignete Zahlenformate,
          Spaltenbreiten, eingefrorene Kopfzeilen, Filter, Druckbereich und nur fachlich hilfreiche Diagramme.
        - Oeffne die erzeugte Datei nach dem Schreiben erneut. Pruefe Blattnamen, Zelltypen, Formeln, Referenzen,
          Selbstbezuege, Wertebereiche, benannte Bereiche, Diagrammquellen und die erwartete Darstellungsstruktur.
        - Eine Bibliothek berechnet Excel-Formeln nicht zwingend. Behaupte keine berechneten Workbook-Werte ohne eine
          reale Rechenengine; validiere die Formellogik zusaetzlich mit einer unabhaengigen Referenzrechnung und
          mindestens einem Grenz- oder Negativfall.
        """;

    private const string ScientificGuidance = """
        Mathematische und physikalische Arbeit:
        - Definiere zuerst Symbole, Voraussetzungen, Definitionsbereiche, Einheiten, Konventionen und den behaupteten
          Gueltigkeitsumfang. Trenne mathematischen Satz, physikalisches Modell, numerische Evidenz und offene Hypothese.
        - Verwende drei voneinander unabhaengige Pruefebenen, soweit fachlich anwendbar: analytische Cross-Checks
          (Dimensionen, Symmetrien, Grenzfaelle, Erhaltungssaetze oder alternative Herleitung), numerische Gegenpruefung
          (Referenzfaelle, Randfaelle, Konvergenz, endliche Werte und Negativkontrolle) sowie formale Verifikation.
        - Nutze fuer geeignete mathematische Aussagen freiwillig `proof.lean` mit Lean/Mathlib. Ein formaler Nachweis darf
          erst nach erfolgreichem `verify` fuer den konkreten Theoremnamen behauptet werden. `sorry`, `admit`, eigene
          ungepruefte Axiome, `sorryAx` und Compilervertrauen sind keine Beweise.
        - Ein formaler mathematischer Beweis validiert nicht automatisch Modellannahmen oder Messdaten. Umgekehrt ist
          numerische Uebereinstimmung kein allgemeiner Beweis. Benenne den Evidenztyp und seine Grenzen wahrheitsgemaess.
        - Symbolische oder numerische Residuen muessen aus den Eingaben neu berechnet werden. Eingesetzte Nulltensoren,
          Selbstvergleiche, leere Stichproben oder aus Exceptions erzeugte Nullwerte sind ungueltig. Eine gezielte
          Stoerung muss den Checker nachweislich fehlschlagen lassen.
        - Simulationen sind deterministisch reproduzierbar zu konfigurieren. Speichere Parameter, Einheiten, Seed,
          Solver/Toleranzen und Rohdaten getrennt von Plot und Interpretation.
        """;

    private const string RelativityGuidance = """
        Differentialgeometrie und Relativitaet:
        - Lege Koordinaten, Signatur, Einheiten, Parameterbereich und Ausschlussmengen explizit fest. Unterscheide
          Koordinatensingularitaeten, Horizonte und echte Kruemmungssingularitaeten anhand geeigneter Invarianten.
        - Leite inverse Metrik, Christoffel-Symbole, Riemann-, Ricci- und Einstein-Tensor aus der angegebenen Metrik ab.
          Ein vorab auf null gesetzter Tensor oder eine nur bedingt ausgefuehrte Bianchi-Pruefung ist kein Nachweis.
        - Pruefe bekannte Referenz- und Grenzfaelle, Tensor-Symmetrien, Kontraktionen, Bianchi-Identitaeten und – falls
          Materie enthalten ist – die Konsistenz des Energie-Impuls-Tensors. Fuehre mindestens eine perturbierte
          Negativkontrolle aus, welche ein falsches Modell erkennt.
        - Bezeichne eine Loesung nur in dem Umfang als verifiziert, den die tatsaechliche symbolische, formale oder
          zertifizierte numerische Beweiskette abdeckt. Offene Forschungsansaetze bleiben als solche sichtbar und
          werden nicht durch Metadaten oder einen gruenen Smoke-Test zu exakten Loesungen.
        """;

    private const string PublicationGuidance = """
        Buch-, Bericht- und PDF-Ausgabe:
        - Nutze das typisierte `document.create`, sofern angeboten. Es pflegt eine kanonische, diffbare Markdown-Quelle
          und erzeugt PDF oder DOCX reproduzierbar daraus. Kapitel und Einheiten erhalten stabile sectionId-Werte;
          validiere, dass Titel, IDs und Inhalte nicht doppelt vorkommen.
        - Verfasse Überschriften, Fließtext, Tabellen, Bildunterschriften und sonstige nutzerlesbare Bestandteile vollständig
          in der Sprache der aktuellen Nutzeranweisung. Bei einem deutschen Auftrag ist auch das gesamte Manuskript deutsch;
          eine englische Vorlage oder ein englischer Fachbegriff ändert die Ausgabesprache nicht automatisch.
        - Formeln muessen KaTeX-kompatibel begrenzt und im echten Renderer geprueft werden. Rohe Befehle, fehlende
          Delimiter oder eine PDF, die nur den LaTeX-Quelltext zeigt, gelten als Fehler.
        - Der lesbare Inhalt erklaert Definition, Herleitung, Ergebnis, Gueltigkeitsbereich und Beispiel. Formale
          Lean-Beweise werden als verstaendliche Aussage mit nachvollziehbarer Beweisstruktur aufbereitet; ausfuehrliche
          Checker-Logs und numerische Hilfsschritte bleiben Validierungsartefakte, sofern der Nutzer sie nicht verlangt.
        - Verwende fuer die finale PDF den deterministischen GO-KaTeX-Exporter. Regeneriere sie nach einer Aenderung der
          kanonischen Quelle und pruefe anschliessend Dateityp, Groesse, Seitenzahl, gerenderte Formeln und Aktualitaet.
        """;
}
