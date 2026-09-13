# 当前实现与开发入口

H2-B2：OcctSharp.BooleanHistoryModeling 提供同次布尔逐源关系，Cadoryx 经复制及 BRep 重载校验后发布证据，直接 Box → 布尔后继可诊断追踪。消费本地包 `8.0.1-preview.28.cadoryx.h2b2.2`，文件写出版本 0.4.5，history v2 不变；见 [布尔历史](BOOLEAN_HISTORY.md)。以下保留先前阶段背景。

H2-B1：TopologyHistory 支持多输入参数，BooleanCommand 原子接收内核证据，history 文件节升级为 v2，应用写出版本 0.4.4。真实 Boolean 历史仍依赖上游接口，当前生产解析不开放；协议、迁移和测试边界见 [MULTI_INPUT_HISTORY](MULTI_INPUT_HISTORY.md)。

M4-T1-H2-A 在同一存储写读链上增加 BrepDirectionRoundtrip 的限定单位轴舍入校验，写出 axis-v2 历史并保留旧 v1 可读；未通过的模型仍无映射。旋转重算／旧文件和真实布尔拆分能力边界见 [HISTORY_ROTATION](HISTORY_ROTATION.md)。八节协议及 NuGet 版本不变。

M4-T1-H1 新增 Db/TopologyHistory、Kernel.Occt/OcctLocalHistory 与 OcctHistoryResolver、Editor/TopologyHistoryInspection 和 IO/HistorySections。局部算法在同次调用捕获证据，严格校验 BRep 重载对应后才开放诊断解析；不通过者仍建模但不提供映射。八节持久化和后续门禁见 [算法历史](TOPOLOGY_HISTORY.md)。

更新：2026-09-13。文档描述代码现状；更长期的目标保留在 ROADMAP 和其他设计文档。

## 模块与主要入口

| 模块 | 核心文件 | 实际职责 |
|---|---|---|
| Db | Identity、Geometry、Model、Features、Sketches、DocumentSnapshot、OccurrencePlacement | 强类型 ID、双精度刚体变换、不可变业务图、草图实体/约束、循环/所有权验证、实例路径解析 |
| Kernel.Abstractions | Assets、KernelContracts、RecoveryContracts | 内容寻址资产、引用租约、几何/交换/存储与恢复契约 |
| Kernel.Occt | OcctGeometryKernel、OcctGeometryBridge | 固定 OcctSharp NuGet、全局串行原生任务、BRep/XDE 适配 |
| Sketching | ManagedSketchConstraintSolver、SketchEquationSystem、SketchSolveContracts | 13 种约束、解析导数/阻尼 SVD、局部 DOF、冗余/冲突与预算；纯托管 |
| Commands | DocumentCommands、RecomputeCommand、ResourceCommands、SketchCommands | 候选状态、归属与锁定检查、基础建模/布尔、属性与资源修改、位姿、依赖闭包重算、草图求解后提交 |
| Editor | DocumentSession、Workspace、Selection、SketchDraft、DocumentRecoveryService | Dispatcher 提交、代际检查、精确历史、草稿几何/历史、保存点、恢复协调和关闭排空 |
| IO | CadDocumentStorage、GeometrySections、SketchSections、HistorySections、SectionMigrations、AssetCatalog、CadRecoveryStore | 八节 MessagePack、旧 JSON/MessagePack 迁移、资产/草图/历史目录、完整性/限额、原子替换与恢复快照 |
| Rendering | Scene、CadCamera | 托管场景、实例路径、最终世界变换和可见性 |
| Rendering.Occt | OcctViewport | 独立 native 显示资产、候选场景替换、选取与照明 |
| ViewModels | MainWindowViewModel、CadDocumentViewModel、SketchEditorViewModel、RecoveryCenterViewModel、DocumentResourcesViewModel、InstancePlacementViewModel、Toolboxes | 多文档命令路由、草图输入/诊断/关联特征、归属/位姿/资源面板、预览、树/属性双向操作、恢复入口 |
| WPF | App、MainWindow、OcctViewportHost、ViewportView、SketchCanvas、SketchEditorWindow、CadFileDialogs、RecoveryHost、DialogService | DI、停靠、Win32 宿主、Metro 主窗口/草图编辑器、DialogHost 内容、快捷键与定时恢复保护 |
| Tests | Domain / Kernel / Storage / MessagePackStorage / Export / ToolSession / Recovery / DocumentResources / SketchSolver / SketchCommand / SketchStorage | 领域、并发/租约、真实几何、草图求解/命令/存储、资源/位姿、格式、预览与恢复回归 |

## 数据流与生命周期

1. 业务操作捕获不可变快照和输入资产租约；内核从 BRep 字节读取独立形状。
2. 原生结果写成新资产，形成 PreparedDocumentEdit，尚不改变文档。
3. 参数预览将候选完整场景显示为预览，支持删除/空布尔结果和装配实例放置。确认后复用已计算结果；取消释放暂存资产。
4. Session 在所属 Dispatcher 验证 Generation，再提交新 StateId 和历史。Undo/Redo 恢复精确快照，绝不重跑算法。
5. UI Viewer 独立加载显示形状，资源按几何修订复用；新场景准备成功后才替换旧展示。切换标签保留相机和显示模式。
6. 关闭先禁用新操作、处理保存/放弃/取消、等待正在准备的工具结束，通过 Detaching 立即销毁视口，再释放会话；不等待 WPF 的延迟 Unloaded。标签 ID 与持久 DocumentId 分离。

新文档默认未保存。IsDirty 比较 StateId 与保存点；Generation 只增不减。异步保存只标记捕获的状态已保存，期间的新编辑仍保持脏状态。历史当前上限为 50 条，尚未实现字节预算。

空结果特征仍保留输出 BodyId 与 BodyOutputMetadata；恢复成非空时使用同一个业务体身份、名称、外观、图层和材质。旧文件未包含该可选信息时使用明确缺省值。

## MessagePack 选择与兼容边界

采用 MessagePack NuGet：数值和记录数组紧凑、类型契约明确，C# 工具链成熟。M3-V 已测量当时五节实现的共享实例、25 MB BRep 和 128 MiB 扩展载荷；M4-S1 未重跑六节性能基准，也没有做与 JSON 的同条件性能比较，不能据此宣称固定倍数提升。详见 [格式演进与基准](FORMAT_EVOLUTION.md)。

- 清单保留 JSON，便于诊断；七个业务节使用数字键 DTO；精确 BRep/XDE 不进入反射对象图序列化。
- 当前 containerVersion=1、assetCatalogVersion=1、applicationVersion=0.4.5；features v5、document v5、structure v3、presentation/sketches/history v2、geometry/topology v1，均为 MessagePack。旧四节 v1/json → v2/messagepack → 提取共享 geometry 表；document v2 → v3 建立空 sketches 表；再迁移 sketches v1→v2 和 features v3→v4，明确草图修订与可选特征引用；document v3→v4 初始化 topology v1，document v4→v5 初始化 history v1，再经 history v1→v2 明确来源参数表。
- AssetFormat 记录媒体类型、编码/格式版本、内核及写出库版本，跟随不可变几何引用。当前新资产为 OCCT 8.0.1 / OcctSharp 8.0.1-preview.28.cadoryx.h2b2.2；旧文件缺失的生产者版本保持未知。实际依赖的原生资产先校验描述与文件头，再进入内核。
- Key 与枚举数字是文件协议，不能重新编号或复用。配方有显式白名单。新增字段需要缺省语义或迁移；未知未来必需功能拒绝加载。
- 未知可选节逐字节保留，同时文档只读；避免业务修改后悄悄写回不理解的引用。
- ZIP 默认总解压上限 1 GiB、单资产 256 MiB、单结构节 32 MiB、清单 8 MiB、20 万条目。迁移输出也检查每节和总容量。还检查路径、重复名、声明长度与 SHA-256；这些限额属于当前可配置策略。
- `ReadSettingsAsync` 不初始化 OCCT。当前存储仍在内存中解码资产，大型模型需要后续流式/按需读取。

## 草图编辑与关联特征

“草图”页签可新建/编辑草图、删除未被引用的草图，以及创建草图拉伸/旋转。MetroWindow 编辑器支持点/直线/矩形/圆、捕捉、点拖动、尺寸与 13 种约束、草稿历史、求解诊断和预览/确认/取消。固定零件平面和共享端点身份贯穿保存/恢复。

关联特征记录 SketchId、草图修订和有序直线 ID；修改草图会重算全部依赖特征及后继闭包，整体提交或失败回滚。原冻结轮廓保持原语义，关联特征的轮廓/平面输入受来源草图控制；当前只支持单个直线闭环，不支持圆/孔洞区域。详见 [草图基础](SKETCH_FOUNDATION.md)和[编辑器使用说明](SKETCH_EDITOR.md)。

## 导入与导出

导入支持 Cadoryx、STEP/STP、IGES/IGS。STEP/IGES 通过 XDE 投影定义、实例、放置、名称及整体颜色，并嵌入源 XDE 上下文。它不代表所有 PMI、图层或子面样式都已完整映射。

导出在独立任务上下文操作捕获的文档：STEP/STP、IGES/IGS 重建 XDE，STL 展开实例并离散。三者均支持可见对象过滤。STL 有线性偏差 mm、角度偏差和二进制/ASCII 选项；只写三角网格，没有内在单位、材质、装配和历史。

输出先写目标目录临时文件，原生写出成功且未取消后替换目标。无几何、错误、取消不得破坏旧文件。导出不会把当前 Cadoryx 文档标成已保存。原生长调用只能在支持的边界响应取消。

## 自动快照与恢复

每 30 秒尝试保护脏文档，在独立恢复目录保留最近两份完整快照。下次启动会显示可恢复文档，也可从“文件 → 文档恢复”进入。恢复以未保存副本打开，先在新会话写好快照再清理来源，不覆盖原文件。正常保存和关闭会协调清理自己的记录；最新快照损坏可回退。快照包含已提交模型及精确资产，不包含撤销栈或未确认预览。详见 [恢复实现](RECOVERY.md)。

## 使用与验证

主页可新建、打开、保存、另存为、导出与管理文档资源。右侧建模参数支持目标零件/图层/材料选择和长方体、圆柱、有限闭合多边形拉伸/旋转；预览后确认。模型树勾选支持多体布尔，差集以选择顺序中的第一个体为主体；当前只允许同一零件定义内运算。点击特征可编辑配方并重算后续依赖。属性面板可修改名称、颜色、显隐、图层和材料；选择实例节点可修改父装配局部坐标下的位姿。详见 [资源与实例](RESOURCES_AND_INSTANCES.md)。

左键选取，Ctrl 多选，中键平移，右键旋转，滚轮缩放；Ctrl+S/Z/Y 路由到活动文档，Esc 取消预览。原生宿主会在 DialogHost 打开期间隐藏，关闭后恢复；当前资源、恢复、设置及导出选项均为 DialogHost 内容，系统文件选择器使用 owner。新增独立应用窗口继续采用 `mah:MetroWindow`。

`scripts/verify.ps1 -PublishSmoke` 执行构建、测试、发布和真实窗口冒烟。`--smoke <输出目录>` 仅是开发验收入口，会自动构造测试文档并退出；输出 result.txt、bindings.log、原生视口截图、WPF 外壳截图和三种交换文件。WPF RenderTargetBitmap 不包含 HwndHost 的 OpenGL 内容，需同时检查 viewport.png；外壳截图的空白视口不表示原生渲染失败。

增加 `-RecoverySmoke` 会执行实际 30 秒定时快照、测试进程强制终止、重启恢复窗口、恢复副本另存为和关闭清理，使用隔离目录。该过程及三语言窗口截图见 [恢复验收](RECOVERY.md#可重复验证)。

增加 `-WindowSmoke` 执行 M1-Q 固定源颜色、拾取、捕获丢失以及 12 次浮动/重新停靠/关闭重开验证。`CadAppearance.PreserveSourceStyles` 贯穿源显示、属性编辑、预览与 MessagePack；IGES 一致的面颜色投影为整体颜色，导出报告明确曲面边界。详见 [交换与视口验收](EXCHANGE_AND_VIEWPORT.md)。所有诊断入口的布局与恢复目录现已隔离，不再依赖用户的已保存工具箱布局。

## 尚未完成

M3-V 在 `-WindowSmoke` 中新增三个固定旧文件的 MainWindow 打开、升级保存、关闭/重开和源面颜色验证。存储性能复现命令为 `./scripts/benchmark-storage.ps1`；原始 JSON 结果、内存解释和未覆盖范围见 [格式演进](FORMAT_EVOLUTION.md)。

完整装配编辑和共享子装配使独立、草图曲线区域/孔洞及完整约束集、通用拓扑命名、外部引用、多视口、大型模型性能预算仍未完成。M4-S2 完成固定平面与直线闭环的编辑/关联重算，局部数值 DOF 不等于全局解唯一。跨节/跨编码迁移框架和资产格式目录已实现，后续新增协议仍须注册具体迁移和固定兼容样本。文档内材料/图层管理已接通，外部材料库和纹理/PBR 材质仍未提供。

验证是在当前 Windows 机器和本地包基线上完成；独立发布目录使用已安装的 .NET 10 Desktop Runtime。尚未在全新 Windows 虚拟机、混合 DPI、多 GPU/远程桌面或长时间运行条件下验收，也未制作安装器或发布 NuGet。

## M4-T1 拓扑引用入口

新增 Db/TopologyReferences、Kernel.Abstractions/TopologyContracts、Kernel.Occt/OcctTopologyResolver、Commands/TopologyReferenceCommands、Editor/TopologyReferenceInspection 和 IO/TopologySections。支持 Box 的六面与十二边、两种修订策略及六种解析状态，当前通过命令/API 使用。发布冒烟自动生成 topology-result.json 和七节原生文件；面/边交互与局部建模留在 M4-T2。详细范围和用法见 [拓扑引用基础](TOPOLOGY_REFERENCES.md)。

## M4-T2 局部建模入口

Ribbon“局部建模”命令 → LocalFeatureViewModel → LocalFeatureWindow（MetroWindow + 独立 OcctViewportHost）。视口输出 BoxBoundary 语义，BoxTopology 共用于解析、高亮和建模；LocalFeatureCommand 创建带唯一 Box 上游的 LocalFeatureRecipe。FeatureRecompute 刷新依赖缓存，IO features v5 保存配方；旧 features v4 显式迁移。当前单边圆角/倒角、面/边引用重选已验证，连续编辑仍有门禁，见 [局部建模](LOCAL_FEATURES.md)。
