using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PureUninstall.App.Services;
using PureUninstall.Core.Services;

namespace PureUninstall.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        foreach (var radio in new[] { ThemeDefault, ThemeLight, ThemeDark })
            radio.IsChecked = AppState.Settings.Theme == radio.Tag as string;

        QuietBox.IsChecked = AppState.Settings.QuietUninstall;
        AggressiveBox.IsChecked = AppState.Settings.AggressiveScan;
        BackupBox.IsChecked = AppState.Settings.BackupBeforeDelete;
        UpdateUrlBox.Text = AppState.Settings.UpdateCheckUrl;
        VersionText.Text = $"纯净卸载 v{UpdateChecker.CurrentVersion}";

        var isAdmin = AdminHelper.IsAdministrator();
        AdminStateText.Text = isAdmin ? "当前：管理员模式（可清理全部系统位置）" : "当前：标准模式（部分系统位置清理会失败）";
        ElevateButton.IsEnabled = !isAdmin;
        ElevateButton.Visibility = isAdmin ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string theme }) return;
        AppState.Settings.Theme = theme;
        SettingsService.Save(AppState.Settings);
        if (App.MainWindow is MainWindow window)
            window.ApplyTheme();
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        AppState.Settings.QuietUninstall = QuietBox.IsChecked == true;
        AppState.Settings.AggressiveScan = AggressiveBox.IsChecked == true;
        AppState.Settings.BackupBeforeDelete = BackupBox.IsChecked == true;
        SettingsService.Save(AppState.Settings);
    }

    private void OnUpdateUrlLostFocus(object sender, RoutedEventArgs e)
    {
        AppState.Settings.UpdateCheckUrl = UpdateUrlBox.Text?.Trim() ?? "";
        SettingsService.Save(AppState.Settings);
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = "正在检查…";
        var (_, message) = await UpdateChecker.CheckAsync(AppState.Settings.UpdateCheckUrl);
        UpdateStatusText.Text = message;
    }

    private void OnElevateClick(object sender, RoutedEventArgs e)
    {
        if (AdminHelper.RestartAsAdmin())
            Application.Current.Exit();
    }
}
