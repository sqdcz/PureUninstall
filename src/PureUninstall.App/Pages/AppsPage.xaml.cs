using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using PureUninstall.App.Helpers;
using PureUninstall.App.Services;
using PureUninstall.App.ViewModels;
using PureUninstall.Core.Models;
using PureUninstall.Core.Services;

namespace PureUninstall.App.Pages;

public sealed partial class AppsPage : Page
{
    public ObservableCollection<AppItemViewModel> Apps { get; } = new();

    private readonly List<AppItemViewModel> _all = new();
    private string _query = "";
    private string _filter = "全部类型";
    private string _sort = "按名称";
    private bool _loaded;

    public AppsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_loaded || AppState.RequestAppsRefresh)
        {
            AppState.RequestAppsRefresh = false;
            _loaded = true;
            await LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        LoadingOverlay.Visibility = Visibility.Visible;
        try
        {
            var win32 = await Task.Run(() => InstalledApps.EnumerateWin32());
            var uwp = await Task.Run(() => UwpApps.Enumerate());
            _all.Clear();
            _all.AddRange(win32.Concat(uwp).Select(a => new AppItemViewModel(a)));
            ApplyFilter();
            SelectionText.Text = $"共 {_all.Count} 个软件";
            _ = LoadIconsAsync();
        }
        finally
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadIconsAsync()
    {
        // parallel extraction with limited concurrency (sequential would take minutes)
        using var gate = new SemaphoreSlim(8);
        var tasks = _all.Select(async vm =>
        {
            await gate.WaitAsync();
            try
            {
                var iconPath = vm.Entry.DisplayIcon;
                if (string.IsNullOrWhiteSpace(iconPath) && !string.IsNullOrWhiteSpace(vm.Entry.UninstallString))
                {
                    var (file, _) = Core.Services.Uninstaller.ParseCommand(vm.Entry.UninstallString);
                    iconPath = file;
                }

                var png = await Task.Run(() => IconExtractor.GetIconFile(iconPath, 32));
                if (png is null) return;

                DispatcherQueue.TryEnqueue(() =>
                {
                    try { vm.IconImage = new BitmapImage(new Uri(png)); }
                    catch { /* ignore broken icon */ }
                });
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    private void ApplyFilter()
    {
        IEnumerable<AppItemViewModel> q = _all;

        if (!string.IsNullOrWhiteSpace(_query))
            q = q.Where(v => v.DisplayName.Contains(_query, StringComparison.OrdinalIgnoreCase)
                          || (v.Entry.Publisher?.Contains(_query, StringComparison.OrdinalIgnoreCase) ?? false));

        if (_filter == "传统安装") q = q.Where(v => v.Entry.Kind == AppKind.Win32);
        else if (_filter == "MSI 安装") q = q.Where(v => v.Entry.Kind == AppKind.Msi);
        else if (_filter == "应用商店") q = q.Where(v => v.Entry.Kind == AppKind.Uwp);

        q = _sort switch
        {
            "按大小" => q.OrderByDescending(v => v.Entry.EstimatedSizeKb),
            "按安装日期" => q.OrderByDescending(v => v.Entry.InstallDate ?? DateTimeOffset.MinValue),
            _ => q.OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase),
        };

        Apps.Clear();
        foreach (var vm in q) Apps.Add(vm);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _query = SearchBox.Text ?? "";
        ApplyFilter();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilterCombo.SelectedItem is ComboBoxItem item) _filter = item.Content as string ?? "全部类型";
        ApplyFilter();
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortCombo.SelectedItem is ComboBoxItem item) _sort = item.Content as string ?? "按名称";
        ApplyFilter();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var count = AppsList.SelectedItems.Count;
        UninstallButton.IsEnabled = count == 1;
        BatchAddButton.IsEnabled = count >= 1;
        ForceDeleteButton.IsEnabled = count == 1;
        DetailsButton.IsEnabled = count == 1;
        SelectionText.Text = count == 0 ? $"共 {_all.Count} 个软件" : $"已选 {count} 个";
    }

    private List<AppEntry> SelectedEntries() =>
        AppsList.SelectedItems.OfType<AppItemViewModel>().Select(v => v.Entry).ToList();

    private void OnOpenWizard(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        var entry = SelectedEntries().FirstOrDefault();
        if (entry is null) return;
        var request = new UninstallRequest(entry, ForceMode: false);
        AppState.PendingUninstall = request;
        Frame.Navigate(typeof(UninstallWizardPage), request);
    }

    private void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        var entry = SelectedEntries().FirstOrDefault();
        if (entry is null) return;
        var request = new UninstallRequest(entry, ForceMode: false);
        AppState.PendingUninstall = request;
        Frame.Navigate(typeof(UninstallWizardPage), request);
    }

    private void OnForceDeleteClick(object sender, RoutedEventArgs e)
    {
        var entry = SelectedEntries().FirstOrDefault();
        if (entry is null) return;
        var request = new UninstallRequest(entry, ForceMode: true);
        AppState.PendingUninstall = request;
        Frame.Navigate(typeof(UninstallWizardPage), request);
    }

    private void OnAddToBatchClick(object sender, RoutedEventArgs e)
    {
        AppState.AddToBatch(SelectedEntries());
        SelectionText.Text = $"已加入 {AppState.BatchQueue.Count} 个到批量队列";
    }

    private async void OnDetailsClick(object sender, RoutedEventArgs e)
    {
        var entry = SelectedEntries().FirstOrDefault();
        if (entry is null) return;

        var panel = new StackPanel { Spacing = 6 };
        void Row(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            panel.Children.Add(new TextBlock { Text = label, Opacity = 0.6, FontSize = 12 });
            panel.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }

        Row("类型", entry.KindText);
        Row("版本", entry.DisplayVersion);
        Row("厂商", entry.Publisher);
        Row("大小", entry.SizeText);
        Row("安装日期", entry.InstallDate?.ToString("yyyy-MM-dd"));
        Row("安装位置", entry.InstallLocation);
        Row("卸载命令", entry.UninstallString);
        Row("安静卸载命令", entry.QuietUninstallString);
        Row("注册表位置", entry.RegistryKeyPath);

        var dialog = new ContentDialog
        {
            Title = entry.DisplayName,
            Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
