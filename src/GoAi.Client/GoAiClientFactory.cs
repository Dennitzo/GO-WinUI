namespace GoAi.Client;

public static class GoAiClientFactory
{
    public static GoAiClient Create(GoAiClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ServerUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("GO AI supports HTTP or HTTPS server addresses.", nameof(options));
        }
        var httpClient = new HttpClient(
            new HttpClientHandler { UseProxy = false },
            disposeHandler: true)
        {
            BaseAddress = EnsureTrailingSlash(options.ServerUri),
            Timeout = options.RequestTimeout ?? TimeSpan.FromMinutes(20),
        };
        return new GoAiClient(httpClient, options.ClientId, ownsHttpClient: true);
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        var text = uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + "/";
        return new Uri(text, UriKind.Absolute);
    }
}
