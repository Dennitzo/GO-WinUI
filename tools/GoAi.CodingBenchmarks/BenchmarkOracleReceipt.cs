using System.Text.Json;

namespace GoAi.CodingBenchmarks;

internal static class BenchmarkOracleReceipt
{
    internal static bool IsPassed(JsonElement process, BenchmarkTask task, bool oracleSourceUnchanged)
    {
        if (!oracleSourceUnchanged || process.ValueKind != JsonValueKind.Object
            || !process.TryGetProperty("exitCode", out var exit) || exit.ValueKind != JsonValueKind.Number || !exit.TryGetInt32(out var code) || code != 0
            || !process.TryGetProperty("stdout", out var output) || output.ValueKind != JsonValueKind.String) return false;
        var expectedCases = task.Cases.Count + (task.ModuleProbes?.Sum(static probe => probe.Cases.Count) ?? 0);
        var receipts = 0;
        foreach (var line in output.GetString()!.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var parsed = JsonDocument.Parse(line);
                var receipt = parsed.RootElement;
                if (receipt.ValueKind != JsonValueKind.Object || !receipt.TryGetProperty("oracle", out var name)
                    || name.ValueKind != JsonValueKind.String || name.GetString() != "independent-python-contract-v2") continue;
                if (!receipt.TryGetProperty("passed", out var passed) || passed.ValueKind != JsonValueKind.True
                    || !receipt.TryGetProperty("cases", out var cases) || cases.ValueKind != JsonValueKind.Number || !cases.TryGetInt32(out var count) || count != expectedCases
                    || !receipt.TryGetProperty("failures", out var failures) || failures.ValueKind != JsonValueKind.Array || failures.GetArrayLength() != 0) return false;
                receipts++;
            }
            catch (JsonException) { }
        }
        return receipts == 1;
    }
}
