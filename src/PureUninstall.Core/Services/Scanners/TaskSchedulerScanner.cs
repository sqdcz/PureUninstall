using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services.Scanners;

/// <summary>
/// Scans the Windows Task Scheduler (COM "Schedule.Service") for tasks whose
/// name or executable actions reference the app, and can delete them.
/// This is the cleanup step plain uninstallers (including Geek) never do.
/// </summary>
public static class TaskSchedulerScanner
{
    private static dynamic? Connect()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return null;
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            return service;
        }
        catch
        {
            return null;
        }
    }

    public static List<ResidualItem> Scan(AppEntry app)
    {
        var items = new List<ResidualItem>();
        var tokens = MatchHelper.Tokens(app.DisplayName);
        if (tokens.Count == 0) return items;

        var service = Connect();
        if (service is null) return items;

        try
        {
            ScanFolder(service.GetFolder("\\"), "\\", tokens, items);
        }
        catch
        {
            // enumeration failed (permissions etc.)
        }
        return items;
    }

    private static void ScanFolder(dynamic folder, string folderPath, List<string> tokens, List<ResidualItem> items)
    {
        // tasks
        try
        {
            var tasks = folder.GetTasks(0);
            int count = (int)tasks.Count;
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    dynamic task = tasks.Item(i);
                    var name = (string)task.Name;
                    var taskPath = folderPath.TrimEnd('\\') + "\\" + name;
                    var hit = MatchHelper.IsPotentialMatch(name, tokens);
                    string? detail = null;

                    if (!hit)
                    {
                        var actions = task.Definition.Actions;
                        int ac = (int)actions.Count;
                        for (int a = 1; a <= ac; a++)
                        {
                            try
                            {
                                dynamic action = actions.Item(a);
                                if ((int)action.Type == 0) // exec action
                                {
                                    var text = (string)action.Path + " " + (string)action.Arguments;
                                    if (MatchHelper.IsPotentialMatch(text, tokens))
                                    {
                                        hit = true;
                                        detail = text.Trim();
                                        break;
                                    }
                                }
                            }
                            catch { /* skip unreadable action */ }
                        }
                    }

                    if (hit)
                        items.Add(new ResidualItem { Kind = ResidualKind.ScheduledTask, Path = taskPath, Detail = detail });
                }
                catch { /* skip unreadable task */ }
            }
        }
        catch { /* folder unreadable */ }

        // recurse
        try
        {
            var folders = folder.GetFolders(0);
            int fcount = (int)folders.Count;
            for (int i = 1; i <= fcount; i++)
            {
                try
                {
                    dynamic sub = folders.Item(i);
                    var subName = (string)sub.Name;
                    ScanFolder(sub, folderPath.TrimEnd('\\') + "\\" + subName + "\\", tokens, items);
                }
                catch { /* skip unreadable folder */ }
            }
        }
        catch { /* folder list unreadable */ }
    }

    public static bool Delete(ResidualItem item)
    {
        var service = Connect();
        if (service is null) return false;
        try
        {
            var idx = item.Path.LastIndexOf('\\');
            if (idx <= 0) return false;
            var folderPath = item.Path[..idx];
            var taskName = item.Path[(idx + 1)..];
            var folder = service.GetFolder(string.IsNullOrEmpty(folderPath) ? "\\" : folderPath);
            folder.DeleteTask(taskName, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
