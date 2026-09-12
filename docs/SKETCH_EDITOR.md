# M4-S2：草图编辑与关联特征

本文保留 M4-S2 的实现与当时验收记录。M4-T1 已将文件扩展为七节，最新格式、测试与鼠标验收状态见 [拓扑引用基础](TOPOLOGY_REFERENCES.md)和 [ROADMAP](ROADMAP.md)。

实施日期：2026-09-12。在 M4-S1 求解基础上，增加可操作的二维草图编辑窗口，以及草图 → 拉伸/旋转 → 后继特征的原子重算。当前范围是固定零件平面、点/直线/矩形/圆、已有 13 种约束、单个闭合直线轮廓；圆弧、切线、角度尺寸、曲线区域和孔洞另行扩展。

## 如何使用

1. 打开“草图”页签，点击“新建草图”。目标零件沿用建模面板的选择；空文档会随草图确认一起创建零件，取消不会留下空零件。
2. 在 `mah:MetroWindow` 编辑器中选择 XY、XZ 或 YZ 平面及原点，输入名称。坐标、长度和半径使用 mm；平面属于零件坐标，不是某条装配实例路径的世界坐标。
3. 点工具单击创建；直线、矩形、圆使用两次单击。捕捉开启时优先复用附近已有点身份，其次捕捉网格。直线共享端点后，不必再添加重合约束。
4. 矩形自动添加固定角点、水平/垂直和 X/Y 尺寸；圆自动添加半径约束。选择模式单击或 Ctrl 多选，拖动点后求解预览；受固定/尺寸约束的点可能回到允许的位置。
5. 约束列表可选中、改值、启用/禁用或删除。通过几何列表或画布选择正确类型和数量的目标，再添加所需约束。点坐标、圆半径和约束数值可点击“应用”，也会在下一次预览时接纳尚未应用的输入；输入变化立即使上次预览失效。
6. 点击“求解并预览”，检查自由度、冗余或冲突目标。草图有后继特征时，主视口同时显示候选实体。确认一次提交全部变更；取消、Esc 或窗口关闭均放弃草稿及候选资产。
7. 在模型树选择草图，点击“草图拉伸”或“草图旋转”，在建模面板选择来源草图/闭环、距离或角度，预览并确认。多余的点、圆及独立开放线仍保留在草图中；只有明确选择的直线闭环作为该特征的输入。
8. 再次从模型树选择草图并编辑尺寸，预览会重建依赖的拉伸/旋转及后继布尔；文档撤销/重做恢复整次提交的精确结果。

中键平移、滚轮缩放；Ctrl+Z/Y 操作本窗口的草稿历史。Enter 触发预览；Esc 先取消未完成的绘制/拖动，再关闭窗口。草稿历史与文档提交历史独立，均不序列化到文件。

既有草图的平面在本阶段保持不变；编辑窗口显示其原点及已有方向。共享零件的草图属于零件定义，确认后影响所有使用该零件的实例。编辑窗口为 owner 模态窗口，打开期间主工作区不能切换文档；外部命令导致文档代际改变时，当前编辑器明确失效，必须重新打开。

## 代码职责

| 文件 / 类型 | 职责 |
|---|---|
| Db/Sketches.cs | 草图稳定身份及不可变 Revision |
| Db/SketchProfileReference.cs | SketchId、Revision、有序线 ID；引用解析、所属零件及缓存一致性校验 |
| Db/SketchLoops.cs | 按共享端点邻接关系发现无分叉的闭环，不猜测分支 |
| Editor/SketchDraft.cs | 纯托管几何修改、实体删除的约束清理、局部草稿历史 |
| Commands/SketchCommands.cs | 候选求解、过期修订保护、按需创建零件、依赖重算及被引用删除保护 |
| Commands/RecomputeCommand.cs | FeatureRecompute 统一计算多个变化根及其后继闭包，保留输出身份/属性 |
| ViewModels/SketchEditorViewModel.cs | 输入/选择、诊断、预览、确认/取消、代际/关闭与资产租约 |
| ViewModels/CadDocumentViewModel.cs | 建模来源选择、冻结/关联语义、主视口候选场景 |
| WPF/Controls/SketchCanvas.cs | DIP 坐标绘制、命中、捕捉、鼠标捕获、缩放与平移 |
| WPF/Views/SketchEditorWindow | 共用 MetroWindow 风格、三语言布局、控件绑定与快捷键 |

Sketching 求解器和 OCCT 包版本沿用 M4-S1，不增加绘图库或 native 约束求解依赖。WPF 组合根注册 `ISketchEditorHost`；ViewModels 不依赖 Window、鼠标句柄或 OCCT 对象。

## 关联、版本与事务

```mermaid
flowchart LR
    Input[绘制与尺寸输入] --> Draft[隔离草稿与局部历史]
    Draft --> Solve[13 种约束求解]
    Solve --> Candidate[新草图修订]
    Candidate --> Roots[引用该草图的拉伸或旋转]
    Roots --> Closure[拓扑顺序重算后继闭包]
    Closure --> Preview[二维诊断与三维预览]
    Preview --> Confirm[检查文档代际并一次提交]
    Confirm --> History[精确撤销及保存恢复]
```

`CadSketch.Revision` 是非空 Guid；成功 Upsert 生成新修订，Undo/Redo 恢复快照里的旧/新修订。旧修订的编辑候选不能覆盖已经更新的草图。

`FeatureDefinition.SketchSource` 可为空。空值表示原有冻结轮廓；非空值持有 SketchId、Revision 和有序线身份。关联特征仍保留计算时的完整 Profile/Placement 和精确 Result，便于直接读取几何；领域验证要求这些缓存与所引用的草图修订、所属零件、线身份及平面一致。

更新草图先求解隔离候选，再找出引用它的全部特征，并扩展所有后继。每个特征按依赖顺序只计算一次；共享后继不会重复重建。上游的新结果传入后继布尔/变换，输出 BodyId、FeatureId、图层、材质、名称及空/非空恢复属性保持原有规则。

内核失败、闭环失效、锁定图层、取消、过期结果或关闭不会部分提交。候选结果在预览期间持有资产；确认复用候选快照，文档撤销/重做不再调用求解器或 OCCT。删除仍被特征引用的草图会拒绝；本阶段不隐式删除后继或把关联特征转换为冻结特征。

未成功求解的文档原始草图仍可读取和编辑；求解诊断不是持久权威状态。DOF 仍是成功解附近的局部数值诊断，仿射冲突和非线性未收敛保持 [M4-S1 边界](SKETCH_FOUNDATION.md)。

## 文件协议与旧文件

容器和资产目录版本仍为 1，applicationVersion 为 0.4.0；新增必需能力 `cadoryx.sketch-association.1`。

| 节 | 当前 schemaVersion |
|---|---|
| document | 3 |
| structure | 3 |
| features | 4 |
| presentation | 2 |
| geometry | 1 |
| sketches | 2 |

六节均为 MessagePack。sketches v2 为 PackSketch 追加 Key(8) Revision；features v4 为 PackFeatureV3 的已存在键之后追加 Key(9) SketchSource。引用 DTO 使用 Key(0) Sketch、Key(1) Revision、Key(2) Lines。类型名称 PackFeatureV3 保留以兼容已有代码入口，写出契约已升级为 v4；没有重排旧键。

迁移分别注册 `sketch-revisions`（sketches v1→v2）和 `sketch-feature-references`（features v3→v4）。旧草图以自身 SketchId.Value 作为可重复的初始修订；旧特征的关联为空，不能根据相似轮廓自动制造依赖。迁移会拒绝旧版本标签中夹带的新修订/关联字段。更早的 JSON、四节、五节文件继续按原有流水线迁移。

新增固定 `m4s1-sketches.cadoryx`，在本阶段改写存储之前从 `artifacts/smoke-20260912-173843/sketches.cadoryx` 冻结。原四个旧文件字节全部保留，来源和校验值见 [固定样本](../Cadoryx.Tests/Fixtures/Storage/README.md)。设置读取仍只消费 document；正式文档读取不重新求解，且会拒绝断链、错误类型、过期修订及不匹配的缓存。

## 验收

```powershell
./scripts/verify.ps1 -PublishSmoke -WindowSmoke -SketchSmoke -RecoverySmoke
```

当前 178 项自动化通过，0 失败/跳过；比 M4-S1 新增 21 项：6 项关联与旧文件、8 项草稿/编辑会话、7 项损坏协议。覆盖关联拉伸/旋转、共享后继只算一次、后继失败和锁定保护、草稿撤销/取消、迟到求解、待应用数值、原子创建零件、失效引用与旧文件。

`--sketch-editor-smoke` 从主窗口真实命令打开 owner MetroWindow，经 Win32 鼠标消息进入 WPF 输入链绘制点/线/矩形/圆、拖动点；经实际控件绑定修改尺寸，核对三语言窗口、主视口预览、关闭取消、关联拉伸与后继布尔、冲突禁用确认、精确撤销/重做、保存重开、三格式导出和关闭资产归零。

进程恢复验收在生产 30 秒快照计时器后强制终止并重启；新增关联草图及尺寸变更，逐项核对草图修订、特征引用/配方/结果和原文件 hash。最终路径见 [ROADMAP](ROADMAP.md)。单机本地显示、短时窗口测试不替代混合 DPI/RDP、长期运行或新 Windows 机器验收。

## 仍待扩展

- 圆弧、样条、切线、角度尺寸、参考尺寸和表达式；当前圆支持编辑/约束/保存，尚不支持作为曲线特征区域。
- 孔洞/嵌套区域、面附着基准、已有草图平面交互变更，以及在三维视口内直接叠加编辑。
- 线/圆整体拖动、框选、拖动期间连续求解、多分支选择、大型稀疏草图和局部历史容量策略。
- M4-T 的持久拓扑引用、歧义诊断和重选；通过相应门禁后再接入圆角/倒角。
