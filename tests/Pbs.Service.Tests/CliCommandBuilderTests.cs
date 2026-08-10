using Pbs.Service;

namespace Pbs.Service.Tests;

public sealed class CliCommandBuilderTests
{
    [Fact]
    public void BuildsBackupArgsWithoutSecretsInArgv()
    {
        var config = new ServiceConfig
        {
            Env =
            {
                ["PBS_PASSWORD"] = "super-secret-token",
                ["PBS_FINGERPRINT"] = "fingerprint-secret",
                ["PBS_ENCRYPTION_PASSWORD"] = "encryption-secret"
            }
        };
        var job = new BackupJobConfig
        {
            Id = "docs",
            Repository = "root@pam!token@pbs:store",
            Sources = ["docs.pxar:C:\\docs"],
            Namespace = "win",
            BackupId = "workstation",
            Vss = true,
            Incremental = false,
            Keyfile = "C:\\keys\\pbs.key",
            Schedule = "0 2 * * *"
        };

        var command = CliCommandBuilder.BuildBackup("proxmox-backup-client.exe", config, job);

        Assert.Equal("proxmox-backup-client.exe", command.FileName);
        Assert.Equal(
            [
                "backup",
                "docs.pxar:C:\\docs",
                "--repository",
                "root@pam!token@pbs:store",
                "--ns",
                "win",
                "--backup-id",
                "workstation",
                "--vss",
                "--no-incremental",
                "--keyfile",
                "C:\\keys\\pbs.key"
            ],
            command.Arguments);
        Assert.Equal("super-secret-token", command.Environment["PBS_PASSWORD"]);
        Assert.Equal("fingerprint-secret", command.Environment["PBS_FINGERPRINT"]);
        Assert.False(CliCommandBuilder.ArgumentsContainKnownSecret(command));
        Assert.DoesNotContain("super-secret-token", command.Arguments);
        Assert.DoesNotContain("fingerprint-secret", command.Arguments);
        Assert.DoesNotContain("encryption-secret", command.Arguments);
    }

    [Fact]
    public void BuildsImageBackupAndPruneArgs()
    {
        var config = new ServiceConfig
        {
            Env = { ["PBS_PASSWORD"] = "secret" }
        };
        var job = new BackupJobConfig
        {
            Id = "image",
            Repository = "root@pam!token@pbs:store",
            ImageSource = "disk.img:C:",
            Namespace = "servers",
            BackupId = "server01",
            Schedule = "*/30 * * * *",
            Retention = new RetentionConfig
            {
                KeepLast = 5,
                KeepDaily = 7,
                KeepWeekly = 4,
                KeepMonthly = 3
            }
        };

        var backup = CliCommandBuilder.BuildBackup("pbs-client-win", config, job);
        var prune = CliCommandBuilder.BuildPrune("pbs-client-win", config, job);

        Assert.Equal(["backup", "disk.img:C:", "--image", "--repository", "root@pam!token@pbs:store", "--ns", "servers", "--backup-id", "server01"], backup.Arguments);
        Assert.Equal(
            [
                "prune",
                "host/server01",
                "--keep-last",
                "5",
                "--keep-daily",
                "7",
                "--keep-weekly",
                "4",
                "--keep-monthly",
                "3",
                "--repository",
                "root@pam!token@pbs:store",
                "--ns",
                "servers"
            ],
            prune.Arguments);
        Assert.False(CliCommandBuilder.ArgumentsContainKnownSecret(prune));
    }
}
