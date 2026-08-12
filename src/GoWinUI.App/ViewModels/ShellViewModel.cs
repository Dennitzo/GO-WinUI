using CommunityToolkit.Mvvm.ComponentModel;
using System.Globalization;

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

    [ObservableProperty]
    public partial string RecentActivityText { get; set; } = "Noch keine Aktivität";

    [ObservableProperty]
    public partial string RecentActivityTimeText { get; set; } = "Deine nächsten Schritte erscheinen hier.";

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

    public void SetRecentActivity(string? description, DateTimeOffset? occurredAt)
    {
        if (string.IsNullOrWhiteSpace(description) || occurredAt is null)
        {
            RecentActivityText = "Noch keine Aktivität";
            RecentActivityTimeText = "Deine nächsten Schritte erscheinen hier.";
            return;
        }

        RecentActivityText = description;
        RecentActivityTimeText = FormatActivityTime(occurredAt.Value);
    }

    internal static string FormatActivityTime(DateTimeOffset occurredAt)
    {
        var local = occurredAt.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        var prefix = local.Date == today
            ? "Heute"
            : local.Date == today.AddDays(-1)
                ? "Gestern"
                : local.Year == today.Year
                    ? local.ToString("dd.MM.", CultureInfo.CurrentCulture)
                    : local.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);
        return $"{prefix}, {local.ToString("HH:mm", CultureInfo.CurrentCulture)} Uhr";
    }

}
