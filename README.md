# Cadoryx

H2-B3 已接通有界多步历史诊断，支持局部／布尔链逐段验证与失败定位；范围和限制见[多步历史](docs/HISTORY_CHAINS.md)。仍使用 H2-B2 的独立本地 OcctSharp 包。

WPF / .NET 10 桌面 3D CAD，使用 OcctSharp NuGet。原生文档使用 MessagePack + BRep/XDE 资产；支持 STEP、IGES 导入和 STEP、IGES、STL 导出。

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

```powershell
dotnet build Cadoryx.slnx -c Release
dotnet test Cadoryx.Tests -c Release
dotnet run --project Cadoryx.wpf -c Release
```

环境与本地 NuGet 源配置见文档入口。当前为可运行的基础建模架构，后续能力按 ROADMAP 继续实施。
