# test-trace.ps1 —— 自动跑一遍「安装追踪」完整流程，验证快照/比对/界面是否稳定
# 只读系统、只写自己的记录目录，不删除任何东西
param(
    [string]$Exe = 'D:\admin\文档\.workbuddy\2026-10-20-18-07-44\PureUninstall\dist\PureUninstall-portable\PureUninstall.exe',
    [string]$OutDir = 'D:\admin\文档\.workbuddy\2026-10-20-18-07-44\PureUninstall\design\promo',
    [int]$SnapshotTimeoutSec = 300
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class PUX {
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
    $r = New-Object PUX+RECT
    [void][PUX]::GetWindowRect($hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -lt 100 -or $h -lt 100) { "  shot skipped (bad rect)"; return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][PUX]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "  shot -> $path"
}

function Find-El($scope, [string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Click-El($el, [string]$label) {
    if (-not $el) { "  !! 找不到元素: $label"; return $false }
    try {
        $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 600
        "  click -> $label"
        return $true
    } catch {
        try {
            $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Milliseconds 600
            "  select -> $label"
            return $true
        } catch { "  !! 点击失败: $label"; return $false }
    }
}

$TraceRoot = Join-Path $env:LOCALAPPDATA 'PureUninstall\Traces'
"=== 0. 清理旧测试记录 ==="
if (Test-Path $TraceRoot) {
    Get-ChildItem $TraceRoot -Directory | Where-Object { $_.Name -ne '' } | ForEach-Object {
        $sj = Join-Path $_.FullName 'session.json'
        if (Test-Path $sj) {
            $j = Get-Content $sj -Raw | ConvertFrom-Json
            if ($j.Name -like '测试追踪*') { Remove-Item $_.FullName -Recurse -Force; "  删除旧记录 $($_.Name)" }
        }
    }
}

"=== 1. 启动 ==="
$proc = Start-Process -FilePath $Exe -PassThru
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Seconds 2
    if ($proc.HasExited) { "  !! 进程提前退出 code=$($proc.ExitCode)"; exit 1 }
    $hwnd = [PUX]::Largest([uint32]$proc.Id)
    if ($hwnd -ne [IntPtr]::Zero) { "  窗口出现 ($((($i+1)*2))s)"; break }
}
if ($hwnd -eq [IntPtr]::Zero) { "  !! 没有窗口"; exit 1 }
[void][PUX]::MoveWindow($hwnd, 30, 30, 1600, 1000, $true)
Start-Sleep -Seconds 3

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
"  UIA 窗口: $($win.Current.Name)"

"=== 2. 打开「安装追踪」页 ==="
Click-El (Find-El $win '安装追踪') '安装追踪导航' | Out-Null
Start-Sleep -Seconds 3
Shot $hwnd (Join-Path $OutDir 'test-trace-page.png')

"=== 3. 开始追踪 ==="
Click-El (Find-El $win '开始追踪') '开始追踪' | Out-Null
Start-Sleep -Seconds 2

$edit = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)))
if ($edit) {
    try {
        $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('测试追踪 A')
        "  输入名称: 测试追踪 A"
    } catch { "  !! 输入失败" }
} else { "  !! 没找到输入框" }
Start-Sleep -Milliseconds 500

Click-El (Find-El $win '开始') '弹框-开始' | Out-Null

"=== 4. 等待装前快照完成（最多 $SnapshotTimeoutSec 秒）==="
$snapFile = $null
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt $SnapshotTimeoutSec) {
    Start-Sleep -Seconds 3
    $f = Get-ChildItem $TraceRoot -Recurse -Filter 'before.snap' -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($f -and $f.Length -gt 1000) { $snapFile = $f; break }
}
$sw.Stop()
if ($snapFile) {
    $lines = (Get-Content $snapFile.FullName | Measure-Object -Line).Lines
    "  装前快照 OK：$([math]::Round($snapFile.Length/1KB)) KB / $lines 行 / 耗时 $([math]::Round($sw.Elapsed.TotalSeconds))s"
} else {
    "  !! 装前快照超时未生成"
}
Start-Sleep -Seconds 2
Shot $hwnd (Join-Path $OutDir 'test-trace-active.png')

"=== 5. 结束追踪（比对）==="
Click-El (Find-El $win '结束追踪') '结束追踪' | Out-Null
$sw2 = [Diagnostics.Stopwatch]::StartNew()
$done = $false
while ($sw2.Elapsed.TotalSeconds -lt $SnapshotTimeoutSec) {
    Start-Sleep -Seconds 3
    $sj = Get-ChildItem $TraceRoot -Recurse -Filter 'session.json' -ErrorAction SilentlyContinue |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($sj) {
        try {
            $j = Get-Content $sj.FullName -Raw | ConvertFrom-Json
            if ($j.FinishedAt) { $done = $true; break }
        } catch { }
    }
}
$sw2.Stop()
if ($done) {
    $j = Get-Content $sj.FullName -Raw | ConvertFrom-Json
    "  追踪完成：装前 $($j.BeforeCount) 项 / 装后 $($j.AfterCount) 项 / 新增 $(($j.Added | Measure-Object).Count) 项 / 耗时 $([math]::Round($sw2.Elapsed.TotalSeconds))s"
} else {
    "  !! 追踪结束超时"
}
Start-Sleep -Seconds 2
Shot $hwnd (Join-Path $OutDir 'test-trace-result.png')

"=== 6. 打开「操作日志」页 ==="
Click-El (Find-El $win '操作日志') '操作日志导航' | Out-Null
Start-Sleep -Seconds 3
Shot $hwnd (Join-Path $OutDir 'test-logs-page.png')

"=== 7. 收尾 ==="
if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; "  已关闭（未删除任何系统内容）" }

$logDir = Join-Path $env:LOCALAPPDATA 'PureUninstall\Logs'
if (Test-Path $logDir) {
    $lf = Get-ChildItem $logDir -Filter '*.jsonl' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($lf) { "  日志文件: $($lf.Name) / $([math]::Round($lf.Length/1KB)) KB / $((Get-Content $lf.FullName | Measure-Object -Line).Lines) 条" }
}
"done"
