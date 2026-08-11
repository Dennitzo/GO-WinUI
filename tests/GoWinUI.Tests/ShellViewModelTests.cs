using GoWinUI.App.ViewModels;

namespace GoWinUI.Tests;

public sealed class ShellViewModelTests
{
    [Fact]
    public void AiRunFooterReflectsApiAvailabilityAndRunningState()
    {
        var viewModel = new ShellViewModel();

        Assert.Equal("Nicht bereit", viewModel.AiRunStateLabel);
        Assert.Equal("Lokale AI per API nicht erreichbar", viewModel.AiRunDetail);

        viewModel.IsAiAvailable = true;

        Assert.Equal("Bereit", viewModel.AiRunStateLabel);
        Assert.Equal("Lokale AI per API erreichbar", viewModel.AiRunDetail);

        viewModel.IsAiRunning = true;

        Assert.Equal("Arbeitet", viewModel.AiRunStateLabel);
        Assert.Equal("Lokaler AI-Lauf aktiv", viewModel.AiRunDetail);
    }
}
