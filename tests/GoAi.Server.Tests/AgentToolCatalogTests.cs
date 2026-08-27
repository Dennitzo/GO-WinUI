using GoAi.Contracts;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class AgentToolCatalogTests
{
    [Fact]
    public void CompactSelectorExposesNamesWithoutFullToolSchemas()
    {
        var catalog = new AgentToolCatalog();
        var available = catalog.GetAvailableTools(CreateRequest([]));

        var selector = AgentToolCatalog.CreateSelectorDefinition(available);

        Assert.Equal(AgentToolCatalog.SelectorToolName, selector.Name);
        var names = selector.Parameters.GetProperty("properties").GetProperty("name").GetProperty("enum");
        Assert.Equal(available.Count, names.GetArrayLength());
        Assert.DoesNotContain("maximumResults", selector.Parameters.GetRawText(), StringComparison.Ordinal);
        Assert.All(available, tool =>
        {
            Assert.Contains($"- {tool.Name}", selector.Description, StringComparison.Ordinal);
            Assert.DoesNotContain(tool.Description, selector.Description, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(": ", selector.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectorResolvesExactlyOneAvailableTool()
    {
        var catalog = new AgentToolCatalog();
        var available = catalog.GetAvailableTools(CreateRequest([]));
        var selection = JsonSerializer.SerializeToElement(new { name = "web.search" });

        var tool = catalog.ResolveSelection(selection, available);

        Assert.Equal("web.search", tool.Name);
        Assert.Throws<ArgumentException>(() => catalog.ResolveSelection(
            JsonSerializer.SerializeToElement(new { name = "web.search", extra = true }),
            available));
    }

    [Fact]
    public void ModelReceivesNamesFirstAndOnlyTheSelectedFullSchemaAfterward()
    {
        var catalog = new AgentToolCatalog();
        var available = catalog.GetAvailableTools(CreateRequest(["code"]));
        var selected = catalog.Resolve(ClientToolNames.FileSystemReadText, available);

        var firstStage = Assert.Single(RunProcessor.CreateModelToolDefinitions(available, selectedToolName: null));
        var secondStage = Assert.Single(RunProcessor.CreateModelToolDefinitions(available, selected.Name));

        Assert.Equal(AgentToolCatalog.SelectorToolName, firstStage.Name);
        Assert.DoesNotContain("startLine", firstStage.Parameters.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(selected.Name, secondStage.Name);
        Assert.Equal(selected.Description, secondStage.Description);
        Assert.Equal(selected.Schema.GetRawText(), secondStage.Parameters.GetRawText());
    }

    private static readonly string[] LeanMainArguments = ["Main.lean"];

    [Fact]
    public void ClientToolsAreOnlyAdvertisedForReportedCapabilities()
    {
        var catalog = new AgentToolCatalog();
        var withoutClient = catalog.GetAvailableTools(CreateRequest(null));
        var withCode = catalog.GetAvailableTools(CreateRequest(["code"]));

        Assert.DoesNotContain(withoutClient, static tool => !tool.ServerSide);
        Assert.Contains(withCode, static tool => tool.Name == ClientToolNames.FileSystemReadText);
        Assert.DoesNotContain(withCode, static tool => tool.Name == "fs.readMany");
        Assert.Contains(withCode, static tool => tool.Name == ClientToolNames.FileSystemReplaceText);
        Assert.Contains(withCode, static tool => tool.Name == ClientToolNames.ProcessRunPreset);
        Assert.Contains(withCode, static tool => tool.Name == ClientToolNames.LeanProof);
        Assert.DoesNotContain(withCode, static tool => tool.Name == ClientToolNames.BricsCadMove);

        var withDocuments = catalog.GetAvailableTools(CreateRequest(["documents"]));
        Assert.Contains(withDocuments, static tool => tool.Name == ClientToolNames.DocumentsList);
        Assert.Contains(withDocuments, static tool => tool.Name == ClientToolNames.DocumentsSearch);
        Assert.Contains(withDocuments, static tool => tool.Name == ClientToolNames.DocumentsReadPages);
        Assert.DoesNotContain(withDocuments, static tool => tool.Name == ClientToolNames.FileSystemWriteText);

        var withDocumentIo = catalog.GetAvailableTools(CreateRequest(["documentIo"]));
        Assert.Contains(withDocumentIo, static tool => tool.Name == ClientToolNames.DocumentRead);
        Assert.Contains(withDocumentIo, static tool => tool.Name == ClientToolNames.DocumentCreate);
        Assert.DoesNotContain(withDocumentIo, static tool => tool.Name == ClientToolNames.FileSystemWriteText);
    }

    [Fact]
    public void DocumentToolsUseBoundedSectionContracts()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest(["documentIo"]));
        var read = catalog.Resolve(ClientToolNames.DocumentRead, tools);
        var create = catalog.Resolve(ClientToolNames.DocumentCreate, tools);
        using var readWindow = JsonDocument.Parse("""{"scope":"session","mode":"read","reference":"00000000-0000-0000-0000-000000000001","startUnit":2,"maximumUnits":3,"maximumCharacters":12000}""");
        using var append = JsonDocument.Parse("""{"operation":"appendSection","reference":"00000000-0000-0000-0000-000000000001","format":"pdf","sectionId":"kapitel-2","heading":"Kapitel 2","content":"Text","expectedSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""");
        using var unbounded = JsonDocument.Parse("""{"scope":"session","mode":"read","reference":"00000000-0000-0000-0000-000000000001","maximumCharacters":40001}""");
        using var staleEdit = JsonDocument.Parse("""{"operation":"replaceSection","reference":"00000000-0000-0000-0000-000000000001","format":"pdf","sectionId":"kapitel-2","content":"Text"}""");

        catalog.Validate(read, readWindow.RootElement);
        catalog.Validate(create, append.RootElement);
        Assert.Throws<ArgumentException>(() => catalog.Validate(read, unbounded.RootElement));
        Assert.Throws<ArgumentException>(() => catalog.Validate(create, staleEdit.RootElement));
    }

    [Fact]
    public void LeanProofSchemaRequiresOperationSpecificFields()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest(["code"]));
        var lean = catalog.Resolve(ClientToolNames.LeanProof, tools);
        using var valid = JsonDocument.Parse("""{"operation":"verify","path":"proofs/Main.lean","theoremName":"Main.result","timeoutSeconds":120}""");
        using var missingTheorem = JsonDocument.Parse("""{"operation":"verify","path":"proofs/Main.lean"}""");
        using var freeShell = JsonDocument.Parse("""{"operation":"check","path":"proofs/Main.lean","command":"cmd.exe"}""");

        catalog.Validate(lean, valid.RootElement);
        Assert.Throws<ArgumentException>(() => catalog.Validate(lean, missingTheorem.RootElement));
        Assert.Throws<ArgumentException>(() => catalog.Validate(lean, freeShell.RootElement));
    }

    [Theory]
    [InlineData("lean")]
    [InlineData("lean.exe")]
    [InlineData("C:\\Tools\\lake.exe")]
    public void GenericProcessRunCannotBypassLeanProofContract(string executable)
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest(["code"]));
        var process = catalog.Resolve(ClientToolNames.ProcessRun, tools);
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            executable,
            arguments = LeanMainArguments,
            purpose = "test",
        }));

        var exception = Assert.Throws<ArgumentException>(() => catalog.Validate(process, arguments.RootElement));
        Assert.Contains(ClientToolNames.LeanProof, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceTextRequiresExactBlocksAndRejectsUnknownProperties()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest(["code"]));
        var replace = catalog.Resolve(ClientToolNames.FileSystemReplaceText, tools);
        using var valid = JsonDocument.Parse("""{"path":"ViewModels/ShellViewModel.cs","oldText":"public string Name","newText":"public string DisplayName","expectedContent":"public string Name","expectedContentMode":"fragment"}""");
        using var invalid = JsonDocument.Parse("""{"path":"ViewModels/ShellViewModel.cs","oldText":"","newText":"x","expectedContent":"","shell":true}""");

        catalog.Validate(replace, valid.RootElement);
        Assert.Throws<ArgumentException>(() => catalog.Validate(replace, invalid.RootElement));

        using var legacyReplaceAll = JsonDocument.Parse("""{"path":"ViewModels/ShellViewModel.cs","oldText":"Name","newText":"DisplayName","expectedContent":"Name","expectedContentMode":"fragment","replaceAll":true}""");
        Assert.Throws<ArgumentException>(() => catalog.Validate(replace, legacyReplaceAll.RootElement));
    }

    [Fact]
    public void MoveUsesOnlyPathsAndDoesNotRepeatTheWholeFileContent()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest(["code"]));
        var move = catalog.Resolve(ClientToolNames.FileSystemMove, tools);
        using var valid = JsonDocument.Parse("""{"source":"Physik.py","destination":"Archiv/Physik.py"}""");
        using var legacy = JsonDocument.Parse("""{"source":"Physik.py","destination":"Archiv/Physik.py","expectedContent":"very large source"}""");

        catalog.Validate(move, valid.RootElement);
        Assert.Throws<ArgumentException>(() => catalog.Validate(move, legacy.RootElement));
        Assert.DoesNotContain("expectedContent", move.Schema.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void CreateFileRequiresACompactCompleteToolArgument()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest(["code"]));
        var create = catalog.Resolve(ClientToolNames.FileSystemProposeCreate, tools);
        using var valid = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            path = "Physik.py",
            content = new string('x', 12_000),
        }));
        using var oversized = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            path = "Physik.py",
            content = new string('x', 12_001),
        }));

        catalog.Validate(create, valid.RootElement);
        Assert.Throws<ArgumentException>(() => catalog.Validate(create, oversized.RootElement));
        Assert.Equal(
            12_000,
            create.Schema.GetProperty("properties").GetProperty("content").GetProperty("maxLength").GetInt32());
    }

    [Fact]
    public void SourceToolsExposeTargetedReadAndExplicitMissingSearchSemantics()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest(["code"]));
        var read = catalog.Resolve(ClientToolNames.FileSystemReadText, tools);
        var search = catalog.Resolve(ClientToolNames.FileSystemSearch, tools);

        Assert.Equal(
            12_000,
            read.Schema.GetProperty("properties").GetProperty("maximumCharacters").GetProperty("maximum").GetInt32());
        Assert.Contains(ClientToolNames.FileSystemSearch, read.Description, StringComparison.Ordinal);
        Assert.Equal(
            100,
            search.Schema.GetProperty("properties").GetProperty("maximumResults").GetProperty("maximum").GetInt32());
        Assert.Contains("not_present", search.Description, StringComparison.Ordinal);
        Assert.Contains("wiederhole", search.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WebFetchExposesBoundedPhraseSearchInsteadOfWholePages()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest([]));
        var fetch = catalog.Resolve("web.fetch", tools);
        using var valid = JsonDocument.Parse("""
            {"url":"https://example.com/reference","query":"Newtons zweites Gesetz","maximumCharacters":8000}
            """);
        using var oversized = JsonDocument.Parse("""
            {"url":"https://example.com/reference","query":"Newtons zweites Gesetz","maximumCharacters":12001}
            """);

        catalog.Validate(fetch, valid.RootElement);
        Assert.Throws<ArgumentException>(() => catalog.Validate(fetch, oversized.RootElement));
        Assert.True(fetch.Schema.GetProperty("properties").TryGetProperty("query", out _));
        Assert.Equal(
            12_000,
            fetch.Schema.GetProperty("properties").GetProperty("maximumCharacters").GetProperty("maximum").GetInt32());
        Assert.Contains("Trefferfenster", fetch.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessRunAcceptsWorkspaceFrameworkSetupPurpose()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest(["code"]));
        var process = catalog.Resolve(ClientToolNames.ProcessRun, tools);
        using var setup = JsonDocument.Parse("""
            {
              "executable": "npm.cmd",
              "arguments": ["install"],
              "workingDirectory": ".",
              "purpose": "setup",
              "startMode": "wait"
            }
            """);

        catalog.Validate(process, setup.RootElement);
    }

    [Fact]
    public void ProcessPresetDoesNotExposePdfAsAModelTool()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest(["code"]));
        var preset = catalog.Resolve(ClientToolNames.ProcessRunPreset, tools);
        Assert.DoesNotContain("document.renderPdf", preset.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("document.renderPdf", preset.Schema.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Erzeuge eine PDF aus dem Lehrbuch.")]
    [InlineData("Aktualisiere die vorhandene pdf-Datei.")]
    [InlineData("Render both PDFs with KaTeX.")]
    [InlineData("Erzeuge eine PDF-Datei.")]
    public void PdfCodingPromptsDoNotAdvertiseAModelPdfTool(string prompt)
    {
        var catalog = new AgentToolCatalog();
        var request = CreateRequest(["code", "process", "pdf"]) with
        {
            Mode = RunMode.Code,
            Messages = [new RunMessage("user", [new ContentPart("text", prompt)])],
        };

        var tools = catalog.GetAvailableTools(request);

        Assert.DoesNotContain(tools, static tool => tool.Name == "document.renderPdf");
    }

    [Fact]
    public void DedicatedPdfToolDoesNotLeakIntoUnrelatedOrLegacyCodingRuns()
    {
        var catalog = new AgentToolCatalog();
        var unrelated = CreateRequest(["code", "process", "pdf"]) with
        {
            Mode = RunMode.Code,
            Messages =
            [
                new RunMessage("user", [new ContentPart("text", "Erzeuge zuerst eine PDF.")]),
                new RunMessage("assistant", [new ContentPart("text", "Erledigt.")]),
                new RunMessage(
                    "user",
                    [
                        new ContentPart("text", "Analysiere jetzt nur den C#-Parser."),
                        new ContentPart("text", "[GO_WORKSPACE]\nWorkspace: PDF-Projekt"),
                    ]),
            ],
        };
        var legacyClient = unrelated with
        {
            Messages = [new RunMessage("user", [new ContentPart("text", "Erzeuge eine PDF.")])],
            ClientCapabilities = ["code", "process"],
        };

        Assert.DoesNotContain(
            catalog.GetAvailableTools(unrelated),
            static tool => tool.Name == "document.renderPdf");
        Assert.DoesNotContain(
            catalog.GetAvailableTools(legacyClient),
            static tool => tool.Name == "document.renderPdf");
    }

    [Fact]
    public void PdfToolIsNotAvailableToCodingRuns()
    {
        var catalog = new AgentToolCatalog();
        var request = CreateRequest(["code", "process", "pdf"]) with
        {
            Mode = RunMode.Code,
            Messages = [new RunMessage("user", [new ContentPart("text", "Erzeuge eine PDF.")])],
        };
        Assert.Throws<InvalidOperationException>(() => catalog.Resolve(
            "document.renderPdf",
            catalog.GetAvailableTools(request)));
    }

    [Fact]
    public void UnknownPropertiesAndUnknownToolsAreRejected()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest(null));
        var search = catalog.Resolve("web.search", tools);
        using var arguments = JsonDocument.Parse("""{"query":"TGA","unexpected":true}""");

        Assert.Throws<ArgumentException>(() => catalog.Validate(search, arguments.RootElement));
        Assert.Throws<InvalidOperationException>(() => catalog.Resolve("shell.execute", tools));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    public void WorkspaceRootCanBeAddressedConsistently(string path)
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest(["code"]));
        var list = catalog.Resolve(ClientToolNames.FileSystemList, tools);
        var search = catalog.Resolve(ClientToolNames.FileSystemSearch, tools);
        using var listArguments = JsonDocument.Parse(JsonSerializer.Serialize(new { path }));
        using var searchArguments = JsonDocument.Parse(JsonSerializer.Serialize(new { path, query = "test" }));

        catalog.Validate(list, listArguments.RootElement);
        catalog.Validate(search, searchArguments.RootElement);
    }

    [Fact]
    public void MediaDetailWindowsAreStrictAndBounded()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest(null));
        var media = catalog.Resolve("media.analyze", tools);
        using var valid = JsonDocument.Parse("""{"uploadId":"upload-0123456789abcdef0123456789abcdef","detailWindows":[{"start":10,"end":20}]}""");
        using var invalid = JsonDocument.Parse("""{"uploadId":"upload-0123456789abcdef0123456789abcdef","detailWindows":[{"start":20,"end":10,"extra":true}]}""");

        catalog.Validate(media, valid.RootElement);
        Assert.Throws<ArgumentException>(() => catalog.Validate(media, invalid.RootElement));
    }

    [Fact]
    public void ExplicitServerToolAllowListPreventsInheritedImageGeneration()
    {
        var catalog = new AgentToolCatalog();
        var request = CreateRequest(null) with
        {
            AllowedServerTools = ["math.evaluate", "context.retrieve"],
        };

        var tools = catalog.GetAvailableTools(request);

        Assert.Contains(tools, static tool => tool.Name == "math.evaluate");
        Assert.DoesNotContain(tools, static tool => tool.Name == "image.generate");
        Assert.DoesNotContain(tools, static tool => tool.Name == "web.search");
    }

    [Fact]
    public void CodingRunsExcludeWebResearchFromTheNativeToolCatalog()
    {
        var catalog = new AgentToolCatalog();
        var request = CreateRequest(["code"]) with
        {
            Mode = RunMode.Code,
            AllowedServerTools = [],
        };

        var tools = catalog.GetAvailableTools(request);

        Assert.DoesNotContain(tools, static tool => tool.Name == "web.search");
        Assert.DoesNotContain(tools, static tool => tool.Name == "web.fetch");
        Assert.DoesNotContain(tools, static tool => tool.Name == "youtube.search");
        Assert.DoesNotContain(tools, static tool => tool.Name == "image.generate");
    }

    [Fact]
    public void CodingRunsAcceptExplicitStagedWebResearchTools()
    {
        var catalog = new AgentToolCatalog();
        var request = CreateRequest(["code"]) with
        {
            Mode = RunMode.Code,
            AllowedServerTools = ["web.search", "web.fetch", "math.evaluate"],
        };

        var tools = catalog.GetAvailableTools(request);

        Assert.Contains(tools, static tool => tool.Name == "web.search");
        Assert.Contains(tools, static tool => tool.Name == "web.fetch");
        Assert.True(StagedWebResearchPipeline.IsRequested(request, tools));
    }

    [Fact]
    public void NullServerToolAllowListRetainsProtocolCompatibility()
    {
        var tools = new AgentToolCatalog().GetAvailableTools(CreateRequest(null));
        Assert.Contains(tools, static tool => tool.Name == "image.generate");
    }

    [Fact]
    public void CodingContextPreparationAdvertisesNoTools()
    {
        var request = CreateRequest([]) with
        {
            Mode = RunMode.Code,
            AllowedServerTools = [],
            PreferredCodeModelId = GoAi.Server.Core.Configuration.CodingModelCatalog.GptOss120BId,
            ConversationProfile = ConversationProfile.ContextPreparation,
        };

        Assert.Empty(new AgentToolCatalog().GetAvailableTools(request));
    }

    private static RunRequest CreateRequest(IReadOnlyList<string>? capabilities) => new(
        GoAiProtocol.Version,
        RunMode.General,
        [new RunMessage("user", [new ContentPart("text", "Test")])],
        ClientCapabilities: capabilities);
}
