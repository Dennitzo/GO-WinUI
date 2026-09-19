using System.Text.Json;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Coding;
using GoWinUI.Core.Models;

namespace GoWinUI.Tests;

public sealed class CodingToolStepTests
{
    private static readonly string[] PowerShellArguments = ["-NoProfile", "-File", "probe.ps1"];

    [Fact]
    public async Task LongRunningToolJournalPersistsBeyondPreviousLimitsAndReplaysWithoutDuplicates()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Long running journal");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Progress", MessageStatus.Streaming);
        var steps = Enumerable.Range(0, 1_000).Select(index => new AssistantToolStep(
            $"step-{index}", ClientToolNames.CodingRead, "completed", $"File {index}", ContentOffset: 0)).ToArray();
        await chats.UpdateMessageWithToolStepsAsync(message.Id, "Progress", MessageStatus.Streaming, steps);
        var next = new AssistantToolStep("step-1000", ClientToolNames.CodingRead, "running", "Next file", ContentOffset: 8);
        await chats.SaveToolStepAsync(message.Id, next);
        await chats.SaveToolStepAsync(message.Id, next with { Status = "completed" });
        await chats.SaveToolStepAsync(message.Id, next);
        var stored = await chats.GetMessageAsync(message.Id);
        Assert.Equal(1_001, stored!.ToolSteps!.Count);
        Assert.Equal("step-0", stored.ToolSteps[0].Id);
        Assert.Equal("completed", stored.ToolSteps[^1].Status);
        var snapshot = await environment.Get<IConversationSnapshotRepository>().GetAsync(session.Id);
        Assert.Equal(1_001, Assert.Single(snapshot!.Messages).ToolSteps!.Count);
    }
    [Fact]
    public void RunningTerminalShowsBothStreamsWithoutClaimingAnExitCode()
    {
        var detail = GoAiAssistantService.FormatCommandProgressDetail(new CodingCommandProgress("out-start" + new string('x', 8_000) + "out-end",
            "err-start" + new string('x', 8_000) + "err-end", true, 1_250));
        Assert.Contains("Prozess läuft", detail, StringComparison.Ordinal);
        Assert.Contains("1250 ms", detail, StringComparison.Ordinal);
        Assert.Contains("out-start", detail, StringComparison.Ordinal);
        Assert.Contains("out-end", detail, StringComparison.Ordinal);
        Assert.Contains("err-start", detail, StringComparison.Ordinal);
        Assert.Contains("err-end", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Exit-Code", detail, StringComparison.Ordinal);
        Assert.Contains(new string('x', 8_000), detail, StringComparison.Ordinal);
        Assert.True(detail.Length > 16_000);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("failed")]
    public async Task InterruptedCommandPersistsLastActualStreamsWithoutInventingProcessCompletion(string status)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Partial terminal output");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "", MessageStatus.Streaming);
        var running = new AssistantToolStep("command", ClientToolNames.CodingCommand, "running", "Prozessaufruf: powershell.exe probe.ps1");
        await chats.SaveToolStepAsync(message.Id, running);
        var partial = new CodingCommandProgress("EARLY-OUT\n" + new string('x', 8_000) + "\nLAST-OUT",
            "EARLY-ERR\n" + new string('y', 8_000) + "\nLAST-ERR", true, 1_250);
        var terminal = GoAiAssistantService.CompleteOpenToolStep(running, status, partial);
        await chats.SaveToolStepAsync(message.Id, terminal);
        // Replayed start messages cannot remove the persisted partial terminal output.
        await chats.SaveToolStepAsync(message.Id, running);
        var snapshot = await environment.Get<IConversationSnapshotRepository>().GetAsync(session.Id);
        var stored = Assert.Single(Assert.Single(snapshot!.Messages).ToolSteps!);
        Assert.Equal(status, stored.Status);
        Assert.Contains("powershell.exe probe.ps1", stored.Detail!, StringComparison.Ordinal);
        Assert.Contains("EARLY-OUT", stored.Detail!, StringComparison.Ordinal);
        Assert.Contains("LAST-OUT", stored.Detail!, StringComparison.Ordinal);
        Assert.Contains("EARLY-ERR", stored.Detail!, StringComparison.Ordinal);
        Assert.Contains("LAST-ERR", stored.Detail!, StringComparison.Ordinal);
        Assert.Contains("kein abschließender Prozessstatus", stored.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain("Exit-Code", stored.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain("Prozess läuft", stored.Detail!, StringComparison.Ordinal);
        Assert.Contains(new string('x', 8_000), stored.Detail!, StringComparison.Ordinal);
        Assert.Contains(new string('y', 8_000), stored.Detail!, StringComparison.Ordinal);
        Assert.InRange(stored.Detail!.Length, 16_000, AssistantToolStep.MaximumDetailCharacters);
    }

    [Fact]
    public void CallbackFailureReceiptRetainsAllDeliveredPartialStreamsAndDiagnostic()
    {
        var failure = new ClientToolResult("command", "failed", JsonSerializer.SerializeToElement(new { failed = true }),
            "client.tool_failed", "Callback failure: " + new string('x', 9_000) + " CALLBACK-DIAGNOSTIC-TAIL");
        var partial = new CodingCommandProgress("OUT-HEAD" + new string('x', 8_000) + "OUT-TAIL",
            "ERR-HEAD" + new string('x', 8_000) + "ERR-TAIL", true, 1_500);
        var detail = GoAiAssistantService.FormatClientToolResultDetail(failure, partial);
        Assert.Contains("client.tool_failed", detail, StringComparison.Ordinal);
        Assert.Contains("OUT-HEAD", detail, StringComparison.Ordinal);
        Assert.Contains("OUT-TAIL", detail, StringComparison.Ordinal);
        Assert.Contains("ERR-HEAD", detail, StringComparison.Ordinal);
        Assert.Contains("ERR-TAIL", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Exit-Code", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Prozess läuft", detail, StringComparison.Ordinal);
        Assert.Contains("CALLBACK-DIAGNOSTIC-TAIL", detail, StringComparison.Ordinal);
        Assert.Contains(new string('x', 9_000), detail, StringComparison.Ordinal);
        Assert.InRange(detail.Length, 25_000, AssistantToolStep.MaximumDetailCharacters);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void FinalCommandResultReplacesPartialStreamsInsteadOfAppendingThemAgain(int exitCode)
    {
        var result = new ClientToolResult("command", exitCode == 0 ? "completed" : "failed", JsonSerializer.SerializeToElement(new
        {
            success = exitCode == 0, exitCode, stdout = "FINAL-OUT", stderr = "FINAL-ERR", elapsedMilliseconds = 2_000,
        }));
        var detail = GoAiAssistantService.FormatClientToolResultDetail(result, new("STALE-PARTIAL-OUT", "STALE-PARTIAL-ERR", false, 500));
        Assert.Contains($"Exit-Code: **{exitCode}**", detail, StringComparison.Ordinal);
        Assert.Contains("FINAL-OUT", detail, StringComparison.Ordinal);
        Assert.Contains("FINAL-ERR", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("STALE-PARTIAL", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("kein abschließender Prozessstatus", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchedEditShowsAllReplacementPairs()
    {
        var proposal = new ToolProposal("edit", "run", ClientToolNames.CodingEdit,
            JsonSerializer.SerializeToElement(new { path = "a.txt", edits = new[] {
                new { oldText = "first-old", newText = "first-new" }, new { oldText = "second-old", newText = "second-new" } } }),
            ToolRiskClass.LocalMutation, "Ändern", DateTimeOffset.UtcNow.AddMinutes(1));
        var detail = GoAiAssistantService.FormatToolInputDetail(proposal);
        Assert.Contains("-first-old", detail, StringComparison.Ordinal);
        Assert.Contains("+first-new", detail, StringComparison.Ordinal);
        Assert.Contains("-second-old", detail, StringComparison.Ordinal);
        Assert.Contains("+second-new", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteAndBatchEditKeepPathAndHashMetadataWithoutDuplicatingReplacementText(bool batch)
    {
        var hash = new string('a', 64);
        const string content = "UNIQUE-REPLACEMENT-CONTENT";
        var arguments = new Dictionary<string, object>
        {
            ["path"] = "nested/project.txt",
            ["expectedSha256"] = hash,
        };
        if (batch) arguments["edits"] = new[] { new { oldText = "OLD-CONTENT", newText = content } };
        else arguments["content"] = content;
        var proposal = new ToolProposal("metadata", "run", batch ? ClientToolNames.CodingEdit : ClientToolNames.CodingWrite,
            JsonSerializer.SerializeToElement(arguments), ToolRiskClass.LocalMutation, "Ändern", DateTimeOffset.UtcNow.AddMinutes(1));
        var detail = GoAiAssistantService.FormatToolInputDetail(proposal);
        Assert.Contains("\"path\": \"nested/project.txt\"", detail, StringComparison.Ordinal);
        Assert.Contains("\"expectedSha256\": \"" + hash + "\"", detail, StringComparison.Ordinal);
        Assert.Contains("+" + content, detail, StringComparison.Ordinal);
        Assert.Equal(detail.IndexOf(content, StringComparison.Ordinal), detail.LastIndexOf(content, StringComparison.Ordinal));
        if (batch) Assert.Contains("-OLD-CONTENT", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StepsPersistInTheAtomicConversationSnapshotWithoutChangingMessageContent()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Coding steps");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Antwort", MessageStatus.Streaming);
        var started = new AssistantToolStep("edit-1", "coding.edit", "running", "Vorgeschlagene Änderung");
        Assert.Single(await chats.SaveToolStepAsync(message.Id, started));
        var completed = started with { Status = "completed", Detail = "```diff\n-old\n+new\n```" };
        Assert.Single(await chats.SaveToolStepAsync(message.Id, completed));
        // A reconnected stream can replay its start event. It cannot regress a terminal operation.
        Assert.Equal(completed, Assert.Single(await chats.SaveToolStepAsync(message.Id, started)));
        var research = new AssistantToolStep("research-2", "web.deepResearch", "completed", "Quellen geprüft");
        Assert.Equal(2, (await chats.SaveToolStepAsync(message.Id, research)).Count);
        var snapshot = await environment.Get<IConversationSnapshotRepository>().GetAsync(session.Id);
        var stored = Assert.Single(snapshot!.Messages);
        Assert.Equal("Antwort", stored.Content);
        Assert.Equal(completed, stored.ToolSteps![0]);
        Assert.Equal(research, stored.ToolSteps[1]);
        Assert.True(stored.Revision > message.Revision);
        Assert.Equal(stored.ToolSteps, (await chats.GetMessageAsync(message.Id))!.ToolSteps);
        var other = await chats.CreateSessionAsync("Other");
        Assert.Empty((await environment.Get<IConversationSnapshotRepository>().GetAsync(other.Id))!.Messages);
    }

    [Fact]
    public async Task MissingMessagesAndOversizedStepPayloadsCannotBePersisted()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => chats.SaveToolStepAsync(Guid.NewGuid(),
            new AssistantToolStep("step", "coding.read", "running")));
        await Assert.ThrowsAsync<ArgumentException>(() => chats.SaveToolStepAsync(Guid.NewGuid(),
            new AssistantToolStep("step", "coding.read", "completed", new string('x', AssistantToolStep.MaximumDetailCharacters + 1))));
    }

    [Fact]
    public void GeneralAndCodingAutomaticallyOfferResearchTools()
    {
        var coding = GoAiAssistantService.GetAllowedServerTools(PromptTriggerAction.Coding);
        Assert.Contains("web.search", coding);
        Assert.Contains("web.fetch", coding);
        Assert.Contains("web.deepResearch", coding);
        Assert.Contains("web.deepResearch", GoAiAssistantService.GetAllowedServerTools(null));
    }

    [Fact]
    public void TerminalDetailsKeepArgumentsExitCodeAndBothOutputTailsVisible()
    {
        var proposal = new ToolProposal("command", "run", ClientToolNames.CodingCommand,
            JsonSerializer.SerializeToElement(new { executable = "powershell.exe", arguments = PowerShellArguments }),
            ToolRiskClass.Process, "Prüfen", DateTimeOffset.UtcNow.AddMinutes(1));
        var input = GoAiAssistantService.FormatToolInputDetail(proposal);
        Assert.Contains("powershell.exe", input, StringComparison.Ordinal);
        Assert.Contains("probe.ps1", input, StringComparison.Ordinal);
        Assert.Contains("workingDirectory", input, StringComparison.Ordinal);
        var result = GoAiAssistantService.FormatToolResultDetail(JsonSerializer.SerializeToElement(new
        {
            success = false, exitCode = 7, timedOut = false, elapsedMilliseconds = 123, truncated = true,
            stdout = "BEGIN-OUT\n" + new string('x', 7_000) + "\nEND-OUT", stderr = "BEGIN-ERR\n" + new string('y', 5_000) + "\nEND-ERR",
        }));
        Assert.Contains("Exit-Code: **7**", result, StringComparison.Ordinal);
        Assert.Contains("123 ms", result, StringComparison.Ordinal);
        Assert.Contains("BEGIN-OUT", result, StringComparison.Ordinal);
        Assert.Contains("END-OUT", result, StringComparison.Ordinal);
        Assert.Contains("BEGIN-ERR", result, StringComparison.Ordinal);
        Assert.Contains("END-ERR", result, StringComparison.Ordinal);
        Assert.Contains(new string('x', 7_000), result, StringComparison.Ordinal);
        Assert.Contains(new string('y', 5_000), result, StringComparison.Ordinal);
        Assert.InRange(input.Length + result.Length, 12_000, AssistantToolStep.MaximumDetailCharacters);
    }

    [Fact]
    public void FailedToolResultsRetainTheirDiagnosticAndActualOutcome()
    {
        var stale = new ClientToolResult("p", "failed", JsonSerializer.SerializeToElement(new { failed = true }),
            "coding.hash_conflict", "Datei wurde zwischenzeitlich geändert. Erneut lesen.");
        var detail = GoAiAssistantService.FormatClientToolResultDetail(stale);
        Assert.Contains("coding.hash_conflict", detail, StringComparison.Ordinal);
        Assert.Contains("Erneut lesen", detail, StringComparison.Ordinal);
        Assert.Equal("failed", GoAiAssistantService.GetToolResultStatus(stale));
        Assert.Equal("denied", GoAiAssistantService.GetToolResultStatus(stale with { Status = "rejected" }));
        Assert.Equal("failed", GoAiAssistantService.GetToolResultStatus(stale with
        {
            Status = "completed", Result = JsonSerializer.SerializeToElement(new { success = false, exitCode = 1 }),
        }));
    }

    [Fact]
    public void EditingAndGitOutputProduceBoundedRealDiffsForTheActivityView()
    {
        var proposal = new ToolProposal("p", "r", ClientToolNames.CodingEdit,
            JsonSerializer.SerializeToElement(new { path = "calc.py", oldText = "return a - b", newText = "return a + b" }),
            ToolRiskClass.LocalMutation, "Ändern", DateTimeOffset.UtcNow.AddMinutes(1));
        var input = GoAiAssistantService.FormatToolInputDetail(proposal);
        Assert.Contains("```diff", input, StringComparison.Ordinal);
        Assert.Contains("-return a - b", input, StringComparison.Ordinal);
        Assert.Contains("+return a + b", input, StringComparison.Ordinal);
        Assert.Contains("Vorgeschlagene", input, StringComparison.Ordinal);
        var result = GoAiAssistantService.FormatToolResultDetail(JsonSerializer.SerializeToElement(new
        {
            diff = new { stdout = "+working\n" }, stagedDiff = new { stdout = "+staged\n" },
        }));
        Assert.Contains("+working", result, StringComparison.Ordinal);
        Assert.Contains("+staged", result, StringComparison.Ordinal);
        var unstaged = GoAiAssistantService.FormatToolResultDetail(JsonSerializer.SerializeToElement(new
        {
            diff = new { stdout = "+working\n" }, stagedDiff = (object?)null,
        }));
        Assert.Contains("+working", unstaged, StringComparison.Ordinal);
        var write = proposal with { Name = ClientToolNames.CodingWrite,
            Arguments = JsonSerializer.SerializeToElement(new { path = "new.py", content = "print(42)" }) };
        Assert.Contains("+print(42)", GoAiAssistantService.FormatToolInputDetail(write), StringComparison.Ordinal);
        var largeResult = GoAiAssistantService.FormatToolResultDetail(JsonSerializer.SerializeToElement(new { text = new string('x', 50_000) }));
        Assert.Contains(new string('x', 50_000), largeResult, StringComparison.Ordinal);
        Assert.InRange(largeResult.Length, 50_000, AssistantToolStep.MaximumDetailCharacters);
    }

    [Fact]
    public async Task FullEditDetailsOverTwelveThousandCharactersSurviveDatabaseAndSnapshot()
    {
        var oldText = "OLD-BEGIN\n" + new string('o', 450) + "\n"
            + string.Join('\n', Enumerable.Range(1, 140).Select(index => $"old-{index:D3} " + new string('o', 40))) + "\nOLD-END";
        var newText = "NEW-BEGIN\n" + new string('n', 450) + "\n"
            + string.Join('\n', Enumerable.Range(1, 140).Select(index => $"new-{index:D3} " + new string('n', 40))) + "\nNEW-END";
        var hash = new string('a', 64);
        var proposal = new ToolProposal("full-edit", "run", ClientToolNames.CodingEdit,
            JsonSerializer.SerializeToElement(new { path = "large.txt", oldText, newText, expectedSha256 = hash }),
            ToolRiskClass.LocalMutation, "Ändern", DateTimeOffset.UtcNow.AddMinutes(1));
        var detail = GoAiAssistantService.FormatToolInputDetail(proposal);
        Assert.Contains("-old-140", detail, StringComparison.Ordinal);
        Assert.Contains("+new-140", detail, StringComparison.Ordinal);
        Assert.Contains("-" + new string('o', 450), detail, StringComparison.Ordinal);
        Assert.Contains("+" + new string('n', 450), detail, StringComparison.Ordinal);
        Assert.Contains("OLD-END", detail, StringComparison.Ordinal);
        Assert.Contains("NEW-END", detail, StringComparison.Ordinal);
        Assert.Contains(hash, detail, StringComparison.Ordinal);
        Assert.True(detail.Length > 12_000);
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Full tool output");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Ergebnis", MessageStatus.Completed);
        await chats.SaveToolStepAsync(message.Id, new("full-edit", ClientToolNames.CodingEdit, "completed", detail));
        var snapshot = await environment.Get<IConversationSnapshotRepository>().GetAsync(session.Id);
        Assert.Equal(detail, Assert.Single(Assert.Single(snapshot!.Messages).ToolSteps!).Detail);
    }

    [Fact]
    public void LargestValidCommandArgumentSetFitsDisplayLimitWithUnicodeEscaping()
    {
        var arguments = Enumerable.Repeat(new string('\u0001', 4_000), 64).ToArray();
        var proposal = new ToolProposal("full-command", "run", ClientToolNames.CodingCommand,
            JsonSerializer.SerializeToElement(new { executable = "powershell.exe", arguments, workingDirectory = ".", timeoutSeconds = 120 }),
            ToolRiskClass.Process, "Prüfen", DateTimeOffset.UtcNow.AddMinutes(1));
        var detail = GoAiAssistantService.FormatToolInputDetail(proposal);
        Assert.Contains("timeoutSeconds", detail, StringComparison.Ordinal);
        Assert.InRange(detail.Length, 1_500_000, AssistantToolStep.MaximumDetailCharacters);
        var json = detail[(detail.IndexOf('\n', detail.IndexOf("```json", StringComparison.Ordinal)) + 1)..detail.LastIndexOf("\n```", StringComparison.Ordinal)];
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(64, parsed.RootElement.GetProperty("arguments").GetArrayLength());
        Assert.All(parsed.RootElement.GetProperty("arguments").EnumerateArray(), argument => Assert.Equal(new string('\u0001', 4_000), argument.GetString()));
    }

    [Fact]
    public void GitDetailsKeepFullDiffsStatusAndFailureDiagnostics()
    {
        var raw = JsonSerializer.SerializeToElement(new
        {
            success = false, status = new { stdout = "?? untracked.txt", stderr = "STATUS_DIAGNOSTIC", exitCode = 0 },
            diff = new { stdout = "+BEGIN\n" + new string('d', 6_000) + "\n+END", stderr = "DIFF_FAILURE", exitCode = 7 },
            stagedDiff = new { stdout = "+STAGED", stderr = "STAGED_DIAGNOSTIC", exitCode = 0 }, note = "UNTRACKED_CONTENT_NOT_INCLUDED",
        });
        var detail = GoAiAssistantService.FormatToolResultDetail(raw);
        Assert.Contains(new string('d', 6_000), detail, StringComparison.Ordinal);
        Assert.Contains("+END", detail, StringComparison.Ordinal);
        Assert.Contains("+STAGED", detail, StringComparison.Ordinal);
        Assert.Contains("untracked.txt", detail, StringComparison.Ordinal);
        Assert.Contains("STATUS_DIAGNOSTIC", detail, StringComparison.Ordinal);
        Assert.Contains("DIFF_FAILURE", detail, StringComparison.Ordinal);
        Assert.Contains("STAGED_DIAGNOSTIC", detail, StringComparison.Ordinal);
        Assert.Contains("UNTRACKED_CONTENT_NOT_INCLUDED", detail, StringComparison.Ordinal);
        Assert.Contains("Exit-Code: **7**", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RealOutputWithMarkdownFencesAndHtmlStaysInsideALongerLiteralCodeBlock()
    {
        const string stdout = "before\n```python\nprint('<br>')\n```\n````\nafter";
        var detail = GoAiAssistantService.FormatCommandProgressDetail(new(stdout, "", false, 100));
        Assert.Contains("`````text\n" + stdout + "\n`````", detail, StringComparison.Ordinal);
        Assert.Contains("<br>", detail, StringComparison.Ordinal);
    }
}
