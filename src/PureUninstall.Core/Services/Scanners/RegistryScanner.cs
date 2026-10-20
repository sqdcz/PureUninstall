using Microsoft.Win32;
using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services.Scanners;

/// <summary>
/// Finds leftover registry items: the app's own Uninstall entry, Software keys,
/// App Paths, MUI cache values and Run/RunOnce startup values.
/// </summary>
public static class RegistryScanner
{
    private static IEnumerable<(RegistryHive Hive, RegistryView View)> HiveViews()
    {
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64);
        yield return (RegistryHive.LocalMachine, RegistryView.Registry32);
        yield return (RegistryHive.CurrentUser, RegistryView.Default);
    }

    private static string HiveName(RegistryHive hive) =>
        hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";

    private static string BuildPath(RegistryHive hive, string subPath) =>
        $@"{HiveName(hive)}\{subPath}";

    public static List<ResidualItem> Scan(AppEntry app, bool includeAggressive)
    {
        var items = new List<ResidualItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = MatchHelper.Tokens(app.DisplayName);

        // 1. the app's own uninstall entry
        if (!string.IsNullOrEmpty(app.RegistryKeyPath) && seen.Add(app.RegistryKeyPath))
        {
            items.Add(new ResidualItem
            {
                Kind = ResidualKind.RegistryKey,
                Path = app.RegistryKeyPath,
                Detail = "卸载条目（删除后「设置/控制面板」不再显示该软件）",
                RequiresAdmin = app.Hive == RegistryHive.LocalMachine,
                Hive = app.Hive,
                View = app.View,
            });
        }

        foreach (var (hive, view) in HiveViews())
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);

            // 2. SOFTWARE\<name> and SOFTWARE\<publisher>\<name>
            using (var software = baseKey.OpenSubKey(@"SOFTWARE"))
            {
                if (software is not null)
                {
                    foreach (var child in software.GetSubKeyNames())
                    {
                        if (MatchHelper.IsPotentialMatch(child, tokens))
                            AddKey(items, seen, hive, view, $@"SOFTWARE\{child}");
                        using var childKey = software.OpenSubKey(child);
                        if (childKey is null) continue;
                        foreach (var grand in childKey.GetSubKeyNames())
                        {
                            if (MatchHelper.IsPotentialMatch(grand, tokens))
                                AddKey(items, seen, hive, view, $@"SOFTWARE\{child}\{grand}");
                        }
                    }
                }
            }

            // 3. App Paths
            using (var appPaths = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"))
            {
                if (appPaths is not null)
                {
                    foreach (var child in appPaths.GetSubKeyNames())
                        if (MatchHelper.IsPotentialMatch(child, tokens))
                            AddKey(items, seen, hive, view, $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{child}");
                }
            }

            // 4. MUI cache values
            ScanValueKey(baseKey, @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
                hive, view, tokens, items, seen);

            // 5. Run / RunOnce startup values
            foreach (var runPath in new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
            })
            {
                ScanValueKey(baseKey, runPath, hive, view, tokens, items, seen, "启动项");
            }

            if (includeAggressive)
            {
                // 6. Uninstaller leftovers: orphaned Uninstall keys with matching display names
                using (var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (uninstall is not null)
                    {
                        foreach (var child in uninstall.GetSubKeyNames())
                        {
                            var display = BuildPath(hive, $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{child}");
                            if (!seen.Add(display)) continue;
                            using var sub = uninstall.OpenSubKey(child);
                            var displayName = sub?.GetValue("DisplayName") as string;
                            if (!string.IsNullOrWhiteSpace(displayName) &&
                                MatchHelper.IsPotentialMatch(displayName.Trim(), tokens))
                            {
                                items.Add(new ResidualItem
                                {
                                    Kind = ResidualKind.RegistryKey,
                                    Path = display,
                                    Detail = "卸载条目（显示名匹配）",
                                    RequiresAdmin = hive == RegistryHive.LocalMachine,
                                    Hive = hive,
                                    View = view,
                                });
                            }
                        }
                    }
                }
            }
        }

        return items;
    }

    private static void AddKey(List<ResidualItem> items, HashSet<string> seen,
        RegistryHive hive, RegistryView view, string subPath)
    {
        var display = BuildPath(hive, subPath);
        if (!seen.Add(display)) return;
        items.Add(new ResidualItem
        {
            Kind = ResidualKind.RegistryKey,
            Path = display,
            Detail = "软件配置项",
            RequiresAdmin = hive == RegistryHive.LocalMachine,
            Hive = hive,
            View = view,
        });
    }

    private static void ScanValueKey(RegistryKey baseKey, string subPath,
        RegistryHive hive, RegistryView view, List<string> tokens,
        List<ResidualItem> items, HashSet<string> seen, string detail = "引用缓存值")
    {
        using var key = baseKey.OpenSubKey(subPath);
        if (key is null) return;

        foreach (var valueName in key.GetValueNames())
        {
            var data = key.GetValue(valueName) as string;
            if (string.IsNullOrWhiteSpace(data)) continue;
            if (!MatchHelper.IsPotentialMatch(valueName, tokens) && !MatchHelper.IsPotentialMatch(data, tokens))
                continue;

            var display = BuildPath(hive, subPath);
            var id = $"{display}|{valueName}";
            if (!seen.Add(id)) continue;

            items.Add(new ResidualItem
            {
                Kind = ResidualKind.RegistryValue,
                Path = display,
                ValueName = valueName,
                Detail = $"值: {valueName}（{detail}）",
                RequiresAdmin = hive == RegistryHive.LocalMachine,
                Hive = hive,
                View = view,
            });
        }
    }
}
