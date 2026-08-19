using CommunityToolkit.Mvvm.ComponentModel;
using GoWinUI.Core.Models;

namespace GoWinUI.App.ViewModels;

public sealed partial class PromptTriggerEditorItem : ObservableObject
{
    private PromptTriggerAction _savedAction;
    private string _savedPhrase;
    private string _savedDescription;
    private bool _savedIsEnabled;

    public static IReadOnlyList<PromptTriggerActionOption> AvailableActions { get; } =
        Enum.GetValues<PromptTriggerAction>()
            .Select(value => new PromptTriggerActionOption(value, GetActionDisplayName(value)))
            .ToArray();

    public PromptTriggerEditorItem(PromptTrigger source)
    {
        Id = source.Id;
        MatchMode = source.MatchMode;
        Priority = source.Priority;
        Revision = source.Revision;
        CreatedAt = source.CreatedAt;
        Action = source.Action;
        Phrase = source.Phrase;
        Description = source.Description;
        IsEnabled = source.IsEnabled;
        _savedAction = source.Action;
        _savedPhrase = source.Phrase;
        _savedDescription = source.Description;
        _savedIsEnabled = source.IsEnabled;
    }

    public Guid Id { get; }
    public PromptTriggerMatchMode MatchMode { get; }
    public int Priority { get; }
    public long Revision { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public bool IsNew => Revision == 0;
    public bool IsDirty => IsNew
        || Action != _savedAction
        || !string.Equals(Phrase, _savedPhrase, StringComparison.Ordinal)
        || !string.Equals(Description, _savedDescription, StringComparison.Ordinal)
        || IsEnabled != _savedIsEnabled;

    public string ActionDisplayName => GetActionDisplayName(Action);

    public IReadOnlyList<PromptTriggerActionOption> ActionOptions { get; } = AvailableActions;

    public PromptTriggerActionOption? SelectedActionOption
    {
        get => AvailableActions.FirstOrDefault(option => option.Value == Action);
        set
        {
            if (value is not null && value.Value != Action)
            {
                Action = value.Value;
            }
        }
    }

    [ObservableProperty]
    public partial PromptTriggerAction Action { get; set; }

    [ObservableProperty]
    public partial string Phrase { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    partial void OnActionChanged(PromptTriggerAction value)
    {
        OnPropertyChanged(nameof(ActionDisplayName));
        OnPropertyChanged(nameof(SelectedActionOption));
    }

    public PromptTrigger ToModel() => new(
        Id,
        Action,
        Phrase,
        Description,
        MatchMode,
        IsEnabled,
        Priority,
        Revision,
        CreatedAt,
        DateTimeOffset.UtcNow);

    public void ApplySaved(PromptTrigger saved)
    {
        Revision = saved.Revision;
        Action = saved.Action;
        Phrase = saved.Phrase;
        Description = saved.Description;
        IsEnabled = saved.IsEnabled;
        _savedAction = saved.Action;
        _savedPhrase = saved.Phrase;
        _savedDescription = saved.Description;
        _savedIsEnabled = saved.IsEnabled;
        OnPropertyChanged(nameof(IsNew));
    }

    public static string GetActionDisplayName(PromptTriggerAction value) => value switch
    {
        PromptTriggerAction.ImageGeneration => "Bild generieren",
        PromptTriggerAction.Translation => "Übersetzen",
        PromptTriggerAction.TextToSpeech => "Vorlesen",
        PromptTriggerAction.Transcription => "Audio transkribieren",
        PromptTriggerAction.AudioAnalysis => "Audio analysieren",
        PromptTriggerAction.VideoAnalysis => "Video analysieren",
        PromptTriggerAction.ImageAnalysis => "Bild analysieren",
        PromptTriggerAction.WebSearch => "Websuche",
        PromptTriggerAction.YouTubeSearch => "YouTube-Suche",
        PromptTriggerAction.BricsCad => "BricsCAD",
        PromptTriggerAction.Code => "Code / Laguna",
        PromptTriggerAction.VoiceInput => "Sprachsteuerung",
        PromptTriggerAction.LiveCaptions => "Live-Untertitel",
        PromptTriggerAction.LiveTranslation => "Live-Übersetzung",
        PromptTriggerAction.Audiobook => "Hörbuch erstellen",
        _ => value.ToString(),
    };
}

public sealed record PromptTriggerActionOption(PromptTriggerAction Value, string Label);

public sealed record PromptTriggerCategoryFilterOption(PromptTriggerAction? Value, string Label);
