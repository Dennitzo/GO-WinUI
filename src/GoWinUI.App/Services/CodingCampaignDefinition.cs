using GoWinUI.Core.Models;
using System.Text.Json.Serialization;

namespace GoWinUI.App.Services;

public sealed record CodingCampaignDescriptor(
    string Id,
    string Title,
    string Description,
    string Category,
    IReadOnlyList<string> ArtifactDirectories);

public sealed record CodingCampaignValidationResult(
    bool IsValid,
    IReadOnlyList<string> Issues,
    IReadOnlyList<CodingProofVerificationResult> Proofs)
{
    public static CodingCampaignValidationResult Success { get; } = new(true, [], []);
}

public interface ICodingCampaignDefinition
{
    CodingCampaignDescriptor Descriptor { get; }
    bool PublishSolutionsOnlyAfterValidation => false;
    bool HasFoundation(string workspacePath);
    int ReadIteration(string workspacePath);
    string GetChallenge(int iteration);
    string BuildBootstrapPrompt();
    string BuildIterationPrompt(int iteration, string challenge);
    string BuildCorrectionPrompt(int iteration, string challenge, IReadOnlyList<string> issues);
    Task<CodingCampaignValidationResult> ValidateAsync(string workspacePath, CancellationToken cancellationToken = default);
    IReadOnlySet<string>? GetPublishableSolutionDocuments(string workspacePath) => null;
    string GetSolutionPublicationHeading(string workspacePath, string relativePath, string fallbackHeading) => fallbackHeading;
}

public sealed class CodingCampaignCatalog(IEnumerable<ICodingCampaignDefinition> definitions)
{
    private readonly IReadOnlyDictionary<string, ICodingCampaignDefinition> _definitions = definitions
        .ToDictionary(static item => item.Descriptor.Id, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<CodingCampaignDescriptor> List() => _definitions.Values
        .Select(static item => item.Descriptor)
        .OrderBy(static item => item.Title, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    public ICodingCampaignDefinition GetRequired(string id) =>
        _definitions.TryGetValue(id, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Der Coding-Workflow „{id}“ ist nicht bekannt.");
}

[JsonConverter(typeof(JsonStringEnumConverter<CodingProofKind>))]
public enum CodingProofKind
{
    Symbolic,
    IntervalCertified,
    Formal,
    NumericalEvidence,
}

public sealed record CodingProofVerificationResult(
    string CaseId,
    string ManifestPath,
    CodingProofKind Kind,
    bool IsProof,
    bool Passed,
    string Detail);
