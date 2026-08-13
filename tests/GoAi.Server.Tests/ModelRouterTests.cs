using GoAi.Contracts;
using GoAi.Server.Core.Runs;

namespace GoAi.Server.Tests;

public sealed class ModelRouterTests
{
    [Fact]
    public void ExplicitModesNeverFallback()
    {
        using var context = new TestServerContext();
        var router = new ModelRouter(context.WrappedOptions);

        var general = router.Select(CreateRequest(RunMode.General, "Code debuggen"));
        var code = router.Select(CreateRequest(RunMode.Code, "TGA erklären"));

        Assert.Equal(context.Options.GeneralModelId, general.ModelId);
        Assert.Equal(context.Options.CodeModelId, code.ModelId);
    }

    [Fact]
    public void AutoRoutesCodeAttachmentToLaguna()
    {
        using var context = new TestServerContext();
        var router = new ModelRouter(context.WrappedOptions);
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Auto,
            [new RunMessage("user", [new ContentPart("file", FileName: "MainWindow.xaml")])],
            ClientCapabilities: ["code"]);

        var selection = router.Select(request);

        Assert.Equal("code", selection.Role);
        Assert.Equal(context.Options.CodeModelId, selection.ModelId);
    }

    private static RunRequest CreateRequest(RunMode mode, string text) => new(
        GoAiProtocol.Version,
        mode,
        [new RunMessage("user", [new ContentPart("text", text)])]);
}
