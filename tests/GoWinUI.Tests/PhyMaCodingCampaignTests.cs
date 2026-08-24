using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GoWinUI.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoWinUI.Tests;

public sealed class PhyMaCodingCampaignTests
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly string[] CatalogDefinitions = ["Alle verwendeten Größen werden vor dem Satz explizit definiert."];
    private static readonly string[] CatalogAssumptions = ["Die im Gültigkeitsbereich genannten Voraussetzungen gelten."];
    private static readonly Dictionary<string, string> FormalCoverage = new(StringComparer.Ordinal)
    {
        ["definitions"] = "Die verwendeten Begriffe sind im Lean-Artefakt als Definitionen oder Strukturen angelegt.",
        ["assumptions"] = "Die Voraussetzungen des Satzes sind im Theoremtyp explizit sichtbar.",
        ["theorem"] = "Der zentrale Satz besitzt einen benannten theoremName.",
        ["derivation"] = "Der Beweis wird durch den Lean-Kernel geprüft.",
    };
    private static readonly Dictionary<string, string> AnalyticalCrossChecks = new(StringComparer.Ordinal)
    {
        ["alternative-derivation"] = "Eine zweite symbolische Herleitung prüft die Kernaussage.",
        ["limit-case"] = "Grenzfälle werden separat ausgewertet.",
        ["dimensional-analysis"] = "Ein Strukturcheck prüft die Konsistenz der verwendeten Größen.",
    };
    [Fact]
    public void WorkflowContractRequiresAContinuousBookAndThreeIndependentValidationGates()
    {
        var definition = new PhyMaCodingCampaignDefinition(new CodingProofVerifier());
        var bootstrap = definition.BuildBootstrapPrompt();
        var iteration = definition.BuildIterationPrompt(4, definition.GetChallenge(4));
        var correction = definition.BuildCorrectionPrompt(4, definition.GetChallenge(4), ["Numerischer Grenzfall fehlt."]);
        var catalog = new CodingCampaignCatalog([definition]);

        Assert.Equal("phyma", definition.Descriptor.Id);
        Assert.Equal("PhyMa", definition.Descriptor.Title);
        Assert.True(definition.PublishSolutionsOnlyAfterValidation);
        Assert.Contains(catalog.List(), descriptor => descriptor.Id == "phyma");
        Assert.Contains("solutions/PhyMa.md", bootstrap, StringComparison.Ordinal);
        Assert.Contains("solutions/PhyMa.pdf", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("document.renderPdf", bootstrap, StringComparison.Ordinal);
        Assert.Contains("korrekt gerendertem KaTeX", bootstrap, StringComparison.Ordinal);
        Assert.Contains("rohe LaTeX-Quellen", bootstrap, StringComparison.Ordinal);
        Assert.Contains("aufbereiteten Lean-Beweis", bootstrap, StringComparison.Ordinal);
        Assert.Contains("Buchdarstellung", bootstrap, StringComparison.Ordinal);
        Assert.Contains("nur ein kurzer Validierungsstatus", bootstrap, StringComparison.Ordinal);
        Assert.Contains("formalCoverage` enthält nicht leere Texte", bootstrap, StringComparison.Ordinal);
        Assert.Contains("das Objekt `crossChecks`", bootstrap, StringComparison.Ordinal);
        Assert.Contains("solutions/PhyMa.pdf", iteration, StringComparison.Ordinal);
        Assert.DoesNotContain("document.renderPdf", iteration, StringComparison.Ordinal);
        Assert.Contains("A4-Buchformat mit gerendertem KaTeX", iteration, StringComparison.Ordinal);
        Assert.Contains("Analytische und numerische Prüfdetails gehören in Checker", iteration, StringComparison.Ordinal);
        Assert.Contains("nur einmal vorkommen", bootstrap, StringComparison.Ordinal);
        Assert.Contains("doppelte Einträge", iteration, StringComparison.Ordinal);
        Assert.Contains("lokalen Spiegel der GO-Abnahme", bootstrap, StringComparison.Ordinal);
        Assert.Contains("referenzierten JSON-Artefakte", bootstrap, StringComparison.Ordinal);
        Assert.Contains("test_phyma.py` schwächer", iteration, StringComparison.Ordinal);
        Assert.Contains("proof.lean verify", bootstrap, StringComparison.Ordinal);
        Assert.Contains("leanprover/lean4:v4.30.0", bootstrap, StringComparison.Ordinal);
        Assert.Contains("v4.30.0", bootstrap, StringComparison.Ordinal);
        Assert.Contains("import Mathlib", bootstrap, StringComparison.Ordinal);
        Assert.Contains("Ein gescheiterter Lean-Core-Versuch ist keine Begründung", bootstrap, StringComparison.Ordinal);
        Assert.Contains("Physlib", bootstrap, StringComparison.Ordinal);
        Assert.Contains("LeanCert", bootstrap, StringComparison.Ordinal);
        Assert.Contains("sorry", bootstrap, StringComparison.Ordinal);
        Assert.Contains("admit", bootstrap, StringComparison.Ordinal);
        Assert.Contains("ungeprüfte eigene Axiome", bootstrap, StringComparison.Ordinal);
        Assert.Contains("alternative Herleitung", bootstrap, StringComparison.Ordinal);
        Assert.Contains("Dimensions-", bootstrap, StringComparison.Ordinal);
        Assert.Contains("Grenz- oder Randfall", bootstrap, StringComparison.Ordinal);
        Assert.Contains("Dauerworkflow endet", iteration, StringComparison.Ordinal);
        Assert.Contains("alle drei Manifeste exakt dieselbe Kernaussage", correction, StringComparison.Ordinal);
    }

    [Fact]
    public void FullyValidatedRevisionPublishesOnlyTheAuthoritativeBook()
    {
        var fixture = CreateValidFixture();
        try
        {
            var result = PhyMaCodingCampaignDefinition.ValidateWorkspaceArtifacts(fixture.Workspace, fixture.Proofs);
            var definition = new PhyMaCodingCampaignDefinition(new CodingProofVerifier());

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
            Assert.Empty(result.Issues);
            Assert.True(definition.HasFoundation(fixture.Workspace));
            Assert.Equal(2, definition.ReadIteration(fixture.Workspace));
            Assert.Equal(
                [PhyMaCodingCampaignDefinition.BookRelativePath],
                definition.GetPublishableSolutionDocuments(fixture.Workspace)!.ToArray());
            Assert.Contains(
                "validierte Buchrevision",
                definition.GetSolutionPublicationHeading(
                    fixture.Workspace,
                    PhyMaCodingCampaignDefinition.BookRelativePath,
                    "Lösung"),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixture.Workspace, recursive: true);
        }
    }

    [Fact]
    public void RevisionIsRejectedWhenAFormalLeanGateDidNotPass()
    {
        var fixture = CreateValidFixture();
        try
        {
            var proofs = fixture.Proofs
                .Select(proof => proof.Kind == CodingProofKind.Formal
                    ? proof with { Passed = false, Detail = "Lean verify fehlgeschlagen." }
                    : proof)
                .ToArray();

            var result = PhyMaCodingCampaignDefinition.ValidateWorkspaceArtifacts(fixture.Workspace, proofs);

            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, issue =>
                issue.Contains("Pflichtnachweis", StringComparison.Ordinal)
                && issue.Contains("nicht bestanden", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(fixture.Workspace, recursive: true);
        }
    }

    [Fact]
    public void RevisionIsRejectedWhenTheLeanArtifactOnlyAssertsATheoremWithoutDefinitions()
    {
        var fixture = CreateValidFixture();
        try
        {
            foreach (var leanPath in Directory.EnumerateFiles(fixture.Workspace, "*.lean", SearchOption.AllDirectories))
            {
                File.WriteAllText(leanPath, "theorem asserted_without_formalized_definition : True := by trivial\n");
            }

            var result = PhyMaCodingCampaignDefinition.ValidateWorkspaceArtifacts(fixture.Workspace, fixture.Proofs);

            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, issue => issue.Contains("keine explizit geprüfte Definition", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(fixture.Workspace, recursive: true);
        }
    }

    [Fact]
    public void RevisionIsRejectedWithoutReferenceAndBoundaryNumerics()
    {
        var fixture = CreateValidFixture();
        try
        {
            foreach (var dataPath in fixture.NumericalDataPaths)
            {
                var content = File.ReadAllText(dataPath);
                File.WriteAllText(
                    dataPath,
                    content.Replace("\"reference\"", "\"generic\"", StringComparison.Ordinal)
                        .Replace("\"boundary\"", "\"generic\"", StringComparison.Ordinal));
            }

            var result = PhyMaCodingCampaignDefinition.ValidateWorkspaceArtifacts(fixture.Workspace, fixture.Proofs);

            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, issue => issue.Contains("Referenzstichprobe fehlt", StringComparison.Ordinal));
            Assert.Contains(result.Issues, issue => issue.Contains("Grenz- oder Randfallstichprobe fehlt", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(fixture.Workspace, recursive: true);
        }
    }

    [Fact]
    public void RevisionIsRejectedWhenAnalyticalAndNumericalCheckersAreTheSameProgram()
    {
        var fixture = CreateValidFixture();
        try
        {
            foreach (var pair in fixture.CheckerPairs)
            {
                File.Copy(pair.AnalyticalPath, pair.NumericalPath, overwrite: true);
            }

            var result = PhyMaCodingCampaignDefinition.ValidateWorkspaceArtifacts(fixture.Workspace, fixture.Proofs);

            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, issue => issue.Contains("nicht unabhängig", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(fixture.Workspace, recursive: true);
        }
    }

    [Fact]
    public void RevisionIsRejectedWhenBookContainsDuplicateUnitEntries()
    {
        var fixture = CreateValidFixture();
        try
        {
            File.AppendAllText(
                fixture.BookPath,
                Environment.NewLine
                + BuildBookUnit(
                    "addition-commutativity",
                    "Kommutativität der Addition",
                    "$a+b=b+a$",
                    "reellen Zahlen",
                    "phyma_addition_commutative"));

            var result = PhyMaCodingCampaignDefinition.ValidateWorkspaceArtifacts(fixture.Workspace, fixture.Proofs);

            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, issue =>
                issue.Contains("addition-commutativity", StringComparison.Ordinal)
                && issue.Contains("mehrfach", StringComparison.Ordinal));
            Assert.Contains(result.Issues, issue =>
                issue.Contains("Kommutativität der Addition", StringComparison.Ordinal)
                && issue.Contains("mehrfach", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(fixture.Workspace, recursive: true);
        }
    }

    [Fact]
    public void RevisionIsRejectedWhenBookListsAnalyticalOrNumericalValidationSteps()
    {
        var fixture = CreateValidFixture();
        try
        {
            File.AppendAllText(
                fixture.BookPath,
                """

                ## Ausführliche Prüfdetails

                **Analytischer Cross-Check.** Diese Detaildarstellung gehört nicht in das Buch.

                **Numerische Validierung.** Eine Stichprobentabelle mit absoluteError und relativeError gehört in
                simulation_data.
                """);

            var result = PhyMaCodingCampaignDefinition.ValidateWorkspaceArtifacts(fixture.Workspace, fixture.Proofs);

            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, issue =>
                issue.Contains("nicht als eigene ausführliche Buchabschnitte", StringComparison.Ordinal));
            Assert.Contains(result.Issues, issue =>
                issue.Contains("Numerische Stichproben", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(fixture.Workspace, recursive: true);
        }
    }

    [Fact]
    public void RevisionIsRejectedWhenBookOmitsTheFormalLeanTheoremName()
    {
        var fixture = CreateValidFixture();
        try
        {
            File.WriteAllText(
                fixture.BookPath,
                File.ReadAllText(fixture.BookPath).Replace("phyma_addition_commutative", "nicht_genannter_satz", StringComparison.Ordinal));

            var result = PhyMaCodingCampaignDefinition.ValidateWorkspaceArtifacts(fixture.Workspace, fixture.Proofs);

            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, issue =>
                issue.Contains("Lean-Theoremnamen phyma_addition_commutative", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(fixture.Workspace, recursive: true);
        }
    }

    [Fact]
    public void RevisionIsRejectedWhenAuthoritativeArtifactsAreNotReferencedByCatalog()
    {
        var fixture = CreateValidFixture();
        try
        {
            var orphanProofDirectory = Directory.CreateDirectory(
                Path.Combine(fixture.Workspace, "proofs", "orphan-topic", "formal"));
            File.WriteAllText(
                Path.Combine(orphanProofDirectory.FullName, "proof.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        caseId = "orphan-topic",
                        kind = "formal",
                        statement = "Dieses Manifest darf nicht neben dem Katalog liegen bleiben.",
                        assumptions = Array.Empty<string>(),
                        validityDomain = "Testfixture mit endlichen Eingaben.",
                        artifact = "proofs/orphan-topic/formal/Proof.lean",
                        sourceSha256 = new string('A', 64),
                        theoremName = "orphan_topic",
                    },
                    IndentedJson));
            File.WriteAllText(
                Path.Combine(fixture.Workspace, "simulation_data", "orphan-topic.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        caseId = "orphan-topic",
                        method = "Unreferenzierte Testdaten.",
                        samples = Array.Empty<object>(),
                    },
                    IndentedJson));

            var result = PhyMaCodingCampaignDefinition.ValidateWorkspaceArtifacts(fixture.Workspace, fixture.Proofs);

            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, issue =>
                issue.Contains("Autoritatives Beweismanifest", StringComparison.Ordinal)
                && issue.Contains("proofs/orphan-topic/formal/proof.json", StringComparison.Ordinal));
            Assert.Contains(result.Issues, issue =>
                issue.Contains("Autoritative Simulationsdaten", StringComparison.Ordinal)
                && issue.Contains("simulation_data/orphan-topic.json", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(fixture.Workspace, recursive: true);
        }
    }

    [Fact]
    public void RevisionIsRejectedWhenAuthoritativeBookPdfIsMissingOrStale()
    {
        var fixture = CreateValidFixture();
        try
        {
            var pdfPath = Path.ChangeExtension(fixture.BookPath, ".pdf");
            File.Delete(pdfPath);

            var missing = PhyMaCodingCampaignDefinition.ValidateWorkspaceArtifacts(fixture.Workspace, fixture.Proofs);

            Assert.False(missing.IsValid);
            Assert.Contains(missing.Issues, issue => issue.Contains("solutions/PhyMa.pdf", StringComparison.Ordinal));

            WriteMinimalPdf(pdfPath);
            File.SetLastWriteTimeUtc(pdfPath, File.GetLastWriteTimeUtc(fixture.BookPath).AddMinutes(-5));

            var stale = PhyMaCodingCampaignDefinition.ValidateWorkspaceArtifacts(fixture.Workspace, fixture.Proofs);

            Assert.False(stale.IsValid);
            Assert.Contains(stale.Issues, issue => issue.Contains("älter als", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(fixture.Workspace, recursive: true);
        }
    }

    [Fact]
    public async Task NumericalEvidenceManifestMustExecuteItsPinnedChecker()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "GO", "PhyMaNumericalTests", Guid.NewGuid().ToString("N"));
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(workspace, "proofs", "failing-numerics"));
            var artifact = Path.Combine(directory.FullName, "check_numerical.py");
            await File.WriteAllTextAsync(artifact, "raise SystemExit(7)\n");
            await File.WriteAllTextAsync(
                Path.Combine(directory.FullName, "proof.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        caseId = "failing-numerics",
                        kind = "numerical-evidence",
                        statement = "Die unabhängige numerische Implementierung muss ohne Laufzeitfehler ausgeführt werden.",
                        assumptions = Array.Empty<string>(),
                        validityDomain = "Deterministische Testfixture für endliche Eingabewerte.",
                        artifact = "proofs/failing-numerics/check_numerical.py",
                        sourceSha256 = HashFile(artifact),
                    },
                    IndentedJson));

            var result = Assert.Single(await new CodingProofVerifier().VerifyAllAsync(workspace));

            Assert.False(result.IsProof);
            Assert.False(result.Passed);
            Assert.Contains("Exit 7", result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task AuthoritativeBookIsExportedAsSameNamedA4Pdf()
    {
        var fixture = CreateValidFixture();
        try
        {
            using var exporter = new CodingSolutionPdfExporter(NullLogger<CodingSolutionPdfExporter>.Instance);

            var output = await exporter.EnsureCurrentAsync(fixture.BookPath, sourceChanged: true);

            Assert.NotNull(output);
            Assert.Equal("PhyMa.pdf", Path.GetFileName(output));
            Assert.True(new FileInfo(output).Length >= 1024);
        }
        finally
        {
            Directory.Delete(fixture.Workspace, recursive: true);
        }
    }

    private static PhyMaFixture CreateValidFixture()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "GO", "PhyMaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "solutions"));
        Directory.CreateDirectory(Path.Combine(workspace, "proofs"));
        Directory.CreateDirectory(Path.Combine(workspace, "simulation_data"));
        File.WriteAllText(
            Path.Combine(workspace, "test_phyma.py"),
            "from pathlib import Path\nassert Path('solutions/PhyMa.md').is_file()\n");

        var bookPath = Path.Combine(workspace, "solutions", "PhyMa.md");
        File.WriteAllText(bookPath, BuildBook());
        WriteMinimalPdf(Path.ChangeExtension(bookPath, ".pdf"));

        var units = new[]
        {
            new UnitFixture(
                "addition-commutativity",
                "Kommutativität der Addition",
                "mathematics",
                "algebra-number-theory",
                "Für reelle Zahlen ist die Addition kommutativ und unabhängig von der Reihenfolge der Operanden.",
                "Gültig für alle reellen Zahlen mit der üblichen Addition.",
                "phyma_addition_commutative"),
            new UnitFixture(
                "uniform-linear-motion",
                "Gleichförmige geradlinige Bewegung",
                "physics",
                "classical-mechanics",
                "Bei konstanter Geschwindigkeit entwickelt sich der Ort linear mit der verstrichenen Zeit.",
                "Gültig in einem Inertialsystem bei vernachlässigbarer Beschleunigung.",
                "phyma_uniform_motion_zero_time"),
        };
        var proofs = new List<CodingProofVerificationResult>();
        var numericalDataPaths = new List<string>();
        var checkerPairs = new List<CheckerPair>();
        var catalogUnits = new List<object>();
        foreach (var unit in units)
        {
            var proofRoot = Path.Combine(workspace, "proofs", unit.Id);
            var formalDirectory = Directory.CreateDirectory(Path.Combine(proofRoot, "formal"));
            var analyticDirectory = Directory.CreateDirectory(Path.Combine(proofRoot, "analytic"));
            var numericalDirectory = Directory.CreateDirectory(Path.Combine(proofRoot, "numerical"));
            var formalArtifact = Path.Combine(formalDirectory.FullName, "Proof.lean");
            var analyticArtifact = Path.Combine(analyticDirectory.FullName, "check_symbolic.py");
            var numericalArtifact = Path.Combine(numericalDirectory.FullName, "check_numerical.py");
            File.WriteAllText(formalArtifact, BuildLeanProof(unit));
            File.WriteAllText(
                analyticArtifact,
                $"from sympy import symbols, simplify\nvalue = symbols('value')\nassert simplify(value - value) == 0\nprint('symbolic:{unit.Id}')\n");
            File.WriteAllText(
                numericalArtifact,
                $"import math\nsamples = [0.0, 1.0, 9.0]\nassert all(math.isfinite(x) for x in samples)\nprint('numerical:{unit.Id}')\n");
            checkerPairs.Add(new(analyticArtifact, numericalArtifact));

            var formalManifest = Normalize(Path.GetRelativePath(workspace, Path.Combine(formalDirectory.FullName, "proof.json")));
            var analyticManifest = Normalize(Path.GetRelativePath(workspace, Path.Combine(analyticDirectory.FullName, "proof.json")));
            var numericalManifest = Normalize(Path.GetRelativePath(workspace, Path.Combine(numericalDirectory.FullName, "proof.json")));
            WriteManifest(workspace, formalManifest, unit, "formal", formalArtifact, unit.TheoremName);
            WriteManifest(workspace, analyticManifest, unit, "symbolic", analyticArtifact, theoremName: null);
            WriteManifest(workspace, numericalManifest, unit, "numerical-evidence", numericalArtifact, theoremName: null);

            var numericalDataRelative = $"simulation_data/{unit.Id}.json";
            var numericalDataPath = Path.Combine(workspace, numericalDataRelative.Replace('/', Path.DirectorySeparatorChar));
            WriteNumericalData(numericalDataPath, unit.Id);
            numericalDataPaths.Add(numericalDataPath);

            proofs.Add(new(unit.Id, formalManifest, CodingProofKind.Formal, true, true, "Lean verify erfolgreich."));
            proofs.Add(new(unit.Id, analyticManifest, CodingProofKind.Symbolic, true, true, "Symbolischer Checker erfolgreich."));
            proofs.Add(new(unit.Id, numericalManifest, CodingProofKind.NumericalEvidence, false, true, "Numerische Evidenz erfolgreich."));
            catalogUnits.Add(new
            {
                id = unit.Id,
                title = unit.Title,
                discipline = unit.Discipline,
                field = unit.Field,
                status = "validated",
                statement = unit.Statement,
                validityDomain = unit.ValidityDomain,
                definitions = CatalogDefinitions,
                assumptions = CatalogAssumptions,
                formalCoverage = FormalCoverage,
                formalLibrary = "mathlib",
                crossChecks = AnalyticalCrossChecks,
                formalManifest,
                analyticManifest,
                numericalManifest,
                numericalData = numericalDataRelative,
            });
        }

        var roadmap = PhyMaCodingCampaignDefinition.RoadmapFieldIds
            .Select((id, index) => new
            {
                id,
                title = id.Replace('-', ' '),
                discipline = index < 9 ? "mathematics" : "physics",
                status = units.Any(unit => unit.Field == id) ? "covered" : "planned",
            })
            .ToArray();
        File.WriteAllText(
            Path.Combine(workspace, PhyMaCodingCampaignDefinition.CatalogRelativePath),
            JsonSerializer.Serialize(
                new
                {
                    revision = 2,
                    book = PhyMaCodingCampaignDefinition.BookRelativePath,
                    roadmap,
                    units = catalogUnits,
                },
                IndentedJson));

        return new(workspace, bookPath, proofs.ToArray(), numericalDataPaths.ToArray(), checkerPairs.ToArray());
    }

    private static string BuildBook()
    {
        var introduction = string.Join(' ', Enumerable.Repeat(
            "Dieses Lehrbuch verbindet präzise Mathematik mit physikalischer Modellbildung und erläutert jede Symbolwahl nachvollziehbar.",
            12));
        return $$"""
            # PhyMa

            <!-- phyma-revision:2 -->

            ## Vorwort

            {{introduction}}

            ## Inhaltsübersicht

            Die erste Revision führt von algebraischen Strukturen zur Bewegung in einem Inertialsystem und baut alle
            späteren Gebiete schrittweise auf.

            ## Notation und Konventionen

            Skalare stehen kursiv, Vektoren fett und physikalische Größen werden konsistent im SI-System angegeben.

            {{BuildBookUnit("addition-commutativity", "Kommutativität der Addition", "$a+b=b+a$", "reellen Zahlen", "phyma_addition_commutative")}}

            {{BuildBookUnit("uniform-linear-motion", "Gleichförmige geradlinige Bewegung", "$x(t)=x_0+v t$", "klassischen Mechanik", "phyma_uniform_motion_zero_time")}}
            """;
    }

    private static string BuildBookUnit(string id, string title, string equation, string domain, string theoremName)
    {
        var explanation = string.Join(' ', Enumerable.Repeat(
            $"Die Einheit {title} entwickelt die Aussage aus den eingeführten Begriffen, erklärt jeden Rechenschritt und ordnet das Ergebnis in den übergeordneten Zusammenhang ein.",
            15));
        var leanExplanation = string.Join(' ', Enumerable.Repeat(
            $"Der formale Lean-Beweis `{theoremName}` bindet die Definitionen, Voraussetzungen und den Satz an eine vom Kernel geprüfte Beweiskette.",
            5));
        return $$"""
            <!-- phyma-unit:{{id}} -->
            ## {{title}}

            **Definition.** Alle Größen und Operationen dieses Abschnitts werden vor ihrer Verwendung eindeutig festgelegt.

            **Voraussetzung.** Die beschriebenen Strukturen und Modellannahmen gelten im angegebenen Bereich.

            **Satz oder Gesetz.** Die zentrale Beziehung lautet {{equation}}.

            **Herleitung und Beweis.** {{explanation}}

            **Formaler Lean-Beweis.** {{leanExplanation}} Der Theoremname lautet `{{theoremName}}`. Die Darstellung
            übersetzt den geprüften Lean-Kern in lesbare Fachsprache, ohne eine ungeprüfte Behauptung als Beweis
            auszugeben.

            **Validierungsstatus.** Die formale Lean-Prüfung, der getrennte analytische Checker und die unabhängige
            numerische Evidenz sind als separate Artefakte im Katalog referenziert und bestanden. Ihre Detaildaten
            bleiben in den Manifesten und Simulationsdaten, damit das Buch selbst als Lehrtext lesbar bleibt.

            **Beispiel.** Ein konkreter Beispielwert zeigt, wie die Formel im Alltag des jeweiligen Gebietes angewendet
            wird und welche Zwischenschritte für Leserinnen und Leser nachvollziehbar bleiben müssen.

            **Interpretation.** Das Resultat erklärt, welche Struktur erhalten bleibt, warum die Aussage fachlich
            nützlich ist und wo ihre Grenzen gegenüber allgemeineren Modellen liegen.

            **Gültigkeitsbereich.** Die Aussage gilt innerhalb der {{domain}} und wird außerhalb dieses Bereichs nicht
            ohne zusätzliche Voraussetzungen verallgemeinert.
            """;
    }

    private static string BuildLeanProof(UnitFixture unit) => unit.Id == "addition-commutativity"
        ? """
            import Mathlib

            def phymaAdd (a b : ℝ) : ℝ := a + b

            theorem phyma_addition_commutative (a b : ℝ) : phymaAdd a b = phymaAdd b a := by
              simp [phymaAdd, add_comm]
            """
        : """
            import Mathlib

            def phymaPosition (x₀ v t : ℝ) : ℝ := x₀ + v * t

            theorem phyma_uniform_motion_zero_time (x₀ v : ℝ) : phymaPosition x₀ v 0 = x₀ := by
              simp [phymaPosition]
            """;

    private static void WriteManifest(
        string workspace,
        string relativeManifest,
        UnitFixture unit,
        string kind,
        string artifactPath,
        string? theoremName)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["caseId"] = unit.Id,
            ["kind"] = kind,
            ["statement"] = unit.Statement,
            ["assumptions"] = new[] { "Die explizit dokumentierten Voraussetzungen gelten." },
            ["validityDomain"] = unit.ValidityDomain,
            ["artifact"] = Normalize(Path.GetRelativePath(workspace, artifactPath)),
            ["sourceSha256"] = HashFile(artifactPath),
        };
        if (theoremName is not null) manifest["theoremName"] = theoremName;
        var fullPath = Path.Combine(workspace, relativeManifest.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(fullPath, JsonSerializer.Serialize(manifest, IndentedJson));
    }

    private static void WriteNumericalData(string path, string id)
    {
        const double genericObserved = 9.000000001;
        const double genericReference = 9.0;
        var genericAbsolute = Math.Abs(genericObserved - genericReference);
        var genericRelative = genericAbsolute / Math.Abs(genericReference);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new
                {
                    caseId = id,
                    method = "Unabhängige direkte Auswertung mit doppelt genauer Gleitkommaarithmetik.",
                    independentImplementation = "Separater numerischer Checker ohne Aufruf des symbolischen Programms.",
                    absoluteTolerance = 1e-6,
                    relativeTolerance = 1e-6,
                    maxAbsoluteError = genericAbsolute,
                    maxRelativeError = genericRelative,
                    samples = new object[]
                    {
                        new { name = "bekannte Lösung", kind = "reference", input = new { x = 2.0 }, observed = 4.0, reference = 4.0, absoluteError = 0.0, relativeError = 0.0 },
                        new { name = "Randfall", kind = "boundary", input = new { x = 0.0 }, observed = 0.0, reference = 0.0, absoluteError = 0.0, relativeError = 0.0 },
                        new { name = "reguläre Stichprobe", kind = "generic", input = new { x = 3.0 }, observed = genericObserved, reference = genericReference, absoluteError = genericAbsolute, relativeError = genericRelative },
                    },
                },
                IndentedJson));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WriteMinimalPdf(string path)
    {
        var content = "%PDF-1.7\n"
                      + "1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n"
                      + "2 0 obj << /Type /Pages /Count 0 >> endobj\n"
                      + "trailer << /Root 1 0 R >>\n"
                      + "%%EOF\n";
        File.WriteAllText(path, content + new string(' ', 1200), Encoding.ASCII);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private sealed record UnitFixture(
        string Id,
        string Title,
        string Discipline,
        string Field,
        string Statement,
        string ValidityDomain,
        string TheoremName);

    private sealed record CheckerPair(string AnalyticalPath, string NumericalPath);

    private sealed record PhyMaFixture(
        string Workspace,
        string BookPath,
        CodingProofVerificationResult[] Proofs,
        string[] NumericalDataPaths,
        CheckerPair[] CheckerPairs);
}
