using GoAi.Server.Core.Runtime;

namespace GoAi.Server.Tests;

public sealed class ServerRuntimeStateTests
{
    [Fact]
    public void DiagnosticObserverFailureDoesNotInterruptServerWork()
    {
        var runtime = new ServerRuntimeState();
        var changedObserved = false;
        var logObserved = false;

        runtime.Changed += static (_, _) => throw new InvalidOperationException("UI observer failed.");
        runtime.Changed += (_, _) => changedObserved = true;
        runtime.LogAdded += static (_, _) => throw new InvalidOperationException("UI observer failed.");
        runtime.LogAdded += (_, _) => logObserved = true;

        runtime.SetGatewayState("Bereit", "Test");
        runtime.WriteLog("Information", "test.event", "Test");

        Assert.True(changedObserved);
        Assert.True(logObserved);
        Assert.Single(runtime.GetLogs());
    }

    [Fact]
    public void PersistedSanitizedLogsCanBeReadByAnotherDashboardProcess()
    {
        using var context = new TestServerContext();
        var writer = new ServerRuntimeState(context.WrappedOptions);
        writer.WriteLog("Information", "run.completed", "Run run-test erfolgreich beendet.");

        var reader = new ServerRuntimeState(context.WrappedOptions);
        var entry = Assert.Single(reader.GetLogs());

        Assert.Equal("run.completed", entry.EventId);
        Assert.Equal("Run run-test erfolgreich beendet.", entry.Message);
        Assert.True(File.Exists(Path.Combine(context.Options.LogDirectory, "server-events.jsonl")));
    }
}
