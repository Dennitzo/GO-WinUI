using System.Globalization;
using Microsoft.UI.Xaml.Data;

namespace GoWinUI.App.Converters;

public sealed class ProjectAssetUpdatedAtConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is DateTimeOffset updatedAt
            ? Format(updatedAt)
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    public static string Format(DateTimeOffset updatedAt) =>
        $"Geändert {updatedAt.ToLocalTime():dd.MM.yyyy, HH:mm:ss}";
}

public sealed class ProjectAssetLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is long length
            ? Format(length)
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, (double)bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString("0.#", CultureInfo.CurrentCulture)} {units[unit]}";
    }
}
