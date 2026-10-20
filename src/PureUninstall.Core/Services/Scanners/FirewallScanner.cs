using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services.Scanners;

/// <summary>
/// Scans Windows Firewall rules (COM HNetCfg.FwPolicy2) referencing the app.
/// </summary>
public static class FirewallScanner
{
    private static dynamic? GetPolicy()
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return null;
            return Activator.CreateInstance(type);
        }
        catch
        {
            return null;
        }
    }

    public static List<ResidualItem> Scan(AppEntry app)
    {
        var items = new List<ResidualItem>();
        var tokens = MatchHelper.Tokens(app.DisplayName);
        if (tokens.Count == 0) return items;

        var policy = GetPolicy();
        if (policy is null) return items;

        try
        {
            dynamic rules = policy.Rules;
            int count = rules.Count;
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    dynamic rule = rules.Item(i);
                    var name = SafeString(rule.Name) ?? "";
                    var appName = SafeString(rule.ApplicationName) ?? "";
                    var service = SafeString(rule.ServiceName) ?? "";
                    var haystack = $"{name} {appName} {service}";
                    if (!MatchHelper.IsPotentialMatch(haystack, tokens)) continue;

                    items.Add(new ResidualItem
                    {
                        Kind = ResidualKind.FirewallRule,
                        Path = name,
                        Detail = string.IsNullOrEmpty(appName) ? "防火墙规则" : $"程序: {appName}",
                        RequiresAdmin = true,
                    });
                }
                catch { /* skip unreadable rule */ }
            }
        }
        catch
        {
            // COM firewall API unavailable
        }
        return items;
    }

    private static string? SafeString(object value)
    {
        try { return value as string; } catch { return null; }
    }

    public static bool Delete(ResidualItem item)
    {
        var policy = GetPolicy();
        if (policy is null) return false;
        try
        {
            policy.Rules.Remove(item.Path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
