using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services.Persistence;

/// <summary>
/// 给一条持久化项打「可疑度」分。
///
/// 判断依据全是可解释的硬规则，不猜、不黑箱 —— 每加一条理由，界面上就显示一条，
/// 用户能自己判断要不要删。这里不做「是不是病毒」的断言，只回答「像不像正常软件」。
/// </summary>
public static class RiskEvaluator
{
    private static readonly ConcurrentDictionary<string, (bool? Signed, string? Signer)> SigCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>这几个目录下的自启动项，正常软件很少用，恶意程序很爱用。</summary>
    private static readonly string[] SuspiciousDirs =
    {
        @"\AppData\Local\Temp\", @"\Windows\Temp\", @"\Downloads\", @"\$Recycle.Bin\",
        @"\ProgramData\Temp\", @"\AppData\Roaming\Temp\",
    };

    /// <summary>以脚本方式启动，绕过了 exe 的签名检查。</summary>
    private static readonly string[] ScriptExts = { ".js", ".jse", ".vbs", ".vbe", ".ps1", ".bat", ".cmd", ".hta" };

    /// <summary>读文件签名（不验证证书链，只看有没有签名、签名者是谁）。结果带缓存。</summary>
    public static (bool? Signed, string? Signer) GetSignature(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return (null, null);
        if (!File.Exists(file)) return (null, null);
        if (SigCache.TryGetValue(file, out var cached)) return cached;

        (bool? signed, string? signer) result = (false, null);
        try
        {
            var cert = X509Certificate.CreateFromSignedFile(file);
            string subject = cert?.Subject ?? "";
            result = (true, subject);
        }
        catch
        {
            result = (false, null);
        }

        SigCache[file] = result;
        return result;
    }

    /// <summary>从命令行里抠出可执行文件路径，并把各种简写还原成绝对路径。</summary>
    public static string? ExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        var s = command.Trim();
        string first;
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            first = end > 1 ? s[1..end] : s.Trim('"');
        }
        else
        {
            int space = s.IndexOf(' ');
            first = space > 0 ? s[..space] : s;
        }

        first = first.Trim();
        if (first.Length < 4) return null;
        if (!first.Contains('\\')) return null;

        return ResolvePath(first);
    }

    /// <summary>
    /// 服务/驱动的 ImagePath 经常写成 <c>\SystemRoot\System32\drivers\x.sys</c> 或 <c>System32\x.sys</c>，
    /// 直接 File.Exists 会全部判成「文件不存在」，进而把几百个系统驱动误报成中风险。
    /// 这里统一还原成绝对路径。
    /// </summary>
    public static string ResolvePath(string raw)
    {
        string s = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"', ' '));
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        const string NtPrefix = @"\??\";
        const string SysRootSlash = @"\SystemRoot\";
        const string SysRoot = @"SystemRoot\";

        if (s.StartsWith(NtPrefix, StringComparison.OrdinalIgnoreCase))
            s = s[NtPrefix.Length..];
        if (s.StartsWith(SysRootSlash, StringComparison.OrdinalIgnoreCase))
            s = Path.Combine(winDir, s[SysRootSlash.Length..]);
        else if (s.StartsWith(SysRoot, StringComparison.OrdinalIgnoreCase))
            s = Path.Combine(winDir, s[SysRoot.Length..]);
        else if (!s.Contains(':') && !s.StartsWith(@"\\", StringComparison.Ordinal))
            s = Path.Combine(winDir, s);   // System32\drivers\x.sys 之类

        return s;
    }

    /// <summary>路径是否位于 Windows 目录（系统自带组件）。</summary>
    public static bool IsSystemPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!path.StartsWith(winDir, StringComparison.OrdinalIgnoreCase)) return false;
        // Windows\Temp 不算系统组件，那是最典型的恶意落地点之一
        return !path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>综合判定，返回风险级别和逐条理由。</summary>
    public static (RiskLevel Risk, List<string> Reasons) Evaluate(
        string? command, string? exePath, bool? signed, string? signer)
    {
        var reasons = new List<string>();
        var risk = RiskLevel.Low;

        void Up(RiskLevel level, string reason)
        {
            if (level > risk) risk = level;
            reasons.Add(reason);
        }

        if (string.IsNullOrWhiteSpace(command))
            return (RiskLevel.Low, reasons);

        string cmd = command.ToLowerInvariant();

        // 1) 编码过的命令：正常软件极少这么干，恶意脚本几乎都这么干
        if (cmd.Contains("powershell") || cmd.Contains("pwsh"))
        {
            if (cmd.Contains("-enc") || cmd.Contains("-encodedcommand"))
                Up(RiskLevel.High, "通过 PowerShell 执行 Base64 编码命令（隐藏脚本的典型手法）");
            if (cmd.Contains("-w hidden") || cmd.Contains("-windowstyle hidden"))
                Up(RiskLevel.High, "以隐藏窗口方式运行 PowerShell");
        }

        // 2) 远程加载 / 无文件执行
        if ((cmd.Contains("mshta") || cmd.Contains("regsvr32") || cmd.Contains("rundll32"))
            && (cmd.Contains("http://") || cmd.Contains("https://")))
            Up(RiskLevel.High, "从远程地址加载脚本执行（无文件落地）");

        if (cmd.Contains("certutil") && (cmd.Contains("-urlcache") || cmd.Contains("-decode")))
            Up(RiskLevel.High, "用 certutil 下载或解码文件（常见于木马释放器）");

        if (cmd.Contains("bitsadmin") && cmd.Contains("/transfer"))
            Up(RiskLevel.High, "用 BITS 后台下载文件");

        // 3) 位置可疑
        if (!string.IsNullOrEmpty(exePath))
        {
            // 先看是不是落在临时/下载目录——这个优先级最高，系统目录判断要让位
            bool inSuspiciousDir = false;
            foreach (var dir in SuspiciousDirs)
                if (exePath.Contains(dir, StringComparison.OrdinalIgnoreCase))
                {
                    Up(RiskLevel.High, $"可执行文件位于临时/下载目录：{dir.Trim('\\')}");
                    inSuspiciousDir = true;
                    break;
                }

            var ext = Path.GetExtension(exePath).ToLowerInvariant();
            if (ScriptExts.Contains(ext))
                Up(RiskLevel.Medium, $"以 {ext} 脚本方式启动，绕过了程序签名检查");

            if (!inSuspiciousDir && IsSystemPath(exePath))
            {
                // Windows 目录里的东西是系统自带组件（驱动、系统服务），
                // 列出来是为了可追溯，但绝不能算风险，否则一屏 400 条误报
                reasons.Insert(0, "系统目录内的组件");
                return (RiskLevel.Low, reasons);
            }

            if (!File.Exists(exePath))
                Up(RiskLevel.Medium, "启动目标已不存在（可能是卸载残留）");
        }

        // 4) 签名
        if (signed == false)
        {
            bool inUserDir = !string.IsNullOrEmpty(exePath) &&
                             (exePath.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) ||
                              exePath.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase));

            if (inUserDir)
                Up(RiskLevel.Medium, "没有数字签名，且位于用户目录（正常软件通常装在 Program Files）");
            else
                Up(RiskLevel.Low, "没有数字签名");
        }
        else if (signed == true && !string.IsNullOrEmpty(signer))
        {
            // 有知名厂商签名基本可以放心
            foreach (var trusted in new[] { "Microsoft", "Google", "Mozilla", "Tencent", "Valve", "Adobe", "Intel", "NVIDIA", "Realtek", "AMD" })
                if (signer.Contains(trusted, StringComparison.OrdinalIgnoreCase))
                {
                    if (reasons.Count == 0) reasons.Add($"签名者：{trusted}");
                    break;
                }
        }

        return (risk, reasons);
    }
}
