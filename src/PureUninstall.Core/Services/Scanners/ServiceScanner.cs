using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services.Scanners;

/// <summary>
/// Scans Windows services whose name/display name/binary path references the app.
/// Enumeration goes through WMI (Win32_Service) - no manual buffer marshalling.
/// Stopping uses the managed ServiceController; deletion uses SCM handles only.
/// </summary>
public static class ServiceScanner
{
    private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    private const uint SERVICE_ALL_ACCESS = 0xF01FF;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint dwAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr hService);

    public static List<ResidualItem> Scan(AppEntry app)
    {
        var items = new List<ResidualItem>();
        var tokens = MatchHelper.Tokens(app.DisplayName);
        if (tokens.Count == 0) return items;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new ObjectQuery("SELECT Name, DisplayName, PathName FROM Win32_Service"));

            foreach (ManagementObject mo in searcher.Get())
            {
                var name = mo["Name"]?.ToString() ?? "";
                if (name.Length == 0) continue;

                var display = mo["DisplayName"]?.ToString() ?? "";
                var path = mo["PathName"]?.ToString() ?? "";
                var haystack = $"{name} {display} {path}";
                if (!MatchHelper.IsPotentialMatch(haystack, tokens)) continue;
                if (IsCriticalSystemService(name)) continue;

                items.Add(new ResidualItem
                {
                    Kind = ResidualKind.Service,
                    Path = name,
                    Detail = $"{(string.IsNullOrEmpty(display) ? name : display)} | {path}",
                    RequiresAdmin = true,
                });
            }
        }
        catch
        {
            // WMI unavailable or no permission
        }
        return items;
    }

    /// <summary>Never suggest deleting services that keep Windows alive.</summary>
    public static bool IsCriticalSystemService(string name)
    {
        string[] critical =
        {
            "wuauserv", "bits", "cryptsvc", "msiserver", "trustedinstaller", "windefend",
            "mpssvc", "bfe", "dhcp", "dnscache", "lanmanworkstation", "lanmanserver",
            "eventlog", "plugplay", "power", "profisvc", "rpcss", "schedule",
            "securityhealthservice", "sense", "wbengine", "vss", "swprv", "samss",
            "lsass", "services", "smss", "csrss", "wininit", "winmgmt", "netprofm",
        };
        return critical.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Stops (if running) and deletes the service. Refuses critical system services.</summary>
    public static bool Delete(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return false;
        if (IsCriticalSystemService(serviceName)) return false;

        // stop via managed API (best effort)
        try
        {
            using var sc = new ServiceController(serviceName);
            if (sc.Status != ServiceControllerStatus.Stopped &&
                sc.Status != ServiceControllerStatus.StopPending)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
        }
        catch
        {
            // service missing or already stopped
        }

        var scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) return false;
        try
        {
            var svc = OpenServiceW(scm, serviceName, SERVICE_ALL_ACCESS);
            if (svc == IntPtr.Zero) return false;
            try
            {
                return DeleteService(svc);
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }
}
