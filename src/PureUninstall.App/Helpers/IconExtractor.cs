using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PureUninstall.App.Helpers;

/// <summary>
/// Extracts EXE/DLL icons to cached PNG files (System.Drawing based).
/// Returns a file path; the caller creates the BitmapImage on the UI thread.
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

    /// <summary>Extracts the icon to a cached PNG and returns its path (null on failure).</summary>
    public static string? GetIconFile(string? iconPath, int size = 32)
    {
        if (string.IsNullOrWhiteSpace(iconPath)) return null;

        string file;
        var index = 0;
        var comma = iconPath.LastIndexOf(',');
        if (comma > 0)
        {
            file = iconPath[..comma].Trim().Trim('"');
            if (int.TryParse(iconPath[(comma + 1)..].Trim(), out var idx)) index = idx;
        }
        else
        {
            file = iconPath.Trim().Trim('"');
        }

        if (file.Length == 0) return null;
        if (!File.Exists(file)) return null;
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
