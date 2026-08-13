namespace GoAi.Contracts;

public sealed record UploadManifest(
    string FileName,
    string MediaType,
    long Length,
    string Sha256,
    int ChunkSize = GoAiProtocol.UploadChunkSize,
    int? ChunkCount = null);

public sealed record UploadCreated(
    string UploadId,
    int ChunkSize,
    int ChunkCount,
    IReadOnlyList<int> ReceivedChunks,
    DateTimeOffset ExpiresAt);

public sealed record UploadChunkReceipt(
    string UploadId,
    int Index,
    string Sha256,
    long Length,
    bool Accepted);

public sealed record UploadCompleted(
    string UploadId,
    string FileName,
    string MediaType,
    long Length,
    string Sha256,
    DateTimeOffset ExpiresAt);

public sealed record ArtifactDescriptor(
    string ArtifactId,
    string FileName,
    string MediaType,
    long Length,
    string Sha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, string>? Metadata = null);
