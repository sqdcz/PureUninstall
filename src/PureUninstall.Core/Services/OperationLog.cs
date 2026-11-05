using System.Globalization;
using System.Text;
using System.Text.Json;
using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services;

/// <summary>
/// 操作日志：所有会改动系统的动作都记一条，落成 JSONL 按天分文件。
///
/// 为什么用 JSONL 而不是一个 json 数组：
///  - 追加写不用重写整个文件，代价恒定
///  - 中途崩溃最多丢最后一行，不会毁掉整个日志
///  - 出问题时可以直接把文件发给开发者，不用先导出
/// </summary>
public static class OperationLog
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>本次运行的会话号，用于把一次运行的操作归到一起。</summary>
    public static string SessionId { get; } = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    public static string LogRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PureUninstall", "Logs");

    public static bool IsElevated { get; set; }

    /// <summary>写一条日志。任何异常都吞掉——日志不能反过来把程序搞崩。</summary>
    public static void Write(LogCategory category, string action, string target = "",
        LogResult result = LogResult.Info, string? detail = null, long durationMs = 0)
    {
        var entry = new LogEntry
        {
            Time = DateTimeOffset.Now,
            Session = SessionId,
            Category = category,
            Result = result,
            Target = target,
            Action = action,
            Detail = detail,
            DurationMs = durationMs,
            WasElevated = IsElevated,
        };
        Write(entry);
    }

    public static void Write(LogEntry entry)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogRoot);
                string file = Path.Combine(LogRoot, $"operations-{entry.Time:yyyy-MM-dd}.jsonl");
                string line = JsonSerializer.Serialize(entry, Json);
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 磁盘满、无权限…都不该影响主流程
        }
    }

    /// <summary>便捷写法：包住一个可能抛异常的操作，自动记成功/失败和耗时。</summary>
    public static bool Try(LogCategory category, string action, string target, Action body, out string? error)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            body();
            sw.Stop();
            Write(category, action, target, LogResult.Success, null, sw.ElapsedMilliseconds);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Write(category, action, target, LogResult.Failed, ex.Message, sw.ElapsedMilliseconds);
            error = ex.Message;
            return false;
        }
    }

    /// <summary>读最近 N 天的日志，最新的在前。</summary>
    public static List<LogEntry> Read(int days = 7)
    {
        var list = new List<LogEntry>();
        if (!Directory.Exists(LogRoot)) return list;

        var cutoff = DateTime.Today.AddDays(-Math.Max(0, days - 1));
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(LogRoot, "operations-*.jsonl")
                             .OrderByDescending(f => f, StringComparer.Ordinal)
                             .Take(Math.Max(1, days));
        }
        catch
        {
            return list;
        }

        foreach (var file in files)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var datePart = name.Replace("operations-", "");
                if (DateTime.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date) && date < cutoff)
                    continue;

                foreach (var line in File.ReadLines(file))
                {
                    if (line.Length == 0) continue;
                    try
                    {
                        var e = JsonSerializer.Deserialize<LogEntry>(line, Json);
                        if (e is not null) list.Add(e);
                    }
                    catch
                    {
                        // 坏行跳过（比如崩溃时写了半行）
                    }
                }
            }
            catch
            {
            }
        }

        return list.OrderByDescending(e => e.Time).ToList();
    }

    public static IReadOnlyList<string> ListLogFiles()
    {
        try
        {
            if (!Directory.Exists(LogRoot)) return Array.Empty<string>();
            return Directory.GetFiles(LogRoot, "operations-*.jsonl")
                            .OrderByDescending(f => f, StringComparer.Ordinal)
                            .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // ---------------------------------------------------------------- 导出

    /// <summary>把日志导出成文件，返回实际写入的路径。</summary>
    public static string Export(IEnumerable<LogEntry> entries, LogExportFormat format, string targetPath)
    {
        var list = entries as IList<LogEntry> ?? entries.ToList();
        string dir = Path.GetDirectoryName(targetPath) ?? LogRoot;
        Directory.CreateDirectory(dir);

        string content = format switch
        {
            LogExportFormat.Csv => BuildCsv(list),
            LogExportFormat.Json => BuildJson(list),
            _ => BuildText(list),
        };

        // 带 BOM，Excel 直接打开 CSV 不会乱码
        File.WriteAllText(targetPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return targetPath;
    }

    /// <summary>
    /// 诊断包：系统环境 + 程序版本 + 全部日志。
    /// 用户反馈问题时把这个文件发过来，比一句「用不了」有用得多。
    /// </summary>
    public static string BuildDiagnosticReport(IEnumerable<LogEntry> entries)
    {
        var list = entries as IList<LogEntry> ?? entries.ToList();
        var sb = new StringBuilder();

        sb.AppendLine("纯净卸载 · 诊断报告");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine($"生成时间    : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"程序版本    : {typeof(OperationLog).Assembly.GetName().Version}");
        sb.AppendLine($"运行会话    : {SessionId}");
        sb.AppendLine($"操作系统    : {Environment.OSVersion.VersionString}");
        sb.AppendLine($"系统架构    : {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        sb.AppendLine($".NET 版本   : {Environment.Version}");
        sb.AppendLine($"64 位进程   : {Environment.Is64BitProcess}");
        sb.AppendLine($"管理员权限  : {(IsElevated ? "是" : "否")}");
        sb.AppendLine($"逻辑处理器  : {Environment.ProcessorCount}");
        sb.AppendLine($"日志目录    : {LogRoot}");
        sb.AppendLine($"日志条数    : {list.Count}");
        sb.AppendLine();
        sb.AppendLine("原始日志文件");
        sb.AppendLine(new string('-', 60));
        foreach (var f in ListLogFiles())
        {
            try
            {
                var fi = new FileInfo(f);
                sb.AppendLine($"  {fi.Name}   {fi.Length / 1024.0:F1} KB   {fi.LastWriteTime:yyyy-MM-dd HH:mm}");
            }
            catch { }
        }
        sb.AppendLine();
        sb.AppendLine("操作记录（新的在前）");
        sb.AppendLine(new string('-', 60));
        sb.Append(BuildText(list));
        return sb.ToString();
    }

    private static string BuildText(IEnumerable<LogEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.Append(e.TimeText)
              .Append("  [").Append(e.CategoryText).Append(']')
              .Append(' ').Append(e.Action);

            if (!string.IsNullOrEmpty(e.Target))
                sb.Append("  <- ").Append(e.Target);

            sb.Append("  => ").Append(e.ResultText);

            if (e.DurationMs > 0)
                sb.Append("  (").Append(e.DurationMs).Append(" ms)");

            if (!string.IsNullOrEmpty(e.Detail))
                sb.Append("  |  ").Append(e.Detail);

            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildCsv(IEnumerable<LogEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("时间,类别,结果,动作,对象,详情,耗时ms,管理员,会话");
        foreach (var e in entries)
        {
            sb.Append(Csv(e.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))).Append(',')
              .Append(Csv(e.CategoryText)).Append(',')
              .Append(Csv(e.ResultText)).Append(',')
              .Append(Csv(e.Action)).Append(',')
              .Append(Csv(e.Target)).Append(',')
              .Append(Csv(e.Detail ?? "")).Append(',')
              .Append(e.DurationMs).Append(',')
              .Append(e.WasElevated ? "是" : "否").Append(',')
              .Append(Csv(e.Session))
              .AppendLine();
        }
        return sb.ToString();
    }

    private static string Csv(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string BuildJson(IEnumerable<LogEntry> entries)
    {
        return JsonSerializer.Serialize(entries, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }
}
