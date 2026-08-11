namespace GoWinUI.BricsCad.Plugin;

/// <summary>
/// Marker emitted by ordinary solution builds. The loadable BricsCAD assembly
/// is produced only by windows/build-bricscad-plugin.ps1.
/// </summary>
public static class OptionalBuildMarker
{
    public const string BuildCommand = "windows\\build-bricscad-plugin.ps1";
}
