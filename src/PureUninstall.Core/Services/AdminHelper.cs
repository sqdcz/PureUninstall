using System.Diagnostics;
using System.Security.Principal;

namespace PureUninstall.Core.Services;

/// <summary>
/// Elevation helpers. The app runs as-invoker and re-launches itself elevated
/// on demand (works for both the portable exe and MSIX/Store builds, where
/// a requireAdministrator manifest is not allowed).
/// </summary>
public static class AdminHelper
{
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool RestartAsAdmin()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        try
        {
            var psi = new ProcessStartInfo(exe) { Verb = "runas", UseShellExecute = true };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false; // user declined UAC
        }
    }
}
