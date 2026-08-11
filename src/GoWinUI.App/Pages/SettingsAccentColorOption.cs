using Microsoft.UI.Xaml.Media;

namespace GoWinUI.App.Pages;

public sealed class SettingsAccentColorOption(string name, string value, Windows.UI.Color color)
{
    public string Name { get; } = name;

    public string Value { get; } = value;

    public SolidColorBrush PreviewBrush { get; } = new(color);
}
