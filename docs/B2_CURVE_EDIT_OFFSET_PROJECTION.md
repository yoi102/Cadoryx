# B2：解析曲线编辑、偏移与草图关联投影

日期：2026-09-30。承接 [B1](B1_SKETCH_EDIT_AND_REVOLVE.md)；范围仍属于 [G03](CAD_PRODUCT_GAPS.md) 的有界扩展。

后续 [B3](B3_LOOP_OFFSET_AND_BASIC_RELATIONS.md)增加单闭环解析偏移及中点/对称约束，sketches 升到 v8。本文记录 B2 当时的范围、v7 格式和验证证据，最新状态见 [STATUS](STATUS.md)。

## 实施契约

- 修剪/延伸覆盖直线、圆和三点圆弧，与其它有限直线、圆及圆弧进行解析求交。圆不能延伸；修剪圆至少需要两个不同交点，结果为新圆弧 ID。圆弧分裂使用新 ID，单段修改保留原 ID；不移动其它几何的共享点，移除被编辑实体及废弃点的相关约束。切点、重合边界、交点点击及无解明确拒绝。
- 单实体偏移：直线以方向左侧为正距离；圆和圆弧以增大半径为正。产生独立的新几何，不复制来源约束；零距离、半径塌陷和不支持的曲线拒绝。多段轮廓连接、角部裁剪、Bézier/样条偏移另行扩展。
- 关联投影：来源为同一零件内另一草图的直线、圆或圆弧；直线正交投影到目标平面，圆/圆弧只支持平行平面（含反向法线），倾斜投影生成椭圆的情况拒绝。来源/目标稳定 ID、来源修订与目标锚定约束保存于 sketches v7；document v20 和 features v22 不变。v6→v7 不改旧数据且拒绝伪装的新投影字段。
- 投影几何由来源驱动，不能直接改坐标、修剪或延伸；可明确解除关联后编辑。偏移投影生成独立副本。来源修改按有向无环图刷新所有下游草图，再统一重算关联特征；缺失来源、类型变化、循环依赖、冲突和退化使整个命令原子失败。禁止猜测分裂后的来源。
- 单草图最多 256 条投影，依赖链最多 64 层；深度限制与字典遍历顺序无关。坐标/半径及来源缓存按 `1e-7 mm` 核对，来源 Revision 必须完全匹配。圆/弧平行面条件按变换后的法线验证。

## 操作入口

1. 打开草图窗口，选“修剪直线 / 圆弧 / 圆”，点击两个解析交点之间要删除的区间；圆修剪至少需要两个交点。选“延伸直线 / 圆弧”，点击靠近待延伸端的曲线，取最近有限边界。旧的直线专用工具仍保留。
2. 选“偏移直线 / 圆弧 / 圆”，在工具旁输入带符号的毫米距离，再点击目标。偏移继承来源的构造几何状态，不保持来源关联。直线反向会改变正偏移方向；圆/弧负值缩小半径。
3. 展开“草图关联投影”，选择“草图名 · 序号 · 类型”，点击投影按钮。列表仅包含同零件内其他已确认草图的直线/圆/弧；默认生成构造几何。如需参与轮廓建模，可选中目标并关闭构造状态。
4. 关联坐标/半径和其锚定约束不能改写或禁用。选择一个投影实体，点击“解除投影关联”，保留几何并移除其驱动锚定；草稿撤销可恢复关联。输入被拒绝时先纠正数值，再重新预览。
5. 求解并预览后才能确认；取消或关闭放弃整次草稿。修改来源草图时，候选预览包含所有下游投影草图与实体，确认及文档撤销/重做覆盖整条重算链。多个来源可以投影到同一个目标草图。

## 实现与存储

| 层 | 入口与职责 |
|---|---|
| 解析几何 | [SketchAnalyticalCurve](../Cadoryx.Db/SketchAnalyticalCurve.cs)：直线—直线、直线—圆和圆—圆解析求交，有限圆弧过滤与周期参数 |
| 草稿编辑 | [SketchCurveEditing](../Cadoryx.Editor/SketchCurveEditing.cs)、[SketchDraft](../Cadoryx.Editor/SketchDraft.cs)：区间替换、稳定 ID/共享点规则、独立偏移、投影与解除、草稿历史 |
| 来源校验 | [SketchProjections](../Cadoryx.Db/SketchProjections.cs)：锚定、平面变换、修订/类型/所有权校验、依赖顺序和驱动编辑保护 |
| 文档命令 | [SketchCommands](../Cadoryx.Commands/SketchCommands.cs)：编辑草图报告保持原身份，按依赖顺序刷新/求解，聚合特征重算和删除引用保护 |
| 界面 | [SketchEditorViewModel](../Cadoryx.ViewModels/SketchEditorViewModel.cs)、[SketchCanvas](../Cadoryx.wpf/Controls/SketchCanvas.cs)、[SketchEditorWindow](../Cadoryx.wpf/Views/SketchEditorWindow.xaml)：实际点击、带符号距离、来源选择、解除关联及三语言资源 |

sketches v7 的 Key(12) 保存投影列表；每条记录 Key(0–4) 依次保存 SourceSketch、SourceEntity、SourceRevision、TargetEntity 和 Anchors。必需能力为 `cadoryx.sketch-projection.1`。旧 v6 缺字段正常迁移，v6 夹带非 null 投影字段（含空列表）拒绝；既有 v5→v6 样条迁移仍是独立步骤。独立零件复制重映射内部来源草图 ID，防止副本继续引用原零件。详细版本入口见 [命令与存储](COMMANDS_AND_STORAGE.md)。

## 验收

已核对解析交点、弧方向和圆周期区间、端点身份/约束、偏移两侧与半径塌陷、跨平面投影、来源重算链、循环/过期/分裂拒绝、预览确认取消、撤销/重做、保存重开和旧文件迁移。

- [解析曲线测试](../Cadoryx.Tests/AnalyticalCurveEditingTests.cs)覆盖直线—圆和圆—圆、顺/逆时针弧、正负偏移、失败原子性；修剪后的精确半圆验证原生拉伸与整球旋转体积，主弧验证原生 BRep。
- [投影测试](../Cadoryx.Tests/SketchProjectionTests.cs)覆盖平面变换、反向法线、倾斜直线、倾斜圆/退化拒绝、只读与解除、两级来源链到实体的预览/确认/撤销/重开、来源分裂和冲突、复制来源 ID 重映射，以及两种遍历顺序下的依赖深度限制。
- 前轮曲线/投影/直线专项 **22/22** 通过：[TRX](../artifacts/b2-tests/b2-focused.trx)。随后补充两例深度回归；`scripts/verify.ps1 -SketchSmoke` 的锁定还原及 Release 全解决方案构建通过（零警告/错误），全量 **655/655** 通过，无跳过。
- 该轮发布版完整桌面冒烟 PASS：[结果](../artifacts/smoke-20260930-203905/result.txt)，绑定日志 0 字节。草图首轮因脚本只 RaiseEvent 而未实际调用按钮命令失败；改为原生 SendInput 点击后通过，再修正截图脚本的界面语言切换并重取三语言证据。后续修改仅涉及冒烟诊断代码，重新发布编译及该专项通过。
- 最终草图专项 PASS：[结果](../artifacts/b2-culture-final-sketch-smoke/result.txt)、[观察记录](../artifacts/b2-culture-final-sketch-smoke/observations.json)。覆盖实际圆修剪、圆弧偏移、投影按钮点击、驱动半径修改拒绝、解除/撤销、预览关闭取消，以及既有草图/实体链、保存重开和 STEP/IGES/STL 导出；绑定日志 0 字节，关闭后资产数 0。
- [中文](../artifacts/b2-culture-final-sketch-smoke/editor-curve-offset-projection-zh-CN.png)、[英文](../artifacts/b2-culture-final-sketch-smoke/editor-curve-offset-projection-en-US.png)、[日文](../artifacts/b2-culture-final-sketch-smoke/editor-curve-offset-projection-ja-JP.png)截图已逐张检查，偏移输入、来源列表、投影/解除按钮、提示及确认入口完整可见。自动输入和本机原生内核验证不能代替物理设备验收。

仍未包含实体拓扑边投影、椭圆投影、任意曲线编辑/偏移、完整偏移闭环及新的对称/曲率约束；物理键鼠/IME 和性能设备门禁仍独立保留。

下一批优先定义闭合直线/圆弧轮廓偏移的拐角连接、自交拒绝和多环结果规则，再扩展 G04 基本约束。实体边投影另行明确拓扑身份、来源历史和曲线转换；不按遍历序号猜测。当前改动保留在工作区，未创建新安装器、签名或公开发布。
