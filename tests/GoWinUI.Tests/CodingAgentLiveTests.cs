using GoAi.Client;
using GoAi.Contracts;
using GoWinUI.Core.Coding;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace GoWinUI.Tests;

/// <summary>Opt-in acceptance against the Docker gateway, real native llama model, and GO Windows client tools.</summary>
public sealed class CodingAgentLiveTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions ProtocolJson = GoAiProtocol.CreateJsonOptions();
    private static readonly string[] UnitTestArguments = ["-m", "unittest", "-v"];

    [Fact]
    [Trait("Category", "Live")]
    public async Task LocalModelFixesPythonProjectRunsTestsAndStreamsProgress()
    {
        if (Environment.GetEnvironmentVariable("GO_AI_CODING_LIVE") != "1") return;
        var model = Environment.GetEnvironmentVariable("GO_AI_LIVE_CODING_MODEL");
        Assert.False(string.IsNullOrWhiteSpace(model), "GO_AI_LIVE_CODING_MODEL must select an installed coding/ model.");
        var python = Environment.GetEnvironmentVariable("GO_AI_LIVE_PYTHON") ?? "python";
        var root = Path.Combine(Path.GetTempPath(), "go-coding-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executor = new LocalCodingToolExecutor(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        using var http = new HttpClient
        {
            BaseAddress = new Uri((Environment.GetEnvironmentVariable("GO_AI_SERVER_URL") ?? "http://127.0.0.1:8080").TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var client = new GoAiClient(http, "go-coding-live-" + Guid.NewGuid().ToString("N"));
        string? runId = null;
        var terminal = false;
        var calls = new List<string>();
        var progress = new List<string>();
        var deltas = new StringBuilder();
        var submitted = new HashSet<string>(StringComparer.Ordinal);
        RunFailedEvent? failure = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "calculator.py"), "def add(a, b):\n    return a - b\n", timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(root, "test_calculator.py"), """
                import unittest
                from calculator import add

                class AddTests(unittest.TestCase):
                    def test_positive(self):
                        self.assertEqual(add(2, 3), 5)
                    def test_negative(self):
                        self.assertEqual(add(-2, -3), -5)
                    def test_zero(self):
                        self.assertEqual(add(4, 0), 4)

                if __name__ == '__main__':
                    unittest.main()
                """, timeout.Token);
            var baseline = await executor.ExecuteAsync(ClientToolNames.CodingCommand,
                JsonSerializer.SerializeToElement(new { executable = python, arguments = UnitTestArguments, timeoutSeconds = 30 }), timeout.Token);
            Assert.False(baseline.GetProperty("success").GetBoolean(), "The buggy fixture must fail before the agent changes it.");
            var accepted = await client.CreateRunAsync(new RunRequest(GoAiProtocol.Version, RunMode.Coding,
                [new RunMessage("user", [new ContentPart("text",
                    "Repariere die Funktion add in calculator.py. Sie muss zwei Zahlen addieren. "
                    + "Lies calculator.py und test_calculator.py mit coding.read. Ändere ausschließlich calculator.py "
                    + "mit coding.edit und dem gelesenen sha256. Führe danach coding.command mit executable="
                    + JsonSerializer.Serialize(python) + " und arguments=[\"-m\",\"unittest\",\"-v\"] aus. "
                    + "Die Tests dürfen nicht verändert werden. Berichte das tatsächliche Testergebnis kurz. "
                    + "Alle Änderungen und der genannte Testaufruf sind für diesen temporären Testordner autorisiert.")])],
                ClientCapabilities: ["coding"], Limits: new RunLimits(4_096, 32_768, 720),
                AllowedServerTools: [], PreferredCodingModelId: model), "go-coding-live-" + Guid.NewGuid().ToString("N"), timeout.Token);
            runId = accepted.RunId;
            output.WriteLine($"Coding run={runId}; model={model}; workspace={root}");
            long cursor = 0;
            var reconnects = 0;
            while (!terminal && reconnects++ < 12)
            {
                await foreach (var item in client.StreamRunEventsAsync(runId, cursor, timeout.Token))
                {
                    cursor = item.Id;
                    if (item.Type == RunEventTypes.ModelGeneration)
                    {
                        var state = item.Data.Deserialize<ModelGenerationEvent>(ProtocolJson)?.State ?? "unknown";
                        if (progress.Count < 200) progress.Add(state);
                    }
                    else if (item.Type == RunEventTypes.TextDelta)
                    {
                        var delta = item.Data.Deserialize<TextDeltaEvent>(ProtocolJson)?.Delta;
                        if (deltas.Length < 8_000) deltas.Append(delta);
                    }
                    else if (item.Type == RunEventTypes.ClientToolProposed)
                    {
                        var proposal = item.Data.Deserialize<ToolProposal>(ProtocolJson)!;
                        if (!submitted.Add(proposal.ProposalId)) continue;
                        Assert.True(calls.Count < 24, "Live fixture exceeded its bounded tool budget.");
                        calls.Add(proposal.Name);
                        ClientToolResult result;
                        try
                        {
                            ValidateFixtureTool(proposal, python);
                            var value = await executor.ExecuteAsync(proposal.Name, proposal.Arguments, timeout.Token);
                            result = new ClientToolResult(proposal.ProposalId, "completed", value);
                            output.WriteLine($"{proposal.Name}: {value.GetRawText()[..Math.Min(value.GetRawText().Length, 1_200)]}");
                        }
                        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                        {
                            output.WriteLine($"{proposal.Name}: {exception.Message}");
                            result = new ClientToolResult(proposal.ProposalId, "failed", JsonSerializer.SerializeToElement(new { success = false }),
                                "coding.live_tool_failed", exception.Message);
                        }
                        await client.SubmitClientToolResultAsync(runId, result, timeout.Token);
                    }
                    else if (item.Type == RunEventTypes.RunFailed)
                    {
                        failure = item.Data.Deserialize<RunFailedEvent>(ProtocolJson);
                        terminal = true;
                    }
                    else if (item.Type is RunEventTypes.RunCompleted or RunEventTypes.RunCancelled) terminal = true;
                    if (terminal) break;
                }
                if (!terminal) await Task.Delay(250, timeout.Token);
            }
            var snapshot = await client.GetRunAsync(runId, timeout.Token);
            output.WriteLine($"State={snapshot.State}; calls={string.Join(",", calls)}; progress={string.Join(",", progress.Distinct())}; text={deltas}");
            Assert.True(snapshot.State == RunState.Completed, $"Coding ended {snapshot.State}: {failure?.ErrorCode ?? snapshot.ErrorCode}; {failure?.Message}");
            Assert.Contains(ClientToolNames.CodingRead, calls);
            Assert.Contains(ClientToolNames.CodingEdit, calls);
            Assert.Contains(ClientToolNames.CodingCommand, calls);
            Assert.Contains("codingWaiting", progress);
            Assert.True(deltas.Length > 0, "No visible text deltas were received.");
            var fixedCode = await File.ReadAllTextAsync(Path.Combine(root, "calculator.py"), timeout.Token);
            Assert.Contains("a + b", fixedCode, StringComparison.Ordinal);
            var independent = await executor.ExecuteAsync(ClientToolNames.CodingCommand,
                JsonSerializer.SerializeToElement(new { executable = python, arguments = UnitTestArguments, timeoutSeconds = 30 }), timeout.Token);
            Assert.True(independent.GetProperty("success").GetBoolean(), independent.GetRawText());
            Assert.Contains("Ran 3 tests", independent.GetProperty("stderr").GetString() ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            if (runId is not null && !terminal)
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await client.CancelRunAsync(runId, cleanupTimeout.Token); }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { output.WriteLine("Cancel cleanup: " + exception.Message); }
            }
            // The exact temporary root was created above and never comes from model arguments.
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task LocalModelCreatesFileThenAppliesSeparateHashGuardedEditAndVerifiesContents()
    {
        if (Environment.GetEnvironmentVariable("GO_AI_CODING_FILE_LIVE") != "1") return;
        var model = Environment.GetEnvironmentVariable("GO_AI_LIVE_CODING_MODEL");
        Assert.False(string.IsNullOrWhiteSpace(model), "GO_AI_LIVE_CODING_MODEL must select an installed coding/ model.");
        var root = Path.Combine(Path.GetTempPath(), "go-coding-file-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "notes"));
        var targetPath = Path.Combine(root, "release-note.txt");
        var notePath = Path.Combine(root, "notes", "request.txt");
        var marker = "GO-FILE-" + Guid.NewGuid().ToString("N");
        var initialText = $"Projekt: {marker}\nStatus: ENTWURF\n";
        var finalText = $"Projekt: {marker}\nStatus: FREIGEGEBEN\n";
        var note = $"Auftragskennung: {marker}\nZieldatei im Projektstamm: release-note.txt\n"
            + "Anfangsinhalt (exakt zwei Zeilen, mit abschließendem Zeilenumbruch):\n" + initialText;
        const string preservedReadme = "Temporäres Akzeptanzprojekt. Die Auftragsnotizen liegen unter notes/.\n";
        var readmePath = Path.Combine(root, "README.md");
        var executor = new LocalCodingToolExecutor(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        using var http = new HttpClient
        {
            BaseAddress = new Uri((Environment.GetEnvironmentVariable("GO_AI_SERVER_URL") ?? "http://127.0.0.1:8080").TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var client = new GoAiClient(http, "go-coding-file-live-" + Guid.NewGuid().ToString("N"));
        string? runId = null;
        var terminal = false;
        var proposed = new List<string>();
        var succeeded = new List<(string Name, JsonElement Arguments, JsonElement Result)>();
        var submitted = new HashSet<string>(StringComparer.Ordinal);
        var progress = new HashSet<string>(StringComparer.Ordinal);
        var response = new StringBuilder();
        string? lastReadHash = null;
        RunFailedEvent? failure = null;
        try
        {
            await File.WriteAllTextAsync(notePath, note, timeout.Token);
            await File.WriteAllTextAsync(readmePath, preservedReadme, timeout.Token);
            Assert.False(File.Exists(targetPath));
            var prompt = $"Verschaffe dir zuerst einen Überblick über die Dateien in diesem Projekt. "
                + $"Suche im Projekt nach der Kennung {marker} und lies die dazugehörige Auftragsnotiz. "
                + "Lege die darin beschriebene Zieldatei zunächst neu mit genau dem angegebenen Anfangsinhalt an. "
                + "Lies die neu angelegte Datei für ihren aktuellen Inhalt und SHA-256. "
                + "Ersetze erst danach in einem separaten gezielten Bearbeitungsschritt ausschließlich "
                + "'Status: ENTWURF' durch 'Status: FREIGEGEBEN'. Sichere diese Änderung mit dem gelesenen SHA-256 ab. "
                + "Lies anschließend die fertige Datei erneut und berichte den überprüften Endzustand kurz. "
                + "Behalte die Projektkennung, alle anderen Zeilen und den abschließenden Zeilenumbruch bei. "
                + "Ändere keine vorhandenen Projektdateien. Nutze nur Dateiwerkzeuge und führe keine Shell- oder Prozessbefehle aus. "
                + "Das Anlegen und die danach getrennte Änderung der Zieldatei sind in diesem temporären Projekt autorisiert.";
            var accepted = await client.CreateRunAsync(new RunRequest(GoAiProtocol.Version, RunMode.Coding,
                [new RunMessage("user", [new ContentPart("text", prompt)])],
                ClientCapabilities: ["coding"], Limits: new RunLimits(4_096, 32_768, 720),
                AllowedServerTools: [], PreferredCodingModelId: model), "go-coding-file-live-" + Guid.NewGuid().ToString("N"), timeout.Token);
            runId = accepted.RunId;
            output.WriteLine($"File acceptance run={runId}; model={model}; workspace={root}; marker={marker}");
            long cursor = 0;
            var reconnects = 0;
            while (!terminal && reconnects++ < 12)
            {
                await foreach (var item in client.StreamRunEventsAsync(runId, cursor, timeout.Token))
                {
                    cursor = item.Id;
                    if (item.Type == RunEventTypes.ModelGeneration)
                    {
                        var generation = item.Data.Deserialize<ModelGenerationEvent>(ProtocolJson);
                        if (generation is not null && progress.Add(generation.State))
                            output.WriteLine($"Progress: {generation.State}; {item.Data.GetRawText()}");
                    }
                    else if (item.Type == RunEventTypes.TextDelta)
                    {
                        if (response.Length < 8_000) response.Append(item.Data.Deserialize<TextDeltaEvent>(ProtocolJson)?.Delta);
                    }
                    else if (item.Type == RunEventTypes.ClientToolProposed)
                    {
                        var proposal = item.Data.Deserialize<ToolProposal>(ProtocolJson)!;
                        Assert.Equal(runId, proposal.RunId);
                        if (!submitted.Add(proposal.ProposalId)) continue;
                        Assert.True(proposed.Count < 24, "File acceptance exceeded its bounded 24-call tool budget.");
                        proposed.Add(proposal.Name);
                        ClientToolResult result;
                        try
                        {
                            ValidateFileFixtureTool(proposal);
                            if (proposal.Name == ClientToolNames.CodingWrite)
                            {
                                Assert.False(File.Exists(targetPath), "The agent must create a new file, not overwrite the edit result.");
                                Assert.False(proposal.Arguments.TryGetProperty("expectedSha256", out var expected)
                                    && expected.ValueKind is not JsonValueKind.Null, "A new file must not claim an existing SHA-256.");
                            }
                            if (proposal.Name == ClientToolNames.CodingEdit)
                            {
                                Assert.NotNull(lastReadHash);
                                Assert.Equal(lastReadHash, proposal.Arguments.GetProperty("expectedSha256").GetString(), ignoreCase: true);
                                Assert.Equal(initialText, (await File.ReadAllTextAsync(targetPath, timeout.Token)).ReplaceLineEndings("\n"));
                            }
                            var value = await executor.ExecuteAsync(proposal.Name, proposal.Arguments, timeout.Token);
                            succeeded.Add((proposal.Name, proposal.Arguments.Clone(), value.Clone()));
                            if (proposal.Name == ClientToolNames.CodingWrite)
                            {
                                Assert.True(value.GetProperty("created").GetBoolean());
                                Assert.Equal(initialText, (await File.ReadAllTextAsync(targetPath, timeout.Token)).ReplaceLineEndings("\n"));
                            }
                            if (proposal.Name == ClientToolNames.CodingRead && IsFileFixtureTarget(proposal.Arguments))
                                lastReadHash = value.GetProperty("sha256").GetString();
                            result = new ClientToolResult(proposal.ProposalId, "completed", value);
                            var raw = value.GetRawText();
                            output.WriteLine($"Tool {proposed.Count}: {proposal.Name}; input={proposal.Arguments.GetRawText()}; result={raw[..Math.Min(raw.Length, 1_200)]}");
                        }
                        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                        {
                            output.WriteLine($"Tool {proposed.Count}: {proposal.Name} failed: {exception.Message}");
                            result = new ClientToolResult(proposal.ProposalId, "failed", JsonSerializer.SerializeToElement(new { success = false }),
                                "coding.live_file_tool_failed", exception.Message);
                        }
                        await client.SubmitClientToolResultAsync(runId, result, timeout.Token);
                    }
                    else if (item.Type == RunEventTypes.RunFailed)
                    {
                        failure = item.Data.Deserialize<RunFailedEvent>(ProtocolJson);
                        terminal = true;
                    }
                    else if (item.Type is RunEventTypes.RunCompleted or RunEventTypes.RunCancelled) terminal = true;
                    if (terminal) break;
                }
                if (!terminal) await Task.Delay(250, timeout.Token);
            }
            var snapshot = await client.GetRunAsync(runId, timeout.Token);
            output.WriteLine($"State={snapshot.State}; proposed={string.Join(",", proposed)}; progress={string.Join(",", progress)}; text={response}");
            Assert.True(snapshot.State == RunState.Completed, $"File acceptance ended {snapshot.State}: {failure?.ErrorCode ?? snapshot.ErrorCode}; {failure?.Message}");
            Assert.Contains("codingWaiting", progress);
            Assert.True(response.Length > 0, "The real model did not produce a visible final response.");
            Assert.DoesNotContain(ClientToolNames.CodingCommand, proposed);
            Assert.DoesNotContain(ClientToolNames.CodingGitDiff, proposed);
            Assert.Contains(succeeded, call => call.Name == ClientToolNames.CodingList);
            Assert.Contains(succeeded, call => call.Name == ClientToolNames.CodingSearch
                && call.Result.GetProperty("matches").EnumerateArray().Any(match => match.GetProperty("text").GetString()?.Contains(marker, StringComparison.Ordinal) == true));
            Assert.Contains(succeeded, call => call.Name == ClientToolNames.CodingRead
                && call.Result.GetProperty("path").GetString() == "notes/request.txt");
            var writeIndex = succeeded.FindIndex(call => call.Name == ClientToolNames.CodingWrite);
            var editIndex = succeeded.FindIndex(call => call.Name == ClientToolNames.CodingEdit);
            Assert.True(writeIndex >= 0 && editIndex > writeIndex, "The new file must be written before the separate edit.");
            Assert.Contains(succeeded.Skip(writeIndex + 1).Take(editIndex - writeIndex - 1),
                call => call.Name == ClientToolNames.CodingRead && IsFileFixtureTarget(call.Arguments));
            Assert.Contains(succeeded.Skip(editIndex + 1), call => call.Name == ClientToolNames.CodingRead
                && IsFileFixtureTarget(call.Arguments)
                && call.Result.GetProperty("content").GetString()?.Contains("Status: FREIGEGEBEN", StringComparison.Ordinal) == true);
            Assert.Equal(finalText, (await File.ReadAllTextAsync(targetPath, timeout.Token)).ReplaceLineEndings("\n"));
            Assert.Equal(note, await File.ReadAllTextAsync(notePath, timeout.Token));
            Assert.Equal(preservedReadme, await File.ReadAllTextAsync(readmePath, timeout.Token));
            Assert.Equal(3, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
        }
        finally
        {
            if (runId is not null && !terminal)
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await client.CancelRunAsync(runId, cleanupTimeout.Token); }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { output.WriteLine("Cancel cleanup: " + exception.Message); }
            }
            // This unique root is allocated by the test, never supplied by the model.
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool IsFileFixtureTarget(JsonElement arguments) =>
        arguments.TryGetProperty("path", out var path) && path.GetString() is "release-note.txt" or "./release-note.txt";

    private static void ValidateFileFixtureTool(ToolProposal proposal)
    {
        if (proposal.Name is not (ClientToolNames.CodingList or ClientToolNames.CodingSearch or ClientToolNames.CodingRead
            or ClientToolNames.CodingWrite or ClientToolNames.CodingEdit))
            throw new UnauthorizedAccessException("Only bounded project file tools are authorized; processes and shell commands are forbidden.");
        var mutates = proposal.Name is ClientToolNames.CodingWrite or ClientToolNames.CodingEdit;
        Assert.Equal(mutates ? ToolRiskClass.LocalMutation : ToolRiskClass.ReadOnly, proposal.RiskClass);
        Assert.True(proposal.ExpiresAt > DateTimeOffset.UtcNow, "The live tool proposal has expired.");
        if (mutates && !IsFileFixtureTarget(proposal.Arguments))
            throw new UnauthorizedAccessException("Only the new release-note.txt fixture may be created or edited.");
    }

    private static void ValidateFixtureTool(ToolProposal proposal, string python)
    {
        if (proposal.Name is not (ClientToolNames.CodingList or ClientToolNames.CodingSearch or ClientToolNames.CodingRead
            or ClientToolNames.CodingEdit or ClientToolNames.CodingCommand))
            throw new UnauthorizedAccessException("Only fixture read/edit/test tools are authorized in this live test.");
        if (proposal.Name == ClientToolNames.CodingEdit
            && proposal.Arguments.GetProperty("path").GetString() is not ("calculator.py" or "./calculator.py"))
            throw new UnauthorizedAccessException("Only calculator.py may be changed.");
        if (proposal.Name == ClientToolNames.CodingCommand)
        {
            var args = proposal.Arguments.GetProperty("arguments").EnumerateArray().Select(static value => value.GetString()).ToArray();
            if (proposal.Arguments.GetProperty("executable").GetString() != python
                || !args.SequenceEqual(UnitTestArguments, StringComparer.Ordinal))
                throw new UnauthorizedAccessException("Only the explicitly authorized Python unit-test invocation is permitted.");
        }
    }
}
