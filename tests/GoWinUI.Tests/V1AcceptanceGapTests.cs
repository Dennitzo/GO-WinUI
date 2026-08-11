using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GoWinUI.Core.Chat;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure.AI;
using GoWinUI.Infrastructure.Documents;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace GoWinUI.Tests;

public sealed class V1AcceptanceGapTests
{
    [Fact]
    public void VeryLongPromptStaysInsideContextBudgetAndReportsTruncation()
    {
        const int contextLength = 4_096;
        const int outputReserve = 1_024;
        var prompt = "ANFANG-" + new string('x', 100_000) + "-ENDE";
        var result = new ContextAssembler().Build(new(
            "Kurzer Systemprompt.",
            prompt,
            Array.Empty<ChatMessage>(),
            null,
            Array.Empty<DocumentPage>(),
            contextLength));

        var userMessage = result.Messages[^1];
        var actualTokens = result.Messages.Sum(static message => Math.Max(1, (message.Content.Length + 3) / 4));

        Assert.True(result.WasTruncated);
        Assert.False(string.IsNullOrWhiteSpace(result.TruncationNotice));
        Assert.Equal(ChatRole.User, userMessage.Role);
        Assert.NotEqual(prompt, userMessage.Content);
        Assert.Contains("\nANFANG-", userMessage.Content, StringComparison.Ordinal);
        Assert.EndsWith("-ENDE", userMessage.Content, StringComparison.Ordinal);
        Assert.Equal(actualTokens, result.EstimatedTokens);
        Assert.InRange(actualTokens, 1, contextLength - outputReserve);
    }

    [Fact]
    public async Task CancelledDocumentImportAfterBlobCommitLeavesNoBlob()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Abbruch");
        using var cancellation = new CancellationTokenSource();
        var store = new CancelAfterCommittedImportStore(environment.Get<IBinaryObjectStore>(), cancellation);
        var ingestor = new DocumentIngestor(environment.Get<SqliteDatabase>(), store);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ingestor.ImportAsync(
            session.Id,
            "abbruch.txt",
            new MemoryStream(Encoding.UTF8.GetBytes("bereits vollstaendig als Blob geschrieben"), writable: false),
            cancellation.Token));

        Assert.Empty(await ingestor.ListAsync(session.Id));
        await using var connection = await OpenDatabaseAsync(environment);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM binary_objects;";
        Assert.Equal(0L, Assert.IsType<long>(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task ChatCancellationPersistsPartialAnswerAndCancelledRun()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Cancel");
        var lmStudio = new BlockingAfterPartialLmStudio();
        using var orchestrator = CreateOrchestrator(environment, lmStudio);

        var send = orchestrator.SendAsync(session.Id, "Frage", "test-model", "Hilf mir.");
        await lmStudio.WaitingForCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        orchestrator.Cancel();
        var answer = await send.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MessageStatus.Cancelled, answer.Status);
        Assert.Equal("bereits empfangen", answer.Content);
        var messages = await chats.ListMessagesAsync(session.Id);
        var persistedAnswer = Assert.Single(messages, static message => message.Role == ChatRole.Assistant);
        Assert.Equal(MessageStatus.Cancelled, persistedAnswer.Status);
        Assert.Equal("bereits empfangen", persistedAnswer.Content);
        var run = await ReadRunAsync(environment, persistedAnswer.Id);
        Assert.Equal("cancelled", run.Status);
        Assert.Null(run.Error);
    }

    [Fact]
    public async Task ChatFailurePersistsPartialAnswerErrorAndFailedRun()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Fehler");
        using var orchestrator = CreateOrchestrator(environment, new FailingAfterPartialLmStudio());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.SendAsync(session.Id, "Frage", "test-model", "Hilf mir."));

        Assert.Equal("simulierter Streamfehler", exception.Message);
        var messages = await chats.ListMessagesAsync(session.Id);
        var persistedAnswer = Assert.Single(messages, static message => message.Role == ChatRole.Assistant);
        Assert.Equal(MessageStatus.Failed, persistedAnswer.Status);
        Assert.Equal("Teilantwort vor Fehler", persistedAnswer.Content);
        Assert.Equal("simulierter Streamfehler", persistedAnswer.Error);
        var run = await ReadRunAsync(environment, persistedAnswer.Id);
        Assert.Equal("failed", run.Status);
        Assert.Equal("simulierter Streamfehler", run.Error);
    }

    [Fact]
    public async Task MalformedSseEventIsIgnoredWithoutLosingFollowingDelta()
    {
        var handler = new QueueHandler(SseResponse(
            "event: response.output_text.delta\ndata: {kein-json}\n\n" +
            "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"Weiter\"}\n\n" +
            "event: response.completed\ndata: {\"type\":\"response.completed\"}\n\n"));
        var client = new LmStudioClient(new HttpClient(handler), new StaticSettingsStore());
        var deltas = new List<LmDelta>();

        await foreach (var delta in client.StreamAsync(CreateLmRequest()))
        {
            deltas.Add(delta);
        }

        Assert.Equal("Weiter", string.Concat(deltas.Select(static delta => delta.Text)));
        Assert.True(deltas[^1].IsCompleted);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CancellationAfterFirstResponsesTokenDoesNotFallbackToChatEndpoint()
    {
        var firstEvent = Encoding.UTF8.GetBytes(
            "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"Token\"}\n\n");
        var content = new StreamContent(new FirstEventThenBlockingStream(firstEvent));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        var client = new LmStudioClient(new HttpClient(handler), new StaticSettingsStore());
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = client.StreamAsync(CreateLmRequest(), cancellation.Token).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("Token", enumerator.Current.Text);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/v1/responses", request.RequestUri?.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackupWithManipulatedPayloadHashIsRejected()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var backup = environment.Get<IBackupService>();
        var path = Path.Combine(environment.Directory, "manipuliert.gobackup");
        _ = await backup.CreateAsync(path);

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var settings = archive.GetEntry("settings.json")
                ?? throw new InvalidDataException("Testbackup enthaelt keine settings.json.");
            settings.Delete();
            var replacement = archive.CreateEntry("settings.json", CompressionLevel.Optimal);
            await using var target = replacement.Open();
            await target.WriteAsync(Encoding.UTF8.GetBytes("{\"version\":1,\"language\":\"manipulated\"}"));
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => backup.ValidateAsync(path));
        Assert.Contains("settings.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackupWithForeignKeyViolationIsRejectedBeforeActiveDatabaseIsReplaced()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Bleibt erhalten");
        _ = await chats.AddMessageAsync(session.Id, ChatRole.User, "Verknüpft", MessageStatus.Completed);
        var backup = environment.Get<IBackupService>();
        var path = Path.Combine(environment.Directory, "fremdschluessel.gobackup");
        _ = await backup.CreateAsync(path);

        var modifiedDatabase = Path.Combine(environment.Directory, "invalid-backup.db");
        using (var archive = ZipFile.OpenRead(path))
        {
            (archive.GetEntry("GO.db") ?? throw new InvalidDataException("Testbackup enthält keine GO.db."))
                .ExtractToFile(modifiedDatabase);
        }

        await using (var connection = new SqliteConnection($"Data Source={modifiedDatabase};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=OFF; DELETE FROM chat_sessions WHERE id=$id;";
            command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        string databaseHash;
        await using (var databaseStream = File.OpenRead(modifiedDatabase))
        {
            databaseHash = Convert.ToHexString(await SHA256.HashDataAsync(databaseStream)).ToLowerInvariant();
        }
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var databaseEntry = archive.GetEntry("GO.db") ?? throw new InvalidDataException("Testbackup enthält keine GO.db.");
            databaseEntry.Delete();
            archive.CreateEntryFromFile(modifiedDatabase, "GO.db", CompressionLevel.Optimal);

            var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Testbackup enthält kein Manifest.");
            JsonNode manifest;
            await using (var manifestStream = manifestEntry.Open())
            {
                manifest = await JsonNode.ParseAsync(manifestStream) ?? throw new InvalidDataException("Testmanifest ist leer.");
            }
            manifestEntry.Delete();
            manifest["databaseSha256"] = databaseHash;
            var replacementManifest = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            await using var replacementStream = replacementManifest.Open();
            await System.Text.Json.JsonSerializer.SerializeAsync(replacementStream, manifest);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => backup.RestoreAsync(path));
        Assert.NotNull(await chats.GetSessionAsync(session.Id));
    }

    private static ChatOrchestrator CreateOrchestrator(TestEnvironment environment, ILmStudioClient lmStudio) => new(
        environment.Get<IChatRepository>(),
        environment.Get<IDocumentIngestor>(),
        lmStudio,
        environment.Get<IContextAssembler>(),
        environment.Get<SqliteDatabase>());

    private static LmChatRequest CreateLmRequest() => new(
        "local-model",
        [new LmChatMessage(ChatRole.User, "Hallo")]);

    private static HttpResponseMessage SseResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    private static async Task<SqliteConnection> OpenDatabaseAsync(TestEnvironment environment)
    {
        var connection = new SqliteConnection($"Data Source={environment.Get<IGoDatabase>().DatabasePath}");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<(string Status, string? Error)> ReadRunAsync(TestEnvironment environment, Guid assistantMessageId)
    {
        await using var connection = await OpenDatabaseAsync(environment);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status,error FROM chat_runs WHERE assistant_message_id=$message;";
        command.Parameters.AddWithValue("$message", assistantMessageId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private sealed class CancelAfterCommittedImportStore(
        IBinaryObjectStore inner,
        CancellationTokenSource cancellation) : IBinaryObjectStore
    {
        public async Task<BinaryObjectDescriptor> ImportAsync(Stream source, string contentType, CancellationToken cancellationToken = default)
        {
            var descriptor = await inner.ImportAsync(source, contentType, cancellationToken);
            cancellation.Cancel();
            return descriptor;
        }

        public Task<Stream> OpenReadAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(id, cancellationToken);

        public Task ExportAsync(Guid id, Stream destination, CancellationToken cancellationToken = default) =>
            inner.ExportAsync(id, destination, cancellationToken);

        public Task<bool> VerifyAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.VerifyAsync(id, cancellationToken);

        public Task DeleteIfUnreferencedAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.DeleteIfUnreferencedAsync(id, cancellationToken);
    }

    private sealed class BlockingAfterPartialLmStudio : ILmStudioClient
    {
        internal TaskCompletionSource WaitingForCancellation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<LmModel>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LmModel>>([new("test-model", ContextLength: 8_192)]);

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public async IAsyncEnumerable<LmDelta> StreamAsync(
            LmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new("bereits empfangen");
            WaitingForCancellation.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FailingAfterPartialLmStudio : ILmStudioClient
    {
        public Task<IReadOnlyList<LmModel>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LmModel>>([new("test-model", ContextLength: 8_192)]);

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public async IAsyncEnumerable<LmDelta> StreamAsync(
            LmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new("Teilantwort vor Fehler");
            await Task.Yield();
            throw new InvalidOperationException("simulierter Streamfehler");
        }
    }

    private sealed class StaticSettingsStore : ISettingsStore
    {
        public string SettingsPath => string.Empty;

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        internal List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class FirstEventThenBlockingStream(byte[] firstEvent) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => firstEvent.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < firstEvent.Length)
            {
                var count = Math.Min(buffer.Length, firstEvent.Length - _offset);
                firstEvent.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return ValueTask.FromResult(count);
            }

            return WaitForCancellationAsync(cancellationToken);
        }

        private static async ValueTask<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
