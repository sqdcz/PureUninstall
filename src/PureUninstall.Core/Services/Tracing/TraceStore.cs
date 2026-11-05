using System.Text;
using System.Text.Json;
using PureUninstall.Core.Models;

namespace PureUninstall.Core.Services.Tracing;

/// <summary>
/// 追踪会话的落盘管理。
///
/// 一次追踪分两步，中间可以隔着装软件、甚至重启：
///   1. StartAsync  → 拍"装前快照"，写进 before.snap
///   2. FinishAsync → 拍"装后快照"，与 before 比对，结果写进 session.json
///
/// 这个"先落盘再比对"的设计是有意的：装软件经常要重启，如果快照只在内存里，
/// 重启后追踪就断了。落盘之后，回来点「结束追踪」就能继续。
/// </summary>
public static class TraceStore
{
    private const char Sep = '\u001F';   // 单元分隔符，路径里不会出现

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PureUninstall", "Traces");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>开始追踪：拍装前快照并落盘。</summary>
    public static async Task<TraceSession> StartAsync(
        string name, string? executablePath, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var session = new TraceSession
        {
            Name = string.IsNullOrWhiteSpace(name) ? "未命名追踪" : name.Trim(),
            ExecutablePath = executablePath,
            StartedAt = DateTimeOffset.Now,
        };

        string dir = Path.Combine(Root, session.Id);
        Directory.CreateDirectory(dir);

        var before = await Task.Run(() => Snapshotter.Capture(progress, ct), ct);
        session.BeforeCount = before.Count;
        await WriteSnapshotAsync(dir, "before.snap", before, ct);
        await WriteSessionAsync(session, ct);

        progress?.Report($"装前快照完成（{before.Count} 项），可以开始安装软件了");
        return session;
    }

    /// <summary>结束追踪：拍装后快照，差出新增项。</summary>
    public static async Task<TraceSession> FinishAsync(
        string id, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var session = await LoadAsync(id, ct)
            ?? throw new InvalidOperationException($"找不到追踪会话 {id}");

        string dir = Path.Combine(Root, id);
        var before = await ReadSnapshotAsync(dir, "before.snap", ct);
        if (before.Count == 0)
            throw new InvalidOperationException("装前快照缺失或为空，这次追踪无法比对");

        var after = await Task.Run(() => Snapshotter.Capture(progress, ct), ct);
        session.AfterCount = after.Count;
        session.Added = Snapshotter.Diff(before, after);
        session.FinishedAt = DateTimeOffset.Now;

        await WriteSnapshotAsync(dir, "after.snap", after, ct);
        await WriteSessionAsync(session, ct);

        progress?.Report($"追踪完成：新增 {session.Added.Count} 项");
        return session;
    }

    /// <summary>所有追踪记录，最近的在前面。</summary>
    public static async Task<List<TraceSession>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<TraceSession>();
        if (!Directory.Exists(Root)) return result;

        foreach (var dir in Directory.GetDirectories(Root))
        {
            string file = Path.Combine(dir, "session.json");
            if (!File.Exists(file)) continue;
            try
            {
                string json = await File.ReadAllTextAsync(file, ct);
                var s = JsonSerializer.Deserialize<TraceSession>(json, Json);
                if (s is not null) result.Add(s);
            }
            catch
            {
                // 单个文件坏了不影响其它记录
            }
        }

        return result.OrderByDescending(s => s.StartedAt).ToList();
    }

    public static async Task<TraceSession?> LoadAsync(string id, CancellationToken ct = default)
    {
        string file = Path.Combine(Root, id, "session.json");
        if (!File.Exists(file)) return null;
        try
        {
            string json = await File.ReadAllTextAsync(file, ct);
            return JsonSerializer.Deserialize<TraceSession>(json, Json);
        }
        catch
        {
            return null;
        }
    }

    public static void Delete(string id)
    {
        string dir = Path.Combine(Root, id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>把内存里的会话写回磁盘（例如清理完一批项之后，从清单里摘掉已删的）。</summary>
    public static Task SaveAsync(TraceSession session, CancellationToken ct = default)
        => WriteSessionAsync(session, ct);

    // ---------------------------------------------------------------- 内部

    private static async Task WriteSessionAsync(TraceSession s, CancellationToken ct)
    {
        string file = Path.Combine(Root, s.Id, "session.json");
        string json = JsonSerializer.Serialize(s, Json);
        await File.WriteAllTextAsync(file, json, ct);
    }

    private static async Task WriteSnapshotAsync(
        string dir, string fileName,
        IReadOnlyDictionary<string, TraceItem> snap, CancellationToken ct)
    {
        var sb = new StringBuilder(snap.Count * 128);
        foreach (var item in snap.Values)
        {
            // 六个字段都要落盘。少存 Hive/View/ValueName 会让读回来的 Key 变样，
            // 比对时所有注册表项都会被当成「新增」——那是灾难性的误报。
            sb.Append((int)item.Kind).Append(Sep)
              .Append(item.Hive is null ? "" : ((int)item.Hive.Value).ToString()).Append(Sep)
              .Append(item.View is null ? "" : ((int)item.View.Value).ToString()).Append(Sep)
              .Append(item.Path).Append(Sep)
              .Append(item.ValueName ?? string.Empty).Append(Sep)
              .Append(item.Detail ?? string.Empty).Append(Sep)
              .Append(item.SizeBytes).Append('\n');
        }
        await File.WriteAllTextAsync(Path.Combine(dir, fileName), sb.ToString(), ct);
    }

    private static async Task<Dictionary<string, TraceItem>> ReadSnapshotAsync(
        string dir, string fileName, CancellationToken ct)
    {
        var map = new Dictionary<string, TraceItem>(StringComparer.OrdinalIgnoreCase);
        string file = Path.Combine(dir, fileName);
        if (!File.Exists(file)) return map;

        string text = await File.ReadAllTextAsync(file, ct);
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            var parts = line.Split(Sep);
            if (parts.Length < 7) continue;                 // 旧格式不兼容，直接跳过
            if (!int.TryParse(parts[0], out int kind)) continue;

            Microsoft.Win32.RegistryHive? hive = int.TryParse(parts[1], out int h)
                ? (Microsoft.Win32.RegistryHive)h : null;
            Microsoft.Win32.RegistryView? view = int.TryParse(parts[2], out int v)
                ? (Microsoft.Win32.RegistryView)v : null;
            long.TryParse(parts[6], out long size);

            var item = new TraceItem
            {
                Kind = (TraceItemKind)kind,
                Hive = hive,
                View = view,
                Path = parts[3],
                ValueName = parts[4].Length == 0 ? null : parts[4],
                Detail = parts[5].Length == 0 ? null : parts[5],
                SizeBytes = size,
            };
            map[item.Key] = item;
        }
        return map;
    }
}
