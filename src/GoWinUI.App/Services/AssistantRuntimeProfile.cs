using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GoWinUI.App.Services;

/// <summary>
/// Resolves the local assistant instance independently from the product display name.
/// New launchers use ASSISTANT_* variables; legacy GO variables are read only as
/// compatibility aliases so an application rename does not change profile identity.
/// </summary>
public sealed record AssistantRuntimeProfile(
    string Name,
    string ProductName,
    string DataDirectory,
    Uri GatewayUri,
    int NativePort,
    int NativeControlPort,
    string NativeStateDirectory,
    string NativeBinaryPath,
    string StackDataRoot,
    string InstanceKey)
{
    private static readonly Regex ProfileNamePattern = new(
        "^[a-z0-9][a-z0-9-]{0,31}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public bool HasExplicitProfileSelection { get; init; }
    public bool HasExplicitNativeBinaryPath { get; init; }

    public static AssistantRuntimeProfile Resolve() => ResolveCore();

    internal static AssistantRuntimeProfile ResolveForPublishedDirectory(string publishedDirectory)
    {
        if (string.IsNullOrWhiteSpace(publishedDirectory))
            throw new ArgumentException("Ein Veröffentlichungsverzeichnis ist erforderlich.", nameof(publishedDirectory));
        return ResolveCore(publishedDirectory);
    }

    private static AssistantRuntimeProfile ResolveCore(string? publishedDirectory = null)
    {
        var profileSelection = ResolveProfileSelection(
            Environment.GetEnvironmentVariable("ASSISTANT_PROFILE"),
            publishedDirectory);
        var profileName = profileSelection.ProfileName;
        var gatewayPort = ReadPort("ASSISTANT_GATEWAY_PORT", 8080);
        var nativePort = ReadPort("ASSISTANT_NATIVE_PORT", 8081);
        if (nativePort == ushort.MaxValue)
            throw new InvalidOperationException("ASSISTANT_NATIVE_PORT muss Platz für den nachfolgenden Kontrollport lassen.");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var legacyRoot = Path.Combine(localAppData, "GO");
        var requestedData = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ASSISTANT_DATA_ROOT"),
            Environment.GetEnvironmentVariable("GO_DATA_DIRECTORY"));
        var dataDirectory = requestedData is not null
            ? Path.GetFullPath(requestedData)
            : legacyRoot;

        var gatewayText = FirstNonEmpty(Environment.GetEnvironmentVariable("ASSISTANT_GATEWAY_URL"))
            ?? $"http://127.0.0.1:{gatewayPort.ToString(CultureInfo.InvariantCulture)}";
        if (!Uri.TryCreate(gatewayText, UriKind.Absolute, out var gateway)
            || gateway.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("ASSISTANT_GATEWAY_URL muss eine absolute HTTP- oder HTTPS-Adresse sein.");
        if (gateway.Port == nativePort || gateway.Port == nativePort + 1)
            throw new InvalidOperationException("Gateway-, Native- und Kontrollport müssen voneinander verschieden sein.");

        var stateDirectory = FirstNonEmpty(Environment.GetEnvironmentVariable("ASSISTANT_NATIVE_STATE_ROOT"))
            ?? Path.Combine(userProfile, ".go-winui", "native-runtime");
        var stackDataRoot = FirstNonEmpty(Environment.GetEnvironmentVariable("ASSISTANT_STACK_DATA_ROOT"))
            ?? Path.Combine(commonAppData, "GO-AI-Stack");
        var requestedNativeBinary = FirstNonEmpty(Environment.GetEnvironmentVariable("ASSISTANT_NATIVE_BINARY_PATH"));
        var nativeBinaryPath = requestedNativeBinary
            ?? ResolveNativeBinaryPath(stackDataRoot, userProfile);
        var productName = FirstNonEmpty(Environment.GetEnvironmentVariable("ASSISTANT_PRODUCT_NAME")) ?? "AI Assistent";
        var requestedInstanceKey = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ASSISTANT_INSTANCE_KEY"),
            Environment.GetEnvironmentVariable("GO_SMOKE_INSTANCE_KEY"));
        var instanceKey = requestedInstanceKey is null
            ? $"LocalAssistant.{profileName}"
            : $"LocalAssistant.{profileName}.{SanitizeInstanceKey(requestedInstanceKey)}";

        return new AssistantRuntimeProfile(
            profileName,
            productName,
            Path.GetFullPath(dataDirectory),
            gateway,
            nativePort,
            checked(nativePort + 1),
            Path.GetFullPath(stateDirectory),
            Path.GetFullPath(nativeBinaryPath),
            Path.GetFullPath(stackDataRoot),
            instanceKey)
        {
            HasExplicitProfileSelection = profileSelection.IsExplicit,
            HasExplicitNativeBinaryPath = requestedNativeBinary is not null,
        };
    }

    internal static string ResolveProfileName(string? requestedProfile, string? publishedDirectory = null) =>
        ResolveProfileSelection(requestedProfile, publishedDirectory).ProfileName;

    internal static string ResolveNativeBinaryPath(string stackDataRoot, string userProfile, Func<string, bool>? fileExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stackDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);
        fileExists ??= static path => File.Exists(path);
        var managed = Path.GetFullPath(Path.Combine(stackDataRoot, "bin", "llama-server.exe"));
        var unsloth = Path.GetFullPath(Path.Combine(userProfile, ".unsloth", "llama.cpp", "build", "bin", "Release", "llama-server.exe"));
        if (fileExists(managed)) return managed;
        if (fileExists(unsloth)) return unsloth;
        // Keep the managed path as the actionable error target when neither runtime exists.
        return managed;
    }

    private static (string ProfileName, bool IsExplicit) ResolveProfileSelection(
        string? requestedProfile,
        string? publishedDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(requestedProfile)) return (NormalizeProfile(requestedProfile), true);

        var directories = string.IsNullOrWhiteSpace(publishedDirectory)
            ? new[] { Path.GetDirectoryName(Environment.ProcessPath), AppContext.BaseDirectory }
            : new[] { publishedDirectory };
        var manifestPath = directories
            .Where(static directory => !string.IsNullOrWhiteSpace(directory))
            .Select(static directory => Path.Combine(Path.GetFullPath(directory!), "assistant-profile.json"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
        if (manifestPath is null) return ("stable", false);

        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = manifest.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var schemaVersion)
                || !schemaVersion.TryGetInt32(out var schema) || schema != 1
                || !root.TryGetProperty("profile", out var profile)
                || profile.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(profile.GetString()))
                throw new InvalidDataException("Das veröffentlichte Assistentenprofil ist unvollständig.");
            return (NormalizeProfile(profile.GetString()), true);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Das veröffentlichte Assistentenprofil ist ungültig: {manifestPath}", exception);
        }
    }

    private static string NormalizeProfile(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "stable" : value.Trim().ToLowerInvariant();
        if (!ProfileNamePattern.IsMatch(normalized))
            throw new InvalidOperationException("ASSISTANT_PROFILE enthält keine gültige Profilkennung.");
        if (!string.Equals(normalized, "stable", StringComparison.Ordinal))
            throw new InvalidOperationException("Es wird ausschließlich das Stable-Profil unterstützt.");
        return normalized;
    }

    private static int ReadPort(string variable, int fallback)
    {
        var text = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > ushort.MaxValue)
            throw new InvalidOperationException($"{variable} enthält keinen gültigen TCP-Port.");
        return port;
    }

    private static string SanitizeInstanceKey(string value)
    {
        var safe = Regex.Replace(value.Trim(), "[^A-Za-z0-9_.-]", "-", RegexOptions.CultureInvariant);
        return safe.Length > 64 ? safe[..64] : safe;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
