using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GoWinUI.Core.Chat;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
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
        using var envelope = JsonDocument.Parse(userMessage.Content);
        var boundedPrompt = envelope.RootElement.GetProperty("originalUserPrompt").GetString();
        Assert.NotNull(boundedPrompt);
        Assert.StartsWith("ANFANG-", boundedPrompt, StringComparison.Ordinal);
        Assert.EndsWith("-ENDE", boundedPrompt, StringComparison.Ordinal);
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

    private static async Task<SqliteConnection> OpenDatabaseAsync(TestEnvironment environment)
    {
        var connection = new SqliteConnection($"Data Source={environment.Get<IGoDatabase>().DatabasePath}");
        await connection.OpenAsync();
        return connection;
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

}
