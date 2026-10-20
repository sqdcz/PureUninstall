using System.Management;
using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services.Scanners;

/// <summary>
/// Scans WMI event subscriptions (root\subscription) referencing the app.
/// These are a classic hiding place for both aggressive adware and broken
/// uninstallers that register permanent event consumers.
/// </summary>
public static class WmiScanner
{
    private const string Scope = @"\\.\root\subscription";

    private static readonly string[] Classes =
    {
        "__EventFilter",
        "CommandLineEventConsumer",
        "ActiveScriptEventConsumer",
    };

    public static List<ResidualItem> Scan(AppEntry app)
    {
        var items = new List<ResidualItem>();
        var tokens = MatchHelper.Tokens(app.DisplayName);
        if (tokens.Count == 0) return items;

        ManagementScope? scope = null;
        try
        {
            scope = new ManagementScope(Scope);
            scope.Connect();
        }
        catch
        {
            return items; // WMI unavailable or no permission
        }

        foreach (var className in Classes)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM {className}"));
                foreach (ManagementObject mo in searcher.Get())
                {
                    var text = $"{mo["Name"]} {mo["CommandLineTemplate"]} {mo["ScriptText"]} {mo["Query"]}";
                    if (!MatchHelper.IsPotentialMatch(text, tokens)) continue;

                    items.Add(new ResidualItem
                    {
                        Kind = ResidualKind.WmiInstance,
                        Path = $"{className}:{mo["Name"]}",
                        Detail = className,
                        RequiresAdmin = true,
                    });
                }
            }
            catch
            {
                // class not present / no permission
            }
        }
        return items;
    }

    public static bool Delete(ResidualItem item)
    {
        try
        {
            var idx = item.Path.IndexOf(':');
            if (idx <= 0) return false;
            var className = item.Path[..idx];
            var name = item.Path[(idx + 1)..].Replace("'", "''");

            var scope = new ManagementScope(Scope);
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery($"SELECT * FROM {className} WHERE Name = '{name}'"));
            foreach (ManagementObject mo in searcher.Get())
            {
                mo.Delete();
                return true;
            }
        }
        catch
        {
            // ignore
        }
        return false;
    }
}
