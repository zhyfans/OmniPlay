namespace OmniPlay.Core.Models;

public sealed record HealthStatus(
    string Service,
    string Version,
    string Environment,
    string RootDirectory,
    string DatabasePath,
    bool DatabaseReady,
    DateTimeOffset ServerTime);

