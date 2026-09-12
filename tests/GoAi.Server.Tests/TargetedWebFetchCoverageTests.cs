using GoAi.Contracts;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class TargetedWebFetchCoverageTests
{
    private static readonly string[] MissingApiPhrases = ["asyncio.wait_for", "NeverInventThisPhrase"];

    [Fact]
    public void RichRepeatedErrorSectionsCannotCrowdOutOtherRequestedApiPhrases()
    {
        var richError = string.Concat(Enumerable.Repeat("TimeoutError describes the timeout failure. Handle exceptions carefully; errors have defined behavior. ", 18));
        var gap = new string('x', 3_000);
        var content = richError + gap + richError + " extra." + gap
            + "asyncio.wait_for waits for one awaitable." + gap + "asyncio.TaskGroup supervises related tasks.";
        string[] queries = ["TimeoutError", "asyncio.wait_for", "asyncio.TaskGroup"];

        var result = Fetch(content, queries, maximumResults: 3, contextCharacters: 900, maximumCharacters: 3_000);

        Assert.True(result.Found);
        Assert.Equal(3, result.Matches.Count);
        Assert.Empty(result.MissingQueries);
        foreach (var query in queries)
            Assert.Contains(result.Matches, match => match.Text.Contains(query, StringComparison.OrdinalIgnoreCase));
        AssertExactSourceWindows(content, result, maximumCharacters: 3_000);
    }

    [Fact]
    public void RepeatedPhraseWindowsOnlyFollowCoverageOfOtherPossibleQueries()
    {
        var gap = new string('x', 500);
        var content = "TimeoutError: first failure. A detailed description explains several possible errors. " + gap
            + "TimeoutError: second failure. Another description explains this error. " + gap
            + "asyncio.wait_for: one awaitable." + gap + "asyncio.TaskGroup: multiple tasks.";
        string[] queries = ["TimeoutError", "asyncio.wait_for", "asyncio.TaskGroup"];

        var result = Fetch(content, queries, maximumResults: 4, contextCharacters: 100, maximumCharacters: 2_000);

        Assert.Equal(4, result.Matches.Count);
        Assert.Equal(3, result.Matches.Take(3).Select(match => match.Query).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("TimeoutError", result.Matches[3].Query);
        Assert.Empty(result.MissingQueries);
        AssertExactSourceWindows(content, result, maximumCharacters: 2_000);
    }

    [Fact]
    public void OneEmittedWindowCoversEveryRequestedPhraseActuallyInsideIt()
    {
        const string content = "Use asyncio.timeout for a block and asyncio.wait_for for one awaitable; both document cancellation behavior.";
        string[] queries = ["asyncio.timeout", "asyncio.wait_for"];

        var result = Fetch(content, queries, maximumResults: 5, contextCharacters: 100, maximumCharacters: 1_000);

        var match = Assert.Single(result.Matches);
        Assert.Contains("asyncio.timeout", match.Text, StringComparison.Ordinal);
        Assert.Contains("asyncio.wait_for", match.Text, StringComparison.Ordinal);
        Assert.Empty(result.MissingQueries);
        AssertExactSourceWindows(content, result, maximumCharacters: 1_000);
    }

    [Fact]
    public void FinalClippingDoesNotClaimCoverageOfAQueryOutsideTheEmittedText()
    {
        var content = new string('p', 1_400) + " "
            + string.Join(' ', Enumerable.Repeat("PRIMARY", 12)) + " " + new string('q', 1_200)
            + " CLIPPED_QUERY " + new string('r', 2_000);
        string[] queries = ["PRIMARY", "CLIPPED_QUERY"];

        var result = Fetch(content, queries, maximumResults: 1, contextCharacters: 2_000, maximumCharacters: 1_000);

        var match = Assert.Single(result.Matches);
        Assert.Contains("PRIMARY", match.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("CLIPPED_QUERY", match.Text, StringComparison.Ordinal);
        Assert.Equal("CLIPPED_QUERY", Assert.Single(result.MissingQueries));
        AssertExactSourceWindows(content, result, maximumCharacters: 1_000);
    }

    [Fact]
    public void OverlappingContextDoesNotHideAnUncoveredPhraseInItsUnusedTail()
    {
        var content = new string('x', 500) + "ALPHA" + new string('y', 280) + "BRAVO" + new string('z', 600);
        string[] queries = ["ALPHA", "BRAVO"];

        var result = Fetch(content, queries, maximumResults: 2, contextCharacters: 400, maximumCharacters: 1_000);

        Assert.Equal(2, result.Matches.Count);
        Assert.Contains("ALPHA", result.Matches[0].Text, StringComparison.Ordinal);
        Assert.Contains("BRAVO", result.Matches[1].Text, StringComparison.Ordinal);
        Assert.Empty(result.MissingQueries);
        AssertExactSourceWindows(content, result, maximumCharacters: 1_000);
    }

    [Fact]
    public void AClippedLargeContextKeepsItsOwnAnchorAndExactSourceOffsets()
    {
        var content = new string('a', 2_500) + "  UNIQUE_ANCHOR  " + new string('z', 3_000);
        string[] queries = ["UNIQUE_ANCHOR"];

        var result = Fetch(content, queries, maximumResults: 1, contextCharacters: 2_000, maximumCharacters: 1_000);

        var match = Assert.Single(result.Matches);
        Assert.Contains("UNIQUE_ANCHOR", match.Text, StringComparison.Ordinal);
        Assert.True(match.StartCharacter > 1_500);
        Assert.Empty(result.MissingQueries);
        AssertExactSourceWindows(content, result, maximumCharacters: 1_000);
    }

    [Fact]
    public void SimilarWordsCannotInventAnExactRequestedApiPhrase()
    {
        const string content = "TimeoutError indicates failure. This separate example writes asyncio.wait for with a space, not an underscore.";
        string[] queries = ["TimeoutError", "asyncio.wait_for", "NeverInventThisPhrase"];

        var result = Fetch(content, queries, maximumResults: 4, contextCharacters: 100, maximumCharacters: 1_000);

        Assert.True(result.Found);
        Assert.Equal(MissingApiPhrases, result.MissingQueries);
        Assert.All(result.Matches, match =>
        {
            Assert.DoesNotContain("asyncio.wait_for", match.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("NeverInventThisPhrase", match.Text, StringComparison.Ordinal);
        });
        AssertExactSourceWindows(content, result, maximumCharacters: 1_000);
    }

    private static TargetedWebFetchResult Fetch(string content, string[] queries, int maximumResults, int contextCharacters, int maximumCharacters) =>
        AgentToolExecutor.CreateTargetedFetchResult(new WebFetchResponse("https://docs.example.org/async", "text/plain", content,
            IsUntrusted: true, DateTimeOffset.UtcNow, []), JsonSerializer.SerializeToElement(new
        {
            queries, maximumResults, contextCharacters, maximumCharacters,
        }));

    private static void AssertExactSourceWindows(string content, TargetedWebFetchResult result, int maximumCharacters)
    {
        var source = AgentToolExecutor.RemoveResearchBoilerplate(content);
        Assert.True(result.Matches.Sum(match => match.Text.Length) <= maximumCharacters);
        foreach (var match in result.Matches)
        {
            Assert.InRange(match.StartCharacter, 0, source.Length);
            Assert.InRange(match.EndCharacter, match.StartCharacter + 1, source.Length);
            Assert.Equal(source[match.StartCharacter..match.EndCharacter], match.Text);
            Assert.Contains(match.Query, match.Text, StringComparison.OrdinalIgnoreCase);
        }
        var ordered = result.Matches.OrderBy(match => match.StartCharacter).ToArray();
        for (var index = 1; index < ordered.Length; index++)
            Assert.True(ordered[index - 1].EndCharacter <= ordered[index].StartCharacter);
    }
}
