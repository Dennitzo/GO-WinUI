using System.Text.Json;

namespace GoWinUI.App.Services;

public sealed class EinsteinCodingCampaignDefinition(CodingProofVerifier proofVerifier) : ICodingCampaignDefinition
{
    private static readonly string[] Challenges =
    [
        "Minkowski-Raumzeit, linearisierte Gravitation und kontrollierter Newtonscher Grenzfall",
        "Schwarzschild-Geometrie mit Krümmungsinvarianten, Geodäten, Horizontfläche und Oberflächengravitation",
        "Kerr- und Kerr-Newman-Geometrie mit Ergosphäre, Horizonten, Drehimpuls und ausgewählten Geodäten",
        "Komplexifizierung der Raumzeit: eigenständig eine mathematisch präzise Bedeutung eines imaginären Anteils entwickeln und gegen etablierte komplexe GR-Formulierungen prüfen",
        "de-Sitter- und Anti-de-Sitter-Raumzeit mit kosmologischer Konstante und Kausalstruktur",
        "FLRW-Kosmologie mit Materie, Strahlung und dunkler Energie sowie numerischer Skalenfaktorentwicklung",
        "TOV-Gleichung mit kausaler, thermodynamisch stabiler Zustandsgleichung und Masse-Radius-Beziehung",
        "Quantenfeldtheorie in gekrümmter Raumzeit mit Modengleichung, Renormierungsannahmen und Erhaltungssätzen",
        "Semiklassische Einstein-Gleichung mit renormiertem Energie-Impuls-Erwartungswert und dokumentiertem Näherungsbereich",
        "Thermodynamik schwarzer Löcher mit Hawking-Temperatur, Entropie, Horizontfläche und erstem Hauptsatz",
        "Niederenergiegrenze der Stringtheorie als Einstein-Dilaton- oder Einstein-Maxwell-Dilaton-Modell",
        "Höhere Krümmungskorrekturen der effektiven Stringtheorie einschließlich korrekter Topologiegrenzen",
        "AdS-Schwarzschild- oder AdS-Black-Brane-Modell mit holografischer Thermodynamik",
        "Regge-Wheeler- oder Zerilli-Störungen mit Potential, Stabilitätsprüfung und numerischer Modensimulation",
        "Bianchi-I-Kosmologie mit anisotroper Expansion, Zwangsbedingungen und numerischer Entwicklung",
    ];

    public CodingCampaignDescriptor Descriptor { get; } = new(
        "einstein-field-equations",
        "Einsteinsche Feldgleichungen",
        "Fortlaufende symbolische, numerische und formal prüfbare Untersuchung etablierter relativistischer Modelle.",
        "Physik und Mathematik",
        ["visualizations", "simulation_data", "solutions", "proofs"]);

    public bool PublishSolutionsOnlyAfterValidation => true;

    public bool HasFoundation(string workspacePath) =>
        File.Exists(Path.Combine(workspacePath, "einstein_engine.py"))
        && File.Exists(Path.Combine(workspacePath, "test_einstein_engine.py"))
        && File.Exists(Path.Combine(workspacePath, "einstein_cases.json"))
        && File.Exists(Path.Combine(workspacePath, "einstein_attempts.json"));

    public int ReadIteration(string workspacePath) => ReadArrayCount(Path.Combine(workspacePath, "einstein_attempts.json"), "attempts");

    public string GetChallenge(int iteration) => Challenges[Math.Abs(iteration) % Challenges.Length];

    public string BuildBootstrapPrompt() => """
        Erstelle im freigegebenen Workspace ein fortsetzbares, wissenschaftlich nachvollziehbares Python-Projekt zur
        symbolischen, numerischen und beweisgestützten Untersuchung der Einsteinschen Feldgleichungen

            G_{μν} + Λ g_{μν} = 8 π T_{μν}

        in geometrisierten Einheiten. Verwende SymPy, NumPy, SciPy und Matplotlib mit headless Backend. Implementiere
        wiederverwendbar Metrik und inverse Metrik, Christoffelsymbole, Riemann-, Ricci- und Einstein-Tensor,
        Krümmungsskalare und kovariante Divergenzen. Trenne analytische Referenz, numerische Auswertung und unabhängige
        Verifikation strikt. Erfinde keine Messwerte, Randbedingungen, Energie-Impuls-Tensoren oder Lösungen.

        Lege mindestens einstein_engine.py, test_einstein_engine.py, einstein_cases.json, einstein_attempts.json,
        einstein_analysis.md und visualize_einstein.py an. Nutze zusätzlich solutions/, proofs/, visualizations/ und
        simulation_data/. Erzeuge für jeden untersuchten Fall aussagekräftige, dauerhaft benannte Plots und die dazu
        gehörenden maschinenlesbaren Daten. Verwende Matplotlib mit einem headless Backend. Konzentriere Rechenzeit
        und Darstellung auf die fachlichen Fallplots statt auf generische Fortschrittsartefakte.
        einstein_cases.json ist der einzige autoritative Fallkatalog. Erzeuge keine parallelen Backup-, Kopie- oder
        Alternativkataloge mit möglicherweise veralteten Klassifikationen.
        Dateien, Funktionen oder Verweise namens live_progress sind ausdrücklich verboten. Erzeuge keine
        live_progress.json, live_progress.csv, live_progress.png oder live_progress.svg; aktualisiere stattdessen nur
        fachlich benannte Fallplots und deren fallbezogene Daten.

        Jeder Fall enthält id, title, theoryDomain, approximationLevel, classification, equations, assumptions,
        validityDomain, residualSamples, maxEinsteinResidual, maxBianchiResidual, verificationMethod,
        independentChecks, conclusion, visualizations und simulationData. classification ist verified, approximation
        oder undetermined. Ein verified-Fall benötigt mindestens zwei tatsächlich ausgewertete reguläre Punkte,
        endliche Einstein- und Bianchi-Residuen höchstens 1e-5 sowie ein detailliertes solutionDocument.
        undetermined ist ausschließlich eine informative Klassifizierung für eine offene Untersuchung. Sie löst keine
        Korrekturschleife und keinen Zwang zur Umklassifizierung aus. Auch offene oder approximative Fälle dürfen ein
        solutionDocument unter solutions/ besitzen, müssen dort aber unmissverständlich als „Offene Untersuchung“
        beziehungsweise „Näherungsuntersuchung“ gekennzeichnet sein und dürfen keinen formalen Beweis behaupten.

        Schreibe ausnahmslos striktes RFC-8259-JSON. NaN, Infinity und -Infinity sind verboten. Unbeschränkte
        Definitionsbereiche werden als erklärender Text in validityDomain dokumentiert, niemals als nicht endliche
        JSON-Zahl. Prüfe die Dateien mit einem strikten Parser; Pythons standardmäßig permissives json.load allein ist
        kein gültiger Nachweis. evaluationPoint ist ein benanntes JSON-Objekt und jede Residuenprobe enthält sowohl
        einsteinResidual als auch bianchiResidual.

        Schreibe jede belastbar verifizierte Lösung als eigenständige Markdown-Datei unter solutions/. Verwende für
        mathematische Ausdrücke gültige KaTeX-kompatible LaTeX-Begrenzer wie $...$, $$...$$, \\(...\\) oder
        \\[...\\]; umschließe Gleichungen nicht mit Markdown-Codezäunen. GO veröffentlicht neue oder geänderte
        Lösungsdateien unmittelbar im Chat und erzeugt daneben eine gleichnamige PDF-Datei.

        Krümmungstensoren werden immer in voller Koordinatendimension aus der noch symbolisch koordinatenabhängigen
        Metrik differenziert. Setze numerische Auswertungspunkte erst nach allen Ableitungen ein. Bilde den Ricci-Skalar
        ausschließlich durch R = g^{μν} R_{μν}; Matrix.trace(Ricci) ist in allgemeinen Koordinaten unzulässig.
        Dokumentiere bei Schwarzschild ausdrücklich: r=2M ist die Koordinatensingularität der Schwarzschild-Karte am
        Ereignishorizont, während r=0 eine echte Krümmungssingularität ist. Eine Außenraumbeschränkung r>2M darf nicht
        als physikalisches Versagen der Lösung im Inneren bezeichnet werden.

        Ergänze zu mathematisch bewiesenen Aussagen maschinenprüfbare Manifeste unter proofs/<caseId>/proof.json mit
        caseId, kind, statement, assumptions, validityDomain, artifact und sourceSha256. Formale Manifeste enthalten
        zusätzlich theoremName mit dem vollständig qualifizierten Lean-Theorem. kind ist symbolic,
        interval-certified, formal oder numerical-evidence. Numerische Evidenz allein ist kein Beweis. Symbolische und
        Intervallnachweise müssen als ausführbares Python-Programm mit Exit-Code null prüfbar sein. Formale Lean-Beweise
        müssen kompilieren und dürfen weder sorry, admit noch lokale axiom-Deklarationen enthalten. Behaupte keinen
        formalen Beweis, wenn der Checker nicht erfolgreich ausgeführt wurde. proof.lean steht dafür freiwillig als
        typisierte Schnittstelle bereit: nutze check während der Bearbeitung und verify mit theoremName für die
        abschließende Kompilierungs- und Axiomprüfung. Offene Fälle benötigen keinen Lean-Beweis.

        Erzeuge immer zuerst das ausführbare Beweisartefakt, führe es tatsächlich aus und berechne danach dessen SHA-256.
        Erst dann darf das Manifest geschrieben werden. Ein proof.json enthält exakt die oben genannten Nachweisfelder
        sowie theoremName ausschließlich für formale Nachweise;
        selbst eingetragene Felder wie exitCode, passed, verified, validation, method, steps oder timestamp sind kein
        Ausführungsbeleg und ersetzen weder artifact noch sourceSha256. validityDomain darf in einstein_cases.json und
        proof.json entweder ein nicht leerer Text oder ein Objekt mit nicht leerer description sein. Zeitstempel werden ausschließlich durch
        ausgeführten Code aus der realen UTC-Systemzeit erzeugt und niemals geschätzt oder auf runde Werte gesetzt.

        Beginne mit Minkowski und Schwarzschild als exakten Referenzen und anschließend mit einem anerkannten Modell
        aus Quantenfeldtheorie in gekrümmter Raumzeit, Schwarze-Loch-Thermodynamik oder kontrollierter String-
        Niederenergiephysik. Lege außerdem zwingend den offenen Fall complexified_spacetime an: Untersuche
        selbstständig, welche mathematisch konsistente Bedeutung ein imaginärer Anteil der Raumzeit haben kann, statt
        eine Interpretation vorzugeben. Vergleiche die gewählte Konstruktion mindestens mit analytischer Fortsetzung
        beziehungsweise Wick-Rotation, komplexen Metriken oder Verbindungen und geeigneten komplexen oder selbstdualen
        GR-Variablen. Trenne eine bloße Rechenhilfe, eine mathematisch konsistente Erweiterung und eine empirisch
        gestützte physikalische Aussage ausdrücklich voneinander.

        Der Fall complexified_spacetime enthält zusätzlich complexificationDefinition, realityConditions,
        realObservableMap, residualNormDefinition, establishedFormalismRelations, epistemicStatus und claimScope.
        epistemicStatus ist mathematical-exploration, established-formalism oder phenomenological-ansatz; claimScope
        ist mathematical-consistency, established-formalism oder empirical-model. Ein imaginärer Anteil ist nicht
        automatisch eine zusätzliche physikalische Dimension. Definiere komplexe Konjugation, Symmetrie- oder
        Hermitizitätsannahmen, Signatur, Einheiten und die Rückgewinnung reeller Observablen. Prüfe reellen Grenzfall,
        Real- und Imaginärteil der Feldgleichungen, Bianchi-Identität und Residuen über eine Norm des vollständigen
        komplexen Tensors. Verwirf keine Imaginärteile durch float-Konvertierung oder bloße Realteilbildung. Eine
        empirische Interpretation benötigt explizite Evidenz; ansonsten bleibt sie als offene mathematische Hypothese
        gekennzeichnet. Stelle eine Wick-Rotation nicht als Invarianz oder unveränderte Identität der lorentzschen
        Minkowski-Metrik dar: Die analytische Fortsetzung muss den Wechsel zwischen lorentzscher und euklidischer
        Signatur samt Konvention und Rücktransformation ausdrücklich benennen.

        Offene Quantengravitationsfragen bleiben offen. Führe Tests, Generatoren, einen begrenzten CLI-Smoke und git
        diff aus und behebe Fehler selbstständig. Speichere Abhängigkeiten reproduzierbar und halte .venv, Caches und
        temporäre Dateien aus Git.
        """;

    public string BuildIterationPrompt(int iteration, string challenge) => $$"""
        Setze den vorhandenen Einstein-Workflow in Iteration {{iteration}} fort. Verwende vorhandenen Code, Fälle,
        Beweise, fehlgeschlagene Ansätze, Simulationsdaten und Erkenntnisse; beginne kein Parallelprojekt.
        Behandle ausschließlich einstein_cases.json als autoritativen Fallkatalog und erzeuge keine Backup-, Kopie-
        oder Alternativkataloge mit Fallklassifikationen.

        Forschungsschwerpunkt: {{challenge}}

        Leite ein anerkanntes exaktes, effektives, semiklassisches oder perturbatives Modell mit Annahmen und
        Gültigkeitsbereich her. Löse eine klar definierte Reduktion analytisch oder numerisch. Prüfe Feldgleichung,
        Bianchi-Identität, Erhaltungssätze, Dimensionen, bekannte Grenzfälle und mindestens zwei reguläre Residuenpunkte
        unabhängig. Klassifiziere ehrlich als verified, approximation oder undetermined.

        Erzeuge oder aktualisiere reproduzierbare, fallbezogene Daten, Plots und Simulationen. Verwende stabile,
        fachlich beschreibende Dateinamen. Erzeuge oder reaktiviere unter keinen Umständen Dateien, Funktionen oder
        Verweise namens live_progress. Ein verified-Fall benötigt ein
        ausführliches Dokument unter solutions/ sowie mindestens einen
        erfolgreichen symbolischen, intervall-zertifizierten oder formalen Nachweis unter proofs/. Eine reine
        numerische Stichprobe ist kein mathematischer Beweis. Lean-Beweise dürfen kein sorry, admit oder lokales axiom
        enthalten. Nutze proof.lean freiwillig, wenn ein formaler Nachweis sinnvoll ist, und behaupte Erfolg nur nach
        bestandenem verify des benannten Theorems. Offene Probleme dürfen nicht als gelöst dargestellt werden.
        undetermined bleibt rein informativ und darf weder einen Korrekturzwang noch eine automatische Umklassifizierung
        auslösen. Ein solcher Fall darf als klar gekennzeichnete „Offene Untersuchung“ unter solutions/ dokumentiert
        werden. approximation-Dokumente werden entsprechend als „Näherungsuntersuchung“ gekennzeichnet.

        Formatiere das Lösungsdokument als KaTeX-kompatibles Markdown: mathematische Ausdrücke erhalten gültige
        LaTeX-Begrenzer und stehen niemals in einem Codezaun. GO übernimmt neue oder geänderte Lösungen unmittelbar
        in den Chat und erzeugt im solutions-Ordner eine PDF mit demselben Basisnamen.

        Für jeden Nachweis gilt zwingend: zuerst das referenzierte Checker-Artefakt erstellen und erfolgreich ausführen,
        danach den echten SHA-256 berechnen und erst zuletzt proofs/<caseId>/proof.json mit caseId, kind, statement,
        assumptions, validityDomain, artifact und sourceSha256 schreiben; bei kind formal ist zusätzlich theoremName
        verpflichtend. Selbst deklarierte Exit-Codes, Statuswerte,
        Schrittlisten oder Zeitstempel sind keine Evidenz. Prüfe auch alle neu angelegten, noch nicht von Git verfolgten
        Dateien in Status und Diff. Erzeuge updatedAt aus der tatsächlichen UTC-Systemzeit während des Prozesslaufs.

        Führe alle Tests, Syntax-/Buildprüfung, Ergebnis- und Grafikgenerierung, Beweischecker, CLI-Smoke und git diff
        aus. Behebe Laufzeit-, Logik- und Darstellungsfehler selbstständig und dokumentiere auch fehlgeschlagene Ansätze.
        Jeder Checker muss jeden von ihm ausgegebenen Soll-/Ist-Vergleich tatsächlich prüfen und bei einer Abweichung mit
        einem Fehlercode enden. Ein nur ausgedruckter Erwartungswert ist keine Verifikation; insbesondere müssen berechnete
        Krümmungsinvarianten symbolisch oder an unabhängigen regulären Punkten gegen die Referenz geprüft werden.
        Alle JSON-Artefakte müssen striktes RFC-8259-JSON ohne NaN oder Infinity sein. Verwende exakt validityDomain,
        evaluationPoint, einsteinResidual und bianchiResidual. Für Tensorprüfungen darfst du weder vor dem
        Differenzieren numerisch substituieren noch den Ricci-Skalar mit Matrix.trace bilden.
        Bei Schwarzschild muss der Gültigkeitsbereich die Koordinatensingularität bei r=2M von der
        Krümmungssingularität bei r=0 unterscheiden; eine gewählte Außenraumkarte ist kein Beleg für ein physikalisches
        Versagen der Lösung unterhalb des Horizonts.

        Der verpflichtende Fall complexified_spacetime bleibt eine ergebnisoffene Untersuchung. Erarbeite die konkrete
        Bedeutung des imaginären Anteils selbstständig, dokumentiere aber complexificationDefinition,
        realityConditions, realObservableMap, residualNormDefinition, establishedFormalismRelations, epistemicStatus
        und claimScope. Prüfe Real- und Imaginärteil, den reellen Grenzfall und die vollständige komplexe Residualnorm.
        Bezeichne mathematische Konsistenz niemals ohne gesonderte Evidenz als empirische Bestätigung. Bei einer
        Wick-Rotation muss der Signaturwechsel zwischen lorentzscher und euklidischer Darstellung explizit bleiben;
        behaupte nicht, die Minkowski-Metrik sei dabei invariant oder unverändert identisch.
        """;

    public string BuildCorrectionPrompt(int iteration, string challenge, IReadOnlyList<string> issues) => $$"""
        Die unabhängige Abnahme des Einstein-Workflows nach Iteration {{iteration}} zum Schwerpunkt „{{challenge}}“
        meldet diese konkreten Mängel:

        {{string.Join(Environment.NewLine, issues.Select(static issue => "- " + issue))}}

        Behebe die Ursachen in Code, Tests, Falldaten, Beweisen, Lösungen, Simulationsdaten und Visualisierungen.
        Schwäche keine Prüfung, erfinde keine Residuen und erkläre numerische Evidenz nicht zum Beweis. Regeneriere die
        betroffenen fallbezogenen Artefakte und führe alle Checker und Tests erneut aus.
        Entferne vorhandene Dateien, Funktionen und Verweise namens live_progress; ersetze sie nicht durch ein anderes
        generisches Fortschrittsartefakt. Fortschritt wird ausschließlich über fachliche Fallartefakte dokumentiert.
        einstein_cases.json bleibt der einzige autoritative Fallkatalog; entferne widersprüchliche Backup-, Kopie-
        oder Alternativkataloge, statt deren Klassifikationen weiterzuverwenden.
        Validiere JSON mit einem strikten RFC-8259-Parser; Python json.load akzeptiert Infinity standardmäßig und ist
        deshalb allein keine Abnahme. Repariere das vorgegebene Schema statt alternative Feldnamen einzuführen.
        Ein Beweismanifest darf keinen Erfolg selbst bescheinigen: Erstelle und starte zuerst sein Checker-Artefakt,
        berechne anschließend dessen echten SHA-256 und verwende ausschließlich caseId, kind, statement, assumptions,
        validityDomain, artifact und sourceSha256 als Manifestvertrag; bei kind formal kommt ausschließlich theoremName
        hinzu. undetermined ist informative Metadaten und kein zu korrigierender Fehler. Lies neu angelegte Dateien vor
        Abschluss erneut.
        """;

    public async Task<CodingCampaignValidationResult> ValidateAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<string>();
        foreach (var file in new[]
                 {
                     "einstein_engine.py", "test_einstein_engine.py", "einstein_cases.json",
                     "einstein_attempts.json", "einstein_analysis.md", "visualize_einstein.py",
                 })
        {
            var path = Path.Combine(workspacePath, file);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                issues.Add($"Pflichtartefakt fehlt oder ist leer: {file}");
            }
        }

        var proofs = await proofVerifier.VerifyAllAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        ValidateCases(workspacePath, proofs, issues);

        var attemptsPath = Path.Combine(workspacePath, "einstein_attempts.json");
        if (!TryReadJson(attemptsPath, out var attemptsDocument, out var attemptsError))
        {
            issues.Add($"einstein_attempts.json ist kein striktes RFC-8259-JSON: {attemptsError}");
        }
        else
        {
            using (attemptsDocument)
            {
                if (!attemptsDocument.RootElement.TryGetProperty("attempts", out var attempts)
                    || attempts.ValueKind != JsonValueKind.Array)
                {
                    issues.Add("einstein_attempts.json benötigt eine attempts-Liste.");
                }
            }
        }

        var plotCount = CountFiles(Path.Combine(workspacePath, "visualizations"), ".png");
        if (plotCount == 0)
        {
            issues.Add("Es wurde keine nicht leere PNG-Visualisierung erzeugt.");
        }
        if (CountFiles(Path.Combine(workspacePath, "simulation_data"), ".json", ".csv") == 0)
        {
            issues.Add("Es wurden keine maschinenlesbaren Simulationsdaten erzeugt.");
        }
        ValidateNoGenericProgressArtifacts(workspacePath, issues);
        ValidateNoDuplicateCaseCatalogs(workspacePath, issues);
        ValidateSimulationJson(workspacePath, issues);

        return new(issues.Count == 0, issues, proofs);
    }

    private static void ValidateCases(
        string workspacePath,
        IReadOnlyList<CodingProofVerificationResult> proofs,
        List<string> issues)
    {
        var path = Path.Combine(workspacePath, "einstein_cases.json");
        if (!TryReadJson(path, out var document, out var jsonError))
        {
            issues.Add($"einstein_cases.json ist kein striktes RFC-8259-JSON: {jsonError}");
            return;
        }
        using (document)
        {
            if (string.IsNullOrWhiteSpace(StringProperty(document.RootElement, "schemaVersion")))
            {
                issues.Add("einstein_cases.json benötigt eine schemaVersion.");
            }
            if (!document.RootElement.TryGetProperty("cases", out var cases) || cases.ValueKind != JsonValueKind.Array)
            {
                issues.Add("einstein_cases.json benötigt eine cases-Liste.");
                return;
            }
            if (cases.GetArrayLength() < 2)
            {
                issues.Add("Mindestens zwei physikalische Referenzfälle fehlen.");
            }
            var caseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var declaredSolutionDocuments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasComplexifiedSpacetimeCase = false;
            foreach (var item in cases.EnumerateArray())
            {
                var id = StringProperty(item, "id") ?? "unbekannt";
                if (id == "unbekannt" || !caseIds.Add(id))
                {
                    issues.Add(id == "unbekannt"
                        ? "Ein Fall besitzt keine gültige id."
                        : $"Fall-ID {id} ist mehrfach vorhanden.");
                }
                var classification = StringProperty(item, "classification");
                RequireCaseString(item, id, "title", issues);
                RequireCaseString(item, id, "theoryDomain", issues);
                RequireCaseString(item, id, "approximationLevel", issues);
                RequireValidityDomain(item, id, issues);
                RequireCaseString(item, id, "verificationMethod", issues);
                RequireCaseString(item, id, "conclusion", issues);
                RequireCaseArray(item, id, "equations", issues);
                RequireCaseArray(item, id, "assumptions", issues);
                RequireCaseArray(item, id, "independentChecks", issues);
                RequireCaseArray(item, id, "visualizations", issues);
                RequireCaseArray(item, id, "simulationData", issues);
                RequireFiniteNonNegative(item, id, "maxEinsteinResidual", issues);
                RequireFiniteNonNegative(item, id, "maxBianchiResidual", issues);
                ValidateKnownCaseData(item, id, issues);
                if (id.Equals("complexified_spacetime", StringComparison.OrdinalIgnoreCase))
                {
                    hasComplexifiedSpacetimeCase = true;
                    ValidateComplexifiedSpacetimeCase(item, id, issues);
                }
                if (classification is not ("verified" or "approximation" or "undetermined"))
                {
                    issues.Add($"Fall {id}: ungültige classification.");
                    continue;
                }
                var solution = StringProperty(item, "solutionDocument");
                if (!string.IsNullOrWhiteSpace(solution))
                {
                    var resolvedDocument = ResolveSafe(workspacePath, solution);
                    if (!File.Exists(resolvedDocument))
                    {
                        issues.Add($"Fall {id}: angegebenes solutionDocument fehlt.");
                    }
                    else if (Path.GetExtension(resolvedDocument).ToLowerInvariant() is not (".md" or ".txt" or ".tex" or ".json"))
                    {
                        issues.Add($"Fall {id}: solutionDocument muss eine textbasierte Datei sein.");
                    }
                    else
                    {
                        declaredSolutionDocuments.Add(
                            Path.GetRelativePath(workspacePath, resolvedDocument).Replace('\\', '/'));
                    }
                }

                if (classification != "verified") continue;

                ValidateResiduals(item, id, issues);
                if (string.IsNullOrWhiteSpace(solution))
                {
                    issues.Add($"Fall {id}: detailliertes solutionDocument fehlt.");
                }
                var expectedManifest = $"proofs/{id}/proof.json";
                if (!proofs.Any(proof => proof.CaseId.Equals(id, StringComparison.OrdinalIgnoreCase)
                                         && proof.IsProof && proof.Passed
                                         && proof.ManifestPath.Equals(expectedManifest, StringComparison.OrdinalIgnoreCase)))
                {
                    issues.Add($"Fall {id}: kein erfolgreicher Beweis mit Manifest {expectedManifest}.");
                }
                ValidateKnownPhysicsImplementation(workspacePath, item, id, issues);
            }
            if (!hasComplexifiedSpacetimeCase)
            {
                issues.Add("Pflichtfall complexified_spacetime fehlt: Der imaginäre Raumzeitanteil muss ergebnisoffen und mit Realitätsbedingungen untersucht werden.");
            }
            ValidateSolutionDirectory(workspacePath, declaredSolutionDocuments, issues);
        }

        // A proof is a mandatory acceptance gate only for cases explicitly classified as
        // verified. Open and approximate investigations may retain unsuccessful proof
        // attempts as evidence without turning their informational classification into a
        // correction or publication gate. The verified-case check above still requires a
        // successful, case-matching proof manifest.
    }

    public IReadOnlySet<string>? GetPublishableSolutionDocuments(string workspacePath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(workspacePath, "einstein_cases.json");
        if (!TryReadJson(path, out var document, out _))
        {
            return result;
        }
        using (document)
        {
            if (!document.RootElement.TryGetProperty("cases", out var cases)
                || cases.ValueKind != JsonValueKind.Array)
            {
                return result;
            }
            foreach (var item in cases.EnumerateArray())
            {
                var resolved = ResolveSafe(workspacePath, StringProperty(item, "solutionDocument"));
                if (File.Exists(resolved))
                {
                    result.Add(Path.GetRelativePath(workspacePath, resolved).Replace('\\', '/'));
                }
            }
        }
        return result;
    }

    public string GetSolutionPublicationHeading(
        string workspacePath,
        string relativePath,
        string fallbackHeading)
    {
        var path = Path.Combine(workspacePath, "einstein_cases.json");
        if (!TryReadJson(path, out var document, out _)) return fallbackHeading;
        using (document)
        {
            if (!document.RootElement.TryGetProperty("cases", out var cases)
                || cases.ValueKind != JsonValueKind.Array)
            {
                return fallbackHeading;
            }
            foreach (var item in cases.EnumerateArray())
            {
                var resolved = ResolveSafe(workspacePath, StringProperty(item, "solutionDocument"));
                if (!File.Exists(resolved)
                    || !Path.GetRelativePath(workspacePath, resolved).Replace('\\', '/').Equals(
                        relativePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                return StringProperty(item, "classification") switch
                {
                    "undetermined" => "Offene Untersuchung",
                    "approximation" => "Näherungsuntersuchung",
                    "verified" => "Verifizierte Lösung",
                    _ => fallbackHeading,
                };
            }
        }
        return fallbackHeading;
    }

    private static void ValidateSolutionDirectory(
        string workspacePath,
        IReadOnlySet<string> declaredSolutionDocuments,
        List<string> issues)
    {
        var root = Path.Combine(workspacePath, "solutions");
        if (!Directory.Exists(root))
        {
            return;
        }
        var allowedDocuments = new HashSet<string>(declaredSolutionDocuments, StringComparer.OrdinalIgnoreCase);
        foreach (var document in declaredSolutionDocuments)
        {
            allowedDocuments.Add(Path.ChangeExtension(document, ".pdf").Replace('\\', '/'));
        }
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(static path => Path.GetExtension(path).ToLowerInvariant() is ".md" or ".txt" or ".tex" or ".json" or ".pdf"))
        {
            var relative = Path.GetRelativePath(workspacePath, path).Replace('\\', '/');
            if (!allowedDocuments.Contains(relative))
            {
                issues.Add(
                    $"Keinem Fall zugeordnete Lösungs- oder Untersuchungsdatei: {relative}. "
                    + "Trage das Dokument als solutionDocument im autoritativen Fallkatalog ein.");
            }
        }
    }

    private static void ValidateResiduals(JsonElement item, string id, List<string> issues)
    {
        if (!item.TryGetProperty("residualSamples", out var samples)
            || samples.ValueKind != JsonValueKind.Array
            || samples.GetArrayLength() < 2)
        {
            issues.Add($"Fall {id}: mindestens zwei Residuen-Stichproben fehlen.");
            return;
        }
        var valid = 0;
        var sampleIndex = 0;
        foreach (var sample in samples.EnumerateArray())
        {
            sampleIndex++;
                if (!sample.TryGetProperty("evaluationPoint", out var point)
                    || point.ValueKind != JsonValueKind.Object
                    || !point.EnumerateObject().Any()
                    || point.EnumerateObject().Any(static coordinate =>
                        coordinate.Value.ValueKind != JsonValueKind.Number
                        || !coordinate.Value.TryGetDouble(out var value)
                        || !double.IsFinite(value)))
            {
                issues.Add($"Fall {id}, Residuenprobe {sampleIndex}: evaluationPoint muss ein nicht leeres Objekt mit endlichen numerischen Koordinaten sein.");
                continue;
            }
            if (!FiniteNumber(sample, "einsteinResidual", out var einstein))
            {
                issues.Add($"Fall {id}, Residuenprobe {sampleIndex}: einsteinResidual fehlt oder ist nicht endlich.");
                continue;
            }
            if (!FiniteNumber(sample, "bianchiResidual", out var bianchi))
            {
                issues.Add($"Fall {id}, Residuenprobe {sampleIndex}: bianchiResidual fehlt oder ist nicht endlich.");
                continue;
            }
            if (Math.Abs(einstein) <= 1e-5 && Math.Abs(bianchi) <= 1e-5)
            {
                valid++;
            }
        }
        if (valid < 2)
        {
            issues.Add($"Fall {id}: weniger als zwei reguläre Residuenpunkte erfüllen Einstein- und Bianchi-Grenze 1e-5.");
        }
    }

    private static void ValidateKnownCaseData(JsonElement item, string id, List<string> issues)
    {
        if (!id.Equals("schwarzschild", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!item.TryGetProperty("metric", out var metric)
            || metric.ValueKind != JsonValueKind.Array)
        {
            issues.Add("Fall schwarzschild: Die koordinatenabhängige Schwarzschild-Metrik fehlt.");
            return;
        }

        var serialized = metric.GetRawText();
        if (!serialized.Contains('M', StringComparison.Ordinal)
            || !serialized.Contains('r', StringComparison.OrdinalIgnoreCase)
            || !serialized.Contains("theta", StringComparison.OrdinalIgnoreCase)
            || (!serialized.Contains("sin", StringComparison.OrdinalIgnoreCase)
                && !serialized.Contains("sinus", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(
                "Fall schwarzschild: Die gespeicherte Metrik muss die tatsächliche Abhängigkeit von M, r und theta "
                + "einschließlich des Winkelterms enthalten; eine konstante Diagonalmatrix ist keine Schwarzschild-Geometrie.");
        }
        if (IsNumericalMinkowskiMetric(metric))
        {
            issues.Add(
                "Fall schwarzschild: Die gespeicherte Matrix diag(-1,1,1,1) ist die kartesische Minkowski-Metrik "
                + "und darf nicht als Schwarzschild-Lösung klassifiziert werden.");
        }
    }

    private static bool IsNumericalMinkowskiMetric(JsonElement metric)
    {
        var rows = metric.EnumerateArray().ToArray();
        if (rows.Length != 4 || rows.Any(static row => row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 4))
        {
            return false;
        }

        for (var rowIndex = 0; rowIndex < 4; rowIndex++)
        {
            var columns = rows[rowIndex].EnumerateArray().ToArray();
            for (var columnIndex = 0; columnIndex < 4; columnIndex++)
            {
                if (columns[columnIndex].ValueKind != JsonValueKind.Number
                    || !columns[columnIndex].TryGetDouble(out var value))
                {
                    return false;
                }
                var expected = rowIndex == columnIndex
                    ? rowIndex == 0 ? -1d : 1d
                    : 0d;
                if (Math.Abs(value - expected) > 1e-12)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static int ReadArrayCount(string path, string property)
    {
        if (!TryReadJson(path, out var document, out _)) return 0;
        using (document)
        {
            return document.RootElement.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
                ? array.GetArrayLength()
                : 0;
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
            document = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 128,
                });
            return true;
        }
        catch (JsonException exception)
        {
            var line = (exception.LineNumber ?? 0) + 1;
            var column = (exception.BytePositionInLine ?? 0) + 1;
            error = $"Zeile {line}, Spalte {column}: {CompactJsonError(exception.Message)}";
            return false;
        }
        catch (IOException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static string CompactJsonError(string message)
    {
        var firstLine = message.ReplaceLineEndings(" ").Trim();
        var location = firstLine.IndexOf(" LineNumber:", StringComparison.Ordinal);
        return location > 0 ? firstLine[..location].TrimEnd() : firstLine;
    }

    private static void ValidateSimulationJson(string workspacePath, List<string> issues)
    {
        var directory = Path.Combine(workspacePath, "simulation_data");
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            if (TryReadJson(path, out var document, out var error))
            {
                document.Dispose();
                continue;
            }
            var relative = Path.GetRelativePath(workspacePath, path).Replace('\\', '/');
            issues.Add($"{relative} ist kein striktes RFC-8259-JSON: {error}");
        }
    }

    private static void ValidateNoGenericProgressArtifacts(string workspacePath, List<string> issues)
    {
        foreach (var directoryName in new[] { "simulation_data", "visualizations" })
        {
            var directory = Path.Combine(workspacePath, directoryName);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "live_progress.*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(workspacePath, path).Replace('\\', '/');
                issues.Add($"Generisches Fortschrittsartefakt ist nicht erlaubt: {relative}. Verwende einen fachlich benannten Fallplot.");
            }
        }

        foreach (var fileName in new[] { "einstein_engine.py", "visualize_einstein.py" })
        {
            var path = Path.Combine(workspacePath, fileName);
            if (File.Exists(path)
                && File.ReadAllText(path).Contains("live_progress", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"{fileName} enthält erneut eine nicht erlaubte live_progress-Funktion oder Referenz.");
            }
        }
    }

    private static void ValidateNoDuplicateCaseCatalogs(string workspacePath, List<string> issues)
    {
        foreach (var path in Directory.EnumerateFiles(workspacePath, "einstein_cases*.json", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(path).Equals("einstein_cases.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var relative = Path.GetRelativePath(workspacePath, path).Replace('\\', '/');
            issues.Add(
                $"Widersprüchlicher Fallkatalog ist nicht erlaubt: {relative}. "
                + "einstein_cases.json ist die einzige autoritative Klassifikationsquelle.");
        }
    }

    private static void RequireCaseString(JsonElement item, string id, string name, List<string> issues)
    {
        if (string.IsNullOrWhiteSpace(StringProperty(item, name)))
        {
            issues.Add($"Fall {id}: Pflichtfeld {name} fehlt oder ist leer.");
        }
    }

    private static void RequireCaseTextOrObject(JsonElement item, string id, string name, List<string> issues)
    {
        if (!item.TryGetProperty(name, out var value)
            || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
            || (value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any())
            || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Object))
        {
            issues.Add($"Fall {id}: Pflichtfeld {name} muss ein nicht leerer Text oder ein nicht leeres Objekt sein.");
        }
    }

    private static void RequireValidityDomain(JsonElement item, string id, List<string> issues)
    {
        if (!item.TryGetProperty("validityDomain", out var domain))
        {
            issues.Add($"Fall {id}: Pflichtfeld validityDomain fehlt oder ist leer.");
            return;
        }

        var valid = domain.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrWhiteSpace(domain.GetString()),
            JsonValueKind.Object => domain.TryGetProperty("description", out var description)
                && description.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(description.GetString()),
            _ => false,
        };
        if (!valid)
        {
            issues.Add(
                $"Fall {id}: validityDomain muss ein nicht leerer Text oder ein Objekt mit nicht leerer description sein.");
        }
    }

    private static void RequireCaseArray(JsonElement item, string id, string name, List<string> issues)
    {
        if (!item.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() == 0)
        {
            issues.Add($"Fall {id}: Pflichtfeld {name} muss eine nicht leere Liste sein.");
        }
    }

    private static void RequireFiniteNonNegative(JsonElement item, string id, string name, List<string> issues)
    {
        if (!FiniteNumber(item, name, out var value) || value < 0)
        {
            issues.Add($"Fall {id}: Pflichtfeld {name} muss eine endliche, nicht negative Zahl sein.");
        }
    }

    private static void ValidateComplexifiedSpacetimeCase(JsonElement item, string id, List<string> issues)
    {
        RequireCaseString(item, id, "complexificationDefinition", issues);
        RequireCaseArray(item, id, "realityConditions", issues);
        RequireCaseTextOrObject(item, id, "realObservableMap", issues);
        RequireCaseString(item, id, "residualNormDefinition", issues);
        RequireCaseArray(item, id, "establishedFormalismRelations", issues);

        if (!ComplexifiedSpacetimeDistinguishesWickRotationSignature(item.GetRawText()))
        {
            issues.Add(
                $"Fall {id}: Die Wick-Rotation muss als analytische Fortsetzung mit explizitem Wechsel zwischen "
                + "lorentzscher und euklidischer Signatur beschrieben werden. Die Metrik darf dabei nicht zugleich "
                + "als invariant, unverändert oder identisch zur lorentzschen Minkowski-Metrik bezeichnet werden.");
        }

        var epistemicStatus = StringProperty(item, "epistemicStatus");
        if (epistemicStatus is not ("mathematical-exploration" or "established-formalism" or "phenomenological-ansatz"))
        {
            issues.Add($"Fall {id}: epistemicStatus muss mathematical-exploration, established-formalism oder phenomenological-ansatz sein.");
        }

        var claimScope = StringProperty(item, "claimScope");
        if (claimScope is not ("mathematical-consistency" or "established-formalism" or "empirical-model"))
        {
            issues.Add($"Fall {id}: claimScope muss mathematical-consistency, established-formalism oder empirical-model sein.");
        }
        else if (claimScope == "empirical-model"
                 && (!item.TryGetProperty("empiricalEvidence", out var evidence)
                     || evidence.ValueKind != JsonValueKind.Array
                     || evidence.GetArrayLength() == 0))
        {
            issues.Add($"Fall {id}: claimScope empirical-model benötigt eine nicht leere empiricalEvidence-Liste.");
        }

        if (item.TryGetProperty("residualSamples", out var samples) && samples.ValueKind == JsonValueKind.Array)
        {
            var sampleIndex = 0;
            foreach (var sample in samples.EnumerateArray())
            {
                sampleIndex++;
                if (!sample.TryGetProperty("evaluationPoint", out var point)
                    || point.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                if (point.EnumerateObject().Any(static coordinate =>
                        coordinate.Value.ValueKind != JsonValueKind.Number
                        || !coordinate.Value.TryGetDouble(out var value)
                        || !double.IsFinite(value)))
                {
                    issues.Add($"Fall {id}, Residuenprobe {sampleIndex}: Komplexe Koordinaten oder Parameter müssen als getrennte endliche Real- und Imaginärkomponenten gespeichert werden.");
                }
            }
        }
    }

    private static void ValidateKnownPhysicsImplementation(
        string workspacePath,
        JsonElement item,
        string id,
        List<string> issues)
    {
        var isKerr = id.Contains("kerr", StringComparison.OrdinalIgnoreCase);
        var isSchwarzschild = id.Contains("schwarzschild", StringComparison.OrdinalIgnoreCase);
        if (!isKerr && !isSchwarzschild)
        {
            return;
        }

        if (isSchwarzschild
            && !SchwarzschildDomainDistinguishesHorizonAndCurvature(ReadValidityDomainDescription(item)))
        {
            issues.Add(
                $"Fall {id}: Der Gültigkeitsbereich muss die Koordinatensingularität am Ereignishorizont r=2M ausdrücklich von der echten Krümmungssingularität bei r=0 unterscheiden. "
                + "Eine Beschränkung auf r>2M beschreibt den gewählten Außenraum beziehungsweise die Koordinatenkarte und darf nicht als physikalisches Versagen der Schwarzschild-Lösung unterhalb des Horizonts bezeichnet werden.");
        }

        var enginePath = Path.Combine(workspacePath, "einstein_engine.py");
        if (!File.Exists(enginePath))
        {
            return;
        }

        string source;
        try
        {
            source = File.ReadAllText(enginePath);
        }
        catch (IOException exception)
        {
            issues.Add($"Fall {id}: Referenzprüfung konnte einstein_engine.py nicht lesen: {exception.Message}");
            return;
        }

        if (source.Contains("def compute_kretschmann_scalar(riemann", StringComparison.Ordinal)
            && source.Contains("kretschmann += val ** 2", StringComparison.Ordinal))
        {
            issues.Add(
                $"Fall {id}: Der Kretschmann-Skalar wird als Summe quadrierter Koordinatenkomponenten gebildet. "
                + "Das ist in einer allgemeinen Koordinatenbasis nicht invariant; senke und kontrahiere die Riemann-Indizes vollständig mit Metrik und inverser Metrik und prüfe eine unabhängige Referenz.");
        }

        if (!isKerr)
        {
            return;
        }

        if (source.Contains("compute_christoffel_symbols(g_num", StringComparison.Ordinal)
            || source.Contains("compute_christoffel_symbols(metric_num.subs", StringComparison.Ordinal))
        {
            issues.Add($"Fall {id}: Die Kerr-Metrik wird vor den koordinatenabhängigen Ableitungen numerisch substituiert; die daraus entstehenden Nullresiduen sind kein gültiger Nachweis.");
        }
        if (source.Contains("compute_riemann_tensor(christoffel, [r, theta])", StringComparison.Ordinal)
            || source.Contains("compute_christoffel_symbols(g_num, [r, theta])", StringComparison.Ordinal))
        {
            issues.Add($"Fall {id}: Die vierdimensionale Kerr-Geometrie wird unzulässig nur mit zwei Koordinaten differenziert.");
        }
        if (source.Contains("ricci.trace()", StringComparison.Ordinal))
        {
            issues.Add($"Fall {id}: Der Ricci-Skalar wird mit Matrix.trace statt mit g^{{μν}}R_{{μν}} gebildet.");
        }
        if (source.Contains("16 * np.pi**2 * a * M", StringComparison.Ordinal)
            || source.Contains("16*np.pi**2*a*M", StringComparison.Ordinal))
        {
            issues.Add($"Fall {id}: Das implementierte Kerr-Horizontflächenprodukt ist dimensionswidrig; prüfe A₊A₋ = 64π²J² in geometrisierten Einheiten.");
        }
    }

    internal static bool SchwarzschildDomainDistinguishesHorizonAndCurvature(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return false;
        }

        var normalized = description.ToLowerInvariant();
        var mentionsHorizonRadius = normalized.Contains("r=2m", StringComparison.Ordinal)
            || normalized.Contains("r = 2m", StringComparison.Ordinal)
            || normalized.Contains("r = 2 m", StringComparison.Ordinal);
        var mentionsOrigin = normalized.Contains("r=0", StringComparison.Ordinal)
            || normalized.Contains("r = 0", StringComparison.Ordinal);
        return mentionsHorizonRadius
            && mentionsOrigin
            && normalized.Contains("koordinat", StringComparison.Ordinal)
            && (normalized.Contains("krümm", StringComparison.Ordinal)
                || normalized.Contains("kruemm", StringComparison.Ordinal));
    }

    internal static bool ComplexifiedSpacetimeDistinguishesWickRotationSignature(string? serializedCase)
    {
        if (string.IsNullOrWhiteSpace(serializedCase))
        {
            return false;
        }

        var normalized = serializedCase.ToLowerInvariant();
        var mentionsAnalyticContinuation = normalized.Contains("wick", StringComparison.Ordinal)
            || normalized.Contains("analytische fortsetzung", StringComparison.Ordinal)
            || normalized.Contains("analytic continuation", StringComparison.Ordinal);
        var distinguishesSignatures = normalized.Contains("signatur", StringComparison.Ordinal)
            && (normalized.Contains("lorentz", StringComparison.Ordinal)
                || normalized.Contains("minkowski", StringComparison.Ordinal))
            && (normalized.Contains("euklid", StringComparison.Ordinal)
                || normalized.Contains("euclid", StringComparison.Ordinal));
        var claimsUnchangedMetric =
            (normalized.Contains("metrik bleibt", StringComparison.Ordinal)
                && (normalized.Contains("invariant", StringComparison.Ordinal)
                    || normalized.Contains("identisch", StringComparison.Ordinal)
                    || normalized.Contains("unverändert", StringComparison.Ordinal)))
            || normalized.Contains("metric remains invariant", StringComparison.Ordinal)
            || normalized.Contains("metric remains identical", StringComparison.Ordinal)
            || normalized.Contains("unchanged minkowski metric", StringComparison.Ordinal);

        return mentionsAnalyticContinuation && distinguishesSignatures && !claimsUnchangedMetric;
    }

    private static string? ReadValidityDomainDescription(JsonElement item)
    {
        if (!item.TryGetProperty("validityDomain", out var domain))
        {
            return null;
        }
        if (domain.ValueKind == JsonValueKind.String)
        {
            return domain.GetString();
        }
        return domain.ValueKind == JsonValueKind.Object
            && domain.TryGetProperty("description", out var description)
            && description.ValueKind == JsonValueKind.String
                ? description.GetString()
                : null;
    }

    private static void RequireProperty(JsonElement root, string name, List<string> issues, string prefix)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())))
        {
            issues.Add($"{prefix}: {name} fehlt.");
        }
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static bool FiniteNumber(JsonElement element, string name, out double value)
    {
        value = double.NaN;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value)
            && double.IsFinite(value);
    }

    private static string ResolveSafe(string workspacePath, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return string.Empty;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : string.Empty;
    }

    private static int CountFiles(string directory, params string[] extensions) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Count(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                               && new FileInfo(path).Length > 0)
            : 0;
}
