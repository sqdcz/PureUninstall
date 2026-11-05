using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PureUninstall.App.Services;
using PureUninstall.Core.Models;
using PureUninstall.Core.Services;
using PureUninstall.Core.Services.Tracing;

namespace PureUninstall.App.Pages;

/// <summary>
/// 安装追踪：装前快照 → 装软件 → 装后快照 → 差集。
/// 界面上只做「开始 / 结束 / 看结果 / 清理」，快照本体在 Core 里。
/// </summary>
public sealed partial class TracePage : Page
{
    private readonly ObservableCollection<TraceSession> _sessions = new();
    private readonly ObservableCollection<TraceItem> _items = new();
    private TraceSession? _active;
    private TraceSession? _selected;

    public TracePage()
    {
        InitializeComponent();
        SessionList.ItemsSource = _sessions;
        ItemList.ItemsSource = _items;
        Loaded += async (_, _) => await RefreshAsync();
    }

    // ---------------------------------------------------------------- 加载

    private async Task RefreshAsync()
    {
        try
        {
            var list = await TraceStore.ListAsync();
            _sessions.Clear();
            foreach (var s in list) _sessions.Add(s);

            HistoryCount.Text = list.Count == 0 ? "" : $"{list.Count} 条";

            // 有未完成的追踪就把它顶到进行中状态
            _active = list.FirstOrDefault(s => !s.IsFinished);
            ActiveBar.Visibility = _active is null ? Visibility.Collapsed : Visibility.Visible;
            if (_active is not null)
            {
                ActiveTitle.Text = $"正在追踪「{_active.Name}」";
                ActiveHint.Text = $"装前快照已记录 {_active.BeforeCount} 项。现在去装软件，装完（重启也行）回来点「结束追踪」。";
            }
            StartButton.IsEnabled = _active is null;

            if (_selected is not null)
            {
                var fresh = list.FirstOrDefault(s => s.Id == _selected.Id);
                if (fresh is not null) ShowSession(fresh);
            }
            else if (list.Count > 0)
            {
                SessionList.SelectedIndex = 0;
            }
            else
            {
                ClearDetail();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取记录失败：" + ex.Message;
        }
    }

    private void ShowSession(TraceSession session)
    {
        _selected = session;
        DetailTitle.Text = session.Name;

        _items.Clear();
        foreach (var item in session.Added) _items.Add(item);

        bool hasItems = session.Added.Count > 0;
        EmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        ItemList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;

        DetailSummary.Text = session.IsFinished
            ? session.SummaryText + (session.TotalBytes > 0 ? $" · {AppEntry.FormatBytes(session.TotalBytes)}" : "")
            : "进行中";

        CleanButton.IsEnabled = hasItems;
        ExportButton.IsEnabled = hasItems;
        DeleteTraceButton.IsEnabled = true;

        if (hasItems && ItemList.SelectedItems.Count == 0)
        {
            // 默认全选，用户可以自己取消
            foreach (var item in _items) ItemList.SelectedItems.Add(item);
        }
    }

    private void ClearDetail()
    {
        _selected = null;
        _items.Clear();
        DetailTitle.Text = "选择一个追踪记录查看结果";
        DetailSummary.Text = "";
        EmptyState.Visibility = Visibility.Visible;
        ItemList.Visibility = Visibility.Collapsed;
        CleanButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        DeleteTraceButton.IsEnabled = false;
    }

    // ---------------------------------------------------------------- 交互

    private void OnSessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is TraceSession s) ShowSession(s);
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_active is not null)
        {
            StatusText.Text = "已有一个进行中的追踪，先结束它。";
            return;
        }

        var input = new TextBox { PlaceholderText = "例如：某播放器 / 某驱动工具" };
        var dlg = new ContentDialog
        {
            Title = "开始追踪",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "先给这次追踪起个名字。点「开始」后会扫描一遍系统，大约十几秒。",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.75,
                        FontSize = 13,
                    },
                    input,
                },
            },
            PrimaryButtonText = "开始",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        string name = input.Text.Trim();
        if (name.Length == 0) name = "未命名追踪";

        SetBusy(true, "正在拍装前快照…");
        try
        {
            var progress = new Progress<string>(m => StatusText.Text = m);
            var session = await TraceStore.StartAsync(name, null, progress, CancellationToken.None);
            OperationLog.Write(LogCategory.Trace, "开始追踪", name, LogResult.Success,
                $"装前快照 {session.BeforeCount} 项");

            await RefreshAsync();
            StatusText.Text = "装前快照完成，去装软件吧。";
        }
        catch (Exception ex)
        {
            OperationLog.Write(LogCategory.Trace, "开始追踪", name, LogResult.Failed, ex.Message);
            StatusText.Text = "开始失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async void OnFinishClick(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;

        SetBusy(true, "正在拍装后快照并比对…");
        try
        {
            var progress = new Progress<string>(m => StatusText.Text = m);
            var session = await TraceStore.FinishAsync(_active.Id, progress, CancellationToken.None);

            OperationLog.Write(LogCategory.Trace, "结束追踪", session.Name, LogResult.Success,
                $"装前 {session.BeforeCount} 项 → 装后 {session.AfterCount} 项，新增 {session.Added.Count} 项");

            await RefreshAsync();

            var fresh = _sessions.FirstOrDefault(s => s.Id == session.Id);
            if (fresh is not null)
            {
                SessionList.SelectedItem = fresh;
                ShowSession(fresh);
            }
            StatusText.Text = session.Added.Count == 0
                ? "没检测到新增项——可能是安装程序没动系统位置，或装之前忘了开始追踪。"
                : $"追踪完成，新增 {session.Added.Count} 项。";
        }
        catch (Exception ex)
        {
            OperationLog.Write(LogCategory.Trace, "结束追踪", _active.Name, LogResult.Failed, ex.Message);
            StatusText.Text = "结束失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;

        var dlg = new ContentDialog
        {
            Title = "放弃这次追踪？",
            Content = "装前快照和这条记录都会被删除。",
            PrimaryButtonText = "放弃",
            CloseButtonText = "继续追踪",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            TraceStore.Delete(_active.Id);
            OperationLog.Write(LogCategory.Trace, "放弃追踪", _active.Name, LogResult.Cancelled);
        }
        catch { }

        _selected = null;
        await RefreshAsync();
        StatusText.Text = "已放弃。";
    }

    // ---------------------------------------------------------------- 清理

    private async void OnCleanClick(object sender, RoutedEventArgs e)
    {
        var selected = ItemList.SelectedItems.OfType<TraceItem>().ToList();
        if (selected.Count == 0 || _selected is null)
        {
            StatusText.Text = "没有选中任何项。";
            return;
        }

        var residuals = TraceConverter.ToResiduals(selected);
        int skipped = selected.Count - residuals.Count;

        if (residuals.Count == 0)
        {
            StatusText.Text = "选中的项无法安全删除（缺少定位信息）。";
            return;
        }

        var preview = string.Join("\n", residuals.Take(6).Select(r => $"· [{r.KindText}] {r.Path}"));
        if (residuals.Count > 6) preview += $"\n… 另有 {residuals.Count - 6} 项";

        var dlg = new ContentDialog
        {
            Title = $"删除 {residuals.Count} 项？",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = AppState.Settings.BackupBeforeDelete
                            ? "删除前会先备份（注册表导出 + 文件快照），删错了可以在「备份还原」里恢复。"
                            : "当前设置为不备份，删除后无法恢复。可在设置里打开「删除前备份」。",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        Opacity = 0.8,
                    },
                    new TextBlock
                    {
                        Text = preview,
                        TextWrapping = TextWrapping.Wrap,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                        FontSize = 12,
                        Opacity = 0.7,
                    },
                },
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true, "正在备份…");
        try
        {
            var ct = CancellationToken.None;
            var progress = new Progress<string>(m => StatusText.Text = m);

            string? backupId = null;
            if (AppState.Settings.BackupBeforeDelete)
            {
                var pseudo = new AppEntry
                {
                    Id = "trace:" + _selected.Id,
                    DisplayName = _selected.Name,
                    Kind = AppKind.Win32,
                };
                var manifest = await BackupManager.CreateAsync(pseudo, residuals, progress, ct);
                backupId = manifest.Id;
                OperationLog.Write(LogCategory.Backup, "备份追踪项", _selected.Name, LogResult.Success,
                    $"备份 {manifest.Items.Count} 项，编号 {backupId}");
            }

            StatusText.Text = "正在删除…";
            var result = await ResidualCleaner.DeleteAsync(residuals, progress, ct);

            OperationLog.Write(LogCategory.Clean, "删除追踪项", _selected.Name,
                result.Failed == 0 ? LogResult.Success : LogResult.Failed,
                $"删除 {result.Removed} 项，失败 {result.Failed} 项" +
                (skipped > 0 ? $"，跳过 {skipped} 项（无法安全映射）" : "") +
                (backupId is null ? "" : $"，备份 {backupId}"));

            // 成功的项从记录里摘掉，避免重复删。
            // 判据用「失败清单」反推：没有失败的就是删掉了，没映射上的保留（用户可能还想要）
            var failedPaths = new HashSet<string>(
                result.FailedItems.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);

            _selected.Added = _selected.Added
                .Where(a =>
                {
                    var r = TraceConverter.ToResidual(a);
                    if (r is null) return true;
                    return failedPaths.Contains(r.Path);
                })
                .ToList();

            await TraceStore.SaveAsync(_selected, ct);
            ShowSession(_selected);

            StatusText.Text = result.Failed == 0
                ? $"完成：删除 {result.Removed} 项" + (backupId is null ? "" : $"，已备份到 {backupId}")
                : $"删除 {result.Removed} 项，{result.Failed} 项失败（可能需要管理员权限）";
        }
        catch (Exception ex)
        {
            OperationLog.Write(LogCategory.Clean, "删除追踪项", _selected.Name, LogResult.Failed, ex.Message);
            StatusText.Text = "删除失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async void OnDeleteTraceClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        var dlg = new ContentDialog
        {
            Title = "删除这条追踪记录？",
            Content = "只删除记录本身，不会动系统里的任何文件。",
            PrimaryButtonText = "删除记录",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            TraceStore.Delete(_selected.Id);
            OperationLog.Write(LogCategory.Trace, "删除追踪记录", _selected.Name, LogResult.Success);
        }
        catch { }

        _selected = null;
        await RefreshAsync();
        StatusText.Text = "记录已删除。";
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _items.Count == 0) return;

        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "PureUninstall导出");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir,
                $"追踪-{SafeFileName(_selected.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"追踪名称：{_selected.Name}");
            sb.AppendLine($"开始时间：{_selected.StartedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"结束时间：{_selected.FinishedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"快照条目：装前 {_selected.BeforeCount} 项 / 装后 {_selected.AfterCount} 项");
            sb.AppendLine($"新增：{_selected.Added.Count} 项，合计 {AppEntry.FormatBytes(_selected.TotalBytes)}");
            sb.AppendLine(new string('=', 60));

            foreach (var group in _selected.Added.GroupBy(a => a.KindText))
            {
                sb.AppendLine();
                sb.AppendLine($"【{group.Key}】{group.Count()} 项");
                foreach (var item in group)
                {
                    sb.Append("  ").Append(item.Path);
                    if (!string.IsNullOrEmpty(item.Subtitle)) sb.Append("    ").Append(item.Subtitle);
                    sb.AppendLine();
                }
            }

            File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(true));
            OperationLog.Write(LogCategory.Trace, "导出追踪清单", _selected.Name, LogResult.Success, file);
            StatusText.Text = "已导出：" + file;
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败：" + ex.Message;
        }
    }

    // ---------------------------------------------------------------- 工具

    private void SetBusy(bool busy, string? message)
    {
        BusyRing.IsActive = busy;
        StartButton.IsEnabled = !busy && _active is null;
        FinishButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        CleanButton.IsEnabled = !busy && _items.Count > 0;
        if (message is not null) StatusText.Text = message;
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length > 40 ? name[..40] : name;
    }
}
