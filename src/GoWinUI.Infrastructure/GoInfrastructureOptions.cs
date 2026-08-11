namespace GoWinUI.Infrastructure;

public sealed class GoInfrastructureOptions
{
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GO");

    public string DatabaseFileName { get; set; } = "GO.db";
    public string SettingsFileName { get; set; } = "settings.json";
    public int LogCapacity { get; set; } = 10_000;
}
