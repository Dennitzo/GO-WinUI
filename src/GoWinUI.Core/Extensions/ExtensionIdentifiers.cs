using System.Text.RegularExpressions;

namespace GoWinUI.Core.Extensions;

public static partial class ExtensionIdentifiers
{
    public const int MaximumIdentifierLength = 160;

    [GeneratedRegex("^builtin\\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex BuiltInIdPattern();

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?){2,}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReverseDnsIdPattern();

    [GeneratedRegex("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ActionNamePattern();

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelToolNamePattern();

    [GeneratedRegex("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionPattern();

    public static bool IsValidExtensionId(string? value) =>
        TryValidateExtensionId(value, out _);

    public static bool TryValidateExtensionId(string? value, out string error)
    {
        if (!TryValidateCommonIdentifier(value, "Extension-ID", out error)) return false;
        if (!IsProductNeutralPublicIdentifier(value!))
        {
            error = "Die Extension-ID verwendet eine reservierte produktgebundene Kennung.";
            return false;
        }

        if (BuiltInIdPattern().IsMatch(value!) || ReverseDnsIdPattern().IsMatch(value!))
        {
            error = string.Empty;
            return true;
        }

        error = "First-Party-IDs müssen 'builtin.<name>' verwenden; Drittanbieter-IDs müssen kleingeschriebene Reverse-DNS-IDs mit mindestens drei Segmenten sein.";
        return false;
    }

    public static bool TryValidateActionId(string? value, string? expectedExtensionId, out string error)
    {
        if (!TryValidateCommonIdentifier(value, "Action-ID", out error)) return false;
        var separator = value!.IndexOf('/');
        if (separator <= 0 || separator != value.LastIndexOf('/') || separator == value.Length - 1)
        {
            error = "Action-IDs müssen genau dem Format '<extensionId>/<action>' entsprechen.";
            return false;
        }

        var extensionId = value[..separator];
        var action = value[(separator + 1)..];
        if (!TryValidateExtensionId(extensionId, out var extensionError))
        {
            error = extensionError;
            return false;
        }

        if (!ActionNamePattern().IsMatch(action))
        {
            error = "Der Action-Name muss kleingeschriebenes Kebab-Case sein.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectedExtensionId)
            && !string.Equals(extensionId, expectedExtensionId, StringComparison.Ordinal))
        {
            error = $"Die Action-ID gehört zu '{extensionId}' statt zur Extension '{expectedExtensionId}'.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryValidateModelToolName(string? value, out string error)
    {
        if (!TryValidateCommonIdentifier(value, "Modell-Toolname", out error)) return false;
        if (!IsProductNeutralPublicIdentifier(value!))
        {
            error = "Der Modell-Toolname verwendet eine reservierte produktgebundene Kennung.";
            return false;
        }

        if (!ModelToolNamePattern().IsMatch(value!))
        {
            error = "Modell-Toolnamen müssen aus mindestens zwei kleingeschriebenen Punktsegmenten bestehen.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryValidateToken(string? value, string fieldName, out string error)
    {
        if (!TryValidateCommonIdentifier(value, fieldName, out error)) return false;
        if (!TokenPattern().IsMatch(value!))
        {
            error = $"{fieldName} muss kleingeschriebenes Kebab-Case sein.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool IsValidSemanticVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 96
        && SemanticVersionPattern().IsMatch(value);

    public static bool IsProductNeutralPublicIdentifier(string value) =>
        !value.StartsWith("go.", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("gowinui", StringComparison.OrdinalIgnoreCase);

    private static bool TryValidateCommonIdentifier(string? value, string fieldName, out string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"{fieldName} fehlt.";
            return false;
        }

        if (value.Length > MaximumIdentifierLength)
        {
            error = $"{fieldName} überschreitet {MaximumIdentifierLength} Zeichen.";
            return false;
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
        {
            error = $"{fieldName} muss kleingeschrieben sein und darf keine äußeren Leerzeichen enthalten.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

public static class BuiltInExtensionIds
{
    public const string Coding = "builtin.coding";
    public const string Web = "builtin.web";
    public const string Media = "builtin.media";
    public const string Image = "builtin.image";
    public const string Speech = "builtin.speech";
    public const string Documents = "builtin.documents";
    public const string Workspace = "builtin.workspace";
    public const string Audiobook = "builtin.audiobook";
    public const string Workflows = "builtin.workflows";
    public const string Memory = "builtin.memory";

    public static IReadOnlyList<string> All { get; } =
    [
        Coding, Web, Media, Image, Speech, Documents, Workspace, Audiobook,
        Workflows, Memory,
    ];
}

public static class BuiltInActionIds
{
    public const string AttachFilesAndFolders = BuiltInExtensionIds.Workspace + "/attach-files-and-folders";
    public const string WebSearch = BuiltInExtensionIds.Web + "/web-search";
    public const string DeepResearch = BuiltInExtensionIds.Web + "/deep-research";
    public const string ImageAnalysis = BuiltInExtensionIds.Media + "/image-analysis";
    public const string AudioAnalysis = BuiltInExtensionIds.Media + "/audio-analysis";
    public const string VideoAnalysis = BuiltInExtensionIds.Media + "/video-analysis";
    public const string CreateDocument = BuiltInExtensionIds.Documents + "/create";
    public const string GenerateImage = BuiltInExtensionIds.Image + "/generate";
    public const string CreateAudiobook = BuiltInExtensionIds.Audiobook + "/create";
    public const string ExportChatPdf = BuiltInExtensionIds.Documents + "/export-chat-pdf";
    public const string Translate = BuiltInExtensionIds.Speech + "/translate";
    public const string ReadAloud = BuiltInExtensionIds.Speech + "/read-aloud";
    public const string LiveCaptions = BuiltInExtensionIds.Speech + "/live-captions";
    public const string OpenWorkflowLibrary = BuiltInExtensionIds.Workflows + "/open-library";
    public const string OpenMemoryLibrary = BuiltInExtensionIds.Memory + "/open-library";
    public const string PlanMode = BuiltInExtensionIds.Coding + "/plan-mode";
}

public static class ToolSelectorNames
{
    public const string Canonical = "assistant.selectTool";
    public const string LegacyReadAlias = "go.selectTool";

    public static string NormalizeForRead(string selector) =>
        string.Equals(selector, LegacyReadAlias, StringComparison.Ordinal)
            ? Canonical
            : selector;

    public static void EnsureWritable(string selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        if (!string.Equals(selector, Canonical, StringComparison.Ordinal))
            throw new ArgumentException($"Neue Daten dürfen ausschließlich den Selektor '{Canonical}' verwenden.", nameof(selector));
    }
}

public static class ExtensionPackageFormat
{
    public const string FileExtension = ".aext";
    public const string ManifestEntryName = "extension.json";
    public const string SignatureEntryName = "signature.json";
    public const int CurrentSchemaVersion = 1;
}
