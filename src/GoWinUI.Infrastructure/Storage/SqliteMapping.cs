using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GoWinUI.Infrastructure.Storage;

internal static class SqliteMapping
{
    internal static string ToDb(this DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    internal static DateTimeOffset ReadDate(this SqliteDataReader reader, int ordinal) =>
        DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    internal static Guid ReadGuid(this SqliteDataReader reader, int ordinal) => Guid.Parse(reader.GetString(ordinal));
    internal static string EnumName<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
    internal static T ReadEnum<T>(this SqliteDataReader reader, int ordinal) where T : struct, Enum =>
        Enum.Parse<T>(reader.GetString(ordinal), ignoreCase: true);
    internal static string ToFtsQuery(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return "__go_no_match__";
        var terms = search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(static term => new string(term.Where(static character => char.IsLetterOrDigit(character)).ToArray()))
            .Where(static term => term.Length > 0)
            .Select(static term => $"\"{term}\"*")
            .ToArray();
        return terms.Length == 0 ? "__go_no_match__" : string.Join(" AND ", terms);
    }
}
