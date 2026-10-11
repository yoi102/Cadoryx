# Cadoryx

2026-10-11：当前开发按用户要求暂停于本地提交检查点；A–F尚未全部完成，G实机、安装及交付暂缓。当前应用与隔离候选的结果分列于[暂停检查点](docs/AF_CHECKPOINT_2026-10-11.md)及[当前状态](docs/STATUS.md)。

WPF / .NET 10 桌面 3D CAD，使用 OcctSharp NuGet。当前已接通基础建模、草图与装配、模型审阅、双视口、大模型按需资产读取和分批显示、BOM/HTML/ZIP 工程交付，以及关联二维工程图。原生文档使用 MessagePack + BRep/XDE 资产；STEP、IGES、STL 均可导入和导出。各阶段实际范围和交付验收边界见[当前进度](docs/STATUS.md)与[M11 工程图](docs/M11_TECHNICAL_DRAWINGS.md)。

[交换往返实测](docs/F4_MEASURED_EXCHANGE.md)及各建模、工程图和命令工具有明确的本地限定契约；剩余范围见[产品缺口与七批规划](docs/CAD_PRODUCT_GAPS.md)和[A–F实施跟踪](docs/AF_IMPLEMENTATION_TRACKER.md)。

- [文档入口](docs/README.md)
- [当前代码实现与使用方式](docs/IMPLEMENTATION.md)
- [整体架构](docs/ARCHITECTURE.md)
- [数据模型](docs/DATA_MODEL.md)
- [路线与验收](docs/ROADMAP.md)
- [当前进度与剩余工作](docs/STATUS.md)
- [M14–M17 后续工作规划](docs/NEXT_STAGES.md)
- [自动快照与异常退出恢复](docs/RECOVERY.md)
- [文件打开进度与快速取消](docs/PROGRESS_DIALOG.md)
- [消息通知与位置设置](docs/NOTIFICATIONS.md)
- [零件、图层/材料与实例位置](docs/RESOURCES_AND_INSTANCES.md)
- [扩展关联基准与旋转轴实施](docs/C2_EXTENDED_DATUMS.md)
- [格式演进、旧文件迁移与存储基准](docs/FORMAT_EVOLUTION.md)
- [草图模型、约束求解与持久化基础](docs/SKETCH_FOUNDATION.md)
- [草图编辑器与关联特征使用说明](docs/SKETCH_EDITOR.md)
- [拓扑引用基础与支持边界](docs/TOPOLOGY_REFERENCES.md)
- [面/边选择、引用重选与局部圆角/倒角](docs/LOCAL_FEATURES.md)
- [局部算法历史映射与持久化门禁](docs/TOPOLOGY_HISTORY.md)
- [旋转历史校验与布尔能力门禁](docs/HISTORY_ROTATION.md)
- [多输入历史协议与布尔接入准备](docs/MULTI_INPUT_HISTORY.md)
- [真实布尔逐源历史](docs/BOOLEAN_HISTORY.md)
- [有界多步历史诊断](docs/HISTORY_CHAINS.md)
- [历史诊断查询与 MetroWindow](docs/HISTORY_QUERIES.md)
- [跨特征边绑定与显式重选](docs/HISTORY_FEATURE_BINDINGS.md)
- [工作网格与视口鼠标建模](docs/VIEWPORT_CONSTRUCTION.md)
- [OcctSharp NuGet 能力核查](docs/OCCT_CAPABILITY_AUDIT.md)

```powershell
dotnet build Cadoryx.slnx -c Release
dotnet test Cadoryx.Tests -c Release
dotnet run --project Cadoryx.wpf -c Release
```

环境与本地 NuGet 源配置见文档入口。当前锁定相邻 OcctSharp 仓库中的本地开发包；本机发布版、真实模型和自包含 MSI 的本机安装/卸载验收已通过，全新机器及公开发布仍未验收。

[M6 功能收尾与实测](docs/M6_COMPLETION.md)：关联截面/封口、工程标注、审阅视图、按需 ZIP 与 XDE 上下文复用；534 项测试和真实 STEP 双视口基准通过，环境验收边界见文档。

工程交付：Ribbon 文件组的包裹图标提供 BOM 预览、CSV、独立 HTML 审阅报告及可验证的 ZIP 交付包；支持附加 STEP/IGES/STL，详见 [M7 工程交付](docs/M7_ENGINEERING_DELIVERY.md)。

二维工程图：Ribbon 文件组的图纸图标打开多页图纸窗口，建立真实 BRep 关联投影、精确基准尺寸，提供矢量 PDF 和打印。CLI `drawing-pdf` 可以导出已有图纸，详见 [M11 工程图](docs/M11_TECHNICAL_DRAWINGS.md)。

M12 增加装配实例的视口 XYZ 拖动和 Revolve 角度手柄，并优化大型 STEP 导入。本机固定样本导入约 81 秒，完整场景提交仍约 9 秒；实现、测试证据和后续性能边界见 [M12 交互与性能](docs/M12_INTERACTION_AND_PERFORMANCE.md)。

M13 提供自包含 Windows x64 MSI 与可复跑的安装/卸载验收脚本；构建方法、本机证据及仍需实机验证的环境见 [M13 安装器与环境](docs/M13_INSTALLER_AND_ENVIRONMENT.md)。

# License

Cadoryx source code is available under the [MIT License](LICENSE). Bundled third-party components retain their own licenses; see the included `licenses` directory and package notices.

- [C4 孔工具、常用尺寸及当前验证](docs/C4_STANDARD_HOLES.md)
- [D1 高级拉伸方向、操作和平面终止](docs/D1_ADVANCED_EXTRUSION.md)
- [D2 扫掠／放样：生产编辑、来源和持久化](docs/D2_SWEEP_LOFT.md)
