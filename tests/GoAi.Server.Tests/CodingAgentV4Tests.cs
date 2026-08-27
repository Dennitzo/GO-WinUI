using GoAi.Contracts;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class CodingAgentV4Tests
{
    [Fact]
    public void ResearchFetchSchemaContainsOnlyUrlAndOneQuery()
    {
        var tool = AvailableTools().Single(static item => item.Name == "web.fetch");

        var definition = CodingAgentV4Engine.CreateFetchDefinition(tool);
        var properties = definition.Parameters.GetProperty("properties")
            .EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();

        Assert.Equal(["url", "query"], properties);
        Assert.Equal(["url", "query"], definition.Parameters.GetProperty("required")
            .EnumerateArray().Select(static item => item.GetString()!).ToArray());
    }

    [Fact]
    public void MutationSchemaAndHostValidationEnforceEightThousandCharacters()
    {
        var tool = AvailableTools().Single(static item => item.Name == ClientToolNames.FileSystemProposeCreate);
        var definition = CodingAgentV4Engine.CreateMutationDefinition(tool);
        var maximum = definition.Parameters.GetProperty("properties")
            .GetProperty("content")
            .GetProperty("maxLength")
            .GetInt32();
        var valid = Call(ClientToolNames.FileSystemProposeCreate, new { path = "Physik.py", content = new string('x', 8_000) });
        var invalid = Call(ClientToolNames.FileSystemProposeCreate, new { path = "Physik.py", content = new string('x', 8_001) });

        Assert.Equal(8_000, maximum);
        Assert.True(CodingAgentV4Engine.MutationWithinLimit(valid));
        Assert.False(CodingAgentV4Engine.MutationWithinLimit(invalid));
    }

    [Fact]
    public void ProviderTextDropsIsolatedSurrogatesButPreservesValidPairs()
    {
        var malformed = "Mechanik \udc81 mit Emoji \ud83d\ude80 und Text";

        var normalized = CodingAgentV4Engine.NormalizeModelText(malformed);

        Assert.Equal("Mechanik  mit Emoji \ud83d\ude80 und Text", normalized);
        Assert.DoesNotContain('\udc81', normalized!);
    }

    [Fact]
    public void FollowingStepReceivesResearchBriefButNoRawWebPage()
    {
        var state = State() with
        {
            ResearchBrief = new ResearchBrief(
                "https://example.test/mechanik",
                "Klassische Mechanik",
                "Newtonsche Axiome und F gleich m mal a."),
            RecentResults = ["Recherchebrief erstellt."],
        };
        var workspace = new WorkspaceDescriptor("Leer", "(leer)", 0);

        var messages = CodingAgentV4Engine.BuildStepMessages(
            state,
            workspace,
            "Erstelle die Datei.",
            currentData: null);
        var serialized = string.Join("\n", messages.Select(static item => item.Content));

        Assert.Contains("Newtonsche Axiome", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("RAW_WEB_PAGE_DO_NOT_FORWARD", serialized, StringComparison.Ordinal);
        Assert.InRange(CodingContextPlanner.EstimateTokens(messages), 1, 12_000);
    }

    [Fact]
    public void FirstUntriedResearchPairIsSelectedChronologically()
    {
        var state = State() with
        {
            ResearchSources = ["https://one.test", "https://two.test"],
            ResearchPhrases = ["erste Phrase", "zweite Phrase"],
            AttemptedResearchKeys = ["https://one.test\nerste Phrase"],
        };

        var pair = CodingAgentV4Engine.NextResearchPair(state);

        Assert.NotNull(pair);
        Assert.Equal("https://one.test", pair.Value.Url);
        Assert.Equal("zweite Phrase", pair.Value.Query);
    }

    [Fact]
    public void DirectUrlPhrasePrecedesOverSpecificModelPhrases()
    {
        var phrases = CodingAgentV4Engine.ParseResearchPhrases(
            "Klassische Mechanik Formelsammlung\nWikibooks Physik Formeln\nPython Lehrbuch Mechanik",
            "Erstelle ein Lehrbuch.",
            "https://de.wikibooks.org/wiki/Formelsammlung_Physik:_Klassische_Mechanik");

        Assert.Equal("Klassische Mechanik", phrases[0]);
        Assert.Equal(3, phrases.Count);
    }

    [Fact]
    public void LeanVerificationUsesCheckAndNeverStatus()
    {
        var tools = AvailableTools();
        var verification = CodingAgentV4Engine.CreateVerificationCall(
            "proofs/result.lean",
            new WorkspaceDescriptor("Lean", "- proofs/result.lean", 1),
            tools,
            State().Step);

        Assert.NotNull(verification);
        Assert.Equal(ClientToolNames.LeanProof, verification.Value.Tool.Name);
        Assert.Equal("check", verification.Value.Call.Arguments.GetProperty("operation").GetString());
        Assert.NotEqual("status", verification.Value.Call.Arguments.GetProperty("operation").GetString());
    }

    [Fact]
    public void PythonVerificationUsesSystemLauncherAndPyCompile()
    {
        var verification = CodingAgentV4Engine.CreateVerificationCall(
            "Physik.py",
            new WorkspaceDescriptor("Leer", "- Physik.py", 1),
            AvailableTools(),
            State().Step);

        Assert.NotNull(verification);
        Assert.Equal(ClientToolNames.ProcessRun, verification.Value.Tool.Name);
        Assert.Equal("py", verification.Value.Call.Arguments.GetProperty("executable").GetString());
        Assert.Contains("py_compile", verification.Value.Call.Arguments.GetProperty("arguments")
            .EnumerateArray().Select(static item => item.GetString()));
    }

    [Fact]
    public void WebFetchChoosesDenseContentInsteadOfNavigationHit()
    {
        var content = """
            Navigation Menü Klassische Mechanik Login Cookie Datenschutz

            Klassische Mechanik beschreibt die Bewegung makroskopischer Körper. Das zweite Newtonsche Axiom lautet F = m · a und verknüpft Kraft, Masse und Beschleunigung. Die Einheit der Kraft ist Newton, also kg m s^-2. Für abgeschlossene Systeme bleibt außerdem die Gesamtenergie erhalten. Grenzfälle bei verschwindender Kraft führen zur gleichförmigen Bewegung und liefern einen direkten Konsistenztest der Gleichungen.
            """;
        var response = new WebFetchResponse(
            "https://example.test/mechanik",
            "text/html",
            content,
            true,
            DateTimeOffset.UtcNow,
            []);
        var arguments = JsonSerializer.SerializeToElement(new
        {
            url = response.Url,
            query = "Klassische Mechanik",
            maximumResults = 1,
            contextCharacters = 800,
            maximumCharacters = 2_000,
        });

        var result = AgentToolExecutor.CreateTargetedFetchResult(response, arguments);

        var match = Assert.Single(result.Matches);
        Assert.True(result.Found);
        Assert.Contains("F = m", match.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Login Cookie", match.Text, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(match.Text.Length, 120, 2_000);
    }

    [Theory]
    [InlineData("Navigation Menü Login Cookie Datenschutz", false)]
    [InlineData("Die klassische Mechanik beschreibt Bewegungen. Es gilt F = m mal a; Kraft, Masse und Beschleunigung werden dadurch quantitativ verknüpft. Die Einheit Newton entspricht Kilogramm Meter pro Sekunde zum Quadrat und Grenzfälle bestätigen die Gleichung.", true)]
    public void UsableResearchExcerptRejectsBoilerplate(string text, bool expected) =>
        Assert.Equal(expected, CodingAgentV4Engine.IsUsableResearchExcerpt(text));

    private static CodingAgentV4State State() => new(
        "Erstelle Physik.py.",
        string.Empty,
        new CodingStepSnapshot(
            "step-0001",
            1,
            CodingStepKind.Mutation,
            CodingStepStatus.Running,
            "Datei erstellen"),
        ["Physik.py"],
        [],
        [],
        [],
        MutationRequested: true,
        ResearchRequested: false,
        ResearchExplicitlyRequired: false,
        ResearchSources: [],
        ResearchPhrases: [],
        AttemptedResearchKeys: []);

    private static IReadOnlyList<AgentToolSpec> AvailableTools()
    {
        var catalog = new AgentToolCatalog();
        return catalog.GetAvailableTools(new RunRequest(
            GoAiProtocol.Version,
            RunMode.Code,
            [new RunMessage("user", [new ContentPart("text", "Erstelle Physik.py.")])],
            ClientCapabilities: ["code", "filesystem", "process"],
            AllowedServerTools: ["web.search", "web.fetch"],
            Workspace: new WorkspaceDescriptor("Leer", "(leer)", 0)));
    }

    private static LmToolCall Call(string name, object arguments) => new(
        "call-test",
        name,
        JsonSerializer.SerializeToElement(arguments));
}
