# M1-Q：交换源样本与视口窗口验收

## 实施范围

本阶段完成当前桌面可执行的交换与窗口回归。真实混合 DPI、RDP 重连和长时间资源监测仍是独立门禁；M1 整体不因此标为完成。

## 固定样本与单位

样本及生成说明见 [Exchange fixtures](../Cadoryx.Tests/Fixtures/Exchange/README.md)。固定输入独立于 Cadoryx 的导出器，使用解析尺寸和手工确定的单位声明验证导入，避免仅靠同一套导入/导出逻辑互相验证。它们由 OCCT 生成基础语法，尚不代表外部 CAD 产品的互操作覆盖。

13 项新增回归覆盖：英寸/米 STEP/IGES 的毫米尺寸与面积；STEP 的实体体积；两层不同轴的旋转、共享零件和实例路径；红色整体与绿/蓝面样式；两种导出格式的世界边界和毫米坐标；保存重开、颜色覆盖/撤销、旧 MessagePack 外观数组兼容。

IGESCAF 当前写出的 Box 是六个面，重新读取为 Sheet，几何尺寸/面积保留，实体体积为 0。新增 `EXPORT.IGES_SURFACES` 明确反馈实体和共享装配语义可能丢失。不要对 Sheet 显示推测的实体质量；需要实体/共享装配时使用 STEP 或原生文件。

IGES 往往把整体颜色分配到每个面，而组标签没有 Color。导入器优先读取标签颜色/视觉材料；如果来源样式颜色一致，则将该颜色投影为整体颜色，避免属性/再次导出退回默认蓝灰色。混合面颜色仍通过源 XDE 显示，不冒充单一整体颜色。

## 源外观与显式颜色

`CadAppearance` 增加 `PreserveSourceStyles`，导入体设为 true。Scene 将是否保留源样式传给 Rendering.Occt；只有存在源 XDE、没有实例覆盖、没有 ByLayer 时使用该样式。显式体颜色、图层颜色和实例覆盖使用普通 Shape presentation，避免被源面样式反向覆盖。

高亮按发生变化的对象替换 presentation，取消选择重新从源 XDE 建立样式。不能调用 ClearAllSubshapeOverrides 后继续假设源面颜色存在。多选状态使用文档实例路径维护，Ctrl 切换、Shift 添加、Alt 移除均有真实拾取回归。

属性面板只修改名称、显隐、材料等时保留原外观；改变颜色/ByLayer 时形成显式外观。仅 Apply 同一颜色不会主动清掉源样式。预览显示色明确关闭源样式，避免出现“预览颜色未生效”。

MessagePack `PackAppearance` 只追加 Key(2)，已有 Key(0) ARGB 与 Key(1) ByLayer 不变。缺失 Key(2) 的旧数组、旧 JSON 默认 false，保持旧文件的显式颜色行为。新增字段属于可选显示语义，节版本仍为 v2。旧程序读取并重存可能丢掉这个新标志；新代码已验证新文件保存重开及旧两字段字节样本读取。

本阶段保留源面样式的**显示和 `.cadoryx` 持久化**。当前 STEP/IGES 导出仍只输出整体颜色，不转移源面样式/PMI，`EXPORT.METADATA` 持续反馈此限制；完整样式导出仍待实施。

## Win32 输入和生命周期

- Rendering.Occt 跟踪已按下按键；移动消息仅采用当前手势中有效的按键，捕获丢失后的迟到 release 不再发布选择变化。
- WM_CAPTURECHANGED、WM_CANCELMODE、WM_KILLFOCUS、Esc 和模态隐藏都清理手势。只有捕获属于当前 HWND 时才释放它；多个按钮按下时，最后一个释放前保持捕获。
- preview.26 的 Input.PointerReleased 会清空任意当前按键。以右键 release 取消可避免合成左键点击，但还需要 Cadoryx 自己清理按键掩码和迟到消息。
- `ViewportView` 在宿主 Destroying 事件捕获相机；现有文档 Detaching 立即释放 native，再释放会话资产的顺序保持不变。
- 浮动内容位于 AvalonDock 单独的 HwndSource，诊断从 PresentationSource 枚举查找视口；只遍历主 Window 的 WPF 树会漏掉它。
- 诊断进程的停靠布局与恢复数据均存到自己的输出目录，不读取或写回用户日常布局。

独立应用弹窗继续使用统一 `mah:MetroWindow`。AvalonDock 浮动停靠容器由库自己的窗口类型及主题管理，未替换其基类。

## 重复验证

```powershell
./scripts/verify.ps1 -PublishSmoke -WindowSmoke -RecoverySmoke
```

脚本锁定还原、Release 构建、全部测试，然后从独立发布目录以清理后的 PATH 运行。新增 `--window-smoke <输出目录> <样本目录>` 入口，执行：

1. 导入有面颜色的装配；保存原生文件；选中/取消选择；属性重命名；显式覆盖颜色/撤销；正反两视角保存原生截图并检查颜色像素。
2. 12 次打开原生文件、设置相机和实例选择、浮动、调整浮窗尺寸、重新停靠、关闭。核对相机 eye/target/up/scale、选择、宿主数及资产数。
3. 真正转移 Win32 Capture，分别验证左/中/右键及迟到消息；验证多键按住时不提前释放捕获；真实投影位置拾取检查 Ctrl/Shift/Alt。
4. 记录绑定日志、显示器数、DPI、远程会话标志、每轮进程句柄和私有内存。资源计数不经过强制 GC 才判定通过。

原生截图单独生成；WPF RenderTargetBitmap 无法包含 OpenGL HwndHost 内容。颜色检查阈值排除了仅坐标轴出现颜色的情况，并覆盖背面。

2026-09-12 完整脚本通过：91 项测试、Release 零警告/错误；发布目录 `artifacts/publish/20260912-133836`。常规、窗口、崩溃恢复三个 result.txt 均 PASS，四个绑定日志为空。窗口证据位于 `artifacts/window-smoke-20260912-133836`，常规与恢复分别位于 `artifacts/smoke-20260912-133836`、`artifacts/recovery-smoke-20260912-133836`。已查看原生颜色、浮动/重新停靠和外壳截图。

## 环境门禁

本机观测为单显示器、96 DPI、本地会话，不能覆盖跨 DPI 或远程重连。12 次循环属于短时回归，进程句柄/私有内存只作观测数据，不能据此宣布长期无泄漏。

后续专项仍需：100%/150%/200% 实际双显示器迁移与输入命中、RDP 断开/重连和恢复绘制、长时间打开大型装配与资源趋势、独立 CAD 厂商文件、全新 Windows/.NET 部署。M3-V 已在原窗口验收前加入三个固定旧文件的打开/升级/重开和源面颜色检查，最新证据见 [格式演进](FORMAT_EVOLUTION.md)；下一阶段转为 M4-S 草图基础，上述环境门禁继续独立保留。
