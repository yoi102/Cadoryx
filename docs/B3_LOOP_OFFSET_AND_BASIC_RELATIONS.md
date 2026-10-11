# B3：闭合轮廓偏移与中点/对称约束

日期：2026-09-30。承接 [B2](B2_CURVE_EDIT_OFFSET_PROJECTION.md)，推进 [G03/G04](CAD_PRODUCT_GAPS.md) 的下一段有界实现，不代表整个 B 批或三维 CAD 产品完成。

## 轮廓偏移契约

- 在“偏移闭合轮廓”工具中输入毫米距离，点击轮廓任一边。正值向外、负值向内，与来源实体方向及轮廓绕向无关；完整圆同样支持。B2 单实体偏移仍保留原先“有向直线左侧/圆半径增加”的定义。
- 来源必须是按共享端点 ID 连接的唯一、非构造、无分叉闭环；支持直线与三点圆弧。不同 ID 的重合点不自动合并。包含 Bézier/样条、开放链、分叉或不合法轮廓时拒绝。
- 按解析支持直线/圆求交连接尖角，选取距相邻偏移端点最近的唯一交点；平滑相切与同向共线连接保留连续连接。尖角长度限制为偏移绝对值的 20 倍；没有交点、距离并列、过长尖角明确拒绝。
- 圆弧保持精确圆心、解析半径和原扫掠方向；检查半径、弧长、线段方向及面积。零距离、塌陷、反转、整圆化、自交和自接触均拒绝，不生成部分结果。
- 本批仅生成一个简单闭环。内偏移穿过窄颈、需要分裂为多环、环消失或复杂角部需要其他连接策略时整次拒绝；没有自动分环、裁剪、自交修补、圆角/倒角连接或孔岛联动偏移。
- 生成独立副本，全部几何和顶点使用新 ID，邻边共享新顶点。来源几何、约束、特征及投影关联不变，不复制其驱动约束。新轮廓不是关联偏移特征，后续修改来源不会带动副本。

## 约束契约与操作

- **点在线段中点**：任意顺序选择一个点和一条直线，选该关系并添加。目标点不能是直线端点；其 X/Y 等于两端点均值，使用两个仿射方程。可用于已存在的圆弧/Bézier/样条端点 ID，但不增加曲线相切或曲率规则。
- **两点关于直线对称**：任意顺序选择两个点和一条对称轴直线。目标点需使用不同 ID，不能是轴端点；两点中点落在无限支撑轴上、两点连线垂直轴。轴方向反转不改变含义，轴和点都可参与求解。这是两个非线性方程，不是创建镜像几何的命令。
- 新关系没有数值尺寸，约束列表显示其类型及全部目标，支持启用/禁用、删除、草稿撤销/重做。删除任何目标会清理相应关系。画布可多选，几何列表可精确选择；提示说明所需目标。
- 坐标仍受 B2 投影驱动保护；新关系与驱动锚定冲突时预览失败，不能改写投影来源。
- 求解器提供局部自由度、冗余和逐约束残差；中点矛盾可按既有仿射冲突机制诊断。非线性对称未收敛不能宣称全局无解或唯一解。两个新增关系各占两个方程并计入预算，求解器版本为 `cadoryx.sketch.lm-svd.2`。

操作完必须求解并预览后确认。取消或关闭丢弃草稿；来源 ID 保持稳定的约束修改沿既有草图→实体→后继特征路径原子重算，文档撤销/重做覆盖整次提交。

## 代码与存储

| 入口 | 职责 |
|---|---|
| [SketchLoopOffset](../Cadoryx.Editor/SketchLoopOffset.cs) | 轮廓发现、绕向、解析偏移、角部交点、塌陷/自交校验与共享新顶点 |
| [Sketches](../Cadoryx.Db/Sketches.cs)、[SketchDraft](../Cadoryx.Editor/SketchDraft.cs) | 新关系类型、严格目标验证、删除清理和草稿历史 |
| [SketchEquationSystem](../Cadoryx.Sketching/SketchEquationSystem.cs) | 两个方程的归一化残差与解析 Jacobian；既有 LM/SVD 求解、局部自由度及预算 |
| [SketchMixedProfile](../Cadoryx.Db/SketchMixedProfile.cs) | 真实有限线/圆弧自交验证；修正精确相切处判别式舍入制造的假交点 |
| [SketchSections](../Cadoryx.IO/SketchSections.cs)、[SectionMigrations](../Cadoryx.IO/SectionMigrations.cs) | sketches v8、类型参数编码及显式 v7→v8 迁移 |
| [SketchEditorViewModel](../Cadoryx.ViewModels/SketchEditorViewModel.cs)、[SketchCanvas](../Cadoryx.wpf/Controls/SketchCanvas.cs) | 轮廓点击、带符号距离、任意顺序的类型选择、关系提示和预览 |

sketches v7→v8 复用现有约束记录的 Kind/Targets/Values/Enabled：`midpoint-point-line` 保存两个目标、零数值；`symmetric-points-line` 保存三个目标、零数值。新增必需能力 `cadoryx.sketch-basic-relations.1`。旧 v7 不改坐标和几何；夹带新关系（含禁用关系）拒绝。v6→v7 的投影迁移仍是独立步骤，document v20/features v22 不变。偏移副本仅保存现有点/直线/圆/圆弧，不新增偏移关联字段。

## 验证

- [轮廓偏移测试](../Cadoryx.Tests/SketchLoopOffsetTests.cs)：正/负距离及反向来源、矩形和精确半圆顶轮廓的原生实体体积、非相切直线—圆弧/圆弧—圆弧交点、凹角/共线、窄颈分裂拒绝、塌陷、分叉、过长尖角、不同闭环选择、圆半径，以及预览取消/确认、关联拉伸、撤销/重开。
- [基本关系测试](../Cadoryx.Tests/SketchBasicRelationTests.cs)：中点移动/禁用/冲突/预算、斜轴与反向轴、平移归一化、对称轴也参与求解、冗余、错误目标及删除清理；界面任意顺序选择、v7 迁移/伪装拒绝，以及对称/中点驱动三角形实体从 36→56 mm³ 的预览、确认、撤销和保存重开。
- 与既有混合曲线回归联合专项 **29/29** 通过：[TRX](../artifacts/b3-tests/b3-focused-final.trx)。锁定还原和 Release 全解决方案构建通过，**零警告/错误**；全量 **677/677** 通过，零跳过。
- 发布版完整桌面冒烟 PASS：[结果](../artifacts/smoke-20260930-211811/result.txt)，包含真实原生视口、导入/导出、文档切换、草图关联拉伸和资源释放；绑定日志为空。
- B3 独立桌面专项 PASS：[结果](../artifacts/b3-accepted-sketch-smoke/result.txt)。通过生产 MetroWindow 和 SendInput 鼠标输入验证闭环偏移、共享端点、塌陷拒绝、偏移撤销/重做、中点/对称按钮绑定与求解、关系撤销/重做、预览关闭取消；关闭后资产数为零，绑定日志为空。[英文](../artifacts/b3-accepted-sketch-smoke/editor-loop-offset-relations-en-US.png)、[中文](../artifacts/b3-accepted-sketch-smoke/editor-loop-offset-relations-zh-CN.png)、[日文](../artifacts/b3-accepted-sketch-smoke/editor-loop-offset-relations-ja-JP.png)截图均已逐张检查，控件、关系提示与底部操作可读。
- B1/B2 历史草图桌面回归 PASS：[结果](../artifacts/b3-history-regression-smoke/result.txt)，包含修剪/延伸、单实体偏移、投影驱动保护、关联实体及后继布尔重算、冲突拒绝、撤销/重做、保存重开和三格式导出；绑定日志为空，关闭后资产数为零。

首次 `scripts/verify.ps1 -SketchSmoke` 已完成还原、构建、677 项测试和完整桌面冒烟，但历史草图输入专项失败，不能把该次整条命令记为成功。随后修正冒烟坐标落在画布边界、移动/点击时序及硬编码圆坐标的问题，并将 B3 专项分为独立入口；最终两个草图专项分别通过。全量测试后仅修改诊断入口/脚本及文档，重新发布成功，没有再改产品几何和求解逻辑。

复现全链：`powershell -File scripts/verify.ps1 -B3SketchSmoke`（锁定还原→Release 构建→全量测试→发布→完整桌面→B3 专项）；历史回归另加 `-SketchSmoke`。新增开关已通过 PowerShell 语法检查；本次实际执行的是上面的原全链及重新发布后的两个独立可执行入口，未重跑新增开关整链。独立入口为 `Cadoryx.wpf.exe --b3-sketch-smoke "<证据目录>"` 和 `Cadoryx.wpf.exe --sketch-editor-smoke "<证据目录>"`，应使用发布目录并按 [verify.ps1](../scripts/verify.ps1) 清理 OCCT 环境变量与 PATH。

后续 [B4](B4_ENDPOINT_TANGENCY.md) 已补共享端点的直线—圆弧/圆弧—圆弧 G1 相切及显式分支。剩余：更多角部连接策略、多环/孔岛偏移、关联偏移、任意曲线、实体拓扑边投影、其他曲线接触及曲率连续约束；Bézier/样条端点 G1 已由 [B5](B5_PARAMETRIC_ENDPOINT_TANGENCY.md)补齐。实体键鼠/IME、混合 DPI/RDP 和性能硬件门禁继续保留，自动桌面输入不能替代这些实物证据。

B3 当时建议的下一批曲线端点相切已由 B4 保存曲线/连接点稳定 ID 和平滑分支，包含退化、冲突及编辑清理规则；更广的曲线范围见 B4 剩余工作。实体边投影保持独立身份/转换契约。
