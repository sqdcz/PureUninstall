using PureUninstall.Core.Models;

namespace PureUninstall.App.Services;

/// <summary>Shared app-wide state (batch queue, settings, pending uninstall target).</summary>
public static class AppState
{
    public static AppSettings Settings { get; set; } = SettingsService.Load();

    public static List<AppEntry> BatchQueue { get; } = new();

    public static UninstallRequest? PendingUninstall { get; set; }

    /// <summary>Raised when the batch queue changes so nav badges can update.</summary>
    public static event Action? QueueChanged;

    /// <summary>Set when something changed the installed-apps list (e.g. an uninstall finished).</summary>
    public static bool RequestAppsRefresh { get; set; }

    public static void AddToBatch(IEnumerable<AppEntry> apps)
    {
        var added = 0;
        foreach (var app in apps)
        {
            if (BatchQueue.Any(a => a.Id == app.Id && a.Kind == app.Kind)) continue;
            BatchQueue.Add(app);
            added++;
        }
        if (added > 0) QueueChanged?.Invoke();
    }

    public static void RemoveFromBatch(AppEntry app)
    {
        if (BatchQueue.RemoveAll(a => a.Id == app.Id && a.Kind == app.Kind) > 0)
            QueueChanged?.Invoke();
    }

    public static void ClearBatch()
    {
        if (BatchQueue.Count == 0) return;
        BatchQueue.Clear();
        QueueChanged?.Invoke();
    }
}

/// <summary>What the uninstall wizard should do.</summary>
public sealed record UninstallRequest(AppEntry App, bool ForceMode);
