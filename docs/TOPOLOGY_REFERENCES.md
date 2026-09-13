# M4-T1 拓扑引用基础

本文保留 T1 的接口和当时验证边界。M4-T2 已证明当前 Box 原图中的唯一边可用于局部建模，并扩展 features v5；交互及原生所有权见 [LOCAL_FEATURES](LOCAL_FEATURES.md)。M4-T1-H1 单列通过 BRep 校验的局部历史诊断和八节协议，见 [算法历史](TOPOLOGY_HISTORY.md)。原 Box 用户引用规则不变，布尔与通用传播仍未实现。

2026-09-12 实现。当前支持范围是 BoxRecipe 的六个平面和十二条直边，包括尺寸和刚体放置变化后的语义重绑定。这是有限的拓扑引用基础，不是通用拓扑命名算法。面/边鼠标选择、圆角/倒角窗口属于 M4-T2。

## 身份、上下文与修订

`DocumentSnapshot.TopologyReferences` 保存不可变 `TopologyReference`。每条引用具有独立强类型 ID、DocumentId、FeatureId、OutputBodyId、OriginRevision、Kind、边界语义、重绑定策略及 SchemaVersion=1。

- 面：XMin/XMax/YMin/YMax/ZMin/ZMax，均相对 BoxRecipe.Placement 定义的局部坐标。
- 边：两个不同轴的边界交线，按 X、Y、Z 轴顺序保存，例如 XMax + ZMax。拒绝同轴、逆序和未知枚举。
- ExactRevision：当前输出修订与 OriginRevision 不同立即 Stale，即使两次生成几何看起来相同。
- Semantic：在同一特征输出上重新解析边界。OriginRevision 是来源记录，不会被重算悄悄覆盖，也不额外保留旧 BRep。
- 引用位于零件定义空间。装配实例路径由使用引用的操作另外提供；不能把同一个定义的引用当作唯一实例选择。

稳定身份是特征及语义。`ResolvedSubshape.Index` 仅为诊断定位：它指向当前 BRep 的 Edge→Face 唯一邻接表（边用 Items，面用 Ancestors），同时绑定 AssetId、Revision 和 KernelVersion。不得持久化或跨修订复用。后续 native 建模必须在自己持有的当前 shape 图上重新解析；本阶段的独立子形状副本不能被当作原图中的 native 身份。

## 解析与拒绝规则

`ITopologyResolver` 是独立的可选内核能力，不改变其他 IGeometryKernel 实现。OcctGeometryKernel 通过已有串行队列读取精确 BRep，使用 GetTopologyAdjacency(Edge, Face) 获得唯一项；普通 GetSubShapes 会重复列出共享边，不能直接用于唯一性判断。

在反向 Placement 后，检查面为 Plane、边为 Line，并核对完整局部边界范围。固定数值容差为 1e-6 mm，最小盒尺寸必须大于 1e-4 mm；用户显示/文档容差不能扩大匹配范围。大坐标数值误差可能造成保守拒绝，不做最近几何猜测。

| 状态 | 意义 | 自动目标 |
|---|---|---|
| Resolved | 恰好一个符合语义的唯一拓扑项 | 当前版本诊断定位 |
| Missing | 生产特征不存在、空输出或零候选 | 无 |
| Stale | ExactRevision 的修订已变化 | 无 |
| Ambiguous | 两个以上独立候选，包括重合的独立面/边 | 无 |
| Unsupported | 非 BoxRecipe、需要算法历史或尺寸低于支持范围 | 无 |
| WrongContext | 文档或输出身份不同 | 无 |

损坏的引用契约抛出 CadValidationException；取消保持 OperationCanceledException；资产损坏或 native 异常不会伪装成“解析成功”。解析不修改文档。特征删除后允许保留引用，以便诊断与撤销；它不会自动转向同名或同形特征。

`TopologyReferenceInspection.InspectAsync` 持有整个快照的资产租约，生成带 DocumentId/StateId 的诊断报告。消费者只接受 `IsCurrent(snapshot)` 的结果；状态变化、重算、撤销、重做之后重新检查。报告是派生状态，不写进文件。

## 算法历史支持边界

preview.26 的 FeatureModeling.Boolean 可以返回 `FeatureHistoryItem(SourceIndex, Kind, Shape)` 和 DeletedSourceIndices。实际包消费测试执行真实切除并检查历史可读。SourceIndex 是输入形状组的索引；接口没有把每个原始源面/边的稳定身份对应到输出项的契约。当前桥接还会写出并重新加载独立 BRep，不能把历史副本的 native 地址当作持久关系。

因此：当前不消费布尔、分割、合并、圆角、倒角的历史作自动重绑定，不推断“一条记录就是唯一继承”。指向原 Box 特征的引用始终指向原输出，即使该输出已被布尔消费；它不表示布尔结果上仍存在的面。指向 BooleanRecipe 输出的 Box 语义请求返回 Unsupported。

后续 M4-T1-H 必须验证逐源子形状标识、输出原图归属、一对多/多对一/删除/未改变的历史、保存重载以及连续重算。满足前不能开放跨这些操作的自动引用传播。M4-T2 可以先做 Box 支持范围内的选择/重选交互，局部建模确认还需验证复制选择与 native 原图的映射，不能直接消费诊断 Index。

## 命令与历史

`UpsertTopologyReferenceCommand` 完成新增或显式重选：要求当前创建修订、可用内核能力、输出图层未锁定，且真实解析唯一后才能提交。失败不产生新历史；会话原有代际/取消/只读检查继续生效。`RemoveTopologyReferenceCommand` 显式移除引用。DocumentChangeSet 增加 ChangedTopologyReferences。

重算不改写引用原始身份和来源修订，依赖结果由解析器重取。撤销/重做恢复同一个不可变快照及精确几何资产。删除生产者留下失效引用；当前没有局部特征消费者，后续引入消费者时必须另外验证删除引用是否会使其悬空。

## MessagePack 与兼容

当前七节为 document v4、structure v3、features v4、presentation v2、geometry v1、sketches v2、topology v1。containerVersion/assetCatalogVersion 仍为 1，applicationVersion 为 0.4.1，新增必需能力 `cadoryx.topology-references.1`。

topology 的 DTO 使用数字 Key 0–9，保存来源修订而不保存解析状态、序号或 native 对象。必须节缺失、未来版本、重复身份、非法边界/策略、文档或输出错配、尾随数据均拒绝。缺失的生产者作为可诊断业务状态保留；它不是错误的文档归属。

document v3→v4 显式生成空 topology v1；已有同名节则拒绝迁移覆盖。原 document v2→v3 的历史输出已固定，不能随 CurrentFormats 升级跳过中间契约。旧文件不会被自动发明面/边引用。设置读取仍只消费 document，不初始化 OCCT。

冻结的 `m4s2-linked.cadoryx` 来自修改写出器前的真实草图编辑产物，包含关联拉伸及后继布尔。SHA256 为 `63f441927e25171e44c7f882e0d139943495337e34cd35644224d09470c7cbd7`；其他五份历史样本保持原字节。

## 验证入口

`TopologyReferenceTests` 和 `TopologyStorageTests` 共新增 22 项，全套现为 200 项。覆盖所有 Box 面/边、旋转平移、重算及精确撤销重做、重复/缺失 native 候选、修订/上下文/历史拒绝、取消、锁定、数值范围、七节往返、旧文件迁移及损坏协议。几何解析测试实际消费本地 preview.26，不依赖参考仓库的生成代码。

`scripts/verify.ps1 -PublishSmoke` 在独立发布目录额外执行 TopologySmokeRunner，生成 topology-result.json、topology.cadoryx 和 STEP/IGES/STL。报告包括 Resolved/Stale 两种结果，保存重开后重新解析一致，退出资产归零。这是应用组合根和真实 native/IO 验证，不是面/边拾取 UI 验收。

最新执行结果与剩余环境门禁见 [ROADMAP](ROADMAP.md)。
