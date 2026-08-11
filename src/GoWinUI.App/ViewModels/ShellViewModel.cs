using CommunityToolkit.Mvvm.ComponentModel;

namespace GoWinUI.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string ActivePageTitle { get; set; } = "AI Assistent";

    [ObservableProperty]
    public partial string DatabaseStatus { get; set; } = "Datenbank wird vorbereitet";

    [ObservableProperty]
    public partial bool IsAiRunning { get; set; }

    [ObservableProperty]
    public partial bool IsAiAvailable { get; set; }

    public string AiRunStateLabel => IsAiRunning
        ? "Arbeitet"
        : IsAiAvailable
            ? "Bereit"
            : "Nicht bereit";

    public string AiRunDetail => IsAiRunning
        ? "Lokaler AI-Lauf aktiv"
        : IsAiAvailable
            ? "Lokale AI per API erreichbar"
            : "Lokale AI per API nicht erreichbar";

    partial void OnIsAiRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(AiRunStateLabel));
        OnPropertyChanged(nameof(AiRunDetail));
    }

    partial void OnIsAiAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(AiRunStateLabel));
        OnPropertyChanged(nameof(AiRunDetail));
    }

}
