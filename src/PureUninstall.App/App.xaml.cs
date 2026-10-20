using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace PureUninstall.App;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);

        // 主窗口都没建起来（多半是程序文件缺失/资源加载失败），
        // 这时吞掉异常只会得到一个"双击没反应"的进程，必须明确报错并退出。
        if (MainWindow is null)
        {
            ShowFatal(e.Exception);
            return;
        }

        try { e.Handled = true; } catch { /* ignore */ }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            MainWindow = new MainWindow();
            MainWindow.Activate();
        }
        catch (Exception ex)
        {
            LogException(ex);
            ShowFatal(ex);
        }
    }

    private static void LogException(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PureUninstall");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // logging is best-effort
        }
    }

    private static void ShowFatal(Exception ex)
    {
        var message =
            "纯净卸载 启动失败。\n\n" +
            "常见原因：压缩包未完整解压（缺少 .xbf / .pri 等程序文件），" +
            "或程序所在目录没有写入权限。\n\n" +
            "请重新完整解压后，直接运行文件夹里的 PureUninstall.exe。\n\n" +
            $"错误详情：{ex.GetType().Name}: {ex.Message}";

        try
        {
            MessageBoxW(IntPtr.Zero, message, "纯净卸载 PureUninstall", 0x00000010 /* MB_ICONERROR */);
        }
        catch
        {
            // 连 MessageBox 都弹不出来就算了
        }

        Environment.Exit(1);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
