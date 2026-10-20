using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PureUninstall.App.Services;
using PureUninstall.Core.Services;

namespace PureUninstall.App.Pages;

public sealed partial class BackupsPage : Page
{
    public ObservableCollection<BackupManifest> Backups { get; } = new();

    public BackupsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Refresh();
    }

    private void Refresh()
    {
        Backups.Clear();
        foreach (var backup in BackupManager.ListBackups())
            Backups.Add(backup);
        StatusText.Text = Backups.Count == 0
            ? "暂无备份。卸载清理时会自动创建（可在设置里关闭）。"
            : $"共 {Backups.Count} 个备份";
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BackupManifest manifest }) return;

        var dialog = new ContentDialog
        {
            Title = "确认还原",
            Content = $"将还原「{manifest.AppName}」的 {manifest.Items.Count} 项备份（注册表重新导入 + 文件拷回原位置）。",
            PrimaryButtonText = "还原",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        StatusText.Text = "正在还原…";
        try
        {
            var result = await BackupManager.RestoreAsync(manifest.Id);
            StatusText.Text = result.Failed == 0
                ? $"还原完成：{result.Restored} 项"
                : $"还原完成：{result.Restored} 项成功，{result.Failed} 项失败（{string.Join("; ", result.Errors.Take(3))}）";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"还原失败: {ex.Message}";
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BackupManifest manifest }) return;

        var dialog = new ContentDialog
        {
            Title = "确认删除备份",
            Content = $"将永久删除「{manifest.AppName}」的备份（{manifest.Items.Count} 项），删除后无法还原。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        StatusText.Text = BackupManager.DeleteBackup(manifest.Id)
            ? $"已删除备份 {manifest.Id}"
            : "删除失败（可能被占用）";
        Refresh();
    }
}
