# set-publisher.ps1 - 把 Partner Center 的应用标识写进 Package.appxmanifest
#
# 用法（在仓库根目录 PureUninstall\ 下执行）：
#   .\set-publisher.ps1 -Publisher "CN=3C8B9F2A-1234-4E5F-9ABC-0123456789AB" -IdentityName "PureUninstall"
#
# Publisher       = Partner Center「应用标识」页的发布者（整串，含 CN=）
# IdentityName    = 同一页的程序包标识名（可能是 PureUninstall，也可能带前缀如 12345xxx.PureUninstall）

param(
    [Parameter(Mandatory = $true)]
    [string]$Publisher,

    [Parameter(Mandatory = $true)]
    [string]$IdentityName
)

$ErrorActionPreference = "Stop"
$manifest = Join-Path $PSScriptRoot "src\PureUninstall.App\Package.appxmanifest"

if (-not (Test-Path $manifest)) {
    Write-Error "找不到 $manifest`n请在 PureUninstall 仓库根目录运行本脚本"
    exit 1
}

if (-not $Publisher.StartsWith("CN=")) {
    Write-Warning "Publisher 通常以 CN= 开头，你填的是：$Publisher"
}

[xml]$doc = Get-Content $manifest -Encoding UTF8
$doc.Package.Identity.Publisher = $Publisher
$doc.Package.Identity.Name = $IdentityName
$doc.Save($manifest)

Write-Host "已更新 Package.appxmanifest："
Write-Host "  Identity Name = $IdentityName"
Write-Host "  Publisher     = $Publisher"
Write-Host ""
Write-Host "下一步构建商店包："
Write-Host '  dotnet build src\PureUninstall.App -c Release --no-incremental -p:WindowsPackageType=MSIX -p:WindowsAppSDKSelfContained=false -p:AppxPackageSigningEnabled=false -p:GenerateAppxPackageOnBuild=true'
