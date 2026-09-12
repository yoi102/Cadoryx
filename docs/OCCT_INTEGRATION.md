# OcctSharp 接入设计

## 1. 本轮核实范围与版本选择

核实日期：2026-09-11。参考仓库为 `C:\Users\yoiri\source\repos\OcctSharp`，源码/包在其内层 `OcctSharp` 目录，产品文档在仓库根 `docs` 目录。

| 检查项 | 本轮看到的事实 | 接入结论 |
|---|---|---|
| 根 `docs/STATUS.md` | 更新于 2026-09-09，标记 preview.26 / ABI 1.70 / bridge 0.78.0 为完整本地验证产品 | 以 preview.26 作为初始接入候选基线 |
| 内层 `Directory.Build.props` | 当前源码版本为 `8.0.1-preview.27` | 这是正在推进的候选，不自动替代已验证基线 |
| STATUS 中 AB 状态 | preview.27 的部分构建/测试已过，Debug 和剩余交付门禁未完成 | 不将 preview.27 说成已完成产品验证 |
| 本地 `artifacts/packages` | 存在 preview.26 的门面、12 个模块和 Native 共 14 个 nupkg | 可用本地 NuGet 源启动消费验证 |
| 实际读取 preview.26 门面 nuspec | `OcctSharp` 面向 net10.0，依赖同版本 12 个模块 | 初期门面包简化友好 API 接入；所有组件锁定同一版本族 |
| NuGet 公网发布 | 本轮未检查；仓库状态注明该轮发布 NOT RUN | 不假设任一候选已经在 nuget.org 可用 |

以上为选型时的源仓库记录。2026-09-12 已在 Cadoryx 中实际消费固定 preview.26 包，完成几何/交换测试及真实 WPF Viewer 冒烟；未重跑 OcctSharp 自身完整产品发布门禁，也未验证 NuGet 公网发布状态。后续升级仍需重新消费验收。

## 2. NuGet 依赖策略

初版由 `Cadoryx.Kernel.Occt` 引用 `OcctSharp` 门面，以便使用现有友好 API；Windows 应用显式引用匹配版本的 `OcctSharp.Native.win-x64` 作为部署依赖。Rendering.Occt 如直接使用友好 Viewer 类型，也显式声明对应门面依赖，不依靠碰巧可见的传递引用。

已应用的配置方向（项目实际通过 `$(OcctSharpVersion)` 统一锁定精确版本）：

```xml
<!-- Cadoryx.Kernel.Occt.csproj -->
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <PlatformTarget>x64</PlatformTarget>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="OcctSharp" Version="8.0.1-preview.26" />
</ItemGroup>

<!-- Cadoryx.wpf.csproj 的部署配置 -->
<PropertyGroup>
  <TargetFramework>net10.0-windows</TargetFramework>
  <PlatformTarget>x64</PlatformTarget>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="OcctSharp.Native.win-x64"
                    Version="8.0.1-preview.26" />
</ItemGroup>
```

代码块分别属于两个项目。当前 OcctSharpVersion 位于 Cadoryx 自己的 Directory.Build.props，项目使用精确版本区间并生成 packages.lock.json；MessagePack 固定为 3.1.8。所有依赖迁移到 Directory.Packages.props 属于后续维护工作。未复制参考仓库的原生构建配置。

本地源：`C:\Users\yoiri\source\repos\OcctSharp\OcctSharp\artifacts\packages`。当前 NuGet.Config 使用相邻目录相对路径及 OcctSharp 源映射；其他机器需提供相同包或修改源位置，不把源码项目作为编译引用。

运行时使用公开的 `OcctRuntime.Info` 触发兼容性检查；`EnsureCompatible()` 是 OcctSharp 内部方法，外部消费者不能调用。几何和交换运行在全局串行队列；XDE 导出先 `BeginTransaction()` 建立结构和颜色，再 Commit，最后 WriteStep/WriteIges。发布输出复制 Native DLL、包内许可证及 Cadoryx runtime-baseline.json。

以后按模块瘦身时以公开 API 所在程序集和实际 NuGet 依赖为准。源码文件仍位于 `src/OcctSharp` 并不等于该类型一定编译在门面中；当前项目已有 Compile Remove、模块链接与类型转发。

## 3. 源码已存在的接入点

下列是已核实的接入点。基础建模、BRep、XDE 交换、Viewer 的当前使用方式已在 preview.26 消费验证；表中的其他能力仍需对应业务闭环验收。

| Cadoryx 需求 | OcctSharp 入口 | 边界 |
|---|---|---|
| 原生运行时检查 | `OcctRuntime.Info` | 公开消费者入口，触发 ABI/版本检查 |
| 基础体 | `ShapeFactory.CreateBox/CreateCylinder/CreateSphere` | 返回 owned Shape；参数由业务单位/公差规则校验 |
| 布尔 | `FeatureModeling.Boolean` | 适配结果、诊断、历史与释放，不能只判断返回非 null |
| BRep 读写 | `ShapeExchange.ReadBrep/WriteBrep` | 精确几何资产入口；不包含完整业务/装配元数据 |
| 几何 STEP/IGES | `ShapeExchange.ReadStep/ReadIges` | 明确 geometry-only；不作为默认保真装配导入 |
| STEP/IGES 与元数据 | `XdeDocument.ReadStep/ReadIges/ReadExchange` | 默认导入方向，读取定义/实例/名称/颜色等 |
| XDE 持久上下文 | `XdeDocument.Open/Save` | 可保存 BinXCAF 上下文；不等于 Cadoryx 文件格式 |
| XDE 导出 | `XdeDocument.WriteStep/WriteIges` | 独立导出上下文，修改标签须先开启事务 |
| STL 导出 | `ShapeExchange.WriteStl` / `StlWriteOptions` | 展开世界坐标实例，指定线性/角度偏差与二进制/ASCII |
| 装配编辑 | `SetOccurrenceLocation/ReplaceOccurrence/ReparentOccurrence` | 标签可能替换；Cadoryx ID 不依赖标签 Entry |
| 装配定位 | `ResolveOccurrencePath`、`XdeOccurrence.GetWorldLocation()` | 完整路径与世界变换；返回 owned 值需要释放 |
| 子拓扑/包围盒 | `Shape.GetSubShapes/GetBoundingBox` | 子形状返回有所有权的包装；序号不是跨版本身份 |
| 草图几何 | `SketchModeling.Evaluate/Project/Intersect/CreateEdge` | 有曲线/轮廓能力，不代表有约束求解器 |
| 参数化图 | `ParametricDocument.Attach/Recompute/EditAndRecompute` | 同步、非并发；适配器内的计算上下文 |
| 持久选择 | `ParametricDocument.Select/Resolve/GetHistory` | 绑定结果版本和原生上下文；允许失败/不支持 |
| Viewer | `OcctViewer.Create/Display/FitAll/SetProjection` | Create 需要 HWND，所有操作受创建线程约束 |
| 拾取 | `GetSelectedItems`、`ViewerSelectionItem.SourceIdentity/Shape` | 转换为业务目标，释放临时 Shape |
| 操纵与截图 | `ViewerInputController`、`ViewerRendering`、复制帧 | 单独适配，复制帧不等于实时 D3DImage |

单个 API 存在不代表整个业务闭环实现：例如 Fillet 需要边选取、持久引用、参数输入、预览、失败处理和撤销；装配移动需要正确的共享定义编辑语义；草图拉伸需要轮廓验证。

## 4. 内核契约与资产所有权

上层需要的最小接口职责：

| 契约 | 输入 | 输出 |
|---|---|---|
| `IKernelCapabilities` | 操作类型与模型类型 | Supported/Unsupported、限制、版本 |
| `IGeometryOperations` | 类型化参数 + 精确输入资产引用 | 暂存输出资产 + 诊断 + 可选拓扑历史 |
| `IModelExchange` | 路径/流、格式、单位与元数据选项 | 导入候选子图 + 资产 + 来源报告，或导出报告 |
| `IGeometryAssetStore` | AssetId、只读/暂存租约 | 受控读取、发布、释放 |
| `ITopologyResolver` | 持久引用 + 目标几何版本 | 明确解析状态与临时子拓扑 token |
| `IKernelScheduler` | 输入快照、CancellationToken | 串行执行结果，不传播 UI 句柄 |

不提供 `object GetNativeObject()`、`IntPtr Handle` 一类公共逃生口。Rendering.Occt 如需 native Shape，使用仅在适配器模块之间可见的受控租约；不得穿过 ViewModel 边界。

| 对象 | 已观察到的所有权行为 | Cadoryx 规则 |
|---|---|---|
| Shape | owned/disposable 原生包装 | 操作、缓存或租约有唯一明确释放路径 |
| XdeDocument | 拥有文档 | 独立交换/计算作用域或视口专用上下文拥有 |
| XdeLabel | 依赖父 XdeDocument | 不跨父对象寿命；复制业务值后离开适配器 |
| XdeOccurrence | 持有 owned 世界位置，Label 依赖文档 | 调用方释放 occurrence；不能因释放它就认为 Label 独立 |
| ViewerSelectionItem | Shape 是 owned，Presentation 依赖 Viewer | 选择回调中及时 Dispose，不从属性面板长期缓存 |
| ViewerPresentation | Viewer 所有、线程/父对象关联 | 从 Viewer 移除/重建时更新映射，不能写入文档 |
| OcctViewer | 创建线程所有 | 在 UI 线程、窗口销毁之前释放 |

不能凭 SafeHandle 推断整个模型可跨线程并发修改；包装保护句柄生命周期，不自动保证 OCCT 算法或 TShape 的数据竞争安全。

## 5. 两个原生上下文边界

### 计算上下文

内核 worker 从不可变输入建立独立 Shape/XDE/ParametricDocument。完成后把结果序列化为资产，复制元数据与诊断，再释放 native 对象。若逐操作重建成本过高，可以按文档版本缓存专属计算上下文，但它只是权威业务快照的派生上下文；版本不匹配必须丢弃或重建。

### 显示上下文

UI 线程从结果资产建立显示专用 Shape 或 XDE 上下文，在对应 Viewer 显示。网格化可能改变形状内部缓存，因此不能把后台算法同时使用的底层共享 Shape 直接送给 Viewer。

首个导入里程碑可以利用视口专用 XDE 上下文保留受支持的颜色/装配显示；业务仍从托管元数据投影读取。实现从 XDE 展示到 Cadoryx 逐体/逐实例可编辑场景的映射，并测试选中回传。不能展示一个根 Compound 后就宣称已支持所有实例和面级选择。

导入投影按可复用 XDE 定义建立本次导入的 DefinitionId 映射，体资产保存在定义局部坐标系，Slot 只存相对父装配的局部位姿。不能把 `XdeOccurrence.GetLocatedShape()` 得到的世界形状保存为零件局部资产后再应用一遍实例变换。导入器必须用已知非零平移、非交换旋转组合和重复子装配验证这一点。

源格式出现当前刚体实例模型无法表达的缩放/镜像时，明确选择烘焙到新几何定义并重建对应关系，或拒绝该项并报告；不能丢弃变换。对于同一次导入，源 Label/定义关系用于识别共享，hash 仅可进一步复用相同资产，不能把几何相同但名称/材料不同的零件自动合并成同一个业务定义。

Presentation 映射至少包含 `(DocumentId, OccurrencePath, BodyId, GeometryRevision)`。XDE SourceIdentity 可帮助映射，但只在对应上下文中有效；重建上下文后必须重新建立映射。

关闭顺序：阻止新任务/输入 → 取消并等待受支持边界 → UI 移除预览/Presentation → UI Dispose Viewer → 销毁 HWND → 释放显示上下文 → 释放 Session 历史/资产 → 释放已结束的计算上下文。长原生调用尚未返回时，不提前释放其输入。

## 6. 参数化能力的复用方式

Cadoryx 定义图和特征参数是业务事实。每次准备命令把其受支持部分投影为候选 `ParametricDocument`，或直接调用 ShapeFactory/FeatureModeling 等独立操作。

- 使用 `EditAndRecompute` 可利用 OcctSharp 内部失败回滚，但结果仍须通过 Cadoryx 的版本检查与提交。
- 不让 UI 同时修改原生图和托管图；不把原生 UndoHistory 直接绑定为 Cadoryx 文档历史。
- 长期复用原生图时，记录投影的业务 StateId/特征修订；Undo/Redo 后按目标快照恢复上下文，不能“猜着”把另一份历史退一步。
- 原生 selection Entry 只有在对应上下文保留时才有意义。若需要跨保存恢复 TNaming 选择，把上下文作为精确版本资产，并验收其与业务 StateId/GeometryRevision 的一致性。
- 原生图未支持的特征返回 Unsupported；使用已保存精确结果只读展示，不静默换成近似建模算法。

这是先保留产品控制权、以有限投影复用内核能力的取舍。若未来决定以 OCAF/XDE 全面作为文档数据库，应通过独立架构决策整体迁移权威状态、事务和存储；不能在当前方案里逐步积累两个主数据库。

## 7. WPF 视口方案

| 方案 | 当前证据 | 适用性 |
|---|---|---|
| HwndHost + OcctViewer | 参考 WPF 示例已经实现 | 首阶段采用；普通控件放旁边/独立行 |
| 复制颜色帧 + WriteableBitmap | 示例用于独立冻结截图 | 缩略图、评审、对话框遮罩占位 |
| 高频复制帧显示 | 本轮未验证持续帧率、延迟和输入命中 | 可作为后续技术试验，不设为首版承诺 |
| D3DImage | 示例明确未实现实时桥接 | 需要 OpenGL/D3D9Ex 共享、同步、设备丢失处理专项验证 |

首阶段宿主放在 WPF View 中；不要把 HWND 放入 Db。WPF 的悬浮工具条、嵌入式对话框和 Adorner 不能假设压在原生视口之上。视口外工具面板天然适合参数编辑；视图立方体/坐标轴优先用内核可用的 Viewer 辅助显示。

统一 `IViewportHostCapabilities` 描述 SupportsWpfOverlay、SupportsCopiedSnapshot、SupportsSubshapeSelection 等。它影响真实 UI 布局和可用操作，不只作为技术状态栏字段。

WPF 浮动/重新停靠、DPI 和 HWND 重建时保存托管相机快照，重新创建 Viewer/Presentation 后恢复；验证多个文档标签、隐藏标签、远程桌面和关闭过程中是否有资源泄漏或黑屏。多视口能力放到真实验证后开放。

## 8. 接入验收清单

1. 干净目录通过指定 NuGet 源还原固定版本，不引用参考源码项目，不依赖 PATH。
2. 检查 ABI，创建 Box，BRep 保存/读回后比对体积、包围盒和拓扑合法性；验证资产损坏错误。
3. 导入包含重复子装配、非单位位姿、名称、颜色/透明度的 STEP；树和 Viewer 对应正确。
4. 导入至少 mm/in 两组已知尺寸模型，验证规范 mm 坐标与导出单位；IGES 单独验证。
5. 布尔/变换前后可撤销重做，重复命令不会让原生句柄数量无限增长。
6. Viewer 选取体/面/边、UI 选择同步、相机导航、窗口 resize/DPI/重建行为正确。
7. 主窗口确认/进度对话框在 Viewer 前可见可操作，关闭后相机和场景恢复。
8. 取消、失败、关闭中任务、重复开关文档等路径不提交旧结果、不提前释放 native 输入。
9. 参数化命名必须包含拓扑改变、删除/歧义、保存恢复测试，Unsupported 要有正确 UI。

这些是后续要运行的验收，不是本轮已通过的测试。

## 9. 源码证据导航

路径相对上述 OcctSharp 仓库根：

- `docs/STATUS.md`：preview.26 验证基线与 preview.27 进行中状态。
- `OcctSharp/Directory.Build.props`：目标框架、平台、当前源码版本。
- `OcctSharp/src/OcctSharp/OcctSharp.csproj`：门面和模块依赖、Compile Remove。
- `OcctSharp/artifacts/packages/OcctSharp.8.0.1-preview.26.nupkg` 内 `OcctSharp.nuspec`：实际包身份和依赖。
- `OcctSharp/src/OcctSharp/ShapeFactory.cs`、`ShapeExchange.cs`、`FeatureModeling.cs`、`Shape.cs`：基础建模和几何交换。
- `OcctSharp/src/OcctSharp/XdeDocument.cs`、`XdeDocument.Assembly.cs`、`XdeOccurrence.cs`：文档/装配与所有权。
- `OcctSharp/src/OcctSharp/OcctViewer.cs`、`ViewerSelectionItem.cs`：创建线程、选中对象和生命周期。
- `OcctSharp/src/OcctSharp/Parametric/ParametricDocument.cs`、`ParametricRecompute.cs`、`ParametricSelection.cs`：图、事务、结果版本和选择。
- `docs/BATCH_O_2D_SKETCH_PLANAR_MODELING_GAP_INVENTORY.md`：草图能力与非目标。
- `docs/BATCH_T_PARAMETRIC_DOCUMENT_RECOMPUTE_GAP_INVENTORY.md`：没有通用约束求解器和任意拓扑变更命名保证。
- `OcctSharp/samples/OcctSharpViewer.Wpf/README.md`：XDE 加载、HwndHost 和静态复制帧说明。
