using System.Text.Json;
using Pbs.Service;

namespace Pbs.Service.Tests;

public sealed class ConfigTests
{
    [Fact]
    public void ParsesAndValidatesConfig()
    {
        const string json = """
        {
          "cliPath": "C:\\Program Files\\pbs-client-win\\pbs-client-win.exe",
          "env": {
            "PBS_PASSWORD": "secret",
            "PBS_FINGERPRINT": "fingerprint"
          },
          "jobs": [
            {
              "id": "docs",
              "repository": "root@pam!token@pbs:store",
              "sources": [ "docs.pxar:C:\\docs" ],
              "namespace": "win",
              "backupId": "workstation",
              "vss": true,
              "incremental": true,
              "keyfile": "C:\\keys\\pbs.key",
              "schedule": "0 2 * * *",
              "retention": { "keepLast": 3, "keepDaily": 7 }
            }
          ]
        }
        """;

        var config = JsonSerializer.Deserialize<ServiceConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(config);
        Assert.Empty(ConfigValidator.Validate(config));
        Assert.Equal("docs", config.Jobs[0].Id);
        Assert.Equal("secret", config.Env["PBS_PASSWORD"]);
        Assert.Equal("C:\\keys\\pbs.key", config.Jobs[0].Keyfile);
    }

    [Fact]
    public void ValidationRejectsMissingRequiredFields()
    {
        var config = new ServiceConfig
        {
            Jobs =
            [
                new BackupJobConfig
                {
                    Id = "bad",
                    Sources = [],
                    Schedule = "not cron"
                }
            ]
        };

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("repository is required", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("either sources or imageSource", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("schedule is invalid", StringComparison.Ordinal));
    }
}
