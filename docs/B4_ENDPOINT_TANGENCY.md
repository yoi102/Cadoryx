# B4：曲线端点平滑相切

后续 [B5](B5_PARAMETRIC_ENDPOINT_TANGENCY.md) 已将端点 G1 扩展到 Bézier/样条并补样条首尾 ID 捕捉；下文保留 B4 当时的范围与验收。

日期：2026-09-30。承接 [B3](B3_LOOP_OFFSET_AND_BASIC_RELATIONS.md)，补 [G04](CAD_PRODUCT_GAPS.md) 中直线—圆弧及圆弧—圆弧的端点连接关系。本批是有界 G1 相切，不代表完整曲线约束系统或整个三维 CAD 完成。

## 几何与身份契约

- 两条不同曲线，至少一条三点圆弧；另一条为直线或三点圆弧。必须共享且只共享一个端点 ID；该点保存为 Joint。不同 ID 的重合坐标、仅有重合方程、两个共享端点、圆弧中点、完整圆、两条直线、Bézier/样条均不接受。
- 端点朝曲线内部的单位切向叉积为零，点积必须为负，即平滑通过连接点。保存 `EndpointTangentBranch.Opposed = 1`，没有自动改成尖点分支。端点在 Start 或 End、曲线排列方向及选择顺序均不改变几何含义。
- 圆弧切向由三点确定的真实圆和原扫掠分支决定，包含大于 180° 的长圆弧。计算使用相对连接点的两条弦及解析三次多项式，不使用屏幕折线近似，也不通过圆心除法求导。
- 每次局部求解保留输入圆弧的绕向，并拒绝跨过共线退化、重复点或非有限值的迭代。输入的两条内向切向必须位于相对方向的半平面（点积小于零）；垂直或尖点方向先修改几何再预览，不由求解器猜测跳到另一根。
- 构造几何同样可作为关系目标；连接点可有其他曲线分支。本关系只约束选中的一对曲线，不自动封闭轮廓、焊接实体或创建圆角。
- G1 只要求切向连续，不要求曲率、半径或参数速度连续。两条圆弧可有不同圆心和半径；不增加 G2 曲率规则。

## 窗口操作与事务

在草图窗口打开捕捉，画两条共享端点的曲线。任意顺序选择直线和圆弧，或两条圆弧，在关系列表选择“端点平滑相切”并添加；不用额外选择连接点，唯一共享端点由 ID 确定。提示显示类型和方向要求。点击“求解并预览”，检查局部自由度、冗余和残差，确认后提交。

关系支持启用/禁用、删除、草稿撤销/重做。关闭或取消丢弃预览；确认沿草图→关联实体→后继特征原子重算。冲突、不收敛或失效引用不产生部分文档变更。非线性未收敛只报告局部失败，不能证明全局无解。

删除曲线/连接点会清理关系；修剪/延伸沿现有编辑规则移除受影响曲线的关系，撤销可恢复。不会把旧相切关系自动重绑到新分裂片段。投影锚定约束仍保留；相切与驱动坐标冲突时预览失败，不能反向修改来源。

## 求解与存储

| 代码入口 | 职责 |
|---|---|
| [Sketches](../Cadoryx.Db/Sketches.cs)、[SketchEndpointTangency](../Cadoryx.Db/SketchEndpointTangency.cs) | 类型、显式分支、严格共享端点身份与圆弧切向多项式 |
| [SketchEquationSystem](../Cadoryx.Sketching/SketchEquationSystem.cs) | 归一化角度残差、所有相关点的解析 Jacobian、共享点贡献相加、绕向与分支拒绝 |
| [ManagedSketchConstraintSolver](../Cadoryx.Sketching/ManagedSketchConstraintSolver.cs) | 每条关系一个角度方程，既有预算/局部自由度/冗余；版本 `cadoryx.sketch.lm-svd.3` |
| [SketchDraft](../Cadoryx.Editor/SketchDraft.cs)、[SketchEditorViewModel](../Cadoryx.ViewModels/SketchEditorViewModel.cs) | 任意顺序选择、三目标显示、清理及历史；英/中/日提示 |
| [SketchSections](../Cadoryx.IO/SketchSections.cs)、[SectionMigrations](../Cadoryx.IO/SectionMigrations.cs) | sketches v9 编码和独立 v8→v9 迁移 |

`endpoint-tangent-curves` 保存 A/B/Joint 三个 ID、一个分支数值（严格为 1）及 Enabled。新增必需能力 `cadoryx.sketch-endpoint-tangency.1`；document v20/features v22 不变。旧 v8 保持几何、关系和修订，夹带新关系（含禁用关系）拒绝。B3 的 v7→v8 保留独立，旧文件不能直接跨过对应迁移校验；未知分支、参数数量或连接点身份错误均拒绝。

## 验证

[专项测试](../Cadoryx.Tests/SketchEndpointTangencyTests.cs)共 16 个用例：

- 直线—圆弧所有端点排列、坐标平移 1,000,000 mm、来源尺寸变化；两圆弧四种端点排列和不同半径；长圆弧、尖点拒绝及禁用。
- 类型/共享端点/分支验证、删除与修剪清理、退化拒绝、方程预算、冗余和非线性冲突诊断。
- 用独立中心差分核对解析 Jacobian 的每一列，覆盖直线—圆弧、圆弧—圆弧及共享点；另用真实圆心/扫掠切向独立检查求解结果。
- 关联混合轮廓真实原生拉伸体积由 `40 + 8π` 改为 `25 + 42.25 asin(12/13)` mm³；验证预览、关闭取消、确认、冲突原子拒绝、撤销/重做及保存重开。
- 窗口 ViewModel 任意顺序选择、草稿历史、v8 迁移、新关系伪装旧版（含禁用）、非法分支/连接点/参数持久化拒绝。

与 B3 基本关系、旧线—圆相切及投影回归联合专项 **36/36** 通过：[TRX](../artifacts/b4-tests/b4-focused.trx)。锁定还原、Release 全解决方案构建通过，零警告/错误；全量 **693/693** 通过，零跳过。

B4 发布版桌面专项 PASS：[结果](../artifacts/b4-accepted-sketch-smoke/result.txt)。生产 MetroWindow、SendInput 鼠标绘制及实际添加按钮覆盖线—弧与弧—弧共享端点、关系绑定/求解、撤销/重做、启用/禁用、预览关闭取消。英/中/日 [英文截图](../artifacts/b4-accepted-sketch-smoke/editor-endpoint-tangency-en-US.png)、[中文截图](../artifacts/b4-accepted-sketch-smoke/editor-endpoint-tangency-zh-CN.png)、[日文截图](../artifacts/b4-accepted-sketch-smoke/editor-endpoint-tangency-ja-JP.png)均已逐张检查，关系目标、提示和按钮可读。绑定日志为空，关闭后资产数为零。

完整发布版桌面复测 PASS：[结果](../artifacts/b4-desktop-retry-smoke/result.txt)，覆盖原生视口、预览/提交、导入/导出、文档切换、资源与装配输入、旧草图/关联拉伸及关闭清理，绑定日志为空。首次联合 `verify.ps1 -B4SketchSmoke -B3SketchSmoke -SketchSmoke` 已完成还原、构建、693 项测试和发布，但 [完整桌面首次结果](../artifacts/smoke-20260930-221135/result.txt)在旧装配实例重命名检查处失败，后续三个草图入口未执行，整条命令不能记为通过。同一发布版独立复测通过；未修改装配产品代码或该检查，尚不能把一次通过当作首次失败的原因已解决。B4 专项另行独立执行并通过。

B1/B2 历史草图桌面回归 PASS：[结果](../artifacts/b4-history-regression-smoke/result.txt)；B3 闭环偏移/中点/对称桌面回归 PASS：[结果](../artifacts/b4-b3-regression-smoke/result.txt)。均使用上述同一发布目录 `artifacts/publish/20260930-221135`，绑定日志为空，关闭后资产数为零。没有为取得复测通过而修改产品几何或诊断代码。

复现入口：`powershell -File scripts/verify.ps1 -B4SketchSmoke -B3SketchSmoke -SketchSmoke`，语法检查通过。实际执行为首次联合脚本加同版独立完整桌面、B4、历史草图与 B3 四个入口；首次联合脚本整体结果仍为失败。B4 独立发布版入口为 `Cadoryx.wpf.exe --b4-sketch-smoke "<证据目录>"`，应使用脚本定义的干净 PATH/OCCT 环境变量。文档链接与 `git diff --check` 均通过。

剩余：Bézier/样条端点 G1、非共享端点接触、任意曲线相切、G2 曲率连续、自动圆角及实体拓扑边投影。下一批建议按现有共享端点与显式分支规则扩展 Bézier/样条端点 G1，先明确端点导数退化与已有受限曲线合法性。物理键鼠、IME、混合 DPI/RDP、长时性能、安装器及公开发布未因本批自动桌面输入而获得新证据。
