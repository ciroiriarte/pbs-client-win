using System.Diagnostics;

namespace Pbs.Service;

public static class ServiceInstaller
{
    public const string ServiceName = "pbs-client-win";
    public const string DisplayName = "pbs-client-win Backup Scheduler";

    public static async Task<int> InstallAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("Windows service installation is only supported on Windows.");
            return 0;
        }

        var configPath = ServiceConfigLoader.ResolveConfigPath(args);
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Unable to resolve current executable path.");
        var binPath = $"\"{exePath}\" run --config \"{configPath}\"";

        var exit = await RunScAsync(["create", ServiceName, $"binPath= {binPath}", "start= auto", $"DisplayName= {DisplayName}"], cancellationToken);
        if (exit != 0)
        {
            return exit;
        }

        await RunScAsync(["description", ServiceName, "pbs-client-win backup scheduler. Not affiliated with Proxmox."], cancellationToken);
        return 0;
    }

    public static Task<int> UninstallAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("Windows service uninstall is only supported on Windows.");
            return Task.FromResult(0);
        }

        return RunScAsync(["delete", ServiceName], cancellationToken);
    }

    private static async Task<int> RunScAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        Console.Write(await stdout);
        Console.Error.Write(await stderr);
        return process.ExitCode;
    }
}
