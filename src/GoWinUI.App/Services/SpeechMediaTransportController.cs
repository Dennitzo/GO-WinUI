using Windows.Media;
using Windows.Media.Playback;

namespace GoWinUI.App.Services;

internal enum SpeechMediaTransportCommand
{
    None,
    Play,
    Pause,
}

/// <summary>
/// Publishes the native NAudio speech session to Windows media controls. The
/// MediaPlayer owns only the SMTC registration; speech audio remains on the
/// existing NAudio output device.
/// </summary>
internal sealed class SpeechMediaTransportController : IDisposable
{
    private readonly Func<SpeechMediaTransportCommand, Task> _commandHandler;
    private readonly Action<Exception> _errorHandler;
    private readonly MediaPlayer _mediaPlayer;
    private readonly SystemMediaTransportControls _controls;
    private bool _active;
    private bool _disposed;

    public SpeechMediaTransportController(
        Func<SpeechMediaTransportCommand, Task> commandHandler,
        Action<Exception> errorHandler)
    {
        _commandHandler = commandHandler;
        _errorHandler = errorHandler;
        _mediaPlayer = new MediaPlayer();
        _mediaPlayer.CommandManager.IsEnabled = false;
        _controls = _mediaPlayer.SystemMediaTransportControls;
        _controls.ButtonPressed += OnButtonPressed;
        _controls.DisplayUpdater.Type = MediaPlaybackType.Music;
        _controls.DisplayUpdater.MusicProperties.Title = "GO Vorlesen";
        _controls.DisplayUpdater.MusicProperties.Artist = "GO AI Assistent";
        _controls.DisplayUpdater.Update();
        Deactivate();
    }

    public void Activate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _active = true;
        _controls.IsEnabled = true;
        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsNextEnabled = false;
        _controls.IsPreviousEnabled = false;
        _controls.IsStopEnabled = false;
        _controls.PlaybackStatus = MediaPlaybackStatus.Changing;
    }

    public void SetPlaying(bool paused)
    {
        if (_disposed || !_active)
        {
            return;
        }
        _controls.PlaybackStatus = paused
            ? MediaPlaybackStatus.Paused
            : MediaPlaybackStatus.Playing;
    }

    public void Deactivate()
    {
        if (_disposed)
        {
            return;
        }
        _active = false;
        _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
        _controls.IsPlayEnabled = false;
        _controls.IsPauseEnabled = false;
        _controls.IsNextEnabled = false;
        _controls.IsPreviousEnabled = false;
        _controls.IsEnabled = false;
    }

    internal static SpeechMediaTransportCommand ResolveCommand(
        SystemMediaTransportControlsButton button) => button switch
    {
        SystemMediaTransportControlsButton.Play => SpeechMediaTransportCommand.Play,
        SystemMediaTransportControlsButton.Pause => SpeechMediaTransportCommand.Pause,
        _ => SpeechMediaTransportCommand.None,
    };

    private async void OnButtonPressed(
        SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        if (_disposed || !_active)
        {
            return;
        }
        var command = ResolveCommand(args.Button);
        if (command == SpeechMediaTransportCommand.None)
        {
            return;
        }
        try
        {
            await _commandHandler(command).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _errorHandler(exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Deactivate();
        _disposed = true;
        _controls.ButtonPressed -= OnButtonPressed;
        _mediaPlayer.Dispose();
    }
}
