# Cadoryx

WPF / .NET 10 桌面 3D CAD，使用 OcctSharp NuGet。原生文档使用 MessagePack + BRep/XDE 资产；支持 STEP、IGES 导入和 STEP、IGES、STL 导出。

- [文档入口](docs/README.md)
- [当前代码实现与使用方式](docs/IMPLEMENTATION.md)
- [整体架构](docs/ARCHITECTURE.md)
- [数据模型](docs/DATA_MODEL.md)
- [路线与验收](docs/ROADMAP.md)

```powershell
dotnet build Cadoryx.slnx -c Release
dotnet test Cadoryx.Tests -c Release
dotnet run --project Cadoryx.wpf -c Release
```

环境与本地 NuGet 源配置见文档入口。当前为可运行的基础建模架构，后续能力按 ROADMAP 继续实施。
