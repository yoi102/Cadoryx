# B6：指定半径的两直线草图圆角

更新：2026-09-30。承接 [B5](B5_PARAMETRIC_ENDPOINT_TANGENCY.md)。本批是草图解析编辑，不是三维实体边圆角。本页保留 B6 历史范围；持续半径及显式关联轮廓转换现由 [B7 实施](B7_SKETCH_FINISHING.md)补齐。

## 操作与范围

在草图窗口选择“草图圆角”，点击两条直线（再次点击可取消该线选择），输入毫米半径，点击“应用圆角”。选择也可来自已有实体列表。两线必须共用唯一端点 ID，且构造状态一致；相同坐标但不同 ID 不算连接。操作生成夹角内的小圆弧，裁短两条有限直线，再走已有求解预览、确认或取消流程。

半径是**创建时的几何尺寸**，本批不添加持续驱动圆弧半径的尺寸约束。生成两条 B4/B5 的 `EndpointTangentConstraint`，维持共享端点的平滑 G1；后续自由编辑或求解可能改变半径。持续半径/等半径约束列入 B7，不能把本批称为完整参数化圆角特征。

## 几何与身份契约

- 从连接点指向两个远端的单位向量计算夹角 `theta`；裁剪距离 `r / tan(theta/2)`，圆心位于夹角平分线上，距离 `r / sin(theta/2)`。中点位于圆心朝原连接点的圆周方向，确定小圆弧分支。
- 保留两条线的 ID、方向、远端 ID 和坐标；新建两个切点、圆弧中点及圆弧 ID。切点同时被对应直线和圆弧使用，不按坐标猜测或合并其他点。
- 原连接点删除；如果它被第三条线、圆/弧、Bézier 或样条使用，则整次拒绝。不得把分叉连接偷偷拆开。
- 移除所有引用两条受裁线或原连接点的约束，包括禁用约束；其他约束保留。新增两条显式平滑分支端点相切约束。界面报告移除数量。
- 半径必须有限且大于 `1e-7 mm`；两条线的剩余长度及裁剪距离都必须大于 `1e-7 mm`。夹角距 0 或 pi 不超过 `1e-6 rad` 时拒绝；圆弧三点退化、半径恢复误差超过 `max(1e-7 mm, r*1e-8)` 时拒绝。
- 投影驱动线必须先明确解除投影。零长度、重复选择、两个共同端点、缺失实体、混合构造状态均拒绝。

`SketchDraft.Fillet` 通过单次 `Change` 验证整个候选，包括投影驱动规则；成功只压入一条草稿历史，失败不修改值或历史。草稿撤销/重做恢复原对象快照；文档确认继续使用已有代次检查和原子重算。

## 特征引用与存储

添加圆弧后，原多边形引用只含原线 ID，已不再闭合。预览或提交时既有引用解析会拒绝整个事务，保留原草图和实体，**不会自动向旧引用插入新圆弧或换一个轮廓**。B6 不提供在此事务内重选轮廓的专用入口；已有依赖需要先明确处理，圆角完成后重新选择混合闭环建立特征。B7 将补显式引用更新工作流。

新草图/无相关引用的草图可正常确认；圆角后的线—弧混合闭环可用现有入口建立关联拉伸，几何和原生 BRep 均保留圆弧。

本批使用既有点、线、三点圆弧和端点相切记录，不增加存储字段或新约束类型。document v20、features v22、sketches v10、现有能力和求解器版本不变；不为一个界面编辑操作虚增格式版本。保存记录裁短后身份及两条相切关系，不保存可重放的圆角操作/原半径关联。

## 实现入口

| 层 | 文件与职责 |
|---|---|
| 编辑 | [SketchFillet.cs](../Cadoryx.Editor/SketchFillet.cs)：解析构造、裁线、约束清理和输入拒绝；[SketchDraft.cs](../Cadoryx.Editor/SketchDraft.cs)：单条历史 |
| 界面 | [SketchEditorViewModel.cs](../Cadoryx.ViewModels/SketchEditorViewModel.cs)、[SketchCanvas.cs](../Cadoryx.wpf/Controls/SketchCanvas.cs)、[SketchEditorWindow.xaml](../Cadoryx.wpf/Views/SketchEditorWindow.xaml)：两线点击、半径和应用按钮 |
| 验证 | [SketchFilletTests.cs](../Cadoryx.Tests/SketchFilletTests.cs)：24 个用例；[SketchEditorSmokeRunner.cs](../Cadoryx.wpf/Diagnostics/SketchEditorSmokeRunner.cs)：三语言真实窗口输入 |
| 语言 | 英/中/日五个资源键，使用 `scripts/update-string-accessors.ps1` 生成访问器 |

## 本机验收

专项及端点相切/直线编辑回归 **47/47**，记录 `artifacts/b6-tests/b6-focused-final.trx`。新增 24 例覆盖四种线方向、锐角/钝角/负绕向及大坐标、半径和稳定性拒绝、身份/约束处理、投影解除、旧特征引用的整次拒绝、编辑器预览取消/确认、文档撤销/重做、保存重开。20×20 mm 方形切一个 R2 圆角并拉伸 3 mm，真实内核体积为 `(400 - (4 - pi)) * 3 mm³`。

首次专项编译暴露 .NET 10 的 `AddRange` 集合表达式重载歧义，改为明确数组类型。首次运行中 7 例因求解器重建坐标的约 `1e-15 mm` 舍入误差而未通过逐位相等断言；改为身份严格一致、坐标 `1e-7 mm` 容差后专项通过，未放宽产品几何契约。

运行 `./scripts/verify.ps1 -B6SketchSmoke` 的完整链通过：锁定还原、全解决方案 Release 零警告/错误、全量 **746/746**（零跳过）、发布、完整桌面和 B6 三语言窗口冒烟均 PASS。与 B4/B5 的历史失败不同，本批该联合命令本次整条通过。

| 验证 | 证据 |
|---|---|
| 发布构建 | `artifacts/publish/20260930-231236` |
| 完整桌面 | `artifacts/smoke-20260930-231236/result.txt` |
| B6 界面 | `artifacts/b6-sketch-smoke-20260930-231236/result.txt`、`progress.log`；真实窗口注入指针绘制矩形/选两条线、半径输入/应用按钮、过大半径拒绝、撤销重做和预览关闭取消 |
| 英/中/日截图 | 上述 B6 目录的 `editor-fillet-{en-US,zh-CN,ja-JP}.png`，逐张视觉检查通过；不同语言顶部换行不遮挡半径/应用入口，圆角和提示可见 |
| B1/B2 同版独立回归 | `artifacts/b6-history-regression-smoke/result.txt` PASS |
| B3 同版独立回归 | `artifacts/b6-b3-regression-smoke/result.txt` PASS |
| B4 同版独立回归 | `artifacts/b6-b4-regression-smoke/result.txt` PASS |
| B5 同版独立回归 | `artifacts/b6-b5-regression-smoke/result.txt` PASS |

六份桌面/界面绑定日志均为空，各流程关闭后资产数 0。独立回归按上述顺序串行运行，使用同一发布目录、净化 PATH/OCCT 环境和 60 秒进程上限。资源访问器同步、PowerShell 语法和 `git -c core.safecrlf=false diff --check` 均通过。未提交、推送或制作/发布新的安装包。

## 剩余边界

尚未支持圆弧/曲线圆角、无共享端点两线、任意支撑线外延圆角、多角批处理、倒角、持续半径驱动、操作关联重放或旧轮廓原子转换。B6 不负责修复整张草图所有自交；选择混合轮廓创建特征时仍由既有轮廓验证拒绝非法环。

本机原生测试和注入指针窗口冒烟不能代替实体键鼠、跨 IME、混合 DPI/RDP、硬件性能和发行安装包门禁；M14–M17 的未验收项保持独立。
