using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PureUninstall.Core.Models;
using PureUninstall.Core.Services;

namespace PureUninstall.App.Pages;

/// <summary>
/// 操作日志页：读本地 JSONL 日志，支持筛选和四种导出。
/// 日志本身由 OperationLog 在各处顺手写，这里只负责展示。
/// </summary>
public sealed partial class LogsPage : Page
{
    private readonly ObservableCollection<LogEntry> _view = new();
    private List<LogEntry> _all = new();
    private bool _ready;

    public LogsPage()
    {
        InitializeComponent();
        LogList.ItemsSource = _view;
        Loaded += async (_, _) =>
        {
            _ready = true;
            await ReloadAsync();
        };
    }

    private async Task ReloadAsync()
    {
        try
        {
            int days = DaysBox.SelectedIndex switch { 0 => 1, 1 => 7, _ => 30 };
            _all = await Task.Run(() => OperationLog.Read(days));
            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取失败：" + ex.Message;
        }
    }

    private void ApplyFilter()
    {
        if (!_ready) return;

        int catIndex = CategoryBox.SelectedIndex;
        LogCategory? want = catIndex switch
        {
            1 => LogCategory.Uninstall,
            2 => LogCategory.Clean,
            3 => LogCategory.Backup,
            4 => LogCategory.Trace,
            5 => LogCategory.Scan,
            6 => LogCategory.Guard,
            _ => null,
        };

        var filtered = want is null
            ? _all
            : _all.Where(e => e.Category == want || (want == LogCategory.Backup && e.Category == LogCategory.Restore))
                  .ToList();

        _view.Clear();
        foreach (var e in filtered) _view.Add(e);

        EmptyState.Visibility = _view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = _view.Count == _all.Count
            ? $"共 {_view.Count} 条"
            : $"显示 {_view.Count} / {_all.Count} 条";
    }

    private async void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        // 天数变了要重新读文件，类别变了只需过滤
        if (ReferenceEquals(sender, DaysBox)) await ReloadAsync();
        else ApplyFilter();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await ReloadAsync();

    // ---------------------------------------------------------------- 导出

    private string ExportDir()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PureUninstall导出");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void OnExportTextClick(object sender, RoutedEventArgs e) => Export(LogExportFormat.Text, "txt");
    private void OnExportCsvClick(object sender, RoutedEventArgs e) => Export(LogExportFormat.Csv, "csv");
    private void OnExportJsonClick(object sender, RoutedEventArgs e) => Export(LogExportFormat.Json, "json");

    private void Export(LogExportFormat format, string ext)
    {
        try
        {
            if (_view.Count == 0)
            {
                StatusText.Text = "当前没有可导出的记录。";
                return;
            }

            string file = Path.Combine(ExportDir(), $"操作日志-{DateTime.Now:yyyyMMdd-HHmmss}.{ext}");
            OperationLog.Export(_view, format, file);
            OperationLog.Write(LogCategory.App, "导出日志", Path.GetFileName(file), LogResult.Success,
                $"共 {_view.Count} 条");
            StatusText.Text = "已导出：" + file;
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败：" + ex.Message;
        }
    }

    private void OnExportReportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string file = Path.Combine(ExportDir(), $"诊断报告-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            string report = OperationLog.BuildDiagnosticReport(_view);
            File.WriteAllText(file, report, new System.Text.UTF8Encoding(true));
            OperationLog.Write(LogCategory.App, "导出诊断报告", Path.GetFileName(file), LogResult.Success,
                $"共 {_view.Count} 条日志");
            StatusText.Text = "诊断报告已导出：" + file;
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败：" + ex.Message;
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(OperationLog.LogRoot);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperationLog.LogRoot,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = "打开目录失败：" + ex.Message;
        }
    }
}
