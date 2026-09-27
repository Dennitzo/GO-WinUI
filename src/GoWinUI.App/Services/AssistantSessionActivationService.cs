using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.App.Services;

internal static class AssistantSessionUri
{
    private const string SchemePrefix = "local-assistant-";
    private const string SessionHost = "session";

    internal static string ProtocolScheme(string profileName)
    {
        if (!IsValidProfileName(profileName))
        {
            throw new ArgumentException("The profile name is not a valid protocol component.", nameof(profileName));
        }

        return SchemePrefix + profileName;
    }

    internal static Uri Create(string profileName, Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty session ID is required.", nameof(sessionId));
        }

        return new Uri(
            $"{ProtocolScheme(profileName)}://{SessionHost}/{sessionId:D}",
            UriKind.Absolute);
    }

    internal static bool TryParse(Uri? uri, string profileName, out Guid sessionId)
    {
        sessionId = Guid.Empty;
        if (uri is null
            || !uri.IsAbsoluteUri
            || !IsValidProfileName(profileName)
            || !string.Equals(uri.Scheme, ProtocolScheme(profileName), StringComparison.Ordinal)
            || !string.Equals(uri.IdnHost, SessionHost, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var escapedPath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        if (escapedPath.Contains('%', StringComparison.Ordinal)
            || escapedPath.Length != 36
            || !Guid.TryParseExact(escapedPath, "D", out sessionId)
            || sessionId == Guid.Empty)
        {
            sessionId = Guid.Empty;
            return false;
        }

        return true;
    }

    private static bool IsValidProfileName(string? profileName)
    {
        if (profileName is null or { Length: 0 or > 32 }
            || profileName[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9'))
        {
            return false;
        }

        return profileName.All(static character =>
            character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-');
    }
}

internal sealed record AssistantSessionActivation(
    Guid SessionId,
    ChatMode ChatMode,
    bool SessionChanged);

internal sealed class AssistantSessionActivationService(
    IChatRepository chats,
    SettingsCoordinator settings)
{
    internal async Task<AssistantSessionActivation?> ActivateAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
        {
            return null;
        }

        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        var current = settings.Current;
        var modeSpecificId = session.ChatMode switch
        {
            ChatMode.General => current.ActiveGeneralSessionId,
            ChatMode.Coding => current.ActiveCodingSessionId,
            ChatMode.ClaudeScience => current.ActiveClaudeScienceSessionId,
            _ => null,
        };
        var sessionChanged = current.SelectedChatMode != session.ChatMode
            || current.ActiveSessionId != session.Id
            || modeSpecificId != session.Id;

        await settings.UpdateAsync(value => session.ChatMode switch
        {
            ChatMode.General => value with
            {
                SelectedChatMode = ChatMode.General,
                ActiveGeneralSessionId = session.Id,
                ActiveSessionId = session.Id,
                LastRoute = "assistant",
            },
            ChatMode.Coding => value with
            {
                SelectedChatMode = ChatMode.Coding,
                ActiveCodingSessionId = session.Id,
                ActiveSessionId = session.Id,
                LastRoute = "assistant",
            },
            ChatMode.ClaudeScience => value with
            {
                SelectedChatMode = ChatMode.ClaudeScience,
                ActiveClaudeScienceSessionId = session.Id,
                ActiveSessionId = session.Id,
                LastRoute = "assistant",
            },
            _ => throw new InvalidOperationException("The session has an unsupported chat mode."),
        }, cancellationToken).ConfigureAwait(false);

        return new(session.Id, session.ChatMode, sessionChanged);
    }
}
