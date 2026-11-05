using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PureUninstall.Core.Models;

public enum LogCategory
{
    App,
    Uninstall,
    Clean,
    Backup,
    Restore,
    Trace,
    Scan,
    Guard,
}

public enum LogResult
{
    Info,
    Success,
    Failed,
    Skipped,
    Cancelled,
}

/// <summary>一条操作日志。字段刻意做得比较全，便于出问题时直接定位。</summary>
public sealed class LogEntry
{
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;

    /// <summary>本次运行的会话号，同一次运行内的日志可以按它归组。</summary>
    public string Session { get; set; } = "";

    public LogCategory Category { get; set; }
    public LogResult Result { get; set; } = LogResult.Info;

    /// <summary>操作对象：软件名、文件路径、注册表键…</summary>
    public string Target { get; set; } = "";

    /// <summary>做了什么，例如「删除目录」「导出注册表」「开始追踪」。</summary>
    public string Action { get; set; } = "";

    /// <summary>补充信息：错误消息、大小、原因。</summary>
    public string? Detail { get; set; }

    public long DurationMs { get; set; }
    public bool WasElevated { get; set; }

    public string CategoryText => Category switch
    {
        LogCategory.App => "程序",
        LogCategory.Uninstall => "卸载",
        LogCategory.Clean => "清理",
        LogCategory.Backup => "备份",
        LogCategory.Restore => "还原",
        LogCategory.Trace => "追踪",
        LogCategory.Scan => "扫描",
        LogCategory.Guard => "防护",
        _ => "其他",
    };

    public string ResultText => Result switch
    {
        LogResult.Success => "成功",
        LogResult.Failed => "失败",
        LogResult.Skipped => "跳过",
        LogResult.Cancelled => "取消",
        _ => "信息",
    };

    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "." +
                               Time.Millisecond.ToString("000", CultureInfo.InvariantCulture);
}

/// <summary>导出格式。</summary>
public enum LogExportFormat
{
    Text,
    Csv,
    Json,
}
