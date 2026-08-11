using System.Globalization;
using System.Text.Json.Nodes;

namespace GoWinUI.BricsCad.Plugin;

internal sealed record JsonValidationIssue(string Path, string Code, string Message, bool Missing = false)
{
    public JsonObject ToJson() => new()
    {
        ["path"] = Path,
        ["code"] = Code,
        ["message"] = Message
    };
}

internal sealed class JsonValidationResult
{
    public List<JsonValidationIssue> Issues { get; } = new();
    public bool Valid => Issues.Count == 0;
    public string Summary => Valid ? "Parameter entsprechen dem BricsCAD-.NET-Vertrag."
        : string.Join("; ", Issues.Select(issue => $"{issue.Path}: {issue.Message}"));

    public JsonObject ToJson()
    {
        var errors = new JsonArray();
        var missing = new JsonArray();
        var details = new JsonArray();
        foreach (JsonValidationIssue issue in Issues)
        {
            errors.Add(issue.Message);
            details.Add(issue.ToJson());
            if (issue.Missing) missing.Add(issue.Path);
        }
        return new JsonObject
        {
            ["valid"] = Valid,
            ["errors"] = errors,
            ["missing"] = missing,
            ["issues"] = details
        };
    }

    public static JsonValidationResult UnknownTool(string tool)
    {
        var result = new JsonValidationResult();
        result.Issues.Add(new JsonValidationIssue("$.tool", "unknown-tool", $"Unbekanntes bricscad-dotnet Tool: {tool}"));
        return result;
    }
}

internal static class JsonContractValidator
{
    public static JsonValidationResult Validate(JsonNode? value, JsonObject schema)
    {
        var result = new JsonValidationResult();
        ValidateNode(value, schema, "$", result);
        return result;
    }

    private static void ValidateNode(JsonNode? value, JsonObject schema, string path, JsonValidationResult result)
    {
        string type = schema["type"]?.GetValue<string>() ?? string.Empty;
        if (!string.IsNullOrEmpty(type) && !MatchesType(value, type))
        {
            result.Issues.Add(new JsonValidationIssue(path, "type", $"Erwartet wird der JSON-Typ {type}."));
            return;
        }

        if (schema["const"] is JsonNode constant && !JsonNode.DeepEquals(value, constant))
            result.Issues.Add(new JsonValidationIssue(path, "const", $"Erwarteter Wert: {constant.ToJsonString()}."));

        if (schema["enum"] is JsonArray enumValues
            && !enumValues.Any(candidate => JsonNode.DeepEquals(value, candidate)))
            result.Issues.Add(new JsonValidationIssue(path, "enum", $"Wert ist nicht erlaubt. Erlaubt: {string.Join(", ", enumValues.Select(item => item?.ToJsonString()))}."));

        if (value is JsonObject obj)
        {
            JsonObject properties = schema["properties"] as JsonObject ?? new JsonObject();
            foreach (JsonNode? requiredNode in schema["required"] as JsonArray ?? new JsonArray())
            {
                string required = requiredNode?.GetValue<string>() ?? string.Empty;
                if (!obj.ContainsKey(required) || obj[required] is null)
                    result.Issues.Add(new JsonValidationIssue($"{path}.{required}", "required", "Pflichtfeld fehlt.", true));
            }

            if (schema["oneOfRequired"] is JsonArray alternatives)
            {
                bool oneSatisfied = alternatives.Any(alternative => alternative is JsonArray fields
                    && fields.All(field => obj.ContainsKey(field?.GetValue<string>() ?? string.Empty)
                        && obj[field?.GetValue<string>() ?? string.Empty] is not null));
                if (!oneSatisfied)
                {
                    string choices = string.Join(" oder ", alternatives.Select(alternative =>
                        $"[{string.Join(", ", alternative?.AsArray().Select(field => field?.GetValue<string>()) ?? Array.Empty<string?>())}]"));
                    result.Issues.Add(new JsonValidationIssue(path, "one-of-required", $"Mindestens eine Feldgruppe muss vollständig vorhanden sein: {choices}.", true));
                }
            }

            foreach ((string key, JsonNode? child) in obj)
            {
                if (properties[key] is JsonObject childSchema)
                    ValidateNode(child, childSchema, $"{path}.{key}", result);
                else if (schema["additionalProperties"]?.GetValue<bool>() == false)
                    result.Issues.Add(new JsonValidationIssue($"{path}.{key}", "additional-property", "Feld ist im BricsCAD-.NET-Vertrag nicht definiert."));
            }
        }
        else if (value is JsonArray array)
        {
            int minimum = schema["minItems"]?.GetValue<int>() ?? 0;
            int maximum = schema["maxItems"]?.GetValue<int>() ?? int.MaxValue;
            if (array.Count < minimum)
                result.Issues.Add(new JsonValidationIssue(path, "min-items", $"Mindestens {minimum} Einträge sind erforderlich."));
            if (array.Count > maximum)
                result.Issues.Add(new JsonValidationIssue(path, "max-items", $"Höchstens {maximum} Einträge sind erlaubt."));
            if (schema["items"] is JsonObject itemSchema)
                for (int index = 0; index < array.Count; ++index)
                    ValidateNode(array[index], itemSchema, $"{path}[{index}]", result);
        }

        if (value is JsonValue jsonValue && TryNumber(jsonValue, out double number))
        {
            if (TrySchemaNumber(schema, "minimum", out double minimum) && number < minimum)
                result.Issues.Add(new JsonValidationIssue(path, "minimum", $"Wert muss mindestens {minimum.ToString(CultureInfo.InvariantCulture)} sein."));
            if (TrySchemaNumber(schema, "maximum", out double maximum) && number > maximum)
                result.Issues.Add(new JsonValidationIssue(path, "maximum", $"Wert darf höchstens {maximum.ToString(CultureInfo.InvariantCulture)} sein."));
            if (TrySchemaNumber(schema, "exclusiveMinimum", out double exclusiveMinimum) && number <= exclusiveMinimum)
                result.Issues.Add(new JsonValidationIssue(path, "exclusive-minimum", $"Wert muss größer als {exclusiveMinimum.ToString(CultureInfo.InvariantCulture)} sein."));
        }

        if (value is JsonValue stringValue && stringValue.TryGetValue(out string? text))
        {
            int minimumLength = schema["minLength"]?.GetValue<int>() ?? 0;
            if ((text?.Length ?? 0) < minimumLength)
                result.Issues.Add(new JsonValidationIssue(path, "min-length", $"Text muss mindestens {minimumLength} Zeichen enthalten."));
        }
    }

    private static bool MatchesType(JsonNode? value, string type) => type switch
    {
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue stringValue && stringValue.TryGetValue(out string? _),
        "boolean" => value is JsonValue booleanValue && booleanValue.TryGetValue(out bool _),
        "integer" => value is JsonValue integerValue && (integerValue.TryGetValue(out int _) || integerValue.TryGetValue(out long _)),
        "number" => value is JsonValue numberValue && TryNumber(numberValue, out _),
        _ => true
    };

    private static bool TryNumber(JsonValue value, out double number)
    {
        if (value.TryGetValue(out double doubleValue)) { number = doubleValue; return true; }
        if (value.TryGetValue(out int intValue)) { number = intValue; return true; }
        if (value.TryGetValue(out long longValue)) { number = longValue; return true; }
        number = 0;
        return false;
    }

    private static bool TrySchemaNumber(JsonObject schema, string key, out double number)
    {
        if (schema[key] is JsonValue value && TryNumber(value, out number)) return true;
        number = 0;
        return false;
    }
}


