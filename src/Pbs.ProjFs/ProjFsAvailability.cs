namespace Pbs.ProjFs;

public static class ProjFsAvailability
{
    public const string EnableGuidance =
        "ProjFS is not enabled. Run an elevated PowerShell prompt and execute: " +
        "Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -All";

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var driverPath = Path.Combine(Environment.SystemDirectory, "drivers", "prjflt.sys");
        return File.Exists(driverPath);
    }
}
