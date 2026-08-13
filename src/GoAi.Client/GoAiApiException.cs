using GoAi.Contracts;

namespace GoAi.Client;

public sealed class GoAiApiException : HttpRequestException
{
    public GoAiApiException(string message, int statusCode, GoAiProblem? problem = null)
        : base(message, null, (System.Net.HttpStatusCode)statusCode)
    {
        Problem = problem;
    }

    public GoAiProblem? Problem { get; }
}
