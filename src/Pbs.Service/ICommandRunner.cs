namespace Pbs.Service;

public interface ICommandRunner
{
    Task<int> RunAsync(CliCommand command, string jobId, CancellationToken cancellationToken);

    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
