using System.Text.Json.Serialization;

namespace Pbs.Service;

public sealed class ServiceConfig
{
    [JsonPropertyName("cliPath")]
    public string? CliPath { get; init; }

    [JsonPropertyName("jobs")]
    public List<BackupJobConfig> Jobs { get; init; } = [];

    [JsonPropertyName("env")]
    public Dictionary<string, string> Env { get; init; } = new(StringComparer.Ordinal);
}

public sealed class BackupJobConfig
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("repository")]
    public string Repository { get; init; } = string.Empty;

    [JsonPropertyName("sources")]
    public List<string> Sources { get; init; } = [];

    [JsonPropertyName("imageSource")]
    public string? ImageSource { get; init; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    [JsonPropertyName("backupId")]
    public string? BackupId { get; init; }

    [JsonPropertyName("vss")]
    public bool Vss { get; init; }

    [JsonPropertyName("incremental")]
    public bool Incremental { get; init; } = true;

    [JsonPropertyName("keyfile")]
    public string? Keyfile { get; init; }

    [JsonPropertyName("schedule")]
    public string Schedule { get; init; } = string.Empty;

    [JsonPropertyName("retention")]
    public RetentionConfig? Retention { get; init; }

    [JsonPropertyName("env")]
    public Dictionary<string, string> Env { get; init; } = new(StringComparer.Ordinal);
}

public sealed class RetentionConfig
{
    [JsonPropertyName("keepLast")]
    public int? KeepLast { get; init; }

    [JsonPropertyName("keepDaily")]
    public int? KeepDaily { get; init; }

    [JsonPropertyName("keepWeekly")]
    public int? KeepWeekly { get; init; }

    [JsonPropertyName("keepMonthly")]
    public int? KeepMonthly { get; init; }
}
