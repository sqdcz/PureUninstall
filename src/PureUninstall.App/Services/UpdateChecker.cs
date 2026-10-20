using System.Net.Http.Json;
using System.Text.Json;

namespace PureUninstall.App.Services;

/// <summary>Checks a GitHub-releases-style JSON endpoint for a newer version.</summary>
public static class UpdateChecker
{
    public static string CurrentVersion =>
        typeof(AppState).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public static async Task<(bool Ok, string Message)> CheckAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return (false, "未配置更新检查地址");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PureUninstall");
            using var doc = JsonDocument.Parse(await client.GetStringAsync(url, ct));
            if (!doc.RootElement.TryGetProperty("tag_name", out var tag))
                return (false, "返回格式不正确（缺少 tag_name）");

            var tagText = tag.GetString();
            if (string.IsNullOrWhiteSpace(tagText))
                return (false, "tag_name 为空");

            var current = typeof(AppState).Assembly.GetName().Version ?? new Version(0, 1, 0);
            var clean = tagText.TrimStart('v', 'V').Split('-')[0];
            if (Version.TryParse(clean, out var latest) && latest > current)
                return (true, $"发现新版本 {tagText}（当前 {CurrentVersion}）");

            return (true, $"已是最新版本（{tagText}，当前 {CurrentVersion}）");
        }
        catch (OperationCanceledException)
        {
            return (false, "检查已取消");
        }
        catch (Exception ex)
        {
            return (false, $"检查失败: {ex.Message}");
        }
    }
}
