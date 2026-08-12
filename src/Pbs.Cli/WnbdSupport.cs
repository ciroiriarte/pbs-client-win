namespace Pbs.Cli;

/// <summary>
/// Helpers for the WNBD dependency used by <c>map</c>/<c>unmap</c>. WNBD is a third-party,
/// open-source (Cloudbase Solutions, LGPL-2.1) Windows kernel driver plus the
/// <c>wnbd-client</c> CLI and <c>libwnbd.dll</c>. This client follows a detect-and-require
/// model: it does not bundle or redistribute WNBD, it detects it and — when absent — tells
/// the user how to install it. See <c>docs/WNBD.md</c>.
/// </summary>
public static class WnbdSupport
{
    /// <summary>Where to obtain WNBD (bundled in the Ceph for Windows package).</summary>
    public const string InstallUrl = "https://cloudbase.it/ceph-for-windows/";

    /// <summary>
    /// Clear, actionable guidance shown when the WNBD driver / <c>wnbd-client</c> CLI is not
    /// available, so <c>map</c>/<c>unmap</c> fail with an install path rather than a bare error.
    /// </summary>
    public static string NotFoundGuidance =>
        "WNBD was not found — it is required for 'map'/'unmap' (image as a Windows block device).\n" +
        "  WNBD is a third-party driver + 'wnbd-client' CLI from Cloudbase Solutions, licensed LGPL-2.1.\n" +
        $"  Install it as Administrator from the Ceph for Windows package: {InstallUrl}\n" +
        "  (Checked --wnbd-client-path, PATH, C:\\wnbd, and the Ceph install dir.)\n" +
        "  Already installed elsewhere? Point at it with:  --wnbd-client-path <path-to-wnbd-client.exe>\n" +
        "  Install and licensing details: docs/WNBD.md";

    /// <summary>Where to get the Visual C++ 2015–2022 x64 redistributable that WNBD needs.</summary>
    public const string VcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    /// <summary>
    /// Exit code a process returns when it cannot start because a dependent DLL is missing:
    /// <c>STATUS_DLL_NOT_FOUND</c> = 0xC0000135 = -1073741515. WNBD's <c>wnbd-client.exe</c> /
    /// <c>libwnbd.dll</c> exit with this when the VC++ runtime is absent.
    /// </summary>
    public const int MissingRuntimeExitCode = -1073741515;

    /// <summary>True when a wnbd-client exit code indicates a missing dependent DLL (VC++ runtime).</summary>
    public static bool IsMissingRuntime(int exitCode) => exitCode == MissingRuntimeExitCode;

    /// <summary>
    /// Guidance shown when <c>wnbd-client</c> fails to start with 0xC0000135 — its
    /// <c>libwnbd.dll</c> needs the Visual C++ 2015–2022 x64 redistributable. We do not
    /// bundle it (WNBD is detect-and-require, not shipped by us), so we point the user at it.
    /// </summary>
    public static string MissingRuntimeGuidance =>
        "wnbd-client failed to start (exit 0xC0000135 — a required DLL is missing).\n" +
        "  WNBD's libwnbd.dll needs the Visual C++ 2015–2022 x64 redistributable.\n" +
        $"  Install it as Administrator: {VcRedistUrl}\n" +
        "  Then retry. Details: docs/WNBD.md";
}
