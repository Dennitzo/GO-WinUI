using GoAi.Contracts;
using GoAi.Server.Core.Security;
using Microsoft.AspNetCore.Http;
using System.Net;

namespace GoAi.Server.Tests;

public sealed class ApiRateLimitMiddlewareTests
{
    [Fact]
    public async Task RejectsRequestsAfterPerKeyWindowIsExhausted()
    {
        var forwarded = 0;
        var middleware = new ApiRateLimitMiddleware(context =>
        {
            forwarded++;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });

        for (var index = 0; index < 240; index++)
        {
            var accepted = CreateContext("stable-test-key");
            await middleware.InvokeAsync(accepted);
            Assert.Equal(StatusCodes.Status204NoContent, accepted.Response.StatusCode);
        }

        var rejected = CreateContext("stable-test-key");
        await middleware.InvokeAsync(rejected);

        Assert.Equal(240, forwarded);
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.Response.StatusCode);
        Assert.Equal("0", rejected.Response.Headers["X-RateLimit-Remaining"]);
        Assert.False(string.IsNullOrWhiteSpace(rejected.Response.Headers.RetryAfter));
        rejected.Response.Body.Position = 0;
        using var document = await System.Text.Json.JsonDocument.ParseAsync(rejected.Response.Body);
        Assert.Equal("rate_limit.exceeded", document.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task HealthEndpointsAreNotRateLimited()
    {
        var forwarded = 0;
        var middleware = new ApiRateLimitMiddleware(context =>
        {
            forwarded++;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });

        for (var index = 0; index < 300; index++)
        {
            var context = CreateContext("stable-test-key");
            context.Request.Path = "/v1/health/live";
            await middleware.InvokeAsync(context);
            Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        }

        Assert.Equal(300, forwarded);
    }

    private static DefaultHttpContext CreateContext(string apiKey)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Path = "/v1/capabilities";
        context.Request.Headers[GoAiHeaders.ApiKey] = apiKey;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
