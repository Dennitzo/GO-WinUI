using CommunityToolkit.Mvvm.ComponentModel;

namespace GoWinUI.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string ActivePageTitle { get; set; } = "AI Assistent";

    [ObservableProperty]
    public partial string DatabaseStatus { get; set; } = "Wird vorbereitet";

    [ObservableProperty]
    public partial string LmStudioStatus { get; set; } = "Nicht verbunden";

    [ObservableProperty]
    public partial bool IsAiRunning { get; set; }
}
