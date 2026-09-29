# 消息通知与后台打开

桌面使用 NuGet `WpfNotifications 1.2.0`（版本和哈希记录在 `Cadoryx.wpf/packages.lock.json`）。现有消息面板仍保存消息历史；新增通知只负责即时反馈。

1.2.0 已公开发布至 [nuget.org](https://www.nuget.org/packages/WpfNotifications/1.2.0)，主包及调试符号均已提交。2026-09-27 00:52 JST 已确认公开索引与主包下载可用；Cadoryx 移除临时本地源后从 nuget.org 重新还原，包内容哈希与本地验证包一致，NuGet 仓库签名验证及 Release 构建通过。证据见 `artifacts/notifications-120-public-proof.json` 和 `notifications-120-public-build.log`。

## 用户设置

**文件 → 应用设置 → 常规 → 消息位置**：

- **当前应用窗口（默认）**：在 Cadoryx 主窗口内容区右下角显示，留出 16 DIP 间距。跟随主窗口移动、大小变化和最大化；最小化时隐藏，恢复窗口后显示仍在进行的通知。
- **Windows 桌面**：在主窗口所在显示器的工作区右下角显示，避开任务栏；移动同一显示器内的主窗口不会改变消息位置，主窗口最小化后通知仍可见。

点击“应用”或“确定”后立即生效；正在读取的通知会一起移动，保留原来的任务和取消按钮。选择保存在 `%APPDATA%/Cadoryx/application-settings.json` 的 `General.NotificationAnchor`。旧配置没有该字段、字段值未知、重置设置时均使用当前应用窗口模式。设置窗口未应用的修改不影响运行中的通知。

## 外观与交互

通知采用自定义 `Notification` 控件模板：360 DIP 宽、12 DIP 圆角、1.5 DIP 彩色边框、MaterialDesign 主题背景/文字和图标。普通信息蓝色、完成绿色、警告黄色、错误红色；支持深色和浅色主题。常规消息 5 秒关闭，错误 10 秒；悬停/键盘焦点暂停倒计时，用户可点击关闭按钮。

通知显示前先完成尺寸计算及右下角定位，再由 WpfNotifications 控件统一播放 340 ms 渐显和 24 DIP 上移；退出为 260 ms 渐隐和下移。动画由控件生命周期驱动，自定义模板也能使用；`PART_AnimationRoot` 仅使用透明度和渲染位移，不改变卡片或宿主 HWND 的尺寸。`DeferEntranceAnimation` 配合 `PlayEntranceAnimation()` 保证定位完成后才入场；中途关闭从当前画面继续退出，`CloseAsync()` 等待动画完成事件。系统关闭动画或通知禁用动画时直接显示最终状态；窗口恢复和切换位置不重复播放入场动画。

同一通知区域最多显示三条，运行中的永久通知不因普通消息溢出而被删除。完整文本保留在消息面板；导入细节诊断只写入面板，避免一次导入弹出多条技术消息。

## STEP 后台读取

STEP/IGES/STL 打开使用非模态通知：标题“正在后台打开”，内容显示文件名、忙碌动画和**取消**按钮。读取期间可以继续查看、编辑已有文档；同一时间只接收一个文件打开任务。读取成功且未取消才添加文档，正在执行的前台操作结束后再激活结果。

点击取消后按钮立即禁用，并显示取消状态；按通知上的 Esc 也会请求取消。通知等本次读取进程退出和资源清理后再关闭，随后提示已取消。普通消息的“关闭”不等于任务取消。退出应用时会取消并等待在途读取，再关闭文档及通知宿主。

OCCT 的 STEP `ReadFile` 没有协作取消入口；这里仍使用上一轮实现的独立读取进程来中止任务，不是让进程内的原生读取强行停止。`.cadoryx` 继续使用可配置取消按钮的 MaterialDesign 进度弹窗，接口见 [PROGRESS_DIALOG.md](PROGRESS_DIALOG.md)。

## 实现

- `ICadNotificationService` 隔离 ViewModel 与 WPF/通知 NuGet。`CadNotificationService` 订阅消息日志，管理通知句柄和后台任务反馈。
- `NotificationHostWindow` 使用 WpfNotifications 的 `NotificationArea` 自定义区域模式；小型透明 HWND 解决 OCCT `HwndHost` 的 WPF 遮挡问题。只占实际消息区域，不覆盖整个桌面；`WS_EX_NOACTIVATE`、`ShowActivated=false`、不显示任务栏图标。
- 应用模式为主窗口的 owned window；桌面模式解除 owner 并置顶。切换位置复用同一通知区域，不销毁/重建在途任务。屏幕位置按原生像素计算，窗口移动/大小/DPI 事件后重新定位。
- 使用 WpfNotifications 1.2.0 NuGet 包，未引用本地源码项目。退出时 `await NotificationManager.DisposeAsync()`，等待活动通知完成后再释放自定义宿主。

## 验证

### 1.2.0 动画统一验证（2026-09-27）

- Notifications 全解决方案 Release 构建通过，7 个库目标框架、两个示例，零警告/错误；net10/net8/net48 各 **60/60** 回归通过，共 180 项执行。新增测试覆盖自定义模板、默认模板、延迟显示、中途关闭、卸载、禁用/零时长和冻结变换。
- 从打包的 1.2.0 创建干净 WPF 消费项目，公开动画 API 与异步释放 API 编译通过。源码构建、干净消费者和 Cadoryx 使用的 `Notifications.dll` SHA256 均为 `FF0484BB2DF62F8C2B34BE707A23DFB9EC46A80FBEF2775E550FDAFF6E102308`。
- Cadoryx Release 发布及桌面冒烟通过，日志 `artifacts/notifications-120-desktop.log`，证据 `artifacts/smoke-20260927-004524`。绑定日志 0 字节；12 个入场采样记录连续透明度与位移，窗口尺寸和右下角稳定；已检查入场中间帧与完成帧。
- 深浅主题、应用/桌面定位、最小化恢复、切换定位、通知取消及退出清理通过；82,907,458 字节合成 STEP 在原生解析中点击取消，到空闲 **279 ms**（含 260 ms 退出动画），源文件哈希不变。
- 此轮未重复全套 Cadoryx 几何单测；混合 DPI 多屏、RDP、读屏和长期压力未运行。本机采样不能代替所有设备上的视觉体验验收。

### 之前的验证记录

发布版桌面冒烟的 `NotificationSmokeRunner` 覆盖深浅主题、消息转发、圆角边框、关闭按钮、非激活窗口、两种定位模式、移动/最大化/最小化恢复、在途通知切换位置、重复取消、成功/失败清理。`ExchangeImportSmokeRunner` 覆盖真实原生 STEP 解析期间从通知按钮取消、文件不变、资产/进程/临时目录清理及关闭应用流程。

`NotificationSettingsTests` 覆盖旧配置默认、非法值归一化、编辑副本隔离、应用/序列化/复制及重置。真实混合 DPI 多显示器、RDP 和长时间通知压力仍需专项验收。

2026-09-27 验证记录：

- 发布构建无编译警告/错误，桌面和窗口冒烟通过；日志 `artifacts/notifications-anchor-verify.log`，最终发布与证据时间戳 `20260927-001756`。两份 WPF 绑定日志为 0 字节。
- `notification-result.txt` 覆盖两种定位的实际窗口行为；`notification-shutdown-result.txt` 确认退出中止在途读取，零文档/资产且任务目录已清理。
- 82,907,458 字节合成 STEP 解析中从通知取消，到空闲 **169 ms**（包括 150 ms 通知关闭动画）；原文件 SHA256 不变，随后 STEP/IGES/STL 仍能读取。这是本机合成负载结果，不是所有文件的延迟承诺。
- 消息接入后的原有测试 **521/521**，日志 `artifacts/notifications-tests.log`；随后位置设置新增定向测试 **2/2**，日志 `artifacts/notifications-settings-tests.log`。没有把两轮测试记为一次完整 523 项运行。
- 已检查通知深浅主题截图、`metro-settings.png`；三个语言资源各 523 个非空键，键集合一致；`git diff --check` 通过。

同日显示动效修正：最终 `artifacts/notifications-motion-final.log` 的 Release 发布和桌面冒烟通过，证据 `artifacts/smoke-20260927-002947`，WPF 绑定日志为空。`notification-motion-result.txt` 逐帧记录透明度由 0→1、位移由 6→0，八个入场采样中宿主尺寸保持 `360×194`、右下角定位不变；退出有中间透明度且尺寸不变，关闭动画设置时立即显示。两种定位/最小化恢复、STEP 实际解析取消及退出清理继续通过。此次只回归相关桌面流程，没有重新运行全套几何单元测试、独立窗口/草图/恢复专项。
