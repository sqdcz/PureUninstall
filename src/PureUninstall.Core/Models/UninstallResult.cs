namespace PureUninstall.Core.Models;

/// <summary>Result of running one program's uninstaller.</summary>
public sealed class UninstallOutcome
{
    public bool Started { get; init; }
    public bool Completed { get; init; }       // root uninstaller exited
    public bool TreeExited { get; init; }      // whole process tree exited
    public bool Cancelled { get; init; }
    public int ExitCode { get; init; } = -1;
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }

    public bool Success => Started && Completed && TreeExited && !Cancelled && Error is null && ExitCode == 0;
}

/// <summary>Per-app result inside a batch uninstall run.</summary>
public sealed class BatchItemResult
{
    public AppEntry App { get; set; } = null!;
    public UninstallOutcome? Outcome { get; set; }
    public List<ResidualItem> Residuals { get; } = new();
    public string? Note { get; set; }
    public string? BackupId { get; set; }
    public int RemovedCount { get; set; }

    public string StatusText => Outcome switch
    {
        null => Note ?? "未执行",
        { Cancelled: true } => "已取消",
        { Success: true } => Residuals.Count > 0 ? "已卸载，发现残留" : "已卸载",
        _ => Note ?? "卸载未正常完成"
    };
}

/// <summary>Progress report for a batch run.</summary>
public readonly record struct BatchProgress(int Done, int Total, string CurrentApp, string Message);
