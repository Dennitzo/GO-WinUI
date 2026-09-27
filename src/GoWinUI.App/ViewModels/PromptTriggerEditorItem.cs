using CommunityToolkit.Mvvm.ComponentModel;
using GoWinUI.Core.Extensions;
using GoWinUI.Core.Models;

namespace GoWinUI.App.ViewModels;

public sealed partial class PromptTriggerEditorItem : ObservableObject
{
    private PromptTriggerAction _savedAction;
    private string _savedPhrase;
    private string _savedDescription;
    private bool _savedIsEnabled;
    private string _savedExtensionActionId;

    public static IReadOnlyList<PromptTriggerActionOption> AvailableActions { get; } =
        PromptActionExtensionIds.EditableBuiltInActions
            .Select(value => new PromptTriggerActionOption(
                value,
                GetActionDisplayName(value),
                PromptActionExtensionIds.FromPromptAction(value)))
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
        ExtensionActionId = source.ExtensionActionId
            ?? (source.Action == PromptTriggerAction.Extension
                ? string.Empty
                : PromptActionExtensionIds.FromPromptAction(source.Action));
        ActionOptions = source.Action == PromptTriggerAction.Extension
            && !string.IsNullOrWhiteSpace(ExtensionActionId)
                ? [new PromptTriggerActionOption(
                    PromptTriggerAction.Extension,
                    $"Erweiterung: {ExtensionActionId}",
                    ExtensionActionId), .. AvailableActions]
                : AvailableActions;
        _savedAction = source.Action;
        _savedPhrase = source.Phrase;
        _savedDescription = source.Description;
        _savedIsEnabled = source.IsEnabled;
        _savedExtensionActionId = ExtensionActionId;
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
        || !string.Equals(ExtensionActionId, _savedExtensionActionId, StringComparison.Ordinal)
        || IsEnabled != _savedIsEnabled;

    public string ActionDisplayName => GetActionDisplayName(Action);

    public IReadOnlyList<PromptTriggerActionOption> ActionOptions { get; }

    public PromptTriggerActionOption? SelectedActionOption
    {
        get => ActionOptions.FirstOrDefault(option => option.Value == Action
            && string.Equals(option.ExtensionActionId, ExtensionActionId, StringComparison.Ordinal));
        set
        {
            if (value is not null
                && (value.Value != Action
                    || !string.Equals(value.ExtensionActionId, ExtensionActionId, StringComparison.Ordinal)))
            {
                Action = value.Value;
                ExtensionActionId = value.ExtensionActionId;
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

    [ObservableProperty]
    public partial string ExtensionActionId { get; set; }

    partial void OnActionChanged(PromptTriggerAction value)
    {
        OnPropertyChanged(nameof(ActionDisplayName));
        OnPropertyChanged(nameof(SelectedActionOption));
    }

    partial void OnExtensionActionIdChanged(string value) =>
        OnPropertyChanged(nameof(SelectedActionOption));

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
        DateTimeOffset.UtcNow,
        ExtensionActionId);

    public void ApplySaved(PromptTrigger saved)
    {
        Revision = saved.Revision;
        Action = saved.Action;
        Phrase = saved.Phrase;
        Description = saved.Description;
        IsEnabled = saved.IsEnabled;
        ExtensionActionId = saved.ExtensionActionId
            ?? (saved.Action == PromptTriggerAction.Extension
                ? string.Empty
                : PromptActionExtensionIds.FromPromptAction(saved.Action));
        _savedAction = saved.Action;
        _savedPhrase = saved.Phrase;
        _savedDescription = saved.Description;
        _savedIsEnabled = saved.IsEnabled;
        _savedExtensionActionId = ExtensionActionId;
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
        PromptTriggerAction.VoiceInput => "Sprachsteuerung",
        PromptTriggerAction.LiveCaptions => "Live-Untertitel",
        PromptTriggerAction.LiveTranslation => "Live-Übersetzung",
        PromptTriggerAction.Audiobook => "Hörbuch erstellen",
        _ => value.ToString(),
    };
}

public sealed record PromptTriggerActionOption(
    PromptTriggerAction Value,
    string Label,
    string ExtensionActionId);

public sealed record PromptTriggerCategoryFilterOption(PromptTriggerAction? Value, string Label);
