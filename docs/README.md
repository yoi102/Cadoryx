# Cadoryx

基于 WPF / .NET 10 和 OcctSharp 的桌面 3D CAD。窗口布局沿用 Direct2dCad 的 Ribbon、可停靠工具箱、多文档区域和状态栏；建模数据按三维零件、装配实例、特征和精确 BRep 重新设计。

想知道目前做到哪里、哪些仍未实现或尚未验收，请先看[当前进度与剩余工作](STATUS.md)。M0–M13 的限定功能已有对应本机证据；M14 性能目标仍未达成，M15 已推进样条和多来源 STEP 精确检查但尚未完成。新机器交付验收也尚未完成。

## 设计入口

| 文档 | 内容 |
|---|---|
| [整体架构](ARCHITECTURE.md) | 项目依赖、职责、窗口布局、会话和线程 |
| [数据结构](DATA_MODEL.md) | 文档、零件、装配、几何、特征、草图、拓扑引用 |
| [命令与存储](COMMANDS_AND_STORAGE.md) | 事务、撤销、异步提交、增量更新、`.cadoryx` 格式 |
| [命令行与 AI Agent](COMMAND_AND_AGENT.md) | 活动文档命令、建模语法、LM Studio/Codex 助手及停靠面板 |
| [OcctSharp 接入](OCCT_INTEGRATION.md) | 已核实的 API、包版本、所有权、视口限制 |
| [OcctSharp 能力核查](OCCT_CAPABILITY_AUDIT.md) | 当前 NuGet 是否足够、独立消费场景、62 DLL 核对、应用接入与后续迁移边界 |
| [历史诊断查询](HISTORY_QUERIES.md) | H2-C1 MetroWindow、源/目标查询、异步状态、九节文件迁移 |
| [跨特征边绑定](HISTORY_FEATURE_BINDINGS.md) | H2-C2 精确目标确认、圆角、过期传播、显式重选与十节文件协议 |
| [局部结果精确拓扑](EXACT_LOCAL_TOPOLOGY.md) | B2d1–d4 生成边/面拾取、结果倒角、精确绑定和冻结重选 |
| [视口鼠标建模](VIEWPORT_CONSTRUCTION.md) | XY 工作网格、显示/间距/吸附设置、基础形体鼠标尺寸与预览 |
| [实施路线与验收](ROADMAP.md) | 每个阶段的交付、验证及本轮完成边界 |
| [当前进度与剩余工作](STATUS.md) | M0–M15 结论、功能缺口、环境与发布门禁 |
| [自动测试与覆盖率](TEST_COVERAGE.md) | xUnit 覆盖率口径、按模块结果、复现脚本及补测重点 |
| [M10–M11 与交付验收规划](M10_M11_DELIVERY_PLAN.md) | 完整装配、二维工程图、安装器与环境的范围、数据结构和验收门槛 |
| [M10 完整装配](M10_ASSEMBLY.md) | 解析 BRep 基准、闭环求解、自由度/冗余、跨文档依赖与本机验收 |
| [M11 二维工程图](M11_TECHNICAL_DRAWINGS.md) | 图纸、关联投影、精确尺寸、PDF/打印和本机验收边界 |
| [M12 交互与性能](M12_INTERACTION_AND_PERFORMANCE.md) | STEP 导入分段与场景复测、实例拖动、Revolve 手柄和验收边界 |
| [M13 安装器与环境](M13_INSTALLER_AND_ENVIRONMENT.md) | 自包含 MSI、文件清单、安装/卸载复验与实机环境门禁 |
| [M14 大模型打开](M14_LARGE_MODEL_OPEN.md) | 用户点击打开的分段计时、视口批次优化、A/B 及未达目标 |
| [M15 精确建模与装配](M15_PRECISION_AND_ASSEMBLY.md) | 样条扩展、多来源 STEP 检查、无效拓扑定位及剩余能力 |
| [后续工作规划](NEXT_STAGES.md) | M14–M17 的优先顺序、剩余能力与逐阶段验收条件 |
| [异常退出恢复](RECOVERY.md) | 自动快照、恢复入口、进程锁、故障边界与验收 |
| [资源与实例](RESOURCES_AND_INSTANCES.md) | 建模归属、图层/材料、局部位姿、MetroWindow 风格 |
| [交换与视口验收](EXCHANGE_AND_VIEWPORT.md) | 固定单位/旋转/面颜色样本、鼠标捕获、浮动与环境门禁 |
| [文件进度与快速取消](PROGRESS_DIALOG.md) | 可选取消按钮、MaterialDesign 弹窗、STEP/IGES/STL 独立读取进程和清理 |
| [右下角消息通知](NOTIFICATIONS.md) | WpfNotifications 自定义圆角边框、后台打开取消、应用窗口/桌面定位设置 |
| [快捷圆盘菜单](RADIAL_MENU.md) | 反引号键按住显示、滚轮选页、八扇区逐槽自定义与取消行为 |
| [格式演进与存储基准](FORMAT_EVOLUTION.md) | 资产描述、跨节迁移、旧文件固定样本、读写时间与内存 |
| [草图模型与求解基础](SKETCH_FOUNDATION.md) | 13 种约束、局部自由度/冲突、命令与六节文件协议 |
| [草图编辑与关联特征](SKETCH_EDITOR.md) | MetroWindow 二维编辑、尺寸/约束、候选预览、依赖重算与新版协议 |
| [精确圆形草图区域](CIRCULAR_SKETCH_PROFILES.md) | M4-S3a–f 单圆关联拉伸、编辑、迁移与恢复边界 |
| [精确圆孔拉伸](CIRCULAR_SKETCH_HOLES.md) | M4-S3g–l 明确选择的完整圆孔、关联重算与文件协议 |
| [草图切线、角度与换源](SKETCH_TANGENCY_AND_REBIND.md) | M4-S3m–r 圆孔多选、已有拉伸换源、切线和角度尺寸、sketches v3 |
| [多边形草图通孔](POLYGON_SKETCH_HOLES.md) | M4-S3s–x 非圆形闭合直线孔、精确拉伸、关联编辑、features v13 与恢复 |
| [三点圆弧草图区域](ARC_SEGMENT_SKETCH_PROFILES.md) | M4-S3y–ad 圆弧＋隐式弦、精确拉伸、关联重算、sketches v4/features v14 与恢复 |
| [直线圆弧混合闭环](MIXED_CURVE_SKETCH_PROFILES.md) | M4-S3ae–aj 一段圆弧与直线成环、精确关联拉伸、features v15 与恢复 |
| [曲线区域三阶段扩展](EXPANDED_MIXED_CURVE_PROFILES.md) | 多圆弧闭环、混合外边界的常规孔、精确混合曲线孔与 features v16 |
| [二次 Bézier 与单层岛屿](BEZIER_AND_SINGLE_LEVEL_ISLANDS.md) | Bézier 精确关联拉伸、孔内岛屿复合实体、features v18/sketches v5 |
| [三次 B 样条与文件故障](CUBIC_SPLINE_AND_FILE_FAULTS.md) | 受限三次样条独立区域、features v19/sketches v6、M3 故障矩阵 |
| [M9 草图区域与自由工作平面](M9_SKETCH_REGIONS.md) | 混合曲线、样条孔、递归孔岛、文档自由平面与鼠标半径手柄；features v20/document v18 |
| [M5 装配实例与独立化](M5_ASSEMBLY_OCCURRENCES.md) | 指定路径的插入、替换、删除、重挂与共享子装配隔离 |
| [M5 零件与定义维护](M5_PARTS_AND_MAINTENANCE.md) | 逐实例零件独立化、未用定义清理和编辑范围 |
| [M5 装配关系](M5_ASSEMBLY_RELATIONS.md) | 固定实例、点对重合/距离、失效诊断、文档保存和属性面板 |
| [M5 局部锚点与轴关系](M5_AXIS_RELATIONS.md) | 可编辑局部点、平行轴/同轴单关系调整及 document v14 |
| [M5 外部零件与关系图收尾](M5_COMPLETION.md) | 冻结零件链接、角度/平面基准、保守批量调整与剩余能力边界 |
| [M6 大型模型基础](M6_SCALE_FOUNDATIONS.md) | 几何复用与规模信息、按展开加载模型树、自适应显示网格 |
| [M6 功能收尾与验收](M6_COMPLETION.md) | 关联截面、工程标注、按需 ZIP、双视口真实模型基准与环境边界 |
| [M7 工程交付](M7_ENGINEERING_DELIVERY.md) | BOM、HTML 报告、校验 ZIP、桌面/CLI 入口和联合验收 |
| [M8 大模型交互](M8_LARGE_MODEL_INTERACTION.md) | 分批首帧、输入响应、完整模型相机与真实样本复测 |
| [M8 实体尺寸手柄](M8_SOLID_HANDLES.md) | Box/Cylinder/Extrude 编辑手柄、发布版窗口验收与剩余边界 |

设计基线日期：2026-09-11；代码实施更新：2026-09-28。现已建立 21 个项目，接通多文档 WPF、真实 OCCT 视口、模型树、属性、基础建模、草图关联重算、装配实例、审阅、工程交付与 M11 关联二维工程图。新增文档命令行、LM Studio/Codex AI Agent 和两个可停靠 Toolbox。M8 为大型模型增加首帧分批显示与状态栏进度，后续补实体尺寸手柄；STEP 内核导入和完整场景建成仍有较长等待。后续单列安装器及实机环境交付验收；当前边界见[进度汇总](STATUS.md)。

M6 功能收尾及真实大型模型的本机证据见 [M6_COMPLETION.md](M6_COMPLETION.md)。M7 的 BOM、HTML 审阅报告和工程交付包见 [M7_ENGINEERING_DELIVERY.md](M7_ENGINEERING_DELIVERY.md)。M8 的首帧交互与性能边界见 [M8_LARGE_MODEL_INTERACTION.md](M8_LARGE_MODEL_INTERACTION.md)。

## 当前存储与交换

H2-B3 已扩展为唯一有界多步链诊断，逐段核对所有输入，报告路径歧义、拆分／共享目标及停止位置；H2-C1 增加可保存的诊断查询和 MetroWindow，见 [历史诊断查询](HISTORY_QUERIES.md)。H2-C2 可在重新分析并确认精确边目标后创建跨特征圆角，过期后必须显式重选，见 [跨特征绑定](HISTORY_FEATURE_BINDINGS.md)。H2-B2 的真实内核接口和本地 NuGet 见 [布尔历史](BOOLEAN_HISTORY.md)，history v2 协议保持兼容，见 [多输入历史](MULTI_INPUT_HISTORY.md)。

H2-A 已补充旋转坐标系的限定舍入处理和旧历史适配器兼容。其当时的布尔接口缺口与阶段证据保留在 [旋转历史与布尔门禁](HISTORY_ROTATION.md)。

M4-T1-H1 建立局部算法历史证据、八节存储和诊断解析，其当时的限制与验证记录见 [算法历史](TOPOLOGY_HISTORY.md)；旋转、布尔和多步诊断的当前范围以上述 H2 文档为准。

M4-T1 已实现 Box 六面/十二边语义引用与持久化；M4-T2 接通 MetroWindow 面/边拾取、失效重选和单边圆角/倒角预览确认；M4-T3-A/B1 增加既有局部特征参数编辑及单边双距离倒角。支持边界见 [拓扑引用基础](TOPOLOGY_REFERENCES.md)和 [局部建模](LOCAL_FEATURES.md)。

`.cadoryx` 采用 ZIP 容器、JSON 清单、MessagePack 3.1.8 数字键 DTO 和独立 BRep/XDE 资产。当前十三节为 features/document v20、structure v3、presentation/history/feature-bindings/drawings v2、sketches v6、geometry/topology/history-queries/external-parts/engineering-review v1；旧文件沿显式迁移链升级，v19→v20 生成空图纸节，drawings v1→v2 迁移新增制图惯例、公差与局部视图字段。资产目录记录媒体类型、编码/格式版本和内核来源。模型数据不直接序列化 ViewModel 或 native 对象。

| 格式 | 读取 | 写入 | 用途 |
|---|---|---|---|
| Cadoryx | 是 | 是 | 稳定业务 ID、定义/实例、参数和精确资产 |
| STEP / STP | 是 | 是 | 精确模型和受支持的装配元数据 |
| IGES / IGS | 是 | 是 | 曲线曲面/模型交换；元数据能力受格式限制 |
| STL | 是 | 是 | 三角网格，二进制或 ASCII，可调网格偏差 |

三种导出均支持可见对象过滤；STL 坐标为 mm，不自带单位标记、装配关系、颜色或参数历史。导出不改变原生文档的保存点。

## 构建与验证

需要 Windows x64、.NET SDK 10.0.401 和可用 OpenGL 桌面环境。`NuGet.Config` 使用相邻 OcctSharp 内层 `artifacts/packages` 作为本地源；当前固定本地开发包 `8.0.1-preview.28.cadoryx.assembly.partner.1`。该版本未公开发布，移植时需提供同版本托管／原生包或从相邻 OcctSharp 源码构建。H2-B2 的历史构建说明见[接入目录](../integrations/occtsharp-boolean-history/README.md)。

```powershell
dotnet build Cadoryx.slnx -c Release
dotnet test Cadoryx.Tests -c Release
dotnet run --project Cadoryx.wpf -c Release
```

[实施说明](IMPLEMENTATION.md)包含实际源码入口、验证方式和当前限制；[格式契约](COMMANDS_AND_STORAGE.md)说明版本演进。详细设计中的未实现类型仍是后续目标，不能据此推断界面已有对应功能。

[M6 历史预算、模型检索与选择测量](M6_WORKSPACE_TOOLS.md)说明本轮三个大阶段的用户入口、计量语义与联合验证。

## 审阅与交付续阶段

- [M6 七阶段：精确检查、剖切、隔离、双视口、CLI、磁盘资产和性能报告](M6_REVIEW_AND_DELIVERY.md)

[M6 截面、干涉与缓存生命周期](M6_SECTION_INTERFERENCE_CACHE.md)记录后续三个大阶段及统一验收。

[M6 功能收尾与验收](M6_COMPLETION.md)记录关联截面、封口、工程标注、持久审阅视图、按需 ZIP、规模预筛选和真实模型验收入口。

[视图导航与 Ribbon 布局](VIEW_NAVIGATION.md)记录聚焦/适应窗口动画、标准视图入口与右键菜单手势。

[M7 工程交付](M7_ENGINEERING_DELIVERY.md)记录装配 BOM、独立 HTML 审阅报告、可校验 ZIP 包及桌面/CLI 验收。
[M11 工程图](M11_TECHNICAL_DRAWINGS.md)记录图纸节、投影视图、尺寸、PDF/打印及本机验收边界。
[M16 图纸与交换](M16_DRAWING_AND_EXCHANGE.md)记录局部放大、公差、制图惯例及交换损失声明的范围；[M17 交付矩阵](M17_RELEASE_MATRIX.md)记录实机取证脚本与未取得的环境证据。

[M8 大模型交互](M8_LARGE_MODEL_INTERACTION.md)记录分批首帧、输入响应、场景切换清理和真实模型基准。

# 许可与签名

- [Cadoryx MIT 许可与本地测试签名](LICENSING_AND_TEST_SIGNING.md)
