using Microsoft.Win32;

namespace Pbs.WinFsp;

/// <summary>
/// Detect-and-require support for WinFsp, the mount engine used on Windows Server Core (issue #29).
/// The managed binding (<c>winfsp-msil.dll</c>) ships with our app via the <c>winfsp.net</c> NuGet
/// package, but the native driver + <c>winfsp-x64.dll</c> must be installed on the machine. This client
/// does not bundle or redistribute the WinFsp driver — it detects it and, when absent, points the user
/// at the installer (mirrors the WNBD posture; see <c>docs/WINFSP.md</c>).
/// </summary>
public static class WinFspAvailability
{
    /// <summary>Where to obtain WinFsp.</summary>
    public const string DownloadUrl = "https://winfsp.dev/";

    /// <summary>
    /// Guidance shown when WinFsp is not installed, so <c>mount --provider winfsp</c> (or <c>auto</c> on
    /// Server Core) fails with an install path rather than a bare error.
    /// </summary>
    public static string NotFoundGuidance =>
        "WinFsp was not found — it is required for 'mount --provider winfsp' (the mount engine on Server Core,\n" +
        "  where ProjFS is unavailable).\n" +
        "  WinFsp is a third-party filesystem driver (GPLv3 with the WinFsp FLOSS exception, AGPL-3.0 compatible).\n" +
        $"  Install it as Administrator from: {DownloadUrl}\n" +
        "  (Checked the WinFsp registry InstallDir and the native winfsp-x64.dll under it.)\n" +
        "  Install and licensing details: docs/WINFSP.md";

    /// <summary>True when the native WinFsp driver package is installed and its native DLL is present.</summary>
    public static bool IsInstalled() =>
        TryGetInstallDir(out var dir) && File.Exists(Path.Combine(dir, "bin", NativeLibraryName));

    /// <summary>The WinFsp install directory recorded in the registry, or null when not installed.</summary>
    public static string? InstallDirectory => TryGetInstallDir(out var dir) ? dir : null;

    private static string NativeLibraryName => Environment.Is64BitProcess ? "winfsp-x64.dll" : "winfsp-x86.dll";

    // WinFsp records its install dir under HKLM\SOFTWARE\WOW6432Node\WinFsp (a 32-bit registry view key)
    // as the "InstallDir" value; the winfsp-msil binding resolves the native DLL through the same key.
    private static bool TryGetInstallDir(out string installDir)
    {
        installDir = string.Empty;
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = baseKey.OpenSubKey(@"Software\WinFsp");
            if (key?.GetValue("InstallDir") is string value && !string.IsNullOrWhiteSpace(value))
            {
                installDir = value;
                return true;
            }
        }
        catch
        {
            // Registry unavailable / access denied — treat as not installed.
        }

        return false;
    }
}
