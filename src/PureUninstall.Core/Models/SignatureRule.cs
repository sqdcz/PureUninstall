using System.Text.Json;
using System.Text.RegularExpressions;

namespace PureUninstall.Core.Models;

/// <summary>特征规则的匹配对象。</summary>
public enum RuleMatchKind
{
    /// <summary>路径通配符：匹配文件路径、注册表路径、启动项命令行。</summary>
    Path,
    /// <summary>名称包含：匹配启动项名、服务名、任务名。</summary>
    Name,
    /// <summary>命令行关键词（不区分大小写）。</summary>
    Command,
    /// <summary>文件 SHA256（需要读文件，慢，默认不启用）。</summary>
    Hash,
}

/// <summary>
/// 一条特征规则。
///
/// 「引擎归我、库归你」：匹配逻辑、风险合并、UI 都在程序里；
/// 具体哪家软件是流氓、哪个路径是推广组件，由用户导入的特征文件决定。
/// 这样既能用上现成的特征库，又不用把别人的数据打包进安装包。
/// </summary>
public sealed class SignatureRule
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "path";      // path / name / command / hash
    public string Pattern { get; set; } = "";
    public string Risk { get; set; } = "medium";     // high / medium / low
    public string Reason { get; set; } = "";
    public string? Category { get; set; }

    public RuleMatchKind MatchKind => Kind.ToLowerInvariant() switch
    {
        "name" => RuleMatchKind.Name,
        "command" => RuleMatchKind.Command,
        "hash" => RuleMatchKind.Hash,
        _ => RuleMatchKind.Path,
    };

    public RiskLevel RiskLevel => Risk.ToLowerInvariant() switch
    {
        "high" => RiskLevel.High,
        "low" => RiskLevel.Low,
        _ => RiskLevel.Medium,
    };

    /// <summary>通配符转正则（* 任意字符，? 单字符），并缓存。</summary>
    private Regex? _regex;

    public Regex Compiled => _regex ??= new Regex(
        "^" + Regex.Escape(Pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool IsMatch(string text)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(Pattern)) return false;
        try
        {
            // 路径类用通配符全匹配，其余用包含匹配（更宽容，误报可控）
            return MatchKind == RuleMatchKind.Path
                ? Compiled.IsMatch(text)
                : text.Contains(Pattern, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>一个特征库文件。</summary>
public sealed class SignaturePack
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Author { get; set; }
    public List<SignatureRule> Rules { get; set; } = new();
}

/// <summary>导入结果。</summary>
public sealed class SignatureImportResult
{
    public bool Ok { get; init; }
    public string FileName { get; init; } = "";
    public int RuleCount { get; init; }
    public string? Error { get; init; }
}
