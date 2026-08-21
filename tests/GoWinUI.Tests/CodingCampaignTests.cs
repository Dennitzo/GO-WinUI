using System.Security.Cryptography;
using System.Text.Json;
using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;

namespace GoWinUI.Tests;

public sealed class CodingCampaignTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ProofFailureIssues = ["Fall minkowski: kein erfolgreicher Beweis."];
    private static readonly CodingProofVerificationResult[] ProofFailures =
    [
        new(
            "minkowski",
            "proofs/minkowski/proof.json",
            CodingProofKind.NumericalEvidence,
            false,
            false,
            "Ungültiges Beweismanifest: Unbekannte Beweisart: symbolic-verification"),
    ];

    [Fact]
    public async Task EinsteinValidationReportsNullResidualsAndAcceptsStructuredValidityDomain()
    {
        var workspace = CreateTemporaryWorkspace();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "einstein_cases.json"),
                """
                {
                  "schemaVersion": "1.0.0",
                  "cases": [{
                    "id": "minkowski",
                    "title": "Minkowski",
                    "theoryDomain": "Allgemeine Relativitätstheorie",
                    "approximationLevel": "exact",
                    "classification": "verified",
                    "equations": ["G=0"],
                    "assumptions": ["Vakuum"],
                    "validityDomain": { "description": "Global" },
                    "residualSamples": [
                      { "evaluationPoint": { "t": 0, "x": 0 }, "einsteinResidual": null, "bianchiResidual": 0 },
                      { "evaluationPoint": { "t": 1, "x": 1 }, "einsteinResidual": 0, "bianchiResidual": null }
                    ],
                    "maxEinsteinResidual": 0,
                    "maxBianchiResidual": 0,
                    "verificationMethod": "symbolisch",
                    "independentChecks": ["Grenzfall"],
                    "conclusion": "Referenz",
                    "visualizations": ["visualizations/minkowski_reference.png"],
                    "simulationData": ["simulation_data/minkowski_reference.json"]
                  }]
                }
                """);

            var result = await new EinsteinCodingCampaignDefinition(new CodingProofVerifier())
                .ValidateAsync(workspace);

            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, issue => issue.Contains("einsteinResidual", StringComparison.Ordinal)
                || issue.Contains("bianchiResidual", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Issues, issue =>
                issue.Contains("Fall minkowski", StringComparison.Ordinal)
                && issue.Contains("validityDomain", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowStateAndIterationsSurviveRepositoryReload()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var workflows = environment.Get<ICodingCampaignRepository>();
        var session = await chats.CreateSessionAsync("Einstein-Workflow");
        var now = DateTimeOffset.UtcNow;
        var state = new CodingCampaignState(
            Guid.NewGuid(), session.Id, "einstein-field-equations", "Einsteinsche Feldgleichungen",
            Path.GetTempPath(), "fingerprint", "qwen3-coder-next", CodingCampaignStatus.Running,
            CodingCampaignPhase.Iteration, 7, "Kerr-Geometrie", null, "[]", 2, now, now);
        var iteration = new CodingCampaignIteration(
            Guid.NewGuid(), state.Id, 7, CodingCampaignPhase.Iteration, "Kerr-Geometrie", null,
            "running", null, "[]", now, now);

        await workflows.SaveAsync(state);
        await workflows.SaveIterationAsync(iteration);

        Assert.Equal(state, await workflows.GetForSessionAsync(session.Id));
        Assert.Equal(iteration, Assert.Single(await workflows.ListIterationsAsync(state.Id)));
    }

    [Fact]
    public async Task SymbolicProofMustExecuteAndMatchItsPinnedHash()
    {
        var workspace = CreateTemporaryWorkspace();
        try
        {
            var proofDirectory = Directory.CreateDirectory(Path.Combine(workspace, "proofs", "minkowski"));
            var artifactPath = Path.Combine(proofDirectory.FullName, "proof.py");
            await File.WriteAllTextAsync(artifactPath, "from fractions import Fraction\nassert Fraction(2, 3) + Fraction(1, 3) == 1\n");
            var manifest = new
            {
                caseId = "minkowski",
                kind = "symbolic",
                statement = "Die exakte rationale Identität zwei Drittel plus ein Drittel ist eins.",
                assumptions = Array.Empty<string>(),
                validityDomain = new
                {
                    description = "Rationale Zahlen mit von null verschiedenen Nennern.",
                    numberSystem = "Q",
                },
                artifact = "proofs/minkowski/proof.py",
                sourceSha256 = await ComputeSha256Async(artifactPath),
            };
            await File.WriteAllTextAsync(
                Path.Combine(proofDirectory.FullName, "proof.json"),
                JsonSerializer.Serialize(manifest));

            var result = Assert.Single(await new CodingProofVerifier().VerifyAllAsync(workspace));

            Assert.True(result.IsProof);
            Assert.True(result.Passed, result.Detail);
            Assert.Equal(CodingProofKind.Symbolic, result.Kind);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ProofManifestRejectsUnknownKindWithoutLosingCaseIdentity()
    {
        var workspace = CreateTemporaryWorkspace();
        try
        {
            var proofDirectory = Directory.CreateDirectory(Path.Combine(workspace, "proofs", "minkowski"));
            var artifactPath = Path.Combine(proofDirectory.FullName, "proof.py");
            await File.WriteAllTextAsync(artifactPath, "print('not executed')\n");
            await File.WriteAllTextAsync(
                Path.Combine(proofDirectory.FullName, "proof.json"),
                JsonSerializer.Serialize(new
                {
                    caseId = "minkowski",
                    kind = "symbolic-verification",
                    statement = "Diese Fixture prüft, dass freie Beweisart-Bezeichnungen nicht akzeptiert werden.",
                    assumptions = Array.Empty<string>(),
                    validityDomain = "Testbereich für den Manifestvertrag.",
                    artifact = "proofs/minkowski/proof.py",
                    sourceSha256 = await ComputeSha256Async(artifactPath),
                }));

            var result = Assert.Single(await new CodingProofVerifier().VerifyAllAsync(workspace));

            Assert.Equal("minkowski", result.CaseId);
            Assert.False(result.Passed);
            Assert.Contains("symbolic-verification", result.Detail, StringComparison.Ordinal);
            Assert.Contains("symbolic, interval-certified, formal und numerical-evidence", result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ProofManifestRejectsAdditionalProperties()
    {
        var workspace = CreateTemporaryWorkspace();
        try
        {
            var proofDirectory = Directory.CreateDirectory(Path.Combine(workspace, "proofs", "minkowski"));
            var artifactPath = Path.Combine(proofDirectory.FullName, "proof.py");
            await File.WriteAllTextAsync(artifactPath, "print('not executed')\n");
            var manifest = $$"""
                {
                  "caseId": "minkowski",
                  "kind": "symbolic",
                  "statement": "Diese Fixture prüft einen strikt geschlossenen Manifestvertrag.",
                  "assumptions": [],
                  "validityDomain": "Testbereich für den Manifestvertrag.",
                  "artifact": "proofs/minkowski/proof.py",
                  "sourceSha256": "{{await ComputeSha256Async(artifactPath)}}",
                  "modelVerdict": "verified"
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(proofDirectory.FullName, "proof.json"), manifest);

            var result = Assert.Single(await new CodingProofVerifier().VerifyAllAsync(workspace));

            Assert.Equal("minkowski", result.CaseId);
            Assert.False(result.Passed);
            Assert.Contains("Unerlaubte Manifestfelder: modelVerdict", result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task FormalProofWithSorryIsRejectedBeforeLeanRuns()
    {
        var workspace = CreateTemporaryWorkspace();
        try
        {
            var proofDirectory = Directory.CreateDirectory(Path.Combine(workspace, "proofs", "invalid"));
            var artifactPath = Path.Combine(proofDirectory.FullName, "Invalid.lean");
            await File.WriteAllTextAsync(artifactPath, "theorem invalid : True := by\n  sorry\n");
            var manifest = $$"""
                {
                  "caseId": "invalid",
                  "kind": "formal",
                  "statement": "Ein unvollständiger formaler Testbeweis darf nicht akzeptiert werden.",
                  "assumptions": [],
                  "validityDomain": "Lean-Testfixture ohne physikalische Behauptung.",
                  "artifact": "proofs/invalid/Invalid.lean",
                  "sourceSha256": "{{await ComputeSha256Async(artifactPath)}}"
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(proofDirectory.FullName, "proof.json"), manifest);

            var result = Assert.Single(await new CodingProofVerifier().VerifyAllAsync(workspace));

            Assert.True(result.IsProof);
            Assert.False(result.Passed);
            Assert.Contains("sorry", result.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void EinsteinWorkflowRequiresExecutableProofsAndCaseSpecificPlots()
    {
        var definition = new EinsteinCodingCampaignDefinition(new CodingProofVerifier());
        var bootstrap = definition.BuildBootstrapPrompt();
        var iteration = definition.BuildIterationPrompt(3, definition.GetChallenge(3));
        var correction = definition.BuildCorrectionPrompt(3, definition.GetChallenge(3), ["Testmangel"]);

        Assert.Contains("proof.json", bootstrap, StringComparison.Ordinal);
        Assert.Contains("Numerische Evidenz allein ist kein Beweis", bootstrap, StringComparison.Ordinal);
        Assert.Contains("striktes RFC-8259-JSON", bootstrap, StringComparison.Ordinal);
        Assert.Contains("Matrix.trace", bootstrap, StringComparison.Ordinal);
        Assert.Contains("Koordinatensingularität", bootstrap, StringComparison.Ordinal);
        Assert.Contains("complexified_spacetime", bootstrap, StringComparison.Ordinal);
        Assert.Contains("realityConditions", bootstrap, StringComparison.Ordinal);
        Assert.Contains("komplexen Tensors", bootstrap, StringComparison.Ordinal);
        Assert.Contains("undetermined ist ausschließlich eine informative Klassifizierung", bootstrap, StringComparison.Ordinal);
        Assert.Contains("Offene Untersuchung", bootstrap, StringComparison.Ordinal);
        Assert.Contains("fallbezogene Daten, Plots und Simulationen", iteration, StringComparison.Ordinal);
        Assert.Contains("fachlich beschreibende Dateinamen", iteration, StringComparison.Ordinal);
        Assert.Contains("live_progress", bootstrap, StringComparison.Ordinal);
        Assert.Contains("live_progress", iteration, StringComparison.Ordinal);
        Assert.Contains("live_progress", correction, StringComparison.Ordinal);
        Assert.Contains("sorry", iteration, StringComparison.Ordinal);
        Assert.Contains("complexified_spacetime", iteration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EinsteinWorkflowPublishesReferencedVerifiedAndOpenDocuments()
    {
        var workspace = CreateTemporaryWorkspace();
        try
        {
            var solutions = Directory.CreateDirectory(Path.Combine(workspace, "solutions"));
            await File.WriteAllTextAsync(Path.Combine(solutions.FullName, "minkowski.md"), "# Minkowski");
            await File.WriteAllTextAsync(Path.Combine(solutions.FullName, "kerr.md"), "# Kerr");
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "einstein_cases.json"),
                """
                {
                  "cases": [
                    {
                      "id": "minkowski",
                      "classification": "verified",
                      "solutionDocument": "solutions/minkowski.md"
                    },
                    {
                      "id": "kerr",
                      "classification": "undetermined",
                      "solutionDocument": "solutions/kerr.md"
                    }
                  ]
                }
                """);

            var documents = new EinsteinCodingCampaignDefinition(new CodingProofVerifier())
                .GetPublishableSolutionDocuments(workspace);

            Assert.NotNull(documents);
            Assert.Equal(
                ["solutions/kerr.md", "solutions/minkowski.md"],
                documents.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task EinsteinValidationAllowsReferencedUndeterminedSolutionArtifacts()
    {
        var workspace = CreateTemporaryWorkspace();
        try
        {
            var solutions = Directory.CreateDirectory(Path.Combine(workspace, "solutions"));
            await File.WriteAllTextAsync(Path.Combine(solutions.FullName, "kerr.md"), "# Offene Kerr-Analyse");
            await File.WriteAllBytesAsync(Path.Combine(solutions.FullName, "kerr.pdf"), [1, 2, 3]);
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "einstein_cases.json"),
                """
                {
                  "cases": [
                    {
                      "id": "kerr",
                      "title": "Kerr",
                      "theoryDomain": "Allgemeine Relativitätstheorie",
                      "approximationLevel": "exact",
                      "classification": "undetermined",
                      "equations": ["G=0"],
                      "assumptions": ["Vakuum"],
                      "validityDomain": "Reguläre Außenregion.",
                      "residualSamples": [],
                      "maxEinsteinResidual": 0.000001,
                      "maxBianchiResidual": 0.000001,
                      "verificationMethod": "numerische Evidenz",
                      "independentChecks": ["Grenzfall"],
                      "conclusion": "Beweis offen.",
                      "visualizations": ["visualizations/kerr.png"],
                      "simulationData": ["simulation_data/kerr.json"],
                      "solutionDocument": "solutions/kerr.md"
                    },
                    {
                      "id": "complexified_spacetime",
                      "title": "Komplexifizierte Raumzeit",
                      "theoryDomain": "Quantengravitation",
                      "approximationLevel": "exploratory",
                      "classification": "undetermined",
                      "equations": ["G=0"],
                      "assumptions": ["Analytische Fortsetzung"],
                      "validityDomain": "Explorativer Bereich.",
                      "residualSamples": [],
                      "maxEinsteinResidual": 1,
                      "maxBianchiResidual": 1,
                      "verificationMethod": "offen",
                      "independentChecks": ["reeller Grenzfall"],
                      "conclusion": "Beweis offen.",
                      "visualizations": ["visualizations/complex.png"],
                      "simulationData": ["simulation_data/complex.json"]
                    }
                  ]
                }
                """);

            var result = await new EinsteinCodingCampaignDefinition(new CodingProofVerifier())
                .ValidateAsync(workspace);

            Assert.DoesNotContain(
                result.Issues,
                issue => issue.Contains("classification undetermined", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Issues, issue => issue.Contains("solutions/kerr.md", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Issues, issue => issue.Contains("solutions/kerr.pdf", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task EinsteinValidationRejectsReintroducedGenericProgressArtifacts()
    {
        var workspace = CreateTemporaryWorkspace();
        try
        {
            var simulation = Directory.CreateDirectory(Path.Combine(workspace, "simulation_data"));
            var visualizations = Directory.CreateDirectory(Path.Combine(workspace, "visualizations"));
            await File.WriteAllTextAsync(Path.Combine(simulation.FullName, "live_progress.json"), "{}");
            await File.WriteAllBytesAsync(Path.Combine(visualizations.FullName, "live_progress.png"), OnePixelPng);
            await File.WriteAllTextAsync(Path.Combine(workspace, "einstein_cases_backup.json"), "{\"cases\":[]}");
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "visualize_einstein.py"),
                "def update_live_progress_plot():\n    pass\n");

            var result = await new EinsteinCodingCampaignDefinition(new CodingProofVerifier())
                .ValidateAsync(workspace);

            Assert.Contains(result.Issues, issue => issue.Contains("simulation_data/live_progress.json", StringComparison.Ordinal));
            Assert.Contains(result.Issues, issue => issue.Contains("visualizations/live_progress.png", StringComparison.Ordinal));
            Assert.Contains(result.Issues, issue => issue.Contains("live_progress-Funktion", StringComparison.Ordinal));
            Assert.Contains(result.Issues, issue => issue.Contains("einstein_cases_backup.json", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData("Gültig für r > 2M; die Metrik versagt am und unter dem Horizont.", false)]
    [InlineData("Die Schwarzschild-Karte hat bei r=2M eine Koordinatensingularität; bei r=0 liegt die echte Krümmungssingularität.", true)]
    public void SchwarzschildDomainMustDistinguishCoordinateAndCurvatureSingularities(
        string description,
        bool expected)
    {
        Assert.Equal(
            expected,
            EinsteinCodingCampaignDefinition.SchwarzschildDomainDistinguishesHorizonAndCurvature(description));
    }

    [Theory]
    [InlineData(
        "Wick-Rotation als analytische Fortsetzung von lorentzscher zu euklidischer Signatur; die Metrik bleibt formal invariant und identisch zur Minkowski-Metrik.",
        false)]
    [InlineData(
        "Wick-Rotation als analytische Fortsetzung: Die lorentzsche Minkowski-Signatur wird unter der angegebenen Konvention in eine euklidische Signatur fortgesetzt und durch die Rücktransformation wiedergewonnen.",
        true)]
    [InlineData("Komplexe Koordinate ohne definierte Signatur oder Realitätsbedingung.", false)]
    public void ComplexifiedSpacetimeMustDistinguishWickRotationSignatureChange(
        string description,
        bool expected)
    {
        Assert.Equal(
            expected,
            EinsteinCodingCampaignDefinition.ComplexifiedSpacetimeDistinguishesWickRotationSignature(description));
    }

    [Fact]
    public void CorrectionPromptProblemsIncludeIndependentProofFailureDetails()
    {
        var validation = JsonSerializer.Serialize(
            new
            {
                issues = ProofFailureIssues,
                proofs = ProofFailures,
            },
            WebJsonOptions);

        var problems = CodingCampaignService.ParseValidationProblems(validation);

        Assert.Contains("Fall minkowski: kein erfolgreicher Beweis.", problems);
        Assert.Contains(
            problems,
            problem => problem.Contains("proofs/minkowski/proof.json", StringComparison.Ordinal)
                && problem.Contains("symbolic-verification", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EinsteinValidationRejectsMinkowskiMatrixLabeledAsSchwarzschild()
    {
        var workspace = CreateTemporaryWorkspace();
        try
        {
            foreach (var file in new[]
                     {
                         "einstein_engine.py", "test_einstein_engine.py", "einstein_analysis.md", "visualize_einstein.py",
                     })
            {
                await File.WriteAllTextAsync(Path.Combine(workspace, file), "# fixture\n");
            }
            await File.WriteAllTextAsync(Path.Combine(workspace, "einstein_attempts.json"), "{\"attempts\":[]}");
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "einstein_cases.json"),
                """
                {
                  "schemaVersion": "1",
                  "cases": [
                    {
                      "id": "schwarzschild",
                      "title": "Schwarzschild",
                      "theoryDomain": "GR",
                      "approximationLevel": "exact",
                      "classification": "undetermined",
                      "equations": ["G=0"],
                      "assumptions": ["Vakuum"],
                      "validityDomain": "r > 2M",
                      "metric": [[-1,0,0,0],[0,1,0,0],[0,0,1,0],[0,0,0,1]],
                      "residualSamples": [],
                      "maxEinsteinResidual": 0,
                      "maxBianchiResidual": 0,
                      "verificationMethod": "offen",
                      "independentChecks": ["Grenzfall"],
                      "conclusion": "offen",
                      "visualizations": ["visualizations/schwarzschild_geometry.png"],
                      "simulationData": ["simulation_data/schwarzschild_geometry.json"]
                    },
                    {
                      "id": "complexified_spacetime",
                      "title": "Komplexifiziert",
                      "theoryDomain": "Komplexe GR",
                      "approximationLevel": "exploratory",
                      "classification": "undetermined",
                      "equations": ["G(g)=0"],
                      "assumptions": ["Analytisch"],
                      "validityDomain": "offen",
                      "residualSamples": [],
                      "maxEinsteinResidual": 1,
                      "maxBianchiResidual": 1,
                      "verificationMethod": "offen",
                      "independentChecks": ["reeller Grenzfall"],
                      "conclusion": "offen",
                      "visualizations": ["visualizations/complexified_spacetime.png"],
                      "simulationData": ["simulation_data/complexified_spacetime.json"],
                      "complexificationDefinition": "g = g_R + i g_I",
                      "realityConditions": ["g_I -> 0"],
                      "realObservableMap": "Realteil unter Bedingungen",
                      "residualNormDefinition": "Frobeniusnorm von Real- und Imaginärteil",
                      "establishedFormalismRelations": ["Wick-Rotation"],
                      "epistemicStatus": "mathematical-exploration",
                      "claimScope": "mathematical-consistency"
                    }
                  ]
                }
                """);
            var simulation = Directory.CreateDirectory(Path.Combine(workspace, "simulation_data"));
            await File.WriteAllTextAsync(
                Path.Combine(simulation.FullName, "schwarzschild_geometry.json"),
                JsonSerializer.Serialize(new
                {
                    status = "completed",
                    caseId = "schwarzschild",
                    phase = "validation",
                    step = 1,
                    totalSteps = 1,
                    updatedAt = DateTimeOffset.UtcNow,
                    metrics = new { residual = 0 },
                }));
            var visualizations = Directory.CreateDirectory(Path.Combine(workspace, "visualizations"));
            await File.WriteAllBytesAsync(Path.Combine(visualizations.FullName, "schwarzschild_geometry.png"), OnePixelPng);

            var result = await new EinsteinCodingCampaignDefinition(new CodingProofVerifier()).ValidateAsync(workspace);

            Assert.Contains(result.Issues, issue =>
                issue.Contains("Minkowski-Metrik", StringComparison.Ordinal)
                && issue.Contains("Schwarzschild", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task EinsteinValidationRequiresAQualifiedComplexifiedSpacetimeCase()
    {
        var workspace = CreateTemporaryWorkspace();
        try
        {
            foreach (var file in new[]
                     {
                         "einstein_engine.py", "test_einstein_engine.py", "einstein_analysis.md", "visualize_einstein.py",
                     })
            {
                await File.WriteAllTextAsync(Path.Combine(workspace, file), "# fixture\n");
            }
            await File.WriteAllTextAsync(Path.Combine(workspace, "einstein_attempts.json"), "{\"attempts\":[]}");
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "einstein_cases.json"),
                """
                {
                  "schemaVersion": "1.0.0",
                  "cases": [
                    {
                      "id": "minkowski",
                      "title": "Minkowski",
                      "theoryDomain": "GR",
                      "approximationLevel": "exact",
                      "classification": "undetermined",
                      "equations": ["G=0"],
                      "assumptions": ["Vakuum"],
                      "validityDomain": "Global",
                      "residualSamples": [],
                      "maxEinsteinResidual": 0,
                      "maxBianchiResidual": 0,
                      "verificationMethod": "symbolisch",
                      "independentChecks": ["Grenzfall"],
                      "conclusion": "Referenz",
                      "visualizations": ["visualizations/minkowski_reference.png"],
                      "simulationData": ["simulation_data/minkowski_reference.json"]
                    },
                    {
                      "id": "complexified_spacetime",
                      "title": "Komplexifizierte Raumzeit",
                      "theoryDomain": "Komplexifizierte GR",
                      "approximationLevel": "exploratory",
                      "classification": "undetermined",
                      "equations": ["G(g_R+i g_I)=0"],
                      "assumptions": ["Analytische Fortsetzung"],
                      "validityDomain": "Definierter komplexer Parameterbereich",
                      "residualSamples": [],
                      "maxEinsteinResidual": 1,
                      "maxBianchiResidual": 1,
                      "verificationMethod": "Real- und Imaginärteil getrennt",
                      "independentChecks": ["Reeller Grenzfall"],
                      "conclusion": "Offene mathematische Untersuchung",
                      "visualizations": ["visualizations/complexified_spacetime.png"],
                      "simulationData": ["simulation_data/complexified_spacetime.json"],
                      "complexificationDefinition": "Komplexe analytische Erweiterung der Metrik",
                      "realityConditions": ["Reelle Observablen nach Projektion"],
                      "realObservableMap": "Realitätsschnitt auf reelle Invarianten",
                      "residualNormDefinition": "Betrag aus Real- und Imaginärteil aller Tensorkomponenten",
                      "establishedFormalismRelations": ["Wick-Rotation", "selbstduale Variablen"],
                      "epistemicStatus": "mathematical-exploration",
                      "claimScope": "empirical-model"
                    }
                  ]
                }
                """);
            var simulation = Directory.CreateDirectory(Path.Combine(workspace, "simulation_data"));
            await File.WriteAllTextAsync(
                Path.Combine(simulation.FullName, "complexified_spacetime.json"),
                JsonSerializer.Serialize(new { status = "completed", caseId = "complexified_spacetime", phase = "validation", updatedAt = DateTimeOffset.UtcNow }));
            var visualizations = Directory.CreateDirectory(Path.Combine(workspace, "visualizations"));
            await File.WriteAllBytesAsync(Path.Combine(visualizations.FullName, "complexified_spacetime.png"), OnePixelPng);

            var result = await new EinsteinCodingCampaignDefinition(new CodingProofVerifier()).ValidateAsync(workspace);

            Assert.Contains(result.Issues, issue =>
                issue.Contains("empiricalEvidence", StringComparison.Ordinal)
                && issue.Contains("complexified_spacetime", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Issues, issue => issue.Contains("Pflichtfall complexified_spacetime fehlt", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task EinsteinValidationReportsTheExactStrictJsonLocation()
    {
        var workspace = CreateTemporaryWorkspace();
        try
        {
            foreach (var file in new[]
                     {
                         "einstein_engine.py", "test_einstein_engine.py", "einstein_analysis.md", "visualize_einstein.py",
                     })
            {
                await File.WriteAllTextAsync(Path.Combine(workspace, file), "# fixture\n");
            }
            await File.WriteAllTextAsync(Path.Combine(workspace, "einstein_attempts.json"), "{\"attempts\":[]}");
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "einstein_cases.json"),
                "{\n  \"cases\": [{\"id\":\"minkowski\",\"validityDomain\": [-Infinity, Infinity]}]\n}");
            var simulation = Directory.CreateDirectory(Path.Combine(workspace, "simulation_data"));
            await File.WriteAllTextAsync(
                Path.Combine(simulation.FullName, "invalid_domain.json"),
                JsonSerializer.Serialize(new
                {
                    status = "completed",
                    caseId = "minkowski",
                    phase = "validation",
                    updatedAt = DateTimeOffset.UtcNow,
                }));
            var visualizations = Directory.CreateDirectory(Path.Combine(workspace, "visualizations"));
            await File.WriteAllBytesAsync(Path.Combine(visualizations.FullName, "invalid_domain.png"), OnePixelPng);

            var result = await new EinsteinCodingCampaignDefinition(new CodingProofVerifier())
                .ValidateAsync(workspace);

            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, issue =>
                issue.Contains("striktes RFC-8259-JSON", StringComparison.Ordinal)
                && issue.Contains("Zeile 2", StringComparison.Ordinal)
                && issue.Contains("Spalte", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task VerifiedKerrCaseRejectsTheObservedTautologicalCurvatureImplementation()
    {
        var workspace = CreateTemporaryWorkspace();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "einstein_engine.py"),
                "christoffel = compute_christoffel_symbols(g_num, [r, theta])\n"
                + "riemann = compute_riemann_tensor(christoffel, [r, theta])\n"
                + "ricci_scalar = ricci.trace()\n"
                + "def compute_kretschmann_scalar(riemann):\n"
                + "    kretschmann = 0\n"
                + "    for val in riemann:\n"
                + "        kretschmann += val ** 2\n"
                + "    return kretschmann\n"
                + "area_product = 16 * np.pi**2 * a * M\n");
            foreach (var file in new[] { "test_einstein_engine.py", "einstein_analysis.md", "visualize_einstein.py" })
            {
                await File.WriteAllTextAsync(Path.Combine(workspace, file), "# fixture\n");
            }
            await File.WriteAllTextAsync(Path.Combine(workspace, "einstein_attempts.json"), "{\"attempts\":[]}");
            var common = new
            {
                title = "Referenz",
                theoryDomain = "Allgemeine Relativitätstheorie",
                approximationLevel = "exact",
                equations = new[] { "G=0" },
                assumptions = new[] { "Vakuum" },
                validityDomain = "Regulärer Außenraum.",
                maxEinsteinResidual = 1e-12,
                maxBianchiResidual = 1e-12,
                verificationMethod = "Unabhängige symbolische Prüfung",
                independentChecks = new[] { "Grenzfall" },
                conclusion = "Referenzfall.",
                visualizations = new[] { "visualizations/kerr_geometry.png" },
                simulationData = new[] { "simulation_data/kerr_geometry.json" },
            };
            object[] samples =
            [
                new { evaluationPoint = new { r = 5d, theta = 1d }, einsteinResidual = 0d, bianchiResidual = 0d },
                new { evaluationPoint = new { r = 8d, theta = 1.2d }, einsteinResidual = 0d, bianchiResidual = 0d },
            ];
            var solutionDirectory = Directory.CreateDirectory(Path.Combine(workspace, "solutions"));
            await File.WriteAllTextAsync(Path.Combine(solutionDirectory.FullName, "kerr.md"), "# Kerr\n\nFixture.");
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "einstein_cases.json"),
                JsonSerializer.Serialize(new
                {
                    cases = new object[]
                    {
                        new
                        {
                            id = "minkowski",
                            common.title,
                            common.theoryDomain,
                            common.approximationLevel,
                            classification = "undetermined",
                            common.equations,
                            common.assumptions,
                            common.validityDomain,
                            residualSamples = samples,
                            common.maxEinsteinResidual,
                            common.maxBianchiResidual,
                            common.verificationMethod,
                            common.independentChecks,
                            common.conclusion,
                            common.visualizations,
                            common.simulationData,
                        },
                        new
                        {
                            id = "kerr",
                            title = "Kerr-Metrik",
                            common.theoryDomain,
                            common.approximationLevel,
                            classification = "verified",
                            common.equations,
                            common.assumptions,
                            common.validityDomain,
                            residualSamples = samples,
                            common.maxEinsteinResidual,
                            common.maxBianchiResidual,
                            common.verificationMethod,
                            common.independentChecks,
                            common.conclusion,
                            common.visualizations,
                            common.simulationData,
                            solutionDocument = "solutions/kerr.md",
                        },
                    },
                }));
            var simulation = Directory.CreateDirectory(Path.Combine(workspace, "simulation_data"));
            await File.WriteAllTextAsync(
                Path.Combine(simulation.FullName, "kerr_geometry.json"),
                JsonSerializer.Serialize(new { status = "completed", caseId = "kerr", phase = "validation", updatedAt = DateTimeOffset.UtcNow }));
            var visualizations = Directory.CreateDirectory(Path.Combine(workspace, "visualizations"));
            await File.WriteAllBytesAsync(Path.Combine(visualizations.FullName, "kerr_geometry.png"), OnePixelPng);

            var result = await new EinsteinCodingCampaignDefinition(new CodingProofVerifier())
                .ValidateAsync(workspace);

            Assert.Contains(result.Issues, issue => issue.Contains("vor den koordinatenabhängigen Ableitungen", StringComparison.Ordinal));
            Assert.Contains(result.Issues, issue => issue.Contains("nur mit zwei Koordinaten", StringComparison.Ordinal));
            Assert.Contains(result.Issues, issue => issue.Contains("Matrix.trace", StringComparison.Ordinal));
            Assert.Contains(result.Issues, issue => issue.Contains("Horizontflächenprodukt", StringComparison.Ordinal));
            Assert.Contains(result.Issues, issue => issue.Contains("Summe quadrierter Koordinatenkomponenten", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void WebViewLoadsCodingWorkflowsWithoutSpecialPlotModule()
    {
        var webRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", "Web"));
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var script = File.ReadAllText(Path.Combine(webRoot, "app.js"));

        Assert.DoesNotContain("id=\"campaign-dashboard\"", html, StringComparison.Ordinal);
        Assert.Contains("campaign.select", script, StringComparison.Ordinal);
        Assert.Contains("campaign.run", script, StringComparison.Ordinal);
        Assert.Contains("Workflow laden", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Coding-Kampagnen", script, StringComparison.Ordinal);
        Assert.Contains("case \"chat.message\":", script, StringComparison.Ordinal);
        Assert.Contains("shouldSuppressMessageInChat(message)", script, StringComparison.Ordinal);
        Assert.Contains("content.includes(\"### Prozessbericht\")", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectingOnlyLoadsWorkflowAndComposerRunContinuesUntilStop()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.txt"), "ready");
        var chats = environment.Get<IChatRepository>();
        var session = await CreateCodingSessionAsync(chats, workspace, "Dauerlauf");
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        var agent = new FakeCampaignAgent(chats);
        var definition = new FakeCampaignDefinition();
        using var service = CreateService(environment, chats, settings, agent, definition);

        var selected = await service.SelectAsync(session.Id, definition.Descriptor.Id);
        Assert.Equal("stopped", selected.ActiveCampaign?.Status);
        Assert.Equal(0, agent.RunCount);

        await service.RunAsync(session.Id);
        await agent.WaitForRunsAsync(2, TimeSpan.FromSeconds(5));
        var stopped = await service.StopAsync(session.Id);

        Assert.Equal("stopped", stopped.ActiveCampaign?.Status);
        var countAtStop = agent.RunCount;
        await Task.Delay(120);
        Assert.Equal(countAtStop, agent.RunCount);
    }

    [Fact]
    public async Task ClientStartupStopsPersistedWorkflowAndItsRunningIterationWithoutStartingAgent()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "startup-workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.txt"), "ready");
        var chats = environment.Get<IChatRepository>();
        var workflows = environment.Get<ICodingCampaignRepository>();
        var session = await CreateCodingSessionAsync(chats, workspace, "Gestoppter Clientstart");
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        var agent = new FakeCampaignAgent(chats);
        var definition = new FakeCampaignDefinition();
        using var service = CreateService(environment, chats, settings, agent, definition);

        await service.SelectAsync(session.Id, definition.Descriptor.Id);
        var selected = (await workflows.GetForSessionAsync(session.Id))!;
        var now = DateTimeOffset.UtcNow;
        await workflows.SaveAsync(selected with { Status = CodingCampaignStatus.Running, UpdatedAt = now });
        var iteration = new CodingCampaignIteration(
            Guid.NewGuid(), selected.Id, selected.Iteration, CodingCampaignPhase.Iteration,
            "Persistierter Lauf", null, "running", null, "[]", now, now);
        await workflows.SaveIterationAsync(iteration);

        await service.PrepareForClientStartAsync();

        Assert.Equal(CodingCampaignStatus.Stopped, (await workflows.GetForSessionAsync(session.Id))!.Status);
        var savedIteration = Assert.Single(await workflows.ListIterationsAsync(selected.Id));
        Assert.Equal("cancelled", savedIteration.Status);
        Assert.Contains("Client", Assert.IsType<string>(savedIteration.Error), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, agent.AttemptCount);
    }

    [Fact]
    public async Task RepeatedClientStartupPreparationCannotStopAWorkflowStartedAfterTheFirstPreparation()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "startup-race-workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.txt"), "ready");
        var chats = environment.Get<IChatRepository>();
        var workflows = environment.Get<ICodingCampaignRepository>();
        var session = await CreateCodingSessionAsync(chats, workspace, "Startbereinigungs-Race");
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        var agent = new FakeCampaignAgent(chats);
        var definition = new FakeCampaignDefinition();
        using var service = CreateService(environment, chats, settings, agent, definition);

        await service.SelectAsync(session.Id, definition.Descriptor.Id);
        await service.PrepareForClientStartAsync();
        await service.RunAsync(session.Id);
        await service.PrepareForClientStartAsync();
        await agent.WaitForRunsAsync(1, TimeSpan.FromSeconds(5));

        Assert.True(service.IsRunning);
        Assert.Equal(CodingCampaignStatus.Running, (await workflows.GetForSessionAsync(session.Id))!.Status);

        _ = await service.StopAsync(session.Id);
    }

    [Fact]
    public async Task StopButtonStopsPersistedWorkflowWithoutInMemoryLoop()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "detached-workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.txt"), "ready");
        var chats = environment.Get<IChatRepository>();
        var workflows = environment.Get<ICodingCampaignRepository>();
        var session = await CreateCodingSessionAsync(chats, workspace, "Persistierter Lauf");
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        var agent = new FakeCampaignAgent(chats);
        var definition = new FakeCampaignDefinition();
        using var service = CreateService(environment, chats, settings, agent, definition);

        await service.SelectAsync(session.Id, definition.Descriptor.Id);
        var selected = (await workflows.GetForSessionAsync(session.Id))!;
        await workflows.SaveAsync(selected with
        {
            Status = CodingCampaignStatus.Running,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var snapshot = await service.StopAsync(session.Id);

        Assert.Equal("stopped", snapshot.ActiveCampaign?.Status);
        Assert.Equal(CodingCampaignStatus.Stopped, (await workflows.GetForSessionAsync(session.Id))!.Status);
        Assert.Equal(1, agent.CancelCount);
    }

    [Fact]
    public async Task TransientAgentFailureIsRetriedWithoutStoppingWorkflow()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "retry-workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.txt"), "ready");
        var chats = environment.Get<IChatRepository>();
        var workflowRepository = environment.Get<ICodingCampaignRepository>();
        var session = await CreateCodingSessionAsync(chats, workspace, "Fortsetzbarer Workflow");
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        var agent = new FakeCampaignAgent(chats) { FailuresRemaining = 1 };
        var definition = new FakeCampaignDefinition();
        using var service = CreateService(environment, chats, settings, agent, definition);

        await service.SelectAsync(session.Id, definition.Descriptor.Id);
        await service.RunAsync(session.Id, "Prüfe den aktuellen Stand.");
        await agent.WaitForRunsAsync(1, TimeSpan.FromSeconds(5));
        await service.StopAsync(session.Id);

        Assert.True(agent.AttemptCount >= 2);
        Assert.NotEqual(
            CodingCampaignStatus.Faulted,
            (await workflowRepository.GetForSessionAsync(session.Id))!.Status);
    }

    [Fact]
    public async Task ComposerInstructionIsSentOnlyToCodingAgentAndCanReplaceRunningStep()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "instruction-workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.txt"), "ready");
        var chats = environment.Get<IChatRepository>();
        var session = await CreateCodingSessionAsync(chats, workspace, "Anweisungs-Workflow");
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        var agent = new FakeCampaignAgent(chats);
        var definition = new FakeCampaignDefinition();
        using var service = CreateService(environment, chats, settings, agent, definition);

        await service.SelectAsync(session.Id, definition.Descriptor.Id);
        await service.RunAsync(session.Id, "Konzentriere dich zunächst auf die Randbedingungen.");
        await agent.WaitForRunsAsync(1, TimeSpan.FromSeconds(5));
        await service.RunAsync(session.Id, "Verwende jetzt stattdessen eine adaptive Diskretisierung.");
        await agent.WaitForPromptAsync("adaptive Diskretisierung", TimeSpan.FromSeconds(5));
        await service.StopAsync(session.Id);

        Assert.Contains(agent.Prompts, prompt =>
            prompt.Contains("Zusätzliche aktuelle Nutzeranweisung", StringComparison.Ordinal)
            && prompt.Contains("Randbedingungen", StringComparison.Ordinal));
        Assert.Contains(agent.Prompts, prompt =>
            prompt.Contains("Zusätzliche aktuelle Nutzeranweisung", StringComparison.Ordinal)
            && prompt.Contains("adaptive Diskretisierung", StringComparison.Ordinal));
        Assert.All(agent.TriggerActions, action => Assert.Equal(PromptTriggerAction.Code, action));
        Assert.All(agent.Prompts, prompt =>
        {
            Assert.Contains("### Prozessbericht", prompt, StringComparison.Ordinal);
            Assert.Contains("**Gegenstand:**", prompt, StringComparison.Ordinal);
            Assert.Contains("**Aktion:**", prompt, StringComparison.Ordinal);
            Assert.Contains("**Annahmenänderung:**", prompt, StringComparison.Ordinal);
            Assert.Contains("**Prüfung:**", prompt, StringComparison.Ordinal);
        });

        var visibleUserMessages = (await chats.ListMessagesAsync(session.Id))
            .Where(static message => message.Role == ChatRole.User)
            .Select(static message => message.Content)
            .ToArray();
        Assert.Equal(
            [
                "Konzentriere dich zunächst auf die Randbedingungen.",
                "Verwende jetzt stattdessen eine adaptive Diskretisierung.",
            ],
            visibleUserMessages);
        Assert.DoesNotContain(visibleUserMessages, message =>
            message.Contains("Zusätzliche aktuelle Nutzeranweisung", StringComparison.Ordinal)
            || message.Contains("Aktueller Schwerpunkt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryPlotIsPublishedExactlyOnceWithItsImageArtifact()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "plot-workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.txt"), "ready");
        var visualizations = Directory.CreateDirectory(Path.Combine(workspace, "visualizations"));
        await File.WriteAllBytesAsync(Path.Combine(visualizations.FullName, "minkowski_lightcone.png"), OnePixelPng);
        await File.WriteAllBytesAsync(
            Path.Combine(visualizations.FullName, "kerr_horizons.png"),
            [.. OnePixelPng, 1]);
        await File.WriteAllBytesAsync(
            Path.Combine(visualizations.FullName, "kerr_ergosphere.jpg"),
            [.. OnePixelPng, 2]);
        var chats = environment.Get<IChatRepository>();
        var artifacts = environment.Get<IChatArtifactRepository>();
        var session = await CreateCodingSessionAsync(chats, workspace, "Plot-Workflow");
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        var definition = new FakeCampaignDefinition();
        using var service = CreateService(environment, chats, settings, new FakeCampaignAgent(chats), definition);

        await service.SelectAsync(session.Id, definition.Descriptor.Id);
        await service.SelectAsync(session.Id, definition.Descriptor.Id);

        var messages = await chats.ListMessagesAsync(session.Id);
        var plotMessages = messages
            .Where(static message => message.ToolExecution?.Tool == "coding.workflow.plot")
            .ToArray();
        Assert.Equal(3, plotMessages.Length);
        var publishedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plotMessage in plotMessages)
        {
            var artifact = Assert.Single(await artifacts.ListForMessageAsync(plotMessage.Id));
            publishedFiles.Add(artifact.FileName);
            Assert.StartsWith("image/", artifact.ContentType, StringComparison.Ordinal);
            Assert.True(await environment.Get<IBinaryObjectStore>().VerifyAsync(artifact.BlobId));
        }
        Assert.Equal(
            ["kerr_ergosphere.jpg", "kerr_horizons.png", "minkowski_lightcone.png"],
            publishedFiles.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FailedValidationKeepsCorrectingUntilAValidStepThenContinuesAutonomously()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "validation-loop-workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.txt"), "ready");
        var chats = environment.Get<IChatRepository>();
        var workflows = environment.Get<ICodingCampaignRepository>();
        var session = await CreateCodingSessionAsync(chats, workspace, "Autonome Korrektur");
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        var agent = new FakeCampaignAgent(chats);
        var definition = new FakeCampaignDefinition { ValidationFailuresRemaining = 2 };
        using var service = CreateService(environment, chats, settings, agent, definition);

        await service.SelectAsync(session.Id, definition.Descriptor.Id);
        await service.RunAsync(session.Id);
        await agent.WaitForRunsAsync(4, TimeSpan.FromSeconds(5));
        var running = await workflows.GetForSessionAsync(session.Id);
        await service.StopAsync(session.Id);

        Assert.NotNull(running);
        Assert.Equal(CodingCampaignStatus.Running, running.Status);
        Assert.True(running.Iteration >= 1);
        Assert.Contains(agent.Prompts, prompt =>
            prompt.Contains("Validierungsfehler", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(agent.Prompts, prompt =>
            prompt.Contains("Wähle danach selbst", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkflowProgressRequiresChangedSourceOrTestLines()
    {
        Assert.True(CodingCampaignService.HasChangedCodeLines(
            "diff --git a/einstein_engine.py b/einstein_engine.py\n--- a/einstein_engine.py\n+++ b/einstein_engine.py\n@@ -1 +1 @@\n-old\n+new\n"));
        Assert.True(CodingCampaignService.HasChangedCodeLines(
            "diff --git a/tests/solver.test.ts b/tests/solver.test.ts\n--- a/tests/solver.test.ts\n+++ b/tests/solver.test.ts\n@@ -1,0 +1 @@\n+test('solver', verify);\n"));
        Assert.False(CodingCampaignService.HasChangedCodeLines(
            "diff --git a/visualizations/kerr.png b/visualizations/kerr.png\nBinary files differ\n"));
        Assert.False(CodingCampaignService.HasChangedCodeLines(
            "diff --git a/einstein_cases.json b/einstein_cases.json\n--- a/einstein_cases.json\n+++ b/einstein_cases.json\n@@ -1 +1 @@\n-old\n+new\n"));
        Assert.False(CodingCampaignService.HasChangedCodeLines(
            "diff --git a/solutions/kerr.md b/solutions/kerr.md\n--- a/solutions/kerr.md\n+++ b/solutions/kerr.md\n@@ -1,0 +1 @@\n+Kerr\n"));
    }

    [Fact]
    public async Task RunWithoutChangedCodeLinesIsHiddenAndRetriedWithAProgressCorrection()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "no-progress-workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.txt"), "ready");
        var chats = environment.Get<IChatRepository>();
        var workflows = environment.Get<ICodingCampaignRepository>();
        var session = await CreateCodingSessionAsync(chats, workspace, "Stillstandskorrektur");
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        var agent = new FakeCampaignAgent(chats) { RunsWithoutDiffRemaining = 1 };
        var definition = new FakeCampaignDefinition();
        using var service = CreateService(environment, chats, settings, agent, definition);

        await service.SelectAsync(session.Id, definition.Descriptor.Id);
        var campaign = (await workflows.GetForSessionAsync(session.Id))!;
        await service.RunAsync(session.Id);
        await agent.WaitForPromptAsync("keine hinzugefÃ¼gte oder entfernte Codezeile", TimeSpan.FromSeconds(5));
        await agent.WaitForRunsAsync(2, TimeSpan.FromSeconds(5));
        await service.StopAsync(session.Id);

        var iterations = await workflows.ListIterationsAsync(campaign.Id);
        Assert.Contains(iterations, static iteration => iteration.Status == "completed" && iteration.AssistantMessageId is null);
        Assert.Contains(agent.Prompts, prompt =>
            prompt.Contains("Verbindliche Korrektur wegen fehlenden Codefortschritts", StringComparison.Ordinal)
            && prompt.Contains("globale Dirty Worktree", StringComparison.Ordinal));
        var visibleReports = (await chats.ListMessagesAsync(session.Id))
            .Count(static message => message.Content.Contains("### Prozessbericht", StringComparison.Ordinal));
        Assert.True(visibleReports < agent.RunCount);
    }

    [Fact]
    public async Task SuccessfulWorkflowContinuesUntilAnExplicitStopAndNeverSelfTerminates()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "strict-endless-workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.txt"), "ready");
        var chats = environment.Get<IChatRepository>();
        var workflows = environment.Get<ICodingCampaignRepository>();
        var session = await CreateCodingSessionAsync(chats, workspace, "Deterministischer Dauerlauf");
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        var agent = new FakeCampaignAgent(chats);
        var definition = new FakeCampaignDefinition();
        using var service = CreateService(environment, chats, settings, agent, definition);

        await service.SelectAsync(session.Id, definition.Descriptor.Id);
        await service.RunAsync(session.Id);
        await agent.WaitForRunsAsync(8, TimeSpan.FromSeconds(8));

        var running = await workflows.GetForSessionAsync(session.Id);
        Assert.NotNull(running);
        Assert.True(service.IsRunning);
        Assert.Equal(CodingCampaignStatus.Running, running.Status);
        Assert.True(running.Iteration >= 7);
        Assert.All(agent.Prompts.Skip(1), prompt =>
            Assert.Contains("Eine erfolgreich verifizierte Lösung beendet den Workflow nicht", prompt, StringComparison.Ordinal));

        var processMessages = (await chats.ListMessagesAsync(session.Id))
            .Where(static message =>
                message.Role == ChatRole.Assistant
                && message.Status == MessageStatus.Completed
                && message.Content.Contains("### Prozessbericht", StringComparison.Ordinal))
            .ToArray();
        Assert.True(processMessages.Length >= 8);
        Assert.All(processMessages, message =>
        {
            Assert.Contains("**Gegenstand:**", message.Content, StringComparison.Ordinal);
            Assert.Contains("**Aktion:**", message.Content, StringComparison.Ordinal);
            Assert.Contains("**Annahmen:**", message.Content, StringComparison.Ordinal);
            Assert.Contains("**Annahmenänderung:**", message.Content, StringComparison.Ordinal);
            Assert.Contains("**Prüfung:**", message.Content, StringComparison.Ordinal);
        });
        Assert.Equal(processMessages.Length, processMessages.Select(static message => message.Id).Distinct().Count());

        await service.StopAsync(session.Id);
        var completedRuns = agent.RunCount;
        await Task.Delay(150);

        Assert.False(service.IsRunning);
        Assert.Equal(completedRuns, agent.RunCount);
        Assert.Equal(CodingCampaignStatus.Stopped, (await workflows.GetForSessionAsync(session.Id))!.Status);
    }

    [Fact]
    public async Task ValidationExceptionBecomesCorrectionAndDoesNotStopTheContinuousWorkflow()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "validation-exception-workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.txt"), "ready");
        var chats = environment.Get<IChatRepository>();
        var workflows = environment.Get<ICodingCampaignRepository>();
        var session = await CreateCodingSessionAsync(chats, workspace, "Robuste Abnahme");
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        var agent = new FakeCampaignAgent(chats);
        var definition = new FakeCampaignDefinition { ValidationExceptionsRemaining = 1 };
        using var service = CreateService(environment, chats, settings, agent, definition);

        await service.SelectAsync(session.Id, definition.Descriptor.Id);
        await service.RunAsync(session.Id);
        await agent.WaitForRunsAsync(3, TimeSpan.FromSeconds(5));
        var running = await workflows.GetForSessionAsync(session.Id);
        await service.StopAsync(session.Id);

        Assert.NotNull(running);
        Assert.Equal(CodingCampaignStatus.Running, running.Status);
        Assert.Contains(agent.Prompts, prompt =>
            prompt.Contains("Workflow-Abnahme", StringComparison.Ordinal)
            && prompt.Contains("simulierter Validatorfehler", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PromptConstructionFailureIsRecoveredByTheContinuousSupervisor()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "prompt-retry-workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.txt"), "ready");
        var chats = environment.Get<IChatRepository>();
        var workflows = environment.Get<ICodingCampaignRepository>();
        var session = await CreateCodingSessionAsync(chats, workspace, "Supervisor-Dauerlauf");
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        var agent = new FakeCampaignAgent(chats);
        var definition = new FakeCampaignDefinition { PromptExceptionsRemaining = 1 };
        using var service = CreateService(environment, chats, settings, agent, definition);

        await service.SelectAsync(session.Id, definition.Descriptor.Id);
        await service.RunAsync(session.Id, "Diese Anweisung muss trotz des Fehlers erhalten bleiben.");
        await agent.WaitForRunsAsync(2, TimeSpan.FromSeconds(8));
        var running = await workflows.GetForSessionAsync(session.Id);
        await service.StopAsync(session.Id);

        Assert.NotNull(running);
        Assert.Equal(CodingCampaignStatus.Running, running.Status);
        Assert.True(running.Iteration >= 1);
        Assert.Contains(agent.Prompts, prompt =>
            prompt.Contains("Diese Anweisung muss trotz des Fehlers erhalten bleiben.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UserStopCancelsTheContinuousSupervisorWhileItWaitsToRetry()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "retry-stop-workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.txt"), "ready");
        var chats = environment.Get<IChatRepository>();
        var workflows = environment.Get<ICodingCampaignRepository>();
        var session = await CreateCodingSessionAsync(chats, workspace, "Stoppbarer Supervisor");
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        var agent = new FakeCampaignAgent(chats);
        var definition = new FakeCampaignDefinition { PromptExceptionsRemaining = 100 };
        using var service = CreateService(environment, chats, settings, agent, definition);

        await service.SelectAsync(session.Id, definition.Descriptor.Id);
        await service.RunAsync(session.Id);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            while ((await workflows.GetForSessionAsync(session.Id, timeout.Token))?.RestartCount < 1)
            {
                await Task.Delay(20, timeout.Token);
            }
        }

        await service.StopAsync(session.Id);
        await Task.Delay(120);

        Assert.False(service.IsRunning);
        Assert.Equal(CodingCampaignStatus.Stopped, (await workflows.GetForSessionAsync(session.Id))!.Status);
        Assert.Equal(0, agent.AttemptCount);
    }

    [Fact]
    public async Task ExistingSolutionsAreImportedOnlyOnceWhenWorkflowIsLoaded()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "solution-workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.txt"), "ready");
        var solutions = Directory.CreateDirectory(Path.Combine(workspace, "solutions"));
        await File.WriteAllTextAsync(Path.Combine(solutions.FullName, "minkowski.md"), "# Minkowski\n\nVollständige verifizierte Lösung.");
        var chats = environment.Get<IChatRepository>();
        var session = await CreateCodingSessionAsync(chats, workspace, "Lösungs-Workflow");
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        var definition = new FakeCampaignDefinition();
        using var service = CreateService(environment, chats, settings, new FakeCampaignAgent(chats), definition);

        await service.SelectAsync(session.Id, definition.Descriptor.Id);
        await service.SelectAsync(session.Id, definition.Descriptor.Id);

        var messages = await chats.ListMessagesAsync(session.Id);
        var solution = Assert.Single(
            messages,
            static message => message.ToolExecution?.Tool == "coding.workflow.solution");
        Assert.Contains("Vorhandene Lösung", solution.Content, StringComparison.Ordinal);
        Assert.Contains("Minkowski", solution.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidationGatedSolutionsAreRevalidatedBeforeImport()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "gated-solution-workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "foundation.txt"), "ready");
        var solutions = Directory.CreateDirectory(Path.Combine(workspace, "solutions"));
        await File.WriteAllTextAsync(Path.Combine(solutions.FullName, "candidate.md"), "# Noch zu prüfen");
        var chats = environment.Get<IChatRepository>();
        var session = await CreateCodingSessionAsync(chats, workspace, "Validierungs-Workflow");
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        var definition = new FakeCampaignDefinition
        {
            PublishSolutionsOnlyAfterValidation = true,
            ValidationFailuresRemaining = 1,
        };
        using var service = CreateService(environment, chats, settings, new FakeCampaignAgent(chats), definition);

        await service.SelectAsync(session.Id, definition.Descriptor.Id);
        Assert.DoesNotContain(
            await chats.ListMessagesAsync(session.Id),
            static message => message.ToolExecution?.Tool == "coding.workflow.solution");

        await service.SelectAsync(session.Id, definition.Descriptor.Id);
        Assert.Single(
            await chats.ListMessagesAsync(session.Id),
            static message => message.ToolExecution?.Tool == "coding.workflow.solution");
    }

    [Fact]
    public async Task SolutionPdfExporterRendersEscapedKatexAndAtomicallyUpdatesExistingPdf()
    {
        var workspace = CreateTemporaryWorkspace();
        try
        {
            var source = Path.Combine(workspace, "tensor.md");
            await File.WriteAllTextAsync(
                source,
                """
                # Tensorrechnung

                $$
                G_{\\mu\\nu} = \\frac{2M}{r^3}
                $$
                """);
            using var exporter = new CodingSolutionPdfExporter(
                NullLogger<CodingSolutionPdfExporter>.Instance);

            var first = await exporter.EnsureCurrentAsync(source, sourceChanged: true);
            var second = await exporter.EnsureCurrentAsync(source, sourceChanged: true);

            Assert.Equal(first, second);
            Assert.NotNull(second);
            Assert.True(new FileInfo(second).Length >= 1024);
            using var document = PdfDocument.Open(second);
            var extracted = string.Concat(document.GetPages().Select(static page => page.Text));
            Assert.Contains("Tensorrechnung", extracted, StringComparison.Ordinal);
            Assert.DoesNotContain("frac", extracted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\mu", extracted, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static CodingCampaignService CreateService(
        TestEnvironment environment,
        IChatRepository chats,
        SettingsCoordinator settings,
        ICodingCampaignAgent agent,
        ICodingCampaignDefinition definition) => new(
            environment.Get<ICodingCampaignRepository>(),
            chats,
            environment.Get<IChatArtifactRepository>(),
            new CodingCampaignCatalog([definition]),
            agent,
            settings,
            NullLogger<CodingCampaignService>.Instance);

    private static async Task<ChatSession> CreateCodingSessionAsync(
        IChatRepository chats,
        string workspace,
        string title)
    {
        var session = await chats.CreateSessionAsync(title);
        await chats.SetAssistantContextAsync(session.Id, AssistantMode.Code, workspace, "fingerprint");
        await chats.SetPersistentToolActionAsync(session.Id, PersistentToolAction.Code);
        return (await chats.GetSessionAsync(session.Id))!;
    }

    private static string CreateTemporaryWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), "GO", "WorkflowTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static async Task<CodingCampaignState> WaitForWorkflowStatusAsync(
        ICodingCampaignRepository workflows,
        Guid sessionId,
        CodingCampaignStatus status)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var state = await workflows.GetForSessionAsync(sessionId, cancellation.Token);
            if (state?.Status == status) return state;
            await Task.Delay(20, cancellation.Token);
        }
    }

    private sealed class FakeCampaignAgent(IChatRepository chats) : ICodingCampaignAgent
    {
        private int _attemptCount;
        private int _failuresRemaining;
        private int _runsWithoutDiffRemaining;
        private int _runCount;
        private int _cancelCount;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _prompts = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<PromptTriggerAction?> _triggerActions = new();
        public bool IsRunning { get; private set; }
        public int AttemptCount => Volatile.Read(ref _attemptCount);
        public int RunCount => Volatile.Read(ref _runCount);
        public int CancelCount => Volatile.Read(ref _cancelCount);
        public IReadOnlyList<string> Prompts => _prompts.ToArray();
        public IReadOnlyList<PromptTriggerAction?> TriggerActions => _triggerActions.ToArray();
        public int FailuresRemaining
        {
            get => Volatile.Read(ref _failuresRemaining);
            init => _failuresRemaining = value;
        }
        public int RunsWithoutDiffRemaining
        {
            get => Volatile.Read(ref _runsWithoutDiffRemaining);
            init => _runsWithoutDiffRemaining = value;
        }

        public async Task<ChatMessage> SendAsync(
            Guid sessionId,
            string prompt,
            PromptTriggerMatch? trigger,
            Func<GoAiAssistantUpdate, Task> update,
            CancellationToken cancellationToken = default) =>
            await ExecuteAsync(sessionId, prompt, trigger, update, cancellationToken);

        public async Task<ChatMessage> SendWorkflowStepAsync(
            Guid sessionId,
            string prompt,
            PromptTriggerMatch? trigger,
            Func<GoAiAssistantUpdate, Task> update,
            CancellationToken cancellationToken = default) =>
            await ExecuteAsync(sessionId, prompt, trigger, update, cancellationToken);

        private async Task<ChatMessage> ExecuteAsync(
            Guid sessionId,
            string prompt,
            PromptTriggerMatch? trigger,
            Func<GoAiAssistantUpdate, Task> update,
            CancellationToken cancellationToken)
        {
            _ = update;
            _prompts.Enqueue(prompt);
            _triggerActions.Enqueue(trigger?.Trigger.Action);
            IsRunning = true;
            try
            {
                Interlocked.Increment(ref _attemptCount);
                await Task.Delay(35, cancellationToken);
                if (TryConsumeFailure())
                {
                    throw new InvalidOperationException("Simulierter Laufzeitfehler im Coding-Agenten.");
                }
                Interlocked.Increment(ref _runCount);
                var message = await chats.AddMessageAsync(
                    sessionId,
                    ChatRole.Assistant,
                    """
                    GO_SESSION_TITLE: Simulierter Workflow-Schritt

                    ### Prozessbericht
                    **Gegenstand:** Der aktuelle fachliche Workflow-Schritt.
                    **Aktion:** Die simulierte Iteration wurde abgeschlossen.
                    **Annahmen:** Die bisherige Testannahme bleibt für diesen Schritt gültig.
                    **Annahmenänderung:** Unverändert.
                    **Prüfung:** Der simulierte Agentenlauf wurde erfolgreich abgeschlossen.
                    """,
                    MessageStatus.Completed,
                    cancellationToken: cancellationToken);
                return TryConsumeNoDiff()
                    ? message
                    : message with
                    {
                        CodeDiff = "diff --git a/src/demo.py b/src/demo.py\n--- a/src/demo.py\n+++ b/src/demo.py\n@@ -1 +1 @@\n-alt\n+neu\n",
                    };
            }
            finally
            {
                IsRunning = false;
            }
        }

        private bool TryConsumeFailure()
        {
            while (true)
            {
                var current = Volatile.Read(ref _failuresRemaining);
                if (current <= 0) return false;
                if (Interlocked.CompareExchange(ref _failuresRemaining, current - 1, current) == current) return true;
            }
        }

        private bool TryConsumeNoDiff()
        {
            while (true)
            {
                var current = Volatile.Read(ref _runsWithoutDiffRemaining);
                if (current <= 0) return false;
                if (Interlocked.CompareExchange(ref _runsWithoutDiffRemaining, current - 1, current) == current) return true;
            }
        }

        public Task CancelCurrentAndWaitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _cancelCount);
            return Task.CompletedTask;
        }

        public async Task WaitForRunsAsync(int expected, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (RunCount < expected)
            {
                await Task.Delay(20, cancellation.Token);
            }
        }

        public async Task WaitForPromptAsync(string fragment, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (!_prompts.Any(prompt => prompt.Contains(fragment, StringComparison.Ordinal)))
            {
                await Task.Delay(20, cancellation.Token);
            }
        }
    }

    private sealed class FakeCampaignDefinition : ICodingCampaignDefinition
    {
        private int _validationFailuresRemaining;
        private int _validationExceptionsRemaining;
        private int _promptExceptionsRemaining;
        public CodingCampaignDescriptor Descriptor { get; } = new(
            "test-continuous", "Test-Dauerlauf", "Test", "Test", ["visualizations", "solutions"]);
        public bool PublishSolutionsOnlyAfterValidation { get; init; }
        public int ValidationFailuresRemaining
        {
            get => Volatile.Read(ref _validationFailuresRemaining);
            init => _validationFailuresRemaining = value;
        }
        public int ValidationExceptionsRemaining
        {
            get => Volatile.Read(ref _validationExceptionsRemaining);
            init => _validationExceptionsRemaining = value;
        }
        public int PromptExceptionsRemaining
        {
            get => Volatile.Read(ref _promptExceptionsRemaining);
            init => _promptExceptionsRemaining = value;
        }
        public bool HasFoundation(string workspacePath) => File.Exists(Path.Combine(workspacePath, "foundation.txt"));
        public int ReadIteration(string workspacePath) { _ = workspacePath; return 0; }
        public string GetChallenge(int iteration) => "Iteration " + iteration;
        public string BuildBootstrapPrompt() => "Bootstrap";
        public string BuildIterationPrompt(int iteration, string challenge)
        {
            while (true)
            {
                var current = Volatile.Read(ref _promptExceptionsRemaining);
                if (current <= 0) break;
                if (Interlocked.CompareExchange(ref _promptExceptionsRemaining, current - 1, current) == current)
                {
                    throw new InvalidDataException("Simulierter Fehler bei der Prompt-Erzeugung.");
                }
            }
            return $"Iteration {iteration}: {challenge}";
        }
        public string BuildCorrectionPrompt(int iteration, string challenge, IReadOnlyList<string> issues) =>
            $"Korrektur {iteration}: {challenge}: {string.Join(", ", issues)}";
        public Task<CodingCampaignValidationResult> ValidateAsync(
            string workspacePath,
            CancellationToken cancellationToken = default)
        {
            _ = workspacePath;
            cancellationToken.ThrowIfCancellationRequested();
            while (true)
            {
                var current = Volatile.Read(ref _validationExceptionsRemaining);
                if (current <= 0) break;
                if (Interlocked.CompareExchange(ref _validationExceptionsRemaining, current - 1, current) == current)
                {
                    throw new InvalidDataException("Simulierter Validatorfehler.");
                }
            }
            while (true)
            {
                var current = Volatile.Read(ref _validationFailuresRemaining);
                if (current <= 0) break;
                if (Interlocked.CompareExchange(ref _validationFailuresRemaining, current - 1, current) == current)
                {
                    return Task.FromResult(new CodingCampaignValidationResult(
                        false,
                        ["Validierungsfehler aus Testfixture."],
                        []));
                }
            }
            return Task.FromResult(CodingCampaignValidationResult.Success);
        }
    }
}
