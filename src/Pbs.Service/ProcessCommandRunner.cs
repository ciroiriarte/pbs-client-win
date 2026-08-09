using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Pbs.Service;

public sealed class ProcessCommandRunner(ILogger<ProcessCommandRunner> logger) : ICommandRunner
{
    private readonly ConcurrentDictionary<int, Process> _running = new();

    public async Task<int> RunAsync(CliCommand command, string jobId, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        foreach (var argument in command.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in command.Environment)
        {
            process.StartInfo.Environment[key] = value;
        }

        logger.LogInformation("Job {JobId}: starting {FileName} {Arguments}", jobId, command.FileName, command.ArgumentsForLog);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{command.FileName}'.");
        }

        _running[process.Id] = process;

        var stdout = PumpOutputAsync(process.StandardOutput, jobId, LogLevel.Information, cancellationToken);
        var stderr = PumpOutputAsync(process.StandardError, jobId, LogLevel.Warning, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
            logger.LogInformation("Job {JobId}: process exited with code {ExitCode}", jobId, process.ExitCode);
            return process.ExitCode;
        }
        finally
        {
            _running.TryRemove(process.Id, out _);
        }
    }

    public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_running.IsEmpty)
        {
            return;
        }

        logger.LogInformation("Waiting up to {TimeoutSeconds}s for {Count} child process(es) to finish.", timeout.TotalSeconds, _running.Count);
        var deadline = Task.Delay(timeout, cancellationToken);
        while (!_running.IsEmpty && !deadline.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        if (_running.IsEmpty)
        {
            return;
        }

        foreach (var process in _running.Values)
        {
            try
            {
                logger.LogWarning("Killing child process {ProcessId} after shutdown timeout.", process.Id);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogDebug(ex, "Child process {ProcessId} could not be killed.", process.Id);
            }
        }
    }

    private async Task PumpOutputAsync(StreamReader reader, string jobId, LogLevel level, CancellationToken cancellationToken)
    {
        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is not null)
            {
                logger.Log(level, "Job {JobId}: {Output}", jobId, line);
            }
        }
    }
}
