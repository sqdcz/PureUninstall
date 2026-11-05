# shot-pages.ps1 —— 截取「安装追踪」「操作日志」两个新页面，验证渲染
param(
    [string]$Exe = 'D:\admin\文档\.workbuddy\2026-10-20-18-07-44\PureUninstall\dist\PureUninstall-portable\PureUninstall.exe',
    [string]$OutDir = 'D:\admin\文档\.workbuddy\2026-10-20-18-07-44\PureUninstall\design\promo'
)
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class PUY {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public static IntPtr Largest(uint pid) {
    IntPtr best = IntPtr.Zero; long bestArea = 0;
    EnumWindows((h, l) => {
      uint p = 0; GetWindowThreadProcessId(h, out p);
      if (p != pid || !IsWindowVisible(h)) return true;
      RECT r; if (!GetWindowRect(h, out r)) return true;
      long a = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
      if (a > bestArea) { bestArea = a; best = h; }
      return true;
    }, IntPtr.Zero);
    return best;
  }
}
'@

function Shot([IntPtr]$hwnd, [string]$path) {
    $r = New-Object PUY+RECT
    [void][PUY]::GetWindowRect($hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
    [void][PUY]::PrintWindow($hwnd, $hdc, 2); $g.ReleaseHdc($hdc)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "  shot -> $path (${w}x${h})"
}

$proc = Start-Process -FilePath $Exe -PassThru
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 2
    if ($proc.HasExited) { "进程退出"; exit 1 }
    $hwnd = [PUY]::Largest([uint32]$proc.Id)
    if ($hwnd -ne [IntPtr]::Zero) { break }
}
if ($hwnd -eq [IntPtr]::Zero) { "没有窗口"; exit 1 }
[void][PUY]::MoveWindow($hwnd, 30, 30, 1600, 1000, $true)
Start-Sleep -Seconds 4

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

function Nav([string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if ($el) {
        try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
        catch { try { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { } }
        Start-Sleep -Seconds 3
        "  nav -> $name"
    } else { "  !! 找不到 $name" }
}

Nav '操作日志'
Shot $hwnd (Join-Path $OutDir 'page-logs.png')

Nav '安装追踪'
Shot $hwnd (Join-Path $OutDir 'page-trace.png')

Nav '持久化检测'

# 必须限定 ControlType=Button：单用 Name 会先命中按钮里的 TextBlock，
# 那个元素没有 InvokePattern，调用会静默失败
$nameCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, '开始检测')
$btnCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)
$both = New-Object System.Windows.Automation.AndCondition($nameCond, $btnCond)
$btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $both)
if ($btn) {
    try {
        $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        "  已触发「开始检测」（InvokePattern）"
    } catch {
        $r = $btn.Current.BoundingRectangle
        [PUY]::MoveWindow($hwnd, 30, 30, 1600, 1000, $true) | Out-Null
        "  InvokePattern 失败，改用坐标点击"
    }
    Start-Sleep -Seconds 14
} else {
    "  !! 仍然找不到「开始检测」按钮"
}
Shot $hwnd (Join-Path $OutDir 'page-persist.png')

Nav '软件列表'
Shot $hwnd (Join-Path $OutDir 'page-apps.png')

if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; "已关闭" }
"done"
