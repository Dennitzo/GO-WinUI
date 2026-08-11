using Microsoft.Extensions.Logging;

namespace GoWinUI.App.Services;

internal static partial class AppLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Warning, Message = "LM Studio status check failed")]
    public static partial void LmStudioStatusFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "Final window state could not be saved")]
    public static partial void WindowStateSaveFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Critical, Message = "Unhandled application exception: {Details}")]
    public static partial void UnhandledApplicationException(ILogger logger, Exception? exception, string details);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Application shutdown cleanup failed")]
    public static partial void ShutdownCleanupFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error, Message = "Navigation to page {PageType} failed")]
    public static partial void NavigationFailed(ILogger logger, Exception exception, string pageType);

    [LoggerMessage(EventId = 1100, Level = LogLevel.Error, Message = "WebView2 initialization failed")]
    public static partial void WebViewInitializationFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Error, Message = "Assistant UI request {RequestType} failed")]
    public static partial void AssistantRequestFailed(ILogger logger, Exception exception, string requestType);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Warning, Message = "WebView bridge rejected origin {Origin}")]
    public static partial void WebBridgeOriginRejected(ILogger logger, string origin);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Warning, Message = "WebView bridge rejected an invalid {FailureKind} message")]
    public static partial void WebBridgeMessageRejected(ILogger logger, string failureKind);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Warning, Message = "Assistant draft could not be flushed")]
    public static partial void AssistantDraftFlushFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1200, Level = LogLevel.Error, Message = "Project UI action failed")]
    public static partial void ProjectActionFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Warning, Message = "Thumbnail generation failed for project asset {AssetId}")]
    public static partial void AssetThumbnailGenerationFailed(ILogger logger, Exception exception, Guid assetId);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Debug, Message = "Thumbnail loading failed for project asset {AssetId}")]
    public static partial void AssetThumbnailLoadingFailed(ILogger logger, Exception exception, Guid assetId);

    [LoggerMessage(EventId = 1300, Level = LogLevel.Error, Message = "Log export failed")]
    public static partial void LogExportFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1400, Level = LogLevel.Error, Message = "Settings UI action failed")]
    public static partial void SettingsActionFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1500, Level = LogLevel.Information, Message = "BricsCAD bridge listening on loopback port {Port}")]
    public static partial void BricsCadBridgeStarted(ILogger logger, int port);

    [LoggerMessage(EventId = 1501, Level = LogLevel.Warning, Message = "BricsCAD bridge could not be started")]
    public static partial void BricsCadBridgeStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1502, Level = LogLevel.Information, Message = "BricsCAD plugin connected: version {PluginVersion}, build {PluginBuildId}")]
    public static partial void BricsCadPluginConnected(ILogger logger, string pluginVersion, string pluginBuildId);

    [LoggerMessage(EventId = 1503, Level = LogLevel.Information, Message = "BricsCAD plugin disconnected: {Reason}")]
    public static partial void BricsCadPluginDisconnected(ILogger logger, string reason);

    [LoggerMessage(EventId = 1504, Level = LogLevel.Information, Message = "BricsCAD capabilities received: {PropertyCount} top-level properties")]
    public static partial void BricsCadCapabilitiesReceived(ILogger logger, int propertyCount);

    [LoggerMessage(EventId = 1505, Level = LogLevel.Debug, Message = "BricsCAD event received: {EventName}")]
    public static partial void BricsCadEventReceived(ILogger logger, string eventName);

    [LoggerMessage(EventId = 1506, Level = LogLevel.Warning, Message = "BricsCAD bridge diagnostic: {Details}")]
    public static partial void BricsCadBridgeDiagnostic(ILogger logger, Exception? exception, string details);
}
