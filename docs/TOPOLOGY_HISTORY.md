# M4-T1-H1：局部算法历史证据

H2-B1 更新：文件 history 节现为 v2，增加输入参数身份；旧地图的单输入版本和适配器来源保留，见 [多输入历史](MULTI_INPUT_HISTORY.md)。本文保留 H1 的阶段结构与验证背景。

H2-A 更新：现已增加限定单位轴舍入校验，恢复部分旋转结果的历史。新证据使用 axis-v2 适配器标签，旧 v1 保持可读；以下 H1 精确指纹失败记录保留其阶段背景，当前实现和限制见 [旋转历史与布尔门禁](HISTORY_ROTATION.md)。

2026-09-13。当前固定消费 OcctSharp 8.0.1-preview.26，不修改参考仓库或生成代码。这一批提供单个 Box → 单边圆角／倒角的历史捕获、持久化和诊断解析。它不是完整的跨算法持久命名；布尔、多步链及生成拓扑继续编辑仍未开放。

## 原生能力与验证边界

实际 NuGet 的 `ContourFilletRecipe`、`ContourChamferRecipe` 返回 `LocalFeatureResult.History`，包含源参数、源拓扑索引／类型、关系和当前结果全图索引。源图来自 `RepairSnapshot`，选择仍通过 Box 六面／十二边语义唯一匹配。倒角使用对应 First 边界面作为对称倒角的支持面。

采用这些接口替换原来的 `FeatureModeling.Fillet/Chamfer`。创建和历史捕获发生在同一次算法调用中，不通过重跑算法猜测旧输出的关系。原生源图复制保留全图对应，结果索引来自最终形体的 full topology map。聚合组（SurfacePatch、ContourEdge 等）不充当逐源演化关系。

保存 BRep 可以规范化表示；倒角实测写入前后整体指纹会变化。适配器因此执行：

1. 存储合法结果，再读回独立 BRep；两端构造 RepairSnapshot。
2. 比较全图的类型、方向、父级索引序列。
3. 对每个全图槽位的子形状分别做 BRep 写入／读回规范化，要求精确指纹一致。这里不进行几何距离搜索或候选重排。
4. 校验原生来源、索引边界和演化能力；记录读回后的源／结果指纹及资产身份。

部分任意旋转模型不能通过第 3 步的严格校验。此时合法建模仍成功，`GeometryResult.Diagnostics` 返回 `HISTORY.UNSUPPORTED`，`TopologyHistory` 为空；解析器拒绝传播。空映射保存重开后仍为空，诊断结果统一为 Unsupported。不能把这项校验失败解释为几何建模失败，也不能声称所有旋转模型都具备可持久传播的历史。

全图复制、逐槽 BRep 规范化有额外开销；当前仅对单 Box 局部算法启用，没有大型模型性能承诺。规范化校验不是两份任意几何模型的等价判定算法。

## 数据结构与生命周期

`FeatureDefinition.TopologyHistory` 是可空不可变证据，包含：

| 字段 | 含义 |
|---|---|
| SourceRevision / SourceAsset | 此次算法输入的精确修订和 BRep 内容地址 |
| ResultRevision / ResultAsset | 此次算法输出的精确修订和 BRep 内容地址 |
| AdapterVersion / SchemaVersion | 索引空间及映射协议版本，H1 为 full-topology-brep-v1 / 1；后续版本见本文开头的阶段链接 |
| SourceFingerprint / ResultFingerprint | 读回图的 RepairSnapshot 指纹 |
| SourceCount / ResultCount | 两个全图索引范围 |
| Entries | 源索引／类型、演化、可空结果索引／类型 |

领域类型与 OCCT 枚举解耦：HistoryShapeKind 包含 Face、Edge、Vertex；TopologyEvolution 包含 Unchanged、Modified、Generated、Deleted、Unmapped。拆分用一源多结果，合并用多源同结果表示。原生算法生成但未到达最终形体的对象标记 Unmapped。没有证据的源面／边／顶点补为 Unmapped。

`DocumentSnapshot.Validate` 要求证据与局部配方源资产、当前结果完全一致；校验版本、枚举、指纹、索引边界、空目标关系、重复项和类型一致性。没有 native 指针、RepairIdentity 或临时 Shape 进入 Db。

`LocalFeatureCommand` 将结果和证据一起加入候选；`FeatureRecompute` 每次以新结果整体替换证据，包括清空新内核未提供的证据。失败／取消不提交候选，撤销和重做恢复原快照／证据／几何，不重算。拓扑证据不新增几何资产，其两端由现有特征和配方租约保留。

## 解析协议

`ITopologyHistoryResolver.TraceAsync(snapshot, sourceReference, targetFeature, assets)` 目前仅接受直接的 Box → LocalFeatureRecipe 依赖。它持有文档资产租约，进入现有 native 串行队列后检查文档、生产者、输出身份、精确修订策略、适配器版本、两端 BRep 指纹、全图数量和实际类型，再按源 Box 语义定位源槽位。

`TopologyHistoryReduction` 的规则：

| 状态 | 条件 |
|---|---|
| Resolved | 唯一、同类型的 Modified／Unchanged 目标，且没有其他源指向该目标 |
| Ambiguous | 拆分、合并或删除与保留等矛盾关系；不取第一个候选 |
| Deleted | 明确且独占的删除关系 |
| Generated | 只有生成关系，没有可延续的同角色目标，例如旧边生成圆角面 |
| Unsupported | 未映射、缺少证据、未知适配器、布尔或不支持的依赖链 |
| Stale / Missing / WrongContext | 修订或指纹过期、生产者缺失、跨文档或输出身份不符 |

生成面不会冒充原边。当前真实简单样本证明修改、未改变、生成和顶点删除；拆分／合并的拒绝规则由合成关系测试验证，尚无实际布尔拆分／合并生产者的集成证明。

`HistoryTarget.FullTopologyIndex` 仅用于指定资产和适配器的诊断。它与旧 `ResolvedSubshape.Index` 的邻接表索引不是同一空间；禁止交叉传入建模。当前没有把这类定位器接到连续建模，也未新增可保存的跨特征用户引用或自动重绑定命令。

`TopologyHistoryInspection` 带 DocumentId / StateId；异步 UI 消费者必须调用 IsCurrent，不能在文档改变后展示旧结果为当前状态。持有租约和取消检查防止队列等待／晚到结果使用已释放资产。

## MessagePack 与旧文件

H1 阶段应用写出版本 0.4.3；ZIP 容器 1、资产目录 1。当时八节：document 5、structure 3、features 5、presentation 2、geometry 1、sketches 2、topology 1、history 1。新增必需能力 `cadoryx.topology-history.1`，旧程序不能静默丢弃证据。当前 history v2 的演进见 [多输入历史](MULTI_INPUT_HISTORY.md)。

history v1 使用稳定数值 Key 的 `PackHistories / PackHistory / PackHistoryEntry` DTO，按 FeatureId 关联已有特征。feature 节仍为 v5，不改变旧配方结构。独立节没有裸索引的通用命名含义，只有带双端资产及版本的历史证据。

document v4 → v5 新建空 history 节。原 document v3 → v4 的拓扑迁移输出冻结为 v4，防止未来版本跳级。读取旧文档不运行算法、不改变原状态或资产；旧局部结果必须显式重算才可能获得证据。仅读取设置仍不需要 native 内核。

冻结样本 `Cadoryx.Tests/Fixtures/Storage/m4t2-local.cadoryx` 来自修改写出器之前的 `artifacts/smoke-20260912-230828/local-feature.cadoryx`，SHA-256：`c5b9347bae6e5b46e7d4109781503c221617758846c4ba211f33f3c0e53cbe02`。原有七份样本保持字节不变。

## 验证与下一步

LocalHistoryInteropTests 验证实际包及 BRep 全图对应；TopologyHistoryTests 验证两类算法、连续三次上游重算、精确历史、八节往返、失败原子性、旧 T2 迁移、旋转模型校验失败降级、歧义拒绝和损坏协议。原十二边 × 两算法的合法性／体积回归继续保留。

独立发布冒烟新增 `history-result.json`、两种局部算法的 `.cadoryx` 和 STEP／IGES／STL；恢复冒烟核对序列化证据及重载后的面解析。执行结果见 [ROADMAP](ROADMAP.md)，不能将源代码中的检查视为已执行。

M4-T1-H2 继续处理旋转表示规范化、真实拆分／合并、布尔逐源历史和多步传播。门禁通过后，再定义持久化跨特征引用、消费者原图重选及 M4-T3 连续编辑。现有窗口仍遵循 MetroWindow 风格，本批没有新增独立窗口。
