# 纯净卸载 PureUninstall

现代化 Windows 深度卸载工具 —— Geek Uninstaller 的重写版。卸载软件后自动清理**文件、注册表、计划任务、服务、启动项、防火墙规则、WMI 事件订阅**七类残留，支持批量卸载、删除前自动备份与一键还原。

- 界面：WinUI 3（Fluent 设计、深浅色主题、Mica 就绪）
- 架构：C# / .NET 8，`PureUninstall.Core`（纯逻辑库）+ `PureUninstall.App`（WinUI 3 界面）
- 平台：Windows 10 1809+ / x64，未打包模式自带 Windows App Runtime，**拷贝即用免安装**

## 功能

| 功能 | 说明 |
|---|---|
| 软件列表 | 枚举 Win32/MSI/注册表卸载项 + UWP 商店应用，显示图标、版本、厂商、大小、安装日期；搜索/过滤/排序 |
| 卸载 | 优先安静卸载（`QuietUninstallString`），失败回退常规卸载；等待整个进程树退出后再扫残留 |
| 七类残留扫描 | 文件目录 / 注册表（Uninstall 项、Software 键、App Paths、MUI Cache、Run 键）/ 计划任务 / 服务 / 启动项 / 防火墙规则 / WMI 订阅 |
| 残留清单 | 扫描结果逐项勾选，显示类型、大小、是否需要管理员 |
| 批量卸载 | 多选入队，顺序执行完整流程（卸载→扫描→备份→清理），实时进度与逐项结果 |
| 强制删除 | 跳过卸载程序，直接扫描并清理残留（对付卸不掉的顽固软件） |
| 备份还原 | 删除前自动备份（注册表导出 `.reg` + 文件快照 + manifest.json），可一键还原 |
| 安全闸 | 系统关键路径/关键服务黑名单；每一项删除前复检；崩溃自动写 `crash.log` |
| 按需提权 | 非管理员时顶部横幅提示，一键 UAC 重启（便携版和未来商店版通用） |
| 更新检查 | 检查 GitHub Releases 最新版本（地址可在设置里改） |

## 下载

| 版本 | 文件 | 说明 |
|---|---|---|
| 便携版 | `PureUninstall-0.1.2-x64-portable.zip`（约 82 MB） | 解压即用，**无需安装任何运行时**；整个文件夹拷到 U 盘也能跑 |
| 商店版 | `PureUninstall-0.1.2-x64-store.msix`（约 11 MB） | 微软商店安装包，运行时由商店分发 |

> ⚠️ v0.1.1 便携版有启动缺陷（发布目录漏拷 `.xbf` / `.pri`，表现为双击无窗口），**请勿使用**，直接用 v0.1.2。

## 使用

1. 解压便携版 zip（**必须完整解压**，只拷单个 exe 会启动失败）
2. 运行 `PureUninstall.exe`（建议右键「以管理员身份运行」，否则系统位置清理会失败）
3. 列表页选中软件 → 「卸载」/「强制删除」/「加入批量队列」

备份位于 `%LOCALAPPDATA%\PureUninstall\Backups\`，可在「备份还原」页还原或删除。
异常日志位于 `%LOCALAPPDATA%\PureUninstall\crash.log`。

## 从源码构建

需要 .NET 8 SDK（x64）：

```bash
# 便携版（自包含，零依赖）
dotnet publish src/PureUninstall.App/PureUninstall.App.csproj -c Release -r win-x64 --self-contained true -o dist/PureUninstall-portable

# 商店 MSIX（先把 store/Package.appxmanifest 拷到工程根目录，构建完移回）
dotnet build src/PureUninstall.App/PureUninstall.App.csproj -c Release -p:StoreBuild=true
```

> 便携版曾经踩过的坑：`dotnet publish` 默认不把编译后的 XAML（`.xbf`）和应用 PRI 拷进发布目录，
> 缺了它们启动即抛 `Cannot locate resource ... themeresources.xaml`、窗口不显示。
> 工程里的 `CopyXamlResourcesToPublish` 目标已修复，改发布流程后请确认产物含
> `PureUninstall.pri` 与 7 个 `.xbf`。

冒烟测试（不删任何东西，只枚举和扫描）：

```bash
dotnet run --project test/PureUninstall.Smoke            # 枚举全部软件
dotnet run --project test/PureUninstall.Smoke -- Arduino  # 扫描指定软件残留
dotnet run --project test/PureUninstall.Smoke -- --backup-test Arduino  # 备份/还原往返测试
```

## 目录结构

```
PureUninstall/
├── src/PureUninstall.Core/          # 核心库（无 UI 依赖）
│   ├── Models/                      # AppEntry / ResidualItem / 备份与批处理模型
│   └── Services/
│       ├── InstalledApps.cs         # 注册表枚举（Win32/MSI）
│       ├── UwpApps.cs               # UWP 枚举与卸载（PowerShell Appx）
│       ├── Uninstaller.cs           # 卸载执行 + 命令行解析
│       ├── ProcessTree.cs           # 进程树等待（Toolhelp32）
│       ├── BatchUninstaller.cs      # 批量流程编排
│       ├── ResidualScanner.cs       # 扫描编排
│       ├── BackupManager.cs         # 备份/还原（reg.exe + 文件快照）
│       ├── ResidualCleaner.cs       # 残留删除（带安全闸）
│       ├── MatchHelper.cs           # 名称匹配（短名仅精确匹配）
│       └── Scanners/                # 文件/注册表/计划任务/服务/启动项/防火墙/WMI
├── src/PureUninstall.App/           # WinUI 3 应用
│   ├── Pages/                       # 列表/卸载向导/批量/备份/设置
│   ├── ViewModels/                  # AppItemViewModel（图标+副标题）
│   ├── Helpers/IconExtractor.cs     # EXE 图标提取与缓存
│   └── Services/                    # 设置持久化/全局状态/更新检查
├── test/PureUninstall.Smoke/        # 控制台冒烟测试
├── design/icons/                    # 图标（draw_icon.py 可重新生成）
└── dist/                            # 便携版 zip
```

## 实现要点 / 已知限制

- **自带运行时**：`WindowsAppSDKSelfContained=true` + 自包含 .NET，无需安装任何运行时
- **单文件不支持**：WinUI 3 未打包应用官方不支持 `PublishSingleFile`（原生引导先于解压，必现 SxS 错误），故发布为文件夹便携版
- 计划任务/服务/防火墙/WMI 删除需要管理员权限
- UWP 卸载走 `Remove-AppxPackage`（PowerShell）
- 服务扫描用 WMI `Win32_Service`（避免手写 SCM 缓冲区 marshalling 的兼容性坑）
- 名称匹配保守策略：≥4 字符的词才做包含匹配，短名（如 QQ）仅精确匹配，避免误伤

## 发布计划

- **GitHub**：public 仓库 + Releases 挂便携版 zip（gh CLI 已登录）
- **微软商店**：需 MSIX 打包 + Partner Center 预留应用名拿 Publisher CN；商店版改用框架依赖（不自带运行时），提权用「按需 UAC」而非 requireAdministrator 清单
- 命名注意：公开名称不使用 "Geek"（CrystalIDEA 商标）

## 许可

MIT
