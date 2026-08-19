using CommunityToolkit.Mvvm.ComponentModel;
using GoAi.Contracts;
using System.Collections.ObjectModel;
using System.Globalization;

namespace GoWinUI.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    private const string GeneralService = "general";
    private const string CodingService = "coding";
    private const string SpeechToTextService = "speech-to-text";
    private const string TextToSpeechService = "text-to-speech";
    private const string VisionMediaService = "vision-media";
    private const string ImageService = "image-generation";
    private const string ResearchService = "research";
    private readonly Dictionary<string, string> _activeClientServices = new(StringComparer.Ordinal);
    private GpuStatusSnapshot? _gpuStatus;
    private ModelStatusSnapshot? _modelStatus;
    private IReadOnlyList<ServiceStatusSnapshot> _serviceStatus = [];
    private bool _gatewayReachable;

    public ShellViewModel()
    {
        AiServices.Add(new(GeneralService, "General AI", "gpt-oss-20b · LM Studio", "\uE950", "General AI - gpt-oss-20b", false, false, "Wird geprüft"));
        AiServices.Add(new(CodingService, "Coding", "Laguna-S-2.1 · LM Studio", "\uE943", "Coding - Laguna-S-2.1", false, false, "Wird geprüft"));
        AiServices.Add(new(SpeechToTextService, "Spracherkennung", "Whisper large-v3 · Docker", "\uE720", "Spracherkennung - Whisper large-v3", false, false, "Wird geprüft"));
        AiServices.Add(new(TextToSpeechService, "Sprachausgabe", "Supertonic F5 Ultra · GPU 1", "\uE767", "Sprachausgabe - Supertonic F5 Ultra", false, false, "Wird geprüft"));
        AiServices.Add(new(VisionMediaService, "Vision / Medien", "Qwen3-VL + Media Worker", "\uE722", "Vision / Medien - Qwen3-VL", false, false, "Wird geprüft"));
        AiServices.Add(new(ImageService, "Bildgenerierung", "Z-Image-Turbo · Docker", "\uE8B9", "Bildgenerierung - Z-Image-Turbo", false, false, "Wird geprüft"));
        AiServices.Add(new(ResearchService, "Web / YouTube", "SearXNG / YouTube API", "\uE721", "Web / YouTube - SearXNG / YouTube API", false, false, "Wird geprüft"));
    }

    [ObservableProperty]
    public partial string ActivePageTitle { get; set; } = "AI Assistent";

    [ObservableProperty]
    public partial string DatabaseStatus { get; set; } = "Datenbank wird vorbereitet";

    [ObservableProperty]
    public partial bool IsAiRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiConnectionModeText))]
    public partial bool IsAiAvailable { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiConnectionModeText))]
    public partial bool IsAiConnectionEnabled { get; set; }

    public string AiConnectionModeText => !IsAiConnectionEnabled
        ? "Offline"
        : IsAiAvailable
            ? "Online"
            : "Nicht erreichbar";

    public ObservableCollection<AiServiceFooterItem> AiServices { get; } = [];

    public bool IsAnyAiRunning => AiServices.Any(static service => service.IsBusy);

    [ObservableProperty]
    public partial string RecentActivityText { get; set; } = "Noch keine Aktivität";

    [ObservableProperty]
    public partial string RecentActivityTimeText { get; set; } = "Deine nächsten Schritte erscheinen hier.";

    partial void OnIsAiRunningChanged(bool value)
    {
        RefreshAiServices();
        OnPropertyChanged(nameof(IsAnyAiRunning));
    }

    partial void OnIsAiAvailableChanged(bool value)
    {
        _gatewayReachable = value;
        RefreshAiServices();
    }

    public void SetActiveAiRuns(GpuStatusSnapshot? status)
    {
        _gpuStatus = status;
        RefreshAiServices();
    }

    public void SetAiServiceAvailability(
        bool gatewayReachable,
        ModelStatusSnapshot? modelStatus,
        IReadOnlyList<ServiceStatusSnapshot>? serviceStatus)
    {
        _gatewayReachable = gatewayReachable;
        _modelStatus = modelStatus;
        _serviceStatus = serviceStatus ?? [];
        RefreshAiServices();
    }

    public void ApplyAiAvailabilitySnapshot(
        bool gatewayReachable,
        GpuStatusSnapshot? gpuStatus,
        ModelStatusSnapshot? modelStatus,
        IReadOnlyList<ServiceStatusSnapshot>? serviceStatus)
    {
        // A running request already has an established server connection. Do not
        // replace that stronger signal with a transiently failed parallel status poll.
        if (!gatewayReachable && IsAiConnectionEnabled && IsAiRunning && IsAiAvailable)
        {
            return;
        }

        IsAiAvailable = gatewayReachable;
        SetAiServiceAvailability(gatewayReachable, modelStatus, serviceStatus);
        SetActiveAiRuns(gpuStatus);
    }

    public void SetClientAiRun(
        string key,
        bool isActive,
        string displayName,
        string runtime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var serviceKey = MapClientService(key);
        if (serviceKey is null)
        {
            return;
        }
        if (isActive) _activeClientServices[key] = serviceKey;
        else _ = _activeClientServices.Remove(key);
        RefreshAiServices();
    }

    private void RefreshAiServices()
    {
        var active = new HashSet<string>(_activeClientServices.Values, StringComparer.Ordinal);
        foreach (var workload in _gpuStatus?.ActiveWorkloads ?? [])
        {
            if (MapWorkload(workload.Workload) is { } serviceKey) _ = active.Add(serviceKey);
        }
        if (active.Count == 0 && IsAiRunning)
        {
            _ = active.Add(GeneralService);
        }
        else if (active.Count == 0 && !string.IsNullOrWhiteSpace(_gpuStatus?.ActiveLease))
        {
            _ = active.Add(GeneralService);
        }

        var waiting = IsAiRunning && active.Count == 0 || (_gpuStatus?.QueueLength ?? 0) > 0;
        for (var index = 0; index < AiServices.Count; index++)
        {
            var current = AiServices[index];
            var reachable = IsServiceReachable(current.Key);
            var isActive = active.Contains(current.Key);
            var isWaiting = waiting && string.Equals(current.Key, GeneralService, StringComparison.Ordinal) && !isActive;
            var state = isActive ? "Aktiv"
                : isWaiting ? "Wartet"
                : reachable ? "Bereit"
                : "Nicht erreichbar";
            var next = current with
            {
                IsActive = isActive,
                IsWaiting = isWaiting,
                IsReachable = reachable,
                StateLabel = state,
            };
            if (next != current) AiServices[index] = next;
        }
        OnPropertyChanged(nameof(IsAnyAiRunning));
    }

    private bool IsServiceReachable(string serviceKey) => serviceKey switch
    {
        GeneralService => IsModelReady("general"),
        CodingService => IsModelReady("code"),
        SpeechToTextService or TextToSpeechService => IsWorkerReady("Speech"),
        VisionMediaService => IsModelReady("vision") && IsWorkerReady("Media Worker"),
        ImageService => IsWorkerReady("Image Worker"),
        ResearchService => IsWorkerReady("SearXNG"),
        _ => false,
    };

    private bool IsModelReady(string role)
    {
        if (!_gatewayReachable) return false;
        if (_modelStatus is null) return string.Equals(role, "general", StringComparison.Ordinal) && IsAiAvailable;
        return _modelStatus.ProviderReachable
            && _modelStatus.Models.Any(model =>
                (string.Equals(model.Role, role, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(role, "vision", StringComparison.OrdinalIgnoreCase)
                    && model.Role.StartsWith("vision", StringComparison.OrdinalIgnoreCase))
                && model.Downloaded
                && !string.Equals(model.State, "error", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(model.State, "missing", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsWorkerReady(string name) => _gatewayReachable && _serviceStatus.Any(service =>
        service.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && service.Reachable);

    private static string? MapClientService(string key) => key switch
    {
        "system-audio-stt" or "microphone-stt" or "microphone-stt-warmup" => SpeechToTextService,
        "microphone-tts" => TextToSpeechService,
        _ => null,
    };

    private static string? MapWorkload(string workload) => workload switch
    {
        "llm-general" or "caption-translation" => GeneralService,
        "llm-code" => CodingService,
        "speech-to-text" or "live-caption" or "live-caption-warmup" => SpeechToTextService,
        "text-to-speech" => TextToSpeechService,
        "vision" or "media-analysis" or "audio-analysis" or "video-audio-fusion" => VisionMediaService,
        "image-generation" => ImageService,
        "web-search" or "youtube-search" or "web-fetch" => ResearchService,
        _ => null,
    };

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

public sealed record AiServiceFooterItem(
    string Key,
    string DisplayName,
    string Runtime,
    string Glyph,
    string ToolTipText,
    bool IsReachable,
    bool IsActive,
    string StateLabel,
    bool IsWaiting = false)
{
    public bool IsBusy => IsActive || IsWaiting;

    public bool IsIdle => !IsBusy;

    public string AutomationLabel => $"{ToolTipText}. {StateLabel}";
}
