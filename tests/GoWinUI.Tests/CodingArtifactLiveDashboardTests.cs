using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class CodingArtifactLiveDashboardTests
{
    [Fact]
    public async Task DashboardServesAndRefreshesWorkspaceArtifactsWithoutLeavingLoopback()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"go-einstein-dashboard-{Guid.NewGuid():N}");
        var visualizations = Path.Combine(workspace, "visualizations");
        var simulationData = Path.Combine(workspace, "simulation_data");
        var solutions = Path.Combine(workspace, "solutions");
        Directory.CreateDirectory(visualizations);
        Directory.CreateDirectory(simulationData);
        Directory.CreateDirectory(solutions);
        var progressPath = Path.Combine(simulationData, "live_progress.json");
        var plotPath = Path.Combine(visualizations, "live_progress.svg");
        await File.WriteAllTextAsync(
            progressPath,
            """
            {"status":"running","caseId":"schwarzschild","phase":"residuals","step":1,"totalSteps":4,"updatedAt":"2026-08-20T18:00:00Z","metrics":{"maxResidual":0.01}}
            """);
        await File.WriteAllTextAsync(
            plotPath,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"640\" height=\"360\"><text x=\"20\" y=\"30\">Live</text>"
                + new string(' ', 1_200)
                + "</svg>");
        await File.WriteAllTextAsync(
            Path.Combine(solutions, "schwarzschild.md"),
            "# Feldgleichung\n\n$$G_{\\mu\\nu} + \\Lambda g_{\\mu\\nu} = 8\\pi T_{\\mu\\nu}$$");

        try
        {
            var events = new ConcurrentQueue<string>();
            await using (var dashboard = CodingArtifactLiveDashboard.Start(
                workspace,
                (name, _) => events.Enqueue(name),
                CancellationToken.None,
                openBrowser: false))
            {
                Assert.True(IPAddress.IsLoopback(IPAddress.Parse(dashboard.Url.Host)));
                using var client = new HttpClient { BaseAddress = dashboard.Url };
                var html = await client.GetStringAsync("/");
                Assert.Contains("Live-Fortschritt", html, StringComparison.Ordinal);
                Assert.Contains("/assets/katex/katex.min.js", html, StringComparison.Ordinal);
                Assert.Contains("goMarkdown.render", html, StringComparison.Ordinal);
                Assert.Contains("item.path.toLowerCase().endsWith(\".md\")", html, StringComparison.Ordinal);
                Assert.Contains("className = \"module-toggle\"", html, StringComparison.Ordinal);
                Assert.Contains("Maximieren", html, StringComparison.Ordinal);
                Assert.Contains("Verkleinern", html, StringComparison.Ordinal);
                Assert.Contains("event.key === \"Escape\"", html, StringComparison.Ordinal);
                Assert.Contains("article.maximized", html, StringComparison.Ordinal);
                Assert.Contains("captureScrollState", html, StringComparison.Ordinal);
                Assert.Contains("restoreScrollState", html, StringComparison.Ordinal);
                Assert.Contains("surface.scrollTop = position.top", html, StringComparison.Ordinal);
                Assert.Contains("window.scrollTo(viewport.x, viewport.y)", html, StringComparison.Ordinal);

                using (var katex = await client.GetAsync("assets/katex/katex.min.js"))
                {
                    Assert.Equal(HttpStatusCode.OK, katex.StatusCode);
                    Assert.Equal("text/javascript", katex.Content.Headers.ContentType?.MediaType);
                }
                using (var markdown = await client.GetAsync("assets/markdown.js"))
                {
                    Assert.Equal(HttpStatusCode.OK, markdown.StatusCode);
                    Assert.Contains("goMarkdown", await markdown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
                }

                await WaitUntilAsync(() => dashboard.ObservedRevisionCount >= 2, TimeSpan.FromSeconds(4));
                var firstRevision = dashboard.ObservedRevisionCount;
                var stateJson = await client.GetStringAsync("api/state");
                using (var state = JsonDocument.Parse(stateJson))
                {
                    Assert.Equal("schwarzschild", state.RootElement.GetProperty("liveProgress").GetProperty("caseId").GetString());
                    Assert.Contains(
                        state.RootElement.GetProperty("artifacts").EnumerateArray(),
                        item => item.GetProperty("path").GetString() == "visualizations/live_progress.svg");
                }

                using (var artifact = await client.GetAsync(
                    "artifact?path=visualizations%2Flive_progress.svg"))
                {
                    Assert.Equal(HttpStatusCode.OK, artifact.StatusCode);
                    Assert.Equal("image/svg+xml", artifact.Content.Headers.ContentType?.MediaType);
                    Assert.Contains("no-store", artifact.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
                }
                using (var escaped = await client.GetAsync("artifact?path=..%2Foutside.json"))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, escaped.StatusCode);
                }

                await File.WriteAllTextAsync(
                    progressPath,
                    """
                    {"status":"completed","caseId":"schwarzschild","phase":"verified","step":4,"totalSteps":4,"updatedAt":"2026-08-20T18:01:00Z","metrics":{"maxResidual":0.0}}
                    """,
                    Encoding.UTF8);
                await WaitUntilAsync(
                    () => dashboard.ObservedRevisionCount > firstRevision,
                    TimeSpan.FromSeconds(4));
                Assert.Contains("artifact.live.updated", events);
            }
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "Die Liveansicht hat die Dateiänderung nicht rechtzeitig erkannt.");
            await Task.Delay(100);
        }
    }
}
