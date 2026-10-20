using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PureUninstall.App.Services;
using PureUninstall.Core.Models;
using PureUninstall.Core.Services;

namespace PureUninstall.App.Pages;

public sealed partial class UninstallWizardPage : Page
{
    public ObservableCollection<ResidualItem> Residuals { get; } = new();

    private UninstallRequest _request = null!;
    private CancellationTokenSource? _cts;
    private string? _backupId;

    public UninstallWizardPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _request = (e.Parameter as UninstallRequest) ?? AppState.PendingUninstall!;
        if (_request is null)
        {
            // no target (e.g. direct navigation) - go back instead of crashing
            GoBack();
            return;
        }
        _cts = new CancellationTokenSource();

        TitleText.Text = _request.ForceMode
            ? $"强制删除：{_request.App.DisplayName}"
            : $"卸载：{_request.App.DisplayName}";
        SubtitleText.Text = _request.ForceMode
            ? "跳过卸载程序，直接扫描并删除该软件残留"
            : "先运行软件自带的卸载程序，完成后扫描残留";

        _ = RunAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _cts?.Cancel();
        base.OnNavigatedFrom(e);
    }

    private async Task RunAsync()
    {
        var ct = _cts!.Token;
        try
        {
            if (!_request.ForceMode)
            {
                RunningPanel.Visibility = Visibility.Visible;
                var outcome = await Core.Services.Uninstaller.RunAsync(
                    _request.App, AppState.Settings.QuietUninstall, TimeSpan.FromHours(1), ct,
                    log: m => DispatcherQueue.TryEnqueue(() => RunningStatus.Text = m));
                RunningPanel.Visibility = Visibility.Collapsed;

                if (outcome.Cancelled) { GoBack(); return; }
                if (!outcome.Success && outcome.Error is not null)
                {
                    var dlg = new ContentDialog
                    {
                        Title = "卸载未正常完成",
                        Content = outcome.Error + "\n\n仍可继续扫描并清理残留。",
                        CloseButtonText = "继续",
                        XamlRoot = XamlRoot,
                    };
                    await dlg.ShowAsync();
                }
            }

            ChecklistPanel.Visibility = Visibility.Visible;
            ChecklistSummary.Text = "正在扫描残留…";

            var scanProgress = new Progress<string>(m => ChecklistSummary.Text = m);
            var residuals = await ResidualScanner.ScanAsync(
                _request.App, AppState.Settings.AggressiveScan, scanProgress, ct);

            Residuals.Clear();
            foreach (var r in residuals) Residuals.Add(r);
            UpdateChecklistSummary();

            if (Residuals.Count == 0)
            {
                DoneSummary.Text = "没有发现残留，清理完成";
                DoneBackup.Text = "";
                OpenBackupDirButton.Visibility = Visibility.Collapsed;
                ChecklistPanel.Visibility = Visibility.Collapsed;
                DonePanel.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
            GoBack();
        }
        catch (Exception ex)
        {
            var dlg = new ContentDialog
            {
                Title = "出错了",
                Content = ex.Message,
                CloseButtonText = "返回",
                XamlRoot = XamlRoot,
            };
            await dlg.ShowAsync();
            GoBack();
        }
    }

    private void UpdateChecklistSummary()
    {
        var selected = Residuals.Where(r => r.Selected).ToList();
        var bytes = selected.Where(r => r.SizeBytes > 0).Sum(r => r.SizeBytes);
        ChecklistSummary.Text = $"发现 {Residuals.Count} 项残留，已选 {selected.Count} 项"
            + (bytes > 0 ? $"（约 {AppEntry.FormatBytes(bytes)}）" : "");
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var r in Residuals) r.Selected = true;
        UpdateChecklistSummary();
    }

    private void OnSelectNoneClick(object sender, RoutedEventArgs e)
    {
        foreach (var r in Residuals) r.Selected = false;
        UpdateChecklistSummary();
    }

    private async void OnCleanClick(object sender, RoutedEventArgs e)
    {
        var selected = Residuals.Where(r => r.Selected).ToList();
        ChecklistPanel.Visibility = Visibility.Collapsed;
        CleaningPanel.Visibility = Visibility.Visible;

        try
        {
            if (selected.Count > 0 && AppState.Settings.BackupBeforeDelete)
            {
                CleaningStatus.Text = "备份中…";
                var manifest = await BackupManager.CreateAsync(
                    _request.App, Residuals.ToList(),
                    new Progress<string>(m => CleaningStatus.Text = m), _cts!.Token);
                _backupId = manifest.Id;

                var failed = manifest.Items.Count(i => i.BackupFailed);
                if (failed > 0)
                    DoneBackup.Text = $"注意：{failed} 项备份失败，这些项未删除";
            }

            if (selected.Count > 0)
            {
                CleaningStatus.Text = "清理中…";
                var result = await ResidualCleaner.DeleteAsync(
                    selected, new Progress<string>(m => CleaningStatus.Text = m), _cts!.Token);

                DoneSummary.Text = result.Failed == 0
                    ? $"清理完成：删除 {result.Removed} 项残留"
                    : $"清理完成：删除 {result.Removed} 项，{result.Failed} 项失败";
            }
            else
            {
                DoneSummary.Text = "未选择任何残留，已跳过清理";
                DoneBackup.Text = "";
            }

            if (_backupId is not null)
                DoneBackup.Text = $"备份位置：{BackupManager.BackupRoot}\\{_backupId}";
            OpenBackupDirButton.Visibility = _backupId is null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            DoneSummary.Text = "清理出错";
            DoneBackup.Text = ex.Message;
        }

        CleaningPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        ChecklistPanel.Visibility = Visibility.Collapsed;
        DoneSummary.Text = "已跳过残留清理";
        DoneBackup.Text = "";
        OpenBackupDirButton.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private void OnDoneClick(object sender, RoutedEventArgs e)
    {
        AppState.RequestAppsRefresh = true;
        GoBack();
    }

    private void OnOpenBackupDirClick(object sender, RoutedEventArgs e)
    {
        if (_backupId is null) return;
        var dir = Path.Combine(BackupManager.BackupRoot, _backupId);
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch
        {
            // explorer missing - ignore
        }
    }

    private void GoBack()
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(AppsPage));
    }
}
