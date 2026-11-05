using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PureUninstall.App.Services;
using PureUninstall.Core.Models;
using PureUninstall.Core.Services;
using PureUninstall.Core.Services.Guard;

namespace PureUninstall.App.Pages;

/// <summary>
/// 安装监控页。
/// 监控本体（InstallGuard）挂在 AppState 上，切走页面也继续跑，
/// 这里只负责开关、展示、撤销。
/// </summary>
public sealed partial class GuardPage : Page
{
    private readonly ObservableCollection<GuardRow> _events = new();
    private readonly DispatcherQueue _dispatcher;

    public GuardPage()
    {
        InitializeComponent();
        EventList.ItemsSource = _events;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var guard = AppState.Guard;

        // 同步上一次的状态（可能是在别的页面开的）
        MonitorSwitch.IsOn = guard.IsRunning;
        AutoRollbackBox.IsChecked = guard.AutoRollbackHighRisk;
        UpdateStatus();

        // 回填历史（切页面期间抓到的不会丢）
        _events.Clear();
        foreach (var r in guard.History) _events.Add(new GuardRow(r));
        RefreshEmptyState();

        guard.ItemDetected += OnItemDetected;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 只退订事件，监控本身继续运行
        AppState.Guard.ItemDetected -= OnItemDetected;
    }

    private void OnItemDetected(PersistenceItem item)
    {
        // 事件来自后台线程，必须切回 UI 线程
        _dispatcher.TryEnqueue(() =>
        {
            var record = AppState.Guard.History.FirstOrDefault(r => ReferenceEquals(r.Item, item));
            _events.Insert(0, new GuardRow(record ?? new GuardRecord { Item = item }));
            if (_events.Count > 500) _events.RemoveAt(_events.Count - 1);
            RefreshEmptyState();
            CountText.Text = $"已捕获 {_events.Count} 项";
        });
    }

    private void OnMonitorToggled(object sender, RoutedEventArgs e)
    {
        var guard = AppState.Guard;

        if (MonitorSwitch.IsOn)
        {
            StatusTitle.Text = "正在建立基线…";
            StatusHint.Text = "先记下当前系统已有的自启动项，之后只报告新增的。";
            _ = Task.Run(() =>
            {
                guard.PrimeBaseline();
                guard.Start();
                _dispatcher.TryEnqueue(UpdateStatus);
            });
        }
        else
        {
            guard.Stop();
            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        var guard = AppState.Guard;
        if (guard.IsRunning)
        {
            StatusTitle.Text = "监控中";
            StatusHint.Text = $"已记录 {guard.KnownCount} 个已有项作为基线，每 {guard.IntervalMs / 1000.0:0.#} 秒检查一次。装软件时让这个窗口在后台即可。";
        }
        else
        {
            StatusTitle.Text = "未开启";
            StatusHint.Text = "打开开关后，程序会定时检查系统里的自启动位置。";
        }
        MonitorSwitch.IsOn = guard.IsRunning;
    }

    private void OnAutoRollbackChanged(object sender, RoutedEventArgs e)
    {
        AppState.Guard.AutoRollbackHighRisk = AutoRollbackBox.IsChecked == true;
        if (AutoRollbackBox.IsChecked == true)
            StatusHint.Text = "已开启自动撤销：监控到高风险项会立即删除。正常软件的自启动也可能被误判，建议只在排查时开。";
    }

    private void OnResetBaseline(object sender, RoutedEventArgs e)
    {
        var guard = AppState.Guard;
        if (!guard.IsRunning)
        {
            StatusTitle.Text = "先打开监控";
            return;
        }
        _ = Task.Run(() =>
        {
            guard.ResetBaseline();
            _dispatcher.TryEnqueue(UpdateStatus);
        });
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        AppState.Guard.ClearHistory();
        _events.Clear();
        RefreshEmptyState();
        CountText.Text = "";
    }

    private void RefreshEmptyState()
    {
        bool empty = _events.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EventList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        CleanButton.IsEnabled = !empty;
    }

    private async void OnCleanClick(object sender, RoutedEventArgs e)
    {
        var rows = EventList.SelectedItems.OfType<GuardRow>().ToList();
        if (rows.Count == 0)
        {
            StatusTitle.Text = "先在列表里选中要撤销的项";
            return;
        }

        var residuals = rows.Select(r => r.Item.ToResidual()).OfType<ResidualItem>().ToList();
        if (residuals.Count == 0)
        {
            StatusTitle.Text = "选中的项不支持自动撤销（WMI 订阅需要手动清理）";
            return;
        }

        var dlg = new ContentDialog
        {
            Title = $"撤销 {residuals.Count} 项自启动？",
            Content = "只删除这些自启动项，不会卸载软件本体。删除前会自动备份。",
            PrimaryButtonText = "撤销",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            if (AppState.Settings.BackupBeforeDelete)
            {
                var pseudo = new AppEntry { Id = "guard", DisplayName = "安装监控", Kind = AppKind.Win32 };
                await BackupManager.CreateAsync(pseudo, residuals, null);
            }

            var result = await ResidualCleaner.DeleteAsync(residuals);
            OperationLog.Write(LogCategory.Guard, "撤销自启动", "",
                result.Failed == 0 ? LogResult.Success : LogResult.Failed,
                $"删除 {result.Removed} 项，失败 {result.Failed} 项");

            StatusTitle.Text = result.Failed == 0
                ? $"已撤销 {result.Removed} 项"
                : $"撤销 {result.Removed} 项，{result.Failed} 项失败（可能需要管理员权限）";
        }
        catch (Exception ex)
        {
            StatusTitle.Text = "撤销失败：" + ex.Message;
            OperationLog.Write(LogCategory.Guard, "撤销自启动", "", LogResult.Failed, ex.Message);
        }
    }
}

/// <summary>列表行包装。</summary>
public sealed class GuardRow
{
    private readonly GuardRecord _record;

    public GuardRow(GuardRecord record) => _record = record;

    public PersistenceItem Item => _record.Item;
    public string Name => _record.Item.Name;
    public string KindText => _record.Item.KindText;
    public string Command => _record.Item.Command;
    public string RiskText => _record.Item.RiskText;
    public string TimeText => _record.TimeText;

    public Microsoft.UI.Xaml.Media.SolidColorBrush RiskBg => Item.Risk switch
    {
        RiskLevel.High => new(Windows.UI.Color.FromArgb(255, 0xFD, 0xE7, 0xE9)),
        RiskLevel.Medium => new(Windows.UI.Color.FromArgb(255, 0xFF, 0xF4, 0xE5)),
        _ => new(Windows.UI.Color.FromArgb(255, 0xF1, 0xF3, 0xF6)),
    };

    public Microsoft.UI.Xaml.Media.SolidColorBrush RiskFg => Item.Risk switch
    {
        RiskLevel.High => new(Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C)),
        RiskLevel.Medium => new(Windows.UI.Color.FromArgb(255, 0x9D, 0x5D, 0x00)),
        _ => new(Windows.UI.Color.FromArgb(255, 0x44, 0x4A, 0x55)),
    };
}
