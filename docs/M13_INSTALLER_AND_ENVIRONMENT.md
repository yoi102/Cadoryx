# M13：可安装交付包与环境门禁

## 范围与结果

M13 建立可重复的 Windows x64 交付链：锁定 WiX 4.0.6、锁定 NuGet 还原、自包含 .NET/WPF 发布、单文件 MSI、逐文件 SHA-256 清单、安装目录启动冒烟，以及安装／卸载和用户数据保留检查。安装包包含配对的 OcctSharp 托管与原生程序集、原生依赖、MessagePack 和第三方许可证。构建仍需本地锁定的 OcctSharp NuGet 源；安装与运行不需要该源码仓库或另装 .NET Desktop Runtime。

安装器为当前用户创建 `%LOCALAPPDATA%\Programs\Cadoryx`、开始菜单快捷方式和 Windows 卸载登记。用户文档与设置不存放在程序目录，卸载只移除安装组件。WiX 组件 ID 由相对路径稳定生成，MSI 固定 UpgradeCode，后续相同产品的大版本升级有确定身份；不得改变这些标识来绕过升级测试。

当前构建的安装器**未签名、未公开发布**。本机静默安装通过，不等于全新机器、企业策略或 SmartScreen 验收。

## 复现方式

在仓库根目录运行：

```powershell
$out = "artifacts/installer-$(Get-Date -Format yyyyMMdd-HHmmss)"
pwsh -NoProfile -File scripts/build-installer.ps1 -Output $out
pwsh -NoProfile -File scripts/verify-installer.ps1 -Installer (Join-Path $out 'Cadoryx-0.4.12-win-x64.msi')
```

`build-installer.ps1` 会建立新证据目录，不覆盖既有结果；检查 `runtime-baseline.json` 的产品版本、锁定 OcctSharp 包与原生 DLL 哈希，并在 `manifest.json` 中记录每个发布文件的长度与 SHA-256，以及 MSI 哈希。`verify-installer.ps1` 在检测到已经安装 Cadoryx 时拒绝运行，防止替换用户现有安装；成功安装后核对全部文件，在安装目录以不含 `dotnet` 的 PATH 运行桌面冒烟，再卸载并检查快捷方式、卸载登记和已有用户数据。

## 2026-09-28 本机证据

最终证据为 `artifacts/m13-installer-final2`：自包含 MSI 96,112,546 字节，SHA-256 `5a7e99f650b4442ffc2214e8bee58f384318075c5f2cd2d8ef6fc22162f6b6ea`；实际哈希请以该目录 `manifest.json` 为准。共 579 个发布文件；`verification-script/result.txt` 为 PASS，安装后的 579 个文件哈希、开始菜单、卸载登记、桌面冒烟、空 WPF 绑定日志和卸载后程序文件归零均通过。另单独验证默认路径 `%LOCALAPPDATA%\Programs\Cadoryx` 可安装及卸载，日志为 `default-install.log` / `default-uninstall.log`。先前的构建尝试保留在其他证据目录，不作为最终安装器。

桌面冒烟经过原生视口、STEP 导入、MessagePack 保存、STEP/IGES/STL 导出、草图、预览/提交、撤销重做和三语言布局。本机具备开发环境；缩小 PATH 与自包含运行只证明安装目录的本机依赖边界。M12 的真实 88 MB STEP 双视口性能数据仍以 [M12](M12_INTERACTION_AND_PERFORMANCE.md)为准，安装器冒烟只使用受控小样本。

同轮源码的锁定还原、Release 构建（零警告/错误）、596/596 测试和 CLI 已通过。原 `scripts/verify.ps1 -PublishSmoke -WindowSmoke` 的桌面冒烟在 120 秒上限处超时，输出已有大部分检查但尚无最终结果；随后对**同一发布产物** `artifacts/publish/20260928-210543` 分别以 240 秒预算复跑，`artifacts/m13-final-smoke/result.txt` 和 `artifacts/m13-final-window/result.txt` 均为 PASS、绑定日志均为空。验证脚本的两处桌面超时已同步调为 240 秒。不能把首次超时记录称为通过，也不能将超时预算调整当成功能提速。

## 尚需外部环境取证

| 门禁 | 当前状态 | 所需证据 |
|---|---|---|
| 干净 Windows x64 | 未验收 | 无 SDK/本地 NuGet 的机器安装、首次启动、STEP/IGES/STL、保存/重开、卸载；记录版本、日志和依赖 |
| 混合 DPI / 双屏 | 未验收 | 物理 100%/150% 或其他不同比例显示器间浮动、停靠、视口拾取、弹窗与输入 |
| RDP 及多 GPU | 未验收 | 断开重连、图形驱动切换后的视口恢复与资源检查 |
| 数小时运行 | 未验收 | 固定大模型的内存、句柄、输入响应及原生资源趋势 |
| 签名与公开发布 | 未实施 | 受控证书签名、发行物哈希、外部下载与安装结果 |

这些属于交付环境验收，不能把本机 MSI 构建、短时冒烟或无 `dotnet` PATH 的测试写成通过。真实磁盘满、断电、网络盘和跨厂商交换仍按 [交付验收计划](M10_M11_DELIVERY_PLAN.md)单列。
