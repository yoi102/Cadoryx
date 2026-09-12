# Cadoryx

基于 WPF / .NET 10 和 OcctSharp 的桌面 3D CAD。窗口布局沿用 Direct2dCad 的 Ribbon、可停靠工具箱、多文档区域和状态栏；建模数据按三维零件、装配实例、特征和精确 BRep 重新设计。

## 设计入口

| 文档 | 内容 |
|---|---|
| [整体架构](ARCHITECTURE.md) | 项目依赖、职责、窗口布局、会话和线程 |
| [数据结构](DATA_MODEL.md) | 文档、零件、装配、几何、特征、草图、拓扑引用 |
| [命令与存储](COMMANDS_AND_STORAGE.md) | 事务、撤销、异步提交、增量更新、`.cadoryx` 格式 |
| [OcctSharp 接入](OCCT_INTEGRATION.md) | 已核实的 API、包版本、所有权、视口限制 |
| [实施路线与验收](ROADMAP.md) | 每个阶段的交付、验证及本轮完成边界 |
| [异常退出恢复](RECOVERY.md) | 自动快照、恢复入口、进程锁、故障边界与验收 |
| [资源与实例](RESOURCES_AND_INSTANCES.md) | 建模归属、图层/材料、局部位姿、MetroWindow 风格 |
| [交换与视口验收](EXCHANGE_AND_VIEWPORT.md) | 固定单位/旋转/面颜色样本、鼠标捕获、浮动与环境门禁 |
| [格式演进与存储基准](FORMAT_EVOLUTION.md) | 资产描述、跨节迁移、旧文件固定样本、读写时间与内存 |
| [草图模型与求解基础](SKETCH_FOUNDATION.md) | 13 种约束、局部自由度/冲突、命令与六节文件协议 |
| [草图编辑与关联特征](SKETCH_EDITOR.md) | MetroWindow 二维编辑、尺寸/约束、候选预览、依赖重算与新版协议 |

设计基线日期：2026-09-11；代码实施更新：2026-09-12。现已建立 15 个项目，接通多文档 WPF、真实 OCCT 视口、模型树、属性、基础建模、特征重算、撤销重做、文件操作、恢复及有限约束集的草图编辑/关联特征。曲线区域、拓扑命名、装配编辑及大型模型能力仍按[路线图](ROADMAP.md)推进。

## 当前存储与交换

M4-T1 已实现 Box 六面/十二边语义引用与持久化；M4-T2 接通 MetroWindow 面/边拾取、失效重选和单边圆角/倒角预览确认。支持边界见 [拓扑引用基础](TOPOLOGY_REFERENCES.md)和 [局部建模](LOCAL_FEATURES.md)。

`.cadoryx` 采用 ZIP 容器、JSON 清单、MessagePack 3.1.8 数字键 DTO 和独立 BRep/XDE 资产。当前七节为 features v5、document v4、structure v3、presentation/sketches v2、geometry/topology v1；支持旧四节 JSON v1、MessagePack v2、M3-V 及 M4-S1/S2/T1 文件迁移。资产目录记录媒体类型、编码/格式版本和内核来源。模型数据不直接序列化 ViewModel 或 native 对象。

| 格式 | 读取 | 写入 | 用途 |
|---|---|---|---|
| Cadoryx | 是 | 是 | 稳定业务 ID、定义/实例、参数和精确资产 |
| STEP / STP | 是 | 是 | 精确模型和受支持的装配元数据 |
| IGES / IGS | 是 | 是 | 曲线曲面/模型交换；元数据能力受格式限制 |
| STL | 尚未提供导入入口 | 是 | 三角网格，二进制或 ASCII，可调网格偏差 |

三种导出均支持可见对象过滤；STL 坐标为 mm，不自带单位标记、装配关系、颜色或参数历史。导出不改变原生文档的保存点。

## 构建与验证

需要 Windows x64、.NET SDK 10.0.401 和可用 OpenGL 桌面环境。`NuGet.Config` 使用相邻 OcctSharp 内层 `artifacts/packages` 作为本地源；固定包版本 `8.0.1-preview.26`。移植到其他机器时配置相应的包源。

```powershell
dotnet build Cadoryx.slnx -c Release
dotnet test Cadoryx.Tests -c Release
dotnet run --project Cadoryx.wpf -c Release
```

[实施说明](IMPLEMENTATION.md)包含实际源码入口、验证方式和当前限制；[格式契约](COMMANDS_AND_STORAGE.md)说明版本演进。详细设计中的未实现类型仍是后续目标，不能据此推断界面已有对应功能。
