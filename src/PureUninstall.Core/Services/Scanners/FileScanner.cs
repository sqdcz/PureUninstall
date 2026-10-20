using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services.Scanners;

/// <summary>
/// Finds leftover files/folders: install location, uninstaller's own folder,
/// and per-name AppData/Roaming/ProgramData folders.
/// </summary>
public static class FileScanner
{
    /// <summary>Never offer to delete these (or anything above them).</summary>
    private static List<string> ProtectedPaths()
    {
        var list = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try { list.Add(Path.GetFullPath(p).TrimEnd('\\', '/')); } catch { /* ignore */ }
        }
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)); // ProgramData
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));       // Roaming
        Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));  // Local
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add(Path.GetPathRoot(Environment.SystemDirectory));
        return list;
    }

    /// <summary>Paths under these need admin rights to modify (still protected from deletion as roots).</summary>
    private static List<string> AdminRequiredPrefixes()
    {
        var list = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try { list.Add(Path.GetFullPath(p).TrimEnd('\\', '/')); } catch { /* ignore */ }
        }
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)); // ProgramData
        return list;
    }

    private static bool IsUnderProtectedRoot(string fullPath)
    {
        foreach (var prot in AdminRequiredPrefixes())
        {
            if (fullPath.StartsWith(prot + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Safety gate: no protected dirs, no ancestors of protected dirs, no drive roots, no our own dir.</summary>
    public static bool IsSafeCandidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\', '/');
            if (full.Length <= 3) return false;

            foreach (var prot in ProtectedPaths())
            {
                if (full.Equals(prot, StringComparison.OrdinalIgnoreCase)) return false;
                if (prot.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) return false;
            }

            var self = AppContext.BaseDirectory?.TrimEnd('\\', '/');
            if (!string.IsNullOrEmpty(self))
            {
                if (full.Equals(self, StringComparison.OrdinalIgnoreCase)) return false;
                if (self.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static long MeasureSize(string dir)
    {
        long total = 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var f in Directory.EnumerateFiles(dir, "*", options))
            {
                try { total += new FileInfo(f).Length; } catch { /* skip */ }
            }
        }
        catch
        {
            // ignore
        }
        return total;
    }

    public static List<ResidualItem> Scan(AppEntry app, bool includeAggressive)
    {
        var items = new List<ResidualItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateRoots = new List<string>();

        if (!string.IsNullOrWhiteSpace(app.InstallLocation))
            candidateRoots.Add(app.InstallLocation);

        if (!string.IsNullOrWhiteSpace(app.UninstallString))
        {
            var (file, _) = Uninstaller.ParseCommand(app.UninstallString);
            if (file.Length > 0)
            {
                try
                {
                    var dir = Path.GetDirectoryName(file);
                    if (!string.IsNullOrEmpty(dir)) candidateRoots.Add(dir);
                }
                catch { /* ignore */ }
            }
        }

        var tokens = MatchHelper.Tokens(app.DisplayName);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        foreach (var token in tokens)
        {
            candidateRoots.Add(Path.Combine(roaming, token));
            candidateRoots.Add(Path.Combine(local, token));
            candidateRoots.Add(Path.Combine(local, token + " Cache"));
            candidateRoots.Add(Path.Combine(programData, token));
        }

        foreach (var root in candidateRoots)
        {
            if (!IsSafeCandidate(root)) continue;
            if (!Directory.Exists(root)) continue;
            if (!seen.Add(root)) continue;
            items.Add(new ResidualItem
            {
                Kind = ResidualKind.Directory,
                Path = root,
                SizeBytes = MeasureSize(root),
                RequiresAdmin = IsUnderProtectedRoot(Path.GetFullPath(root).TrimEnd('\\', '/')),
            });
        }

        // aggressive: look one level inside well-known data roots for name matches
        if (includeAggressive && tokens.Count > 0)
        {
            foreach (var baseDir in new[] { roaming, local, programData })
            {
                if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) continue;
                IEnumerable<string> subDirs;
                try { subDirs = Directory.EnumerateDirectories(baseDir); } catch { continue; }
                foreach (var sub in subDirs)
                {
                    var name = Path.GetFileName(sub);
                    if (!MatchHelper.IsPotentialMatch(name, tokens)) continue;
                    if (!IsSafeCandidate(sub)) continue;
                    if (!seen.Add(sub)) continue;
                    items.Add(new ResidualItem
                    {
                        Kind = ResidualKind.Directory,
                        Path = sub,
                        SizeBytes = MeasureSize(sub),
                        RequiresAdmin = IsUnderProtectedRoot(Path.GetFullPath(sub).TrimEnd('\\', '/')),
                    });
                }
            }
        }

        return items;
    }
}
