# H2-B2：真实布尔拓扑历史

本阶段将 OcctSharp 的真实逐源布尔历史接入 Cadoryx。依赖锁定为本地开发包 `8.0.1-preview.28.cadoryx.h2b2.2`，基于 Preview.28 源码扩展；没有公开发布 NuGet。应用文件写出版本为 0.4.5，八节 MessagePack 版本不变，沿用 history v2。

## 上游接口与所有权

`BooleanHistoryModeling.Build(TopologyBooleanOperation, IReadOnlyList<RepairSnapshot>, argumentCount)` 位于 OcctSharp.Modeling。前 argumentCount 个输入为 arguments，其余为 tools；两组必须非空，总计 2..256 个输入。支持 Fuse、Cut、Common，串行、非破坏运行。既有 `FeatureModeling.Boolean` 和计数历史 API 不变。

新 C ABI 导出 `occtsharp_boolean_topology_history` 复用 LocalFeatures 的 InputGraph、Result、History 和已有 Shape／FeatureResult 注册及释放。一次完整图复制通过 OCCT 的 ModifiedShape 对应保持各输入原图的槽位；算法仍在作用域内时捕获修改、生成、删除和未映射，未改变关系使用最终图中的精确 TShape／location 身份。每条记录保留输入参数、源全图索引、类型、演化关系和最终结果全图索引，结果形状采用最终遍历方向。

发布历史前还使用实际 Repair::Copy 的逐项复制对应，核对最终图到诊断副本的完整槽位及方向。无法证明时返回合法几何及“历史不可用”，不发布未经核对的索引。此检查不以两个独立几何对象的近似程度判定身份。

托管调用期间持有所有输入的 SafeHandle 引用。输入、结果和历史 Shape 在调用结束后可独立释放；没有原生 builder 或临时指针进入 Db。固定 C 结构布局不变，LocalFeatureOperation.Boolean 追加为 11，生成文件未修改。上游记录位于其 docs/BOOLEAN_TOPOLOGY_HISTORY.md、SPECIAL_CASES.md 和 OWNERSHIP.md；补丁和复现入口在[接入目录](../integrations/occtsharp-boolean-history/README.md)。

## Cadoryx 接入与拒绝边界

`OcctBooleanHistory` 用配方输入创建精确快照，调用上述接口，保存几何后重新读取 BRep。沿用限定单位轴舍入规则，核对原诊断图和存储重载图的种类、方向、父级以及每个规范化子形状。校验通过后才保存 SchemaVersion 2 历史；失败时保留几何并返回 HISTORY.UNSUPPORTED。

输入与原生关系有界；空输入或超过 256 个输入仍走既有布尔几何入口，不提供这批历史。空交集可以保留逐源 Deleted 关系。几何仍可由多种配方生成，本阶段可追踪的用户引用限定为 Box 语义面／边到直接局部或布尔后继，未开放跨多步特征链、自动重绑定或用生成面继续建模。

`TraceAsync` 对每个输入核对上游 Feature.Result、修订、资产、指纹、数量、关系覆盖和类型，即使用户查询的是输入 0，也必须验证其他输入。局部与布尔适配器标签分开；旧 Preview.26 单输入轴适配器和原严格适配器仍可读取并重新核对，合成测试标签继续拒绝。

| 情况 | 返回结果 |
|---|---|
| 唯一保留／修改目标，且没有其他来源共享目标 | Resolved |
| 同一源面拆分为多个目标 | Ambiguous，无目标 |
| 多个来源指向同一目标，包括不同输入的相同源槽位 | Ambiguous，无目标 |
| 原生明确删除源对象 | Deleted |
| 其他输入的修订／指纹失配 | Stale |
| 缺少源覆盖、适配器不匹配或复制校验不通过 | Unsupported |

BooleanCommand 和重算将完整历史与几何一起提交；重算替换全部输入身份，没有证据时清空旧记录，精确撤销／重做恢复原快照。文件仍使用 H2-B1 的稳定数字 Key、输入顺序校验与显式旧版本迁移。

## 验证范围

上游测试使用真实几何：10×20×30 箱体和穿过中部的 2×22×32 刀体，Cut／Fuse／Common 体积分别为 4800、6208、1200 mm³；核对两侧全部面／边／顶点覆盖和最终原图槽位，检测实际拆分、删除、未改变及独立重合输入的共享目标。深复制可能调整 p-curve 表示，因此原生接口测试直接核对最终原图，副本与文件重载由独立的复制对应和 Cadoryx 校验负责。

Cadoryx 新增 8 项用例，并将原“布尔不支持”回归改为真实拆分必须返回歧义；覆盖三输入、空交集、两侧追踪、另一输入损坏、遗漏覆盖、重算、精确撤销／重做和存储。H2-B1 的合成关系测试保留其协议用途。

HistorySmokeRunner 新增布尔重算、八节保存重开、三种状态和 STEP／IGES／STL 导出；RecoverySmokeRunner 在生产 30 秒快照后强制终止并重启，验证两侧输入、全部关系字段与恢复后解析。最终执行证据集中记录在 [ROADMAP](ROADMAP.md)。这些验证不代表混合 DPI/RDP、长时间资源运行、完整 OcctSharp 产品发布或通用拓扑命名已经完成。
