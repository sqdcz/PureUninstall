using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services.Scanners;

/// <summary>Scans startup folders and Start Menu / Desktop shortcuts referencing the app.</summary>
public static class StartupScanner
{
    public static List<ResidualItem> Scan(AppEntry app)
    {
        var items = new List<ResidualItem>();
        var tokens = MatchHelper.Tokens(app.DisplayName);
        if (tokens.Count == 0) return items;

        var userStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        var allStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

        // 1. startup folders (per-user + all-users)
        var startupDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),        // per-user
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), // all users
        };

        // 2. Start Menu\Programs + Desktop shortcut folders (Geek cleans these too)
        var shortcutDirs = new[]
        {
            Path.Combine(userStartMenu, "Programs"),
            Path.Combine(allStartMenu, "Programs"),
            userDesktop,
            publicDesktop,
        };

        foreach (var (dir, kind) in startupDirs.Select(d => (d, "启动文件夹"))
                         .Concat(shortcutDirs.Select(d => (d, "快捷方式"))))
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            var allUsers = dir.StartsWith(allStartMenu, StringComparison.OrdinalIgnoreCase)
                        || dir.StartsWith(publicDesktop, StringComparison.OrdinalIgnoreCase)
                        || dir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase);

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!MatchHelper.IsPotentialMatch(name, tokens)) continue;
                items.Add(new ResidualItem
                {
                    Kind = ResidualKind.StartupEntry,
                    Path = file,
                    Detail = kind + (allUsers ? "（所有用户）" : "（当前用户）"),
                    RequiresAdmin = allUsers,
                });
            }
        }
        return items;
    }
}
