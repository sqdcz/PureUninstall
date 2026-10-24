using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using PureUninstall.Core.Models;

namespace PureUninstall.App.Helpers;

/// <summary>
/// Extracts EXE/DLL icons to cached PNG files.
/// Returns a file path; the caller creates the BitmapImage on the UI thread.
///
/// 图标来源按优先级（Geek 的做法）：
///   1. UWP 包清单里的 Square44x44Logo（本身就是 PNG，直接用）
///   2. 注册表 DisplayIcon（最常见，但很多软件这条是空的）
///   3. 卸载程序 exe 的图标
///   4. 安装目录里「最像主程序」的那个 exe（名字匹配优先，否则取体积最大的）
/// 早先只做了前两条，导致近一半软件没有图标。
/// </summary>
public static class IconExtractor
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PureUninstall", "IconCache");

    private readonly record struct IconCandidate(string File, int Index);

    /// <summary>Resolves the best available icon file for an app (null when nothing found).</summary>
    public static string? GetIconFile(AppEntry entry, int size = 32)
    {
        // 1) UWP：包目录里的 logo 就是 png，直接拿来用
        if (entry.Kind == AppKind.Uwp)
        {
            var logo = TryGetUwpLogo(entry.InstallLocation);
            if (logo is not null) return logo;
        }

        // 2) DisplayIcon  3) 卸载程序 exe
        foreach (var candidate in EnumerateIconCandidates(entry))
        {
            var result = ExtractToPng(candidate.File, candidate.Index, size);
            if (result is not null) return result;
        }

        // 4) 安装目录里最像主程序的 exe
        var guessed = TryGuessMainExe(entry);
        if (guessed is not null)
            return ExtractToPng(guessed, 0, size);

        return null;
    }

    /// <summary>兼容旧调用：只看一个路径的图标来源。</summary>
    public static string? GetIconFile(string? iconPath, int size = 32)
    {
        if (string.IsNullOrWhiteSpace(iconPath)) return null;
        var (file, index) = SplitIconPath(iconPath);
        return ExtractToPng(file, index, size);
    }

    private static IEnumerable<IconCandidate> EnumerateIconCandidates(AppEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.DisplayIcon))
        {
            var (file, index) = SplitIconPath(entry.DisplayIcon);
            if (file.Length > 0 && File.Exists(file))
                yield return new IconCandidate(file, index);
        }

        if (!string.IsNullOrWhiteSpace(entry.UninstallString))
        {
            string file = string.Empty;
            try { (file, _) = Core.Services.Uninstaller.ParseCommand(entry.UninstallString); }
            catch { file = string.Empty; }
            if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
                yield return new IconCandidate(file, 0);
        }
    }

    private static (string file, int index) SplitIconPath(string raw)
    {
        var comma = raw.LastIndexOf(',');
        if (comma > 0)
        {
            var tail = raw[(comma + 1)..].Trim();
            if (int.TryParse(tail, out var idx))
                return (raw[..comma].Trim().Trim('"'), idx);
        }
        return (raw.Trim().Trim('"'), 0);
    }

    /// <summary>安装目录里挑「最像主程序」的 exe：名字匹配优先，否则体积最大。</summary>
    private static string? TryGuessMainExe(AppEntry entry)
    {
        var dir = entry.InstallLocation;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;

        try
        {
            string? best = null;
            long bestScore = -1;

            foreach (var exe in EnumerateExesShallow(dir, depth: 2, maxDirs: 40))
            {
                long sizeBytes;
                try { sizeBytes = new FileInfo(exe).Length; } catch { continue; }

                var score = sizeBytes;
                var nameNoExt = Path.GetFileNameWithoutExtension(exe);
                if (NameSimilar(nameNoExt, entry.DisplayName)) score += long.MaxValue / 4;
                if (LooksLikeHelper(nameNoExt)) score /= 64;   // 卸载/更新类程序不当主程序

                if (score > bestScore) { bestScore = score; best = exe; }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 广度优先找 exe，最多下探 depth 层、最多进 maxDirs 个子目录
    /// （WinGet 装的软件主程序常在两层子目录里，只看顶层会漏）。
    /// </summary>
    private static IEnumerable<string> EnumerateExesShallow(string root, int depth, int maxDirs)
    {
        var queue = new Queue<(string Dir, int Level)>();
        queue.Enqueue((root, 0));
        var dirsVisited = 0;

        while (queue.Count > 0)
        {
            var (dir, level) = queue.Dequeue();

            List<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).ToList(); }
            catch { continue; }
            foreach (var f in files) yield return f;

            if (level >= depth) continue;

            List<string> subs;
            try { subs = Directory.EnumerateDirectories(dir).ToList(); }
            catch { continue; }

            foreach (var s in subs)
            {
                if (++dirsVisited > maxDirs) yield break;
                queue.Enqueue((s, level + 1));
            }
        }
    }

    private static bool NameSimilar(string exeName, string displayName)
    {
        if (string.IsNullOrWhiteSpace(exeName) || string.IsNullOrWhiteSpace(displayName)) return false;
        var a = Normalize(exeName);
        var b = Normalize(displayName);
        if (a.Length < 3 || b.Length < 3) return false;
        return a.Contains(b) || b.Contains(a);
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    private static bool LooksLikeHelper(string exeName)
    {
        var n = exeName.ToLowerInvariant();
        return n.Contains("unins") || n.Contains("uninstall") || n.Contains("setup")
            || n.Contains("install") || n.Contains("update") || n.Contains("crash")
            || n.Contains("helper") || n.StartsWith("vc_") || n.Contains("report");
    }

    /// <summary>UWP 包清单里的正方形 logo（通常是 png，直接返回路径）。</summary>
    private static string? TryGetUwpLogo(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation)) return null;
        var manifest = Path.Combine(installLocation, "AppxManifest.xml");
        if (!File.Exists(manifest)) return null;

        try
        {
            var doc = XDocument.Load(manifest);
            var wanted = new[] { "Square44x44Logo", "Square150x150Logo", "Logo" };

            foreach (var name in wanted)
            {
                var candidates = new List<string>();
                foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == name))
                {
                    var value = (el.Value ?? string.Empty).Trim();
                    if (value.Length == 0 || value.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var full = Path.Combine(installLocation, value.Replace('/', '\\'));
                    if (File.Exists(full)) { candidates.Add(full); continue; }

                    // 清单里常写基准名，实际文件带 .scale-200 / .targetsize-44 后缀
                    var dirPart = Path.GetDirectoryName(full);
                    var stem = Path.GetFileNameWithoutExtension(full);
                    if (dirPart is not null && Directory.Exists(dirPart))
                    {
                        foreach (var f in Directory.EnumerateFiles(dirPart, stem + "*.png"))
                        {
                            candidates.Add(f);
                            break;
                        }
                    }
                }

                if (candidates.Count > 0) return candidates[0];
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把一个 exe/dll 里的第 index 个图标导出成缓存 PNG。</summary>
    private static string? ExtractToPng(string file, int index, int size)
    {
        if (file.Length == 0 || !File.Exists(file)) return null;
        if (Path.GetExtension(file).Equals(".ico", StringComparison.OrdinalIgnoreCase)) return file;

        try
        {
            Directory.CreateDirectory(CacheDir);
            var hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{file}|{index}|{size}"));
            var name = Convert.ToHexString(hash)[..16] + ".png";
            var cached = Path.Combine(CacheDir, name);
            if (File.Exists(cached)) return cached;

            ExtractIconExW(file, index, out var large, out var small, 1);
            var hIcon = size >= 32
                ? (large != IntPtr.Zero ? large : small)
                : (small != IntPtr.Zero ? small : large);
            if (hIcon == IntPtr.Zero) return null;

            try
            {
                using var icon = Icon.FromHandle(hIcon);
                using var bmp = icon.ToBitmap();
                bmp.Save(cached, System.Drawing.Imaging.ImageFormat.Png);
                return cached;
            }
            finally
            {
                if (large != IntPtr.Zero) DestroyIcon(large);
                if (small != IntPtr.Zero) DestroyIcon(small);
            }
        }
        catch
        {
            return null;
        }
    }
}
