# Cadoryx 数据结构设计

2026-09-13 更新：FeatureDefinition 持有可空 TopologyHistory，包含来源／结果修订与资产、适配器版本、BRep 指纹、全图数量和逐源演化关系。H2-B1 新增 AdditionalSources 和 SourceArgument，以输入参数与源槽位共同标识来源，并核对配方及上游特征顺序。该值随创建／重算提交及精确撤销恢复；索引仅属于声明的精确图，不是通用持久身份。结构、归约与八节协议见 [算法历史](TOPOLOGY_HISTORY.md)及[多输入历史](MULTI_INPUT_HISTORY.md)。

本文定义长期目标模型、字段语义和不变量。2026-09-12 已实现强类型 ID、双精度几何、DocumentSnapshot、Part/Assembly/Slot/Body、七种特征配方、图层/材料、点/线/圆草图及 13 种约束与引用验证；更完整的约束集、关联基准、持久拓扑命名、装配约束等片段仍是设计草案。准确的当前结构见 `Cadoryx.Db` 和[实施说明](IMPLEMENTATION.md)。序列化 DTO 位于 `Cadoryx.IO`，与领域类型分离。

## 1. 三类数据

M3-V 补充：`GeometryAssetRef.Format`、`XdeSourceRef.Format` 保存不可变 `AssetFormat`（媒体类型、编码/格式版本、内核及写出库来源）。共享内存资产仓只管理字节及租约，不全局覆盖文档来源。持久化时以 `GeometryRevisionId` 建立独立 geometry 表，体、特征结果和配方输入引用同一记录；同一修订的不同定义必须拒绝。未知扩展的资产格式由 `RetainedAssetFormats` 随只读快照保留。详细协议见 [格式演进](FORMAT_EVOLUTION.md)。

| 数据 | 示例 | 是否持久化 | 谁维护 |
|---|---|---|---|
| 业务/设计数据 | 名称、定义、装配引用、参数、草图约束、材质、命名视图 | 是 | Db + 文档命令 |
| 精确结果与源资产 | 导入体 BRep、建模结果 BRep、源交换附件 | 是；不可重建的导入资产必需 | 资产仓 + Kernel.Occt + IO |
| 会话/派生数据 | Selection、Camera、工具预览、BVH、网格、Viewer、原生句柄 | 默认否；NamedView 等显式转成业务对象 | Editor / Rendering |

参数化结果可以重算，但首版仍保存最后一次成功的精确结果，让文档在能力缺失时可查看。导入形状、直接编辑的固化结果不能被当作可随意删除的缓存。装配结构也不能只存在 Viewer 场景里。

## 2. 标识与版本

使用持久 Guid 强类型 ID，避免混用不同类别；名字可以重复，不能作为关联键。MessagePack DTO 使用 Guid 和记录数组；清单及旧 JSON 使用显式 ID 包装字段。窗口标签使用独立会话 ID，允许同时打开业务 DocumentId 相同的两个文件副本。

| ID | 含义 / 作用域 |
|---|---|
| `DocumentId` | 文档身份；普通保存/另存为默认保留，创建独立副本另分配 |
| `DefinitionId` | 文档内唯一；PartDefinition 或 AssemblyDefinition |
| `ComponentSlotId` | 文档内唯一的装配子项身份；属于一个 AssemblyDefinition |
| `BodyId` | 零件内的逻辑体，文档内唯一；不等于某次 Shape 地址 |
| `FeatureId` / `SketchId` / `DatumId` | 特征、草图、基准的业务身份 |
| `SketchElementId` / `ConstraintId` | 草图内部的稳定元素/约束身份 |
| `TopologyReferenceId` | 持久面/边/顶点引用身份，解析可能失败 |
| `LayerId` / `AppearanceId` / `MaterialId` | 组织、显示外观、物理材料分别建模 |
| `GeometryRevisionId` | 一次确定的几何结果身份，旧版本仍可由历史引用 |
| `DocumentStateId` | 一次逻辑业务状态的身份；撤销可恢复旧值 |
| `Generation` | Session 中递增的 long；提交/撤销/重做后递增，绝不回退 |
| `AssetId` | 保存字节的 SHA-256；按内容寻址，与业务 Guid 分开 |

```csharp
public readonly record struct DocumentId(Guid Value);
public readonly record struct DefinitionId(Guid Value);
public readonly record struct ComponentSlotId(Guid Value);
public readonly record struct BodyId(Guid Value);
public readonly record struct GeometryRevisionId(Guid Value);
public readonly record struct DocumentStateId(Guid Value);

// 精确字节的 hash，不用于判定两个 BRep 在几何意义上是否相等。
public readonly record struct AssetId(string Sha256);
public sealed record DocumentVersion(DocumentStateId StateId, long Generation);
```

不变量：Guid.Empty 无效；hash 必须是规范化 64 位十六进制；输入 DTO 不能绕过验证。Copy/Paste 导入另一个文档时，对业务 ID 进行完整映射；资产字节可按 hash 复用。未来跨文件装配引用必须同时带目标文档身份和固定内容版本，不能只凭文件名。

`IsDirty = CurrentStateId != SavedStateId`。Generation 用于异步过期检测，不拿来代替保存点判断。例如：保存 S2 → 编辑 S3 → 撤销 S2，文件不脏，但 Generation 仍增加，S3 启动的旧任务不能提交。

## 3. 数学、单位与公差

采用右手坐标系、Z-up，内核规范长度单位 mm、角度 rad，所有领域坐标用 double。界面可显示 mm/cm/m/in 和度数，但显示单位切换不缩放模型。导入先读取源单位，经明确的一次转换进入规范单位；记录 `SourceUnit` 和 `AppliedScale`，防止 OCCT 已转换后再次乘比例。

```csharp
public readonly record struct Point3d(double X, double Y, double Z);
public readonly record struct Vector3d(double X, double Y, double Z);
public readonly record struct Quaterniond(double X, double Y, double Z, double W);
public sealed record RigidTransform3d(Vector3d TranslationMm, Quaterniond Rotation);
public sealed record PlaneFrame3d(Point3d Origin, Vector3d U, Vector3d V);
public sealed record Bounds3d(Point3d Min, Point3d Max);
```

- 所有输入必须有限；拒绝 NaN、Infinity；空包围盒单独表达，不使用无穷大作为持久数据。
- Quaternion 非零且归一化；Identity 是平移零、旋转 `(0,0,0,1)`。`default` struct 不能冒充有效旋转。
- 约定列向量：`pWorld = Troot * Tslot1 * ... * TslotN * pLocal`。层级组合按此顺序，禁止在不同适配器里互换乘法约定。
- 装配放置使用刚体变换；缩放与镜像是产生新几何版本的建模操作。首版不在实例变换中接受非均匀缩放/负行列式，避免法向、质量和公差语义混乱。
- Plane 的 U/V 为单位正交向量，`N = U × V`；草图二维点映射为 `Origin + u*U + v*V`。
- `DocumentUnits` 保存显示长度单位、角度单位与显示小数位；显示精度不改变 BRep 精度。
- `ModelingTolerance` 分开存绝对线性公差、相对公差和角公差，按操作类型计算有效值，不能全局使用 `double.Epsilon`。
- `MeshingSettings` 的弦高/角偏差与几何运算公差独立。`1e-7 mm` 可作为评估候选值，不能在未验证模型尺度时承诺适合所有导入模型。
- 参数带量纲：长度、面积、体积、角度、无量纲等；公式只能组合兼容量纲。质量和密度采用明确定义的规范单位（例如 kg、kg/mm³），UI 单独转换。

## 4. 文档聚合与索引

```text
CadDocument
├ Metadata / Units / ModelingSettings
├ RootAssemblyId ───────► AssemblyDefinition（合成文档根）
├ Definitions
│  ├ PartDefinition ───► Bodies / Features / Sketches / Datums
│  └ AssemblyDefinition ─► ComponentSlots ─► DefinitionId
├ Layers / Appearances / Materials
├ TopologyReferences / Annotations / NamedViews
├ AssetCatalog（引用；字节在文档资产仓）
└ Extensions（带命名空间与版本的受限数据）
```

最小必需：Metadata、Settings、RootAssembly、Definitions、Bodies、AssetCatalog。Features、Sketches、Annotations 初期可以为空，读取时依然有明确节版本。

`CadDocument` 负责事务后的整体一致性；对外只暴露不可变 `DocumentSnapshot` 和查询接口。内部可采用 ImmutableDictionary/ImmutableArray 结构共享。公开 `IReadOnlyDictionary` 并不自动保证底层不可修改，不允许把原始 Dictionary 返回给调用者再强制转换。

派生索引不重复持久化为第二份业务事实：

- DefinitionUsers：定义被哪些装配 Slot 引用。
- BodyOwner：体所属零件；FeatureDependants：反向特征依赖。
- LayerMembers：图层关联对象。
- Occurrence 展开缓存：根到实例的路径、世界变换、可见性；按定义/放置版本失效。
- GeometryConsumers：哪些体、历史状态、预览和视口引用了资产。

文档首次创建包含一个合成根 AssemblyDefinition。单零件文档也通过根 Slot 引用零件，因此零件和装配共用文件模型；合成根可在 UI 中隐藏。合成根禁止被任何 Slot 引用。

## 5. 零件定义、装配定义与实例路径

```csharp
public abstract record CadDefinition(DefinitionId Id, string Name);

public sealed record PartDefinition(
    DefinitionId Id, string Name,
    ImmutableArray<BodyId> Bodies,
    ImmutableArray<FeatureId> FeatureOrder,
    ImmutableArray<SketchId> Sketches,
    ImmutableArray<DatumId> Datums) : CadDefinition(Id, Name);

public sealed record AssemblyDefinition(
    DefinitionId Id, string Name,
    ImmutableArray<ComponentSlot> Children) : CadDefinition(Id, Name);

public sealed record ComponentSlot(
    ComponentSlotId Id, DefinitionId DefinitionId, string Name,
    RigidTransform3d LocalTransform, bool IsVisible,
    AppearanceBinding Appearance);

// 实例的语义键：DocumentId + 从合成根开始的 SlotId 序列。
public sealed record OccurrencePath(
    DocumentId DocumentId, ImmutableArray<ComponentSlotId> Slots);
```

实现 `OccurrencePath` 时必须定义序列内容相等与稳定 hash；不能依赖 ImmutableArray 默认底层数组身份相等。持久化可写 Guid 数组。结构上允许定义共享，但禁止装配定义引用环；显示深度上限是额外防护，不能替代环检测。

例：装配 A、B 都使用同一个子装配 S；S 内 Slot `bolt` 引用螺栓定义 P。

```text
DocumentRoot
├ slot-A -> S
│  └ slot-bolt -> P
└ slot-B -> S
   └ slot-bolt -> P
```

P 的几何只有一份；两颗螺栓路径分别为 `[slot-A, slot-bolt]`、`[slot-B, slot-bolt]`。因此 `ComponentSlotId` 单独不足以唯一标识屏幕上的实例。

编辑语义：

| 操作 | 变化 | 影响范围 |
|---|---|---|
| 改零件尺寸 / 体几何 | PartDefinition 下的结果/特征 | 引用它的全部实例 |
| 移动根下 slot-A | 根定义中的 Slot 位姿 | slot-A 整个子树 |
| 修改 S 的 slot-bolt | 可复用子装配的 Slot | 所有 S 实例中的该螺栓 |
| 只想改变 slot-A 内部的螺栓 | 先“使子装配独立”，克隆并重新映射定义图，再修改 | 新独立子装配 |
| 删除一个实例 | 移除对应定义中的 Slot；不删除零件资产 | 对应路径；若父定义共享则影响所有使用者 |

首版只允许通过“编辑定义”或“使独立”改变共享子装配内部位姿，不引入隐式的路径位姿覆盖。实例局部临时隐藏属于 Session 隔离显示；将来确需持久路径覆盖时，新增有版本的 `OccurrenceOverride` 节，处理重挂父级后的路径迁移，不能悄悄塞进 Slot。

删除定义默认 RejectIfUsed；明确解除所有引用后才删除。重挂父级若要求保持世界坐标，应以 `TnewLocal = inverse(TnewParentWorld) * ToldWorld` 计算，并在确定的实例编辑上下文中执行。

## 6. 逻辑体、几何资产与子拓扑

`CadBody` 是用户可命名、选取、隐藏、指定材料的逻辑建模对象。允许多体零件；Solid、Sheet、Wire 与 Mesh 在数据层区分，不能把所有 Shape 都称为“实体”。

```csharp
public enum BodyKind { Solid, Sheet, Wire, Compound, Mesh }
public enum GeometryRepresentation { OcctBrep, TriangleMesh }

public sealed record GeometryAssetRef(
    AssetId AssetId, GeometryRevisionId Revision,
    GeometryRepresentation Representation,
    string FormatVersion, string KernelVersion);

public sealed record CadBody(
    BodyId Id, DefinitionId PartId, string Name, BodyKind Kind,
    GeometryAssetRef Geometry, FeatureId? ProducerFeatureId,
    LayerId LayerId, AppearanceBinding Appearance,
    MaterialId? MaterialId, bool IsVisible);
```

校验 Shape 的实际拓扑类型和封闭性，再声明 Solid；导入失败/缺失资产的体使用显式 `Unavailable` 运行状态，不给它一个空 Shape 假装成功。多 Solid 的 Compound 是否拆体由导入选项决定，保留来源对应关系。

一个 Face/Edge/Vertex 通常只以体内引用存在，不各自成为独立 DocumentEntity。需要提取曲面、曲线时，命令生成新的 Sheet/Wire 体或基准对象。这样模型树不会因一个 STEP 含几十万个面就构造几十万个业务对象。

`AssetCatalogEntry` 包含 AssetId、媒体类型、存储编码版本、未压缩字节数、SHA-256、创建内核版本和引用类别。运行时仓维护租约：当前状态、撤销/重做、准备中的任务、预览/渲染。只有全部租约释放后才清理；资产 hash 是字节去重，不声称 OCCT 序列化跨版本稳定。

网格法线、顶点、索引不能代替 BRep 的曲面/边界语义。STL 等网格输入默认 Mesh，精确布尔能力受限；“网格转实体”作为另一个明确操作。

## 7. 特征与重算图

初期直接编辑也要保留操作来源：导入形成 ImportFeature；布尔/变换可先采用“输入结果资产 + 参数 + 已提交输出资产”的记录。逐步增加可编辑参数化特征时，已有导入体仍可作为固定 SourceFeature。

```text
FeatureDefinition
  Id / PartId / Name / TypeKey / SchemaVersion
  Parameters（类型化参数 DTO）
  Inputs[]（上游特征输出、体版本、草图或基准的类型化引用）
  OutputSlots[]（稳定输出槽标识）
  IsSuppressed

FeatureEvaluation
  FeatureId / DefinitionRevision / InputRevisionSet
  Status / OutputRevisions[] / Diagnostics[]
  LastSuccessfulOutput[]（失败时仅供标记为过期的预览）
```

`FeatureDefinition` 为持久设计意图；`FeatureEvaluation` 为评估状态与缓存。执行状态建议 `NotEvaluated / Dirty / Computing / Valid / Failed / BlockedByDependency / Suppressed`；`Computing` 不作为可继续工作的已保存状态，恢复时改为 Dirty。

首批类型与参数：

| TypeKey | 参数 | 主要输入 / 输出 |
|---|---|---|
| `cadoryx.source.import` | 源文件摘要、单位策略、导入选项 | 已嵌入资产 -> 一个或多个体 |
| `cadoryx.primitive.box` | 长宽高、局部基准 | -> Solid |
| `cadoryx.primitive.cylinder` | 半径、高度、轴 | -> Solid |
| `cadoryx.transform` | 平移/旋转/缩放的显式模式 | 体版本 -> 新体版本 |
| `cadoryx.boolean` | Fuse/Cut/Common、公差、失败策略 | 多个体版本 -> 0..N 个输出 |
| `cadoryx.extrude` | 距离、方向、对称、操作模式 | 已验证草图轮廓 -> Solid/Sheet |
| `cadoryx.revolve` | 角度、旋转轴、操作模式 | 轮廓/基准 -> 体 |
| `cadoryx.fillet` / `chamfer` | 半径/距离 | 体与持久边引用 -> 体 |

`TypeKey` 是带命名空间的字符串，配合 SchemaVersion 与白名单编解码器；不把 CLR 类型全名写入文件或反射实例化任意类型。具体参数用独立 record，如 BoxParameters、BooleanParameters；不采用 `Dictionary<string, object>` 作为内置特征主数据。

依赖图必须无环；FeatureOrder 是用户树排序/线性建模顺序，依赖图才决定重算顺序。外部引用首版先禁止，后续按固定文档版本建模并引入跨文档环检测。

体身份与特征输出不能混淆：用 `(FeatureId, OutputSlotId)` 映射到逻辑 BodyId。同一输出角色重算时保持 BodyId，GeometryRevision 更新；若一体分裂成多体，建立新输出槽并显式记录旧槽删除/映射。不依赖返回数组顺序猜测体身份。

参数编辑先验证、准备成功结果，再将新意图与新结果一起提交。失败保留原来的已提交文档，任务面板保留失败参数供修改。将来允许保存“失败的设计修改”时，必须单独设计该模式，不能默认把旧几何标为新参数的正确结果。

抑制特征的具体输出策略由特征类型定义（例如线性链中的输入直通）；没有定义直通的消费者应 Blocked。取消抑制后重算受影响闭包。功能树的回滚位置属于明确的编辑上下文，不通过删除历史特征实现。

## 8. 草图与约束

M4-S1 实现 `DocumentSnapshot.Sketches`、稳定 SketchEntityId/SketchConstraintId、点/线/圆、固定平面、13 种类型化约束、独立求解结果和六节存储。M4-S2 增加 CadSketch.Revision、FeatureDefinition.SketchSource 和固定平面编辑/关联重算。采用共享点引用，构造几何也参加求解；圆弧和更完整约束仍是后续目标。准确字段、数值范围与验收见 [草图基础](SKETCH_FOUNDATION.md)和[草图编辑](SKETCH_EDITOR.md)。

当前 `CadSketch`：Id、PartId、Name、Plane、Points、Lines、Circles、Constraints。Plane 为固定 RigidTransform3d，将局部 XY 映射到零件坐标。长期设计可增加 Support、DimensionParameters；Support 再扩展为 DatumPlaneId 或带几何版本约束的平面 Face 引用。引用失效必须报错，不自动跳到世界 XY。

几何全部在草图局部二维坐标系中：

| 类型 | 字段 |
|---|---|
| Point | Position(u,v)、是否构造元素 |
| LineSegment | Start/End 的点引用 |
| Circle | Center、Radius |
| Arc（后续） | Center、Radius、StartAngle、SweepAngle |
| Ellipse / BSpline（后续） | 明确的轴/参数区间，或次数/控制点/节点/权重 |

端点是可引用的独立 SketchPoint，线的 Start/End 和圆的 Center 持有稳定 SketchEntityId；共享端点只保留一份坐标。不能把“第 2 条线的第 1 个端点”作为长期身份。

当前每种约束是独立类型，携带 SketchConstraintId、类型化目标/尺寸及 IsEnabled，支持 FixPoint、Coincident、Horizontal、Vertical、OffsetX、OffsetY、Distance、Length、Parallel、Perpendicular、EqualLength、Radius、EqualRadius。IsReference、Tangent、Angle 和表达式尺寸仍待实现，不能用通用字符串目标绕过类型验证。

求解结果独立为 `SketchSolveReport`：UnderConstrained / FullyConstrained / Inconsistent / DidNotConverge / InvalidInput / LimitExceeded，包含局部自由度/秩、冗余/冲突约束 ID、解坐标和残差。仅前两种状态可提交；失败时解和自由度/秩为空。冗余约束单独报告，不等同于无解。读取文件不重新求解，UI 不能把尚未求解的坐标直接显示为“完全约束”。

OcctSharp 提供几何曲线和建模能力，约束求解由独立 `ISketchConstraintSolver` 承担。M4-S1 已采用 MathNet.Numerics 5.0.0（MIT）的托管 SVD，Cadoryx 实现解析约束方程和阻尼迭代。DOF/冗余是局部线性化诊断；非线性未收敛不能声称无解。它不代表完整约束草图已经交付。

草图生成轮廓要验证闭合、重复、零长和自交；M4-S1 提取单个直线闭环的冻结 SketchProfile，M4-S2 的 SketchProfileReference 以 SketchId/Revision/有序线 ID 持续关联同一闭环。孔洞方向/嵌套与曲线区域留待后续，不能把任意线条集合直接视为可拉伸面。

## 9. 持久拓扑引用

```text
TopologyReference
  Id / BodyId / ProducerFeatureId?
  CreatedGeometryRevision / Kind（Face/Edge/Vertex）
  Selector（语义名、算法历史 token、精确结果内定位等带版本方案）
  ContextAssetId?（若解析依赖 OCAF 命名上下文，保存该上下文资产）
  Evidence（可选：面积、质心、相邻信息等诊断指纹）

TopologyResolution
  Status = Resolved | Deleted | Ambiguous | Stale | Unsupported
  ResolvedGeometryRevision / ResolvedSubshapeKey? / Diagnostics
```

- 首选特征语义输出（如 box 的 +Z 面）或算法提供的来源/演化历史。
- 当前几何版本内的子形状序号可以作为临时定位信息，但必须绑定精确 GeometryRevision，禁止跨重算直接复用“Face 7”。若把序号用于保存恢复，还必须绑定资产、遍历协议和内核版本，并验证 BRep 往返后的对应关系；否则仅供本次会话定位，不能作为持久命名方案。
- 重算时使用明确历史映射；多候选或无法追踪时返回 Ambiguous/Unsupported，并要求重新选取。几何指纹只辅助建议，不能静默选一个最近面。
- OCCT/ParametricDocument 命名能力由适配器验证后使用；若仅保存 BRep 而丢掉命名上下文，不能声称原有 OCAF selector 可以重建。需要把对应上下文作为版本化资产保留，或诚实降级为当前版本引用。
- Feature、尺寸标注、面样式和装配约束都使用此机制；持久拓扑命名是专门里程碑，不是给 Face 加 Guid 就完成。

## 10. 选择、外观、图层、材料与标注

### 选择

`SelectionTarget` 是受约束的联合类型：Definition、Occurrence、BodyOccurrence、SubshapeOccurrence、Feature、SketchElement、Datum。几何选中包含 `DocumentId + OccurrencePath + BodyId + GeometryRevision + SubshapeToken?`；树中仅选定义可以不包含路径。

SelectionSet、Hover、检测容差和过滤器属于 Editor Session。Viewer 检测结果转换为上述值后马上释放临时 `ViewerSelectionItem.Shape`。文档改动后过滤无效目标；视口与模型树通过同一 Selection 服务同步，避免循环回调。

### 外观与材料

`Appearance` 保存显示颜色、透明度、线宽等；`PhysicalMaterial` 保存材料名称、密度等物理属性。外观颜色不能被当成材料类型。

`AppearanceBinding` 是 `Explicit(AppearanceId) / ByLayer / Inherit` 联合值。字段级解析原则：由低到高合成“应用默认 → 图层/对象默认 → 定义/体 → 定义内面边样式 → 外层到内层实例的显式覆盖 → 实例子拓扑覆盖”；只有明确设置的通道才覆盖。`ByLayer` 明确取当前对象所属图层的对应通道，`Inherit` 沿上述上下文继续解析。UI 应显示最终值及来源。

普通实例整体着色若需保留 STEP 面颜色，应使用一个显式策略选项（保留子形状样式/覆盖子形状样式），而非在两个渲染路径里各自猜测。导入先保留源样式语义，再由统一 resolver 生成渲染样式。

`Layer`：Id、Name、默认 Appearance、IsVisible、IsLocked；对象关联图层用于组织/显示。层隐藏、祖先实例隐藏与对象隐藏任一生效即不可见；锁定只限制业务编辑，不伪装成几何只读。

### 标注、视图和自定义属性

- Annotation 保存尺寸类型、拓扑引用、显示格式、位置和单位策略；测量值可派生，驱动尺寸实际写入特征/草图参数，两者明确区分。
- NamedView 保存 CameraPose、投影类型、视野参数、裁剪设置和可选显示过滤。临时 Camera 不自动覆盖 NamedView。
- CameraPose 使用 Eye、Target、Up；Eye/Target 不重合，Up 与观察方向不平行。透视视图保存 VerticalFovRad，正交视图保存 OrthoHeightMm，两者用联合类型防止混填；近远裁剪须为有效有序区间，也可显式选择自动范围。
- 自定义属性使用命名空间键、类型标签（string/bool/int64/double/quantity/date/reference）和版本，不保存任意 CLR 对象。
- 扩展数据限制大小、深度和类型。未知可选字段可保留；未知必需功能进入只读或拒绝编辑，避免保存时丢数据。

### 基准与装配约束预留

`CadDatum` 包含 Id、PartId、Name、Kind、Definition、Dependencies。Kind 为 Point/Axis/Plane/CoordinateSystem；Definition 是固定数值定义，或带明确偏移/方向参数的关联定义。关联基准参与特征依赖环检测；支持面失效时基准也失效，不能变成原点处的有效坐标系。

装配约束采用独立结构，不混入草图 Constraint：

```text
AssemblyConstraint
  ConstraintId / OwnerAssemblyId / Name / TypeKey / SchemaVersion
  Participants[]（相对 OwnerAssembly 的 SlotId 路径 + Datum/Topology 引用）
  Parameters（距离、角度、方向解、偏置等类型化值）
  IsSuppressed

AssemblySolveReport
  AssemblyId / InputRevisions
  Status（NotSolved/UnderConstrained/Solved/OverConstrained/Failed）
  RemainingDegreesOfFreedom / ConflictingConstraintIds / Diagnostics
  CandidatePlacements[]（解的候选位姿，成功后整体提交）
```

拥有者为装配定义，因此约束参与路径相对该定义解析；同一子装配的多个实例复用约束图，各自在世界中定位。首版约束只引用拥有者子图内的实例，禁止通过全局路径依赖外层装配，避免循环和实例相关求解。柔性子装配、跨装配上下文约束和运动学不在首版承诺内。

外部零件引用后续可扩展为 `ExternalDefinitionSource(DocumentId, PinnedStateId, ContentHash, RelativeUri, ResolvePolicy)`。已解析内容作为固定版本的本地快照参与本轮命令；文件更新由显式更新命令获取新版本、校验引用并整体提交。源 URI 是定位提示，不能代替文档和内容身份。

## 11. 提交前必须成立的不变量

1. 所有业务 ID 非空且在其索引范围内唯一；所有引用指向存在且类型匹配的对象。
2. 一个 Body 只属于一个 Part；一个 Slot 只属于一个 AssemblyDefinition；定义引用无环，合成根不可被引用。
3. 所有体的权威资产存在且 hash、媒体类型、版本匹配；Mesh 与 BRep 能力明确区分。
4. 所有变换/公差/参数有限有效；单位转换只发生在明确边界。
5. 参数化依赖无环；有效输出的输入版本与特征参数版本一致。
6. 几何变更后，未解析拓扑引用进入显式失效状态；不能带着旧序号假装有效。
7. 一个事务同时发布业务状态、资产引用与差量，失败时全部不发布。
8. ModelTree 和 Properties 仅投影已提交状态或有明确标识的预览；不能成为文档的第二份可修改事实。

验证分为 DTO 语法、领域引用一致性、内核几何合法性三层。纯元数据读取不加载 OCCT；真实精确几何验证在接入对应能力后运行。

## M4-T1 实际拓扑数据

DocumentSnapshot.TopologyReferences 是以 TopologyReferenceId 为键的不可变表。引用具有 DocumentId、FeatureId、OutputBodyId、OriginRevision、Kind、BoxBoundary/SecondBoundary、Policy 和 SchemaVersion。此实现限定长方体的面与边；上文更广泛的语义路径和几何签名仍是目标模型。解析结果不持久化，删除的生产者可保留为诊断对象；精确撤销恢复原引用及几何。参见 [实际协议和支持边界](TOPOLOGY_REFERENCES.md)。

M4-T2 新增 LocalFeatureRecipe：Source、上游 BoxRecipe 缓存、两个语义边界、Operation 和 Size；输入必须为同零件的唯一 Box 特征，缓存和几何必须与上游一致。局部特征输出使用新的 FeatureId/BodyId，原生产者和来源元数据保留。无序号或 native 对象进入持久状态。具体范围见 [局部建模](LOCAL_FEATURES.md)。
