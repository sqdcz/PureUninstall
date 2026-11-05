using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services.Signatures;

/// <summary>
/// 特征库管理：导入、保存、加载、匹配。
///
/// 设计原则是「引擎归程序、特征归用户」——
/// 程序自带一套判断逻辑（可疑路径、隐藏参数、无签名等），
/// 但「某某软件是推广组件」这种知识，不写死在安装包里，
/// 而是让用户导入自己的特征文件。这样既能接入现成特征库，
/// 又不用把别人的数据打包分发。
/// </summary>
public static class SignatureStore
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PureUninstall", "Signatures");

    private const int MaxRulesPerPack = 20_000;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static List<SignatureRule> _rules = new();
    private static bool _loaded;

    public static IReadOnlyList<SignatureRule> Rules
    {
        get { EnsureLoaded(); return _rules; }
    }

    public static int RuleCount => Rules.Count;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _rules = LoadAll();
        _loaded = true;
    }

    /// <summary>重新从磁盘加载（导入/删除后调用）。</summary>
    public static int Reload()
    {
        _rules = LoadAll();
        _loaded = true;
        return _rules.Count;
    }

    private static List<SignatureRule> LoadAll()
    {
        var all = new List<SignatureRule>();
        if (!Directory.Exists(Root)) return all;

        foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
        {
            try
            {
                var pack = JsonSerializer.Deserialize<SignaturePack>(File.ReadAllText(file), Json);
                if (pack?.Rules is null) continue;
                foreach (var r in pack.Rules)
                {
                    if (!string.IsNullOrWhiteSpace(r.Pattern) && !string.IsNullOrWhiteSpace(r.Id))
                        all.Add(r);
                }
            }
            catch
            {
                // 单个文件坏了不影响其它包
            }
        }
        return all;
    }

    /// <summary>已导入的特征库文件列表。</summary>
    public static List<(string Path, string Name, int Rules)> ListPacks()
    {
        var list = new List<(string, string, int)>();
        if (!Directory.Exists(Root)) return list;

        foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
        {
            try
            {
                var pack = JsonSerializer.Deserialize<SignaturePack>(File.ReadAllText(file), Json);
                list.Add((file, string.IsNullOrWhiteSpace(pack?.Name) ? Path.GetFileName(file) : pack!.Name!,
                          pack?.Rules?.Count ?? 0));
            }
            catch
            {
                list.Add((file, Path.GetFileName(file) + "（格式错误）", 0));
            }
        }
        return list;
    }

    /// <summary>导入一个特征文件。校验通过后复制到特征目录。</summary>
    public static SignatureImportResult Import(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath))
                return new SignatureImportResult { Ok = false, FileName = sourcePath, Error = "文件不存在" };

            string text = File.ReadAllText(sourcePath);
            var pack = JsonSerializer.Deserialize<SignaturePack>(text, Json);

            if (pack is null)
                return new SignatureImportResult { Ok = false, FileName = Path.GetFileName(sourcePath), Error = "不是合法的 JSON" };

            if (pack.Rules is null || pack.Rules.Count == 0)
                return new SignatureImportResult { Ok = false, FileName = Path.GetFileName(sourcePath), Error = "文件里没有规则（rules 为空）" };

            if (pack.Rules.Count > MaxRulesPerPack)
                return new SignatureImportResult
                {
                    Ok = false,
                    FileName = Path.GetFileName(sourcePath),
                    Error = $"规则太多（{pack.Rules.Count} 条，上限 {MaxRulesPerPack}）",
                };

            // 规整：丢掉缺字段的规则，id 缺失时自动补
            int idx = 0;
            pack.Rules = pack.Rules
                .Where(r => !string.IsNullOrWhiteSpace(r.Pattern))
                .Select(r =>
                {
                    idx++;
                    if (string.IsNullOrWhiteSpace(r.Id)) r.Id = $"auto-{idx}";
                    r.Kind = r.MatchKind switch
                    {
                        RuleMatchKind.Name => "name",
                        RuleMatchKind.Command => "command",
                        RuleMatchKind.Hash => "hash",
                        _ => "path",
                    };
                    r.Risk = r.RiskLevel switch
                    {
                        RiskLevel.High => "high",
                        RiskLevel.Low => "low",
                        _ => "medium",
                    };
                    if (string.IsNullOrWhiteSpace(r.Reason)) r.Reason = "匹配到导入的特征规则";
                    return r;
                })
                .ToList();

            if (pack.Rules.Count == 0)
                return new SignatureImportResult { Ok = false, FileName = Path.GetFileName(sourcePath), Error = "所有规则都缺少 pattern 字段" };

            Directory.CreateDirectory(Root);
            string fileName = Path.GetFileName(sourcePath);
            string target = Path.Combine(Root, fileName);
            if (File.Exists(target))
            {
                // 同名覆盖，但先保留一份旧的
                File.Copy(target, target + ".bak", overwrite: true);
            }
            File.WriteAllText(target, JsonSerializer.Serialize(pack, Json), new UTF8Encoding(false));

            Reload();

            return new SignatureImportResult
            {
                Ok = true,
                FileName = fileName,
                RuleCount = pack.Rules.Count,
            };
        }
        catch (JsonException ex)
        {
            return new SignatureImportResult
            {
                Ok = false,
                FileName = Path.GetFileName(sourcePath),
                Error = "JSON 解析失败：" + ex.Message,
            };
        }
        catch (Exception ex)
        {
            return new SignatureImportResult { Ok = false, FileName = Path.GetFileName(sourcePath), Error = ex.Message };
        }
    }

    /// <summary>把命中的规则叠加到扫描结果上：提升风险等级、补充理由。</summary>
    public static int Apply(List<PersistenceItem> items)
    {
        var rules = Rules;
        if (rules.Count == 0 || items.Count == 0) return 0;

        int hits = 0;
        foreach (var item in items)
        {
            foreach (var rule in rules)
            {
                bool matched = rule.MatchKind switch
                {
                    RuleMatchKind.Name => rule.IsMatch(item.Name),
                    RuleMatchKind.Command => rule.IsMatch(item.Command),
                    RuleMatchKind.Path => rule.IsMatch(item.Location) || rule.IsMatch(item.Command),
                    _ => false,   // hash 规则暂不参与（要读文件，扫描时不做）
                };

                if (!matched) continue;

                hits++;
                item.Reasons.Insert(0, string.IsNullOrWhiteSpace(rule.Category)
                    ? $"特征库命中：{rule.Reason}"
                    : $"特征库命中［{rule.Category}］：{rule.Reason}");

                // 只升不降：规则说是高危就是高危，但不能把系统组件的判定降下来
                if (rule.RiskLevel > item.Risk) item.Risk = rule.RiskLevel;
                break;   // 一个项只取第一条命中规则，避免理由刷屏
            }
        }
        return hits;
    }

    /// <summary>写一份示例特征文件，方便用户照着自己写。</summary>
    public static void WriteTemplate(string targetPath)
    {
        var pack = new SignaturePack
        {
            Name = "我的特征库（示例）",
            Version = "1",
            Author = "你的名字",
            Rules = new List<SignatureRule>
            {
                new()
                {
                    Id = "demo-path-1",
                    Kind = "path",
                    Pattern = @"*\AppData\Roaming\SomeAdware\*",
                    Risk = "high",
                    Reason = "某推广组件的常驻目录",
                    Category = "推广组件",
                },
                new()
                {
                    Id = "demo-name-1",
                    Kind = "name",
                    Pattern = "某弹窗助手",
                    Risk = "medium",
                    Reason = "捆绑安装的弹窗程序",
                    Category = "捆绑软件",
                },
                new()
                {
                    Id = "demo-command-1",
                    Kind = "command",
                    Pattern = "-WindowStyle Hidden",
                    Risk = "medium",
                    Reason = "以隐藏窗口方式启动",
                    Category = "可疑行为",
                },
            },
        };

        File.WriteAllText(targetPath, JsonSerializer.Serialize(pack, Json), new UTF8Encoding(true));
    }
}
