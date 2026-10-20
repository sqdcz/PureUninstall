# 微软应用商店上架指南（纯净卸载）

本项目已具备完整的 MSIX 打包管线，本文件是上架 Partner Center 的完整流程。
**应用身份已配置完成**（2026-10-20）：Identity `31096ADE.453098C59DDDA` / Publisher `CN=EDFD9F3C-211F-42CA-B6B9-3A9E55BEEEBC`，直接构建即可用于提交。

---

## 第 1 步：在 Partner Center 预留应用名（需要你操作）

1. 打开 <https://partner.microsoft.com/dashboard>，登录你的开发者账户（发布者：**衰气的船只**）
2. 左侧「应用和服务」→「新建应用」→「手动预留应用名称」
3. 名称建议：**纯净卸载**（英文名 PureUninstall 会被写进包标识，与显示名独立）
4. 预留成功后进入应用概览，找到 **「应用标识」/「标识名与发布者」**，记录：
   - **Identity Name（包标识名）**：形如 `PureUninstall` 或带后缀（如 `12345衰气的船只.PureUninstall`）
   - **Publisher（发布者 CN）**：形如 `CN=3C8B9F2A-1234-4E5F-9ABC-0123456789AB`
   
   > 这两个值必须一字不差地写进 `Package.appxmanifest`，否则认证直接失败。

## 第 2 步：应用标识（✅ 已完成）

`Package.appxmanifest` 已写入 Partner Center 的真实身份：

```xml
<Identity
    Name="31096ADE.453098C59DDDA"
    Publisher="CN=EDFD9F3C-211F-42CA-B6B9-3A9E55BEEEBC"
    Version="0.1.2.0"
    ProcessorArchitecture="x64" />
```

> 如果以后改了应用名/重新预留，用 `set-publisher.ps1 -Publisher "CN=..." -IdentityName "..."` 一键重写。

## 第 3 步：构建商店包

清单文件放在 `src/PureUninstall.App/store/Package.appxmanifest`，构建前拷到工程根目录，
构建完再移回，避免便携版构建时被 MSIX 工具链干扰：

```powershell
cd src\PureUninstall.App
Copy-Item store\Package.appxmanifest Package.appxmanifest -Force

# 便携版（GitHub Releases 用，自带运行时，零依赖）
dotnet publish src/PureUninstall.App -c Release -r win-x64 --self-contained true -o dist\PureUninstall-portable

# 商店 MSIX 包（依赖商店分发的 Windows App Runtime，体积小）
dotnet build src\PureUninstall.App -c Release --no-incremental -p:StoreBuild=true

Remove-Item Package.appxmanifest -Force
```

产物：`src/PureUninstall.App/AppPackages/PureUninstall.App_0.1.2.0_x64_Test/PureUninstall.App_0.1.2.0_x64.msix`

> ⚠️ 便携版坑位记录：`dotnet publish` 默认**不会**把编译后的 XAML（`.xbf`）和本应用 PRI
> 拷进发布目录，缺了它们启动时抛 `Cannot locate resource ... themeresources.xaml`，
> 表现就是"双击没反应、没有窗口"。工程里 `CopyXamlResourcesToPublish` 目标已修复，
> 改发布流程后请确认产物里有 `PureUninstall.pri` 和 7 个 `.xbf`。

## 第 4 步：签名（一键脚本）

```powershell
# 前置：winget install Microsoft.WindowsSDK.10.0.26100 --source winget
.\sign-msix.ps1
```

脚本会自动：定位 signtool → 创建/复用与 Publisher CN 匹配的自签名证书（PFX 导出到 `%USERPROFILE%\PureUninstall.pfx`，密码 `pureuninstall`）→ 签名（带时间戳，失败则退而不带）→ 验证。

不想本地签名也可以：**先拿未签名包直接上传**（`dist\PureUninstall-0.1.1-x64-store-unsigned.msix` 就是当前产物），Partner Center 接收时会用商店身份重签；若被拒再跑上面的脚本。

## 第 5 步：Partner Center 提交

1. 应用概览 →「应用包」→ 拖入 .msix（或 .msixupload）
2. 填写「商店一览」：
   - 显示名称：**纯净卸载**
   - 类别：`实用工具` → `系统工具/维护`
   - 说明/功能要点用 README 里的功能清单
   - 截图：至少 1 张 1366×768 或更大（可截图便携版界面）
   - 图标：用 `design/icons/pureuninstall_512.png`（512×512）
3. 「属性」→ 年龄分级选 3+ 或全年龄；隐私政策 URL **建议提供**（GitHub Pages 放一份即可，哪怕只写"本工具不收集任何数据"）
4. 「提交」

### allowElevation 受限能力的审批说明（提交时会要求填写理由）

> 本应用是 Windows 桌面卸载工具，核心功能是在卸载软件后清理系统级残留：Program Files 下的程序目录、HKLM 注册表项、Windows 服务与计划任务。这些操作需要管理员权限。应用采用「按需提权」设计：仅在用户主动点击卸载/清理并经 UAC 确认后才提升权限，不在启动时强制要求管理员，也不进行任何后台提权操作。allowElevation 受限能力用于实现该按需提权流程。

## 认证注意事项

- **不要**在商店版里要求 `requireAdministrator`（MSIX 不允许，本项目的按需提权设计已规避）
- 更新检查只用了 `internetClient`，已声明
- 包体积约 10.4 MB（运行时走商店框架依赖）
- 认证通常 1~3 个工作日；若因 allowElevation 被拒，把上面的说明文案填进审批理由框

## 本地验证商店包（可选）

```powershell
# 需要开发者模式已开启（设置 → 更新和安全 → 开发者选项）
Add-AppxPackage -Path "<msix路径>\PureUninstall.App_0.1.1.0_x64.msix"
```

---

**把 Partner Center 的 Publisher CN 发给我，我可以直接改好清单、重建、签名脚本一并备好。**
