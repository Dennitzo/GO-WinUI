using GoAi.Contracts;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Core.Coding;

internal static class CodingWorkingStateTools
{
    internal const string PlanTool = "coding.updatePlan";

    internal static IReadOnlyList<AgentToolSpec> CreateTools() =>
    [
        Tool(PlanTool, "Aktualisiere nur geänderte Planpunkte/Kriterien per id; nicht genannte Punkte und Felder bleiben erhalten. Bestehende IDs brauchen keinen title; neue IDs benötigen title und status. Bei neuem completed-Status sind evidenceIds erfolgreicher Werkzeugbelege erforderlich. Bündele echte Änderungen, wiederhole keinen vollständigen Plan, Befunde oder unveränderte Kriterien. Planung ersetzt keine Ausführung.",
            """{"steps":{"type":"array","minItems":1,"maxItems":16,"items":{"type":"object","additionalProperties":false,"properties":{"id":{"type":"string","minLength":1,"maxLength":80},"title":{"type":"string","minLength":1,"maxLength":400,"description":"Nur bei neuer ID erforderlich; sonst bestehenden Titel beibehalten."},"status":{"type":"string","enum":["pending","in_progress","completed"],"description":"Bei neuer ID erforderlich. Neu completed braucht echte erfolgreiche evidenceIds."},"evidenceIds":{"type":"array","maxItems":3,"uniqueItems":true,"items":{"type":"string","minLength":1,"maxLength":100}}},"required":["id"]}},"acceptanceCriteria":{"type":"array","minItems":1,"maxItems":16,"items":{"type":"object","additionalProperties":false,"properties":{"id":{"type":"string","minLength":1,"maxLength":80},"title":{"type":"string","minLength":1,"maxLength":400,"description":"Nur bei neuer ID erforderlich; sonst bestehenden Titel beibehalten."},"status":{"type":"string","enum":["pending","in_progress","completed"],"description":"Bei neuer ID erforderlich. Neu completed braucht echte erfolgreiche evidenceIds."},"evidenceIds":{"type":"array","maxItems":3,"uniqueItems":true,"items":{"type":"string","minLength":1,"maxLength":100}}},"required":["id"]}},"facts":{"type":"array","maxItems":12,"items":{"type":"object","additionalProperties":false,"properties":{"text":{"type":"string","minLength":1,"maxLength":600},"evidenceIds":{"type":"array","maxItems":3,"uniqueItems":true,"items":{"type":"string","minLength":1,"maxLength":100},"minItems":1}},"required":["text","evidenceIds"]}},"rejectedHypotheses":{"type":"array","maxItems":12,"items":{"type":"object","additionalProperties":false,"properties":{"text":{"type":"string","minLength":1,"maxLength":600},"evidenceIds":{"type":"array","maxItems":3,"uniqueItems":true,"items":{"type":"string","minLength":1,"maxLength":100},"minItems":1}},"required":["text","evidenceIds"]}},"explanation":{"type":"string","minLength":1,"maxLength":2000},"nextStep":{"type":"string","minLength":1,"maxLength":1000},"phase":{"type":"string","enum":["planning","exploration","editing","review","final","error_recovery","unknown"]}}""", []),
    ];

    internal static void Validate(JsonElement args)
    {
        if (!args.EnumerateObject().Any()) throw new ArgumentException("updatePlan requires at least one working-state field.");
        if (args.TryGetProperty("steps", out var steps)) ValidatePlan(steps);
        if (args.TryGetProperty("acceptanceCriteria", out var criteria)) ValidatePlan(criteria);
        if (args.TryGetProperty("facts", out var facts)) ValidateFacts(facts);
        if (args.TryGetProperty("rejectedHypotheses", out var rejected)) ValidateFacts(rejected);
        if (args.TryGetProperty("explanation", out var explanation)) Text(explanation, 1, 2000);
        if (args.TryGetProperty("nextStep", out var next)) Text(next, 1, 1000);
        if (args.TryGetProperty("phase", out var phase) && (phase.ValueKind != JsonValueKind.String
            || phase.GetString() is not ("planning" or "exploration" or "editing" or "review" or "final" or "error_recovery" or "unknown")))
            throw new ArgumentException("Unknown working phase.");
    }

    private static void ValidatePlan(JsonElement steps)
    {
        if (steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() is < 1 or > 16) throw new ArgumentException("Plans must contain one to sixteen items.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var active = 0;
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each plan step must be an object.");
            foreach (var field in step.EnumerateObject())
                if (field.Name is not ("id" or "title" or "status" or "evidenceIds")) throw new ArgumentException("Unknown plan step field.");
            var id = Required(step, "id"); Text(id, 1, 80);
            if (step.TryGetProperty("title", out var title)) Text(title, 1, 400);
            if (!ids.Add(id.GetString()!)) throw new ArgumentException("Plan IDs must be unique.");
            if (step.TryGetProperty("status", out var status))
            {
                if (status.ValueKind != JsonValueKind.String || status.GetString() is not ("pending" or "in_progress" or "completed"))
                    throw new ArgumentException("Unknown plan status.");
                if (status.GetString() == "in_progress" && ++active > 1) throw new ArgumentException("Only one plan step may be in progress.");
            }
            // The reducer checks new IDs, completed transitions and the merged plan.
            // An already completed item can retain its previously verified evidence.
            ValidateEvidence(step, false);
        }
    }

    internal static JsonElement CreatePlanReceipt(CodingWorkingState state, JsonElement arguments)
    {
        var receipt = new Dictionary<string, object?> { ["success"] = true, ["phase"] = state.Phase };
        if (arguments.TryGetProperty("nextStep", out _)) receipt["nextStep"] = state.NextStep;
        AddChangedItems("steps", "updatedSteps", state.Plan);
        AddChangedItems("acceptanceCriteria", "updatedAcceptanceCriteria", state.AcceptanceCriteria);
        if (arguments.TryGetProperty("facts", out var facts)) receipt["factsReceived"] = facts.GetArrayLength();
        if (arguments.TryGetProperty("rejectedHypotheses", out var rejected)) receipt["rejectedHypothesesReceived"] = rejected.GetArrayLength();
        return JsonSerializer.SerializeToElement(receipt);

        void AddChangedItems(string argument, string result, IReadOnlyList<CodingPlanItem> items)
        {
            if (!arguments.TryGetProperty(argument, out var changes)) return;
            var ids = changes.EnumerateArray().Select(static item => item.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);
            receipt[result] = items.Where(item => ids.Contains(item.Id)).Select(static item => new { id = item.Id, status = item.Status }).ToArray();
        }
    }

    private static void ValidateFacts(JsonElement facts)
    {
        if (facts.ValueKind != JsonValueKind.Array || facts.GetArrayLength() > 12) throw new ArgumentException("At most twelve evidenced facts are allowed.");
        foreach (var fact in facts.EnumerateArray())
        {
            if (fact.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each fact must be an object.");
            foreach (var field in fact.EnumerateObject())
                if (field.Name is not ("text" or "evidenceIds")) throw new ArgumentException("Unknown fact field.");
            Text(Required(fact, "text"), 1, 600);
            ValidateEvidence(fact, true);
        }
    }

    private static void ValidateEvidence(JsonElement item, bool required)
    {
        if (!item.TryGetProperty("evidenceIds", out var ids))
        {
            if (required) throw new ArgumentException("Completed steps and facts require evidence IDs.");
            return;
        }
        if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > 3 || required && ids.GetArrayLength() == 0)
            throw new ArgumentException("At most three evidence IDs are allowed; completed steps and facts need evidence.");
        foreach (var id in ids.EnumerateArray()) Text(id, 1, 100);
        if (ids.EnumerateArray().Select(static id => id.GetString()).Distinct(StringComparer.Ordinal).Count() != ids.GetArrayLength())
            throw new ArgumentException("Evidence IDs must be unique.");
    }

    private static JsonElement Required(JsonElement item, string field) => item.TryGetProperty(field, out var value)
        ? value : throw new ArgumentException($"The required field {field} is missing.");

    private static void Text(JsonElement value, int minimum, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length < minimum || text.Length > maximum)
            throw new ArgumentException($"Expected text with {minimum} to {maximum} characters.");
    }

    private static AgentToolSpec Tool(string name, string description, string properties, string[] required)
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = JsonSerializer.Deserialize<JsonElement>(properties), required, additionalProperties = false });
        return new(name, description, ToolRiskClass.ReadOnly, true, schema, required.ToHashSet(StringComparer.Ordinal),
            schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal));
    }
}
