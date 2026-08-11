using System.Text.Json.Nodes;
using Bricscad.ApplicationServices;
using GoWinUI.BricsCad.Protocol;

namespace GoWinUI.BricsCad.Plugin;

internal static class CapabilityRegistry
{
    public const string Provider = BridgeProtocol.Provider;
    public const string BridgeBuild = BridgeProtocol.BridgeBuild;
    public const string ContractVersion = BridgeProtocol.ContractVersion;
    public const int Protocol = BridgeProtocol.Version;

    private static readonly JsonObject Contract = LoadContract();
    private static readonly IReadOnlyDictionary<string, JsonObject> Methods = BuildMethodIndex();

    public static bool TryGetMethod(string name, out JsonObject? descriptor)
    {
        if (Methods.TryGetValue(name, out JsonObject? found) && IsAvailable(name, out _))
        {
            descriptor = (JsonObject)found.DeepClone();
            return true;
        }
        descriptor = null;
        return false;
    }

    public static JsonValidationResult ValidateParameters(string method, JsonObject parameters)
    {
        if (!Methods.TryGetValue(method, out JsonObject? descriptor))
            return JsonValidationResult.UnknownTool(method);
        return JsonContractValidator.Validate(parameters, descriptor["paramsSchema"]?.AsObject() ?? new JsonObject());
    }

    public static JsonObject Capabilities()
    {
        var methods = new JsonArray();
        foreach (JsonObject source in Methods.Values)
        {
            var method = (JsonObject)source.DeepClone();
            string name = method["name"]?.GetValue<string>() ?? string.Empty;
            bool available = IsAvailable(name, out string reason);
            method["available"] = available;
            method["availability"] = new JsonObject {
                ["available"] = available,
                ["reason"] = available ? string.Empty : reason
            };
            method["inputSchema"] = method["paramsSchema"]?.DeepClone();
            methods.Add(method);
        }

        return new JsonObject
        {
            ["schema"] = "barebone.bricscad.capabilities.dotnet.v2",
            ["provider"] = Provider,
            ["api"] = ".NET",
            ["contractVersion"] = ContractVersion,
            ["contractHash"] = ContractIdentity.Sha256,
            ["bridgeBuild"] = BridgeBuild,
            ["protocol"] = Protocol,
            ["pluginBuildId"] = BuildIdentity.Id,
            ["pluginVersion"] = BuildIdentity.Version,
            ["pluginBuiltAt"] = BuildIdentity.BuiltAt,
            ["runtimeInstanceId"] = RuntimeIdentity.Id,
            ["bricscadVersion"] = $"V{Application.Version.Major}.{Application.Version.Minor}.{Application.Version.Build}",
            ["dotnetRuntime"] = Environment.Version.ToString(),
            ["units"] = "mm",
            ["coordinateSystem"] = "WCS",
            ["revisionScope"] = "runtimeInstance",
            ["drawingSnapshotProtocol"] = "immutable-paged-v1",
            ["capabilityAuthority"] = "live-plugin-only",
            ["methods"] = methods,
            ["toolCategories"] = new JsonArray("geometry", "selection", "layer", "analysis", "annotation", "mep", "document", "undo", "bim")
        };
    }

    public static JsonObject Actions()
    {
        var actions = new JsonArray();
        foreach (JsonObject method in Methods.Values)
        {
            if (method["kind"]?.GetValue<string>() != "action") continue;
            string name = method["name"]?.GetValue<string>() ?? string.Empty;
            if (!IsAvailable(name, out _)) continue;
            actions.Add(new JsonObject
            {
                ["name"] = method["name"]?.GetValue<string>(),
                ["category"] = method["category"]?.GetValue<string>(),
                ["risk"] = method["risk"]?.GetValue<string>(),
                ["description"] = method["description"]?.GetValue<string>(),
                ["resultSchema"] = method["resultSchema"]?.GetValue<string>(),
                ["paramsSchema"] = method["paramsSchema"]?.DeepClone()
            });
        }
        return new JsonObject
        {
            ["schema"] = "barebone.bricscad.actions.dotnet.v2",
            ["provider"] = Provider,
            ["contractVersion"] = ContractVersion,
            ["contractHash"] = ContractIdentity.Sha256,
            ["actions"] = actions
        };
    }

    private static bool IsAvailable(string name, out string reason)
    {
        if (name == "bim.create" && !BimComponentCatalog.GetAll().Any(component => component.Available))
        {
            reason = "Keine lokale BricsCAD-V26-Window-/Door-Komponente wurde gefunden.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static JsonObject LoadContract()
    {
        JsonObject root = ContractIdentity.Parse();
        if (root["provider"]?.GetValue<string>() != Provider
            || root["contractVersion"]?.GetValue<string>() != ContractVersion
            || root["bridgeBuild"]?.GetValue<string>() != BridgeBuild
            || root["protocol"]?.GetValue<int>() != Protocol)
            throw new InvalidOperationException("Embedded BricsCAD .NET tool contract metadata does not match the plugin protocol.");
        return root;
    }

    private static IReadOnlyDictionary<string, JsonObject> BuildMethodIndex()
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (JsonNode? node in Contract["methods"]?.AsArray() ?? new JsonArray())
        {
            JsonObject method = node?.AsObject() ?? throw new InvalidOperationException("Tool contract contains a non-object method.");
            string name = method["name"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) || result.ContainsKey(name))
                throw new InvalidOperationException($"Tool contract contains an invalid or duplicate method name: {name}");
            var copy = (JsonObject)method.DeepClone();
            copy["paramsSchema"] = ResolveReferences(copy["paramsSchema"]);
            result.Add(name, copy);
        }
        return result;
    }

    private static JsonNode? ResolveReferences(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"]?.GetValue<string>() is string reference)
            {
                const string prefix = "#/definitions/";
                if (!reference.StartsWith(prefix, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Unsupported contract reference: {reference}");
                string key = reference[prefix.Length..];
                JsonNode definition = Contract["definitions"]?[key]
                    ?? throw new InvalidOperationException($"Missing contract definition: {key}");
                return ResolveReferences(definition.DeepClone());
            }
            var resolved = new JsonObject();
            foreach ((string key, JsonNode? value) in obj)
                resolved[key] = ResolveReferences(value);
            return resolved;
        }
        if (node is JsonArray array)
        {
            var resolved = new JsonArray();
            foreach (JsonNode? value in array)
                resolved.Add(ResolveReferences(value));
            return resolved;
        }
        return node?.DeepClone();
    }
}
