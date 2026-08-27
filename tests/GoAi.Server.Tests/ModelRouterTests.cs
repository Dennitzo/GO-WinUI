using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
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
        Assert.Equal(CodingModelCatalog.DefaultModelId, context.Options.CodeModelId);
        Assert.Equal(context.Options.CodeModelId, code.ModelId);
    }

    [Fact]
    public void ExplicitCodeModeUsesTheOnlyConfiguredModel()
    {
        using var context = new TestServerContext();
        var router = new ModelRouter(context.WrappedOptions);
        var request = CreateRequest(RunMode.Code, "Projekt analysieren") with
        {
            PreferredCodeModelId = "gpt-oss-120b",
        };

        var selection = router.Select(request);

        Assert.Equal("code", selection.Role);
        Assert.Equal("gpt-oss-120b", selection.ModelId);
    }

    [Fact]
    public void DefaultCodeModeUsesQwenAtItsNativeMaximumContext()
    {
        using var context = new TestServerContext();
        var router = new ModelRouter(context.WrappedOptions);

        var selection = router.Select(CreateRequest(RunMode.Code, "Projekt analysieren"));

        Assert.Equal("code", selection.Role);
        Assert.Equal(CodingModelCatalog.DefaultModelId, selection.ModelId);
        Assert.Equal(262_144, selection.ContextLength);
    }

    [Fact]
    public void GeneralAndCodeShareGptOssWithoutChangingRoles()
    {
        using var context = new TestServerContext();
        var router = new ModelRouter(context.WrappedOptions);
        var request = CreateRequest(RunMode.Code, "Behebe den Fehler im Projekt.") with
        {
            PreferredCodeModelId = "gpt-oss-120b",
        };

        var selection = router.Select(request);

        Assert.Equal("code", selection.Role);
        Assert.Equal("gpt-oss-120b", selection.ModelId);
        Assert.Equal(131_072, selection.ContextLength);
    }

    [Fact]
    public void AutoRoutesCodeAttachmentToSharedCodingModel()
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

    [Fact]
    public void ExplicitGeneralModeHonorsThePersistedClientSelection()
    {
        using var context = new TestServerContext();
        var router = new ModelRouter(context.WrappedOptions);
        var request = CreateRequest(RunMode.General, "TGA erklären") with
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
