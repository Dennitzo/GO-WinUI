using GoWinUI.Core.Models;
using System.Text;
using System.Text.Json;

namespace GoWinUI.App.Services;

internal static class WorkflowChatFormatter
{
    public static string Format(WorkflowDefinition workflow)
    {
        var lines = new List<string>
        {
            $"**Workflow: {workflow.Title.Trim()}**",
        };

        AppendParagraph(lines, workflow.Description);

        try
        {
            using var document = JsonDocument.Parse(workflow.ContentJson);
            var root = document.RootElement;
            AppendBlocks(lines, FindBlocks(root));
            AppendTables(lines, root);
            AppendFormulas(lines, root);
            AppendObjectList(lines, root, "examples", "Beispiele", "title", "summary", "description", "result");
            AppendObjectList(lines, root, "requiredSlots", "Pflichtangaben", "name", "description");
            AppendObjectList(lines, root, "derivedValues", "Berechnete Werte", "name", "expression", "description");
            AppendSteps(lines, root);
            AppendStringList(lines, root, "preferredTools", "Tools");
            AppendStringList(lines, root, "constructionStrategy", "Strategie");
            AppendStringList(lines, root, "assumptions", "Annahmen");
            AppendStringList(lines, root, "warnings", "Hinweise");
            AppendStringList(lines, root, "validationWarnings", "Validierungshinweise");
        }
        catch (JsonException)
        {
            lines.Add(string.Empty);
            lines.Add("## Inhalt");
            lines.Add("```json");
            lines.Add(workflow.ContentJson.Trim());
            lines.Add("```");
        }

        if (!string.IsNullOrWhiteSpace(workflow.ContextSummary)
            && !string.Equals(workflow.ContextSummary.Trim(), workflow.Description.Trim(), StringComparison.Ordinal))
        {
            lines.Add(string.Empty);
            lines.Add("## Kontext");
            lines.Add(workflow.ContextSummary.Trim());
        }

        lines.Add(string.Empty);
        lines.Add("Nutze diesen Workflow als Kontext für die weitere Unterhaltung.");
        return NormalizeSpacing(lines);
    }

    private static JsonElement FindBlocks(JsonElement root)
    {
        if (root.TryGetProperty("blocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            return blocks;
        }

        if (root.TryGetProperty("display", out var display)
            && display.ValueKind == JsonValueKind.Object
            && display.TryGetProperty("blocks", out blocks)
            && blocks.ValueKind == JsonValueKind.Array)
        {
            return blocks;
        }

        return default;
    }

    private static void AppendBlocks(List<string> lines, JsonElement blocks)
    {
        if (blocks.ValueKind != JsonValueKind.Array || blocks.GetArrayLength() == 0)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add("## Inhalte");
        foreach (var block in blocks.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.String)
            {
                AppendParagraph(lines, block.GetString());
                continue;
            }

            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = GetText(block, "title");
            var content = GetText(block, "text", "content", "markdown");
            if (!string.IsNullOrWhiteSpace(title))
            {
                lines.Add(string.Empty);
                lines.Add($"### {title.Trim()}");
            }

            AppendParagraph(lines, content);
        }
    }

    private static void AppendTables(List<string> lines, JsonElement root)
    {
        if (!root.TryGetProperty("tables", out var tables)
            || tables.ValueKind != JsonValueKind.Array
            || tables.GetArrayLength() == 0)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add("## Tabellen");
        foreach (var table in tables.EnumerateArray())
        {
            if (table.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = GetText(table, "title", "name");
            if (!string.IsNullOrWhiteSpace(title))
            {
                lines.Add(string.Empty);
                lines.Add($"### {title.Trim()}");
            }

            if (!table.TryGetProperty("columns", out var columns)
                || columns.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var headers = columns.EnumerateArray().Select(CellText).ToArray();
            if (headers.Length == 0)
            {
                continue;
            }

            lines.Add($"| {string.Join(" | ", headers)} |");
            lines.Add($"| {string.Join(" | ", headers.Select(static _ => "---"))} |");
            if (table.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var cells = row.EnumerateArray().Select(CellText).ToList();
                    while (cells.Count < headers.Length)
                    {
                        cells.Add(string.Empty);
                    }

                    lines.Add($"| {string.Join(" | ", cells.Take(headers.Length))} |");
                }
            }
        }
    }

    private static void AppendFormulas(List<string> lines, JsonElement root)
    {
        if (!root.TryGetProperty("formulas", out var formulas)
            || formulas.ValueKind != JsonValueKind.Array
            || formulas.GetArrayLength() == 0)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add("## Formeln");
        foreach (var formula in formulas.EnumerateArray())
        {
            if (formula.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = GetText(formula, "title", "name", "label", "id");
            var expression = GetText(formula, "latex", "expression");
            var description = GetText(formula, "description");
            if (!string.IsNullOrWhiteSpace(title))
            {
                lines.Add(string.Empty);
                lines.Add($"### {title.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(expression))
            {
                lines.Add($"\\[{expression.Trim()}\\]");
            }

            AppendParagraph(lines, description);
        }
    }

    private static void AppendSteps(List<string> lines, JsonElement root)
    {
        var steps = new List<JsonElement>();
        if (root.TryGetProperty("executionBatches", out var batches) && batches.ValueKind == JsonValueKind.Array)
        {
            foreach (var batch in batches.EnumerateArray())
            {
                if (batch.ValueKind == JsonValueKind.Object
                    && batch.TryGetProperty("steps", out var batchSteps)
                    && batchSteps.ValueKind == JsonValueKind.Array)
                {
                    steps.AddRange(batchSteps.EnumerateArray());
                }
            }
        }
        else if (root.TryGetProperty("steps", out var rootSteps) && rootSteps.ValueKind == JsonValueKind.Array)
        {
            steps.AddRange(rootSteps.EnumerateArray());
        }

        if (steps.Count == 0)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add("## Schritte");
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var title = step.ValueKind == JsonValueKind.String
                ? step.GetString()
                : GetText(step, "title", "name", "id", "tool");
            var description = step.ValueKind == JsonValueKind.Object ? GetText(step, "description") : null;
            lines.Add($"{index + 1}. {(string.IsNullOrWhiteSpace(title) ? $"Schritt {index + 1}" : title.Trim())}");
            if (!string.IsNullOrWhiteSpace(description))
            {
                lines.Add($"   - {description.Trim()}");
            }
        }
    }

    private static void AppendStringList(List<string> lines, JsonElement root, string propertyName, string heading)
    {
        if (!root.TryGetProperty(propertyName, out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var items = values.EnumerateArray()
            .Select(static item => item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        if (items.Length == 0)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add($"## {heading}");
        lines.AddRange(items.Select(static item => $"- {item}"));
    }

    private static void AppendObjectList(
        List<string> lines,
        JsonElement root,
        string propertyName,
        string heading,
        params string[] fields)
    {
        if (!root.TryGetProperty(propertyName, out var values)
            || values.ValueKind != JsonValueKind.Array
            || values.GetArrayLength() == 0)
        {
            return;
        }

        var items = new List<string>();
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                items.Add(item.GetString()!.Trim());
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var parts = fields
                .Select(field => GetText(item, field))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (parts.Length > 0)
            {
                items.Add(string.Join(": ", parts));
            }
        }

        if (items.Count == 0)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add($"## {heading}");
        lines.AddRange(items.Select(static item => $"- {item}"));
    }

    private static string? GetText(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.GetString()))
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static string CellText(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        return (text ?? string.Empty)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal)
            .Trim();
    }

    private static void AppendParagraph(List<string> lines, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add(string.Empty);
            lines.Add(value.Trim());
        }
    }

    private static string NormalizeSpacing(IEnumerable<string> lines)
    {
        var result = new StringBuilder();
        var previousWasEmpty = false;
        foreach (var line in lines)
        {
            var isEmpty = string.IsNullOrWhiteSpace(line);
            if (isEmpty && (previousWasEmpty || result.Length == 0))
            {
                continue;
            }

            result.AppendLine(isEmpty ? string.Empty : line.TrimEnd());
            previousWasEmpty = isEmpty;
        }

        return result.ToString().Trim();
    }
}
