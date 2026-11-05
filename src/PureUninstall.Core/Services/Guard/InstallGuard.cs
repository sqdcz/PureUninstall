using PureUninstall.Core.Models;
using PureUninstall.Core.Services.Persistence;

namespace PureUninstall.Core.Services.Guard;

/// <summary>
/// 安装监控：盯着「会自动运行」的那几处位置，一旦冒出新东西就报告。
///
/// 为什么叫「监控」而不是「禁止安装」：
/// 真正阻止安装需要文件过滤驱动或系统策略（SRP/AppLocker），
/// 那类方案一旦配错，用户连正常软件都装不了，卸载工具不该干这种事。
/// 这里做的是同一件事的另一半 —— **装可以装，但别想悄悄藏一个自启动**：
/// 发现即记录、可一键撤销；开了自动回滚，等于「装了也自动被拆掉」。
/// </summary>
/// <summary>监控捕获的一条记录。</summary>
public sealed class GuardRecord
{
    public PersistenceItem Item { get; init; } = new();
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
    public string TimeText => Time.ToString("HH:mm:ss");
}

public sealed class InstallGuard : IDisposable
{
    private const int MaxHistory = 500;
    private readonly List<GuardRecord> _history = new();
    private readonly object _historyGate = new();

    /// <summary>最近捕获的记录（新的在前）。页面切走再回来也能看到。</summary>
    public List<GuardRecord> History
    {
        get { lock (_historyGate) return _history.ToList(); }
    }

    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private CancellationTokenSource? _cts;
    private int _busy;      // 防止上一轮没跑完又进下一轮

    /// <summary>发现新持久化项时触发（在后台线程上，UI 需自己切回去）。</summary>
    public event Action<PersistenceItem>? ItemDetected;

    /// <summary>自动撤销命中的高风险项（默认关闭，开了就是「装了也白装」）。</summary>
    public bool AutoRollbackHighRisk { get; set; }

    public bool IsRunning => _timer is not null;

    public int IntervalMs { get; set; } = 2500;

    public int KnownCount => _known.Count;

    /// <summary>建立基线：把当前已有的项记下来，之后只报告新增。</summary>
    public void PrimeBaseline()
    {
        var items = PersistenceScanner.Scan(null, CancellationToken.None, fast: true);
        _known.Clear();
        foreach (var i in items) _known.Add(KeyOf(i));
    }

    public void Start()
    {
        if (_timer is not null) return;

        if (_known.Count == 0) PrimeBaseline();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _timer = new Timer(async _ =>
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
            try
            {
                await Task.Run(() => Check(ct), ct);
            }
            catch (OperationCanceledException) { }
            catch { /* 单轮失败不影响后续 */ }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }, null, IntervalMs, IntervalMs);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>手动清空基线（比如用户刚清理完，想让软件重新学一遍）。</summary>
    public void ResetBaseline() => PrimeBaseline();

    private void Check(CancellationToken ct)
    {
        var items = PersistenceScanner.Scan(null, ct, fast: true);

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            string key = KeyOf(item);
            if (_known.Contains(key)) continue;

            _known.Add(key);

            // 刚装完软件那几秒会连续冒出多个，逐个报即可
            var record = new GuardRecord { Item = item, Time = DateTimeOffset.Now };
            lock (_historyGate)
            {
                _history.Insert(0, record);
                if (_history.Count > MaxHistory) _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
            }

            ItemDetected?.Invoke(item);

            OperationLog.Write(LogCategory.Guard, "发现新增持久化项", item.Name,
                item.Risk == RiskLevel.High ? LogResult.Failed : LogResult.Info,
                $"[{item.KindText}] {item.Command}");

            if (AutoRollbackHighRisk && item.Risk == RiskLevel.High && item.CanDelete)
            {
                try
                {
                    var residual = item.ToResidual();
                    if (residual is not null)
                    {
                        var result = ResidualCleaner.DeleteAsync(new[] { residual }).GetAwaiter().GetResult();
                        OperationLog.Write(LogCategory.Guard, "自动撤销", item.Name,
                            result.Removed > 0 ? LogResult.Success : LogResult.Failed,
                            result.Removed > 0 ? "已删除（自动回滚）" : "删除失败，可能需要管理员权限");
                    }
                }
                catch (Exception ex)
                {
                    OperationLog.Write(LogCategory.Guard, "自动撤销", item.Name, LogResult.Failed, ex.Message);
                }
            }
        }
    }

    public void ClearHistory()
    {
        lock (_historyGate) _history.Clear();
    }

    private static string KeyOf(PersistenceItem i) =>
        $"{(int)i.Kind}|{i.Location}|{i.Name}";

    public void Dispose() => Stop();
}
