using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.Core.Coding;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class CodingStructuredToolStepTests
{
    [Fact]
    public async Task StructuredChronologySurvivesSnapshotsReplayAndOutOfOrderUpdates()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Chronology");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "First.\nSecond.", MessageStatus.Streaming);
        var now = DateTimeOffset.UtcNow;
        var first = new AssistantToolStep("z-first", "coding.read", "running", InputJson: "{\"path\":\"a.py\"}",
            Explanation: "Read the existing implementation.", ContentOffset: 6, StartedAt: now, UpdatedAt: now);
        await chats.SaveToolStepAsync(message.Id, first);
        var live = first with { OutputJson = "{\"stdout\":\"live output\"}", UpdatedAt = now.AddSeconds(1) };
        await chats.SaveToolStepAsync(message.Id, live);
        await chats.SaveToolStepAsync(message.Id, first with { Status = "failed", OutputJson = "{}" });
        await chats.SaveToolStepAsync(message.Id, new(first.Id, first.Tool, "running", "stale legacy snapshot"));
        var second = new AssistantToolStep("a-second", "coding.edit", "running", ContentOffset: 6,
            InputJson: "{\"path\":\"a.py\"}", StartedAt: now.AddSeconds(2), UpdatedAt: now.AddSeconds(2));
        await chats.SaveToolStepAsync(message.Id, second);
        var completed = live with { Status = "completed", CompletedAt = now.AddSeconds(3), UpdatedAt = now.AddSeconds(3) };
        await chats.SaveToolStepAsync(message.Id, completed);
        await chats.SaveToolStepAsync(message.Id, live with { UpdatedAt = now.AddSeconds(4) });

        var stored = Assert.Single((await environment.Get<IConversationSnapshotRepository>().GetAsync(session.Id))!.Messages);
        Assert.Equal(completed, stored.ToolSteps![0]);
        Assert.Equal(second, stored.ToolSteps[1]);
        var json = JsonSerializer.SerializeToElement(stored.ToolSteps[0], JsonSerializerOptions.Web);
        Assert.Equal(first.InputJson, json.GetProperty("inputJson").GetString());
        Assert.Equal(6, json.GetProperty("contentOffset").GetInt32());
        Assert.Equal(now, json.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(completed.OutputJson, (await chats.GetMessageAsync(message.Id))!.ToolSteps![0].OutputJson);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("interrupted")]
    [InlineData("failed")]
    public void InterruptedStructuredOutputRetainsInputAndActualPartialStreams(string status)
    {
        var now = DateTimeOffset.UtcNow;
        var step = new AssistantToolStep("cmd", "coding.command", "running", InputJson: "{\"executable\":\"python\"}",
            Explanation: "Run the test.", ContentOffset: 17, StartedAt: now, UpdatedAt: now);
        var stopped = GoAiAssistantService.CompleteOpenToolStep(step, status, new("out", "err", true, 250));
        Assert.Equal(step.InputJson, stopped.InputJson);
        Assert.Equal(step.ContentOffset, stopped.ContentOffset);
        Assert.Equal(step.StartedAt, stopped.StartedAt);
        Assert.NotNull(stopped.CompletedAt);
        Assert.True(stopped.UpdatedAt > step.UpdatedAt);
        using var output = JsonDocument.Parse(stopped.OutputJson!);
        Assert.Equal("out", output.RootElement.GetProperty("stdout").GetString());
        Assert.Equal("err", output.RootElement.GetProperty("stderr").GetString());
        Assert.Equal(status, output.RootElement.GetProperty("toolStatus").GetString());
        Assert.True(output.RootElement.GetProperty("partial").GetBoolean());
        Assert.False(output.RootElement.TryGetProperty("exitCode", out _));
    }

    [Fact]
    public void FullAppliedDiffSurvivesCompactModelReceiptWithoutReplacingGitStatusObject()
    {
        var diff = "--- a/a.py\n+++ b/a.py\n@@ -1 +1 @@\n-old\n+" + new string('x', 30_000);
        var applied = JsonSerializer.Serialize(new { phase = "applied", applied = true, diff, diffTruncated = false });
        var result = new ClientToolResult("edit", "completed", JsonSerializer.SerializeToElement(new
        {
            success = true, applied = true, phase = "applied", diff = "", diffTruncated = true, sha256 = "new-hash",
        }));
        using var output = JsonDocument.Parse(GoAiAssistantService.SerializeClientToolOutput(result, applied));
        Assert.Equal(diff, output.RootElement.GetProperty("diff").GetString());
        Assert.True(output.RootElement.GetProperty("applied").GetBoolean());
        Assert.False(output.RootElement.GetProperty("diffTruncated").GetBoolean());
        Assert.Equal("new-hash", output.RootElement.GetProperty("sha256").GetString());
        var git = result with { Result = JsonSerializer.SerializeToElement(new { status = new { exitCode = 0, stdout = " M a.py", stderr = "" } }) };
        using var gitOutput = JsonDocument.Parse(GoAiAssistantService.SerializeClientToolOutput(git));
        Assert.Equal(" M a.py", gitOutput.RootElement.GetProperty("status").GetProperty("stdout").GetString());
        Assert.Equal("completed", gitOutput.RootElement.GetProperty("toolStatus").GetString());
    }

    [Fact]
    public void AppliedDiffSurvivesFailedProgressPersistenceAndCompactReceipt()
    {
        var diff = "--- a/a.py\n+++ b/a.py\n@@ -1 +1 @@\n-old\n+" + new string('x', 30_000);
        var storedPreview = JsonSerializer.Serialize(new { phase = "preview", applied = false, diff, diffTruncated = false });
        var deliveredApplied = new CodingCommandProgress("", "", false, 1,
            JsonSerializer.Serialize(new { phase = "applied", applied = true, diff, diffTruncated = false, sha256 = "after-write" }));
        var result = new ClientToolResult("edit", "completed", JsonSerializer.SerializeToElement(new
        {
            success = true, applied = true, phase = "applied", diff = "", diffTruncated = true,
            sha256 = "after-write", progressError = "The intermediate database write failed.",
        }));
        using var output = JsonDocument.Parse(GoAiAssistantService.SerializeClientToolOutput(result, storedPreview, deliveredApplied));
        Assert.Equal(diff, output.RootElement.GetProperty("diff").GetString());
        Assert.False(output.RootElement.GetProperty("diffTruncated").GetBoolean());
        Assert.True(output.RootElement.GetProperty("applied").GetBoolean());
        Assert.Equal("applied", output.RootElement.GetProperty("phase").GetString());
        Assert.Equal("after-write", output.RootElement.GetProperty("sha256").GetString());
        Assert.Equal("The intermediate database write failed.", output.RootElement.GetProperty("progressError").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FinalGitReceiptReplacesTransientSubprocessOutput(bool success)
    {
        var live = new CodingCommandProgress("old partial diff", "fatal: unknown HEAD", false, 100,
            JsonSerializer.Serialize(new { phase = "git-diff", stdout = "old partial diff", stderr = "fatal: unknown HEAD",
                elapsedMilliseconds = 100, truncated = false, partial = true, diffTruncated = false }));
        var result = new ClientToolResult("git", success ? "completed" : "failed", JsonSerializer.SerializeToElement(new
        {
            success,
            status = new { exitCode = 0, stdout = "AM a.py", stderr = "" },
            diff = new { exitCode = success ? 0 : 7, stdout = "working diff", stderr = success ? "" : "actual final failure" },
            stagedDiff = new { exitCode = 0, stdout = "staged diff", stderr = "" },
        }));
        var json = GoAiAssistantService.SerializeClientToolOutput(result, finalProgress: live);
        using var output = JsonDocument.Parse(json);
        Assert.False(output.RootElement.TryGetProperty("stdout", out _));
        Assert.False(output.RootElement.TryGetProperty("stderr", out _));
        Assert.False(output.RootElement.TryGetProperty("phase", out _));
        Assert.False(output.RootElement.TryGetProperty("elapsedMilliseconds", out _));
        Assert.False(output.RootElement.TryGetProperty("diffTruncated", out _));
        Assert.Equal("AM a.py", output.RootElement.GetProperty("status").GetProperty("stdout").GetString());
        Assert.Equal("working diff", output.RootElement.GetProperty("diff").GetProperty("stdout").GetString());
        Assert.Equal("staged diff", output.RootElement.GetProperty("stagedDiff").GetProperty("stdout").GetString());
        Assert.Equal(success ? "" : "actual final failure", output.RootElement.GetProperty("diff").GetProperty("stderr").GetString());
        Assert.DoesNotContain("unknown HEAD", json, StringComparison.Ordinal);
        Assert.DoesNotContain("old partial diff", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedEditKeepsPreviewAsPreviewAndNeverInventsAppliedState()
    {
        var preview = JsonSerializer.Serialize(new { phase = "preview", applied = false, diff = "-old\n+new" });
        var result = new ClientToolResult("edit", "failed", JsonSerializer.SerializeToElement(new { success = false }), "sha_changed", "File changed.");
        using var output = JsonDocument.Parse(GoAiAssistantService.SerializeClientToolOutput(result, preview));
        Assert.Equal("preview", output.RootElement.GetProperty("phase").GetString());
        Assert.False(output.RootElement.GetProperty("applied").GetBoolean());
        Assert.Equal("sha_changed", output.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task SanitizedTextAndRebasedPositionsAreCommittedTogether()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Rebase");
        const string raw = "First.\r\nGO_SESSION_TITLE: Hidden\r\nSecond.\r\nDone.";
        var visible = GoAiAssistantService.NormalizeCodingNarration(raw);
        var firstOffset = GoAiAssistantService.RebaseToolContentOffset(raw, raw.IndexOf("Second.", StringComparison.Ordinal), visible);
        var secondOffset = GoAiAssistantService.RebaseToolContentOffset(raw, raw.IndexOf("Done.", StringComparison.Ordinal), visible);
        // Metadata removal trims the streaming prefix; its following separator remains
        // in the next narration segment, without moving text across a tool action.
        Assert.Equal("First.", visible[..firstOffset]);
        Assert.Equal("\nSecond.", visible[firstOffset..secondOffset]);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "old", MessageStatus.Streaming);
        var first = new AssistantToolStep("first", "coding.read", "completed", ContentOffset: firstOffset);
        var second = new AssistantToolStep("second", "coding.edit", "completed", ContentOffset: secondOffset);
        await chats.UpdateMessageWithToolStepsAsync(message.Id, visible, MessageStatus.Completed, [first, second]);
        var saved = (await chats.GetMessageAsync(message.Id))!;
        Assert.Equal(visible, saved.Content);
        Assert.Equal(firstOffset, saved.ToolSteps![0].ContentOffset);
        Assert.Equal(secondOffset, saved.ToolSteps[1].ContentOffset);
        Assert.DoesNotContain("Hidden", saved.Content, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => chats.UpdateMessageWithToolStepsAsync(message.Id, "bad", MessageStatus.Failed, [first, second]));
        Assert.Equal(saved.Content, (await chats.GetMessageAsync(message.Id))!.Content);
    }

    [Theory]
    [InlineData("😀 Read.\r\n", 9)]
    [InlineData("  Read.\nGO_SESSION_TITLE: Hidden\n", 5)]
    public void PositionsUseJavascriptUtf16UnitsAfterNormalization(string rawPrefix, int expected)
    {
        var raw = rawPrefix + "Next.";
        var visible = GoAiAssistantService.NormalizeCodingNarration(raw);
        Assert.Equal(expected, GoAiAssistantService.RebaseToolContentOffset(raw, rawPrefix.Length, visible));
    }

    [Fact]
    public void CodingCompletionPreservesVisibleNarrationAndSourcesWithoutEnvelopeReplacement()
    {
        const string text = "I checked the file.\n\n{\"message\":\"A quoted JSON example\"}\n\nVerwendete Dokumentbelege:\nsource";
        Assert.Equal(text, GoAiAssistantService.NormalizeCodingNarration(text));
    }
}
