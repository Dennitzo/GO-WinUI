using GoAi.Client;
using GoAi.Contracts;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class GoAiClientStreamLivenessTests
{
    [Fact]
    public async Task CancellationBetweenBufferedEventsIsNotReportedAsNormalCompletion()
    {
        using var stop = new CancellationTokenSource();
        var item = new RunEvent(4322, "existing-run", RunEventTypes.ModelGeneration,
            DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { state = "running" }));
        var bytes = Encoding.UTF8.GetBytes("data: " + JsonSerializer.Serialize(item, GoAiProtocol.CreateJsonOptions()) + "\n\n");
        using var http = new HttpClient(new StreamHandler(new MemoryStream(bytes))) { BaseAddress = new Uri("http://localhost/") };
        using var client = new GoAiClient(http);
        await using var events = client.StreamRunEventsAsync("existing-run", 4321, stop.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await events.MoveNextAsync());
    }

    [Fact]
    public async Task MissingHeartbeatDisconnectsWithoutCancellingTheRemoteJob()
    {
        using var http = new HttpClient(new StreamHandler(new HeartbeatStream(false))) { BaseAddress = new Uri("http://localhost/") };
        using var client = new GoAiClient(http, streamIdleTimeout: TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var _ in client.StreamRunEventsAsync("existing-run", 4321)) { }
        });
    }

    [Fact]
    public async Task HeartbeatsKeepConnectionAliveAcrossManyIdleWindowsAndUserCancellationStillWorks()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(650));
        var stream = new HeartbeatStream(true);
        using var http = new HttpClient(new StreamHandler(stream)) { BaseAddress = new Uri("http://localhost/") };
        using var client = new GoAiClient(http, streamIdleTimeout: TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.StreamRunEventsAsync("existing-run", 4321, stop.Token)) { }
        });
        Assert.True(stream.ReadCount >= 4);
    }

    private sealed class StreamHandler(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/existing-run/events", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal("4321", Assert.Single(request.Headers.GetValues("Last-Event-ID")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        }
    }

    private sealed class HeartbeatStream(bool emitHeartbeat) : Stream
    {
        public int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(emitHeartbeat ? 40 : Timeout.Infinite, cancellationToken);
            ReadCount++;
            return Encoding.UTF8.GetBytes(": keep-alive\n\n", buffer.Span);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
