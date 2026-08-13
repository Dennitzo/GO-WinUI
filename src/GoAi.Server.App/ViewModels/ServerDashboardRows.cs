namespace GoAi.Server.App.ViewModels;

public sealed record ModelStatusRow(
    string Name,
    string Role,
    string Context,
    string State,
    string DownloadState);

public sealed record GpuStatusRow(
    string Name,
    string Memory,
    string Utilization,
    string Temperature);

public sealed record ServiceStatusRow(
    string Name,
    string Endpoint,
    string State,
    string Detail);

public sealed record ApiKeyRow(
    string KeyId,
    string Name,
    string Created,
    string LastUsed,
    bool CanRevoke);
