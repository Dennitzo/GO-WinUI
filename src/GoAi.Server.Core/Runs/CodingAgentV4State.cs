using GoAi.Contracts;

namespace GoAi.Server.Core.Runs;

public sealed record CodingAgentV4State(
    string Goal,
    string SessionBrief,
    CodingStepSnapshot Step,
    IReadOnlyList<string> RequiredOutputPaths,
    IReadOnlyList<string> MutatedPaths,
    IReadOnlyList<string> VerifiedPaths,
    IReadOnlyList<string> RecentResults,
    bool MutationRequested,
    bool ResearchRequested,
    bool ResearchExplicitlyRequired,
    IReadOnlyList<string> ResearchSources,
    IReadOnlyList<string> ResearchPhrases,
    IReadOnlyList<string> AttemptedResearchKeys,
    string SearchSummary = "",
    ResearchBrief? ResearchBrief = null,
    string? CurrentFilePath = null,
    bool CurrentFileKnownToExist = false,
    string? LastObservation = null,
    string? LastError = null,
    string? BlockedReason = null,
    int InspectionCount = 0,
    int MutationCount = 0,
    int VerificationCount = 0,
    string VisibleMessage = "",
    int VisibleDeltaCount = 0,
    bool FinalMessageStarted = false);
