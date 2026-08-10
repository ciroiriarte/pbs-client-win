using System.Text.Json;

namespace Pbs.Service;

public static class ServiceConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string DefaultConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "pbs-client-win", "config.json");

    public static string ResolveConfigPath(IReadOnlyList<string> args)
    {
        var cliPath = ReadOption(args, "--config");
        if (!string.IsNullOrWhiteSpace(cliPath))
        {
            return Path.GetFullPath(cliPath);
        }

        var envPath = Environment.GetEnvironmentVariable("PBS_SERVICE_CONFIG");
        return string.IsNullOrWhiteSpace(envPath) ? DefaultConfigPath : Path.GetFullPath(envPath);
    }

    public static async Task<ServiceConfig> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var config = await JsonSerializer.DeserializeAsync<ServiceConfig>(stream, JsonOptions, cancellationToken);
        return config ?? throw new InvalidOperationException($"Config file '{path}' is empty.");
    }

    public static string ResolveCliPath(ServiceConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.CliPath))
        {
            return config.CliPath;
        }

        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        return Path.Combine(AppContext.BaseDirectory, $"proxmox-backup-client{extension}");
    }

    private static string? ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == name && i + 1 < args.Count)
            {
                return args[i + 1];
            }

            var prefix = name + "=";
            if (args[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                return args[i][prefix.Length..];
            }
        }

        return null;
    }
}
