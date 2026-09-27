using GoWinUI.Core.Models;

namespace GoWinUI.Core.Extensions;

/// <summary>
/// Stable persisted identifiers for the built-in actions that historically used
/// <see cref="PromptTriggerAction"/> enum names in SQLite. The enum remains an
/// in-process execution adapter while persistence and extension contracts use
/// product-neutral action IDs.
/// </summary>
public static class PromptActionExtensionIds
{
    public const string Transcribe = BuiltInExtensionIds.Speech + "/transcribe";
    public const string VoiceInput = BuiltInExtensionIds.Speech + "/voice-input";
    public const string LiveTranslation = BuiltInExtensionIds.Speech + "/live-translation";
    public const string CodingRun = BuiltInExtensionIds.Coding + "/run";

    /// <summary>
    /// Actions backed by a stable built-in extension action ID. <see cref="PromptTriggerAction.Extension"/>
    /// is an execution adapter for third-party IDs and must never be offered as a standalone editable choice.
    /// </summary>
    public static IReadOnlyList<PromptTriggerAction> EditableBuiltInActions { get; } =
        Enum.GetValues<PromptTriggerAction>()
            .Where(static action => action is not (PromptTriggerAction.Extension
                or PromptTriggerAction.PlanMode))
            .ToArray();

    public static string FromPromptAction(PromptTriggerAction action) => action switch
    {
        PromptTriggerAction.ImageGeneration => BuiltInActionIds.GenerateImage,
        PromptTriggerAction.Translation => BuiltInActionIds.Translate,
        PromptTriggerAction.TextToSpeech => BuiltInActionIds.ReadAloud,
        PromptTriggerAction.Transcription => Transcribe,
        PromptTriggerAction.AudioAnalysis => BuiltInActionIds.AudioAnalysis,
        PromptTriggerAction.VideoAnalysis => BuiltInActionIds.VideoAnalysis,
        PromptTriggerAction.ImageAnalysis => BuiltInActionIds.ImageAnalysis,
        PromptTriggerAction.WebSearch => BuiltInActionIds.WebSearch,
        PromptTriggerAction.VoiceInput => VoiceInput,
        PromptTriggerAction.LiveCaptions => BuiltInActionIds.LiveCaptions,
        PromptTriggerAction.LiveTranslation => LiveTranslation,
        PromptTriggerAction.Audiobook => BuiltInActionIds.CreateAudiobook,
        PromptTriggerAction.Coding => CodingRun,
        PromptTriggerAction.DocumentCreate => BuiltInActionIds.CreateDocument,
        PromptTriggerAction.PlanMode => BuiltInActionIds.PlanMode,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Die Promptaktion besitzt keine persistierbare Extension-ID."),
    };

    public static bool TryGetPromptAction(string? extensionActionId, out PromptTriggerAction action)
    {
        action = extensionActionId switch
        {
            BuiltInActionIds.GenerateImage => PromptTriggerAction.ImageGeneration,
            BuiltInActionIds.Translate => PromptTriggerAction.Translation,
            BuiltInActionIds.ReadAloud => PromptTriggerAction.TextToSpeech,
            Transcribe => PromptTriggerAction.Transcription,
            BuiltInActionIds.AudioAnalysis => PromptTriggerAction.AudioAnalysis,
            BuiltInActionIds.VideoAnalysis => PromptTriggerAction.VideoAnalysis,
            BuiltInActionIds.ImageAnalysis => PromptTriggerAction.ImageAnalysis,
            BuiltInActionIds.WebSearch => PromptTriggerAction.WebSearch,
            VoiceInput => PromptTriggerAction.VoiceInput,
            BuiltInActionIds.LiveCaptions => PromptTriggerAction.LiveCaptions,
            LiveTranslation => PromptTriggerAction.LiveTranslation,
            BuiltInActionIds.CreateAudiobook => PromptTriggerAction.Audiobook,
            CodingRun => PromptTriggerAction.Coding,
            BuiltInActionIds.CreateDocument => PromptTriggerAction.DocumentCreate,
            BuiltInActionIds.PlanMode => PromptTriggerAction.PlanMode,
            _ => default,
        };
        return extensionActionId is BuiltInActionIds.GenerateImage
            or BuiltInActionIds.Translate
            or BuiltInActionIds.ReadAloud
            or Transcribe
            or BuiltInActionIds.AudioAnalysis
            or BuiltInActionIds.VideoAnalysis
            or BuiltInActionIds.ImageAnalysis
            or BuiltInActionIds.WebSearch
            or VoiceInput
            or BuiltInActionIds.LiveCaptions
            or LiveTranslation
            or BuiltInActionIds.CreateAudiobook
            or CodingRun
            or BuiltInActionIds.CreateDocument
            or BuiltInActionIds.PlanMode;
    }

    public static string EnsureValid(string extensionActionId)
    {
        if (!ExtensionIdentifiers.TryValidateActionId(extensionActionId, expectedExtensionId: null, out var error))
            throw new ArgumentException(error, nameof(extensionActionId));
        return extensionActionId;
    }

    public static (PromptTriggerAction Action, string ExtensionActionId) ResolveForWrite(
        PromptTriggerAction action,
        string? extensionActionId)
    {
        if (!Enum.IsDefined(action))
            throw new ArgumentOutOfRangeException(nameof(action), action, "Die Promptaktion ist ungültig.");

        if (string.IsNullOrWhiteSpace(extensionActionId))
        {
            if (action == PromptTriggerAction.Extension)
                throw new ArgumentException(
                    "Eine Erweiterungs-Promptaktion benötigt eine konkrete extensionActionId.",
                    nameof(extensionActionId));
            return (action, FromPromptAction(action));
        }

        EnsureValid(extensionActionId);
        if (TryGetPromptAction(extensionActionId, out var builtInAction))
        {
            if (action is not PromptTriggerAction.Extension && action != builtInAction)
                throw new ArgumentException(
                    $"Promptaktion und extensionActionId widersprechen sich ('{action}' / '{extensionActionId}').",
                    nameof(extensionActionId));
            return (builtInAction, extensionActionId);
        }

        if (action != PromptTriggerAction.Extension)
            throw new ArgumentException(
                $"Die Drittanbieteraktion '{extensionActionId}' benötigt den internen Extension-Adapter.",
                nameof(extensionActionId));
        return (PromptTriggerAction.Extension, extensionActionId);
    }

    public static PromptTriggerAction ResolveForRead(string extensionActionId)
    {
        EnsureValid(extensionActionId);
        return TryGetPromptAction(extensionActionId, out var action)
            ? action
            : PromptTriggerAction.Extension;
    }
}
