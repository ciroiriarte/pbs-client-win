using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pbs.Service;

var command = args.FirstOrDefault();
var remainingArgs = command is "run" or "install" or "uninstall" or "validate-config"
    ? args.Skip(1).ToArray()
    : args;

return command switch
{
    "install" => await ServiceInstaller.InstallAsync(remainingArgs),
    "uninstall" => await ServiceInstaller.UninstallAsync(),
    "validate-config" => await ValidateConfigAsync(remainingArgs),
    "run" or null => await RunHostAsync(remainingArgs),
    "--help" or "-h" or "help" => PrintUsage(),
    _ => UnknownCommand(command)
};

static async Task<int> RunHostAsync(string[] args)
{
    var configPath = ServiceConfigLoader.ResolveConfigPath(args);

    // Service mode must stay alive even with no/invalid config, so unattended installs
    // (msiexec /qn with ServiceControl Start="install") and every boot-time auto-start
    // succeed before a real config.json is dropped. The service simply runs idle with no
    // jobs and logs why; `validate-config` is the strict gate admins should run instead.
    var config = await LoadConfigOrIdleAsync(configPath);

    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton<ICommandRunner, ProcessCommandRunner>();
    builder.Services.AddHostedService<SchedulerWorker>();

    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    });
    builder.Logging.AddProvider(new RollingFileLoggerProvider(DefaultLogDirectory()));

#if WINDOWS
    builder.Services.AddWindowsService(options => options.ServiceName = ServiceInstaller.ServiceName);
    builder.Logging.AddEventLog(options => options.SourceName = ServiceInstaller.ServiceName);
#endif

    using var host = builder.Build();
    await host.RunAsync();
    return 0;
}

static async Task<int> ValidateConfigAsync(string[] args)
{
    try
    {
        var configPath = ServiceConfigLoader.ResolveConfigPath(args);
        var config = await ServiceConfigLoader.LoadAsync(configPath);
        var errors = ConfigValidator.Validate(config);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                Console.Error.WriteLine(error);
            }

            return 1;
        }

        Console.WriteLine($"Config OK: {configPath}");
        var now = DateTimeOffset.UtcNow;
        foreach (var job in config.Jobs)
        {
            var runs = new CronSchedule(job.Schedule).GetNext(now, 3);
            Console.WriteLine($"{job.Id}: repository={job.Repository} backupId={ResolveBackupId(job)}");
            foreach (var run in runs)
            {
                Console.WriteLine($"  {run:O}");
            }
        }

        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static int PrintUsage()
{
    Console.WriteLine("pbs-client-win-service run|install|uninstall|validate-config [--config <path>]");
    Console.WriteLine("Not affiliated with Proxmox.");
    return 0;
}

static int UnknownCommand(string? command)
{
    Console.Error.WriteLine($"unknown command: {command}");
    return PrintUsage() == 0 ? 1 : 1;
}

static async Task<ServiceConfig> LoadConfigOrIdleAsync(string configPath)
{
    try
    {
        var config = await ServiceConfigLoader.LoadAsync(configPath);
        var errors = ConfigValidator.Validate(config);
        if (errors.Count == 0)
        {
            return config;
        }

        Console.Error.WriteLine($"Configuration '{configPath}' is invalid; the service will run idle (no jobs) until it is corrected:");
        foreach (var error in errors)
        {
            Console.Error.WriteLine($"  - {error}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not load configuration '{configPath}': {ex.Message}. The service will run idle (no jobs) until a valid config.json is present.");
    }

    return new ServiceConfig();
}

static string DefaultLogDirectory() =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "pbs-client-win", "logs");

static string ResolveBackupId(BackupJobConfig job) =>
    string.IsNullOrWhiteSpace(job.BackupId) ? Environment.MachineName.ToLowerInvariant() : job.BackupId;
