namespace Pbs.Service;

public static class CliCommandBuilder
{
    private static readonly string[] SecretEnvironmentNames =
    [
        "PBS_PASSWORD",
        "PBS_FINGERPRINT",
        "PBS_ENCRYPTION_PASSWORD",
        "PBS_KEY_PASSWORD"
    ];

    public static CliCommand BuildBackup(string cliPath, ServiceConfig config, BackupJobConfig job)
    {
        var args = new List<string> { "backup" };

        if (!string.IsNullOrWhiteSpace(job.ImageSource))
        {
            args.Add(job.ImageSource);
            args.Add("--image");
        }
        else
        {
            args.AddRange(job.Sources);
        }

        args.Add("--repository");
        args.Add(job.Repository);

        if (!string.IsNullOrWhiteSpace(job.Namespace))
        {
            args.Add("--ns");
            args.Add(job.Namespace);
        }

        if (!string.IsNullOrWhiteSpace(job.BackupId))
        {
            args.Add("--backup-id");
            args.Add(job.BackupId);
        }

        if (job.Vss)
        {
            args.Add("--vss");
        }

        if (!job.Incremental)
        {
            args.Add("--no-incremental");
        }

        if (!string.IsNullOrWhiteSpace(job.Keyfile))
        {
            args.Add("--keyfile");
            args.Add(job.Keyfile);
        }

        return new CliCommand(cliPath, args, BuildEnvironment(config, job));
    }

    public static CliCommand BuildPrune(string cliPath, ServiceConfig config, BackupJobConfig job)
    {
        if (job.Retention is null)
        {
            throw new ArgumentException("Retention is required to build a prune command.", nameof(job));
        }

        var args = new List<string>
        {
            "prune",
            "host/" + ResolveBackupId(job)
        };

        AddKeep(args, "--keep-last", job.Retention.KeepLast);
        AddKeep(args, "--keep-daily", job.Retention.KeepDaily);
        AddKeep(args, "--keep-weekly", job.Retention.KeepWeekly);
        AddKeep(args, "--keep-monthly", job.Retention.KeepMonthly);

        args.Add("--repository");
        args.Add(job.Repository);

        if (!string.IsNullOrWhiteSpace(job.Namespace))
        {
            args.Add("--ns");
            args.Add(job.Namespace);
        }

        return new CliCommand(cliPath, args, BuildEnvironment(config, job));
    }

    public static string LockKey(BackupJobConfig job) =>
        job.Repository + "\n" + ResolveBackupId(job);

    private static Dictionary<string, string> BuildEnvironment(ServiceConfig config, BackupJobConfig job)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in config.Env)
        {
            environment[key] = value;
        }

        foreach (var (key, value) in job.Env)
        {
            environment[key] = value;
        }

        if (!string.IsNullOrWhiteSpace(job.Keyfile))
        {
            environment["PBS_KEYFILE"] = job.Keyfile;
        }

        return environment;
    }

    private static void AddKeep(List<string> args, string name, int? value)
    {
        if (value is not null)
        {
            args.Add(name);
            args.Add(value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static string ResolveBackupId(BackupJobConfig job) =>
        string.IsNullOrWhiteSpace(job.BackupId) ? Environment.MachineName.ToLowerInvariant() : job.BackupId;

    public static bool ArgumentsContainKnownSecret(CliCommand command)
    {
        foreach (var secretName in SecretEnvironmentNames)
        {
            if (command.Environment.TryGetValue(secretName, out var secret) &&
                !string.IsNullOrEmpty(secret) &&
                command.Arguments.Any(arg => arg.Contains(secret, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}
