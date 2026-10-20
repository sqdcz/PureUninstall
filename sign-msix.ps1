# sign-msix.ps1 - 用与 Publisher CN 匹配的自签名证书给 MSIX 签名
#
# 前置：已安装 Windows SDK（含 signtool）：
#   winget install Microsoft.WindowsSDK.10.0.26100
#
# 用法（仓库根目录下）：
#   .\sign-msix.ps1
#   .\sign-msix.ps1 -MsixPath "dist\PureUninstall-0.1.1-x64-store.msix"

param(
    [string]$MsixPath = "src\PureUninstall.App\AppPackages\PureUninstall.App_0.1.1.0_x64_Test\PureUninstall.App_0.1.1.0_x64.msix",
    [string]$Publisher = "CN=EDFD9F3C-211F-42CA-B6B9-3A9E55BEEEBC"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $MsixPath)) {
    Write-Error "找不到 MSIX：$MsixPath`n先跑构建命令生成商店包"
    exit 1
}

# 1. locate signtool (x64; the Windows SDK installs it under bin\<ver>\x64,
#    the App Certification Kit also ships a copy)
$signtool = @(
    "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\signtool.exe"
) + @(Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "\\x64\\" } | Select-Object -ExpandProperty FullName) |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $signtool) {
    Write-Error "找不到 signtool.exe。请安装 Windows SDK 或 App Certification Kit：winget install Microsoft.WindowsSDK.10.0.26100 --source winget"
    exit 1
}
Write-Host "signtool: $($signtool.FullName)"

# 2. find or create a self-signed cert whose subject matches the Publisher CN
$cert = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $Publisher } |
    Select-Object -First 1
if (-not $cert) {
    Write-Host "创建自签名证书（Subject = $Publisher）…"
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature -CertStoreLocation Cert:\CurrentUser\My
    $pwd = ConvertTo-SecureString -String "pureuninstall" -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath "$env:USERPROFILE\PureUninstall.pfx" -Password $pwd | Out-Null
    Write-Host "PFX 已导出: $env:USERPROFILE\PureUninstall.pfx（密码 pureuninstall，请保管好）"
}
Write-Host "证书 thumbprint: $($cert.Thumbprint)"

# 3. sign + verify (use the PFX; /sha is not supported by the legacy cert-kit signtool)
$pfx = "$env:USERPROFILE\PureUninstall.pfx"
if (-not (Test-Path $pfx)) {
    Write-Error "找不到 PFX：$pfx（应在上一步自动导出）"
    exit 1
}
& $signtool sign /fd SHA256 /f $pfx /p pureuninstall $MsixPath
Write-Host ""
Write-Host "验证签名："
& $signtool verify /pa $MsixPath

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "签名完成：$MsixPath"
    Write-Host "现在可以把这个 .msix 上传到 Partner Center 提交审核了。"
}
