using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Security;
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
}
