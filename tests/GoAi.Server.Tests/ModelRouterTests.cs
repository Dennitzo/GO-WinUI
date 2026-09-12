using GoAi.Contracts;
using GoAi.Server.Core.Runs;

namespace GoAi.Server.Tests;

public sealed class ModelRouterTests
{
    [Theory]
    [InlineData(RunMode.Auto)]
    [InlineData(RunMode.General)]
    public void EveryConversationModeRoutesToGeneral(RunMode mode)
    {
        using var context = new TestServerContext();
        var router = new ModelRouter(context.WrappedOptions);

        var selection = router.Select(CreateRequest(mode, "Projektdatei analysieren und Fehler beheben"));

        Assert.Equal("general", selection.Role);
        Assert.Equal(context.Options.GeneralModelId, selection.ModelId);
        Assert.Equal(context.Options.GeneralContextLength, selection.ContextLength);
    }

    [Fact]
    public void AutoDoesNotInferAnotherRoleFromCodeLikeAttachmentsOrCapabilities()
    {
        using var context = new TestServerContext();
        var router = new ModelRouter(context.WrappedOptions);
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Auto,
            [new RunMessage("user", [new ContentPart("file", FileName: "MainWindow.xaml")])],
            ClientCapabilities: ["documentIo"]);

        var selection = router.Select(request);

        Assert.Equal("general", selection.Role);
        Assert.Equal(context.Options.GeneralModelId, selection.ModelId);
    }

    [Fact]
    public void GeneralModeHonorsThePersistedClientSelection()
    {
        using var context = new TestServerContext();
        var router = new ModelRouter(context.WrappedOptions);
        var request = CreateRequest(RunMode.General, "Rekursion erklären") with
        {
            PreferredGeneralModelId = "gpt-oss-120b",
        };

        var selection = router.Select(request);

        Assert.Equal("general", selection.Role);
        Assert.Equal("gpt-oss-120b", selection.ModelId);
    }

    private static RunRequest CreateRequest(RunMode mode, string text) => new(
        GoAiProtocol.Version,
        mode,
        [new RunMessage("user", [new ContentPart("text", text)])]);
}
