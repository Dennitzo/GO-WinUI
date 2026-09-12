using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace GoWinUI.Tests;

/// <summary>Opt-in acceptance through the real AssistantService, broker, executor and SQLite tool journal.</summary>
public sealed class CodingTimelineLiveTests(ITestOutputHelper output)
{
    private const string Title = "Inline-Schritte Liveprüfung";
    private const string DefaultModel = "coding/Qwen3.8-27B-UD-Q8_K_XL~86704cb9d896";
    private const string SourceName = "timeline_sample.py";
    private const string SourceBefore = "def greeting():\n    return 'before'\n";
    private const string SourceAfter = "def greeting():\n    return 'after'\n";
    private const string NoteName = "created-note.txt";
    private const string NoteContent = "Inline-Schritte geprüft.\n";
    private static readonly string[] ProbeArguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", "probe.ps1"];
    private static readonly string[] MutationTools = [ClientToolNames.CodingWrite, ClientToolNames.CodingEdit];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Fact]
    [Trait("Category", "Live")]
    public async Task RealAssistantPersistsInlineFileDiffsAndTerminalProgressBeforeCompletion()
    {
        if (Environment.GetEnvironmentVariable("GO_AI_TIMELINE_LIVE") != "1") return;
        await using var history = await HistoryEnvironment.CreateAsync();
        var identifier = Guid.NewGuid().ToString("N");
        var fixture = Path.Combine(FindRepository(), "artifacts", "coding-validation", "ui-project", "timeline-live", "run-" + identifier);
        var workspace = Path.Combine(fixture, "workspace");
        Assert.False(Directory.Exists(fixture), "A live fixture must never reuse an existing workspace.");
        RejectLinkedParents(fixture);
        Directory.CreateDirectory(workspace);
        var marker = "GO-INLINE-" + identifier;
        var sourceBytes = Encoding.UTF8.GetBytes(SourceBefore);
        var sourceHash = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
        var script = CreateProbeScript(marker);
        byte[] scriptBytes = [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(script)];
        var scriptPath = Path.Combine(workspace, "probe.ps1");
        await File.WriteAllBytesAsync(Path.Combine(workspace, SourceName), sourceBytes);
        await File.WriteAllBytesAsync(scriptPath, scriptBytes);
        await RunGitAsync(workspace, "init", "--quiet");
        await RunGitAsync(workspace, "-c", "core.autocrlf=false", "add", "--", SourceName, "probe.ps1");
        await RunGitAsync(workspace, "-c", "user.name=GO Timeline Acceptance", "-c", "user.email=timeline@example.invalid",
            "-c", "commit.gpgsign=false", "-c", "core.hooksPath=.git/no-hooks", "commit", "--quiet", "-m", "Isolated live acceptance fixture");

        var model = Environment.GetEnvironmentVariable("GO_AI_LIVE_CODING_MODEL") ?? DefaultModel;
        var server = Environment.GetEnvironmentVariable("GO_AI_SERVER_URL") ?? "http://127.0.0.1:8080";
        using var settings = new SettingsCoordinator(new LiveSettings(server, model));
        await settings.InitializeAsync();
        using var connection = new GoAiConnectionService(settings, NullLogger<GoAiConnectionService>.Instance);
        var chats = history.Get<IChatRepository>();
        var documents = history.Get<IDocumentIngestor>();
        var session = await chats.CreateSessionAsync(Title);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: true);
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(executable));
        var sourceRead = false;
        var scriptRead = false;
        var commandProposals = 0;
        // Coding does not enter CAD, media capture or document-generation branches.
        // Unexpected tool proposals are rejected in the real service update before broker dispatch.
        var broker = new LocalToolBroker(connection, null!, documents, null!, chats);
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = new GoAiAssistantService(connection, chats, history.Get<IAssistantAttachmentRepository>(),
            history.Get<IChatArtifactRepository>(), history.Get<IGoAiRunRepository>(), history.Get<IClientToolExecutionRepository>(),
            history.Get<IBinaryObjectStore>(), documents, new DocumentContextPreparationService(documents),
            new SessionContextPreparationService(chats), broker, null!, null!, settings, recent, NullLogger<GoAiAssistantService>.Instance);
        var prompt = CreatePrompt(executable);
        var now = DateTimeOffset.UtcNow;
        var trigger = new PromptTriggerMatch(new(Guid.NewGuid(), PromptTriggerAction.Coding, "coding", "Isolated inline acceptance",
            PromptTriggerMatchMode.Exact, true, 0, 1, now, now), prompt, prompt);
        var observations = new List<Observation>();
        var proposals = new HashSet<string>(StringComparer.Ordinal);
        Guid? assistantId = null;
        string? serverRunId = null;
        ChatMessage? final = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var eventsPath = Path.Combine(fixture, "updates.jsonl");
        output.WriteLine($"session={session.Id}; title={Title}; workspace={workspace}; profile={history.Directory}; marker={marker}");
        await File.WriteAllTextAsync(Path.Combine(fixture, "session.json"), JsonSerializer.Serialize(new
        {
            sessionId = session.Id, title = Title, workspace, profile = history.Directory, marker, model,
            authorization = "Production automatic authorization; no approval callback or dialog. The test observer limits proposals to its isolated fixture.",
        }, Json));
        try
        {
            final = await service.SendAsync(session.Id, prompt, trigger, async update =>
            {
                assistantId = update.Message.Id;
                if (serverRunId is null)
                    serverRunId = (await history.Get<IGoAiRunRepository>().ListResumableAsync(timeout.Token))
                        .FirstOrDefault(run => run.SessionId == session.Id && run.ServerRunId is not null)?.ServerRunId;
                var step = update.ToolStep;
                if (step is not null)
                {
                    if (proposals.Add(step.Id))
                    {
                        Assert.InRange(proposals.Count, 1, 14);
                        ValidateProposal(step, sourceRead, sourceHash, executable, scriptRead, commandProposals);
                        if (step.Tool == ClientToolNames.CodingCommand)
                        {
                            Assert.Equal(scriptBytes, await File.ReadAllBytesAsync(scriptPath, timeout.Token));
                            commandProposals++;
                        }
                    }
                    if (step.Status == "completed" && step.Tool == ClientToolNames.CodingRead && step.OutputJson is not null)
                    {
                        var data = JsonSerializer.Deserialize<JsonElement>(step.OutputJson);
                        if (String(data, "path") == SourceName) sourceRead = true;
                        if (String(data, "path") == "probe.ps1") scriptRead = true;
                    }
                    var observation = new Observation(DateTimeOffset.UtcNow, update.Kind.ToString(), update.Message.Content.Length, step);
                    observations.Add(observation);
                    await File.AppendAllTextAsync(eventsPath, JsonSerializer.Serialize(observation) + "\n", timeout.Token);
                }
                if (update.Kind == GoAiAssistantUpdateKind.Started || step is not null)
                    output.WriteLine($"{DateTimeOffset.UtcNow:O} {update.Kind}: {step?.Tool} {step?.Status} {OutputPhase(step)}");
            }, timeout.Token);
            Assert.True(final.Status == MessageStatus.Completed, final.Error ?? final.Content);
            Assert.Equal(1, commandProposals);
            Assert.Equal(SourceAfter, await File.ReadAllTextAsync(Path.Combine(workspace, SourceName), timeout.Token));
            Assert.Equal(NoteContent, await File.ReadAllTextAsync(Path.Combine(workspace, NoteName), timeout.Token));
            Assert.Equal(scriptBytes, await File.ReadAllBytesAsync(scriptPath, timeout.Token));
            Assert.Equal(3, Directory.GetFiles(workspace, "*", SearchOption.TopDirectoryOnly).Length);
            var snapshot = await history.Get<IConversationSnapshotRepository>().GetAsync(session.Id, timeout.Token);
            var stored = Assert.Single(snapshot!.Messages, message => message.Id == final.Id);
            Assert.Equal(final.Content, stored.Content);
            Assert.False(string.IsNullOrWhiteSpace(stored.Content));
            var steps = stored.ToolSteps!;
            Assert.InRange(steps.Count, 5, 14);
            Assert.All(steps, step =>
            {
                Assert.Equal("completed", step.Status);
                Assert.False(string.IsNullOrWhiteSpace(step.InputJson));
                Assert.False(string.IsNullOrWhiteSpace(step.OutputJson));
                Assert.False(string.IsNullOrWhiteSpace(step.Explanation));
                Assert.NotNull(step.ContentOffset);
                Assert.InRange(step.ContentOffset.Value, 0, stored.Content.Length);
                Assert.NotNull(step.StartedAt);
                Assert.NotNull(step.CompletedAt);
                Assert.True(step.CompletedAt >= step.StartedAt);
            });
            for (var index = 1; index < steps.Count; index++) Assert.True(steps[index].ContentOffset >= steps[index - 1].ContentOffset);
            foreach (var tool in MutationTools)
            {
                var step = Assert.Single(steps, step => step.Tool == tool);
                var updates = observations.Where(value => value.Step.Id == step.Id).ToArray();
                var preview = Assert.Single(updates, value => OutputPhase(value.Step) == "preview");
                var applied = Assert.Single(updates, value => value.Step.Status == "running" && OutputPhase(value.Step) == "applied");
                Assert.Equal("running", preview.Step.Status);
                Assert.True(preview.SeenAt <= applied.SeenAt);
                var data = JsonSerializer.Deserialize<JsonElement>(step.OutputJson!);
                Assert.True(data.GetProperty("applied").GetBoolean());
                Assert.Equal("applied", String(data, "phase"));
                Assert.Contains("@@", data.GetProperty("diff").GetString()!, StringComparison.Ordinal);
                Assert.False(data.GetProperty("diffTruncated").GetBoolean());
                Assert.Equal(JsonSerializer.Deserialize<JsonElement>(applied.Step.OutputJson!).GetProperty("diff").GetString(), data.GetProperty("diff").GetString());
            }
            var git = JsonSerializer.Deserialize<JsonElement>(Assert.Single(steps, step => step.Tool == ClientToolNames.CodingGitDiff).OutputJson!);
            Assert.Contains("-    return 'before'", git.GetProperty("diff").GetProperty("stdout").GetString()!, StringComparison.Ordinal);
            Assert.Contains("+    return 'after'", git.GetProperty("diff").GetProperty("stdout").GetString()!, StringComparison.Ordinal);
            Assert.Equal(0, git.GetProperty("status").GetProperty("exitCode").GetInt32());
            var command = Assert.Single(steps, step => step.Tool == ClientToolNames.CodingCommand);
            var commandData = JsonSerializer.Deserialize<JsonElement>(command.OutputJson!);
            Assert.Equal(0, commandData.GetProperty("exitCode").GetInt32());
            Assert.Contains(marker + "|STDOUT_BEFORE|Grüße € 日本語", commandData.GetProperty("stdout").GetString()!, StringComparison.Ordinal);
            Assert.Contains(marker + "|STDOUT_AFTER", commandData.GetProperty("stdout").GetString()!, StringComparison.Ordinal);
            Assert.Contains(marker + "|STDERR_BEFORE", commandData.GetProperty("stderr").GetString()!, StringComparison.Ordinal);
            Assert.Contains(marker + "|STDERR_AFTER", commandData.GetProperty("stderr").GetString()!, StringComparison.Ordinal);
            var early = observations.First(value => value.Step.Id == command.Id && value.Step.Status == "running"
                && value.Step.OutputJson?.Contains("STDOUT_BEFORE", StringComparison.Ordinal) == true);
            var earlyData = JsonSerializer.Deserialize<JsonElement>(early.Step.OutputJson!);
            Assert.Contains("STDERR_BEFORE", earlyData.GetProperty("stderr").GetString()!, StringComparison.Ordinal);
            Assert.DoesNotContain("STDOUT_AFTER", early.Step.OutputJson!, StringComparison.Ordinal);
            Assert.False(earlyData.TryGetProperty("exitCode", out _));
            Assert.True(early.SeenAt < observations.First(value => value.Step.Id == command.Id && value.Step.Status == "completed").SeenAt);
            Assert.InRange(commandData.GetProperty("elapsedMilliseconds").GetInt32(), 2_800, 15_000);
            output.WriteLine("Inline service acceptance passed: actual file phases, Git patch, early stdout/stderr and final persisted steps.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                if (service.IsRunning) await service.CancelCurrentAndWaitAsync(cleanup.Token);
                // SendAsync treats caller cancellation as a detached stream and clears its active
                // run handle. Cancel only this fixture's remaining server run explicitly as well.
                if (final?.Status != MessageStatus.Completed)
                {
                    var pending = (await history.Get<IGoAiRunRepository>().ListResumableAsync(cleanup.Token))
                        .Where(run => run.SessionId == session.Id).ToArray();
                    using var client = await connection.CreateClientAsync(cleanup.Token);
                    foreach (var run in pending)
                    {
                        if (run.ServerRunId is not null) await client.CancelRunAsync(run.ServerRunId, cleanup.Token);
                        await history.Get<IGoAiRunRepository>().UpdateAsync(run.Id, run.ServerRunId, run.LastEventId,
                            "cancelled", errorCode: "test.acceptance_cleanup", cancellationToken: cleanup.Token);
                    }
                    if (assistantId is { } messageId && await chats.GetMessageAsync(messageId, cleanup.Token) is { } incomplete)
                    {
                        foreach (var step in (incomplete.ToolSteps ?? []).Where(static step => step.Status == "running"))
                            await chats.SaveToolStepAsync(messageId, GoAiAssistantService.CompleteOpenToolStep(step, "cancelled", null), cleanup.Token);
                        if (incomplete.Status is MessageStatus.Pending or MessageStatus.Streaming)
                            await chats.UpdateMessageAsync(messageId, incomplete.Content, MessageStatus.Cancelled,
                                "Der Live-Akzeptanztest wurde abgebrochen.", cleanup.Token);
                    }
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
            {
                output.WriteLine("Scoped live cleanup: " + exception.Message);
            }
            await chats.RenameSessionAsync(session.Id, Title, CancellationToken.None);
            var messages = await chats.ListMessagesAsync(session.Id, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(fixture, "evidence.json"), JsonSerializer.Serialize(new
            {
                sessionId = session.Id, assistantId, serverRunId, workspace, model, marker, history.Persistent,
                finalStatus = final?.Status.ToString(), observations, messages,
                authorization = "Production automatic authorization; no approval callback or dialog. Fixture scope checked before dispatch; no fabricated model/tool outputs.",
            }, Json));
            output.WriteLine($"Persisted history: {session.Id}; evidence={Path.Combine(fixture, "evidence.json")}");
        }
    }

    private static void ValidateProposal(AssistantToolStep step, bool sourceRead, string originalHash,
        string executable, bool scriptRead, int commandProposals)
    {
        var args = JsonSerializer.Deserialize<JsonElement>(step.InputJson!);
        var path = (String(args, "path") ?? ".").Replace('\\', '/');
        if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        var allowed = step.Tool switch
        {
            ClientToolNames.CodingRead => path is SourceName or "probe.ps1" or NoteName,
            ClientToolNames.CodingList => path is "." or "",
            ClientToolNames.CodingWrite => path == NoteName && String(args, "content") == NoteContent,
            ClientToolNames.CodingEdit => path == SourceName && sourceRead && String(args, "expectedSha256") == originalHash
                && String(args, "oldText") == "'before'" && String(args, "newText") == "'after'",
            ClientToolNames.CodingGitDiff => path == SourceName,
            ClientToolNames.CodingCommand => scriptRead && commandProposals == 0
                && String(args, "executable") == executable && String(args, "workingDirectory") is null or "." or "./"
                && args.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Array
                && arguments.EnumerateArray().All(static value => value.ValueKind == JsonValueKind.String)
                && arguments.EnumerateArray().Select(static value => value.GetString()).SequenceEqual(ProbeArguments, StringComparer.Ordinal)
                && args.TryGetProperty("timeoutSeconds", out var seconds) && seconds.TryGetInt32(out var timeout) && timeout == 15
                && args.EnumerateObject().All(static property => property.Name is "executable" or "arguments" or "workingDirectory" or "timeoutSeconds"),
            _ => false,
        };
        if (!allowed) throw new InvalidOperationException("A model proposal exceeded the isolated live fixture: " + step.Tool + " " + path);
    }

    private static string? String(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static string? OutputPhase(AssistantToolStep? step) => step?.OutputJson is null ? null : String(JsonSerializer.Deserialize<JsonElement>(step.OutputJson), "phase");

    private static string CreatePrompt(string executable) => $$"""
        Prüfe im ausgewählten Testprojekt die sichtbaren einzelnen Werkzeugschritte. Erkläre vor jedem Aufruf kurz natürlich, was du als Nächstes prüfst.
        1. Lies timeline_sample.py mit coding.read. Ändere ausschließlich mit coding.edit den eindeutigen Text 'before' in 'after' (jeweils inklusive einfacher Anführungszeichen) und verwende den aktuellen SHA256 aus dem Lesen. Lies die Datei erneut.
        2. Erstelle einmal mit coding.write created-note.txt mit exakt dem Text „Inline-Schritte geprüft.“ und einem abschließenden LF-Zeilenumbruch. Lies auch diese Datei.
        3. Rufe coding.gitDiff mit path timeline_sample.py auf und prüfe den tatsächlichen Git-Patch.
        4. Lies probe.ps1. Verändere dieses Prüfskript nicht. Führe dann genau einmal coding.command mit executable {{JsonSerializer.Serialize(executable)}}, arguments {{JsonSerializer.Serialize(ProbeArguments)}}, workingDirectory ".", timeoutSeconds 15 aus. Es schreibt echte Standard- und Fehlerausgaben vor und nach einer Pause von drei Sekunden und prüft die geänderten Dateien.
        Keine anderen Dateien verändern, keine anderen Programme, keine Websuche, kein git add/commit. Verwende keine Batch-Edit-Alternative; die einzelne Ersetzung soll sichtbar geprüft werden. Fasse zum Schluss nur anhand der tatsächlichen Ergebnisse zusammen, ob Schreiben, Ersetzen, Git-Prüfung und PowerShell erfolgreich waren.
        """;

    private static string CreateProbeScript(string marker) => $$"""
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        [Console]::WriteLine('{{marker}}|STDOUT_BEFORE|Grüße € 日本語')
        [Console]::Error.WriteLine('{{marker}}|STDERR_BEFORE')
        Start-Sleep -Seconds 3
        $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'timeline_sample.py'))
        $note = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'created-note.txt'))
        if (!$source.Contains("return 'after'") -or $source.Contains("return 'before'") -or $note -cne "Inline-Schritte geprüft.`n") {
            [Console]::Error.WriteLine('{{marker}}|VERIFY_FAILED')
            exit 7
        }
        [Console]::WriteLine('{{marker}}|STDOUT_AFTER')
        [Console]::Error.WriteLine('{{marker}}|STDERR_AFTER')
        exit 0
        """;

    private static async Task RunGitAsync(string workspace, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workspace, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.fsmonitor=false");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start fixture git.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.True(process.ExitCode == 0, "Fixture git failed: " + await stdout + "\n" + await stderr);
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "GO.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Run this opt-in acceptance from a GO-WinUI repository build.");
    }

    private static void RejectLinkedParents(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("The acceptance workspace may not traverse a directory link.");
    }

    private sealed record Observation(DateTimeOffset SeenAt, string Kind, int ContentLength, AssistantToolStep Step);
    private sealed class LiveSettings(string server, string model) : ISettingsStore
    {
        private AppSettings _settings = new() { GoAiServerUrl = server, SelectedCodingModel = model };
        public string SettingsPath => "in-memory inline live settings; persistent profile settings are untouched";
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_settings);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { _settings = settings; return Task.CompletedTask; }
    }

    private sealed class HistoryEnvironment : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly TestEnvironment? _temporary;
        private HistoryEnvironment(TestEnvironment temporary) { _temporary = temporary; _services = temporary.Services; Directory = temporary.Directory; }
        private HistoryEnvironment(string directory, ServiceProvider services) { Directory = directory; _services = services; }
        public string Directory { get; }
        public bool Persistent => _temporary is null;
        public T Get<T>() where T : notnull => _services.GetRequiredService<T>();
        public static async Task<HistoryEnvironment> CreateAsync()
        {
            var profile = Environment.GetEnvironmentVariable("GO_AI_TIMELINE_CHAT_PROFILE");
            if (string.IsNullOrWhiteSpace(profile)) return new(await TestEnvironment.CreateAsync());
            if (!Path.IsPathFullyQualified(profile) || !System.IO.Directory.Exists(profile))
                throw new ArgumentException("GO_AI_TIMELINE_CHAT_PROFILE must be an existing absolute GO DataDirectory.");
            var root = Path.GetFullPath(profile);
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddGoInfrastructure(options => options.DataDirectory = root);
            var services = collection.BuildServiceProvider(validateScopes: true);
            try { await services.GetRequiredService<IGoDatabase>().InitializeAsync(); return new(root, services); }
            catch { await services.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if (_temporary is not null) await _temporary.DisposeAsync();
            else await _services.DisposeAsync();
            // Explicit profile history and the isolated evidence workspace are preserved for native UI review.
        }
    }
}
