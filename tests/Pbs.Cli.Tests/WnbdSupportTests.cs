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

    [Theory]
    [InlineData(-1073741515, true)]   // 0xC0000135 STATUS_DLL_NOT_FOUND
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    public void IsMissingRuntime_OnlyMatchesTheDllNotFoundExitCode(int exitCode, bool expected)
    {
        Assert.Equal(expected, WnbdSupport.IsMissingRuntime(exitCode));
    }

    [Fact]
    public void MissingRuntimeGuidance_PointsAtTheVcRedistAndSymptom()
    {
        var g = WnbdSupport.MissingRuntimeGuidance;

        Assert.Contains("vc_redist.x64.exe", g);   // the exact installer
        Assert.Contains("aka.ms", g);              // download URL host
        Assert.Contains("0xC0000135", g);          // the observed symptom
        Assert.Contains("docs/WNBD.md", g);        // pointer to full docs
    }
}
