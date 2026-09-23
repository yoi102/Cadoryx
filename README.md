# Cadoryx

H2-C1 已接通可保存的历史诊断查询与统一风格窗口；H2-C2 在唯一且精确确认的 Box 源边历史上创建跨特征圆角，并支持过期后的显式重选。范围见[跨特征绑定](docs/HISTORY_FEATURE_BINDINGS.md)。当前消费加入视口天空渐变的独立本地 OcctSharp 包。

WPF / .NET 10 桌面 3D CAD，使用 OcctSharp NuGet。底部状态栏可按文档调整 XY 工作网格显示、间距和吸附，并打开文档设置修改天空渐变背景；视口支持鼠标确定基础形体尺寸／高度。原生文档使用 MessagePack + BRep/XDE 资产，支持 STEP、IGES 导入和 STEP、IGES、STL 导出。

- [文档入口](docs/README.md)
- [当前代码实现与使用方式](docs/IMPLEMENTATION.md)
- [整体架构](docs/ARCHITECTURE.md)
- [数据模型](docs/DATA_MODEL.md)
- [路线与验收](docs/ROADMAP.md)
- [自动快照与异常退出恢复](docs/RECOVERY.md)
- [零件、图层/材料与实例位置](docs/RESOURCES_AND_INSTANCES.md)
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

环境与本地 NuGet 源配置见文档入口。当前为可运行的基础建模架构，后续能力按 ROADMAP 继续实施。
