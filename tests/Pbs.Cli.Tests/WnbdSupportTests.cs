using Pbs.Cli;
using Xunit;

namespace Pbs.Cli.Tests;

public class WnbdSupportTests
{
    [Fact]
    public void NotFoundGuidance_CoversInstallSourceAdminOverrideAndLicensing()
    {
        var g = WnbdSupport.NotFoundGuidance;

        Assert.Contains("cloudbase.it/ceph-for-windows", g);   // where to install from
        Assert.Contains("Administrator", g);                    // privilege requirement
        Assert.Contains("--wnbd-client-path", g);               // point at an existing install
        Assert.Contains("LGPL-2.1", g);                         // third-party licensing note
        Assert.Contains("docs/WNBD.md", g);                     // pointer to full docs
    }

    [Fact]
    public void InstallUrl_IsTheCephForWindowsPackage()
    {
        Assert.Equal("https://cloudbase.it/ceph-for-windows/", WnbdSupport.InstallUrl);
    }
}
