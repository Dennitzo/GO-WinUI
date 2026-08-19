namespace GoAi.Contracts;

public sealed record EmbeddingBatchRequest(
    IReadOnlyList<EmbeddingInput> Inputs,
    bool KeepModelLoaded = false);

public sealed record EmbeddingInput(string Id, string Text);

public sealed record EmbeddingBatchResponse(
    string ModelId,
    int Dimensions,
    IReadOnlyList<EmbeddingVector> Vectors);

public sealed record EmbeddingVector(string Id, IReadOnlyList<double> Values);
