# 当前实现与开发入口

更新：2026-09-12。文档描述代码现状；更长期的目标保留在 ROADMAP 和其他设计文档。

## 模块与主要入口

| 模块 | 核心文件 | 实际职责 |
|---|---|---|
| Db | Identity、Geometry、Model、Features、DocumentSnapshot、OccurrencePlacement | 强类型 ID、双精度刚体变换、不可变业务图、循环/所有权验证、实例路径解析 |
| Kernel.Abstractions | Assets、KernelContracts、RecoveryContracts | 内容寻址资产、引用租约、几何/交换/存储与恢复契约 |
| Kernel.Occt | OcctGeometryKernel、OcctGeometryBridge | 固定 OcctSharp NuGet、全局串行原生任务、BRep/XDE 适配 |
| Commands | DocumentCommands、RecomputeCommand、ResourceCommands | 候选状态、归属与锁定检查、基础建模/布尔、属性与资源修改、位姿、依赖闭包重算 |
| Editor | DocumentSession、Workspace、Selection、DocumentRecoveryService | Dispatcher 提交、代际检查、精确历史、保存点、恢复协调和关闭排空 |
| IO | CadDocumentStorage、MessagePackContracts、MessagePackSections、Format、CadRecoveryStore | ZIP/MessagePack v2、JSON v1 读取、完整性与限额、原子替换与恢复快照 |
| Rendering | Scene、CadCamera | 托管场景、实例路径、最终世界变换和可见性 |
| Rendering.Occt | OcctViewport | 独立 native 显示资产、候选场景替换、选取与照明 |
| ViewModels | MainWindowViewModel、CadDocumentViewModel、RecoveryCenterViewModel、DocumentResourcesViewModel、InstancePlacementViewModel、Toolboxes | 多文档命令路由、归属/位姿/资源面板、预览、树/属性双向操作、恢复入口 |
| WPF | App、MainWindow、OcctViewportHost、ViewportView、CadFileDialogs、RecoveryHost、各 MetroWindow | DI、停靠、Win32 宿主、统一弹窗、快捷键与定时恢复保护 |
| Tests | Domain / Kernel / Storage / MessagePackStorage / Export / ToolSession / Recovery / DocumentResources | 领域、并发/租约、真实几何、资源/位姿、格式、预览与恢复回归 |

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

采用 MessagePack NuGet 是合适的工程选择：数值和记录数组紧凑、类型契约明确，C# 工具链成熟。性能提升幅度未做大型模型基准，不能据此保证固定倍数。

- 清单保留 JSON，便于诊断；四个业务节使用数字键 DTO；精确 BRep/XDE 不进入反射对象图序列化。
- 当前 containerVersion=1，核心节 schemaVersion=2 / encoding=messagepack。v1/json 读取为同一领域模型，下次保存写 v2。
- Key 与枚举数字是文件协议，不能重新编号或复用。配方有显式白名单。新增字段需要缺省语义或迁移；未知未来必需功能拒绝加载。
- 未知可选节逐字节保留，同时文档只读；避免业务修改后悄悄写回不理解的引用。
- ZIP 默认总解压上限 1 GiB、单资产 256 MiB、单结构节 32 MiB、20 万条目。还检查路径、重复名、声明长度与 SHA-256；这些限额属于当前可配置策略。
- `ReadSettingsAsync` 不初始化 OCCT。当前存储仍在内存中解码资产，大型模型需要后续流式/按需读取。

## 导入与导出

导入支持 Cadoryx、STEP/STP、IGES/IGS。STEP/IGES 通过 XDE 投影定义、实例、放置、名称及整体颜色，并嵌入源 XDE 上下文。它不代表所有 PMI、图层或子面样式都已完整映射。

导出在独立任务上下文操作捕获的文档：STEP/STP、IGES/IGS 重建 XDE，STL 展开实例并离散。三者均支持可见对象过滤。STL 有线性偏差 mm、角度偏差和二进制/ASCII 选项；只写三角网格，没有内在单位、材质、装配和历史。

输出先写目标目录临时文件，原生写出成功且未取消后替换目标。无几何、错误、取消不得破坏旧文件。导出不会把当前 Cadoryx 文档标成已保存。原生长调用只能在支持的边界响应取消。

## 自动快照与恢复

每 30 秒尝试保护脏文档，在独立恢复目录保留最近两份完整快照。下次启动会显示可恢复文档，也可从“文件 → 文档恢复”进入。恢复以未保存副本打开，先在新会话写好快照再清理来源，不覆盖原文件。正常保存和关闭会协调清理自己的记录；最新快照损坏可回退。快照包含已提交模型及精确资产，不包含撤销栈或未确认预览。详见 [恢复实现](RECOVERY.md)。

## 使用与验证

主页可新建、打开、保存、另存为、导出与管理文档资源。右侧建模参数支持目标零件/图层/材料选择和长方体、圆柱、有限闭合多边形拉伸/旋转；预览后确认。模型树勾选支持多体布尔，差集以选择顺序中的第一个体为主体；当前只允许同一零件定义内运算。点击特征可编辑配方并重算后续依赖。属性面板可修改名称、颜色、显隐、图层和材料；选择实例节点可修改父装配局部坐标下的位姿。详见 [资源与实例](RESOURCES_AND_INSTANCES.md)。

左键选取，Ctrl 多选，中键平移，右键旋转，滚轮缩放；Ctrl+S/Z/Y 路由到活动文档，Esc 取消预览。原生宿主会在嵌入 DialogHost 打开期间隐藏，关闭后恢复；导出选项与文件对话框使用独立 owner 窗口。

`scripts/verify.ps1 -PublishSmoke` 执行构建、测试、发布和真实窗口冒烟。`--smoke <输出目录>` 仅是开发验收入口，会自动构造测试文档并退出；输出 result.txt、bindings.log、原生视口截图、WPF 外壳截图和三种交换文件。WPF RenderTargetBitmap 不包含 HwndHost 的 OpenGL 内容，需同时检查 viewport.png；外壳截图的空白视口不表示原生渲染失败。

增加 `-RecoverySmoke` 会执行实际 30 秒定时快照、测试进程强制终止、重启恢复窗口、恢复副本另存为和关闭清理，使用隔离目录。该过程及三语言窗口截图见 [恢复验收](RECOVERY.md#可重复验证)。

增加 `-WindowSmoke` 执行 M1-Q 固定源颜色、拾取、捕获丢失以及 12 次浮动/重新停靠/关闭重开验证。`CadAppearance.PreserveSourceStyles` 贯穿源显示、属性编辑、预览与 MessagePack；IGES 一致的面颜色投影为整体颜色，导出报告明确曲面边界。详见 [交换与视口验收](EXCHANGE_AND_VIEWPORT.md)。所有诊断入口的布局与恢复目录现已隔离，不再依赖用户的已保存工具箱布局。

## 尚未完成

通用跨节迁移、资产媒体类型/内核版本目录、完整装配编辑和共享子装配使独立、完整约束草图求解、持久拓扑命名、外部引用、多视口、大型模型性能预算仍未完成。文档内材料/图层管理已接通，外部材料库和纹理/PBR 材质仍未提供。拉伸/旋转参数面板属于有限轮廓工具，不能称为完整草图编辑器。

验证是在当前 Windows 机器和本地包基线上完成；独立发布目录使用已安装的 .NET 10 Desktop Runtime。尚未在全新 Windows 虚拟机、混合 DPI、多 GPU/远程桌面或长时间运行条件下验收，也未制作安装器或发布 NuGet。
