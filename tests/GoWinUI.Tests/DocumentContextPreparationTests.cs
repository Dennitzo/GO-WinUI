using GoWinUI.App.Services;
using GoWinUI.Core.Models;

namespace GoWinUI.Tests;

public sealed class DocumentContextPreparationTests
{
    [Fact]
    public void SerializedEvidenceHonorsModelBudgetIncludingDocumentHeaders()
    {
        const string prompt = "Erstelle mir eine Anleitung für XREF Vorlage";
        var maximumCharacters = DocumentContextPreparationService.CalculatePreparationCandidateCharacters(
            131_072,
            prompt);
        var hits = Enumerable.Range(1, 100)
            .Select(index => new DocumentContextHit(
                Guid.NewGuid(),
                new string('a', 64),
                $"Sehr-aussagekräftiger-Dokumentname-{index:000}.pdf",
                index,
                new string((char)('a' + index % 20), 4_000),
                1d - index / 1_000d,
                $"chunk-{index}"))
            .ToArray();

        var serialized = DocumentContextPreparationService.BuildEvidenceBlocks(hits, maximumCharacters);

        Assert.InRange(serialized.Length, 1, maximumCharacters);
        Assert.StartsWith("[GO_DOCUMENT_EVIDENCE_CANDIDATES]", serialized, StringComparison.Ordinal);
        Assert.EndsWith("[ENDE_GO_DOCUMENT_EVIDENCE_CANDIDATES]\n", serialized, StringComparison.Ordinal);
        Assert.Contains("Dokument:", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Dokumentname-100.pdf", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceBudgetLeavesRoomForPolicyOutputAndSafetyReserve()
    {
        var maximumCharacters = DocumentContextPreparationService.CalculatePreparationCandidateCharacters(
            131_072,
            "Erstelle mir eine Anleitung für XREF Vorlage");

        Assert.InRange(maximumCharacters, 300_000, 330_000);
        var estimatedEvidenceTokens = (maximumCharacters + 2) / 3;
        Assert.True(estimatedEvidenceTokens + 8_192 + 8_192 + 6_144 < 131_072);
    }

    [Theory]
    [InlineData(4_096, 8_000)]
    [InlineData(16_384, 16_384)]
    public void PreparedDossierLimitLeavesRoomForOriginalEvidence(
        int documentBudget,
        int expectedLimit)
    {
        Assert.Equal(
            expectedLimit,
            DocumentContextPreparationService.CalculatePreparedDossierLimit(documentBudget));
    }
}
