using GoAi.Contracts;
using System.Text.Json;

namespace GoWinUI.Tests;

internal static class CodingLiveTestConsole
{
    public static void WriteProgramStart(
        ToolProposal proposal,
        ClientToolResult result,
        CodingLiveTestLog? log = null,
        string? runId = null)
    {
        if (!IsProgramStart(proposal))
        {
            return;
        }

        var command = DescribeCommand(proposal);
        var exitCode = ReadInteger(result.Result, "exitCode");
        var standardOutput = ReadString(result.Result, "standardOutput");
        var standardError = ReadString(result.Result, "standardError");

        Console.WriteLine();
        Console.WriteLine("========== SICHTBARER PROGRAMMSTART ==========");
        Console.WriteLine($"> {command}");
        Console.WriteLine(exitCode is null ? $"Status: {result.Status}" : $"Exit-Code: {exitCode}");
        Console.WriteLine("---------- stdout ----------");
        Console.WriteLine(string.IsNullOrWhiteSpace(standardOutput) ? "[Keine Standardausgabe]" : standardOutput.TrimEnd());
        if (!string.IsNullOrWhiteSpace(standardError) || !string.IsNullOrWhiteSpace(result.Message))
        {
            Console.WriteLine("---------- stderr ----------");
            Console.WriteLine(string.IsNullOrWhiteSpace(standardError) ? result.Message : standardError.TrimEnd());
        }
        Console.WriteLine("==============================================");
        Console.WriteLine();
        log?.Write("program.visible", new
        {
            command,
            status = result.Status,
            exitCode,
            standardOutput,
            standardError,
            result.Message,
        }, runId);
    }

    private static bool IsProgramStart(ToolProposal proposal)
    {
        if (proposal.Arguments.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (proposal.Name == ClientToolNames.ProcessRun)
        {
            return string.Equals(ReadString(proposal.Arguments, "purpose"), "start", StringComparison.OrdinalIgnoreCase);
        }
        if (proposal.Name != ClientToolNames.ProcessRunPreset)
        {
            return false;
        }
        return ReadString(proposal.Arguments, "preset") is "repository.start" or "repository.verify" or "code.run";
    }

    private static string DescribeCommand(ToolProposal proposal)
    {
        if (proposal.Name == ClientToolNames.ProcessRunPreset)
        {
            return $"GO-Preset {ReadString(proposal.Arguments, "preset")}";
        }

        var executable = ReadString(proposal.Arguments, "executable") ?? "Programm";
        var arguments = proposal.Arguments.TryGetProperty("arguments", out var values)
            && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray()
                    .Where(static value => value.ValueKind == JsonValueKind.String)
                    .Select(static value => Quote(value.GetString() ?? string.Empty))
                : [];
        return string.Join(' ', new[] { Quote(executable) }.Concat(arguments));
    }

    private static string Quote(string value) =>
        value.Length > 0 && !value.Any(char.IsWhiteSpace)
            ? value
            : $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string? ReadString(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var property in owner.EnumerateObject())
        {
            if ((property.NameEquals(name) || string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }
        return null;
    }

    private static int? ReadInteger(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var property in owner.EnumerateObject())
        {
            if ((property.NameEquals(name) || string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                && property.Value.TryGetInt32(out var value))
            {
                return value;
            }
        }
        return null;
    }
}
