using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PureUninstall.App.Services;
using PureUninstall.App.ViewModels;
using PureUninstall.Core.Models;
using PureUninstall.Core.Services;
using PureUninstall.Core.Services.Persistence;
using PureUninstall.Core.Services.Signatures;

namespace PureUninstall.App.Pages;

/// <summary>
/// 持久化检测：把所有会自动运行的位置列出来，按可疑度排序。
///
/// 这里的定位是「帮你找到该看的地方」，不是「替你说的算病毒」。
/// 所以每条都给理由、都能看到签名，删除前一定备份。
/// </summary>
public sealed partial class PersistencePage : Page
{
    private readonly ObservableCollection<PersistenceRow> _view = new();
    private List<PersistenceItem> _all = new();
    private bool _busy;
    // ComboBox 的 IsSelected 会在 InitializeComponent 期间就触发 SelectionChanged，
    // 那时 ItemList / EmptyState 还没建好，直接访问就是空引用
    private bool _ready;

    public PersistencePage()
    {
        InitializeComponent();
        ItemList.ItemsSource = _view;
        Loaded += (_, _) =>
        {
            _ready = true;
            UpdateSignatureInfo();
        };
    }

    // ---------------------------------------------------------------- 扫描

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        ScanButton.IsEnabled = false;
        BusyRing.IsActive = true;
        StatusText.Text = "正在扫描…";

        try
        {
            var progress = new Progress<string>(m => StatusText.Text = m);
            var items = await Task.Run(() => PersistenceScanner.Scan(progress));

            _all = items;
            var sw = _all.Count;
            OperationLog.Write(LogCategory.Scan, "持久化检测", "", LogResult.Success,
                $"共 {sw} 项：高 {Count(RiskLevel.High)} / 中 {Count(RiskLevel.Medium)} / 低 {Count(RiskLevel.Low)}");

            StatsBar.Visibility = Visibility.Visible;
            HighCount.Text = Count(RiskLevel.High).ToString();
            MediumCount.Text = Count(RiskLevel.Medium).ToString();
            LowCount.Text = Count(RiskLevel.Low).ToString();
            SystemCount.Text = _all.Count(i => i.IsSystemComponent).ToString();
            ExportButton.IsEnabled = _all.Count > 0;

            ApplyFilter();
            StatusText.Text = $"扫描完成，{_all.Count} 项，用时很短";
        }
        catch (Exception ex)
        {
            StatusText.Text = "扫描失败：" + ex.Message;
            OperationLog.Write(LogCategory.Scan, "持久化检测", "", LogResult.Failed, ex.Message);
        }
        finally
        {
            BusyRing.IsActive = false;
            ScanButton.IsEnabled = true;
            _busy = false;
        }
    }

    private int Count(RiskLevel level) => _all.Count(i => i.Risk == level);

    // ---------------------------------------------------------------- 过滤

    private void ApplyFilter()
    {
        if (!_ready) return;

        var rows = FilterBox.SelectedIndex switch
        {
            1 => _all.Where(i => i.Risk == RiskLevel.High),
            2 => _all.AsEnumerable(),
            _ => _all.Where(i => !i.IsSystemComponent && i.Risk >= RiskLevel.Medium),
        };

        _view.Clear();
        foreach (var item in rows) _view.Add(new PersistenceRow(item));

        bool empty = _view.Count == 0;
        EmptyState.Visibility = empty && _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ItemList.Visibility = _view.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_all.Count > 0 && _view.Count == 0)
            StatusText.Text = "这个筛选下没有项目 —— 说明没有需要关注的。";

        CountText.Text = _all.Count == 0
            ? ""
            : _view.Count == _all.Count ? $"共 {_view.Count} 项" : $"显示 {_view.Count} / {_all.Count} 项";

        UpdateSelectionState();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void OnSelectAllChanged(object sender, RoutedEventArgs e)
    {
        bool on = SelectAllBox.IsChecked == true;
        foreach (var row in _view.Where(r => r.Item.CanDelete))
        {
            if (on) ItemList.SelectedItems.Add(row);
            else ItemList.SelectedItems.Remove(row);
        }
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        int n = ItemList.SelectedItems.Count;
        CleanButton.IsEnabled = n > 0 && !_busy;
        SelectAllBox.IsChecked = n > 0 && n == _view.Count(r => r.Item.CanDelete) ? true : false;
    }

    // ---------------------------------------------------------------- 特征库

    private void UpdateSignatureInfo()
    {
        int n = SignatureStore.RuleCount;
        SignatureInfo.Text = n == 0 ? "未导入特征库" : $"已加载 {n} 条特征规则";
    }

    private async void OnImportSignatureClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var result = SignatureStore.Import(file.Path);
            UpdateSignatureInfo();

            if (result.Ok)
            {
                StatusText.Text = $"已导入 {result.FileName}，共 {result.RuleCount} 条规则。重新检测即可生效。";
                OperationLog.Write(LogCategory.Scan, "导入特征库", result.FileName, LogResult.Success,
                    $"{result.RuleCount} 条规则");
                if (_all.Count > 0) OnScanClick(sender, e);   // 有结果就直接重扫一遍
            }
            else
            {
                StatusText.Text = "导入失败：" + result.Error;
                OperationLog.Write(LogCategory.Scan, "导入特征库", result.FileName, LogResult.Failed, result.Error);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "导入失败：" + ex.Message;
        }
    }

    private async void OnExportTemplateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PureUninstall导出");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "特征库模板.json");
            SignatureStore.WriteTemplate(file);

            var dlg = new ContentDialog
            {
                Title = "模板已导出",
                Content = new TextBlock
                {
                    Text = file + "\n\n按里面的格式写好规则后，回到这里点「导入特征库」。",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                },
                CloseButtonText = "好",
                XamlRoot = XamlRoot,
            };
            await dlg.ShowAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败：" + ex.Message;
        }
    }

    // ---------------------------------------------------------------- 处理

    private async void OnCleanClick(object sender, RoutedEventArgs e)
    {
        var rows = ItemList.SelectedItems.OfType<PersistenceRow>().ToList();
        if (rows.Count == 0) return;

        var deletable = rows.Where(r => r.Item.CanDelete).Select(r => r.Item.ToResidual()).Where(r => r != null).ToList()!;
        int skipped = rows.Count - deletable.Count;

        if (deletable.Count == 0)
        {
            StatusText.Text = "选中的项都不支持自动删除（WMI 订阅需要手动在 wbemtest 里清）。";
            return;
        }

        var preview = string.Join("\n", deletable.Take(8).Select(r => $"· [{r!.KindText}] {r.Path}"));
        if (deletable.Count > 8) preview += $"\n… 另有 {deletable.Count - 8} 项";

        var dlg = new ContentDialog
        {
            Title = $"删除 {deletable.Count} 项？",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = AppState.Settings.BackupBeforeDelete
                            ? "删除前会先备份，删错了可以在「备份还原」里恢复。注意：删掉正常软件的自启动项，那个软件可能就不会开机自启了。"
                            : "当前设置为不备份，删除后无法恢复。",
                        TextWrapping = TextWrapping.Wrap, FontSize = 13, Opacity = 0.8,
                    },
                    new TextBlock
                    {
                        Text = preview, TextWrapping = TextWrapping.Wrap,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                        FontSize = 12, Opacity = 0.7,
                    },
                },
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        _busy = true;
        BusyRing.IsActive = true;
        StatusText.Text = "正在备份…";

        try
        {
            var list = deletable.Select(r => r!).ToList();
            var ct = CancellationToken.None;
            var progress = new Progress<string>(m => StatusText.Text = m);

            string? backupId = null;
            if (AppState.Settings.BackupBeforeDelete)
            {
                var pseudo = new AppEntry { Id = "persist", DisplayName = "持久化项", Kind = AppKind.Win32 };
                var manifest = await BackupManager.CreateAsync(pseudo, list, progress, ct);
                backupId = manifest.Id;
                OperationLog.Write(LogCategory.Backup, "备份持久化项", "", LogResult.Success,
                    $"备份 {manifest.Items.Count} 项，编号 {backupId}");
            }

            StatusText.Text = "正在删除…";
            var result = await ResidualCleaner.DeleteAsync(list, progress, ct);

            OperationLog.Write(LogCategory.Clean, "删除持久化项", "",
                result.Failed == 0 ? LogResult.Success : LogResult.Failed,
                $"删除 {result.Removed} 项，失败 {result.Failed} 项" +
                (skipped > 0 ? $"，跳过 {skipped} 项（不支持自动删除）" : "") +
                (backupId is null ? "" : $"，备份 {backupId}"));

            StatusText.Text = result.Failed == 0
                ? $"完成：删除 {result.Removed} 项" + (backupId is null ? "" : $"，已备份")
                : $"删除 {result.Removed} 项，{result.Failed} 项失败（部分服务需要管理员权限）";

            // 删完立刻重扫，避免列表还挂着已经不存在的东西
            await Task.Delay(400);
            OnScanClick(sender, e);
        }
        catch (Exception ex)
        {
            StatusText.Text = "删除失败：" + ex.Message;
            OperationLog.Write(LogCategory.Clean, "删除持久化项", "", LogResult.Failed, ex.Message);
        }
        finally
        {
            BusyRing.IsActive = false;
            _busy = false;
            UpdateSelectionState();
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_all.Count == 0) return;
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PureUninstall导出");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"持久化检测-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("纯净卸载 · 持久化检测报告");
            sb.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"管理员权限：{(OperationLog.IsElevated ? "是" : "否")}");
            sb.AppendLine($"共 {_all.Count} 项 —— 高 {Count(RiskLevel.High)} / 中 {Count(RiskLevel.Medium)} / " +
                          $"低 {Count(RiskLevel.Low)} / 系统组件 {_all.Count(i => i.IsSystemComponent)}");
            sb.AppendLine(new string('=', 68));

            foreach (var group in _all.Where(i => !i.IsSystemComponent).GroupBy(i => i.Risk).OrderByDescending(g => g.Key))
            {
                sb.AppendLine();
                sb.AppendLine($"【{(group.Key == RiskLevel.High ? "高风险" : group.Key == RiskLevel.Medium ? "中风险" : "低风险")}】{group.Count()} 项");
                foreach (var item in group.GroupBy(i => i.Kind))
                {
                    sb.AppendLine($"  -- {item.First().KindText} --");
                    foreach (var p in item)
                    {
                        sb.AppendLine($"    {p.Name}");
                        sb.AppendLine($"      命令：{p.Command}");
                        sb.AppendLine($"      签名：{p.SignerText}");
                        sb.AppendLine($"      位置：{p.Location}");
                        foreach (var r in p.Reasons) sb.AppendLine($"      · {r}");
                    }
                }
            }

            int sysCount = _all.Count(i => i.IsSystemComponent);
            if (sysCount > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"【系统组件】{sysCount} 项（位于 Windows 目录，未计入风险）");
                foreach (var p in _all.Where(i => i.IsSystemComponent).Take(300))
                    sb.AppendLine($"    [{p.KindText}] {p.Name}  {p.Command}");
            }

            File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(true));
            OperationLog.Write(LogCategory.Scan, "导出持久化报告", "", LogResult.Success, file);
            StatusText.Text = "已导出：" + file;
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败：" + ex.Message;
        }
    }
}
