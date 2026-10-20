using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PureUninstall.App.Services;
using PureUninstall.Core.Models;
using PureUninstall.Core.Services;

namespace PureUninstall.App.Pages;

public sealed partial class BatchPage : Page
{
    public ObservableCollection<AppEntry> Queue { get; } = new();
    public ObservableCollection<BatchItemResult> Results { get; } = new();

    private CancellationTokenSource? _cts;

    public BatchPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RefreshQueue();
        AppState.QueueChanged += RefreshQueue;

        QuietBox.IsChecked = AppState.Settings.QuietUninstall;
        AggressiveBox.IsChecked = AppState.Settings.AggressiveScan;
        BackupBox.IsChecked = AppState.Settings.BackupBeforeDelete;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppState.QueueChanged -= RefreshQueue;
        base.OnNavigatedFrom(e);
    }

    private void RefreshQueue()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Queue.Clear();
            foreach (var app in AppState.BatchQueue) Queue.Add(app);
            QueueCountText.Text = Queue.Count == 0 ? "队列为空" : $"共 {Queue.Count} 个";
            RunBatchButton.IsEnabled = Queue.Count > 0 && CancelBatchButton.IsEnabled == false;
        });
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        AppState.Settings.QuietUninstall = QuietBox.IsChecked == true;
        AppState.Settings.AggressiveScan = AggressiveBox.IsChecked == true;
        AppState.Settings.BackupBeforeDelete = BackupBox.IsChecked == true;
        SettingsService.Save(AppState.Settings);
    }

    private void OnRemoveFromQueueClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AppEntry app })
            AppState.RemoveFromBatch(app);
    }

    private void OnClearQueueClick(object sender, RoutedEventArgs e)
    {
        AppState.ClearBatch();
    }

    private void OnCancelBatchClick(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private async void OnRunBatchClick(object sender, RoutedEventArgs e)
    {
        if (Queue.Count == 0) return;

        var options = new BatchOptions
        {
            Quiet = AppState.Settings.QuietUninstall,
            AggressiveScan = AppState.Settings.AggressiveScan,
            BackupBeforeDelete = AppState.Settings.BackupBeforeDelete,
            DeleteResiduals = true,
            SkipUninstaller = ForceModeBox.IsChecked == true,
        };

        Results.Clear();
        RunBatchButton.IsEnabled = false;
        CancelBatchButton.IsEnabled = true;
        BatchProgressBar.Value = 0;
        _cts = new CancellationTokenSource();

        var progress = new Progress<BatchProgress>(p =>
        {
            BatchProgressBar.Value = (p.Done - 1) * 100.0 / Math.Max(1, p.Total);
            BatchStatusText.Text = $"[{p.Done}/{p.Total}] {p.CurrentApp} — {p.Message}";
        });

        try
        {
            var results = await BatchUninstaller.RunAsync(Queue.ToList(), options, progress, _cts.Token);
            foreach (var r in results) Results.Add(r);
            BatchProgressBar.Value = 100;
            BatchStatusText.Text = $"完成：{results.Count(r => r.Outcome?.Success ?? false)}/{results.Count} 卸载成功"
                + $"，共清理 {results.Sum(r => r.RemovedCount)} 项残留";
        }
        catch (OperationCanceledException)
        {
            BatchStatusText.Text = "已取消";
        }
        catch (Exception ex)
        {
            BatchStatusText.Text = $"出错: {ex.Message}";
        }
        finally
        {
            RunBatchButton.IsEnabled = Queue.Count > 0;
            CancelBatchButton.IsEnabled = false;
        }
    }
}
