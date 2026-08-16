using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace GoAi.Server.Tests;

public sealed class DpapiSecretStoreTests
{
    [Fact]
    public async Task StoredProviderSecretsExposeOnlyPresenceAndCanBeDeleted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"go-ai-secrets-{Guid.NewGuid():N}");
        try
        {
            var options = Options.Create(new GoAiServerOptions
            {
                DataDirectory = root,
                ProviderDataDirectory = root,
            });
            var store = new DpapiSecretStore(options);

            Assert.False(store.HasLmStudioToken);
            Assert.False(store.HasYouTubeApiKey);

            await store.SaveLmStudioTokenAsync("lm-studio-secret");
            await store.SaveYouTubeApiKeyAsync("youtube-secret");

            Assert.True(store.HasLmStudioToken);
            Assert.True(store.HasYouTubeApiKey);
            Assert.Equal("lm-studio-secret", await store.ReadLmStudioTokenAsync());
            Assert.True(await store.ValidateLmStudioTokenAsync("lm-studio-secret"));
            Assert.False(await store.ValidateLmStudioTokenAsync("lm-studio-secret-wrong"));
            Assert.Equal("youtube-secret", await store.ReadYouTubeApiKeyAsync());

            store.DeleteLmStudioToken();
            store.DeleteYouTubeApiKey();

            Assert.False(store.HasLmStudioToken);
            Assert.False(store.HasYouTubeApiKey);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GatewayAcceptsTheConfiguredLmStudioTokenAsClientCredential()
    {
        var root = Path.Combine(Path.GetTempPath(), $"go-ai-lm-auth-{Guid.NewGuid():N}");
        try
        {
            using var context = new TestServerContext();
            var secretStore = new DpapiSecretStore(Options.Create(new GoAiServerOptions
            {
                DataDirectory = root,
                ProviderDataDirectory = root,
            }));
            await secretStore.SaveLmStudioTokenAsync("shared-lm-studio-token");

            var reachedGateway = false;
            var middleware = new ApiKeyAuthenticationMiddleware(_ =>
            {
                reachedGateway = true;
                return Task.CompletedTask;
            });
            var request = new DefaultHttpContext();
            request.Request.Path = "/v1/capabilities";
            request.Request.Headers[GoAiHeaders.ApiKey] = "shared-lm-studio-token";

            await middleware.InvokeAsync(request, new ApiKeyStore(context.Database), secretStore);

            Assert.True(reachedGateway);
            Assert.NotEqual(StatusCodes.Status401Unauthorized, request.Response.StatusCode);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
