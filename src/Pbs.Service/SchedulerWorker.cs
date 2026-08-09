using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pbs.Service;

public sealed class SchedulerWorker(
    ServiceConfig config,
    ICommandRunner commandRunner,
    ILogger<SchedulerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StopChildTimeout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _runningTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScheduledJob> _jobs = config.Jobs.ToDictionary(
        job => job.Id,
        job => new ScheduledJob(job, new CronSchedule(job.Schedule)),
        StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("pbs-client-win service starting. Not affiliated with Proxmox.");
        logger.LogInformation("Loaded {JobCount} scheduled job(s).", _jobs.Count);

        var now = DateTimeOffset.UtcNow;
        foreach (var job in _jobs.Values)
        {
            job.NextRunUtc = job.Schedule.GetNext(now);
            logger.LogInformation("Job {JobId}: next scheduled run at {NextRunUtc:O}", job.Config.Id, job.NextRunUtc);
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = Tick(DateTimeOffset.UtcNow, stoppingToken);
                await Task.Delay(delay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        var running = _runningTasks.Values.Where(task => !task.IsCompleted).ToArray();
        if (running.Length > 0)
        {
            logger.LogInformation("Waiting for {Count} active job task(s) during shutdown.", running.Length);
            await Task.WhenAny(Task.WhenAll(running), Task.Delay(StopChildTimeout, cancellationToken));
        }

        await commandRunner.StopAsync(StopChildTimeout, cancellationToken);
    }

    internal TimeSpan Tick(DateTimeOffset nowUtc, CancellationToken stoppingToken)
    {
        DateTimeOffset? nearest = null;

        foreach (var job in _jobs.Values)
        {
            if (job.NextRunUtc is null)
            {
                continue;
            }

            if (job.NextRunUtc <= nowUtc)
            {
                job.NextRunUtc = job.Schedule.GetNext(nowUtc.AddSeconds(1));
                logger.LogInformation("Job {JobId}: next scheduled run at {NextRunUtc:O}", job.Config.Id, job.NextRunUtc);
                StartJob(job.Config, stoppingToken);
            }

            if (job.NextRunUtc is not null && (nearest is null || job.NextRunUtc < nearest))
            {
                nearest = job.NextRunUtc;
            }
        }

        if (nearest is null)
        {
            return IdleDelay;
        }

        var delay = nearest.Value - nowUtc;
        if (delay < TimeSpan.FromSeconds(1))
        {
            return TimeSpan.FromSeconds(1);
        }

        return delay < IdleDelay ? delay : IdleDelay;
    }

    private void StartJob(BackupJobConfig job, CancellationToken stoppingToken)
    {
        var task = Task.Run(() => RunJobAsync(job, stoppingToken), CancellationToken.None);
        _runningTasks[job.Id] = task;
    }

    private async Task RunJobAsync(BackupJobConfig job, CancellationToken stoppingToken)
    {
        var lockKey = CliCommandBuilder.LockKey(job);
        var gate = _locks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, stoppingToken))
        {
            logger.LogWarning("Job {JobId}: skipped because repository/backup-id key is already running.", job.Id);
            return;
        }

        try
        {
            logger.LogInformation("Job {JobId}: backup started.", job.Id);
            var cliPath = ServiceConfigLoader.ResolveCliPath(config);
            var backup = CliCommandBuilder.BuildBackup(cliPath, config, job);
            if (CliCommandBuilder.ArgumentsContainKnownSecret(backup))
            {
                throw new InvalidOperationException($"Job '{job.Id}' would expose a secret in argv.");
            }

            var backupExit = await commandRunner.RunAsync(backup, job.Id, CancellationToken.None);
            logger.LogInformation("Job {JobId}: backup finished with exit code {ExitCode}.", job.Id, backupExit);

            if (backupExit == 0 && job.Retention is not null)
            {
                logger.LogInformation("Job {JobId}: prune started.", job.Id);
                var prune = CliCommandBuilder.BuildPrune(cliPath, config, job);
                var pruneExit = await commandRunner.RunAsync(prune, job.Id, CancellationToken.None);
                logger.LogInformation("Job {JobId}: prune finished with exit code {ExitCode}.", job.Id, pruneExit);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId}: failed.", job.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed class ScheduledJob(BackupJobConfig config, CronSchedule schedule)
    {
        public BackupJobConfig Config { get; } = config;
        public CronSchedule Schedule { get; } = schedule;
        public DateTimeOffset? NextRunUtc { get; set; }
    }
}
