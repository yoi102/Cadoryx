# B5：Bézier 与样条端点平滑相切

日期：2026-09-30。承接 [B4](B4_ENDPOINT_TANGENCY.md)，继续补 [G04](CAD_PRODUCT_GAPS.md) 的曲线端点 G1 关系，以及样条绘制首尾的端点身份捕捉。现有二次 Bézier 与 4–16 控制点夹持均匀三次 B 样条的几何限制保持有效。

## 范围和方向

- 直线、三点圆弧、二次 Bézier、上述三次样条之间可添加端点平滑相切；两条不同实体、至少一条曲线，且只共享一个端点 ID。允许线—Bézier、线—样条、弧—Bézier、弧—样条、Bézier—Bézier、Bézier—样条、样条—样条。构造几何也可参与。
- 继续使用 A/B/Joint 和 `EndpointTangentBranch.Opposed = 1`，端点内向单位切线叉积为零、点积为负。首尾反向或选择顺序不改变含义；尖点、垂直初始切向及不同 ID 的重合坐标不会自动改分支或合并。
- Bézier 的内向端点导数方向为端点到唯一控制点；夹持均匀三次样条为端点到相邻控制点。正的次数/首末结点跨度系数不影响归一化方向。本批要求端点到相邻控制点的距离大于 `1e-7 mm`，否则包括禁用关系在内均拒绝。
- 只约束端点切向，保留 G1；不要求两侧曲率、参数速度或半径一致，不提供 G2、任意参数处接触、非均匀结点、非夹持样条、样条编辑或自动圆角。
- 每次局部迭代保留输入曲线相对首尾弦的侧向。Bézier 不得变成共线退化；样条须继续沿弦严格单调，所有内部控制点在同侧且离开关闭弦。候选不满足这些规则、端点导数过小、圆弧绕向翻转或相切分支反向时拒绝。没有通过放宽区域规则换取相切。
- 尺寸/角度仍按求解公差判断，不代表浮点意义上的完全相等；近退化但仍超过既有阈值的合法曲线不自动归类为失败。数值非线性失败只表示本次局部求解未成功，不证明全局无解。

## 绘制与操作

开启端点捕捉，画直线或曲线，再从已有端点画 Bézier 或样条。样条现在对首次和末次点击复用捕捉到的端点 ID，内部控制点仍创建独立 ID。取消、切工具或滚轮缩放会清理当前未完成的样条手势；已有几何不因取消而删除。程序入口 [SketchDraft.AddSpline](../Cadoryx.Editor/SketchDraft.cs)增加可选 `existingStart` / `existingEnd`，支持两端复用，重复或失效身份原子拒绝。

任意顺序选择两条曲线，选“端点平滑相切”并添加；连接点由唯一共享端点 ID 决定。英/中/日提示已扩展曲线类型与退化规则。求解预览后检查局部自由度/冗余/残差，再确认；关闭或取消丢弃草稿和候选资产。修改尺寸沿草图→关联特征→后继重算链原子提交，文档撤销/重做包含全部结果。删除曲线、连接点或使曲线删除的控制点会清理关系，撤销恢复原身份。

## 代码与存储

| 入口 | 内容 |
|---|---|
| [SketchEndpointTangency](../Cadoryx.Db/SketchEndpointTangency.cs)、[Sketches](../Cadoryx.Db/Sketches.cs) | 支持有限曲线的端点身份、相邻控制点、退化距离及严格关系验证 |
| [SketchEquationSystem](../Cadoryx.Sketching/SketchEquationSystem.cs) | 相邻控制点与端点的解析方向导数、归一化 Jacobian、共享点贡献叠加、控制多边形和侧向检查 |
| [ManagedSketchConstraintSolver](../Cadoryx.Sketching/ManagedSketchConstraintSolver.cs) | 每条相切一个角度方程，预算/局部诊断，版本 `cadoryx.sketch.lm-svd.4` |
| [SketchDraft](../Cadoryx.Editor/SketchDraft.cs)、[SketchCanvas](../Cadoryx.wpf/Controls/SketchCanvas.cs) | 样条首尾 ID 复用、指针捕捉、未完成手势清理 |
| [SketchSections](../Cadoryx.IO/SketchSections.cs)、[SectionMigrations](../Cadoryx.IO/SectionMigrations.cs) | sketches v10、独立 v9→v10 语义迁移 |

存储复用 `endpoint-tangent-curves` 的三个目标和严格为 1 的分支数值，没有追加键。新增必需能力 `cadoryx.sketch-parametric-tangency.1`，以 sketches v10 表明目标类型扩大；document v20/features v22 不变。v9→v10 保留旧几何、ID、修订和圆弧相切关系；v9 中夹带指向 Bézier/样条的新相切语义（含禁用关系）拒绝。v8→v9 保持独立，仍拒绝旧 v8 夹带任何端点相切。新语义不能靠伪装节版本绕过迁移。

## 验证

[新增专项](../Cadoryx.Tests/SketchParametricTangencyTests.cs)共 29 个用例，与 B4、样条及混合曲线联合专项 **52/52** 通过：[TRX](../artifacts/b5-tests/b5-focused-final.trx)。覆盖：

- Bézier 两端、4/5/16 点样条首尾反向及 1,000,000 mm 平移；源尺寸带动端点切向。
- 弧/Bézier/样条相互组合、首尾反向、独立三点圆与单侧 de Boor 样本核对方向。
- 对全部变量列以中心差分核对解析 Jacobian，包含共享点与不参与端点切向的内部控制点。
- 控制多边形合法性、退化导数及禁用关系、尖点、局部冲突/预算/冗余、样条两端 ID 复用与删除/撤销。
- 真实原生混合轮廓关联拉伸：Bézier **80→92 mm³**、样条 **81→96 mm³**，包含预览取消、确认、冲突原子拒绝、精确撤销/重做和保存重开。
- 关系任意顺序选择、v9 旧几何及圆弧相切迁移、新语义伪装旧版（含禁用）拒绝。

锁定还原和 Release 全解决方案构建通过，零警告/错误；全量 **722/722** 通过，零跳过。完整发布版桌面 PASS：[结果](../artifacts/smoke-20260930-224624/result.txt)，包含原生视口、资源/装配绑定、导入导出、文档切换、旧草图与关联拉伸；绑定日志为空，关闭后资产数为零。

B5 三语言桌面专项 PASS：[结果](../artifacts/b5-accepted-sketch-smoke/result.txt)。生产 MetroWindow 和 SendInput 实际绘制直线/Bézier/样条，验证样条首尾都复用 ID、实际添加按钮、求解、撤销/重做、启用/禁用、合法控制多边形和预览关闭取消。[英文](../artifacts/b5-accepted-sketch-smoke/editor-parametric-tangency-en-US.png)、[中文](../artifacts/b5-accepted-sketch-smoke/editor-parametric-tangency-zh-CN.png)、[日文](../artifacts/b5-accepted-sketch-smoke/editor-parametric-tangency-ja-JP.png)截图均已逐张检查，提示、目标与按钮可读。绑定日志为空，关闭后资产数为零。

首次联合脚本 `verify.ps1 -B5SketchSmoke -B4SketchSmoke -B3SketchSmoke -SketchSmoke` 完成还原、构建、722 项测试、发布和完整桌面；随后历史草图入口触及 45 秒超时，未写最终 result，后续 B3/B4/B5 未执行，整条首次命令不能记为通过。B5 专项使用同一发布版独立执行通过；[B1/B2 历史草图](../artifacts/b5-history-regression-smoke/result.txt)、[B3 闭环偏移/中点/对称](../artifacts/b5-b3-regression-smoke/result.txt)、[B4 圆弧端点相切](../artifacts/b5-b4-regression-smoke/result.txt)也在相同发布版独立通过，绑定日志均为空，关闭后资产数为零。发布目录为 `artifacts/publish/20260930-224624`。

独立历史回归使用 60 秒上限；最后将脚本中该入口的超时余量从 45 调为 60 秒，其余断言不变，未修改产品几何或求解代码。该调整不证明首次超时原因已经完全确定。更新后的脚本通过语法检查，没有再重跑整条联合命令；本批验收依据为完整桌面加四个同版独立草图入口。文档链接与 `git diff --check` 均通过。

复现入口：`powershell -File scripts/verify.ps1 -B5SketchSmoke -B4SketchSmoke -B3SketchSmoke -SketchSmoke`；B5 独立发布版入口为 `Cadoryx.wpf.exe --b5-sketch-smoke "<证据目录>"`，使用脚本中的干净 PATH/OCCT 环境变量。

下一批建议补指定半径的解析草图圆角，先限定两条共享端点直线，并明确裁剪长度、半径塌陷、受影响约束清理及引用失效规则。非共享点接触、任意曲线相切、G2、多环偏移和实体拓扑边投影仍缺。物理键鼠、IME、混合 DPI/RDP、长时性能、安装器与公开发布仍需独立证据。
