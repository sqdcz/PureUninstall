using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PureUninstall.App.Pages;
using PureUninstall.App.Services;
using PureUninstall.Core.Services;
using Windows.Graphics;

namespace PureUninstall.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        try
        {
            AppWindow.Resize(new SizeInt32(1180, 780));
        }
        catch
        {
            // resize unsupported
        }

        ApplyTheme();

        var isAdmin = AdminHelper.IsAdministrator();
        AdminBanner.Visibility = isAdmin ? Visibility.Collapsed : Visibility.Visible;

        AppState.QueueChanged += UpdateBatchBadge;

        ContentFrame.Navigate(typeof(AppsPage));
    }

    public void ApplyTheme()
    {
        var theme = AppState.Settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        if (Content is FrameworkElement root)
            root.RequestedTheme = theme;
    }

    private void UpdateBatchBadge()
    {
        var count = AppState.BatchQueue.Count;
        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag as string == "batch")
                item.InfoBadge = count > 0 ? new InfoBadge { Value = count } : null;
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not NavigationViewItem item) return;

        var page = (item.Tag as string) switch
        {
            "batch" => typeof(BatchPage),
            "backups" => typeof(BackupsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(AppsPage),
        };

        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }

    private void OnRestartAdmin(object sender, RoutedEventArgs e)
    {
        if (AdminHelper.RestartAsAdmin())
            Application.Current.Exit();
    }
}
